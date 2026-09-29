using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CuepointDeck.App.Views;

/// <summary>A message box in the app's own dark style. Same call shape as MessageBox.Show.</summary>
public partial class DarkMessageBox : Window
{
    private MessageBoxResult _result = MessageBoxResult.None;

    private DarkMessageBox(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        DarkTitleBar.Apply(this);

        void Add(string text, MessageBoxResult result, bool primary = false, bool danger = false)
        {
            var b = new Button { Content = text, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
            if (primary) b.Style = (Style)FindResource("PrimaryButton");
            if (danger) b.Style = (Style)FindResource("DangerButton");
            b.Click += (_, _) => { _result = result; DialogResult = true; };
            Buttons.Children.Add(b);
        }
        var warn = image is MessageBoxImage.Warning or MessageBoxImage.Error;
        switch (buttons)
        {
            case MessageBoxButton.OK: Add("OK", MessageBoxResult.OK, primary: true); break;
            case MessageBoxButton.OKCancel: Add("Cancel", MessageBoxResult.Cancel); Add("OK", MessageBoxResult.OK, primary: true); break;
            case MessageBoxButton.YesNo: Add("No", MessageBoxResult.No); Add("Yes", MessageBoxResult.Yes, primary: !warn, danger: warn); break;
            case MessageBoxButton.YesNoCancel: Add("Cancel", MessageBoxResult.Cancel); Add("No", MessageBoxResult.No); Add("Yes", MessageBoxResult.Yes, primary: !warn, danger: warn); break;
        }
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { _result = buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel; DialogResult = true; } };
    }

    public static MessageBoxResult Show(string message, string title = "Cuepoint Deck", MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        var box = new DarkMessageBox(message, title, buttons, image);
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        if (owner is not null && owner.IsLoaded) box.Owner = owner;
        box.ShowDialog();
        return box._result;
    }
}
