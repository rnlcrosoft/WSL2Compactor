using System.Buffers.Binary;
using DiscUtils.Streams;
using DiscUtils.Vhdx;

namespace WSL2Compactor.Services;

internal static class VhdxExt4UsageProbe
{
    private const int SuperBlockOffset = 1024;
    private const int SuperBlockSize = 1024;
    private const ushort ExtMagic = 0xEF53;
    private const uint Ext4FeatureIncompat64Bit = 0x80;
    private const uint Ext4FeatureIncompatBigAlloc = 0x200;

    public static LinuxUsageSnapshot? TryRead(string vhdPath)
    {
        if (TestHooks.IsEnabled(TestHooks.FailExt4Probe))
        {
            return null;
        }

        try
        {
            FileStream? stream = new(vhdPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            try
            {
                using var disk = new Disk(stream, Ownership.Dispose);
                stream = null;
                return TryRead(disk);
            }
            finally
            {
                stream?.Dispose();
            }
        }
        catch
        {
            // Fall through to DiscUtils path-based open before the caller tries WSL df fallback.
        }

        try
        {
            using var disk = new Disk(vhdPath, FileAccess.Read);
            return TryRead(disk);
        }
        catch
        {
            return null;
        }
    }

    private static LinuxUsageSnapshot? TryRead(Disk disk)
    {
        var content = disk.Content;
        var superBlock = new byte[SuperBlockSize];
        content.Position = SuperBlockOffset;
        ReadExactly(content, superBlock);

        return TryReadSuperBlock(superBlock, "ext4 probe");
    }

    public static LinuxUsageSnapshot? TryReadSuperBlock(byte[] superBlock, string source)
    {
        if (superBlock.Length < SuperBlockSize ||
            BinaryPrimitives.ReadUInt16LittleEndian(superBlock.AsSpan(0x38)) != ExtMagic)
        {
            return null;
        }

        var blockSizeShift = BinaryPrimitives.ReadUInt32LittleEndian(superBlock.AsSpan(0x18));
        if (blockSizeShift > 6)
        {
            return null;
        }

        var blockSize = 1UL << (10 + (int)blockSizeShift);
        var incompatibleFeatures = BinaryPrimitives.ReadUInt32LittleEndian(superBlock.AsSpan(0x60));
        var has64BitBlocks = (incompatibleFeatures & Ext4FeatureIncompat64Bit) != 0;
        if ((incompatibleFeatures & Ext4FeatureIncompatBigAlloc) != 0)
        {
            return null;
        }

        var totalBlocks = ReadExt4BlockCount(superBlock, 0x04, 0x150, has64BitBlocks);
        var freeBlocks = ReadExt4BlockCount(superBlock, 0x0C, 0x158, has64BitBlocks);
        var overheadBlocks = BinaryPrimitives.ReadUInt32LittleEndian(superBlock.AsSpan(0x248));

        if (totalBlocks == 0 || freeBlocks > totalBlocks || overheadBlocks == 0 || overheadBlocks > totalBlocks)
        {
            return null;
        }

        var usedBlocks = totalBlocks - freeBlocks;
        usedBlocks = usedBlocks > overheadBlocks ? usedBlocks - overheadBlocks : 0;

        var usedBytes = checked((long)(usedBlocks * blockSize));
        var overheadBytes = checked((long)(overheadBlocks * blockSize));
        return new LinuxUsageSnapshot(usedBytes, overheadBytes, source);
    }

    private static ulong ReadExt4BlockCount(byte[] superBlock, int lowOffset, int highOffset, bool has64BitBlocks)
    {
        var value = (ulong)BinaryPrimitives.ReadUInt32LittleEndian(superBlock.AsSpan(lowOffset));
        if (has64BitBlocks)
        {
            value |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(superBlock.AsSpan(highOffset)) << 32;
        }

        return value;
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException("Could not read ext4 superblock.");
            }

            offset += read;
        }
    }
}
