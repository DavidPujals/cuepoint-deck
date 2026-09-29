using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SegmentDeck.Core.Library;

namespace SegmentDeck.App.ViewModels;

public sealed class SetlistEntry
{
    public required Song Song { get; init; }
    public string Title => Song.Title;
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
    public ObservableCollection<SongListItem> LibrarySongs { get; } = new();
    [ObservableProperty] private SongListItem? _selectedLibrarySong;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isActive;

    public SetlistsViewModel(AppServices services)
    {
        _services = services;
        Reload(services.Settings.ActiveSetlist);
    }

    private void Reload(string? select)
    {
        _loading = true;
        Names.Clear();
        foreach (var s in _services.Library.Setlists) Names.Add(s.Name);
        LibrarySongs.Clear();
        foreach (var s in _services.Library.Songs) LibrarySongs.Add(new SongListItem { Song = s });
        _loading = false;
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
            if (Views.DarkMessageBox.Show($"\"{SelectedName}\" changed on disk (probably from the other PC). Overwrite it with this version?", "Segment Deck", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
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
        if (Views.DarkMessageBox.Show($"Delete setlist \"{SelectedName}\"? A .bak copy is kept.", "Segment Deck", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
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
        if (SelectedName is null || SelectedLibrarySong is null) return;
        Entries.Add(new SetlistEntry { Song = SelectedLibrarySong.Song });
        Persist();
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
