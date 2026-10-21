using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;

namespace CuepointDeck.App.Views;

/// <summary>A window that can dim itself while a sheet is attached to it.</summary>
public interface IDimmable
{
    void SetDimmed(bool dimmed);
}

/// <summary>
/// Presents a window as a sheet: no title bar, attached to the top centre of its owner just below the toolbar, the
/// owner dimmed behind it, Return for the default button and Esc for Cancel. Alerts, prompts and pickers all use
/// it, so every modal question looks and behaves the same way and clearly belongs to the window that asked it.
/// </summary>
public static class Sheet
{
    /// <summary>The active window, or the main window: what a question should attach to.</summary>
    public static Window? FindOwner()
    {
        var app = Application.Current;
        if (app is null) return null;
        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsLoaded) ?? app.MainWindow;
    }

    public static bool? Present(Window sheet, Window? owner = null)
    {
        owner ??= FindOwner();
        if (owner is null || !owner.IsLoaded || ReferenceEquals(owner, sheet) || !owner.IsVisible) owner = null;
        if (owner is not null) sheet.Owner = owner;

        sheet.ShowInTaskbar = false;
        sheet.ResizeMode = ResizeMode.NoResize;
        sheet.SizeToContent = SizeToContent.Height;
        WindowChrome.SetWindowChrome(sheet, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            UseAeroCaptionButtons = false,
            CornerRadius = new CornerRadius(10),
        });

        if (owner is null) sheet.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        else
        {
            sheet.WindowStartupLocation = WindowStartupLocation.Manual;
            sheet.Left = -32000;   // placed once its size is known, so it never flashes in the wrong spot
            sheet.Top = -32000;
            sheet.Loaded += (_, _) => Place(sheet, owner);
        }

        var dim = owner as IDimmable;
        dim?.SetDimmed(true);
        try { return sheet.ShowDialog(); }
        finally { dim?.SetDimmed(false); }
    }

    private static void Place(Window sheet, Window owner)
    {
        try
        {
            var content = owner.Content as FrameworkElement ?? owner;
            var source = PresentationSource.FromVisual(owner);
            if (source?.CompositionTarget is null) return;
            var origin = source.CompositionTarget.TransformFromDevice.Transform(content.PointToScreen(new Point(0, 0)));
            // Drop out from under the toolbar on the main window; just below the title bar elsewhere.
            var inset = owner is MainWindow ? 54 : 10;
            sheet.Left = origin.X + Math.Max(0, (content.ActualWidth - sheet.ActualWidth) / 2);
            sheet.Top = origin.Y + inset;
        }
        catch
        {
            sheet.Left = owner.Left + 40;
            sheet.Top = owner.Top + 60;
        }
    }

    /// <summary>Buttons inside a sheet take keyboard focus (Tab, Space), unlike the main window's buttons.</summary>
    public static void MakeFocusable(DependencyObject root)
    {
        foreach (var b in Descendants<System.Windows.Controls.Primitives.ButtonBase>(root)) b.Focusable = true;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            if (child is T t) yield return t;
            foreach (var x in Descendants<T>(child)) yield return x;
        }
    }
}
