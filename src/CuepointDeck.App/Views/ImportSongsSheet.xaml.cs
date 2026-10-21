using System.Windows;
using CuepointDeck.App.Services;
using CuepointDeck.App.ViewModels;
using CuepointDeck.Core.Library;

namespace CuepointDeck.App.Views;

/// <summary>
/// Reviews a transfer file before anything is saved: each song is added or replaces the one already here (or is
/// skipped), and is linked to a column on this campus's song layer, pre-filled by matching names.
/// </summary>
public partial class ImportSongsSheet : Window
{
    private readonly List<ImportSongItem> _items;

    private ImportSongsSheet(TransferFile file, List<ImportSongItem> items, bool connected, int songLayer)
    {
        InitializeComponent();
        _items = items;
        List.ItemsSource = items;

        var count = file.Songs.Count == 1 ? "1 song" : $"{file.Songs.Count} songs";
        var from = string.IsNullOrWhiteSpace(file.ExportedFrom) ? "" : $" from {file.ExportedFrom}";
        SourceText.Text = $"{count}{from}, exported {file.ExportedAt.ToLocalTime():d MMMM yyyy 'at' HH:mm}. Timings are kept exactly; each song plays from the column you choose here.";

        if (!connected)
        {
            OfflineNotice.Visibility = Visibility.Visible;
            OfflineText.Text = "Resolume isn't connected, so songs can't be linked to columns now. They come in as they are; link each one later in Edit mode with Link to Column, or import again once Resolume is running.";
        }
        HintText.Text = connected
            ? $"Columns are on layer {songLayer}, the song layer, and are matched to songs by name. Replacing a song keeps its title here and swaps in the imported segments."
            : "Replacing a song keeps its title and link here and swaps in the imported segments.";
        BulkLinks.Visibility = items.Count(i => i.IsExisting) > 1 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var i in items) i.Changed += Refresh;
        Refresh();
    }

    /// <summary>The rows as decided (Include and SelectedColumn on each), or null when cancelled.</summary>
    public static List<ImportSongItem>? Ask(TransferFile file, List<ImportSongItem> items, bool connected, int songLayer, Window? owner = null)
    {
        var sheet = new ImportSongsSheet(file, items, connected, songLayer);
        return Sheet.Present(sheet, owner) == true ? items : null;
    }

    private void Refresh()
    {
        SongTransferFlow.UpdateWarnings(_items);
        int add = _items.Count(i => i.Include && !i.IsExisting);
        int replace = _items.Count(i => i.Include && i.IsExisting);
        int skip = _items.Count(i => !i.Include);
        int unlinked = _items.Count(i => i.Include && i.SelectedColumn?.Clip is null && i.SelectedColumn?.KeepsLink != true);
        var parts = new List<string>();
        if (add > 0) parts.Add($"Adds {add}");
        if (replace > 0) parts.Add($"{(parts.Count == 0 ? "Replaces" : "replaces")} {replace}");
        if (skip > 0) parts.Add($"{(parts.Count == 0 ? "Skips" : "skips")} {skip}");
        if (unlinked > 0) parts.Add($"{unlinked} not linked");
        SummaryText.Text = string.Join("  ·  ", parts);
        ActionButton.IsEnabled = add + replace > 0;
        ActionButton.Content = add + replace <= 1 ? "Import" : $"Import {add + replace} Songs";
    }

    private void ReplaceAll_Click(object sender, RoutedEventArgs e) { foreach (var i in _items.Where(i => i.IsExisting)) i.Include = true; }
    private void SkipAll_Click(object sender, RoutedEventArgs e) { foreach (var i in _items.Where(i => i.IsExisting)) i.Include = false; }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Any(i => i.Include)) DialogResult = true;
    }
}
