namespace WSL2Compactor;

using System.Security.Principal;
using Spectre.Console;
using WSL2Compactor.Models;
using WSL2Compactor.Services;

static class Program
{
    private const string AppName = "WSL2Compactor";

    static async Task<int> Main()
    {
        Console.Title = AppName;

        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WSL2Compactor",
            "Logs");
        Directory.CreateDirectory(logDirectory);
        var logFile = Path.Combine(logDirectory, $"wsl2compactor-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        var log = new FileLog(logFile);
        var processRunner = new ProcessRunner();
        var distributionService = new WslDistributionService(processRunner);
        var virtDiskBackend = new VirtDiskCompactBackend();
        var diskPartBackend = new DiskPartCompactBackend(processRunner);
        var optimizeVhdBackend = new OptimizeVhdCompactBackend(processRunner);
        var orchestrator = new CompactOrchestrator(processRunner, virtDiskBackend, diskPartBackend, optimizeVhdBackend);

        try
        {
            PrintHeader();
            log.Write($"Log file: {logFile}");

            if (!IsRunningAsAdministrator())
            {
                log.Write("Warning: This process is not elevated. Published builds request administrator privileges automatically.");
                AnsiConsole.MarkupLine("[yellow]Warning:[/] This process is not elevated.");
                if (!AnsiConsole.Confirm("Continue anyway?", defaultValue: true))
                {
                    return 1;
                }
            }

            log.Write("Scanning WSL2 distros...");
            AnsiConsole.MarkupLine("[grey]Scanning WSL2 distros...[/]");
            var distributions = await distributionService.GetDistributionsAsync(CancellationToken.None).ConfigureAwait(false);
            if (distributions.Count == 0)
            {
                log.Write("No WSL2 ext4.vhdx files were found.");
                AnsiConsole.MarkupLine("[yellow]No WSL2 ext4.vhdx files were found.[/]");
                return 0;
            }

            PrintDistributions(distributions);
            var selected = PromptForDistributions(distributions);
            if (selected.Count == 0)
            {
                log.Write("No distros selected.");
                return 0;
            }

            var backendMode = await PromptForBackendAsync(optimizeVhdBackend).ConfigureAwait(false);

            AnsiConsole.WriteLine();
            if (!AnsiConsole.Confirm("Run wsl --shutdown and compact selected distros?", defaultValue: true))
            {
                log.Write("Canceled.");
                return 0;
            }

            var startedAt = DateTimeOffset.Now;
            var rows = selected.Select(distribution => new DistributionRow(distribution)).ToList();
            await RunWithProgressAsync(orchestrator, rows, backendMode, logFile, CancellationToken.None).ConfigureAwait(false);
            var endedAt = DateTimeOffset.Now;

            PrintSummary(rows, startedAt, endedAt, logFile, log);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[green]Complete.[/] You can close this terminal window when ready. Press Ctrl+C to exit this process.");
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            log.Write("Canceled.");
            return 130;
        }
        catch (Exception ex)
        {
            log.Write($"Error: {ex.Message}");
            log.Write($"Log saved to: {logFile}");
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine($"Log saved to: [grey]{Markup.Escape(logFile)}[/]");
            return 1;
        }
    }

    private static void PrintHeader()
    {
        AnsiConsole.Write(new FigletText("WSL2Compactor").LeftJustified().Color(Color.Teal));
        AnsiConsole.MarkupLine("[grey]Interactive CLI for compacting WSL2 ext4.vhdx files.[/]");
        AnsiConsole.WriteLine();
    }

    private static void PrintDistributions(IReadOnlyList<WslDistribution> distributions)
    {
        var table = new Table()
            .Title("Detected WSL2 distros")
            .AddColumn("#")
            .AddColumn("Distro")
            .AddColumn("State")
            .AddColumn(new TableColumn("Size").RightAligned())
            .AddColumn("VHDX");

        for (var index = 0; index < distributions.Count; index++)
        {
            var distribution = distributions[index];
            table.AddRow(
                (index + 1).ToString(),
                Markup.Escape(distribution.Name),
                Markup.Escape(distribution.State),
                SizeFormatter.Format(distribution.SizeBytes),
                Markup.Escape(distribution.VhdPath));
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
    }

    private static IReadOnlyList<WslDistribution> PromptForDistributions(IReadOnlyList<WslDistribution> distributions)
    {
        var options = new List<DistributionChoice>
        {
            new("All distros", distributions)
        };

        options.AddRange(distributions.Select(distribution =>
            new DistributionChoice(
                $"{distribution.Name} ({SizeFormatter.Format(distribution.SizeBytes)})",
                [distribution])));
        options.Add(new DistributionChoice("Cancel", []));

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<DistributionChoice>()
                .Title("Select distros to compact")
                .PageSize(Math.Min(options.Count, 10))
                .MoreChoicesText("[grey](Move up and down to reveal more distros)[/]")
                .UseConverter(choice => choice.Label)
                .AddChoices(options));

        return choice.Distributions;
    }

