namespace WSL2Compactor.Services;

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

    public async Task CompactAsync(string vhdPath, IProgress<CompactProgressUpdate> progress, CancellationToken cancellationToken)
    {
        var escapedPath = vhdPath.Replace("'", "''");
        var command = $"Optimize-VHD -Path '{escapedPath}' -Mode Full";
        progress.Report(CompactProgressUpdate.Indeterminate("Optimize-VHD", "running", backend: Name));
        var result = await _processRunner.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command],
            new StringProgress(progress),
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Optimize-VHD failed with exit code {result.ExitCode}.");
        }

        progress.Report(CompactProgressUpdate.Complete("Optimize-VHD", "completed", backend: Name));
    }

    private sealed class StringProgress : IProgress<string>
    {
        private readonly IProgress<CompactProgressUpdate> _progress;

        public StringProgress(IProgress<CompactProgressUpdate> progress)
        {
            _progress = progress;
        }

        public void Report(string value)
            => _progress.Report(CompactProgressUpdate.Indeterminate("Optimize-VHD", value, backend: "Optimize-VHD"));
    }
}
