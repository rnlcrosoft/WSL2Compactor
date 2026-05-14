namespace WSL2Compactor.Models;

internal sealed record WslDistribution(
    string Name,
    string BasePath,
    int Version,
    string VhdPath,
    string State,
    long DiskUsageBytes,
    long VirtualSizeBytes)
{
    public bool IsWsl2 => Version == 2;
}