    private static async Task<BackendMode> PromptForBackendAsync(OptimizeVhdCompactBackend optimizeVhdBackend)
    {
        var optimizeAvailable = await optimizeVhdBackend.IsAvailableAsync(CancellationToken.None).ConfigureAwait(false);

        var options = new List<BackendChoice>
        {
            new("VirtDisk API (recommended, fallback: DiskPart)", BackendMode.VirtDisk)
        };

        if (optimizeAvailable)
        {
            options.Add(new BackendChoice("Optimize-VHD (fallback: DiskPart)", BackendMode.OptimizeVhd));
        }

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<BackendChoice>()
                .Title("Choose backend")
                .UseConverter(choice => choice.Label)
                .AddChoices(options));

        return choice.Mode;
    }

    private static Task RunWithProgressAsync(
        CompactOrchestrator orchestrator,
        IReadOnlyList<DistributionRow> rows,
        BackendMode backendMode,
        string logFile,
        CancellationToken cancellationToken)
    {
        return AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
            [
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new ElapsedTimeColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn()
            ])
            .StartAsync(async context =>
            {
                var progress = new SpectreProgressSink(logFile, context);
                await orchestrator.RunAsync(rows, backendMode, progress, cancellationToken).ConfigureAwait(false);
            });
    }

    private static void PrintSummary(
        IReadOnlyList<DistributionRow> rows,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string logFile,
        FileLog log)
    {
        AnsiConsole.WriteLine();
        var table = new Table()
            .Title("Summary")
            .AddColumn("Distro")
            .AddColumn("Started")
            .AddColumn("Ended")
            .AddColumn("Elapsed")
            .AddColumn("Before")
            .AddColumn("After")
            .AddColumn(new TableColumn("Bytes saved").RightAligned())
            .AddColumn("Saved")
            .AddColumn("Backend");

        foreach (var row in rows)
        {
            var savedBytes = Math.Max(0, row.BeforeBytes - (row.AfterBytes ?? row.BeforeBytes));
            var message = $"Successfully compressed {row.Name}: {savedBytes:N0} bytes saved ({SizeFormatter.Format(savedBytes)}).";
            table.AddRow(
                Markup.Escape(row.Name),
                startedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                endedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                FormatDuration(endedAt - startedAt),
                SizeFormatter.Format(row.BeforeBytes),
                row.AfterBytes is null ? "-" : SizeFormatter.Format(row.AfterBytes.Value),
                savedBytes.ToString("N0"),
                SizeFormatter.Format(savedBytes),
                Markup.Escape(row.Backend));
            log.Write(message);
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Log saved to: [grey]{Markup.Escape(logFile)}[/]");
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed record DistributionChoice(string Label, IReadOnlyList<WslDistribution> Distributions);

    private sealed record BackendChoice(string Label, BackendMode Mode);

    private sealed class SpectreProgressSink : IProgress<CompactProgressUpdate>
    {
        private readonly string _logFile;
        private readonly ProgressContext _context;
        private readonly object _gate = new();
        private ProgressTask? _task;

        public SpectreProgressSink(string logFile, ProgressContext context)
        {
            _logFile = logFile;
            _context = context;
        }

        public void Report(CompactProgressUpdate value)
        {
            lock (_gate)
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] {BuildDescription(value, plainText: true)}";
                File.AppendAllText(_logFile, line + Environment.NewLine);

                _task ??= _context.AddTask("Starting", new ProgressTaskSettings { MaxValue = 100, AutoStart = true });
                _task.Description(BuildDescription(value, plainText: false));

                if (value.Percent is { } percent)
                {
                    _task.IsIndeterminate(false);
                    _task.MaxValue(100);
                    _task.Value(Math.Clamp(percent, 0, 100));
                }
                else
                {
                    _task.IsIndeterminate(true);
                }
            }
        }

        private static string BuildDescription(CompactProgressUpdate value, bool plainText)
        {
            var parts = new[]
            {
                value.Distro,
                value.Backend,
                value.Phase,
                value.Message
            }.Where(part => !string.IsNullOrWhiteSpace(part));

            var description = string.Join(" - ", parts);
            return plainText ? description : Markup.Escape(description);
        }
    }

    private sealed class FileLog
    {
        private readonly string _logFile;
        private readonly object _gate = new();

        public FileLog(string logFile)
        {
            _logFile = logFile;
        }

        public void Write(string value)
        {
            lock (_gate)
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] {value}";
                File.AppendAllText(_logFile, line + Environment.NewLine);
            }
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return duration.ToString(@"h\:mm\:ss");
        }

        return duration.ToString(@"m\:ss");
    }
}
