using System.IO;
using System.Windows.Media.Imaging;
using CuepointDeck.Core.Logging;

namespace CuepointDeck.App;

/// <summary>Loads thumbnail JPEGs once, decoded small, and hands out frozen bitmaps. Keyed by path and write time
/// so a regenerated thumbnail replaces the old one. Bounded so a long service cannot grow it.</summary>
public static class ThumbCache
{
    private static readonly Dictionary<string, (long Stamp, BitmapImage Image)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxEntries = 600;

    public static BitmapImage? Get(string? path, int decodeWidth = 480)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            if (!File.Exists(path)) return null;
            var stamp = File.GetLastWriteTimeUtc(path).Ticks ^ ((long)decodeWidth << 48);
            lock (Cache)
            {
                if (Cache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Image;
            }
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            lock (Cache)
            {
                if (Cache.Count >= MaxEntries) Cache.Clear();
                Cache[path] = (stamp, bmp);
            }
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn($"Thumbnail could not be read: {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    public static void Forget(string path) { lock (Cache) Cache.Remove(path); }
}
