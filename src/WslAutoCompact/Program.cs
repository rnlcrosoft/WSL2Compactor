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
        PrintDistributions(distributions);

        var options = new List<MenuOption<IReadOnlyList<WslDistribution>>>
        {
            new("All distros", distributions)
        };

        options.AddRange(distributions.Select(distribution =>
            new MenuOption<IReadOnlyList<WslDistribution>>(
                $"{distribution.Name} ({SizeFormatter.Format(distribution.SizeBytes)})",
                [distribution])));
        options.Add(new MenuOption<IReadOnlyList<WslDistribution>>("Cancel", []));

        return PromptMenu("Select distros to compact", options, allowCancel: true, cancelValue: []);
    }

    private static async Task<BackendMode> PromptForBackendAsync(OptimizeVhdCompactBackend optimizeVhdBackend)
    {
        var optimizeAvailable = await optimizeVhdBackend.IsAvailableAsync(CancellationToken.None).ConfigureAwait(false);

        var options = new List<MenuOption<BackendMode>>
        {
            new("VirtDisk API (recommended, fallback: DiskPart)", BackendMode.VirtDisk)
        };

        if (optimizeAvailable)
        {
            options.Add(new MenuOption<BackendMode>("Optimize-VHD (fallback: DiskPart)", BackendMode.OptimizeVhd));
        }

        return PromptMenu("Choose backend", options, allowCancel: false, cancelValue: BackendMode.VirtDisk);
    }

    private static T PromptMenu<T>(string title, IReadOnlyList<MenuOption<T>> options, bool allowCancel, T cancelValue)
    {
        if (options.Count == 0)
        {
            throw new ArgumentException("At least one menu option is required.", nameof(options));
        }

        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));
        Console.WriteLine(allowCancel
            ? "Use Up/Down arrows to move, Enter to select, Esc or Q to cancel."
            : "Use Up/Down arrows to move, Enter to select.");

        var selectedIndex = 0;
        var menuTop = Console.CursorTop;
        var previousCursorVisible = Console.CursorVisible;
        Console.CursorVisible = false;

        try
        {
            while (true)
            {
                RenderMenu(options, selectedIndex, menuTop);
                var key = Console.ReadKey(intercept: true);

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        selectedIndex = selectedIndex == 0 ? options.Count - 1 : selectedIndex - 1;
                        break;
                    case ConsoleKey.DownArrow:
                        selectedIndex = (selectedIndex + 1) % options.Count;
                        break;
                    case ConsoleKey.Home:
                        selectedIndex = 0;
                        break;
                    case ConsoleKey.End:
                        selectedIndex = options.Count - 1;
                        break;
                    case ConsoleKey.Enter:
                        RenderMenu(options, selectedIndex, menuTop);
                        Console.SetCursorPosition(0, menuTop + options.Count);
                        Console.WriteLine();
                        return options[selectedIndex].Value;
                    case ConsoleKey.Escape:
                    case ConsoleKey.Q:
                        if (allowCancel)
                        {
                            RenderMenu(options, selectedIndex, menuTop);
                            Console.SetCursorPosition(0, menuTop + options.Count);
                            Console.WriteLine();
                            return cancelValue;
                        }

                        break;
                }
            }
        }
        finally
        {
            Console.CursorVisible = previousCursorVisible;
        }
    }

    private static void RenderMenu<T>(IReadOnlyList<MenuOption<T>> options, int selectedIndex, int top)
    {
        for (var index = 0; index < options.Count; index++)
        {
            Console.SetCursorPosition(0, top + index);
            var marker = index == selectedIndex ? ">" : " ";
            var text = $"{marker} {options[index].Label}";
            Console.Write(text.PadRight(Math.Max(Console.WindowWidth - 1, text.Length)));
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

    private sealed record MenuOption<T>(string Label, T Value);

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
