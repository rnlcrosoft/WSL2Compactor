using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WSL2Compactor.Models;

namespace WSL2Compactor.Services;

internal sealed partial class WslDistributionService
{
    private const string LxssRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Lxss";
    private readonly ProcessRunner _processRunner;

    public WslDistributionService(ProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<IReadOnlyList<WslDistribution>> GetDistributionsAsync(CancellationToken cancellationToken)
    {
        var states = await GetWslStatesAsync(cancellationToken).ConfigureAwait(false);
        using var lxssKey = Registry.CurrentUser.OpenSubKey(LxssRegistryPath);

        if (lxssKey is null)
        {
            return [];
        }

        var distributions = new List<WslDistribution>();
        foreach (var subKeyName in lxssKey.GetSubKeyNames())
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var distroKey = lxssKey.OpenSubKey(subKeyName);
            if (distroKey is null)
            {
                continue;
            }

            var name = distroKey.GetValue("DistributionName") as string;
            var basePath = distroKey.GetValue("BasePath") as string;
            var version = ToInt32(distroKey.GetValue("Version"));

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(basePath) || version != 2)
            {
                continue;
            }

            var vhdPath = Path.Combine(basePath, "ext4.vhdx");
            if (!File.Exists(vhdPath))
            {
                continue;
            }

            var size = VhdxSizeProbe.Read(vhdPath);
            var detectedState = states.TryGetValue(name, out var parsedState) ? parsedState : "Unknown";
            var linuxUsageResult = await ReadLinuxUsageAsync(name, detectedState, vhdPath, cancellationToken)
                .ConfigureAwait(false);
            var linuxUsage = linuxUsageResult.Usage;
            distributions.Add(new WslDistribution(
                name,
                basePath,
                version,
                vhdPath,
                linuxUsageResult.State,
                size.HostAllocatedBytes,
                size.FileSizeBytes,
                linuxUsage?.UsedBytes,
                linuxUsage?.Ext4OverheadBytes,
                linuxUsage?.Source));
        }

        return distributions.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<LinuxUsageSnapshot?> ReadLinuxUsageViaWslAsync(string distroName, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync(
                "wsl.exe",
                ["-d", distroName, "--user", "root", "df", "-B1", "--output=used", "/"],
                log: null,
                cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                return null;
            }

            var output = ProcessRunner.NormalizeProcessText(result.StandardOutput);
            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Reverse())
            {
                if (long.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var usedBytes))
                {
                    var overheadBytes = await ReadExt4OverheadViaWslAsync(distroName, cancellationToken).ConfigureAwait(false);
                    var source = overheadBytes is null ? "df" : "df+ext4 superblock";
                    return new LinuxUsageSnapshot(usedBytes, overheadBytes, source);
                }
            }
        }
        catch
        {
            // Linux usage is informational. Discovery should continue without it.
        }

        return null;
    }

    private async Task<LinuxUsageReadResult> ReadLinuxUsageAsync(
        string distroName,
        string state,
        string vhdPath,
        CancellationToken cancellationToken)
    {
        if (IsRunning(state))
        {
            var runningUsage = await ReadLinuxUsageViaWslAsync(distroName, cancellationToken).ConfigureAwait(false)
                ?? VhdxExt4UsageProbe.TryRead(vhdPath);
            return new LinuxUsageReadResult(runningUsage, state);
        }

        var offlineUsage = VhdxExt4UsageProbe.TryRead(vhdPath);
        if (offlineUsage is not null)
        {
            return new LinuxUsageReadResult(offlineUsage, state);
        }

        var fallbackUsage = await ReadLinuxUsageViaWslAsync(distroName, cancellationToken).ConfigureAwait(false);
        return fallbackUsage is null
            ? new LinuxUsageReadResult(null, state)
            : new LinuxUsageReadResult(fallbackUsage, "Running");
    }

    private async Task<long?> ReadExt4OverheadViaWslAsync(string distroName, CancellationToken cancellationToken)
    {
        try
        {
            const string command = "root_device=$(findmnt -no SOURCE / | head -n 1) && [ -n \"$root_device\" ] && dd if=\"$root_device\" bs=1024 skip=1 count=1 status=none 2>/dev/null | base64 | tr -d '\\n'";
            var result = await _processRunner.RunAsync(
                "wsl.exe",
                ["-d", distroName, "--user", "root", "sh", "-c", command],
                log: null,
                cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                return null;
            }

            var output = ProcessRunner.NormalizeProcessText(result.StandardOutput).Trim();
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            var superBlock = Convert.FromBase64String(output);
            return VhdxExt4UsageProbe.TryReadSuperBlock(superBlock, "ext4 superblock")?.Ext4OverheadBytes;
        }
        catch
        {
            // Ext4 overhead is informational. Discovery should continue without it.
            return null;
        }
    }

    private async Task<Dictionary<string, string>> GetWslStatesAsync(CancellationToken cancellationToken)
    {
        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var result = await _processRunner.RunAsync("wsl.exe", ["--list", "--verbose"], log: null, cancellationToken)
                .ConfigureAwait(false);

            var output = ProcessRunner.NormalizeProcessText(result.StandardOutput);
            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = line.Replace("\0", string.Empty);
                var match = WslVerboseLineRegex().Match(normalized);
                if (match.Success)
                {
                    states[match.Groups["name"].Value.Trim()] = match.Groups["state"].Value.Trim();
                }
            }
        }
        catch
        {
            // Registry discovery remains the source of truth; state is only a UI hint.
        }

        return states;
    }

    private static int ToInt32(object? value)
        => value switch
        {
            int integer => integer,
            long integer => checked((int)integer),
            string text when int.TryParse(text, out var integer) => integer,
            _ => 0
        };

    private static bool IsRunning(string state)
        => string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\s*\*?\s*(?<name>.+?)\s{2,}(?<state>\S+)\s{2,}(?<version>\d+)\s*$")]
    private static partial Regex WslVerboseLineRegex();

    private sealed record LinuxUsageReadResult(LinuxUsageSnapshot? Usage, string State);
}
