using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Tereditor.Host.Core;

/// <summary>
/// The disk space removing a worktree would free: only bytes private to its files. Clones of the Default workspace
/// share blocks with it (clonefile, reflinks), so a plain size sum would count them in full. macOS asks the
/// filesystem for each file's private size; Linux reads FIEMAP extents and skips shared ones, and counts the allocated
/// size where the filesystem cannot tell; Windows counts allocated size. Symlinks are never followed.
/// </summary>
internal static partial class WorkspaceDiskUsage
{
    private const int FallbackBlock = 4096;

    internal static long Measure(string root, CancellationToken cancellationToken)
    {
        var total = 0L;
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false };
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options).ToList(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (entry is DirectoryInfo) pending.Push(entry.FullName);
                else total += PrivateBytes(entry.FullName, ((FileInfo)entry).Length);
            }
        }
        return total;
    }

    private static long PrivateBytes(string path, long length)
    {
        try
        {
            if (OperatingSystem.IsMacOS() && MacPrivateSize(path) is { } mac) return mac;
            if (OperatingSystem.IsLinux() && LinuxUnsharedSize(path) is { } linux) return linux;
            if (OperatingSystem.IsWindows() && WindowsAllocatedSize(path) is { } windows) return windows;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return (length + FallbackBlock - 1) / FallbackBlock * FallbackBlock;
    }

    // ---- macOS: getattrlist with FSOPT_ATTR_CMN_EXTENDED and ATTR_CMNEXT_PRIVATESIZE (bytes freed if the file were deleted).

    [StructLayout(LayoutKind.Sequential)]
    private struct AttrList
    {
        public ushort BitmapCount, Reserved;
        public uint Common, Volume, Directory, File, Fork;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PrivateSizeReply
    {
        public uint Length;
        public long Size;
    }

    [LibraryImport("libc", EntryPoint = "getattrlist", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int GetAttrList(string path, ref AttrList list, out PrivateSizeReply buffer, nuint size, nuint options);

    private static long? MacPrivateSize(string path)
    {
        // With FSOPT_ATTR_CMN_EXTENDED (0x20) the extended common attributes travel in the fork field.
        var list = new AttrList { BitmapCount = 5, Fork = 0x8 };
        return GetAttrList(path, ref list, out var reply, (nuint)Marshal.SizeOf<PrivateSizeReply>(), 0x20) == 0 && reply.Size >= 0 ? reply.Size : null;
    }

    // ---- Linux: FS_IOC_FIEMAP; FIEMAP_EXTENT_SHARED marks extents another file also references (btrfs, xfs reflinks).

    private const uint FsIocFiemap = 0xC020660B;
    private const uint ExtentLast = 0x1, ExtentShared = 0x2000;
    private const int FiemapHeader = 32, FiemapExtent = 56, ExtentBatch = 64;

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static unsafe partial int Ioctl(int fd, nuint request, byte* argument);

    private static unsafe long? LinuxUnsharedSize(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var fd = (int)handle.DangerousGetHandle();
        var buffer = new byte[FiemapHeader + FiemapExtent * ExtentBatch];
        var total = 0L;
        ulong start = 0;
        fixed (byte* map = buffer)
            while (true)
            {
                new Span<byte>(map, buffer.Length).Clear();
                BinaryPrimitives.WriteUInt64LittleEndian(new(map, 8), start);
                BinaryPrimitives.WriteUInt64LittleEndian(new(map + 8, 8), ulong.MaxValue - start);
                BinaryPrimitives.WriteUInt32LittleEndian(new(map + 24, 4), ExtentBatch);
                if (Ioctl(fd, FsIocFiemap, map) != 0) return null;
                var mapped = BinaryPrimitives.ReadUInt32LittleEndian(new(map + 20, 4));
                if (mapped == 0) return total;
                var last = false;
                for (var index = 0; index < mapped && index < ExtentBatch; index++)
                {
                    var extent = map + FiemapHeader + index * FiemapExtent;
                    var logical = BinaryPrimitives.ReadUInt64LittleEndian(new(extent, 8));
                    var length = BinaryPrimitives.ReadUInt64LittleEndian(new(extent + 16, 8));
                    var flags = BinaryPrimitives.ReadUInt32LittleEndian(new(extent + 40, 4));
                    if ((flags & ExtentShared) == 0) total += (long)length;
                    start = logical + length;
                    last = (flags & ExtentLast) != 0;
                }
                if (last) return total;
            }
    }

    // ---- Windows: allocated size, which a sparse or compressed file reports below its length.

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetCompressedFileSize(string path, out uint high);

    private static long? WindowsAllocatedSize(string path)
    {
        var low = GetCompressedFileSize(path, out var high);
        return low == uint.MaxValue && Marshal.GetLastPInvokeError() != 0 ? null : ((long)high << 32) | low;
    }
}