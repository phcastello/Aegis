using System.Runtime.InteropServices;

namespace Aegis.Infrastructure.Tools;

internal static class ScriptFileSystem
{
    private const long TmpfsMagic = 0x01021994;

    internal static bool IsBoundedScratch(string path)
    {
        if (!OperatingSystem.IsLinux() || !Environment.Is64BitProcess || !Directory.Exists(path)) return false;
        for (DirectoryInfo? dir = new(path); dir is not null; dir = dir.Parent)
            if (dir.LinkTarget is not null) return false;
        if (StatFs(path, out var fs) != 0 || fs.Type != TmpfsMagic) return false;
        return fs.BlockSize > 0 && fs.Blocks > 0 && fs.Blocks <= (1UL << 30) / (ulong)fs.BlockSize &&
            fs.Files is > 0 and <= 16384;
    }

    internal static long AvailableBytes(string path)
    {
        if (!OperatingSystem.IsLinux()) return new DriveInfo(path).AvailableFreeSpace;
        if (!Environment.Is64BitProcess || StatFs(path, out var fs) != 0 || fs.BlockSize <= 0)
            throw new IOException("Não foi possível consultar o espaço do volume.");
        return checked((long)fs.Available * fs.BlockSize);
    }

    // Linux statfs(2), the native 64-bit ABI shared by the amd64 and arm64
    // backend images. Query this path's actual mount, including container bind
    // mounts. DriveInfo(path) can normalize the argument to a different root.
    [DllImport("libc", EntryPoint = "statfs", SetLastError = true)]
    private static extern int StatFs([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out FileSystemStats stats);

    [StructLayout(LayoutKind.Sequential, Size = 120)]
    private struct FileSystemStats
    {
        public long Type, BlockSize;
        public ulong Blocks, Free, Available, Files;
        // Remaining native fields (ffree through spare[4]) are reserved in Size.
    }
}
