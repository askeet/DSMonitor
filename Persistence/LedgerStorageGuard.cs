using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DSMonitor.Persistence;

/// <summary>
/// Fail-closed storage confinement for the ledger database and its SQLite
/// sidecars. Every existing ancestor from the volume root to the database
/// directory is inspected before any directory is created. Existing paths must
/// not be reparse points, must resolve inside the approved root, must have a
/// verifiable handle identity, and must not be multiply hard-linked.
///
/// Residual threats are explicit and are not claimed to be eliminated:
/// an active actor can replace a path between a check and SQLite's own open or
/// between revalidation points; SQLite itself may create, inspect, or recover
/// WAL/SHM/hot-journal files while opening; and an OS-level link race is
/// outside a path-based guard. Avoiding those exposures would require a custom
/// VFS or stronger OS isolation, neither of which is implemented. This guard
/// only reads metadata/handles and never deletes, truncates, or modifies a
/// suspicious file.
/// </summary>
internal sealed class LedgerStorageGuard
{
    private const uint FileAttributeReparsePoint = 0x00000400;

    private static readonly string[] EntrySuffixes = { string.Empty, "-wal", "-shm", "-journal" };

    private readonly string _databasePath;
    private readonly string _root;

    public LedgerStorageGuard(string databasePath, string root)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public void PrepareAndValidate()
    {
        var databaseDirectory = Path.GetDirectoryName(_databasePath);
        if (string.IsNullOrEmpty(databaseDirectory))
        {
            throw Fail("The ledger database directory is invalid.");
        }

        if (!IsWithinRoot(databaseDirectory))
        {
            throw Fail("The ledger database directory is outside the approved storage root.");
        }

        // Validate every existing component before creating anything. This
        // includes ancestors above the approved root, so an ancestor junction
        // cannot redirect initial database or directory creation.
        ValidateExistingAncestry(databaseDirectory);

        EnsureRoot();
        EnsureDirectoryChain(databaseDirectory);

        // Revalidate the now-existing chain to catch observable changes made
        // during creation. A concurrent replacement after this point remains a
        // documented residual race.
        ValidateExistingAncestry(databaseDirectory);

        var databaseExists = TryGetAttributes(_databasePath, out _);
        if (!databaseExists)
        {
            foreach (var suffix in EntrySuffixes)
            {
                if (suffix.Length > 0 &&
                    TryGetAttributes(_databasePath + suffix, out _))
                {
                    throw Fail(
                        "A SQLite sidecar exists without its ledger database; storage was refused.");
                }
            }
        }

        foreach (var suffix in EntrySuffixes)
        {
            ValidateEntry(
                _databasePath + suffix,
                isDatabase: suffix.Length == 0);
        }
    }

    private void ValidateExistingAncestry(string targetPath)
    {
        var fullTarget = Path.GetFullPath(targetPath);
        var pathRoot = Path.GetPathRoot(fullTarget);
        if (string.IsNullOrEmpty(pathRoot))
        {
            throw Fail("The ledger storage path root is invalid.");
        }

        if (TryGetAttributes(pathRoot, out var rootAttributes))
        {
            ValidateDirectoryComponent(pathRoot, rootAttributes);
        }
        else
        {
            throw Fail("The ledger storage volume root could not be verified.");
        }

        var relative = fullTarget[pathRoot.Length..];
        var current = pathRoot;
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
                throw Fail("The ledger storage path escapes its resolved ancestry.");
            }

            current = Path.Combine(current, segment);
            if (!TryGetAttributes(current, out var attributes))
            {
                // The first missing component means all deeper components are
                // also missing. Creation happens only after this full existing
                // ancestry check has completed.
                return;
            }

            ValidateDirectoryComponent(current, attributes);
        }
    }

    private static void ValidateDirectoryComponent(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Fail(
                $"The ledger storage ancestor '{path}' is a reparse point; storage was refused.");
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw Fail(
                $"The ledger storage ancestor '{path}' is not a directory.");
        }
    }

    private void EnsureRoot()
    {
        if (TryGetAttributes(_root, out var attributes))
        {
            ValidateDirectoryComponent(_root, attributes);
            return;
        }

        Directory.CreateDirectory(_root);

        if (!TryGetAttributes(_root, out attributes))
        {
            throw Fail("The approved ledger storage root could not be created or verified.");
        }

        ValidateDirectoryComponent(_root, attributes);
    }

    private void EnsureDirectoryChain(string databaseDirectory)
    {
        var relative = Path.GetRelativePath(_root, databaseDirectory);
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
            if (TryGetAttributes(current, out var attributes))
            {
                ValidateDirectoryComponent(current, attributes);
                continue;
            }

            Directory.CreateDirectory(current);
            if (!TryGetAttributes(current, out attributes))
            {
                throw Fail("A ledger storage directory component could not be created or verified.");
            }

            ValidateDirectoryComponent(current, attributes);
        }
    }

    private void ValidateEntry(string path, bool isDatabase)
    {
        if (!IsWithinRoot(path))
        {
            throw Fail("A ledger storage path is outside the approved root.");
        }

        if (!TryGetAttributes(path, out var attributes))
        {
            // SQLite is allowed to create a missing database, WAL, SHM, or
            // rollback-journal sidecar after these checks.
            return;
        }

        var label = isDatabase
            ? "The ledger database path"
            : "A ledger SQLite sidecar path";

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Fail($"{label} is a reparse point; storage was refused.");
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw Fail($"{label} is a directory.");
        }

        SafeFileHandle handle;
        try
        {
            // Read-only open with broad sharing: inspect without modifying or
            // exclusively locking the file, even when SQLite currently holds
            // the database or a sidecar.
            handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw Fail($"{label} could not be opened for non-modifying verification.");
        }

        using (handle)
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw Fail($"{label} has no verifiable handle identity; storage was refused.");
            }

            if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw Fail($"{label} is a reparse point; storage was refused.");
            }

            if (information.NumberOfLinks != 1)
            {
                throw Fail(
                    $"{label} has multiple hard links or an unverifiable link count; storage was refused.");
            }

            var finalPath = GetFinalPath(handle);
            if (finalPath is null || !IsWithinRoot(finalPath))
            {
                throw Fail($"{label} resolves outside the approved root.");
            }
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

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
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
