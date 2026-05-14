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
        using var formatPromptGuard = new FormatPromptGuard(new ProcessProgressAdapter(progress, "format guard"));
        TestHooks.ReportActiveHooks(progress);

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            row.Status = "Running fstrim";
            progress.Report(CompactProgressUpdate.Indeterminate("fstrim", "running", row.Name));

            var trimResult = TestHooks.IsEnabled(TestHooks.FailFstrim)
                ? new ProcessResult(1, string.Empty, "Injected fstrim failure.")
                : await _processRunner.RunAsync(
                    "wsl.exe",
                    ["-d", row.Name, "--user", "root", "fstrim", "-av"],
                    new ProcessProgressAdapter(progress, "fstrim", row.Name),
                    cancellationToken).ConfigureAwait(true);

            if (!trimResult.Succeeded)
            {
                progress.Report(CompactProgressUpdate.Warning("fstrim", $"fstrim failed for {row.Name}. Compact will continue.", row.Name));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(CompactProgressUpdate.Indeterminate("shutdown", "running wsl --shutdown"));
        foreach (var row in rows)
        {
            row.Status = "Stopping WSL";
        }

        var shutdownResult = await _processRunner.RunAsync("wsl.exe", ["--shutdown"], new ProcessProgressAdapter(progress, "shutdown"), cancellationToken)
            .ConfigureAwait(true);

        if (!shutdownResult.Succeeded)
        {
            progress.Report(CompactProgressUpdate.Warning("shutdown", "wsl --shutdown exited with a non-zero code. Checking whether VHDX locks were released."));
        }

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitForVhdUnlockAsync(row, progress, cancellationToken).ConfigureAwait(true);
            row.BeforeBytes = new FileInfo(row.VhdPath).Length;
            row.Status = "Running compact";
            row.Backend = backendMode == BackendMode.OptimizeVhd ? _optimizeVhdBackend.Name : _virtDiskBackend.Name;

            progress.Report(CompactProgressUpdate.Size("compact", $"Before: {SizeFormatter.Format(row.BeforeBytes)}", beforeBytes: row.BeforeBytes, distro: row.Name, backend: row.Backend));

            try
            {
                if (backendMode == BackendMode.OptimizeVhd)
                {
                    await _optimizeVhdBackend.CompactAsync(row.VhdPath, new DistroProgress(progress, row.Name, _optimizeVhdBackend.Name), cancellationToken).ConfigureAwait(true);
                }
                else
                {
                    if (TestHooks.IsEnabled(TestHooks.FailVirtDisk))
                    {
                        throw new CompactFailureException(
                            CompactFailureKind.Backend,
                            "VirtDisk",
                            "Injected VirtDisk failure.",
                            row.Name,
                            _virtDiskBackend.Name,
                            row.VhdPath,
                            fallbackAllowed: true);
                    }

                    await _virtDiskBackend.CompactAsync(row.VhdPath, new DistroProgress(progress, row.Name, _virtDiskBackend.Name), cancellationToken).ConfigureAwait(true);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failure = ToCompactFailure(ex, row, phase: "compact", fallbackAllowed: true);
                if (!failure.FallbackAllowed)
                {
                    row.Status = "Failed";
                    progress.Report(CompactProgressUpdate.Warning("compact", $"{row.Backend} failed without fallback: {failure.Message}", row.Name, row.Backend));
                    throw failure;
                }

                progress.Report(CompactProgressUpdate.Warning("fallback", $"{row.Backend} failed: {failure.Message}", row.Name, row.Backend));
                progress.Report(CompactProgressUpdate.Indeterminate("fallback", "Running DiskPart fallback.", row.Name, _diskPartBackend.Name));
                row.Backend = _diskPartBackend.Name;
                try
                {
                    await _diskPartBackend.CompactAsync(row.VhdPath, new DistroProgress(progress, row.Name, _diskPartBackend.Name), cancellationToken).ConfigureAwait(true);
                }
                catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
                {
                    row.Status = "Failed";
                    throw ToCompactFailure(fallbackEx, row, phase: "DiskPart", fallbackAllowed: false);
                }
            }

            row.AfterBytes = new FileInfo(row.VhdPath).Length;
            row.Status = "Done";
            var savedBytes = Math.Max(0, row.BeforeBytes - row.AfterBytes.Value);
            progress.Report(CompactProgressUpdate.Size(
                "complete",
                $"After: {SizeFormatter.Format(row.AfterBytes.Value)}; saved: {row.SavedText}",
                beforeBytes: row.BeforeBytes,
                afterBytes: row.AfterBytes.Value,
                savedBytes: savedBytes,
                distro: row.Name,
                backend: row.Backend));
            progress.Report(CompactProgressUpdate.Complete("complete", $"Finished {row.Name}.", row.Name, row.Backend));
        }
    }

    private static async Task WaitForVhdUnlockAsync(DistributionRow row, IProgress<CompactProgressUpdate> progress, CancellationToken cancellationToken)
    {
        if (TestHooks.IsEnabled(TestHooks.LockedVhd))
        {
            progress.Report(CompactProgressUpdate.Warning("lock check", "Injected locked VHDX failure.", row.Name));
            throw new CompactFailureException(
                CompactFailureKind.Locked,
                "lock check",
                "Injected locked VHDX failure. Another process may still be holding the VHDX.",
                row.Name,
                vhdPath: row.VhdPath,
                fallbackAllowed: false);
        }

        if (!File.Exists(row.VhdPath))
        {
            throw new CompactFailureException(
                CompactFailureKind.Missing,
                "lock check",
                $"VHDX file was not found: {row.VhdPath}",
                row.Name,
                vhdPath: row.VhdPath,
                fallbackAllowed: false);
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        var attempt = 0;
        Exception? lastException = null;

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
                lastException = ex;
                progress.Report(CompactProgressUpdate.Indeterminate("lock check", $"waiting ({attempt}) {ex.Message}", row.Name));
                await Task.Delay(1000, cancellationToken).ConfigureAwait(true);
            }
            catch (UnauthorizedAccessException ex) when (DateTimeOffset.UtcNow < deadline)
            {
                lastException = ex;
                progress.Report(CompactProgressUpdate.Indeterminate("lock check", $"waiting ({attempt}) {ex.Message}", row.Name));
                await Task.Delay(1000, cancellationToken).ConfigureAwait(true);
            }
            catch (IOException ex)
            {
                lastException = ex;
                throw CreateLockedFailure(row, lastException);
            }
            catch (UnauthorizedAccessException ex)
            {
                lastException = ex;
                throw new CompactFailureException(
                    CompactFailureKind.AccessDenied,
                    "lock check",
                    $"VHDX could not be opened after WSL shutdown: {ex.Message}",
                    row.Name,
                    vhdPath: row.VhdPath,
                    fallbackAllowed: false,
                    innerException: ex);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw CreateLockedFailure(row, lastException);
            }
        }
    }

    private static CompactFailureException CreateLockedFailure(DistributionRow row, Exception? innerException)
        => new(
            CompactFailureKind.Locked,
            "lock check",
            "VHDX could not be opened after WSL shutdown. Another process may still be holding the VHDX.",
            row.Name,
            vhdPath: row.VhdPath,
            fallbackAllowed: false,
            innerException: innerException);

    private static CompactFailureException ToCompactFailure(Exception exception, DistributionRow row, string phase, bool fallbackAllowed)
    {
        if (exception is CompactFailureException failure)
        {
            return new CompactFailureException(
                failure.Kind,
                failure.Phase,
                failure.Message,
                failure.Distro ?? row.Name,
                failure.Backend ?? (string.IsNullOrWhiteSpace(row.Backend) ? null : row.Backend),
                failure.VhdPath ?? row.VhdPath,
                failure.ExitCode,
                failure.Win32ErrorCode,
                failure.FallbackAllowed,
                failure);
        }

        return new CompactFailureException(
                CompactFailureKind.Unknown,
                phase,
                exception.Message,
                row.Name,
                row.Backend,
                row.VhdPath,
                fallbackAllowed: fallbackAllowed,
                innerException: exception);
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
