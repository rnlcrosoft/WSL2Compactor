namespace WSL2Compactor.Services;

internal interface ICompactBackend
{
    string Name { get; }

    Task CompactAsync(string vhdPath, IProgress<CompactProgressUpdate> progress, CancellationToken cancellationToken);
}
