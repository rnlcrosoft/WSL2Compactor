using WslAutoCompact.Models;

namespace WslAutoCompact.Services;

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
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        using var formatPromptGuard = new FormatPromptGuard(log);

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            row.Status = "Running fstrim";
            log.Report("");
            log.Report($"== {row.Name}: fstrim ==");

            var trimResult = await _processRunner.RunAsync(
                "wsl.exe",
                ["-d", row.Name, "--user", "root", "fstrim", "-av"],
                log,
                cancellationToken).ConfigureAwait(true);

            if (!trimResult.Succeeded)
            {
                log.Report($"Warning: fstrim failed for {row.Name}. Compact will continue.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        log.Report("");
        log.Report("== WSL shutdown ==");
        foreach (var row in rows)
        {
            row.Status = "Stopping WSL";
        }

        var shutdownResult = await _processRunner.RunAsync("wsl.exe", ["--shutdown"], log, cancellationToken)
            .ConfigureAwait(true);

        if (!shutdownResult.Succeeded)
        {
            log.Report("Warning: wsl --shutdown exited with a non-zero code. Checking whether VHDX locks were released.");
        }

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitForVhdUnlockAsync(row.VhdPath, log, cancellationToken).ConfigureAwait(true);
            row.BeforeBytes = new FileInfo(row.VhdPath).Length;
            row.Status = "Running compact";
            row.Backend = backendMode == BackendMode.OptimizeVhd ? _optimizeVhdBackend.Name : _virtDiskBackend.Name;

            log.Report("");
            log.Report($"== {row.Name}: compact ==");
            log.Report($"VHDX: {row.VhdPath}");
            log.Report($"Before: {SizeFormatter.Format(row.BeforeBytes)}");

            try
            {
                if (backendMode == BackendMode.OptimizeVhd)
                {
                    await _optimizeVhdBackend.CompactAsync(row.VhdPath, log, cancellationToken).ConfigureAwait(true);
                }
                else
                {
                    await _virtDiskBackend.CompactAsync(row.VhdPath, log, cancellationToken).ConfigureAwait(true);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Report($"Warning: {row.Backend} failed: {ex.Message}");
                log.Report("Running DiskPart fallback.");
                row.Backend = _diskPartBackend.Name;
                await _diskPartBackend.CompactAsync(row.VhdPath, log, cancellationToken).ConfigureAwait(true);
            }

            row.AfterBytes = new FileInfo(row.VhdPath).Length;
            row.Status = "Done";
            log.Report($"After: {SizeFormatter.Format(row.AfterBytes.Value)}");
            log.Report($"Saved: {row.SavedText}");
        }
    }

    private static async Task WaitForVhdUnlockAsync(string vhdPath, IProgress<string> log, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            try
            {
                using var stream = new FileStream(vhdPath, FileMode.Open, FileAccess.Read, FileShare.None);
                log.Report($"VHDX lock check: OK ({attempt})");
                return;
            }
            catch (IOException ex) when (DateTimeOffset.UtcNow < deadline)
            {
                log.Report($"VHDX lock check: waiting ({attempt}) {ex.Message}");
                await Task.Delay(1000, cancellationToken).ConfigureAwait(true);
            }
            catch (UnauthorizedAccessException ex) when (DateTimeOffset.UtcNow < deadline)
            {
                log.Report($"VHDX lock check: waiting ({attempt}) {ex.Message}");
                await Task.Delay(1000, cancellationToken).ConfigureAwait(true);
            }
        }
    }
}
