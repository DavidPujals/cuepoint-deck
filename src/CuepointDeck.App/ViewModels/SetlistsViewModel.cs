using System.Collections.ObjectModel;
using System.Windows;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CuepointDeck.Core.Library;
using CuepointDeck.Core.Logging;
using CuepointDeck.Core.Resolume;

namespace CuepointDeck.App.ViewModels;

public sealed class SetlistEntry
{
    public required Song Song { get; init; }
    public string Title => Song.Title;
    public string Detail => Song.Segments.Count == 0 ? "no segments yet" : $"{Song.Segments.Count} segments";
}

/// <summary>A song to add: a Resolume column on the song layer (linked to a library song when its clip matches one).</summary>
public sealed class SourceItem
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public Song? Song { get; init; }
    public ClipInfo? Clip { get; init; }
}

/// <summary>Create, rename, duplicate, delete setlists; add, reorder and remove songs. Every change saves at once.</summary>
public partial class SetlistsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _loading;

    public ObservableCollection<string> Names { get; } = new();
    [ObservableProperty] private string? _selectedName;
    public ObservableCollection<SetlistEntry> Entries { get; } = new();
    [ObservableProperty] private SetlistEntry? _selectedEntry;
    public ObservableCollection<SourceItem> Sources { get; } = new();
    [ObservableProperty] private SourceItem? _selectedSource;
    [ObservableProperty] private string _sourceHeader = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isActive;

    public SetlistsViewModel(AppServices services)
    {
        _services = services;
        Reload(services.Settings.ActiveSetlist);
    }

    /// <summary>The song list on the right: Resolume's columns on the song layer, in column order. Falls back to the
    /// library files when Resolume is not connected.</summary>
    public void RefreshSources()
    {
        var keep = SelectedSource?.Title;
        Sources.Clear();
        var comp = _services.Connection.Composition;
        var layer = comp?.Layer(_services.Settings.SongLayer);
        if (comp is not null && layer is not null)
        {
            SourceHeader = $"RESOLUME COLUMNS (layer {layer.Index}{(layer.Name.Length > 0 ? " " + layer.Name : "")})";
            foreach (var clip in layer.Clips)
            {
                if (clip.IsEmpty) continue;
                var song = _services.Matches.SongForClip(clip.ClipId);
                var columnName = comp.Column(clip.Column)?.Name ?? "";
                var title = columnName.Length > 0 ? columnName : song?.Title ?? clip.Name;
                Sources.Add(new SourceItem
                {
                    Title = title,
                    Detail = song is null ? $"column {clip.Column} · not in the library yet (added as a new song)" : $"column {clip.Column} · {(song.Segments.Count == 0 ? "no segments yet" : song.Segments.Count + " segments")}",
                    Song = song,
                    Clip = clip,
                });
            }
            if (Sources.Count == 0) SourceHeader += " · no clips on this layer";
        }
        else
        {
            SourceHeader = "LIBRARY (Resolume not connected)";
            foreach (var song in _services.Library.Songs)
                Sources.Add(new SourceItem { Title = song.Title, Detail = song.Segments.Count == 0 ? "no segments yet" : $"{song.Segments.Count} segments", Song = song });
        }
        if (keep is not null) SelectedSource = Sources.FirstOrDefault(x => x.Title == keep);
    }

    private void Reload(string? select)
    {
        _loading = true;
        Names.Clear();
        foreach (var s in _services.Library.Setlists) Names.Add(s.Name);
        _loading = false;
        RefreshSources();
        SelectedName = select is not null && Names.Contains(select) ? select : Names.FirstOrDefault();
        LoadEntries();
    }

    partial void OnSelectedNameChanged(string? value) { if (!_loading) LoadEntries(); }

    private void LoadEntries()
    {
        Entries.Clear();
        var setlist = SelectedName is null ? null : _services.Library.GetSetlist(SelectedName);
        if (setlist is not null)
            foreach (var id in setlist.SongIds) { var song = _services.Library.GetSong(id); if (song is not null) Entries.Add(new SetlistEntry { Song = song }); }
        IsActive = SelectedName is not null && string.Equals(SelectedName, _services.Settings.ActiveSetlist, StringComparison.OrdinalIgnoreCase);
    }

    private void Persist()
    {
        if (SelectedName is null) return;
        var setlist = new Setlist { Name = SelectedName, SongIds = Entries.Select(e => e.Song.Id).ToList() };
        var r = _services.Library.SaveSetlist(setlist, overwrite: false);
        if (r.Status == SaveStatus.Conflict)
        {
            if (Views.DarkMessageBox.Show($"\"{SelectedName}\" changed on disk (probably from the other PC). Overwrite it with this version?", "Cuepoint Deck", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                r = _services.Library.SaveSetlist(setlist, overwrite: true);
            else { LoadEntries(); return; }
        }
        Status = r.Ok ? $"Saved {DateTime.Now:HH:mm:ss}" : $"Save failed: {r.Error}";
    }

    private static string? Ask(string prompt, string initial)
    {
        var w = new Views.PromptWindow(prompt, initial) { Owner = Application.Current.MainWindow };
        return w.ShowDialog() == true ? w.Value : null;
    }

    [RelayCommand]
    private void New()
    {
        var name = Ask("Name for the new setlist", $"{DateTime.Now:yyyy-MM-dd} ");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_services.Library.GetSetlist(name) is not null) { Status = "A setlist with that name already exists"; return; }
        var r = _services.Library.SaveSetlist(new Setlist { Name = name.Trim() });
        Status = r.Ok ? "Created" : $"Failed: {r.Error}";
        Reload(name.Trim());
    }

    [RelayCommand]
    private void Rename()
    {
        if (SelectedName is null) return;
        var name = Ask("New name", SelectedName);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == SelectedName) return;
        var r = _services.Library.RenameSetlist(SelectedName, name.Trim());
        if (r.Ok && IsActive) { var s = _services.Settings.Clone(); s.ActiveSetlist = name.Trim(); _ = _services.ApplySettingsAsync(s); }
        Status = r.Ok ? "Renamed" : $"Failed: {r.Error}";
        Reload(name.Trim());
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (SelectedName is null) return;
        var name = Ask("Name for the copy", SelectedName + " copy");
        if (string.IsNullOrWhiteSpace(name)) return;
        var r = _services.Library.DuplicateSetlist(SelectedName, name.Trim());
        Status = r.Ok ? "Duplicated" : $"Failed: {r.Error}";
        Reload(name.Trim());
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedName is null) return;
        if (Views.DarkMessageBox.Show($"Delete setlist \"{SelectedName}\"? A .bak copy is kept.", "Cuepoint Deck", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _services.Library.DeleteSetlist(SelectedName);
        if (IsActive) { var s = _services.Settings.Clone(); s.ActiveSetlist = null; _ = _services.ApplySettingsAsync(s); }
        Reload(null);
    }

    [RelayCommand]
    private void MakeActive()
    {
        if (SelectedName is null) return;
        var s = _services.Settings.Clone();
        s.ActiveSetlist = SelectedName;
        _ = _services.ApplySettingsAsync(s);
        IsActive = true;
        Status = $"\"{SelectedName}\" is now the active setlist in Show mode";
    }

    [RelayCommand]
    private void AddSong()
    {
        if (SelectedName is null || SelectedSource is null) return;
        var song = SelectedSource.Song;
        if (song is null)
        {
            // A column whose clip has no song yet: create a stub in the library so the setlist can refer to it.
            var clip = SelectedSource.Clip;
            if (clip is null) return;
            song = new Song
            {
                Title = SelectedSource.Title,
                Clip = new ClipRef { FilePath = clip.FilePath, FileName = Path.GetFileName(clip.FilePath), ClipName = clip.Name },
                DurationMs = (long)Math.Round(clip.DurationMs),
            };
            var saved = _services.Library.SaveSong(song);
            if (!saved.Ok) { Status = $"Could not create \"{song.Title}\": {saved.Error}"; return; }
            Log.Info($"Song \"{song.Title}\" created from Resolume column {clip.Column} with no segments; build them in Edit mode");
        }
        Entries.Add(new SetlistEntry { Song = song });
        Persist();
        RefreshSources();
    }

    [RelayCommand]
    private void RemoveSong()
    {
        if (SelectedEntry is null) return;
        Entries.Remove(SelectedEntry);
        Persist();
    }

    [RelayCommand]
    private void MoveUp() => Move(-1);
    [RelayCommand]
    private void MoveDown() => Move(1);

    private void Move(int delta)
    {
        if (SelectedEntry is null) return;
        var idx = Entries.IndexOf(SelectedEntry);
        var to = idx + delta;
        if (idx < 0 || to < 0 || to >= Entries.Count) return;
        Entries.Move(idx, to);
        Persist();
    }

    /// <summary>Drag-and-drop reorder from the view.</summary>
    public void MoveEntry(SetlistEntry entry, SetlistEntry? before)
    {
        var from = Entries.IndexOf(entry);
        var to = before is null ? Entries.Count - 1 : Entries.IndexOf(before);
        if (from < 0 || to < 0 || from == to) return;
        Entries.Move(from, to);
        SelectedEntry = entry;
        Persist();
    }
}
