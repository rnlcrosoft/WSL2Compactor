namespace WSL2Compactor.Services;

internal static class TestHooks
{
    public const string FailFstrim = "WSL2COMPACTOR_TEST_FAIL_FSTRIM";
    public const string FailVirtDisk = "WSL2COMPACTOR_TEST_FAIL_VIRTDISK";
    public const string FailExt4Probe = "WSL2COMPACTOR_TEST_FAIL_EXT4_PROBE";
    public const string LockedVhd = "WSL2COMPACTOR_TEST_LOCKED_VHD";

    public static bool IsEnabled(string name)
        => string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);

    public static void ReportActiveHooks(IProgress<CompactProgressUpdate> progress)
    {
        foreach (var name in new[] { FailFstrim, FailVirtDisk, FailExt4Probe, LockedVhd })
        {
            if (IsEnabled(name))
            {
                progress.Report(CompactProgressUpdate.Warning("test hook", $"{name}=1 is active."));
            }
        }
    }
}
