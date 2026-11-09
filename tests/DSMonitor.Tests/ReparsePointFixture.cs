using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DSMonitor.Tests;

/// <summary>
/// Test-only helpers that create reparse points inside the temporary test
/// workspace. Links are always cleaned non-recursively.
/// </summary>
internal static class ReparsePointFixture
{
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint SymbolicLinkFlagAllowUnprivilegedCreate = 0x00000002;

    public static bool TryCreateJunction(string linkPath, string targetPath)
    {
        var fullLink = Path.GetFullPath(linkPath);
        var fullTarget = Path.GetFullPath(targetPath);
        if (Directory.Exists(fullLink) || File.Exists(fullLink))
        {
            throw new IOException("The junction fixture path already exists.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullLink)!);
        Directory.CreateDirectory(fullLink);

        using var handle = CreateFileW(
            fullLink,
            GenericWrite,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            TryDeleteDirectory(fullLink);
            return false;
        }

        var substitute = @"\??\" + fullTarget;
        var print = fullTarget;
        var substituteBytes = Encoding.Unicode.GetBytes(substitute);
        var printBytes = Encoding.Unicode.GetBytes(print);
        var pathBytes = substituteBytes.Length + sizeof(char) + printBytes.Length + sizeof(char);
        var reparseDataLength = 8 + pathBytes;
        var totalLength = 8 + reparseDataLength;
        var buffer = Marshal.AllocHGlobal(totalLength);
        try
        {
            for (var index = 0; index < totalLength; index++)
            {
                Marshal.WriteByte(buffer, index, 0);
            }

            Marshal.WriteInt32(buffer, 0, unchecked((int)IoReparseTagMountPoint));
            Marshal.WriteInt16(buffer, 4, checked((short)reparseDataLength));
            Marshal.WriteInt16(buffer, 6, 0);
            Marshal.WriteInt16(buffer, 8, 0);
            Marshal.WriteInt16(buffer, 10, checked((short)substituteBytes.Length));
            Marshal.WriteInt16(buffer, 12, checked((short)(substituteBytes.Length + sizeof(char))));
            Marshal.WriteInt16(buffer, 14, checked((short)printBytes.Length));
            Marshal.Copy(substituteBytes, 0, IntPtr.Add(buffer, 16), substituteBytes.Length);
            Marshal.Copy(
                printBytes,
                0,
                IntPtr.Add(buffer, 16 + substituteBytes.Length + sizeof(char)),
                printBytes.Length);

            var success = DeviceIoControl(
                handle,
                FsctlSetReparsePoint,
                buffer,
                totalLength,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero);

            if (!success)
            {
                TryDeleteDirectory(fullLink);
                return false;
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static bool TryCreateFileSymbolicLink(
        string linkPath,
        string targetPath,
        out int win32Error)
    {
        var fullLink = Path.GetFullPath(linkPath);
        var fullTarget = Path.GetFullPath(targetPath);
        if (File.Exists(fullLink) || Directory.Exists(fullLink))
        {
            throw new IOException("The symbolic-link fixture path already exists.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullLink)!);
        win32Error = 0;
        try
        {
            // Try the unprivileged flag first. Developer Mode or the
            // SeCreateSymbolicLinkPrivilege may still be required by Windows.
            foreach (var flags in new[]
                     {
                         SymbolicLinkFlagAllowUnprivilegedCreate,
                         0u
                     })
            {
                if (!CreateSymbolicLinkW(fullLink, fullTarget, flags))
                {
                    win32Error = Marshal.GetLastWin32Error();
                    continue;
                }

                // Some locked-down environments report success but leave an
                // unusable/dangling link. Treat that as unsupported for this test.
                if (!File.Exists(fullLink))
                {
                    DeleteLink(fullLink);
                    win32Error = Marshal.GetLastWin32Error();
                    continue;
                }

                return true;
            }

            DeleteLink(fullLink);
            return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            win32Error = ex is DllNotFoundException ? 126 : 127;
            return false;
        }
    }

    public static bool TryCreateHardLink(string linkPath, string existingPath)
    {
        var fullLink = Path.GetFullPath(linkPath);
        var fullExisting = Path.GetFullPath(existingPath);
        if (File.Exists(fullLink) || Directory.Exists(fullLink))
        {
            throw new IOException("The hard-link fixture path already exists.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullLink)!);
        return CreateHardLinkW(fullLink, fullExisting, IntPtr.Zero);
    }

    public static void DeleteLink(string path)
    {
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
                {
                    // No remaining link entry to clean up.
                }

                return;
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                return;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.Delete(path, recursive: false);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort test cleanup only.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort test cleanup only.
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        IntPtr inputBuffer,
        int inputBufferSize,
        IntPtr outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CreateSymbolicLinkW(
        string symbolicLink,
        string targetFileName,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
