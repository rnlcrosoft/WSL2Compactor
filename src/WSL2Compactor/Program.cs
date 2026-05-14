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

        var log = new RunLogger(logFile);
        using var consoleModeGuard = new ConsoleModeGuard(log);
        using var exitGuard = new ExitGuard(log);
        var processRunner = new ProcessRunner();
        var distributionService = new WslDistributionService(processRunner);
        var virtDiskBackend = new VirtDiskCompactBackend();
        var diskPartBackend = new DiskPartCompactBackend(processRunner);
        var optimizeVhdBackend = new OptimizeVhdCompactBackend(processRunner);
        var orchestrator = new CompactOrchestrator(processRunner, virtDiskBackend, diskPartBackend, optimizeVhdBackend);

        try
        {
            PrintHeader();
            log.Info("startup", $"Log file: {logFile}");

            if (!IsRunningAsAdministrator())
            {
                log.Warning("startup", "This process is not elevated. Published builds request administrator privileges automatically.");
                AnsiConsole.MarkupLine("[yellow]Warning:[/] This process is not elevated.");
                var continueAnyway = AnsiConsole.Confirm("Continue anyway?", defaultValue: true);
                log.Prompt("startup", $"Continue without elevation: {continueAnyway}");
                if (!continueAnyway)
                {
                    return 1;
                }
            }

            log.Info("scan", "Scanning WSL2 distros.");
            AnsiConsole.MarkupLine("[grey]Scanning WSL2 distros...[/]");
            var distributions = await distributionService.GetDistributionsAsync(CancellationToken.None).ConfigureAwait(false);
            if (distributions.Count == 0)
            {
                log.Warning("scan", "No WSL2 ext4.vhdx files were found.");
                AnsiConsole.MarkupLine("[yellow]No WSL2 ext4.vhdx files were found.[/]");
                return 0;
            }

            PrintDistributions(distributions);
            var selected = PromptForDistributions(distributions);
            log.Prompt("selection", selected.Count == 0
                ? "Selected distros: none"
                : $"Selected distros: {string.Join(", ", selected.Select(distribution => distribution.Name))}");
            if (selected.Count == 0)
            {
                log.Info("selection", "No distros selected.");
                return 0;
            }

            var rows = selected.Select(distribution => new DistributionRow(distribution)).ToList();
            var backendMode = await PromptForBackendAsync(optimizeVhdBackend).ConfigureAwait(false);
            log.Prompt("selection", $"Selected backend mode: {backendMode}");

            PrintSelectedDistributions(rows);
            AnsiConsole.WriteLine();
            var runConfirmed = AnsiConsole.Confirm("Run wsl --shutdown and compact selected distros?", defaultValue: true);
            log.Prompt("confirmation", $"Run confirmed: {runConfirmed}");
            if (!runConfirmed)
            {
                log.Info("confirmation", "Run canceled before compaction.");
                return 0;
            }

            var startedAt = DateTimeOffset.Now;
            var display = new TerminalRunDisplay(rows, log);
            exitGuard.SetProtected(true);
            try
            {
                await display.RunAsync(
                    (progress, token) => orchestrator.RunAsync(rows, backendMode, progress, token),
                    exitGuard.Token).ConfigureAwait(false);
            }
            finally
            {
                exitGuard.SetProtected(false);
            }

            var endedAt = DateTimeOffset.Now;

            PrintSummary(rows, startedAt, endedAt, log);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[green]Complete.[/] You can close this terminal window when ready. Press Ctrl+C to exit this process.");
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            exitGuard.SetProtected(false);
            log.Warning("cancel", "Run canceled.");
            AnsiConsole.MarkupLine("[yellow]Canceled.[/]");
            AnsiConsole.MarkupLine($"Log saved to: [grey]{Markup.Escape(log.LogFile)}[/]");
            return 130;
        }
        catch (Exception ex)
        {
            exitGuard.SetProtected(false);
            log.Error("error", ex.ToString());
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine($"Log saved to: [grey]{Markup.Escape(log.LogFile)}[/]");
            return 1;
        }
    }

    private static void PrintHeader()
    {
        AnsiConsole.Write(new FigletText("WSL2Compactor").LeftJustified().Color(Color.Teal));
        AnsiConsole.MarkupLine("[grey]Interactive app for compacting WSL2 ext4.vhdx files.[/]");
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

    private static void PrintSelectedDistributions(IReadOnlyList<DistributionRow> rows)
    {
        var table = new Table()
            .Title("Selected distros")
            .AddColumn("Distro")
            .AddColumn("State")
            .AddColumn(new TableColumn("Current size").RightAligned())
            .AddColumn("VHDX");

        foreach (var row in rows)
        {
            table.AddRow(
                Markup.Escape(row.Name),
                Markup.Escape(row.State),
                SizeFormatter.Format(row.BeforeBytes),
                Markup.Escape(row.VhdPath));
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

    private static void PrintSummary(
        IReadOnlyList<DistributionRow> rows,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        RunLogger log)
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
            log.Write(CompactProgressUpdate.Size(
                "summary",
                message,
                beforeBytes: row.BeforeBytes,
                afterBytes: row.AfterBytes,
                savedBytes: savedBytes,
                distro: row.Name,
                backend: row.Backend));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Log saved to: [grey]{Markup.Escape(log.LogFile)}[/]");
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed record DistributionChoice(string Label, IReadOnlyList<WslDistribution> Distributions);

    private sealed record BackendChoice(string Label, BackendMode Mode);

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return duration.ToString(@"h\:mm\:ss");
        }

        return duration.ToString(@"m\:ss");
    }
}
