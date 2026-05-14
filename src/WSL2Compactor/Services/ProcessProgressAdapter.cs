namespace WSL2Compactor.Services;

internal sealed class ProcessProgressAdapter : IProgress<string>
{
    private const string CommandPrefix = "> ";
    private const string ExitCodePrefix = "Exit code: ";
    private readonly string? _backend;
    private readonly string? _distro;
    private readonly string _phase;
    private readonly IProgress<CompactProgressUpdate> _progress;

    public ProcessProgressAdapter(
        IProgress<CompactProgressUpdate> progress,
        string phase,
        string? distro = null,
        string? backend = null)
    {
        _progress = progress;
        _phase = phase;
        _distro = distro;
        _backend = backend;
    }

    public void Report(string value)
    {
        var message = string.IsNullOrWhiteSpace(value) ? "(blank output)" : value;

        if (message.StartsWith(CommandPrefix, StringComparison.Ordinal))
        {
            _progress.Report(CompactProgressUpdate.CommandEvent(_phase, message[CommandPrefix.Length..], _distro, _backend));
            return;
        }

        if (message.StartsWith(ExitCodePrefix, StringComparison.Ordinal)
            && int.TryParse(message[ExitCodePrefix.Length..], out var exitCode))
        {
            _progress.Report(CompactProgressUpdate.Exit(_phase, exitCode, _distro, _backend));
            return;
        }

        _progress.Report(CompactProgressUpdate.Output(_phase, message, _distro, _backend));
    }
}
