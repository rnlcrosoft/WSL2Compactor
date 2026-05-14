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

            var sizeBytes = new FileInfo(vhdPath).Length;
            var state = states.TryGetValue(name, out var parsedState) ? parsedState : "Unknown";
            distributions.Add(new WslDistribution(name, basePath, version, vhdPath, state, sizeBytes));
        }

        return distributions.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
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

    [GeneratedRegex(@"^\s*\*?\s*(?<name>.+?)\s{2,}(?<state>\S+)\s{2,}(?<version>\d+)\s*$")]
    private static partial Regex WslVerboseLineRegex();
}
