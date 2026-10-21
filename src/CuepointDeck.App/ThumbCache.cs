using System.IO;
using System.Windows.Media.Imaging;
using CuepointDeck.Core.Logging;

namespace CuepointDeck.App;

/// <summary>Loads thumbnail JPEGs once, decoded small, and hands out frozen bitmaps. Keyed by path and write time
/// so a regenerated thumbnail replaces the old one. Bounded by decoded size, least recently used first, so a long
/// service or a long scrub can't grow it and the deck's cards aren't thrown out all at once.
/// <see cref="Peek"/> answers from memory only, and <see cref="GetAsync"/> does the disk work on a worker thread, so
/// the UI never waits on a slow disk or a network share when a song comes up.</summary>
public static class ThumbCache
{
    private sealed record Entry(string Path, long Stamp, int Width, BitmapImage Image, long Bytes);

    private static readonly Dictionary<string, LinkedListNode<Entry>> Index = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<Entry> Recent = new();   // most recently used first
    private static long _bytes;
    /// <summary>About 300 of the deck's 480 px card thumbnails at 16:9, or far more stage-strip ones.</summary>
    private const long MaxBytes = 160L * 1024 * 1024;
    /// <summary>A few decodes at once: enough to fill a deck quickly without flooding a share or the thread pool.</summary>
    private static readonly SemaphoreSlim Loads = new(4, 4);

    /// <summary>The cached bitmap for this path and size, without touching the disk. Null when it isn't cached yet.</summary>
    public static BitmapImage? Peek(string? path, int decodeWidth = 480)
    {
        if (string.IsNullOrEmpty(path)) return null;
        lock (Index)
        {
            if (!Index.TryGetValue(path, out var node) || node.Value.Width != decodeWidth) return null;
            Touch(node);
            return node.Value.Image;
        }
    }

    /// <summary><see cref="Get"/> on a worker thread. The bitmap is frozen, so it can be used on the UI thread.</summary>
    public static async Task<BitmapImage?> GetAsync(string? path, int decodeWidth = 480)
    {
        if (string.IsNullOrEmpty(path)) return null;
        await Loads.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => Get(path, decodeWidth)).ConfigureAwait(false); }
        finally { Loads.Release(); }
    }

    /// <summary>Decodes an image on a worker thread without keeping it in the cache: for one-off frames.</summary>
    public static async Task<BitmapImage?> LoadUncachedAsync(string path, int decodeWidth)
    {
        await Loads.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => { try { return Decode(path, decodeWidth); } catch { return null; } }).ConfigureAwait(false); }
        finally { Loads.Release(); }
    }

    /// <summary>Checks the file on disk and returns its bitmap, decoding it only if it is new or changed.</summary>
    public static BitmapImage? Get(string? path, int decodeWidth = 480)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var stamp = info.LastWriteTimeUtc.Ticks;
            lock (Index)
            {
                if (Index.TryGetValue(path, out var node) && node.Value.Stamp == stamp && node.Value.Width == decodeWidth)
                {
                    Touch(node);
                    return node.Value.Image;
                }
            }
            var bmp = Decode(path, decodeWidth);
            Store(new Entry(path, stamp, decodeWidth, bmp, (long)bmp.PixelWidth * bmp.PixelHeight * 4));
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn($"Thumbnail could not be read: {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>A frozen bitmap, decoded at <paramref name="decodeWidth"/> (0 = the file's own size), file closed after.</summary>
    public static BitmapImage Decode(string path, int decodeWidth)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public static void Forget(string path)
    {
        lock (Index)
        {
            if (!Index.Remove(path, out var node)) return;
            Recent.Remove(node);
            _bytes -= node.Value.Bytes;
        }
    }

    private static void Store(Entry entry)
    {
        lock (Index)
        {
            if (Index.Remove(entry.Path, out var old)) { Recent.Remove(old); _bytes -= old.Value.Bytes; }
            Index[entry.Path] = Recent.AddFirst(entry);
            _bytes += entry.Bytes;
            while (_bytes > MaxBytes && Recent.Last is { } last && !ReferenceEquals(last.Value, entry))
            {
                Recent.RemoveLast();
                Index.Remove(last.Value.Path);
                _bytes -= last.Value.Bytes;
            }
        }
    }

    private static void Touch(LinkedListNode<Entry> node)
    {
        if (ReferenceEquals(Recent.First, node)) return;
        Recent.Remove(node);
        Recent.AddFirst(node);
    }
}
