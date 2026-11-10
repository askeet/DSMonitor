using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DSMonitor.Persistence;
using Microsoft.Win32.SafeHandles;

namespace DSMonitor.Services;

public enum SafeReadFailure
{
    None,
    InvalidPath,
    OutsideRoot,
    ReparsePoint,
    Unverifiable,
    Missing,
    Unavailable
}

public sealed class SafeReadResult : IDisposable
{
    internal SafeReadResult(
        FileStream? stream,
        SafeReadFailure failure,
        string? warning)
    {
        Stream = stream;
        Failure = failure;
        Warning = warning;
    }

    public FileStream? Stream { get; }
    public SafeReadFailure Failure { get; }
    public string? Warning { get; }
    /// <summary>Final in-root path verified against the opened file handle.</summary>
    public string? VerifiedPath { get; internal init; }

    /// <summary>Physical identity read from the same opened handle as the content.</summary>
    public LedgerSourceIdentity PhysicalIdentity { get; internal init; }

    public void Dispose() => Stream?.Dispose();
}

/// <summary>
/// Centralized guarded opens for untrusted monitored content. A lexical check
/// alone is not sufficient: a junction/symlink can redirect an in-root path
/// outside the runs root. Every open validates the path before opening and the
/// actual opened handle before any bytes are read.
/// </summary>
public static class SafeFileAccess
{
    private const uint FileAttributeReparsePoint = 0x00000400;

    public static SafeReadResult OpenRead(string? path, string rootPath, bool rejectHardLinks = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Fail(SafeReadFailure.InvalidPath, "Monitored path is invalid.");
            }

            var root = PathSafety.NormalizeRoot(rootPath);
            var fullPath = Path.GetFullPath(path);

            if (!PathSafety.IsWithinRoot(fullPath, root))
            {
                return Fail(
                    SafeReadFailure.OutsideRoot,
                    "Monitored path is outside the trusted forge runs root; read was refused.");
            }

            var rootFailure = ValidateTrustedRoot(root, out var rootWarning);
            if (rootFailure != SafeReadFailure.None)
            {
                return Fail(rootFailure, rootWarning!);
            }

            if (TryFindReparsePoint(fullPath, out var accessDenied))
            {
                return Fail(
                    SafeReadFailure.ReparsePoint,
                    "Monitored path or a parent is a reparse point; read was refused.");
            }

            if (accessDenied)
            {
                return Fail(
                    SafeReadFailure.Unverifiable,
                    "Monitored path could not be verified safely; read was refused.");
            }

            FileStream? stream = null;
            try
            {
                stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096,
                    FileOptions.SequentialScan);

                var handle = stream.SafeFileHandle;
                if (!GetFileInformationByHandle(handle, out var information))
                {
                    stream.Dispose();
                    return Fail(
                        SafeReadFailure.Unverifiable,
                        "Opened monitored path could not be verified; read was refused.");
                }

                if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    stream.Dispose();
                    return Fail(
                        SafeReadFailure.ReparsePoint,
                        "Opened monitored path is a reparse point; read was refused.");
                }

                if (rejectHardLinks && information.NumberOfLinks != 1)
                {
                    stream.Dispose();
                    return Fail(SafeReadFailure.Unverifiable,
                        "Monitored content has ambiguous file aliases; read was refused.");
                }

                var finalPath = GetFinalPath(handle);
                if (finalPath is null ||
                    !PathSafety.IsWithinRoot(finalPath, root))
                {
                    stream.Dispose();
                    return Fail(
                        SafeReadFailure.Unverifiable,
                        "Opened monitored path resolved outside the trusted runs root; read was refused.");
                }

                return new SafeReadResult(stream, SafeReadFailure.None, null)
                {
                    VerifiedPath = finalPath,
                    PhysicalIdentity = SourceIdentity.ReadWindows(handle)
                };
            }
            catch (FileNotFoundException)
            {
                stream?.Dispose();
                return Fail(SafeReadFailure.Missing, "Monitored path is not available.");
            }
            catch (DirectoryNotFoundException)
            {
                stream?.Dispose();
                return Fail(SafeReadFailure.Missing, "Monitored path is not available.");
            }
            catch (UnauthorizedAccessException)
            {
                stream?.Dispose();
                return Fail(SafeReadFailure.Unavailable, "Monitored path could not be opened read-only.");
            }
            catch (IOException)
            {
                stream?.Dispose();
                return Fail(SafeReadFailure.Unavailable, "Monitored path could not be opened read-only.");
            }
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fail(SafeReadFailure.InvalidPath, "Monitored path is invalid.");
        }
    }

    public static bool TryValidateTrustedRoot(string rootPath, out string? warning)
    {
        try
        {
            var root = PathSafety.NormalizeRoot(rootPath);
            var failure = ValidateTrustedRoot(root, out warning);
            return failure == SafeReadFailure.None;
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            warning = "Forge runs directory path is invalid.";
            return false;
        }
    }

    private static SafeReadFailure ValidateTrustedRoot(string root, out string? warning)
    {
        if (!Directory.Exists(root))
        {
            warning = "Forge runs directory is not available.";
            return SafeReadFailure.Missing;
        }

        if (TryFindReparsePoint(root, out var accessDenied))
        {
            warning = "Forge runs directory or a parent is a reparse point; read-only monitoring was refused.";
            return SafeReadFailure.ReparsePoint;
        }

        if (accessDenied)
        {
            warning = "Forge runs directory could not be verified safely; read-only monitoring was refused.";
            return SafeReadFailure.Unverifiable;
        }

        warning = null;
        return SafeReadFailure.None;
    }

    private static bool TryFindReparsePoint(string path, out bool accessDenied)
    {
        accessDenied = false;
        var current = path;

        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // A missing target may still have a reparse-point parent. Keep walking up.
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or IOException or ArgumentException or
                NotSupportedException or PathTooLongException)
            {
                accessDenied = true;
                return false;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return false;
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

    private static SafeReadResult Fail(SafeReadFailure failure, string warning) =>
        new(null, failure, warning);

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
