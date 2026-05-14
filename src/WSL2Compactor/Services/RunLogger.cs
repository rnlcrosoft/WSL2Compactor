namespace WSL2Compactor.Services;

internal sealed class RunLogger
{
    private readonly List<RunEvent> _events = [];
    private readonly object _gate = new();

    public RunLogger(string logFile)
    {
        LogFile = logFile;
    }

    public string LogFile { get; }

    public void Write(RunEvent runEvent)
    {
        lock (_gate)
        {
            _events.Add(runEvent);
            File.AppendAllText(LogFile, runEvent.ToLogLine() + Environment.NewLine);
        }
    }

    public void Write(CompactProgressUpdate update)
        => Write(RunEvent.FromUpdate(update));

    public void Info(string phase, string message, string? distro = null, string? backend = null)
        => Write(new RunEvent(DateTimeOffset.Now, CompactEventLevel.Info, CompactEventKind.Event, phase, message, distro, backend));

    public void Warning(string phase, string message, string? distro = null, string? backend = null)
        => Write(new RunEvent(DateTimeOffset.Now, CompactEventLevel.Warning, CompactEventKind.Event, phase, message, distro, backend));

    public void Error(string phase, string message, string? distro = null, string? backend = null)
        => Write(new RunEvent(DateTimeOffset.Now, CompactEventLevel.Error, CompactEventKind.Event, phase, message, distro, backend));

    public void Prompt(string phase, string message)
        => Write(new RunEvent(DateTimeOffset.Now, CompactEventLevel.Info, CompactEventKind.Prompt, phase, message));

    public IReadOnlyList<RunEvent> SnapshotEvents()
    {
        lock (_gate)
        {
            return _events.ToList();
        }
    }

    public IReadOnlyList<RunEvent> SnapshotEvents(int maxCount)
    {
        lock (_gate)
        {
            var skip = Math.Max(0, _events.Count - maxCount);
            return _events.Skip(skip).ToList();
        }
    }
}
