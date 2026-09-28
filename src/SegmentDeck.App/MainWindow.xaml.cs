using System.Collections.Specialized;
using System.Windows;
using SegmentDeck.App.ViewModels;

namespace SegmentDeck.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new ShellViewModel(App.Services, Dispatcher);
        DataContext = _vm;
        _vm.LogLines.CollectionChanged += (_, e) =>
        {
            // Defer: this handler runs before the ListBox has seen the change, and scrolling then throws.
            if (e.Action == NotifyCollectionChangedAction.Add)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                {
                    if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
                });
        };
    }

    private void Window_Activated(object sender, EventArgs e) => _vm.OnWindowActivated();
}
