using System.IO;
using System.Text;

namespace FileDock.Services;

/// <summary>
/// Small local diagnostic log (<c>%LOCALAPPDATA%\FileDock\filedock.log</c>).
/// The file is capped; when it grows past the limit it is rolled to <c>filedock.old.log</c>.
/// Nothing is ever sent anywhere.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    public static void Initialize(string directory)
    {
        _path = Path.Combine(directory, "filedock.log");
    }

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        if (_path is null)
            return;

        var sb = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(' ').Append(level).Append(' ').Append(message);
        if (ex is not null)
            sb.AppendLine().Append(ex);
        sb.AppendLine();

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(_path, Path.ChangeExtension(_path, ".old.log"), overwrite: true);
                File.AppendAllText(_path, sb.ToString());
            }
            catch (Exception)
            {
                // Logging must never take the application down; there is nowhere else to report this.
            }
        }
    }
}
