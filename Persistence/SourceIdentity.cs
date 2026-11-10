using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DSMonitor.Persistence;

/// <summary>
/// Single extraction point for the Windows physical identity used by both the
/// ledger and guarded monitored-source opens. Paths and content hashes are not
/// substitutes for this identity.
/// </summary>
internal static class SourceIdentity
{
    public static LedgerSourceIdentity ReadWindows(FileStream stream) =>
        ReadWindows(stream.SafeFileHandle);

    public static LedgerSourceIdentity ReadWindows(SafeFileHandle handle)
    {
        try
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                return default;
            }

            var fileIndex =
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
            var creationTicks =
                ((long)information.CreationTime.dwHighDateTime << 32) |
                (uint)information.CreationTime.dwLowDateTime;

            return new LedgerSourceIdentity(
                information.VolumeSerialNumber,
                fileIndex,
                creationTicks);
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or EntryPointNotFoundException or IOException or
            UnauthorizedAccessException or InvalidOperationException)
        {
            return default;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation information);
}
