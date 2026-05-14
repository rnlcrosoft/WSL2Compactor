namespace WSL2Compactor;

using Spectre.Console;
using Spectre.Console.Rendering;
using WSL2Compactor.Models;
using WSL2Compactor.Services;

internal sealed class TerminalRunDisplay : IProgress<CompactProgressUpdate>
{
    private static readonly string[] SpinnerFrames = ["-", "\\", "|", "/"];
    private readonly object _gate = new();
    private readonly RunLogger _log;
    private readonly IReadOnlyList<DistributionRow> _rows;
    private CompactProgressUpdate? _current;
    private DateTimeOffset _phaseStartedAt;
    private string _phaseKey = "";
    private int _spinnerIndex;
    private DateTimeOffset _startedAt;

    public TerminalRunDisplay(IReadOnlyList<DistributionRow> rows, RunLogger log)
    {
        _rows = rows;
        _log = log;
        _startedAt = DateTimeOffset.Now;
        _phaseStartedAt = _startedAt;
    }

    public void Report(CompactProgressUpdate value)
    {
        var runEvent = RunEvent.FromUpdate(value);
        _log.Write(runEvent);

        if (IsLowPriorityCurrentEvent(value))
        {
            return;
        }

        lock (_gate)
        {
            var phaseKey = $"{value.Distro}|{value.Backend}|{value.Phase}";
            if (!string.Equals(_phaseKey, phaseKey, StringComparison.Ordinal))
            {
                _phaseKey = phaseKey;
                _phaseStartedAt = runEvent.Timestamp;
            }

            _current = value;
        }
    }

    public async Task RunAsync(
        Func<IProgress<CompactProgressUpdate>, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        _startedAt = DateTimeOffset.Now;
        _phaseStartedAt = _startedAt;

        await AnsiConsole.Live(BuildRenderable())
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Cropping(VerticalOverflowCropping.Top)
            .StartAsync(async context =>
            {
                var work = Task.Run(() => action(this, cancellationToken), CancellationToken.None);
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));

                while (!work.IsCompleted)
                {
                    context.UpdateTarget(BuildRenderable());
                    context.Refresh();

                    try
                    {
                        await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                await work.ConfigureAwait(false);
                context.UpdateTarget(BuildRenderable());
                context.Refresh();
            }).ConfigureAwait(false);
    }

    private IRenderable BuildRenderable()
        => new Rows(
        [
            BuildCurrentPanel(),
            BuildDistroTable(),
            BuildTranscriptPanel()
        ]);

    private Panel BuildCurrentPanel()
    {
        CompactProgressUpdate? current;
        DateTimeOffset phaseStartedAt;
        DateTimeOffset startedAt;
        int spinnerIndex;

        lock (_gate)
        {
            current = _current;
            phaseStartedAt = _phaseStartedAt;
            startedAt = _startedAt;
            spinnerIndex = _spinnerIndex++;
        }

        var now = DateTimeOffset.Now;
        var table = new Table().Expand();
        table.AddColumn("Field");
        table.AddColumn("Value");

        table.AddRow("Distro", Markup.Escape(current?.Distro ?? "-"));
        table.AddRow("Backend", Markup.Escape(current?.Backend ?? "-"));
        table.AddRow("Phase", Markup.Escape(current?.Phase ?? "starting"));
        table.AddRow("Message", Markup.Escape(Trim(current?.Message ?? "Waiting for work to start.", 120)));
        table.AddRow("Progress", Markup.Escape(BuildProgressText(current, spinnerIndex)));
        table.AddRow("API status", Markup.Escape(BuildApiStatusText(current)));
        table.AddRow("Elapsed", Markup.Escape(FormatDuration(now - startedAt)));
        table.AddRow("Phase elapsed", Markup.Escape(FormatDuration(now - phaseStartedAt)));
        table.AddRow("ETA", Markup.Escape(EstimateEta(current, now - phaseStartedAt)));
        table.AddRow("Started", Markup.Escape(startedAt.ToString("yyyy-MM-dd HH:mm:ss zzz")));

        return new Panel(table)
            .Header("Current operation")
            .Expand();
    }

    private Table BuildDistroTable()
    {
        var table = new Table()
            .Title("Selected distros")
            .AddColumn("Distro")
            .AddColumn("Status")
            .AddColumn("Before")
            .AddColumn("After")
            .AddColumn("Saved")
            .AddColumn("Backend");

        foreach (var row in _rows)
        {
            table.AddRow(
                Markup.Escape(row.Name),
                Markup.Escape(row.Status),
                Markup.Escape(row.BeforeText),
                Markup.Escape(row.AfterText),
                Markup.Escape(row.SavedText),
                Markup.Escape(string.IsNullOrWhiteSpace(row.Backend) ? "-" : row.Backend));
        }

        return table;
    }

    private Panel BuildTranscriptPanel()
    {
        var events = SelectTranscriptEvents(_log.SnapshotEvents(200), maxCount: 22)
            .Select(RenderEvent)
            .ToList<IRenderable>();
        if (events.Count == 0)
        {
            events.Add(new Markup("[grey]No events yet.[/]"));
        }

        return new Panel(new Rows(events))
            .Header("Transcript")
            .Expand();
    }

