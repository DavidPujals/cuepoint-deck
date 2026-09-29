using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SegmentDeck.App;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var has = value is not null && !(value is string s && s.Length == 0);
        if (Invert) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"#RRGGBB" → SolidColorBrush, with an optional opacity in the parameter.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hex = value as string ?? "#4A90D9";
        var opacity = parameter is string p && double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var o) ? o : 1.0;
        var key = hex + "|" + opacity;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var b)) return b;
            Color c;
            try { c = (Color)ColorConverter.ConvertFromString(hex); } catch { c = Color.FromRgb(0x4A, 0x90, 0xD9); }
            var brush = new SolidColorBrush(c) { Opacity = opacity };
            brush.Freeze();
            Cache[key] = brush;
            return brush;
        }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Multiplies a double by the parameter (card scale → pixel sizes).</summary>
public sealed class ScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var scale = value is double d ? d : 1.0;
        var basePx = parameter is string p && double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? b : 240;
        return scale * basePx;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Width:height ratio of a bitmap, for <see cref="Controls.AspectBox"/>. Anything that is not a bitmap
/// (no thumbnail yet) gives 16:9 so placeholders keep a sensible shape.</summary>
public sealed class ImageAspectConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is System.Windows.Media.Imaging.BitmapSource { PixelHeight: > 0 } b ? b.PixelWidth / (double)b.PixelHeight : Controls.AspectBox.DefaultRatio;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
