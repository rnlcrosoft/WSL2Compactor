namespace WSL2Compactor.Services;

internal sealed record CompactProgressUpdate(
    string Phase,
    string Message,
    string? Distro = null,
    string? Backend = null,
    double? Percent = null,
    bool IsComplete = false)
{
    public static CompactProgressUpdate Log(string message)
        => new("Log", message);

    public static CompactProgressUpdate Indeterminate(string phase, string message, string? distro = null, string? backend = null)
        => new(phase, message, distro, backend);

    public static CompactProgressUpdate Progress(string phase, string message, double percent, string? distro = null, string? backend = null)
        => new(phase, message, distro, backend, Math.Clamp(percent, 0, 100));

    public static CompactProgressUpdate Complete(string phase, string message, string? distro = null, string? backend = null)
        => new(phase, message, distro, backend, 100, IsComplete: true);
}
