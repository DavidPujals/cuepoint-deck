using System.Windows;
using System.Windows.Controls;

namespace CuepointDeck.App.Views;

[Flags]
public enum AlertRole
{
    None = 0,
    /// <summary>Return presses it; drawn prominent. Never put this on a destructive button.</summary>
    Default = 1,
    /// <summary>Esc presses it; also the result if the sheet is closed any other way.</summary>
    Cancel = 2,
    /// <summary>Red label: the button deletes, discards or overwrites something.</summary>
    Destructive = 4,
    /// <summary>Sits apart at the leading edge (for example "Don't Save"), away from the default button.</summary>
    Leading = 8,
}

public sealed record AlertButton(string Id, string Label, AlertRole Role = AlertRole.None);

/// <summary>
/// An alert in the Human Interface Guidelines' shape: a title that states the situation or asks the question, an
/// optional sentence of context, and buttons named for what they do ("Save", "Delete Song", "Quit"), never Yes/No/OK.
/// The default button sits at the trailing edge; Cancel is next to it; a destructive alternative sits apart at the
/// leading edge. Shown as a sheet on the window that asked.
/// </summary>
public partial class Alert : Window
{
    private string _result;

    private Alert(string title, string message, string? suppressLabel, IReadOnlyList<AlertButton> buttons)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        if (suppressLabel is not null) { Suppress.Content = suppressLabel; Suppress.Visibility = Visibility.Visible; }

        _result = buttons.FirstOrDefault(b => b.Role.HasFlag(AlertRole.Cancel))?.Id ?? buttons.Last().Id;
        foreach (var spec in buttons)
        {
            var button = new Button
            {
                Content = spec.Label,
                MinWidth = 92,
                Focusable = true,
                IsDefault = spec.Role.HasFlag(AlertRole.Default),
                IsCancel = spec.Role.HasFlag(AlertRole.Cancel),
            };
            if (spec.Role.HasFlag(AlertRole.Default)) button.Style = (Style)FindResource("PrimaryButton");
            else if (spec.Role.HasFlag(AlertRole.Destructive)) button.Style = (Style)FindResource("DangerButton");
            button.Focusable = true;
            button.Click += (_, _) => { _result = spec.Id; DialogResult = true; };
            if (spec.Role.HasFlag(AlertRole.Leading)) { button.Margin = new Thickness(0, 0, 8, 0); Leading.Children.Add(button); }
            else { button.Margin = new Thickness(8, 0, 0, 0); Trailing.Children.Add(button); }
            if (button.IsDefault) Loaded += (_, _) => button.Focus();
        }
    }

    /// <summary>Shows the alert and returns the Id of the button chosen (Cancel's Id if dismissed).</summary>
    public static string Show(string title, string message, params AlertButton[] buttons)
        => Show(title, message, null, out _, buttons);

    /// <summary>As <see cref="Show(string,string,AlertButton[])"/>, with a "Don't ask again"-style checkbox.</summary>
    public static string Show(string title, string message, string? suppressLabel, out bool suppressed, params AlertButton[] buttons)
    {
        if (buttons.Length == 0) buttons = new[] { new AlertButton("ok", "OK", AlertRole.Default | AlertRole.Cancel) };
        var alert = new Alert(title, message, suppressLabel, buttons);
        Sheet.Present(alert);
        suppressed = alert.Suppress.IsChecked == true;
        return alert._result;
    }

    /// <summary>A notice with a single "OK".</summary>
    public static void Inform(string title, string message) => Show(title, message, new AlertButton("ok", "OK", AlertRole.Default | AlertRole.Cancel));
}
