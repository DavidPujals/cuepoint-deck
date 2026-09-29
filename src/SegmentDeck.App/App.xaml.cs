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
        ArmDebugOcr();
        ArmDebugUpdateTest();
    }

    /// <summary>Test aid: SEGMENTDECK_UPDATE_TEST=check logs what the updater finds; =install also downloads and swaps
    /// the exe, then exits, so the update path can be proven against the real GitHub release without clicking.</summary>
    private static void ArmDebugUpdateTest()
    {
        var mode = Environment.GetEnvironmentVariable("SEGMENTDECK_UPDATE_TEST");
        if (string.IsNullOrWhiteSpace(mode)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var info = await SegmentDeck.App.Services.UpdateService.CheckAsync();
                Log.Info($"Update test: running {SegmentDeck.App.Services.UpdateService.Format(SegmentDeck.App.Services.UpdateService.CurrentVersion)}, canSelfUpdate={SegmentDeck.App.Services.UpdateService.CanSelfUpdate}, latest={(info is null ? "none newer" : $"v{SegmentDeck.App.Services.UpdateService.Format(info.Version)} at {info.DownloadUrl}")}");
                if (string.Equals(mode, "install", StringComparison.OrdinalIgnoreCase) && info is not null && SegmentDeck.App.Services.UpdateService.CanSelfUpdate)
                {
                    var last = -1;
                    var progress = new Progress<double>(p => { var pct = (int)(p * 100); if (pct / 25 != last / 25) { last = pct; Log.Info($"Update test: downloaded {pct}%"); } });
                    await SegmentDeck.App.Services.UpdateService.DownloadAndInstallAsync(info, progress);
                    Log.Info("Update test: install finished");
                }
            }
            catch (Exception ex) { Log.Error("Update test failed", ex); }
            _ = Current.Dispatcher.BeginInvoke(() => Current.Shutdown());
        });
    }

    /// <summary>Test aid: SEGMENTDECK_OCR_TEST=<image;image…> logs what Windows OCR reads from each image.</summary>
    private static void ArmDebugOcr()
    {
        var images = Environment.GetEnvironmentVariable("SEGMENTDECK_OCR_TEST");
        if (string.IsNullOrWhiteSpace(images)) return;
        _ = Task.Run(async () =>
        {
            foreach (var image in images.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var lines = await SegmentDeck.App.Services.WindowsOcr.ReadLinesAsync(image.Trim());
                Log.Info($"OCR test {Path.GetFileName(image)} ({SegmentDeck.App.Services.WindowsOcr.LanguageTag}): {lines.Count} lines: {string.Join(" | ", lines)}  → note: \"{SegmentDeck.App.Services.WindowsOcr.ToLyricNote(lines)}\"");
            }
        });
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
        // Run the shutdown off the UI thread: awaiting it here would post its continuation back to this (blocked)
        // dispatcher and the process would never exit. Bounded so a stuck socket can't hold the app open either.
        var stopped = Task.Run(Services.StopAsync).Wait(TimeSpan.FromSeconds(3));
        Log.Info(stopped ? "Segment Deck closed" : "Segment Deck closed (shutdown timed out)");
        base.OnExit(e);
    }
}
