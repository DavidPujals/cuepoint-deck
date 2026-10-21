using System.Collections.Concurrent;
using System.Text;

namespace CuepointDeck.Core.Logging;

public enum LogLevel { Info, Warn, Error }

/// <summary>Daily rolling text log in %AppData%\CuepointDeck\logs\cuepointdeck-yyyy-MM-dd.log, kept 14 days.
/// Thread-safe. Every line is also raised on <see cref="Written"/> for the status area. Never throws.
/// Lines are written by one background thread that keeps the file open and flushes after each batch, so callers
/// (the trigger timer, the Resolume socket, the UI) never wait on the disk.</summary>
public static class Log
{
    private static string _dir = "";
    private const int KeepDays = 14;
    private static readonly BlockingCollection<(DateTime Day, string Line)> Queue = new(new ConcurrentQueue<(DateTime, string)>(), 10_000);
    private static Thread? _writer;
    /// <summary>Lines queued but not yet written.</summary>
    private static int _pending;

    public static string Directory => _dir;
    public static event Action<LogLevel, string>? Written;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CuepointDeck", "logs");

    public static void Init(string? directory = null)
    {
        _dir = directory ?? DefaultDirectory;
        try
        {
            System.IO.Directory.CreateDirectory(_dir);
            foreach (var f in System.IO.Directory.GetFiles(_dir, "cuepointdeck-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-KeepDays)) File.Delete(f);
        }
        catch { /* logging must never take the app down */ }
        lock (Queue)
        {
            if (_writer is not null) return;
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "CuepointDeck log writer", Priority = ThreadPriority.BelowNormal };
            _writer.Start();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        }
    }

    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);
    public static void Error(string context, Exception ex) => Write(LogLevel.Error, $"{context}: {ex.GetType().Name}: {ex.Message}");

    public static void Write(LogLevel level, string message)
    {
        var now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {message}";
        if (_dir.Length > 0 && _writer is not null)
        {
            Interlocked.Increment(ref _pending);
            // A full queue (the disk stalled for a long time) drops the line rather than blocking the caller.
            if (!Queue.TryAdd((now.Date, line))) Interlocked.Decrement(ref _pending);
        }
        try { Written?.Invoke(level, line); } catch { }
    }

    /// <summary>Waits (briefly) until everything logged so far is on disk: on exit and before a crash report.</summary>
    public static void Flush(int timeoutMs = 2000)
    {
        if (_writer is null) return;
        var until = Environment.TickCount64 + timeoutMs;
        while (Volatile.Read(ref _pending) > 0 && Environment.TickCount64 < until) Thread.Sleep(5);
    }

    private static void WriterLoop()
    {
        StreamWriter? file = null;
        DateTime fileDay = default;
        var batch = new StringBuilder();
        while (true)
        {
            (DateTime Day, string Line) item;
            try { item = Queue.Take(); }
            catch { return; }
            try
            {
                int count = 0;
                do
                {
                    count++;
                    if (item.Day != fileDay || file is null)
                    {
                        WriteBatch(file, batch);
                        file?.Dispose();
                        file = null;
                        fileDay = item.Day;
                        file = Open(item.Day);
                    }
                    batch.Append(item.Line).Append(Environment.NewLine);
                } while (count < 500 && Queue.TryTake(out item));
                WriteBatch(file, batch);
                Interlocked.Add(ref _pending, -count);
            }
            catch
            {
                // The disk went away: drop this batch, reopen on the next line.
                batch.Clear();
                try { file?.Dispose(); } catch { }
                file = null;
                Volatile.Write(ref _pending, Queue.Count);
            }
        }
    }

    private static StreamWriter? Open(DateTime day)
    {
        try
        {
            var stream = new FileStream(Path.Combine(_dir, $"cuepointdeck-{day:yyyy-MM-dd}.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new StreamWriter(stream, new UTF8Encoding(false));
        }
        catch { return null; }
    }

    private static void WriteBatch(StreamWriter? file, StringBuilder batch)
    {
        if (batch.Length == 0) return;
        if (file is not null) { file.Write(batch.ToString()); file.Flush(); }
        batch.Clear();
    }
}
