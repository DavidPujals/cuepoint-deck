using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CuepointDeck.App.ViewModels;
using CuepointDeck.Core;
using CuepointDeck.Core.Library;

namespace CuepointDeck.App.Views;

/// <summary>Picks the songs to put in one transfer file. Ticks survive searching, so a long list can be narrowed and
/// picked from in passes.</summary>
public partial class ExportSongsSheet : Window
{
    private readonly List<ExportSongItem> _all;
    private List<ExportSongItem> _shown = new();

    private ExportSongsSheet(IEnumerable<Song> songs, IEnumerable<string>? preselect)
    {
        InitializeComponent();
        var pre = preselect?.ToHashSet() ?? new HashSet<string>();
        _all = songs.Select(s => new ExportSongItem { Song = s, Detail = DetailOf(s), IsChecked = pre.Contains(s.Id) }).ToList();
        foreach (var i in _all) i.PropertyChanged += Item_PropertyChanged;
        Filter();
        Loaded += (_, _) => Search.Focus();
    }

    /// <summary>The songs to export, or null when cancelled.</summary>
    public static List<Song>? Pick(IEnumerable<Song> songs, IEnumerable<string>? preselect = null, Window? owner = null)
    {
        var sheet = new ExportSongsSheet(songs, preselect);
        return Sheet.Present(sheet, owner) == true ? sheet._all.Where(i => i.IsChecked).Select(i => i.Song).ToList() : null;
    }

    private static string DetailOf(Song s)
    {
        var seg = s.Segments.Count == 0 ? "No segments yet" : s.Segments.Count == 1 ? "1 segment" : $"{s.Segments.Count} segments";
        var parts = new List<string> { seg };
        if (s.DurationMs > 0) parts.Add(Timecode.FormatShort(s.DurationMs, s.Fps));
        if (s.Artist.Length > 0) parts.Add(s.Artist);
        return string.Join("  ·  ", parts);
    }

    private void Filter()
    {
        var q = Search.Text.Trim();
        _shown = q.Length == 0 ? _all : _all.Where(i => i.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || i.Song.Artist.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        List.ItemsSource = _shown;
        EmptyText.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AllButton.Content = q.Length == 0 ? "Select All" : "Select Shown";
        UpdateCount();
    }

    private void UpdateCount()
    {
        var n = _all.Count(i => i.IsChecked);
        CountText.Text = n == 0 ? $"{_all.Count} songs" : $"{n} of {_all.Count} selected";
        ActionButton.IsEnabled = n > 0;
        ActionButton.Content = n <= 1 ? "Export…" : $"Export {n} Songs…";
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(ExportSongItem.IsChecked)) UpdateCount(); }
    private void Search_TextChanged(object sender, TextChangedEventArgs e) => Filter();
    private void All_Click(object sender, RoutedEventArgs e) { foreach (var i in _shown) i.IsChecked = true; }
    private void None_Click(object sender, RoutedEventArgs e) { foreach (var i in _all) i.IsChecked = false; }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_all.Any(i => i.IsChecked)) DialogResult = true;
    }
}
