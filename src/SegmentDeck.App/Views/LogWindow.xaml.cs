using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using SegmentDeck.Core.Logging;

namespace SegmentDeck.App.Views;

public partial class LogWindow : Window
{
    public LogWindow(ObservableCollection<string> lines)
    {
        InitializeComponent();
        List.ItemsSource = lines;
        lines.CollectionChanged += OnChanged;
        Closed += (_, _) => lines.CollectionChanged -= OnChanged;
        Loaded += (_, _) => ScrollToEnd();
    }

    private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) Dispatcher.BeginInvoke(DispatcherPriority.Background, ScrollToEnd);
    }

    private void ScrollToEnd() { if (List.Items.Count > 0) List.ScrollIntoView(List.Items[^1]); }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", Log.Directory) { UseShellExecute = true }); } catch { }
    }
}
