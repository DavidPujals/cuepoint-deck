using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CuepointDeck.App;

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

/// <summary>Visible when a width (usually the window's ActualWidth) is below the threshold in ConverterParameter;
/// Invert flips it. Lets the top bar and headers drop to a second row or hide hints on small windows.</summary>
public sealed class WidthBelowConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var width = value is double d ? d : 0;
        var threshold = double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 0;
        var below = width < threshold;
        return (below ^ Invert) ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Max(value, ConverterParameter): sizes a scrolling page to its viewport but never below a working minimum.</summary>
public sealed class AtLeastConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var v = value is double d && !double.IsNaN(d) ? d : 0;
        var min = double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 0;
        return Math.Max(v, min);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Bool → !bool, both ways.</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
}

/// <summary>True when the value's text equals ConverterParameter. Two-way for radio-style pane pickers: checking one
/// writes the parameter back; unchecking writes nothing.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? parameter : Binding.DoNothing;
}

/// <summary>True when two bound values have the same text (a colour swatch and the segment's colour).</summary>
public sealed class SameTextMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length == 2 && string.Equals(values[0]?.ToString(), values[1]?.ToString(), StringComparison.OrdinalIgnoreCase);
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when a count is above zero (Invert: when it is zero). Drives empty states and badges.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var any = value is int n && n > 0;
        if (Invert) any = !any;
        return any ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>value − ConverterParameter, never below a floor ("280" or "280,60"; default floor 0). Gives a panel the
/// space left after its neighbours' minimums, so it shrinks (and scrolls inside) instead of pushing them off-screen.</summary>
public sealed class MinusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var v = value is double d && !double.IsNaN(d) ? d : 0;
        var parts = (parameter?.ToString() ?? "0").Split(',');
        var minus = double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 0;
        var floor = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0;
        return Math.Max(floor, v - minus);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when a width is below ConverterParameter: lets a row of buttons drop under its title on narrow panels.</summary>
public sealed class LessThanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d && d > 0 && double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && d < t;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
