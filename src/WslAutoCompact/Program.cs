namespace WslAutoCompact;

using System.Security.Principal;
using Spectre.Console;
using WslAutoCompact.Models;
using WslAutoCompact.Services;

static class Program
{
    private const string AppName = "WSL Auto Compact";

    static async Task<int> Main()
    {
        Console.Title = AppName;

        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WSL Auto Compact",
            "Logs");
        Directory.CreateDirectory(logDirectory);
        var logFile = Path.Combine(logDirectory, $"wsl-auto-compact-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        var log = new ConsoleFileProgress(logFile);
        var processRunner = new ProcessRunner();
        var distributionService = new WslDistributionService(processRunner);
        var virtDiskBackend = new VirtDiskCompactBackend();
        var diskPartBackend = new DiskPartCompactBackend(processRunner);
        var optimizeVhdBackend = new OptimizeVhdCompactBackend(processRunner);
        var orchestrator = new CompactOrchestrator(processRunner, virtDiskBackend, diskPartBackend, optimizeVhdBackend);

        try
        {
            PrintHeader();
            log.Report($"Log file: {logFile}");

            if (!IsRunningAsAdministrator())
            {
                log.Report("Warning: This process is not elevated. Published builds request administrator privileges automatically.");
                if (!AnsiConsole.Confirm("Continue anyway?", defaultValue: false))
                {
                    return 1;
                }
            }

            log.Report("Scanning WSL2 distros...");
            var distributions = await distributionService.GetDistributionsAsync(CancellationToken.None).ConfigureAwait(false);
            if (distributions.Count == 0)
            {
                log.Report("No WSL2 ext4.vhdx files were found.");
                return 0;
            }

            PrintDistributions(distributions);
            var selected = PromptForDistributions(distributions);
            if (selected.Count == 0)
            {
                log.Report("No distros selected.");
                return 0;
            }

            var backendMode = await PromptForBackendAsync(optimizeVhdBackend).ConfigureAwait(false);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]This will run wsl --shutdown before compacting.[/]");
            if (!AnsiConsole.Confirm("Continue?", defaultValue: false))
            {
                log.Report("Canceled.");
                return 0;
            }

            var rows = selected.Select(distribution => new DistributionRow(distribution)).ToList();
            await orchestrator.RunAsync(rows, backendMode, log, CancellationToken.None).ConfigureAwait(false);

            PrintSummary(rows, log);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"Log saved to: [grey]{Markup.Escape(logFile)}[/]");
            return 0;
        }
        catch (OperationCanceledException)
        {
            log.Report("Canceled.");
            return 130;
        }
        catch (Exception ex)
        {
            log.Report($"Error: {ex.Message}");
            log.Report($"Log saved to: {logFile}");
            return 1;
        }
    }

    private static void PrintHeader()
    {
        AnsiConsole.Write(new FigletText("WSL Auto Compact").LeftJustified().Color(Color.Teal));
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

    private static void PrintSummary(IReadOnlyList<DistributionRow> rows, ConsoleFileProgress log)
    {
        AnsiConsole.WriteLine();
        var table = new Table()
            .Title("Summary")
            .AddColumn("Distro")
            .AddColumn(new TableColumn("Bytes saved").RightAligned())
            .AddColumn("Saved")
            .AddColumn("Backend");

        foreach (var row in rows)
        {
            var savedBytes = Math.Max(0, row.BeforeBytes - (row.AfterBytes ?? row.BeforeBytes));
            var message = $"Successfully compressed {row.Name}: {savedBytes:N0} bytes saved ({SizeFormatter.Format(savedBytes)}).";
            table.AddRow(
                Markup.Escape(row.Name),
                savedBytes.ToString("N0"),
                SizeFormatter.Format(savedBytes),
                Markup.Escape(row.Backend));
            log.WriteFileLine(message);
        }

        AnsiConsole.Write(table);
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed record DistributionChoice(string Label, IReadOnlyList<WslDistribution> Distributions);

    private sealed record BackendChoice(string Label, BackendMode Mode);

    private sealed class ConsoleFileProgress : IProgress<string>
    {
        private readonly string _logFile;
        private readonly object _gate = new();

        public ConsoleFileProgress(string logFile)
        {
            _logFile = logFile;
        }

        public void Report(string value)
        {
            lock (_gate)
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] {value}";
                AnsiConsole.MarkupLine(Markup.Escape(line));
                File.AppendAllText(_logFile, line + Environment.NewLine);
            }
        }

        public void WriteFileLine(string value)
        {
            lock (_gate)
            {
                File.AppendAllText(_logFile, value + Environment.NewLine);
            }
        }
    }
}
