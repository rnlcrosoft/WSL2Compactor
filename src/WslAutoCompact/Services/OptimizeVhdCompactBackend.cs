namespace WslAutoCompact.Services;

internal sealed class OptimizeVhdCompactBackend : ICompactBackend
{
    private readonly ProcessRunner _processRunner;

    public OptimizeVhdCompactBackend(ProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public string Name => "Optimize-VHD";

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
        => _processRunner.CommandExistsAsync(
            "powershell.exe",
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", "Get-Command Optimize-VHD -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name"],
            cancellationToken);

    public async Task CompactAsync(string vhdPath, IProgress<string> log, CancellationToken cancellationToken)
    {
        var escapedPath = vhdPath.Replace("'", "''");
        var command = $"Optimize-VHD -Path '{escapedPath}' -Mode Full";
        var result = await _processRunner.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command],
            log,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Optimize-VHD failed with exit code {result.ExitCode}.");
        }
    }
}
