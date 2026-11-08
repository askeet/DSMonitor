using System.IO;

namespace DSMonitor.Services;

/// <summary>
/// Path-confinement helpers. Forge metadata is untrusted input, so any path
/// taken from it must be proven to stay inside the configured runs root.
/// </summary>
public static class PathSafety
{
    public static string NormalizeRoot(string rootPath) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));

    public static bool IsWithinRoot(string? candidatePath, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        try
        {
            var root = NormalizeRoot(rootPath);
            var candidate = Path.GetFullPath(candidatePath);
            if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return candidate.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static bool IsSafeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Contains("..", StringComparison.Ordinal) ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        return value == Path.GetFileName(value) &&
               value != "." &&
               value != "..";
    }
}
