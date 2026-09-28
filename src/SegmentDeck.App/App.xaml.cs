using System.Windows;
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

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error($"Unhandled exception: {args.ExceptionObject}");

        Services = new AppServices();
        Services.Start();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.StopAsync().GetAwaiter().GetResult();
        Log.Info("Segment Deck closed");
        base.OnExit(e);
    }
}
