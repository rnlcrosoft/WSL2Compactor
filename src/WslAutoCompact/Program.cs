namespace WslAutoCompact;

using System.Security.Principal;
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
                if (!Confirm("Continue anyway?", defaultNo: true))
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

            Console.WriteLine();
            Console.WriteLine("Running compact will stop WSL.");
            Console.WriteLine("Docker Desktop, VS Code Remote, and open WSL terminals may be interrupted.");
            Console.WriteLine("Format prompts are monitored and closed during compact operations.");
            if (!Confirm("Continue?", defaultNo: true))
            {
                log.Report("Canceled.");
                return 0;
            }

            var rows = selected.Select(distribution => new DistributionRow(distribution)).ToList();
            await orchestrator.RunAsync(rows, backendMode, log, CancellationToken.None).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("Summary");
            Console.WriteLine("-------");
            foreach (var row in rows)
            {
                var savedBytes = Math.Max(0, row.BeforeBytes - (row.AfterBytes ?? row.BeforeBytes));
                Console.WriteLine($"Successfully compressed {row.Name}: {savedBytes:N0} bytes saved ({SizeFormatter.Format(savedBytes)}).");
            }

            Console.WriteLine();
            Console.WriteLine($"Log saved to: {logFile}");
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
        Console.WriteLine(AppName);
        Console.WriteLine(new string('=', AppName.Length));
        Console.WriteLine("Interactive CLI for compacting WSL2 ext4.vhdx files.");
        Console.WriteLine();
    }

    private static void PrintDistributions(IReadOnlyList<WslDistribution> distributions)
    {
        Console.WriteLine();
        Console.WriteLine("Detected WSL2 distros");
        Console.WriteLine("---------------------");

        for (var index = 0; index < distributions.Count; index++)
        {
            var distribution = distributions[index];
            Console.WriteLine($"[{index + 1}] {distribution.Name}");
            Console.WriteLine($"    State: {distribution.State}");
            Console.WriteLine($"    Size:  {SizeFormatter.Format(distribution.SizeBytes)}");
            Console.WriteLine($"    VHDX:  {distribution.VhdPath}");
        }
    }

    private static IReadOnlyList<WslDistribution> PromptForDistributions(IReadOnlyList<WslDistribution> distributions)
    {
        while (true)
        {
            Console.WriteLine();
            Console.Write("Select distros to compact [all, numbers like 1,2, or q]: ");
            var input = (Console.ReadLine() ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(input) || input.Equals("all", StringComparison.OrdinalIgnoreCase) || input.Equals("a", StringComparison.OrdinalIgnoreCase))
            {
                return distributions;
            }

            if (input.Equals("q", StringComparison.OrdinalIgnoreCase) || input.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            var selected = new List<WslDistribution>();
            var invalid = false;
            foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(part, out var number) || number < 1 || number > distributions.Count)
                {
                    invalid = true;
                    break;
                }

                var distribution = distributions[number - 1];
                if (!selected.Contains(distribution))
                {
                    selected.Add(distribution);
                }
            }

            if (!invalid && selected.Count > 0)
            {
                return selected;
            }

            Console.WriteLine("Invalid selection.");
        }
    }

    private static async Task<BackendMode> PromptForBackendAsync(OptimizeVhdCompactBackend optimizeVhdBackend)
    {
        var optimizeAvailable = await optimizeVhdBackend.IsAvailableAsync(CancellationToken.None).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("Backend");
        Console.WriteLine("-------");
        Console.WriteLine("[1] VirtDisk API (recommended, fallback: DiskPart)");
        if (optimizeAvailable)
        {
            Console.WriteLine("[2] Optimize-VHD (fallback: DiskPart)");
        }

        while (true)
        {
            Console.Write("Choose backend [1]: ");
            var input = (Console.ReadLine() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(input) || input == "1")
            {
                return BackendMode.VirtDisk;
            }

            if (optimizeAvailable && input == "2")
            {
                return BackendMode.OptimizeVhd;
            }

            Console.WriteLine("Invalid backend.");
        }
    }

    private static bool Confirm(string prompt, bool defaultNo)
    {
        var suffix = defaultNo ? " [y/N]: " : " [Y/n]: ";
        while (true)
        {
            Console.Write(prompt);
            Console.Write(suffix);
            var input = (Console.ReadLine() ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(input))
            {
                return !defaultNo;
            }

            if (input.Equals("y", StringComparison.OrdinalIgnoreCase) || input.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (input.Equals("n", StringComparison.OrdinalIgnoreCase) || input.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Console.WriteLine("Please answer yes or no.");
        }
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

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
                Console.WriteLine(line);
                File.AppendAllText(_logFile, line + Environment.NewLine);
            }
        }
    }
}
