using System.Reflection;
using System.Text;

namespace ClearStart;

/// <summary>Small append-only file log. Never throws: logging must not be able to take the shell down.</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _file;

    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearStart");

    public static string DefaultDirectory => Path.Combine(DataDirectory, "logs");

    public static string? FilePath => _file;

    public static void Init(string? directory = null)
    {
        try
        {
            directory ??= DefaultDirectory;
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "clearstart.log");
            var info = new FileInfo(file);
            if (info.Exists && info.Length > 1_000_000)
            {
                File.Move(file, file + ".old", overwrite: true);
            }
            _file = file;
        }
        catch
        {
            _file = null;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Info($"ClearStart {version} starting (Windows {Environment.OSVersion.Version}, pid {Environment.ProcessId})");
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        if (_file == null) return;
        try
        {
            lock (Gate)
            {
                File.AppendAllText(_file,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Nothing sensible to do if the log itself cannot be written.
        }
    }
}
