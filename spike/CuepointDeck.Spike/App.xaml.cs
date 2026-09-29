using System.Windows;

namespace CuepointDeck.Spike;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SpikeLog.Init();
        DispatcherUnhandledException += (_, args) =>
        {
            SpikeLog.Write($"ERROR unhandled: {args.Exception}");
            args.Handled = true;
        };
    }
}
