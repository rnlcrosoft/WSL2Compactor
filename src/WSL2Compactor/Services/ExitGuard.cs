using System.Runtime.InteropServices;

namespace WSL2Compactor.Services;

internal sealed class ExitGuard : IDisposable
{
    private const uint CtrlCEvent = 0;
    private const uint CtrlBreakEvent = 1;
    private const uint CtrlCloseEvent = 2;
    private const uint CtrlLogoffEvent = 5;
    private const uint CtrlShutdownEvent = 6;
    private const uint MbYesNo = 0x00000004;
    private const uint MbIconWarning = 0x00000030;
    private const uint MbDefaultButton2 = 0x00000100;
    private const uint MbSystemModal = 0x00001000;
    private const int IdYes = 6;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConsoleCtrlHandler _consoleCtrlHandler;
    private readonly RunLogger _log;
    private int _confirmationActive;
    private volatile bool _protected;

    public ExitGuard(RunLogger log)
    {
        _log = log;
        _consoleCtrlHandler = HandleConsoleControl;
        Console.CancelKeyPress += HandleCancelKeyPress;

        if (OperatingSystem.IsWindows())
        {
            _ = SetConsoleCtrlHandler(_consoleCtrlHandler, add: true);
        }
    }

    private delegate bool ConsoleCtrlHandler(uint controlType);

    public CancellationToken Token => _cancellation.Token;

    public void SetProtected(bool value)
    {
        if (_protected == value)
        {
            return;
        }

        _protected = value;
        _log.Info("exit guard", value ? "Exit guard protected mode enabled." : "Exit guard protected mode disabled.");
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= HandleCancelKeyPress;
        if (OperatingSystem.IsWindows())
        {
            _ = SetConsoleCtrlHandler(_consoleCtrlHandler, add: false);
        }

        _cancellation.Dispose();
    }

    private void HandleCancelKeyPress(object? sender, ConsoleCancelEventArgs args)
    {
        if (!_protected)
        {
            return;
        }

        args.Cancel = true;
        _ = ConfirmStop("Ctrl+C");
    }

    private bool HandleConsoleControl(uint controlType)
    {
        if (!_protected)
        {
            return false;
        }

        return controlType switch
        {
            CtrlCEvent or CtrlBreakEvent => HandleProtectedSignal(GetSignalName(controlType), allowDefaultTerminationOnYes: false),
            CtrlCloseEvent or CtrlLogoffEvent or CtrlShutdownEvent => HandleProtectedSignal(GetSignalName(controlType), allowDefaultTerminationOnYes: true),
            _ => false
        };
    }

    private bool HandleProtectedSignal(string signalName, bool allowDefaultTerminationOnYes)
    {
        var stop = ConfirmStop(signalName);
        return stop ? !allowDefaultTerminationOnYes : true;
    }

    private bool ConfirmStop(string source)
    {
        if (Interlocked.Exchange(ref _confirmationActive, 1) == 1)
        {
            _log.Warning("exit guard", $"Ignored repeated interrupt while confirmation is already open. Source: {source}");
            return false;
        }

        try
        {
            _log.Warning("exit guard", $"Interrupt requested. Source: {source}");
            var stop = false;

            if (OperatingSystem.IsWindows())
            {
                var result = MessageBoxW(
                    IntPtr.Zero,
                    "Stop compaction and close?",
                    "WSL2Compactor",
                    MbYesNo | MbIconWarning | MbDefaultButton2 | MbSystemModal);
                stop = result == IdYes;
            }

            _log.Prompt("exit guard", stop ? $"Interrupt confirmed. Source: {source}" : $"Interrupt declined. Source: {source}");
            if (stop)
            {
                _cancellation.Cancel();
            }

            return stop;
        }
        finally
        {
            Interlocked.Exchange(ref _confirmationActive, 0);
        }
    }

    private static string GetSignalName(uint controlType)
        => controlType switch
        {
            CtrlCEvent => "Ctrl+C",
            CtrlBreakEvent => "Ctrl+Break",
            CtrlCloseEvent => "console close",
            CtrlLogoffEvent => "logoff",
            CtrlShutdownEvent => "shutdown",
            _ => $"control signal {controlType}"
        };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, bool add);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
