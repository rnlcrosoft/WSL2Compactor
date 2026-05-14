namespace WSL2Compactor.Services;

internal enum CompactEventLevel
{
    Info,
    Warning,
    Error,
    Debug
}

internal enum CompactEventKind
{
    Event,
    Prompt,
    Command,
    Output,
    ExitCode,
    Progress,
    Size,
    Complete
}

internal enum CompactProgressMode
{
    Indeterminate,
    PercentKnown,
    PendingNoReliablePercent
}

internal sealed record CompactProgressUpdate(
    string Phase,
    string Message,
    string? Distro = null,
    string? Backend = null,
    double? Percent = null,
    CompactProgressMode ProgressMode = CompactProgressMode.Indeterminate,
    bool IsComplete = false,
    CompactEventLevel Level = CompactEventLevel.Info,
    CompactEventKind Kind = CompactEventKind.Event,
    string? Command = null,
    int? ExitCode = null,
    long? BeforeBytes = null,
    long? AfterBytes = null,
    long? SavedBytes = null,
    ulong? CurrentValue = null,
    ulong? CompletionValue = null,
    uint? OperationStatus = null,
    double? CalculatedPercent = null)
{
    public static CompactProgressUpdate Log(string message)
        => new("Log", message);

    public static CompactProgressUpdate Warning(string phase, string message, string? distro = null, string? backend = null)
        => new(phase, message, distro, backend, Level: CompactEventLevel.Warning);

    public static CompactProgressUpdate Debug(string phase, string message, string? distro = null, string? backend = null)
        => new(phase, message, distro, backend, Level: CompactEventLevel.Debug);

    public static CompactProgressUpdate Prompt(string phase, string message)
        => new(phase, message, Kind: CompactEventKind.Prompt);

    public static CompactProgressUpdate CommandEvent(string phase, string command, string? distro = null, string? backend = null)
        => new(phase, command, distro, backend, Kind: CompactEventKind.Command, Command: command);

    public static CompactProgressUpdate Output(string phase, string message, string? distro = null, string? backend = null)
        => new(phase, message, distro, backend, Kind: CompactEventKind.Output);

    public static CompactProgressUpdate Exit(string phase, int exitCode, string? distro = null, string? backend = null)
        => new(phase, $"Exit code: {exitCode}", distro, backend, Kind: CompactEventKind.ExitCode, ExitCode: exitCode);

    public static CompactProgressUpdate Size(string phase, string message, long? beforeBytes = null, long? afterBytes = null, long? savedBytes = null, string? distro = null, string? backend = null)
        => new(phase, message, distro, backend, Kind: CompactEventKind.Size, BeforeBytes: beforeBytes, AfterBytes: afterBytes, SavedBytes: savedBytes);

    public static CompactProgressUpdate Indeterminate(string phase, string message, string? distro = null, string? backend = null)
        => new(phase, message, distro, backend);

    public static CompactProgressUpdate Progress(string phase, string message, double percent, string? distro = null, string? backend = null)
        => new(phase, message, distro, backend, Math.Clamp(percent, 0, 100), CompactProgressMode.PercentKnown, Kind: CompactEventKind.Progress);

    public static CompactProgressUpdate Progress(
        string phase,
        string message,
        double percent,
        string? distro,
        string? backend,
        ulong currentValue,
        ulong completionValue,
        uint operationStatus,
        double calculatedPercent,
        CompactProgressMode progressMode = CompactProgressMode.PercentKnown)
        => new(
            phase,
            message,
            distro,
            backend,
            Math.Clamp(percent, 0, 100),
            progressMode,
            Kind: CompactEventKind.Progress,
            CurrentValue: currentValue,
            CompletionValue: completionValue,
            OperationStatus: operationStatus,
            CalculatedPercent: calculatedPercent);

    public static CompactProgressUpdate Complete(string phase, string message, string? distro = null, string? backend = null)
        => new(phase, message, distro, backend, 100, CompactProgressMode.PercentKnown, IsComplete: true, Kind: CompactEventKind.Complete);
}
