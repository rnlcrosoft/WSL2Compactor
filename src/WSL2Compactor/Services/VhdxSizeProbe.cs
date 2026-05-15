using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WSL2Compactor.Services;

internal readonly record struct VhdxSizeSnapshot(long FileSizeBytes, long HostAllocatedBytes);

internal static class VhdxSizeProbe
{
    private const uint InvalidFileSize = 0xFFFFFFFF;

    public static VhdxSizeSnapshot Read(string path)
    {
        var fileSizeBytes = new FileInfo(path).Length;
        return new VhdxSizeSnapshot(fileSizeBytes, ReadHostAllocatedBytes(path, fileSizeBytes));
    }

    private static long ReadHostAllocatedBytes(string path, long fallbackBytes)
    {
        if (!OperatingSystem.IsWindows())
        {
            return fallbackBytes;
        }

        var low = GetCompressedFileSizeW(path, out var high);
        if (low == InvalidFileSize)
        {
            var errorCode = Marshal.GetLastWin32Error();
            if (errorCode != 0)
            {
                throw new Win32Exception(errorCode, $"GetCompressedFileSize failed for {path}");
            }
        }

        var bytes = ((ulong)high << 32) | low;
        if (bytes > long.MaxValue)
        {
            throw new IOException($"Allocated file size is too large: {bytes}");
        }

        return (long)bytes;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);
}
