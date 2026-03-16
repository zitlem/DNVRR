using System.IO;

namespace DNVRR.Services;

public static class Log
{
    private static string? _logPath;
    private static readonly object _lock = new();

    public static void Init(string logPath)
    {
        _logPath = logPath;
        var dir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Start fresh each run
        File.WriteAllText(logPath, $"=== DNVRR started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex.Message}");

    private static void Write(string level, string message)
    {
        if (_logPath == null) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (_lock)
        {
            try { File.AppendAllText(_logPath, line); } catch { }
        }
    }
}
