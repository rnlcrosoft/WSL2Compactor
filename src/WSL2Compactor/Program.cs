namespace WSL2Compactor;

using System.Reflection;
using System.Security.Principal;
using Spectre.Console;
using WSL2Compactor.Models;
using WSL2Compactor.Services;

static class Program
{
    private const string AppName = "WSL2Compactor";
    private const string IssueUrl = "https://github.com/rnlcrosoft/WSL2Compactor/issues/new";
    private const string SingleInstanceMutexName = @"Local\WSL2Compactor.SingleInstance";

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
        var processRunner = new ProcessRunner();
        var virtDiskOperations = new VirtDiskOperationRegistry();
        using var exitGuard = new ExitGuard(log, processRunner, virtDiskOperations);
        var distributionService = new WslDistributionService(processRunner);
        var virtDiskBackend = new VirtDiskCompactBackend(virtDiskOperations);
        var orchestrator = new CompactOrchestrator(processRunner, virtDiskBackend);

        try
        {
            PrintHeader();
            var elevated = IsRunningAsAdministrator();
            log.Info("startup", $"App version: {GetAppVersion()}");
            log.Info("startup", $"Command line: {Environment.CommandLine}");
            log.Info("startup", $"OS version: {Environment.OSVersion}");
            log.Info("startup", $"Elevated: {elevated}");
            log.Info("startup", $"Log file: {logFile}");

            using var singleInstanceGuard = SingleInstanceGuard.TryAcquire(SingleInstanceMutexName);
            if (!singleInstanceGuard.IsAcquired)
            {
                const string message = "Another WSL2Compactor run is already active. Close it before starting a new run.";
                log.Warning("startup", message);
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(message)}[/]");
                AnsiConsole.MarkupLine($"Log saved to: [grey]{Markup.Escape(log.LogFile)}[/]");
                return 1;
            }

            if (!elevated)
            {
                log.Error("startup", "Administrator privileges are required for Windows-side VHDX compaction.");
                AnsiConsole.MarkupLine("[red]Administrator privileges are required for Windows-side VHDX compaction.[/]");
                AnsiConsole.MarkupLine($"Log saved to: [grey]{Markup.Escape(log.LogFile)}[/]");
                return 1;
            }

            log.Info("scan", "Scanning WSL2 distros.");
            AnsiConsole.MarkupLine("[grey]Scanning WSL2 distros...[/]");
            IReadOnlyList<WslDistribution> distributions;
            exitGuard.SetProtected(true);
            try
            {
                using var consoleModeGuard = new ConsoleModeGuard(log, "scan");
                distributions = await distributionService.GetDistributionsAsync(exitGuard.Token).ConfigureAwait(false);
            }
            finally
            {
                exitGuard.SetProtected(false);
            }

            log.Info("scan", distributions.Count == 0
                ? "Detected distros: none"
                : $"Detected distros: {string.Join(" | ", distributions.Select(FormatDistributionForLog))}");
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
            var compactMode = PromptForCompactMode();
            log.Prompt("selection", $"Selected compact mode: {compactMode}");
            var backend = CompactOrchestrator.GetBackendName(compactMode);
            log.Info("selection", $"Backend: {backend}");

            PrintSelectedDistributions(rows);
            AnsiConsole.MarkupLine($"[grey]Compact mode:[/] {Markup.Escape(FormatCompactMode(compactMode))}");
            AnsiConsole.MarkupLine($"[grey]Backend:[/] {Markup.Escape(backend)}");
            AnsiConsole.WriteLine();
            var runConfirmed = AnsiConsole.Confirm("Continue with fstrim, wsl --shutdown and compact selected distros?", defaultValue: true);
            log.Prompt("confirmation", $"Run confirmed: {runConfirmed}");
            if (!runConfirmed)
            {
                log.Info("confirmation", "Run canceled before compaction.");
                return 0;
            }

            var startedAt = DateTimeOffset.Now;
            await RunCompactionAsync(orchestrator, rows, compactMode, exitGuard, log).ConfigureAwait(false);

            var endedAt = DateTimeOffset.Now;

