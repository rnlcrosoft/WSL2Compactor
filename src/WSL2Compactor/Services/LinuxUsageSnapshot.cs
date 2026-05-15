namespace WSL2Compactor.Services;

internal readonly record struct LinuxUsageSnapshot(long UsedBytes, long? Ext4OverheadBytes, string Source);
