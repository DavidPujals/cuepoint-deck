using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CuepointDeck.App.ViewModels;

namespace CuepointDeck.App;

public partial class MainWindow : Window, Views.IDimmable
{
    private readonly ShellViewModel _vm;
    private bool _quitConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new ShellViewModel(App.Services, Dispatcher);
        DataContext = _vm;
        _vm.PropertyChanged += (_, e) =>
        {
            // Back in Show mode nothing should hold focus, so number keys reach the window.
            if (e.PropertyName == nameof(ShellViewModel.IsShowMode) && _vm.IsShowMode)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => Focus());
        };
        Loaded += (_, _) => Focus();
        SizeChanged += (_, _) => UpdateDensity();
    }

    /// <summary>Short windows give the trigger cards the room: under 720 px the toolbar and song header condense.</summary>
    private void UpdateDensity() => _vm.IsCompact = ActualHeight < 720;

    public void SetDimmed(bool dimmed) => DimLayer.Visibility = dimmed ? Visibility.Visible : Visibility.Collapsed;

    private void Window_Activated(object sender, EventArgs e)
    {
        _vm.OnWindowActivated();
        // Keys only arrive when something in this window has keyboard focus; give it to the window itself if nothing has it.
        if (!IsKeyboardFocusWithin) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => Focus());
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Shortcuts only while this window has focus; nothing global, because Resolume uses the same keys.
        var focused = Keyboard.FocusedElement;
        var inTextBox = focused is TextBox or PasswordBox || focused is ComboBox { IsEditable: true } || focused is DependencyObject d && FindParent<TextBox>(d) is not null;
        if (_vm.HandleKey(e, inTextBox)) e.Handled = true;
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void SetlistSong_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // A click brings the song up: its column is triggered in Resolume and the cards follow.
        if (sender is not FrameworkElement { DataContext: SetlistSongItem item }) return;
        _ = _vm.Show.LaunchItemAsync(item);
        e.Handled = true;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_quitConfirmed)
        {
            // Unsaved edits first (Save / Don't Save / Cancel), then the quit question itself.
            if (_vm.Edit is not null && !_vm.IsShowMode && !_vm.Edit.CanLeave()) { e.Cancel = true; return; }
            var headless = Environment.GetEnvironmentVariable("CUEPOINTDECK_SCREENSHOT") is { Length: > 0 }
                        || Environment.GetEnvironmentVariable("CUEPOINTDECK_UPDATE_TEST") is { Length: > 0 }
                        || App.SkipQuitConfirmation;
            if (!headless && !_vm.ConfirmQuit()) { e.Cancel = true; return; }
            _quitConfirmed = true;
        }
        _vm.Shutdown();
    }
}
