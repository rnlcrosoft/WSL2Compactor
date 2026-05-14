namespace WSL2Compactor;

using Spectre.Console;
using WSL2Compactor.Services;

internal sealed class TerminalRunDisplay : IProgress<CompactProgressUpdate>
{
    private readonly object _consoleGate = new();
    private readonly RunLogger _log;
    private int _nextConsoleEventIndex;

    public TerminalRunDisplay(RunLogger log)
    {
        _log = log;
        _nextConsoleEventIndex = log.SnapshotEvents().Count;
    }

    public void Report(CompactProgressUpdate value)
    {
        _log.Write(RunEvent.FromUpdate(value));
        FlushConsoleEvents();
    }

    public async Task RunAsync(
        Func<IProgress<CompactProgressUpdate>, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        FlushConsoleEvents();

        try
        {
            await action(this, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            FlushConsoleEvents();
        }
    }

    private void FlushConsoleEvents()
    {
        lock (_consoleGate)
        {
            var events = _log.SnapshotEvents();
            var startIndex = Math.Clamp(_nextConsoleEventIndex, 0, events.Count);
            _nextConsoleEventIndex = events.Count;

            foreach (var runEvent in events.Skip(startIndex).Where(ShouldPrintConsoleEvent))
            {
                AnsiConsole.Write(RenderEvent(runEvent));
                AnsiConsole.WriteLine();
            }
        }
    }

    private static bool ShouldPrintConsoleEvent(RunEvent runEvent)
        => runEvent.Kind != CompactEventKind.Progress;

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
            text += $"[blue]$[/] {Markup.Escape(OneLine(runEvent.Command))}";
        }
        else
        {
            text += Markup.Escape(OneLine(runEvent.Message));
        }

        return new Markup(text);
    }

    private static string OneLine(string value)
        => value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
}
