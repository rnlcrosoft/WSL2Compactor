using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WSL2Compactor.Services;

internal sealed class VirtDiskCompactBackend
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorIoPending = 997;
    private const uint ErrorOperationAborted = 995;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const string BackendName = "VirtDisk API";
    private static readonly Guid MicrosoftVirtualStorageVendorId = new("EC984AEC-A0F9-47E9-901F-71415A66345B");
    private readonly VirtDiskOperationRegistry _operationRegistry;

    public VirtDiskCompactBackend(VirtDiskOperationRegistry operationRegistry)
    {
        _operationRegistry = operationRegistry;
    }

    public string Name => BackendName;

    public async Task CompactAsync(string vhdPath, bool quickMode, IProgress<CompactProgressUpdate> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(CompactProgressUpdate.Progress("VirtDisk", "OpenVirtualDisk", 0, backend: Name));

        using var handle = Open(vhdPath);
        var attached = false;

        try
        {
            var attachResult = AttachVirtualDisk(
                handle,
                IntPtr.Zero,
                AttachVirtualDiskFlag.ReadOnly | AttachVirtualDiskFlag.NoDriveLetter,
                0,
                IntPtr.Zero,
                IntPtr.Zero);

            if (attachResult == ErrorSuccess)
            {
                attached = true;
                progress.Report(CompactProgressUpdate.Progress("VirtDisk", "read-only attach completed", 5, backend: Name));
            }
            else
            {
                progress.Report(CompactProgressUpdate.Progress("VirtDisk", $"skipping read-only attach ({FormatWin32Error(attachResult)})", 5, backend: Name));
            }

            cancellationToken.ThrowIfCancellationRequested();
            await CompactWithProgressAsync(handle, vhdPath, quickMode, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (attached)
            {
                var detachResult = DetachVirtualDisk(handle, DetachVirtualDiskFlag.None, 0);
                progress.Report(detachResult == ErrorSuccess
                    ? CompactProgressUpdate.Indeterminate("VirtDisk", "detach completed", backend: Name)
                    : CompactProgressUpdate.Warning("VirtDisk", $"detach failed ({FormatWin32Error(detachResult)})", backend: Name));
            }
        }
    }

    private async Task CompactWithProgressAsync(
        SafeFileHandle handle,
        string vhdPath,
        bool quickMode,
        IProgress<CompactProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        var eventHandle = CreateEvent(IntPtr.Zero, bManualReset: true, bInitialState: false, lpName: null);
        if (eventHandle == IntPtr.Zero)
        {
            var errorCode = (uint)Marshal.GetLastWin32Error();
            throw CompactFailureException.FromWin32(
                errorCode,
                "VirtDisk",
                $"CreateEvent failed: {FormatWin32Error(errorCode)}",
                backend: BackendName,
                vhdPath: vhdPath);
        }

        var overlappedPtr = IntPtr.Zero;
        try
        {
            var overlapped = new NativeOverlapped
            {
                Internal = UIntPtr.Zero,
                InternalHigh = UIntPtr.Zero,
                Offset = 0,
                OffsetHigh = 0,
                EventHandle = eventHandle
            };
            overlappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
            Marshal.StructureToPtr(overlapped, overlappedPtr, fDeleteOld: false);

            var parameters = new CompactVirtualDiskParameters
            {
                Version = CompactVirtualDiskVersion.Version1,
                Reserved = 0
            };

            var flags = quickMode ? CompactVirtualDiskFlag.NoZeroScan : CompactVirtualDiskFlag.None;
            var mode = quickMode ? "no zero scan" : "zero scan";
            using var operationRegistration = _operationRegistry.Register(handle, overlappedPtr, eventHandle, vhdPath, mode);
            progress.Report(CompactProgressUpdate.Progress("VirtDisk", $"CompactVirtualDisk {mode} mode started", 5, backend: BackendName));
            var compactResult = CompactVirtualDisk(handle, flags, ref parameters, overlappedPtr);
            if (compactResult == ErrorSuccess)
            {
                progress.Report(CompactProgressUpdate.Complete("VirtDisk", "CompactVirtualDisk completed", backend: BackendName));
                return;
            }
            if (compactResult != ErrorIoPending)
            {
                throw CompactFailureException.FromWin32(
                    compactResult,
                    "VirtDisk",
                    $"CompactVirtualDisk failed: {FormatWin32Error(compactResult)}",
                    backend: BackendName,
                    vhdPath: vhdPath);
            }

            var maxPercent = 5d;
            while (true)
            {
                var waitResult = WaitForSingleObject(eventHandle, 500);
                if (waitResult == WaitObject0)
                {
                    CompleteOverlappedOperation(handle, overlappedPtr, vhdPath, progress);
                    return;
                }

                if (waitResult != WaitTimeout)
                {
                    var waitError = waitResult == WaitFailed ? (uint)Marshal.GetLastWin32Error() : waitResult;
                    throw CompactFailureException.FromWin32(
                        waitError,
                        "VirtDisk",
                        $"WaitForSingleObject failed: {FormatWin32Error(waitError)}",
                        backend: BackendName,
                        vhdPath: vhdPath);
                }

                cancellationToken.ThrowIfCancellationRequested();

                var progressResult = GetVirtualDiskOperationProgress(handle, overlappedPtr, out var virtualDiskProgress);
                if (progressResult != ErrorSuccess)
                {
                    throw CompactFailureException.FromWin32(
                        progressResult,
                        "VirtDisk",
                        $"GetVirtualDiskOperationProgress failed: {FormatWin32Error(progressResult)}",
                        backend: BackendName,
                        vhdPath: vhdPath);
                }

                if (virtualDiskProgress.OperationStatus == ErrorIoPending)
                {
                    var calculatedPercent = CalculatePercent(virtualDiskProgress);
                    var progressMode = calculatedPercent is > 0 and < 99.5
                        ? CompactProgressMode.PercentKnown
                        : CompactProgressMode.PendingNoReliablePercent;
                    if (progressMode == CompactProgressMode.PercentKnown)
                    {
                        maxPercent = Math.Min(99, Math.Max(maxPercent, calculatedPercent));
                    }

                    var message = progressMode == CompactProgressMode.PercentKnown
                        ? "CompactVirtualDisk running"
                        : "waiting for VirtDisk completion";

                    progress.Report(CompactProgressUpdate.Progress(
                        "VirtDisk",
                        message,
                        maxPercent,
                        distro: null,
                        backend: BackendName,
                        currentValue: virtualDiskProgress.CurrentValue,
                        completionValue: virtualDiskProgress.CompletionValue,
                        operationStatus: virtualDiskProgress.OperationStatus,
                        calculatedPercent: calculatedPercent,
                        progressMode: progressMode));

                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (virtualDiskProgress.OperationStatus != ErrorSuccess)
                {
                    throw CompactFailureException.FromWin32(
                        virtualDiskProgress.OperationStatus,
                        "VirtDisk",
                        $"CompactVirtualDisk failed: {FormatWin32Error(virtualDiskProgress.OperationStatus)}",
                        backend: BackendName,
                        vhdPath: vhdPath);
                }

                progress.Report(CompactProgressUpdate.Complete("VirtDisk", "CompactVirtualDisk completed", backend: BackendName));
                return;
            }
        }
        finally
        {
            if (overlappedPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(overlappedPtr);
            }

            CloseHandle(eventHandle);
        }
    }

    private static void CompleteOverlappedOperation(
        SafeFileHandle handle,
        IntPtr overlappedPtr,
        string vhdPath,
        IProgress<CompactProgressUpdate> progress)
    {
        var completed = GetOverlappedResult(handle, overlappedPtr, out _, bWait: false);
        var finalError = completed ? ErrorSuccess : (uint)Marshal.GetLastWin32Error();
        if (finalError == ErrorSuccess)
        {
            progress.Report(CompactProgressUpdate.Complete("VirtDisk", "CompactVirtualDisk completed", backend: BackendName));
            return;
        }

        if (finalError == ErrorOperationAborted)
        {
            throw new CompactFailureException(
                CompactFailureKind.Canceled,
                "VirtDisk",
                "VirtDisk compact was canceled. Compaction can be re-run later.",
                backend: BackendName,
                vhdPath: vhdPath,
                win32ErrorCode: finalError);
        }

        throw CompactFailureException.FromWin32(
            finalError,
            "VirtDisk",
            $"CompactVirtualDisk failed: {FormatWin32Error(finalError)}",
            backend: BackendName,
            vhdPath: vhdPath);
    }

    private static SafeFileHandle Open(string vhdPath)
    {
        var storageType = new VirtualStorageType
        {
            DeviceId = VirtualStorageDeviceType.Vhdx,
            VendorId = MicrosoftVirtualStorageVendorId
        };

        var accessMask =
            VirtualDiskAccessMask.MetaOps |
            VirtualDiskAccessMask.AttachReadOnly |
            VirtualDiskAccessMask.Detach;

        var result = OpenVirtualDisk(
            ref storageType,
            vhdPath,
            accessMask,
            OpenVirtualDiskFlag.None,
            IntPtr.Zero,
            out var handle);

        if (result != 0)
        {
            storageType.DeviceId = VirtualStorageDeviceType.Unknown;
            result = OpenVirtualDisk(
                ref storageType,
                vhdPath,
                accessMask,
                OpenVirtualDiskFlag.None,
                IntPtr.Zero,
                out handle);
        }

        if (result != 0)
        {
            throw CompactFailureException.FromWin32(
                result,
                "VirtDisk",
                $"OpenVirtualDisk failed: {FormatWin32Error(result)}",
                backend: BackendName,
                vhdPath: vhdPath);
        }

        return handle;
    }

    private static string FormatWin32Error(uint errorCode)
        => $"{errorCode} / 0x{errorCode:X8}: {new Win32Exception((int)errorCode).Message}";

    private static double CalculatePercent(VirtualDiskProgress progress)
    {
        if (progress.CompletionValue == 0)
        {
            return 0;
        }

        return Math.Clamp(progress.CurrentValue / (double)progress.CompletionValue * 100, 0, 100);
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint OpenVirtualDisk(
        ref VirtualStorageType virtualStorageType,
        string path,
        VirtualDiskAccessMask virtualDiskAccessMask,
        OpenVirtualDiskFlag flags,
        IntPtr parameters,
        out SafeFileHandle handle);

    [DllImport("virtdisk.dll")]
    private static extern uint AttachVirtualDisk(
        SafeFileHandle virtualDiskHandle,
        IntPtr securityDescriptor,
        AttachVirtualDiskFlag flags,
        uint providerSpecificFlags,
        IntPtr parameters,
        IntPtr overlapped);

    [DllImport("virtdisk.dll")]
    private static extern uint CompactVirtualDisk(
        SafeFileHandle virtualDiskHandle,
        CompactVirtualDiskFlag flags,
        ref CompactVirtualDiskParameters parameters,
        IntPtr overlapped);

    [DllImport("virtdisk.dll")]
    private static extern uint GetVirtualDiskOperationProgress(
        SafeFileHandle virtualDiskHandle,
        IntPtr overlapped,
        out VirtualDiskProgress progress);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle hFile, IntPtr lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("virtdisk.dll")]
    private static extern uint DetachVirtualDisk(
        SafeFileHandle virtualDiskHandle,
        DetachVirtualDiskFlag flags,
        uint providerSpecificFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(
        IntPtr lpEventAttributes,
        bool bManualReset,
        bool bInitialState,
        string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualStorageType
    {
        public VirtualStorageDeviceType DeviceId;
        public Guid VendorId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOverlapped
    {
        public UIntPtr Internal;
        public UIntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr EventHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualDiskProgress
    {
        public uint OperationStatus;
        public ulong CurrentValue;
        public ulong CompletionValue;
    }

    private enum VirtualStorageDeviceType : uint
    {
        Unknown = 0,
        Vhdx = 3
    }

    [Flags]
    private enum VirtualDiskAccessMask : uint
    {
        AttachReadOnly = 0x00010000,
        Detach = 0x00040000,
        MetaOps = 0x00200000
    }

    private enum OpenVirtualDiskFlag : uint
    {
        None = 0
    }

    [Flags]
    private enum AttachVirtualDiskFlag : uint
    {
        None = 0,
        ReadOnly = 0x00000001,
        NoDriveLetter = 0x00000002
    }

    private enum CompactVirtualDiskFlag : uint
    {
        None = 0,
        NoZeroScan = 0x00000001,
        NoBlockMoves = 0x00000002
    }

    private enum CompactVirtualDiskVersion : uint
    {
        Unspecified = 0,
        Version1 = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompactVirtualDiskParameters
    {
        public CompactVirtualDiskVersion Version;
        public uint Reserved;
    }

    private enum DetachVirtualDiskFlag : uint
    {
        None = 0
    }
}
