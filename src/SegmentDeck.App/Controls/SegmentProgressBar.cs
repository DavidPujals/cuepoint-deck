using System.Windows;
using System.Windows.Media;

namespace SegmentDeck.App.Controls;

/// <summary>Whole-clip progress bar with segment boundaries drawn as ticks. Cheap to redraw at 30 Hz.</summary>
public sealed class SegmentProgressBar : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(SegmentProgressBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TicksProperty =
        DependencyProperty.Register(nameof(Ticks), typeof(IReadOnlyList<double>), typeof(SegmentProgressBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TickColorsProperty =
        DependencyProperty.Register(nameof(TickColors), typeof(IReadOnlyList<Brush>), typeof(SegmentProgressBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(SegmentProgressBar), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public IReadOnlyList<double>? Ticks { get => (IReadOnlyList<double>?)GetValue(TicksProperty); set => SetValue(TicksProperty, value); }
    public IReadOnlyList<Brush>? TickColors { get => (IReadOnlyList<Brush>?)GetValue(TickColorsProperty); set => SetValue(TickColorsProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    private static readonly Brush Track = Frozen(Color.FromRgb(0x11, 0x15, 0x1A));
    private static readonly Brush TickBrush = Frozen(Color.FromRgb(0xE8, 0xED, 0xF2));
    private static readonly Pen HeadPen = FrozenPen(Colors.White, 2);

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double w) { var p = new Pen(new SolidColorBrush(c), w); p.Freeze(); return p; }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var radius = h / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), radius, radius);

        var p = Math.Clamp(Progress, 0, 1);
        if (p > 0) dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, Math.Max(h, w * p), h), radius, radius);

        var ticks = Ticks;
        if (ticks is not null)
        {
            for (int i = 0; i < ticks.Count; i++)
            {
                var x = Math.Round(ticks[i] * w);
                var brush = TickColors is { } colors && i < colors.Count ? colors[i] : TickBrush;
                dc.DrawRectangle(brush, null, new Rect(x - 1, 0, 2, h));
            }
        }

        if (p > 0) { var x = w * p; dc.DrawLine(HeadPen, new Point(x, -2), new Point(x, h + 2)); }
    }
}
