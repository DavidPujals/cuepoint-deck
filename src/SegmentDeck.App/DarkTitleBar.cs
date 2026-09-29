using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SegmentDeck.App;

/// <summary>Asks Windows 10/11 for the dark title bar on a window. Applied to every window the app opens.</summary>
public static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;

    public static void Apply(Window window)
    {
        void Set()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref on, sizeof(int));
            // Windows 11: match the panel colour exactly (COLORREF is 0x00BBGGRR). Ignored on Windows 10.
            int colour = 0x00282221;
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref colour, sizeof(int));
        }
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Set();
        else window.SourceInitialized += (_, _) => Set();
    }

    /// <summary>Hooks every Window in the process, including dialogs created later.</summary>
    public static void ApplyToAllWindows()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => { if (s is Window w) Apply(w); }));
    }
}
