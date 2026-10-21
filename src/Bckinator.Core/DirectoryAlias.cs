using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Bckinator.Core;

// A junction/symlink changes only the pathname 7-Zip sees. It never copies source data.
internal sealed class DirectoryAlias(string path) : IDisposable
{
    public string Path { get; } = path;

    public static DirectoryAlias? TryCreate(string path, string target, Action<string> log)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
            return new(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            log("Directory symlink unavailable; trying a local junction.");
        }

        if (!target.StartsWith(@"\\", StringComparison.Ordinal) && OperatingSystem.IsWindows())
        {
            try
            {
                Directory.CreateDirectory(path);
                string substitute = @"\??\" + System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(target));
                byte[] names = Encoding.Unicode.GetBytes(substitute + '\0' + target + '\0');
                byte[] buffer = new byte[16 + names.Length];
                BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0); // IO_REPARSE_TAG_MOUNT_POINT
                BitConverter.GetBytes(checked((ushort)(8 + names.Length))).CopyTo(buffer, 4);
                BitConverter.GetBytes(checked((ushort)(substitute.Length * 2))).CopyTo(buffer, 10);
                BitConverter.GetBytes(checked((ushort)((substitute.Length + 1) * 2))).CopyTo(buffer, 12);
                BitConverter.GetBytes(checked((ushort)(target.Length * 2))).CopyTo(buffer, 14);
                names.CopyTo(buffer, 16);
                using var handle = CreateFile(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return new(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or OverflowException)
            {
                log("Directory junction unavailable: " + ex.Message);
                // Remove only the newly created empty directory; never recursively traverse a link.
                if (Directory.Exists(path)) Directory.Delete(path, recursive: false);
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (!File.GetAttributes(Path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("The temporary source alias changed unexpectedly; it was not removed.");
        Directory.Delete(Path, recursive: false);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
