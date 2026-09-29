using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SegmentDeck.App.ViewModels;

namespace SegmentDeck.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _vm;
    private DateTime _lastSetlistDown;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new ShellViewModel(App.Services, Dispatcher);
        DataContext = _vm;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.IsFollow))
                RoleBadge.Background = _vm.IsFollow ? (Brush)FindResource("WarnBrush") : (Brush)FindResource("Panel2Brush");
            // Back in Show mode nothing should hold focus, so number keys reach the window.
            if (e.PropertyName == nameof(ShellViewModel.IsShowMode) && _vm.IsShowMode)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => Focus());
        };
        Loaded += (_, _) => Focus();
    }

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
        var inTextBox = focused is TextBox or PasswordBox or ComboBox || focused is DependencyObject d && FindParent<TextBox>(d) is not null;
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

    private void SetlistSong_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Click selects; a quick second click launches (when launching is enabled).
        if (sender is not FrameworkElement { DataContext: SetlistSongItem item }) return;
        _vm.Show.SelectSetlistSong(item);
        var now = DateTime.Now;
        if ((now - _lastSetlistDown).TotalMilliseconds < 400 && _vm.Show.CanLaunch) _vm.Show.LaunchSelectedCommand.Execute(null);
        _lastSetlistDown = now;
        e.Handled = true;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_vm.Edit is not null && !_vm.IsShowMode && !_vm.Edit.CanLeave()) { e.Cancel = true; return; }
        _vm.Shutdown();
    }
}
