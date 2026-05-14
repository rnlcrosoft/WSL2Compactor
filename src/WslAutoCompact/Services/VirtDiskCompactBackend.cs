using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WslAutoCompact.Services;

internal sealed class VirtDiskCompactBackend : ICompactBackend
{
    private static readonly Guid MicrosoftVirtualStorageVendorId = new("EC984AEC-A0F9-47E9-901F-71415A66345B");

    public string Name => "VirtDisk API";

    public async Task CompactAsync(string vhdPath, IProgress<string> log, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            log.Report("virtdisk.dll: OpenVirtualDisk");

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

                if (attachResult == 0)
                {
                    attached = true;
                    log.Report("virtdisk.dll: read-only attach completed");
                }
                else
                {
                    log.Report($"virtdisk.dll: skipping read-only attach ({FormatWin32Error(attachResult)})");
                }

                cancellationToken.ThrowIfCancellationRequested();
                log.Report("virtdisk.dll: running CompactVirtualDisk");
                var compactResult = CompactVirtualDisk(handle, CompactVirtualDiskFlag.None, IntPtr.Zero, IntPtr.Zero);
                if (compactResult != 0)
                {
                    throw new Win32Exception((int)compactResult, $"CompactVirtualDisk failed: {FormatWin32Error(compactResult)}");
                }

                log.Report("virtdisk.dll: CompactVirtualDisk completed");
            }
            finally
            {
                if (attached)
                {
                    var detachResult = DetachVirtualDisk(handle, DetachVirtualDiskFlag.None, 0);
                    log.Report(detachResult == 0
                        ? "virtdisk.dll: detach completed"
                        : $"virtdisk.dll: detach failed ({FormatWin32Error(detachResult)})");
                }
            }
        }, cancellationToken).ConfigureAwait(false);
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
    private static extern uint DetachVirtualDisk(
        SafeFileHandle virtualDiskHandle,
        DetachVirtualDiskFlag flags,
        uint providerSpecificFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualStorageType
    {
        public VirtualStorageDeviceType DeviceId;
        public Guid VendorId;
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
