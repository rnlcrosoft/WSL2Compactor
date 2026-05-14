using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WSL2Compactor.Services;

internal sealed class VirtDiskCompactBackend : ICompactBackend
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorIoPending = 997;
    private static readonly Guid MicrosoftVirtualStorageVendorId = new("EC984AEC-A0F9-47E9-901F-71415A66345B");

    public string Name => "VirtDisk API";

    public async Task CompactAsync(string vhdPath, IProgress<CompactProgressUpdate> progress, CancellationToken cancellationToken)
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
            await CompactWithProgressAsync(handle, progress, cancellationToken).ConfigureAwait(false);
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

    private static async Task CompactWithProgressAsync(
        SafeFileHandle handle,
        IProgress<CompactProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        var eventHandle = CreateEvent(IntPtr.Zero, bManualReset: true, bInitialState: false, lpName: null);
        if (eventHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEvent failed.");
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

            progress.Report(CompactProgressUpdate.Progress("VirtDisk", "CompactVirtualDisk started", 5, backend: "VirtDisk API"));
            var compactResult = CompactVirtualDisk(handle, CompactVirtualDiskFlag.None, IntPtr.Zero, overlappedPtr);
            if (compactResult == ErrorSuccess)
            {
                progress.Report(CompactProgressUpdate.Complete("VirtDisk", "CompactVirtualDisk completed", backend: "VirtDisk API"));
                return;
            }
            if (compactResult != ErrorIoPending)
            {
                throw new Win32Exception((int)compactResult, $"CompactVirtualDisk failed: {FormatWin32Error(compactResult)}");
            }

            var maxPercent = 5d;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var progressResult = GetVirtualDiskOperationProgress(handle, overlappedPtr, out var virtualDiskProgress);
                if (progressResult != ErrorSuccess)
                {
                    throw new Win32Exception((int)progressResult, $"GetVirtualDiskOperationProgress failed: {FormatWin32Error(progressResult)}");
                }

                if (virtualDiskProgress.OperationStatus == ErrorIoPending)
                {
                    var calculatedPercent = CalculatePercent(virtualDiskProgress);
                    maxPercent = Math.Max(maxPercent, calculatedPercent);
                    var message = calculatedPercent >= 100
                        ? "Completing VirtDisk operation"
                        : "CompactVirtualDisk running";

                    progress.Report(CompactProgressUpdate.Progress(
                        "VirtDisk",
                        message,
                        maxPercent,
                        distro: null,
                        backend: "VirtDisk API",
                        currentValue: virtualDiskProgress.CurrentValue,
                        completionValue: virtualDiskProgress.CompletionValue,
                        operationStatus: virtualDiskProgress.OperationStatus,
                        calculatedPercent: calculatedPercent));

                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (virtualDiskProgress.OperationStatus != ErrorSuccess)
                {
                    throw new Win32Exception((int)virtualDiskProgress.OperationStatus, $"CompactVirtualDisk failed: {FormatWin32Error(virtualDiskProgress.OperationStatus)}");
                }

                progress.Report(CompactProgressUpdate.Complete("VirtDisk", "CompactVirtualDisk completed", backend: "VirtDisk API"));
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
            throw new Win32Exception((int)result, $"OpenVirtualDisk failed: {FormatWin32Error(result)}");
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
        IntPtr parameters,
        IntPtr overlapped);

    [DllImport("virtdisk.dll")]
    private static extern uint GetVirtualDiskOperationProgress(
        SafeFileHandle virtualDiskHandle,
        IntPtr overlapped,
        out VirtualDiskProgress progress);

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
        None = 0
    }

    private enum DetachVirtualDiskFlag : uint
    {
        None = 0
    }
}
