using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security;

namespace FileDock.Services;

public enum PathKind
{
    Missing,
    File,
    Folder,
}

public readonly record struct PathStatus(string Path, PathKind Kind, long? Size);

/// <summary>Path normalisation and read-only file system inspection. Nothing here modifies files.</summary>
public static class FileSystemService
{
    /// <summary>Windows paths are case-insensitive; this is the comparer for duplicate detection.</summary>
    public static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private static readonly char[] InvalidChars = [.. Path.GetInvalidPathChars(), '*', '?'];

    /// <summary>
    /// Turns an untrusted path (from a drop or the saved state) into a canonical absolute path,
    /// or rejects it. Relative paths, wildcards, device paths and malformed input are rejected.
    /// </summary>
    public static bool TryNormalize(string? raw, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 32_000)
            return false;

        string p = raw;

        // Strip the Win32 file namespace prefix so that equivalent spellings compare equal.
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
            p = p[4..];

        // Device namespace (\\.\PhysicalDrive0, \\.\pipe\...) is never a shelf item.
        if (p.StartsWith(@"\\.\", StringComparison.Ordinal) || p.StartsWith(@"//./", StringComparison.Ordinal))
            return false;
        if (p.IndexOfAny(InvalidChars) >= 0)
            return false;
        if (!Path.IsPathFullyQualified(p))
            return false;

        try
        {
            p = Path.GetFullPath(p);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return false;
        }

        // Remove trailing separators, but keep roots such as C:\ and \\server\share\ intact.
        string root = Path.GetPathRoot(p) ?? "";
        while (p.Length > root.Length && (p[^1] == '\\' || p[^1] == '/'))
            p = p[..^1];

        if (p.Length >= 2 && p[1] == ':' && char.IsAsciiLetter(p[0]))
            p = char.ToUpperInvariant(p[0]) + p[1..];

        normalized = p;
        return true;
    }

    /// <summary>
    /// Reports whether a path is currently a file, a folder, or unavailable. May block on slow
    /// or disconnected network paths, so callers run it off the UI thread.
    /// </summary>
    public static PathStatus Inspect(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Exists)
                return new PathStatus(path, PathKind.File, file.Length);

            if (Directory.Exists(path))
                return new PathStatus(path, PathKind.Folder, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or SecurityException)
        {
            Log.Warn($"Could not inspect '{path}': {ex.Message}");
        }

        return new PathStatus(path, PathKind.Missing, null);
    }

    /// <summary>Name shown in the shelf: the last path segment, or the root itself for drives and shares.</summary>
    public static string GetDisplayName(string path)
    {
        string name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>Short description of where an item lives, for example "Downloads" or "C:\".</summary>
    public static string GetLocationName(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent))
            return "";
        string name = Path.GetFileName(parent);
        return string.IsNullOrEmpty(name) ? parent : name;
    }

    /// <summary>Compact, Explorer-style size text.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return bytes == 1 ? "1 byte" : $"{bytes} bytes";

        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = -1;
        do
        {
            value /= 1024;
            unit++;
        }
        while (value >= 1024 && unit < units.Length - 1);

        string number = value < 10 ? value.ToString("0.#") : value.ToString("0");
        return $"{number} {units[unit]}";
    }
}
