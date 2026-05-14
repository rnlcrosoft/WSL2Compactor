namespace WslAutoCompact.Services;

internal interface ICompactBackend
{
    string Name { get; }

    Task CompactAsync(string vhdPath, IProgress<string> log, CancellationToken cancellationToken);
}
