using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SegmentDeck.Core.Logging;

namespace SegmentDeck.App;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Init();
        Log.Info($"Segment Deck {typeof(App).Assembly.GetName().Version} starting");
        SegmentDeck.App.Services.UpdateService.CleanupLeftovers();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error($"Unhandled exception: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        DarkTitleBar.ApplyToAllWindows();
        Services = new AppServices();
        Services.Start();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        ArmDebugScreenshot(window);
    }

    /// <summary>Test aid: SEGMENTDECK_SCREENSHOT=<png path> saves the window after a few seconds
    /// (SEGMENTDECK_SCREENSHOT_MODE=edit switches to Edit mode first). Harmless when unset.</summary>
    private static void ArmDebugScreenshot(MainWindow window)
    {
        var path = Environment.GetEnvironmentVariable("SEGMENTDECK_SCREENSHOT");
        if (string.IsNullOrWhiteSpace(path)) return;
        var delay = int.TryParse(Environment.GetEnvironmentVariable("SEGMENTDECK_SCREENSHOT_DELAY"), out var d) ? d : 8;
        var mode = Environment.GetEnvironmentVariable("SEGMENTDECK_SCREENSHOT_MODE");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                if (string.Equals(mode, "edit", StringComparison.OrdinalIgnoreCase) && window.DataContext is ViewModels.ShellViewModel shell)
                {
                    shell.EnterEdit(Services.CurrentSong ?? Services.Library.Songs.FirstOrDefault());
                    window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => { Thread.Sleep(1500); Save(window, path); });
                    return;
                }
                Save(window, path);
            }
            catch (Exception ex) { Log.Error("Screenshot", ex); }
        };
        timer.Start();

        static void Save(Window w, string file)
        {
            var dpi = VisualTreeHelper.GetDpi(w);
            var width = (int)(w.ActualWidth * dpi.DpiScaleX);
            var height = (int)(w.ActualHeight * dpi.DpiScaleY);
            var rtb = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rtb.Render(w);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var fs = File.Create(file);
            enc.Save(fs);
            Log.Info($"Screenshot saved to {file}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.StopAsync().GetAwaiter().GetResult();
        Log.Info("Segment Deck closed");
        base.OnExit(e);
    }
}
