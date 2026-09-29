using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CuepointDeck.App.ViewModels;

namespace CuepointDeck.App.Views;

public partial class SetlistsWindow : Window
{
    private readonly SetlistsViewModel _vm;
    private Point _dragStart;
    private SetlistEntry? _dragging;

    public SetlistsWindow(AppServices services)
    {
        InitializeComponent();
        _vm = new SetlistsViewModel(services);
        DataContext = _vm;
        // Keep the column list live while the window is open (Resolume re-sends the composition on changes).
        Action refresh = () => Dispatcher.BeginInvoke(_vm.RefreshSources);
        services.MatchesChanged += refresh;
        Closed += (_, _) => services.MatchesChanged -= refresh;
    }

    private void Library_MouseDoubleClick(object sender, MouseButtonEventArgs e) => _vm.AddSongCommand.Execute(null);

    private void EntryList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragging = ItemAt(e.OriginalSource as DependencyObject);
    }

    private void EntryList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging is null || e.LeftButton != MouseButtonState.Pressed) return;
        var diff = e.GetPosition(null) - _dragStart;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _dragging;
        _dragging = null;
        DragDrop.DoDragDrop(EntryList, item, DragDropEffects.Move);
    }

    private void EntryList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(SetlistEntry)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void EntryList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(SetlistEntry)) is not SetlistEntry entry) return;
        var target = ItemAt(e.OriginalSource as DependencyObject);
        _vm.MoveEntry(entry, target);
    }

    private static SetlistEntry? ItemAt(DependencyObject? d)
    {
        while (d is not null and not ListBoxItem) d = VisualTreeHelper.GetParent(d);
        return (d as ListBoxItem)?.DataContext as SetlistEntry;
    }
}
