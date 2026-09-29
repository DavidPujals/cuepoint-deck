using System.Windows.Controls;
using System.Windows.Input;
using CuepointDeck.App.ViewModels;

namespace CuepointDeck.App.Views;

public partial class ShowView : UserControl
{
    public ShowView() => InitializeComponent();

    private ShowViewModel? Vm => DataContext as ShowViewModel;

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || sender is not System.Windows.FrameworkElement { DataContext: SegmentCardViewModel card }) return;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        _ = Vm.FireDefaultAsync(card, shift);
        e.Handled = true;
    }
}
