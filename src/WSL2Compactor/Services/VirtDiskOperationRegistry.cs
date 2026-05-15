using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WSL2Compactor.Services;

internal sealed class VirtDiskOperationRegistry
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorOperationAborted = 995;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = 0xFFFFFFFF;
    private readonly object _gate = new();
    private ActiveOperation? _activeOperation;

    public IDisposable Register(
        SafeFileHandle handle,
        IntPtr overlappedPtr,
        IntPtr eventHandle,
        string vhdPath,
        string mode)
    {
        var operation = new ActiveOperation(handle, overlappedPtr, eventHandle, vhdPath, mode);
        lock (_gate)
        {
            _activeOperation = operation;
        }

        return new Registration(this, operation);
    }

    public bool RequestCancel(TimeSpan waitTimeout, Action<string> log)
    {
        ActiveOperation? operation;
        lock (_gate)
        {
            operation = _activeOperation;
        }

        if (operation is null)
        {
            log("No active VirtDisk compact operation.");
            return false;
        }

        lock (operation.Gate)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_activeOperation, operation))
                {
                    log("VirtDisk compact operation is no longer active.");
                    return false;
                }
            }

            if (operation.Handle.IsInvalid || operation.Handle.IsClosed || operation.OverlappedPtr == IntPtr.Zero)
            {
                log("Active VirtDisk compact operation is no longer cancellable.");
                return false;
            }

            log($"CancelIoEx requested for VirtDisk {operation.Mode}: {operation.VhdPath}");
            var cancelRequested = CancelIoEx(operation.Handle, operation.OverlappedPtr);
            if (!cancelRequested)
            {
                log($"CancelIoEx failed: {FormatWin32Error((uint)Marshal.GetLastWin32Error())}");
            }

            var waitResult = WaitForSingleObject(operation.EventHandle, (uint)Math.Clamp(waitTimeout.TotalMilliseconds, 0, uint.MaxValue));
            if (waitResult == WaitObject0)
            {
                var completed = GetOverlappedResult(operation.Handle, operation.OverlappedPtr, out _, bWait: false);
                var finalError = completed ? ErrorSuccess : (uint)Marshal.GetLastWin32Error();
                log(finalError switch
                {
                    ErrorSuccess => "VirtDisk compact completed before or during close cancellation.",
                    ErrorOperationAborted => "VirtDisk compact cancellation completed.",
                    _ => $"VirtDisk compact completed with error after close cancellation: {FormatWin32Error(finalError)}"
                });
                return true;
            }

            if (waitResult == WaitTimeout)
            {
                log($"VirtDisk compact did not complete within {waitTimeout.TotalSeconds:0.#} seconds after CancelIoEx.");
                return cancelRequested;
            }

            var waitError = waitResult == WaitFailed ? (uint)Marshal.GetLastWin32Error() : waitResult;
            log($"WaitForSingleObject failed while waiting for VirtDisk cancellation: {FormatWin32Error(waitError)}");
            return cancelRequested;
        }
    }

    private void Unregister(ActiveOperation operation)
    {
        lock (operation.Gate)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeOperation, operation))
                {
                    _activeOperation = null;
                }
            }
        }
    }

    private static string FormatWin32Error(uint errorCode)
        => $"{errorCode} / 0x{errorCode:X8}: {new Win32Exception((int)errorCode).Message}";

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle hFile, IntPtr lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    private sealed record ActiveOperation(
        SafeFileHandle Handle,
        IntPtr OverlappedPtr,
        IntPtr EventHandle,
        string VhdPath,
        string Mode)
    {
        public object Gate { get; } = new();
    }

    private sealed class Registration : IDisposable
    {
        private readonly VirtDiskOperationRegistry _registry;
        private readonly ActiveOperation _operation;
        private int _disposed;

        public Registration(VirtDiskOperationRegistry registry, ActiveOperation operation)
        {
            _registry = registry;
            _operation = operation;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _registry.Unregister(_operation);
            }
        }
    }
}
