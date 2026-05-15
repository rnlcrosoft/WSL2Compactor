using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WSL2Compactor.Services;

internal sealed class ConsoleModeGuard : IDisposable
{
    private const int StdInputHandle = -10;
    private const uint EnableQuickEditMode = 0x0040;
    private const uint EnableExtendedFlags = 0x0080;
    private readonly IntPtr _inputHandle;
    private readonly RunLogger _log;
    private readonly uint _originalInputMode;
    private readonly bool _restoreInputMode;

    public ConsoleModeGuard(RunLogger log, string scope)
    {
        _log = log;

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _inputHandle = GetStdHandle(StdInputHandle);
        if (_inputHandle == IntPtr.Zero || _inputHandle == new IntPtr(-1))
        {
            return;
        }

        if (!GetConsoleMode(_inputHandle, out _originalInputMode))
        {
            return;
        }

        _restoreInputMode = true;
        var newMode = (_originalInputMode | EnableExtendedFlags) & ~EnableQuickEditMode;
        if (newMode == _originalInputMode)
        {
            _log.Info("console", "Console QuickEdit mode is already disabled.");
            return;
        }

        if (SetConsoleMode(_inputHandle, newMode))
        {
            _log.Info("console", $"Console QuickEdit mode disabled during {scope} to prevent console selection pause.");
        }
        else
        {
            _log.Warning("console", $"Failed to disable QuickEdit mode: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }
    }

    public void Dispose()
    {
        if (!_restoreInputMode)
        {
            return;
        }

        _ = SetConsoleMode(_inputHandle, _originalInputMode);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
