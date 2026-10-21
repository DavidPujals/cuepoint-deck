using System.Windows;
using System.Windows.Controls;

namespace CuepointDeck.App.Views;

/// <summary>Asks for one line of text (a setlist name). Problems show under the field as you type, and the action
/// button stays disabled until the name is usable, instead of failing after the sheet has closed.</summary>
public partial class PromptSheet : Window
{
    private readonly Func<string, string?>? _validate;

    private PromptSheet(string title, string? message, string initial, string actionLabel, Func<string, string?>? validate)
    {
        InitializeComponent();
        _validate = validate;
        TitleText.Text = title;
        MessageText.Text = message ?? "";
        MessageText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        ActionButton.Content = actionLabel;
        Box.Text = initial;
        Loaded += (_, _) => { Box.Focus(); Box.SelectAll(); };
        Validate();
    }

    public static string? Ask(string title, string? message, string initial, string actionLabel, Func<string, string?>? validate = null)
    {
        var sheet = new PromptSheet(title, message, initial, actionLabel, validate);
        return Sheet.Present(sheet) == true ? sheet.Box.Text.Trim() : null;
    }

    private void Box_TextChanged(object sender, TextChangedEventArgs e) => Validate();

    private void Validate()
    {
        var text = Box.Text.Trim();
        var error = text.Length == 0 ? null : _validate?.Invoke(text);
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        ActionButton.IsEnabled = text.Length > 0 && error is null;
    }

    private void Action_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
