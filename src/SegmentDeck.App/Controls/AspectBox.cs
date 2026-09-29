using System.Windows;
using System.Windows.Controls;

namespace SegmentDeck.App.Controls;

/// <summary>Sizes its child to a fixed width:height ratio so video frames are shown whole: no cropping and no
/// letterboxing whatever the clip's shape (16:9, or a 2688x512 wide stage canvas). Given a width it derives the
/// height; given only a height (a WrapPanel row) it derives the width.</summary>
public sealed class AspectBox : Decorator
{
    public const double DefaultRatio = 16.0 / 9.0;

    public static readonly DependencyProperty RatioProperty =
        DependencyProperty.Register(nameof(Ratio), typeof(double), typeof(AspectBox),
            new FrameworkPropertyMetadata(DefaultRatio, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Width divided by height of the content. Non-positive or non-finite values fall back to 16:9.</summary>
    public double Ratio { get => (double)GetValue(RatioProperty); set => SetValue(RatioProperty, value); }

    private double SafeRatio => Ratio > 0 && double.IsFinite(Ratio) ? Ratio : DefaultRatio;

    protected override Size MeasureOverride(Size available)
    {
        var r = SafeRatio;
        double w, h;
        var hasW = !double.IsInfinity(available.Width);
        var hasH = !double.IsInfinity(available.Height);
        if (hasW)
        {
            w = available.Width; h = w / r;
            if (hasH && h > available.Height) { h = available.Height; w = h * r; }
        }
        else if (hasH) { h = available.Height; w = h * r; }
        else
        {
            Child?.Measure(available);
            var d = Child?.DesiredSize ?? new Size();
            w = d.Width; h = w / r;
        }
        var size = new Size(Math.Max(0, w), Math.Max(0, h));
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size final)
    {
        Child?.Arrange(new Rect(final));
        return final;
    }
}
