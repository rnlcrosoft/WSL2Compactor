using System.Runtime.InteropServices;
using System.Text;

namespace WslAutoCompact.Services;

internal sealed class FormatPromptGuard : IDisposable
{
    private const int MaxTextLength = 1024;
    private const uint WmClose = 0x0010;
    private readonly HashSet<IntPtr> _closedWindows = [];
    private readonly object _gate = new();
    private readonly IProgress<string> _log;
    private readonly System.Threading.Timer _timer;

    public FormatPromptGuard(IProgress<string> log)
    {
        _log = log;
        _timer = new System.Threading.Timer(_ => ScanAndClosePrompts(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
        _log.Report("Format prompt guard: enabled");
    }

    public void Dispose()
    {
        _timer.Dispose();
        _log.Report("Format prompt guard: stopped");
    }

    private void ScanAndClosePrompts()
    {
        try
        {
            lock (_gate)
            {
                EnumWindows((window, _) =>
                {
                    if (!IsWindowVisible(window) || _closedWindows.Contains(window))
                    {
                        return true;
                    }

                    var title = GetWindowText(window);
                    var className = GetClassName(window);
                    if (!IsDialogCandidate(title, className))
                    {
                        return true;
                    }

                    var text = CollectWindowText(window);
                    if (!LooksLikeFormatPrompt($"{title}\n{text}"))
                    {
                        return true;
                    }

                    _closedWindows.Add(window);
                    _log.Report($"Format prompt guard: closed a format prompt ({title})");
                    PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
                    return true;
                }, IntPtr.Zero);
            }
        }
        catch (Exception ex)
        {
            _log.Report($"Format prompt guard: monitoring error {ex.Message}");
        }
    }

    private static bool IsDialogCandidate(string title, string className)
    {
        if (className == "#32770")
        {
            return true;
        }

        return title.Contains("Microsoft Windows", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Windows", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeFormatPrompt(string text)
    {
        var hasFormat = text.Contains("format", StringComparison.OrdinalIgnoreCase);
        var hasDisk = text.Contains("disk", StringComparison.OrdinalIgnoreCase)
            || text.Contains("drive", StringComparison.OrdinalIgnoreCase);

        return hasFormat && hasDisk;
    }

    private static string CollectWindowText(IntPtr window)
    {
        var builder = new StringBuilder();
        AppendText(builder, GetWindowText(window));

        EnumChildWindows(window, (child, _) =>
        {
            AppendText(builder, GetWindowText(child));
            return true;
        }, IntPtr.Zero);

        return builder.ToString();
    }

    private static void AppendText(StringBuilder builder, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.Append(text.Trim());
    }

    private static string GetWindowText(IntPtr window)
    {
        var length = Math.Min(GetWindowTextLength(window), MaxTextLength);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(window, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetClassName(IntPtr window)
    {
        var builder = new StringBuilder(256);
        _ = GetClassName(window, builder, builder.Capacity);
        return builder.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc enumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
