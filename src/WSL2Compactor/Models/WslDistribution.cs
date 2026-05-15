namespace WSL2Compactor.Models;

internal sealed record WslDistribution(
    string Name,
    string BasePath,
    int Version,
    string VhdPath,
    string State,
    long HostAllocatedBytes,
    long VhdxFileSizeBytes,
    long? LinuxUsedBytes,
    long? Ext4OverheadBytes,
    string? LinuxUsageSource)
{
    public bool IsWsl2 => Version == 2;

    public long? LinuxFootprintBytes
        => LinuxUsedBytes is null || Ext4OverheadBytes is null
            ? null
            : LinuxUsedBytes.Value + Ext4OverheadBytes.Value;
}