    private static Markup RenderEvent(RunEvent runEvent)
    {
        var level = runEvent.Level switch
        {
            CompactEventLevel.Warning => "[yellow]WARN[/]",
            CompactEventLevel.Error => "[red]ERROR[/]",
            CompactEventLevel.Debug => "[grey]DEBUG[/]",
            _ => "[green]INFO[/]"
        };
        var scope = string.Join(" / ", new[] { runEvent.Distro, runEvent.Backend, runEvent.Phase }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var text = $"{runEvent.Timestamp:HH:mm:ss} {level} ";
        if (!string.IsNullOrWhiteSpace(scope))
        {
            text += $"[grey]{Markup.Escape(scope)}[/] ";
        }

        if (runEvent.Kind == CompactEventKind.Command && !string.IsNullOrWhiteSpace(runEvent.Command))
        {
            text += $"[blue]$[/] {Markup.Escape(Trim(runEvent.Command, 160))}";
        }
        else
        {
            text += Markup.Escape(Trim(runEvent.Message, 180));
        }

        if (runEvent.Percent is { } percent)
        {
            text += runEvent.ProgressMode == CompactProgressMode.PendingNoReliablePercent
                ? " [grey](pending)[/]"
                : $" [grey]({percent:0.#}%)[/]";
        }

        return new Markup(text);
    }

    private static string BuildProgressText(CompactProgressUpdate? current, int spinnerIndex)
    {
        if (current is null)
        {
            return $"{SpinnerFrames[spinnerIndex % SpinnerFrames.Length]} running";
        }

        if (current.ProgressMode is CompactProgressMode.Indeterminate)
        {
            return $"{SpinnerFrames[spinnerIndex % SpinnerFrames.Length]} running";
        }

        if (current.ProgressMode is CompactProgressMode.PendingNoReliablePercent)
        {
            return $"{SpinnerFrames[spinnerIndex % SpinnerFrames.Length]} waiting for VirtDisk completion";
        }

        if (current.Percent is not { } percent)
        {
            return $"{SpinnerFrames[spinnerIndex % SpinnerFrames.Length]} running";
        }

        var displayPercent = Math.Clamp(percent, 0, 100);
        var width = 34;
        var filled = (int)Math.Round(width * displayPercent / 100, MidpointRounding.AwayFromZero);
        filled = Math.Clamp(filled, 0, width);
        return $"[{new string('#', filled)}{new string('-', width - filled)}] {displayPercent,5:0.0}%";
    }

    private static string BuildApiStatusText(CompactProgressUpdate? current)
    {
        if (current is null)
        {
            return "-";
        }

        var parts = new List<string>();
        if (current.OperationStatus is { } status)
        {
            parts.Add(status == 997 ? "ERROR_IO_PENDING" : $"0x{status:X8}");
        }

        if (current.CalculatedPercent is { } percent)
        {
            parts.Add($"apiPercent={percent:0.###}%");
        }

        if (current.CurrentValue is { } currentValue && current.CompletionValue is { } completionValue)
        {
            parts.Add($"raw={currentValue}/{completionValue}");
        }

        return parts.Count == 0 ? "-" : string.Join(", ", parts);
    }

    private static string EstimateEta(CompactProgressUpdate? current, TimeSpan phaseElapsed)
    {
        if (current?.ProgressMode != CompactProgressMode.PercentKnown
            || current.Percent is not { } percent
            || percent <= 0)
        {
            return "-";
        }

        if (!current.IsComplete && percent >= 100)
        {
            return "waiting for completion";
        }

        if (current.IsComplete || percent >= 100)
        {
            return "0:00";
        }

        var seconds = phaseElapsed.TotalSeconds * ((100 - percent) / percent);
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            return "-";
        }

        return FormatDuration(TimeSpan.FromSeconds(seconds));
    }

    private static IReadOnlyList<RunEvent> SelectTranscriptEvents(IReadOnlyList<RunEvent> events, int maxCount)
    {
        var selected = new List<RunEvent>();
        string? previousKey = null;

        foreach (var runEvent in events)
        {
            var key = GetTranscriptKey(runEvent);
            if (string.Equals(previousKey, key, StringComparison.Ordinal))
            {
                selected[^1] = runEvent;
                continue;
            }

            selected.Add(runEvent);
            previousKey = key;
        }

        return selected.TakeLast(maxCount).ToList();
    }

    private static string GetTranscriptKey(RunEvent runEvent)
    {
        var percent = runEvent.Percent is null ? "" : runEvent.Percent.Value.ToString("0.###");
        return string.Join("|", runEvent.Level, runEvent.Kind, runEvent.Distro, runEvent.Backend, runEvent.Phase, runEvent.Message, percent, runEvent.ProgressMode, runEvent.OperationStatus);
    }

    private static bool IsLowPriorityCurrentEvent(CompactProgressUpdate update)
        => string.Equals(update.Phase, "format guard", StringComparison.OrdinalIgnoreCase);

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return duration.ToString(@"h\:mm\:ss");
        }

        return duration.ToString(@"m\:ss");
    }

    private static string Trim(string value, int maxLength)
    {
        var normalized = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..Math.Max(0, maxLength - 3)] + "...";
    }
}
