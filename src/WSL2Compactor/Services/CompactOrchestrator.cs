using WSL2Compactor.Models;

namespace WSL2Compactor.Services;

internal enum BackendMode
{
    VirtDisk,
    OptimizeVhd
}

internal sealed class CompactOrchestrator
{
    private readonly ProcessRunner _processRunner;
    private readonly VirtDiskCompactBackend _virtDiskBackend;
    private readonly DiskPartCompactBackend _diskPartBackend;
    private readonly OptimizeVhdCompactBackend _optimizeVhdBackend;

    public CompactOrchestrator(
        ProcessRunner processRunner,
        VirtDiskCompactBackend virtDiskBackend,
        DiskPartCompactBackend diskPartBackend,
        OptimizeVhdCompactBackend optimizeVhdBackend)
    {
        _processRunner = processRunner;
        _virtDiskBackend = virtDiskBackend;
        _diskPartBackend = diskPartBackend;
        _optimizeVhdBackend = optimizeVhdBackend;
    }

    public async Task RunAsync(
        IReadOnlyList<DistributionRow> rows,
        BackendMode backendMode,
        IProgress<CompactProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        using var formatPromptGuard = new FormatPromptGuard(new StringProgress(progress));

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            row.Status = "Running fstrim";
            progress.Report(CompactProgressUpdate.Indeterminate("fstrim", "running", row.Name));

            var trimResult = await _processRunner.RunAsync(
                "wsl.exe",
                ["-d", row.Name, "--user", "root", "fstrim", "-av"],
                new StringProgress(progress, row.Name, "fstrim"),
                cancellationToken).ConfigureAwait(true);

            if (!trimResult.Succeeded)
            {
                progress.Report(CompactProgressUpdate.Indeterminate("fstrim", $"Warning: fstrim failed for {row.Name}. Compact will continue.", row.Name));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(CompactProgressUpdate.Indeterminate("shutdown", "running wsl --shutdown"));
        foreach (var row in rows)
        {
            row.Status = "Stopping WSL";
        }

        var shutdownResult = await _processRunner.RunAsync("wsl.exe", ["--shutdown"], new StringProgress(progress, phase: "shutdown"), cancellationToken)
            .ConfigureAwait(true);

        if (!shutdownResult.Succeeded)
        {
            progress.Report(CompactProgressUpdate.Indeterminate("shutdown", "Warning: wsl --shutdown exited with a non-zero code. Checking whether VHDX locks were released."));
        }

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitForVhdUnlockAsync(row, progress, cancellationToken).ConfigureAwait(true);
            row.BeforeBytes = new FileInfo(row.VhdPath).Length;
            row.Status = "Running compact";
            row.Backend = backendMode == BackendMode.OptimizeVhd ? _optimizeVhdBackend.Name : _virtDiskBackend.Name;

            progress.Report(CompactProgressUpdate.Indeterminate("compact", $"Before: {SizeFormatter.Format(row.BeforeBytes)}", row.Name, row.Backend));

            try
            {
                if (backendMode == BackendMode.OptimizeVhd)
                {
                    await _optimizeVhdBackend.CompactAsync(row.VhdPath, new DistroProgress(progress, row.Name, _optimizeVhdBackend.Name), cancellationToken).ConfigureAwait(true);
                }
                else
                {
                    await _virtDiskBackend.CompactAsync(row.VhdPath, new DistroProgress(progress, row.Name, _virtDiskBackend.Name), cancellationToken).ConfigureAwait(true);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                progress.Report(CompactProgressUpdate.Indeterminate("fallback", $"Warning: {row.Backend} failed: {ex.Message}", row.Name, row.Backend));
                progress.Report(CompactProgressUpdate.Indeterminate("fallback", "Running DiskPart fallback.", row.Name, _diskPartBackend.Name));
                row.Backend = _diskPartBackend.Name;
                await _diskPartBackend.CompactAsync(row.VhdPath, new DistroProgress(progress, row.Name, _diskPartBackend.Name), cancellationToken).ConfigureAwait(true);
            }

            row.AfterBytes = new FileInfo(row.VhdPath).Length;
            row.Status = "Done";
            progress.Report(CompactProgressUpdate.Complete("complete", $"After: {SizeFormatter.Format(row.AfterBytes.Value)}; saved: {row.SavedText}", row.Name, row.Backend));
        }
    }

    private static async Task WaitForVhdUnlockAsync(DistributionRow row, IProgress<CompactProgressUpdate> progress, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            try
            {
                using var stream = new FileStream(row.VhdPath, FileMode.Open, FileAccess.Read, FileShare.None);
                progress.Report(CompactProgressUpdate.Indeterminate("lock check", $"OK ({attempt})", row.Name));
                return;
            }
            catch (IOException ex) when (DateTimeOffset.UtcNow < deadline)
            {
                progress.Report(CompactProgressUpdate.Indeterminate("lock check", $"waiting ({attempt}) {ex.Message}", row.Name));
                await Task.Delay(1000, cancellationToken).ConfigureAwait(true);
            }
            catch (UnauthorizedAccessException ex) when (DateTimeOffset.UtcNow < deadline)
            {
                progress.Report(CompactProgressUpdate.Indeterminate("lock check", $"waiting ({attempt}) {ex.Message}", row.Name));
                await Task.Delay(1000, cancellationToken).ConfigureAwait(true);
            }
        }
    }

    private sealed class StringProgress : IProgress<string>
    {
        private readonly IProgress<CompactProgressUpdate> _progress;
        private readonly string? _distro;
        private readonly string _phase;

        public StringProgress(IProgress<CompactProgressUpdate> progress, string? distro = null, string phase = "process")
        {
            _progress = progress;
            _distro = distro;
            _phase = phase;
        }

        public void Report(string value)
            => _progress.Report(CompactProgressUpdate.Indeterminate(_phase, value, _distro));
    }

    private sealed class DistroProgress : IProgress<CompactProgressUpdate>
    {
        private readonly IProgress<CompactProgressUpdate> _progress;
        private readonly string _distro;
        private readonly string _backend;

        public DistroProgress(IProgress<CompactProgressUpdate> progress, string distro, string backend)
        {
            _progress = progress;
            _distro = distro;
            _backend = backend;
        }

        public void Report(CompactProgressUpdate value)
            => _progress.Report(value with
            {
                Distro = value.Distro ?? _distro,
                Backend = value.Backend ?? _backend
            });
    }
}
