using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CuepointDeck.App.ViewModels;

namespace CuepointDeck.App.Views;

/// <summary>Chooses a Resolume column to build a song from. Columns already in the library are marked, and choosing
/// one opens that song instead of making a duplicate.</summary>
public partial class PickClipSheet : Window
{
    private readonly List<CompositionClipItem> _all;
    public CompositionClipItem? Selected { get; private set; }

    /// <summary>Set when linking an existing song to a column rather than making a new one.</summary>
    private readonly string? _linkFor;
    private readonly long? _current;

    private PickClipSheet(List<CompositionClipItem> clips, string hint, string? linkFor, long? current)
    {
        InitializeComponent();
        _all = clips;
        _linkFor = linkFor;
        _current = current;
        HintText.Text = hint;
        if (linkFor is not null) { Title = "Link to a Resolume Column"; TitleText.Text = Title; }
        Filter();
        Loaded += (_, _) => { Search.Focus(); if (List.SelectedItem is not null) List.ScrollIntoView(List.SelectedItem); };
    }

    /// <summary>A column to build a song from or, with <paramref name="linkFor"/>, to link that song to.</summary>
    public static CompositionClipItem? Pick(List<CompositionClipItem> clips, string hint, string? linkFor = null, long? current = null)
    {
        var sheet = new PickClipSheet(clips, hint, linkFor, current);
        return Sheet.Present(sheet) == true ? sheet.Selected : null;
    }

    private void Filter()
    {
        var q = Search.Text.Trim();
        var shown = q.Length == 0 ? _all : _all.Where(c => c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Detail.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        List.ItemsSource = shown;
        // Start on the song's current column when linking, else the first column that is not in the library yet.
        List.SelectedItem = (_current is long id ? shown.FirstOrDefault(c => c.Clip.ClipId == id) : null)
                            ?? shown.FirstOrDefault(c => c.ExistingSong is null) ?? shown.FirstOrDefault();
        EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _all.Count == 0 ? "Resolume has no clips with a media file on the song layer. Check the Song layer in Settings, or that Resolume is connected." : "No columns match.";
        UpdateAction();
    }

    private void UpdateAction()
    {
        var item = List.SelectedItem as CompositionClipItem;
        ActionButton.IsEnabled = item is not null;
        ActionButton.Content = _linkFor is not null ? "Link" : item?.ExistingSong is not null ? "Open Song" : "Create Song";
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => Filter();
    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAction();
    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (List.SelectedItem is not null) Action_Click(sender, e); }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        Selected = List.SelectedItem as CompositionClipItem;
        if (Selected is not null) DialogResult = true;
    }
}
