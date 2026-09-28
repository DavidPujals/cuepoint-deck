namespace SegmentDeck.Core.Logging;

public enum LogLevel { Info, Warn, Error }

/// <summary>Daily rolling text log in %AppData%\SegmentDeck\logs\segmentdeck-yyyy-MM-dd.log, kept 14 days.
/// Thread-safe. Every line is also raised on <see cref="Written"/> for the status area. Never throws.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string _dir = "";
    private const int KeepDays = 14;

    public static string Directory => _dir;
    public static event Action<LogLevel, string>? Written;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SegmentDeck", "logs");

    public static void Init(string? directory = null)
    {
        _dir = directory ?? DefaultDirectory;
        try
        {
            System.IO.Directory.CreateDirectory(_dir);
            foreach (var f in System.IO.Directory.GetFiles(_dir, "segmentdeck-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-KeepDays)) File.Delete(f);
        }
        catch { /* logging must never take the app down */ }
    }

    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);
    public static void Error(string context, Exception ex) => Write(LogLevel.Error, $"{context}: {ex.GetType().Name}: {ex.Message}");

    public static void Write(LogLevel level, string message)
    {
        var now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {message}";
        if (_dir.Length > 0)
        {
            try
            {
                lock (Gate)
                    File.AppendAllText(Path.Combine(_dir, $"segmentdeck-{now:yyyy-MM-dd}.log"), line + Environment.NewLine);
            }
            catch { }
        }
        try { Written?.Invoke(level, line); } catch { }
    }
}
