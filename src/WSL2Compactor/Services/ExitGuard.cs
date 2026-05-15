using System.Runtime.InteropServices;

namespace WSL2Compactor.Services;

internal sealed class ExitGuard : IDisposable
{
    private const uint CtrlCEvent = 0;
    private const uint CtrlBreakEvent = 1;
    private const uint CtrlCloseEvent = 2;
    private const uint CtrlLogoffEvent = 5;
    private const uint CtrlShutdownEvent = 6;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConsoleCtrlHandler _consoleCtrlHandler;
    private readonly RunLogger _log;
    private readonly ProcessRunner _processRunner;
    private readonly VirtDiskOperationRegistry _virtDiskOperations;
    private int _interruptActive;
    private volatile bool _protected;

    public ExitGuard(RunLogger log, ProcessRunner processRunner, VirtDiskOperationRegistry virtDiskOperations)
    {
        _log = log;
        _processRunner = processRunner;
        _virtDiskOperations = virtDiskOperations;
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
        args.Cancel = true;
    }

    private bool HandleConsoleControl(uint controlType)
    {
        if (controlType is CtrlCEvent or CtrlBreakEvent)
        {
            return true;
        }

        if (!_protected)
        {
            return false;
        }

        return controlType switch
        {
            CtrlCloseEvent or CtrlLogoffEvent or CtrlShutdownEvent => HandleCloseSignal(GetSignalName(controlType)),
            _ => false
        };
    }

    private bool HandleCloseSignal(string source)
    {
        if (Interlocked.Exchange(ref _interruptActive, 1) == 1)
        {
            _log.Warning("exit guard", $"Ignored repeated close signal while close handling is already in progress. Source: {source}");
            return true;
        }

        var message = $"{source} received. Requesting VirtDisk cancellation when possible.";
        _log.Warning("exit guard", message);

        var cancelRequested = _virtDiskOperations.RequestCancel(TimeSpan.FromSeconds(4), message => _log.Warning("VirtDisk cancel", message));
        var stopped = _processRunner.KillActiveProcesses(source, message => _log.Warning("process", message));
        _log.Warning("exit guard", $"Close handling completed. VirtDisk cancel requested: {cancelRequested}. Active child processes stopped: {stopped}.");
        return true;
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
}
