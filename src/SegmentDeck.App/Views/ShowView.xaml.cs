using System.Windows.Controls;
using System.Windows.Input;
using SegmentDeck.App.ViewModels;

namespace SegmentDeck.App.Views;

public partial class ShowView : UserControl
{
    private DateTime _lastSetlistDown;

    public ShowView() => InitializeComponent();

    private ShowViewModel? Vm => DataContext as ShowViewModel;

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || sender is not System.Windows.FrameworkElement { DataContext: SegmentCardViewModel card }) return;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        _ = Vm.FireDefaultAsync(card, shift);
        e.Handled = true;
    }

    private void SetlistSong_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Double-click launches (when enabled); single click just selects.
        if (Vm is null || sender is not System.Windows.FrameworkElement { DataContext: SetlistSongItem item }) return;
        Vm.SelectSetlistSong(item);
        var now = DateTime.Now;
        if ((now - _lastSetlistDown).TotalMilliseconds < 400 && Vm.CanLaunch) Vm.LaunchSelectedCommand.Execute(null);
        _lastSetlistDown = now;
    }

    private void SetlistSong_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
