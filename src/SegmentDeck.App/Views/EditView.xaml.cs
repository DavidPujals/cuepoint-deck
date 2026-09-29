using System.Windows.Controls;
using System.Windows.Input;
using SegmentDeck.App.ViewModels;

namespace SegmentDeck.App.Views;

public partial class EditView : UserControl
{
    public EditView() => InitializeComponent();

    private EditViewModel? Vm => DataContext as EditViewModel;

    private void ScrubSlider_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Left: Vm.ScrubStep(-1, shift); e.Handled = true; break;
            case Key.Right: Vm.ScrubStep(1, shift); e.Handled = true; break;
            case Key.M: Vm.MarkAtScrubberCommand.Execute(null); e.Handled = true; break;
        }
    }

    private void Frame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || sender is not System.Windows.FrameworkElement { DataContext: FilmstripFrame frame }) return;
        Vm.ScrubTo(frame);
        ScrubSlider.Focus();
    }
}
