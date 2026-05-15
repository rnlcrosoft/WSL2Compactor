using System.Globalization;
using WSL2Compactor.Models;

namespace WSL2Compactor.Services;

internal enum CompactMode
{
    NoZeroScan,
    ZeroScan
}

internal sealed class CompactOrchestrator
{
    private readonly ProcessRunner _processRunner;
    private readonly VirtDiskCompactBackend _virtDiskBackend;

    public CompactOrchestrator(
        ProcessRunner processRunner,
        VirtDiskCompactBackend virtDiskBackend)
    {
        _processRunner = processRunner;
        _virtDiskBackend = virtDiskBackend;
    }

    public async Task RunAsync(
        IReadOnlyList<DistributionRow> rows,
        CompactMode compactMode,
        IProgress<CompactProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        using var formatPromptGuard = new FormatPromptGuard(new ProcessProgressAdapter(progress, "format guard"));
        TestHooks.ReportActiveHooks(progress);

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.BeforeLinuxUsedBytes is null)
            {
                row.Status = "Reading Linux usage";
                progress.Report(CompactProgressUpdate.Indeterminate("df", "reading Linux filesystem usage", row.Name));
                var linuxUsage = await ReadLinuxUsedBytesAsync(row, progress, cancellationToken).ConfigureAwait(true);
                row.BeforeLinuxUsedBytes = linuxUsage?.UsedBytes;
                row.BeforeExt4OverheadBytes = linuxUsage?.Ext4OverheadBytes;
                row.LinuxUsageSource = linuxUsage?.Source;
            }

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
                throw CreateProcessFailure(
                    "fstrim",
                    $"fstrim failed for {row.Name}.",
                    trimResult,
                    row.Name,
                    row.VhdPath);
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
            throw CreateProcessFailure(
                "shutdown",
                "wsl --shutdown failed.",
                shutdownResult,
                distro: null,
                vhdPath: null);
        }

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitForVhdUnlockAsync(row, progress, cancellationToken).ConfigureAwait(true);
            var beforeSize = VhdxSizeProbe.Read(row.VhdPath);
            row.BeforeHostAllocatedBytes = beforeSize.HostAllocatedBytes;
            row.BeforeVhdxFileSizeBytes = beforeSize.FileSizeBytes;
            row.AfterHostAllocatedBytes = null;
            row.AfterVhdxFileSizeBytes = null;
            row.Status = "Running compact";
            row.Backend = GetBackendName(compactMode);

            progress.Report(CompactProgressUpdate.Size(
                "compact",
                $"Host allocated before: {row.BeforeHostAllocatedText}; VHDX file size: {row.BeforeVhdxFileSizeText}",
                beforeBytes: row.BeforeHostAllocatedBytes,
                distro: row.Name,
                backend: row.Backend));

            await RunBackendAsync(row, compactMode, progress, cancellationToken).ConfigureAwait(true);

            var afterSize = VhdxSizeProbe.Read(row.VhdPath);
            row.AfterHostAllocatedBytes = afterSize.HostAllocatedBytes;
            row.AfterVhdxFileSizeBytes = afterSize.FileSizeBytes;
            row.Status = "Done";
            var savedBytes = Math.Max(0, row.BeforeHostAllocatedBytes - row.AfterHostAllocatedBytes.Value);
            progress.Report(CompactProgressUpdate.Size(
                "complete",
                $"Host allocated after: {row.AfterHostAllocatedText}; actual host saved: {row.SavedText}; VHDX file size: {row.AfterVhdxFileSizeText}",
                beforeBytes: row.BeforeHostAllocatedBytes,
                afterBytes: row.AfterHostAllocatedBytes.Value,
                savedBytes: savedBytes,
                distro: row.Name,
                backend: row.Backend));
            progress.Report(CompactProgressUpdate.Complete("complete", $"Finished {row.Name}.", row.Name, row.Backend));
        }
    }

    internal static string GetBackendName(CompactMode compactMode)
        => compactMode == CompactMode.ZeroScan ? "VirtDisk API zero scan" : "VirtDisk API no zero scan";

    private async Task RunBackendAsync(
        DistributionRow row,
        CompactMode compactMode,
        IProgress<CompactProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        row.Backend = GetBackendName(compactMode);

        try
        {
            if (TestHooks.IsEnabled(TestHooks.FailVirtDisk))
            {
                throw new CompactFailureException(
                    CompactFailureKind.Backend,
                    "VirtDisk",
                    "Injected VirtDisk failure.",
                    row.Name,
                    row.Backend,
                    row.VhdPath);
            }

            await _virtDiskBackend.CompactAsync(
                row.VhdPath,
                quickMode: compactMode == CompactMode.NoZeroScan,
                new DistroProgress(progress, row.Name, _virtDiskBackend.Name),
                cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failure = ToCompactFailure(ex, row, phase: "compact");
            row.Status = "Failed";
            progress.Report(CompactProgressUpdate.Warning("compact", $"{row.Backend} failed: {failure.Message}", row.Name, row.Backend));
            throw failure;
        }
    }

    private async Task<LinuxUsageSnapshot?> ReadLinuxUsedBytesAsync(
        DistributionRow row,
        IProgress<CompactProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync(
                "wsl.exe",
                ["-d", row.Name, "--user", "root", "df", "-B1", "--output=used", "/"],
                new ProcessProgressAdapter(progress, "df", row.Name),
                cancellationToken).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                if (IsSharingViolation(result))
                {
                    throw CreateLockedFailure(row, innerException: null);
                }

                progress.Report(CompactProgressUpdate.Warning("df", "Linux usage unavailable. Compact will continue.", row.Name));
                return null;
            }

            var output = ProcessRunner.NormalizeProcessText(result.StandardOutput);
            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Reverse())
            {
                if (long.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var usedBytes))
                {
                    progress.Report(CompactProgressUpdate.Size(
                        "df",
                        $"Linux used before: {SizeFormatter.Format(usedBytes)}",
                        distro: row.Name));
                    return new LinuxUsageSnapshot(usedBytes, null, "df");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not CompactFailureException)
        {
            progress.Report(CompactProgressUpdate.Warning("df", $"Linux usage unavailable: {ex.Message}. Compact will continue.", row.Name));
        }

        return null;
    }

    private static Task WaitForVhdUnlockAsync(DistributionRow row, IProgress<CompactProgressUpdate> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (TestHooks.IsEnabled(TestHooks.LockedVhd))
        {
            progress.Report(CompactProgressUpdate.Warning("lock check", "Injected locked VHDX failure.", row.Name));
            throw CreateLockedFailure(row, innerException: null);
        }

        if (!File.Exists(row.VhdPath))
        {
            throw new CompactFailureException(
                CompactFailureKind.Missing,
                "lock check",
                $"VHDX file was not found: {row.VhdPath}",
                row.Name,
                vhdPath: row.VhdPath);
        }

        try
        {
            using var stream = new FileStream(row.VhdPath, FileMode.Open, FileAccess.Read, FileShare.None);
            progress.Report(CompactProgressUpdate.Indeterminate("lock check", "OK", row.Name));
            return Task.CompletedTask;
        }
        catch (IOException ex)
        {
            throw CreateLockedFailure(row, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new CompactFailureException(
                CompactFailureKind.AccessDenied,
                "lock check",
                $"VHDX could not be opened after WSL shutdown: {ex.Message}",
                row.Name,
                vhdPath: row.VhdPath,
                innerException: ex);
        }
    }

    private static CompactFailureException CreateLockedFailure(DistributionRow row, Exception? innerException)
        => new(
            CompactFailureKind.Locked,
            "lock check",
            "VHDX is in use by Windows/WSL or another process. Run wsl --shutdown and try again. If PID 4/System still holds the file, reboot may be required.",
            row.Name,
            vhdPath: row.VhdPath,
            innerException: innerException);

    private static CompactFailureException CreateProcessFailure(
        string phase,
        string message,
        ProcessResult result,
        string? distro,
        string? vhdPath)
    {
        if (IsSharingViolation(result))
        {
            return new CompactFailureException(
                CompactFailureKind.Locked,
                phase,
                "VHDX is in use by Windows/WSL or another process. Run wsl --shutdown and try again. If PID 4/System still holds the file, reboot may be required.",
                distro,
                vhdPath: vhdPath,
                exitCode: result.ExitCode);
        }

        var details = ExtractProcessDetails(result);
        return new CompactFailureException(
            CompactFailureKind.CommandFailed,
            phase,
            string.IsNullOrWhiteSpace(details)
                ? $"{message} Exit code: {result.ExitCode}."
                : $"{message} Exit code: {result.ExitCode}. {details}",
            distro,
            vhdPath: vhdPath,
            exitCode: result.ExitCode);
    }

    private static bool IsSharingViolation(ProcessResult result)
        => ContainsSharingViolation(result.StandardOutput) || ContainsSharingViolation(result.StandardError);

    private static bool ContainsSharingViolation(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("ERROR_SHARING_VIOLATION", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Wsl/Service/CreateInstance/MountDisk/HCS", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Failed to attach disk", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("別のプロセスが使用中です", StringComparison.Ordinal) ||
            text.Contains("プロセスはファイルにアクセスできません", StringComparison.Ordinal);
    }

    private static string ExtractProcessDetails(ProcessResult result)
    {
        var output = ProcessRunner.NormalizeProcessText(result.StandardOutput);
        var error = ProcessRunner.NormalizeProcessText(result.StandardError);
        return string.Join(
            " ",
            new[] { output, error }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Length > 500 ? value[..500] + "..." : value));
    }

    private static CompactFailureException ToCompactFailure(Exception exception, DistributionRow row, string phase)
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
                failure);
        }

        return new CompactFailureException(
                CompactFailureKind.Unknown,
                phase,
                exception.Message,
                row.Name,
                row.Backend,
                row.VhdPath,
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
