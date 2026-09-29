using System.Windows;
using System.Windows.Input;

namespace CuepointDeck.App.Views;

public partial class PromptWindow : Window
{
    public string Value => Box.Text;

    public PromptWindow(string prompt, string initial)
    {
        InitializeComponent();
        PromptText.Text = prompt;
        Box.Text = initial;
        Loaded += (_, _) => { Box.Focus(); Box.CaretIndex = Box.Text.Length; };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Box_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) DialogResult = true; else if (e.Key == Key.Escape) DialogResult = false; }
}
