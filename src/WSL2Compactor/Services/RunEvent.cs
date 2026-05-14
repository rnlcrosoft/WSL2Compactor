using WSL2Compactor.Models;

namespace WSL2Compactor.Services;

internal sealed record RunEvent(
    DateTimeOffset Timestamp,
    CompactEventLevel Level,
    CompactEventKind Kind,
    string Phase,
    string Message,
    string? Distro = null,
    string? Backend = null,
    double? Percent = null,
    CompactProgressMode ProgressMode = CompactProgressMode.Indeterminate,
    bool IsComplete = false,
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
    public static RunEvent FromUpdate(CompactProgressUpdate update)
        => new(
            DateTimeOffset.Now,
            update.Level,
            update.Kind,
            update.Phase,
            update.Message,
            update.Distro,
            update.Backend,
            update.Percent,
            update.ProgressMode,
            update.IsComplete,
            update.Command,
            update.ExitCode,
            update.BeforeBytes,
            update.AfterBytes,
            update.SavedBytes,
            update.CurrentValue,
            update.CompletionValue,
            update.OperationStatus,
            update.CalculatedPercent);

    public string ToLogLine()
    {
        var parts = new List<string>
        {
            Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"),
            $"level={Level}",
            $"kind={Kind}",
            $"phase={Quote(Phase)}"
        };

        Add(parts, "distro", Distro);
        Add(parts, "backend", Backend);
        Add(parts, "percent", Percent?.ToString("0.###"));
        Add(parts, "progressMode", ProgressMode.ToString());
        Add(parts, "calculatedPercent", CalculatedPercent?.ToString("0.###"));
        Add(parts, "operationStatus", OperationStatus is null ? null : $"0x{OperationStatus:X8}");
        Add(parts, "currentValue", CurrentValue?.ToString());
        Add(parts, "completionValue", CompletionValue?.ToString());
        Add(parts, "command", Command);
        Add(parts, "exitCode", ExitCode?.ToString());
        Add(parts, "beforeDiskUsage", BeforeBytes is null ? null : $"{BeforeBytes} ({SizeFormatter.Format(BeforeBytes.Value)})");
        Add(parts, "afterDiskUsage", AfterBytes is null ? null : $"{AfterBytes} ({SizeFormatter.Format(AfterBytes.Value)})");
        Add(parts, "savedDiskUsage", SavedBytes is null ? null : $"{SavedBytes} ({SizeFormatter.Format(SavedBytes.Value)})");
        Add(parts, "message", Message);

        return string.Join(" ", parts);
    }

    private static void Add(List<string> parts, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{key}={Quote(value)}");
        }
    }

    private static string Quote(string value)
        => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
