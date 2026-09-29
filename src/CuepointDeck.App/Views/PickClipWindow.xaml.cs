using System.Windows;
using System.Windows.Input;
using CuepointDeck.App.ViewModels;

namespace CuepointDeck.App.Views;

public partial class PickClipWindow : Window
{
    public CompositionClipItem? Selected { get; private set; }

    public PickClipWindow(List<CompositionClipItem> clips, string? hint = null)
    {
        InitializeComponent();
        if (hint is not null) Hint.Text = hint;
        List.ItemsSource = clips;
        if (clips.Count > 0) List.SelectedIndex = 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) { Selected = List.SelectedItem as CompositionClipItem; DialogResult = Selected is not null; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Ok_Click(sender, e);
}
