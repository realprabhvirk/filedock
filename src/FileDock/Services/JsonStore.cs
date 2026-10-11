using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileDock.Services;

public enum LoadStatus
{
    /// <summary>The file was read successfully.</summary>
    Loaded,

    /// <summary>No file yet (first run). Defaults are used.</summary>
    NotFound,

    /// <summary>The file was damaged. It was set aside for troubleshooting and defaults are used.</summary>
    Corrupt,

    /// <summary>The file exists but could not be read (for example, locked). Defaults are used.</summary>
    Unreadable,
}

public sealed record LoadResult<T>(T Value, LoadStatus Status, string? BackupPath);

/// <summary>Reads and writes the small JSON state files with crash-safe replacement.</summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Keep paths and shortcuts readable ("Ctrl+Alt", "Café") in these local, never-HTML files.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static LoadResult<T> Load<T>(string path) where T : class, new()
    {
        if (!File.Exists(path))
            return new LoadResult<T>(new T(), LoadStatus.NotFound, null);

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Could not read {path}", ex);
            return new LoadResult<T>(new T(), LoadStatus.Unreadable, null);
        }

        try
        {
            T? value = JsonSerializer.Deserialize<T>(json, Options);
            if (value is not null)
                return new LoadResult<T>(value, LoadStatus.Loaded, null);
            Log.Warn($"{path} contained a null document");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            Log.Error($"{path} is not valid JSON", ex);
        }

        // Keep the damaged file so it can be inspected, then continue with defaults.
        string backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(path, backup, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Could not preserve damaged file {path}", ex);
            backup = path;
        }
        return new LoadResult<T>(new T(), LoadStatus.Corrupt, backup);
    }

    /// <summary>
    /// Writes to a temporary file first and only then replaces the real file, so a crash or
    /// full disk mid-write never leaves a truncated state file behind.
    /// </summary>
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Options);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}
