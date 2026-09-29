using System.Globalization;

namespace CuepointDeck.Core;

/// <summary>Time shown the way Resolume shows it: hh:mm:ss:ff at the clip's frame rate. Milliseconds stay the unit on disk and on the wire.</summary>
public static class Timecode
{
    public const double DefaultFps = 25;

    public static string Format(double ms, double fps = DefaultFps)
    {
        if (double.IsNaN(ms) || double.IsInfinity(ms)) return "--:--:--:--";
        if (fps <= 0) fps = DefaultFps;
        ms = Math.Max(0, ms);
        long totalMs = (long)Math.Floor(ms + 0.0001);
        long h = totalMs / 3_600_000, m = totalMs / 60_000 % 60, s = totalMs / 1000 % 60;
        int ff = (int)Math.Floor((totalMs % 1000) / 1000.0 * fps + 1e-6);
        int maxFrame = Math.Max(1, (int)Math.Ceiling(fps)) - 1;
        if (ff > maxFrame) ff = maxFrame;
        return $"{h:00}:{m:00}:{s:00}:{ff:00}";
    }

    /// <summary>Short form without hours for headers: mm:ss:ff.</summary>
    public static string FormatShort(double ms, double fps = DefaultFps)
    {
        var full = Format(ms, fps);
        return full.StartsWith("00:") ? full[3..] : full;
    }

    /// <summary>Accepts hh:mm:ss:ff, mm:ss:ff, mm:ss.fff, ss.fff and plain seconds.</summary>
    public static bool TryParse(string text, double fps, out long ms)
    {
        ms = 0;
        if (fps <= 0) fps = DefaultFps;
        text = text.Trim();
        if (text.Length == 0) return false;
        var parts = text.Split(':');
        var inv = CultureInfo.InvariantCulture;
        try
        {
            if (parts.Length >= 3 && !text.Contains('.'))
            {
                // frames form: [hh:]mm:ss:ff
                int ff = int.Parse(parts[^1], inv);
                int s = int.Parse(parts[^2], inv);
                int m = int.Parse(parts[^3], inv);
                int h = parts.Length >= 4 ? int.Parse(parts[^4], inv) : 0;
                if (ff < 0 || s < 0 || m < 0 || h < 0) return false;
                ms = (long)Math.Round(((h * 3600L + m * 60L + s) * 1000.0) + ff / fps * 1000.0);
                return true;
            }
            double total = 0;
            foreach (var part in parts)
            {
                if (!double.TryParse(part, NumberStyles.Float, inv, out var v) || v < 0) return false;
                total = total * 60 + v;
            }
            ms = (long)Math.Round(total * 1000);
            return true;
        }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }

    public static double FrameMs(double fps) => 1000.0 / (fps > 0 ? fps : DefaultFps);
}