            PrintSummary(rows, startedAt, endedAt, log, $"{FormatCompactMode(compactMode)} summary");
            singleInstanceGuard.Dispose();

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[green]Complete.[/]");
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);

            return 0;
        }
        catch (OperationCanceledException)
        {
            exitGuard.SetProtected(false);
            log.Warning("cancel", "Operation canceled.");
            AnsiConsole.MarkupLine("[yellow]Operation canceled.[/]");
            AnsiConsole.MarkupLine($"Log saved to: [grey]{Markup.Escape(log.LogFile)}[/]");
            return 130;
        }
        catch (CompactFailureException ex)
        {
            exitGuard.SetProtected(false);
            log.Error("error", ex.ToString(), ex.Distro, ex.Backend);
            PrintFailurePanel(ex, log);
            return 1;
        }
        catch (Exception ex)
        {
            exitGuard.SetProtected(false);
            log.Error("error", ex.ToString());
            PrintFailurePanel(
                new CompactFailureException(
                    CompactFailureKind.Unknown,
                    "error",
                    ex.Message,
                    innerException: ex),
                log);
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
        var showLinuxFootprint = distributions.Any(distribution => distribution.LinuxFootprintBytes is not null);
        var table = new Table()
            .Title("Detected WSL2 distros")
            .AddColumn("#")
            .AddColumn("Distro")
            .AddColumn("State")
            .AddColumn(new TableColumn("Linux used").RightAligned());

        if (showLinuxFootprint)
        {
            table
                .AddColumn(new TableColumn("Ext4 overhead").RightAligned())
                .AddColumn(new TableColumn("Linux footprint").RightAligned());
        }

        table
            .AddColumn(new TableColumn("Host allocated").RightAligned())
            .AddColumn(new TableColumn("File length").RightAligned());

        for (var index = 0; index < distributions.Count; index++)
        {
            var distribution = distributions[index];
            var cells = new List<string>
            {
                (index + 1).ToString(),
                Markup.Escape(distribution.Name),
                Markup.Escape(distribution.State),
                FormatOptionalSize(distribution.LinuxUsedBytes)
            };

            if (showLinuxFootprint)
            {
                cells.Add(FormatOptionalSize(distribution.Ext4OverheadBytes));
                cells.Add(FormatOptionalSize(distribution.LinuxFootprintBytes));
            }

            cells.AddRange([
                SizeFormatter.Format(distribution.HostAllocatedBytes),
                SizeFormatter.Format(distribution.VhdxFileSizeBytes)
            ]);
            table.AddRow(cells.ToArray());
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
    }

    private static void PrintSelectedDistributions(IReadOnlyList<DistributionRow> rows)
    {
        var showLinuxFootprint = rows.Any(row => row.BeforeLinuxFootprintBytes is not null);
        var table = new Table()
            .Title("Selected distros")
            .AddColumn("Distro")
            .AddColumn("State")
            .AddColumn(new TableColumn("Linux used").RightAligned());

        if (showLinuxFootprint)
        {
            table
                .AddColumn(new TableColumn("Ext4 overhead").RightAligned())
                .AddColumn(new TableColumn("Linux footprint").RightAligned());
        }

        table
            .AddColumn(new TableColumn("Host allocated").RightAligned())
            .AddColumn(new TableColumn("File length").RightAligned());

        foreach (var row in rows)
        {
            var cells = new List<string>
            {
                Markup.Escape(row.Name),
                Markup.Escape(row.State),
                row.BeforeLinuxUsedText
            };

            if (showLinuxFootprint)
            {
                cells.Add(row.BeforeExt4OverheadText);
                cells.Add(row.BeforeLinuxFootprintText);
            }

            cells.AddRange([
                row.BeforeHostAllocatedText,
                row.BeforeVhdxFileSizeText
            ]);
            table.AddRow(cells.ToArray());
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
                $"{distribution.Name} ({SizeFormatter.Format(distribution.HostAllocatedBytes)} host allocated)",
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

    private static CompactMode PromptForCompactMode()
    {
        var options = new List<CompactModeChoice>
        {
            new("No zero scan", CompactMode.NoZeroScan),
            new("Zero scan", CompactMode.ZeroScan)
        };

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<CompactModeChoice>()
                .Title("Select compact mode")
                .UseConverter(choice => choice.Label)
                .AddChoices(options));

        return choice.Mode;
    }

    private static void PrintFailurePanel(CompactFailureException failure, RunLogger log)
    {
        var table = new Table().Expand();
        table.AddColumn("Field");
        table.AddColumn("Value");
        table.AddRow("Kind", Markup.Escape(failure.Kind.ToString()));
        table.AddRow("Phase", Markup.Escape(failure.Phase));
        table.AddRow("Distro", Markup.Escape(failure.Distro ?? "-"));
        table.AddRow("Backend", Markup.Escape(failure.Backend ?? "-"));
        table.AddRow("VHDX", Markup.Escape(failure.VhdPath ?? "-"));
        table.AddRow("Error", Markup.Escape(failure.Message));
        table.AddRow("Exit code", Markup.Escape(failure.ExitCode?.ToString() ?? "-"));
        table.AddRow("Win32 error", Markup.Escape(failure.Win32ErrorCode is null ? "-" : $"0x{failure.Win32ErrorCode:X8}"));
        table.AddRow("Log", Markup.Escape(log.LogFile));
        table.AddRow("Issue URL", Markup.Escape(IssueUrl));

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(table)
            .Header("Failure")
            .BorderColor(Color.Red)
            .Expand());
    }

    private static void PrintSummary(
        IReadOnlyList<DistributionRow> rows,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        RunLogger log,
        string title = "Summary")
    {
        var showLinuxFootprint = rows.Any(row => row.BeforeLinuxFootprintBytes is not null);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[grey]Started:[/] {Markup.Escape(startedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"))}  " +
            $"[grey]Ended:[/] {Markup.Escape(endedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"))}  " +
            $"[grey]Elapsed:[/] {Markup.Escape(FormatDuration(endedAt - startedAt))}");

        var table = new Table()
            .Title(title)
            .AddColumn("Distro")
            .AddColumn("Elapsed")
            .AddColumn("Linux used");

        if (showLinuxFootprint)
        {
            table
                .AddColumn("Ext4 overhead")
                .AddColumn("Linux footprint");
        }

        table
            .AddColumn("Host allocated")
            .AddColumn("Saved")
            .AddColumn("File length");

        foreach (var row in rows)
        {
            var savedBytes = Math.Max(0, row.BeforeHostAllocatedBytes - (row.AfterHostAllocatedBytes ?? row.BeforeHostAllocatedBytes));
            var message = $"Successfully compacted {row.Name}: actual host saved {savedBytes:N0} bytes ({SizeFormatter.Format(savedBytes)}). VHDX file size: {row.BeforeVhdxFileSizeText} -> {row.AfterVhdxFileSizeText}.";
            var cells = new List<string>
            {
                Markup.Escape(row.Name),
                FormatDuration(endedAt - startedAt),
                row.BeforeLinuxUsedText
            };

            if (showLinuxFootprint)
            {
                cells.Add(row.BeforeExt4OverheadText);
                cells.Add(row.BeforeLinuxFootprintText);
            }

            cells.AddRange([
                FormatBeforeAfter(row.BeforeHostAllocatedText, row.AfterHostAllocatedText),
                SizeFormatter.Format(savedBytes),
                FormatBeforeAfter(row.BeforeVhdxFileSizeText, row.AfterVhdxFileSizeText)
            ]);
            table.AddRow(cells.ToArray());
            log.Write(CompactProgressUpdate.Size(
                "summary",
                message,
                beforeBytes: row.BeforeHostAllocatedBytes,
                afterBytes: row.AfterHostAllocatedBytes,
                savedBytes: savedBytes,
                distro: row.Name,
                backend: row.Backend));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Log saved to: [grey]{Markup.Escape(log.LogFile)}[/]");
    }

    private static async Task RunCompactionAsync(
        CompactOrchestrator orchestrator,
        IReadOnlyList<DistributionRow> rows,
        CompactMode compactMode,
        ExitGuard exitGuard,
        RunLogger log)
    {
        var display = new TerminalRunDisplay(log);
        exitGuard.SetProtected(true);
        try
        {
            using var consoleModeGuard = new ConsoleModeGuard(log, "compaction");
            await display.RunAsync(
                (progress, token) => orchestrator.RunAsync(rows, compactMode, progress, token),
                exitGuard.Token).ConfigureAwait(false);
        }
        finally
        {
            exitGuard.SetProtected(false);
        }
    }

    private static string FormatBeforeAfter(string before, string after)
        => $"{before} -> {after}";

    private static string FormatCompactMode(CompactMode compactMode)
        => compactMode == CompactMode.ZeroScan ? "Zero scan" : "No zero scan";

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string GetAppVersion()
        => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    private static string FormatDistributionForLog(WslDistribution distribution)
        => $"{distribution.Name}; state={distribution.State}; linuxUsed={FormatNullableBytesForLog(distribution.LinuxUsedBytes)}; ext4Overhead={FormatNullableBytesForLog(distribution.Ext4OverheadBytes)}; linuxFootprint={FormatNullableBytesForLog(distribution.LinuxFootprintBytes)}; linuxUsageSource={distribution.LinuxUsageSource ?? "-"}; hostAllocated={distribution.HostAllocatedBytes}; vhdxFileSize={distribution.VhdxFileSizeBytes}; vhd={distribution.VhdPath}";

    private static string FormatOptionalSize(long? bytes)
        => bytes is null ? "-" : SizeFormatter.Format(bytes.Value);

    private static string FormatNullableBytesForLog(long? bytes)
        => bytes is null ? "-" : bytes.Value.ToString();

    private sealed record DistributionChoice(string Label, IReadOnlyList<WslDistribution> Distributions);

    private sealed record CompactModeChoice(string Label, CompactMode Mode);

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return duration.ToString(@"h\:mm\:ss");
        }

        return duration.ToString(@"m\:ss");
    }
}
