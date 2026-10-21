using System.Windows;
using CuepointDeck.App.ViewModels;

namespace CuepointDeck.App.Views;

/// <summary>A normal, modeless window: changes apply as they are made, so there is no Save or Cancel.</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(AppServices services, ShellViewModel shell)
    {
        InitializeComponent();
        _vm = new SettingsViewModel(services, shell);
        DataContext = _vm;
        Closing += (_, _) =>
        {
            // A field still being typed in commits when the window closes, like any other click-away.
            if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox box)
                box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            _vm.FlushPending();
        };
        Closed += (_, _) => _vm.Detach();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; } };
    }

    public void SelectPane(string pane) => _vm.Pane = pane;
}
