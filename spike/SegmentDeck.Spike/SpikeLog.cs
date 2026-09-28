using System.IO;

namespace SegmentDeck.Spike;

/// <summary>Daily log file in %AppData%\SegmentDeck\logs\spike-yyyy-MM-dd.log, pruned after 14 days.
/// Every line is also raised on <see cref="Line"/> so the window can mirror it.</summary>
public static class SpikeLog
{
    private static readonly object Gate = new();
    private static string _dir = "";

    public static string Dir => _dir;
    public static event Action<string>? Line;

    public static void Init()
    {
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SegmentDeck", "logs");
        try
        {
            Directory.CreateDirectory(_dir);
            foreach (var f in Directory.GetFiles(_dir, "spike-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
        }
        catch { /* logging must never take the app down */ }
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        try
        {
            lock (Gate)
                File.AppendAllText(Path.Combine(_dir, $"spike-{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine);
        }
        catch { }
        Line?.Invoke(line);
    }
}
