using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DSMonitor.Persistence;

/// <summary>
/// Fail-closed storage confinement for the ledger database and its SQLite
/// sidecars. Existing path components and opened-handle final paths must stay
/// inside the approved root and must not be reparse points.
///
/// A concurrent replacement between validation and SQLite's own open cannot
/// be fully eliminated without OS-level isolation. The guard revalidates
/// before writes and fails closed whenever an escape or reparse point is
/// observable.
/// </summary>
internal sealed class LedgerStorageGuard
{
    private const uint FileAttributeReparsePoint = 0x00000400;

    private readonly string _databasePath;
    private readonly string _root;

    public LedgerStorageGuard(string databasePath, string root)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public void PrepareAndValidate()
    {
        EnsureRoot();
        EnsureDirectoryChain();
        ValidateEntry(_databasePath, isDatabase: true);
        ValidateEntry(_databasePath + "-wal", isDatabase: false);
        ValidateEntry(_databasePath + "-shm", isDatabase: false);
    }

    private void EnsureRoot()
    {
        if (!Directory.Exists(_root))
        {
            Directory.CreateDirectory(_root);
        }

        var attributes = File.GetAttributes(_root);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Fail("The approved ledger storage root is a reparse point; storage was refused.");
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw Fail("The approved ledger storage root is not a directory.");
        }
    }

    private void EnsureDirectoryChain()
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (string.IsNullOrEmpty(directory))
        {
            throw Fail("The ledger database directory is invalid.");
        }

        if (!IsWithinRoot(directory))
        {
            throw Fail("The ledger database directory is outside the approved storage root.");
        }

        var relative = Path.GetRelativePath(_root, directory);
        if (relative == ".")
        {
            return;
        }

        var current = _root;
        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                throw Fail("The ledger database directory escapes the approved storage root.");
            }

            current = Path.Combine(current, segment);
            if (File.Exists(current) && !Directory.Exists(current))
            {
                throw Fail("A ledger storage directory component is a file.");
            }

            if (!Directory.Exists(current))
            {
                Directory.CreateDirectory(current);
            }

            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Fail("A ledger storage directory component is a reparse point; storage was refused.");
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw Fail("A ledger storage directory component is not a directory.");
            }
        }
    }

    private void ValidateEntry(string path, bool isDatabase)
    {
        if (!IsWithinRoot(path))
        {
            throw Fail("A ledger storage path is outside the approved root.");
        }

        var directoryEntry = Directory.Exists(path);
        var fileEntry = File.Exists(path);
        if (!directoryEntry && !fileEntry)
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Fail(
                isDatabase
                    ? "The ledger database path is a reparse point; storage was refused."
                    : "A ledger SQLite sidecar path is a reparse point; storage was refused.");
        }

        if (directoryEntry)
        {
            throw Fail(
                isDatabase
                    ? "The ledger database path is a directory."
                    : "A ledger SQLite sidecar path is a directory.");
        }

        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw Fail("A ledger storage path could not be verified.");
        }

        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw Fail("An opened ledger storage path is a reparse point; storage was refused.");
        }

        var finalPath = GetFinalPath(handle);
        if (finalPath is null || !IsWithinRoot(finalPath))
        {
            throw Fail("An opened ledger storage path resolves outside the approved root.");
        }
    }

    private bool IsWithinRoot(string candidate)
    {
        try
        {
            var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (fullCandidate.Equals(_root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return fullCandidate.StartsWith(
                _root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
        {
            return null;
        }

        if (length >= (uint)buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= (uint)buffer.Capacity)
            {
                return null;
            }
        }

        var path = buffer.ToString(0, checked((int)length)).TrimEnd('\0');
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path[8..];
        }
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        return path;
    }

    private static LedgerException Fail(string message) =>
        new(LedgerStorageFailure.Unavailable, message);

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle handle,
        StringBuilder path,
        uint pathLength,
        uint flags);
}
