using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CuepointDeck.App.Views;
using CuepointDeck.Core.Library;
using CuepointDeck.Core.Logging;
using CuepointDeck.Core.Resolume;

namespace CuepointDeck.App.ViewModels;

public partial class SetlistEntry : ObservableObject
{
    public required Song Song { get; init; }
    public string Title => Song.Title;
    public string Detail => Song.Segments.Count == 0 ? "No segments yet" : Song.Segments.Count == 1 ? "1 segment" : $"{Song.Segments.Count} segments";
    [ObservableProperty] private int _number;
}

/// <summary>A song to add: a Resolume column on the song layer (linked to a library song when its clip matches one).</summary>
public sealed class SourceItem
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public Song? Song { get; init; }
    public ClipInfo? Clip { get; init; }
    public bool IsNew => Song is null;
}

/// <summary>
/// Setlists, edited in place in Edit mode (they used to have their own window). Every change saves at once, so there
/// is nothing to remember to save. Names are asked for in a sheet that explains a clash before you press Create.
/// </summary>
public partial class SetlistsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private bool _loading;

    public ObservableCollection<string> Names { get; } = new();
    [ObservableProperty] private string? _selectedName;
    [ObservableProperty] private string? _activeName;
    public ObservableCollection<SetlistEntry> Entries { get; } = new();
    [ObservableProperty] private SetlistEntry? _selectedEntry;
    public ObservableCollection<SourceItem> Sources { get; } = new();
    [ObservableProperty] private SourceItem? _selectedSource;
    [ObservableProperty] private string _sourceHeader = "";
    [ObservableProperty] private string _sourceSearch = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _summary = "";

    public SetlistsViewModel(AppServices services, ShellViewModel shell)
    {
        _services = services;
        _shell = shell;
        ActiveName = services.Settings.ActiveSetlist;
        Reload(services.Settings.ActiveSetlist);
    }

    partial void OnSourceSearchChanged(string value) => RefreshSources();

    /// <summary>The songs to add: Resolume's columns on the song layer, in column order. Falls back to the library
    /// files when Resolume is not connected.</summary>
    public void RefreshSources()
    {
        var keep = SelectedSource?.Title;
        var q = SourceSearch.Trim();
        Sources.Clear();
        var comp = _services.Connection.Composition;
        var layer = comp?.Layer(_services.Settings.SongLayer);
        if (comp is not null && layer is not null)
        {
            SourceHeader = $"Resolume · Layer {layer.Index}{(layer.Name.Length > 0 ? " · " + layer.Name : "")}";
            foreach (var clip in layer.Clips)
            {
                if (clip.IsEmpty) continue;
                var song = _services.Matches.SongForClip(clip.ClipId);
                var columnName = comp.Column(clip.Column)?.Name ?? "";
                var title = columnName.Length > 0 ? columnName : song?.Title ?? clip.Name;
                if (q.Length > 0 && !title.Contains(q, StringComparison.CurrentCultureIgnoreCase)) continue;
                Sources.Add(new SourceItem
                {
                    Title = title,
                    Detail = song is null ? $"Column {clip.Column} · new to the library" : $"Column {clip.Column} · {(song.Segments.Count == 0 ? "no segments yet" : song.Segments.Count + " segments")}",
                    Song = song,
                    Clip = clip,
                });
            }
        }
        else
        {
            SourceHeader = "Library · Resolume not connected";
            foreach (var song in _services.Library.Songs.OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase))
                if (q.Length == 0 || song.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                    Sources.Add(new SourceItem { Title = song.Title, Detail = song.Segments.Count == 0 ? "No segments yet" : $"{song.Segments.Count} segments", Song = song });
        }
        if (keep is not null) SelectedSource = Sources.FirstOrDefault(x => x.Title == keep);
    }

    public void Reload(string? select)
    {
        // Rebuilt only when the names really changed, so the sidebar keeps its selection after a save.
        var names = _services.Library.Setlists.Select(s => s.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (!names.SequenceEqual(Names))
        {
            _loading = true;
            Names.Clear();
            foreach (var n in names) Names.Add(n);
            _loading = false;
        }
        RefreshSources();
        SelectedName = select is not null && Names.Contains(select) ? select : SelectedName is not null && Names.Contains(SelectedName) ? SelectedName : null;
        LoadEntries();
    }

    public void RefreshActive()
    {
        ActiveName = _services.Settings.ActiveSetlist;
        IsActive = SelectedName is not null && string.Equals(SelectedName, ActiveName, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSelectedNameChanged(string? value) { if (!_loading) LoadEntries(); }

    private void LoadEntries()
    {
        var setlist = SelectedName is null ? null : _services.Library.GetSetlist(SelectedName);
        var songs = setlist is null ? new List<Song>()
                  : setlist.SongIds.Select(id => _services.Library.GetSong(id)).Where(s => s is not null).Select(s => s!).ToList();
        // Our own save comes back through here: when the running order on screen is already what was saved, leave it
        // (and its selection and "Saved" status) alone.
        if (songs.Count == Entries.Count && songs.Select((s, i) => ReferenceEquals(s, Entries[i].Song)).All(x => x))
        {
            RefreshActive();
            return;
        }
        var keepId = SelectedEntry?.Song.Id;
        var keepIndex = SelectedEntry is null ? -1 : Entries.IndexOf(SelectedEntry);
        Entries.Clear();
        foreach (var song in songs) Entries.Add(new SetlistEntry { Song = song });
        if (keepId is not null)
            SelectedEntry = keepIndex >= 0 && keepIndex < Entries.Count && Entries[keepIndex].Song.Id == keepId ? Entries[keepIndex]
                          : Entries.FirstOrDefault(e => e.Song.Id == keepId);
        Renumber();
        RefreshActive();
        Status = "";
    }

    private void Renumber()
    {
        for (int i = 0; i < Entries.Count; i++) Entries[i].Number = i + 1;
        Summary = Entries.Count == 0 ? "No songs yet" : Entries.Count == 1 ? "1 song" : $"{Entries.Count} songs";
    }

    private void Persist()
    {
        if (SelectedName is null) return;
        Renumber();
        var setlist = new Setlist { Name = SelectedName, SongIds = Entries.Select(e => e.Song.Id).ToList() };
        var r = _services.Library.SaveSetlist(setlist, overwrite: false);
        if (r.Status == SaveStatus.Conflict)
        {
            var choice = Alert.Show(
                $"“{SelectedName}” was changed on another computer",
                "Keep your version of the running order, or use theirs.",
                new AlertButton("theirs", "Use Their Version", AlertRole.Destructive | AlertRole.Leading),
                new AlertButton("mine", "Keep Mine", AlertRole.Default | AlertRole.Cancel));
            if (choice == "mine") r = _services.Library.SaveSetlist(setlist, overwrite: true);
            else { LoadEntries(); return; }
        }
        Status = r.Ok ? "Saved automatically" : $"Not saved: {r.Error}";
    }

    private string? ValidateName(string name, string? except)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "A name can't contain \\ / : * ? \" < > |";
        if (except is not null && string.Equals(name, except, StringComparison.OrdinalIgnoreCase)) return null;
        return _services.Library.GetSetlist(name) is not null ? "There's already a setlist with that name." : null;
    }

    // ------------------------------------------------------------------ setlist-level actions (sidebar and header)

    /// <summary>Asks for a name and creates an empty setlist. Returns its name, or null if cancelled.</summary>
    public string? CreateNew()
    {
        var name = PromptSheet.Ask("New Setlist", "Give it a name. The date is filled in to start you off.", $"{DateTime.Now:yyyy-MM-dd} ", "Create", n => ValidateName(n, null));
        if (string.IsNullOrWhiteSpace(name)) return null;
        var r = _services.Library.SaveSetlist(new Setlist { Name = name });
        if (!r.Ok) { Alert.Inform("The setlist couldn't be created", r.Error ?? ""); return null; }
        Reload(name);
        _shell.ShowToast($"Created {name}");
        return name;
    }

    public string? Rename(string? current)
    {
        if (current is null) return null;
        var name = PromptSheet.Ask("Rename Setlist", null, current, "Rename", n => ValidateName(n, current));
        if (string.IsNullOrWhiteSpace(name) || name == current) return null;
        var wasActive = string.Equals(current, _services.Settings.ActiveSetlist, StringComparison.OrdinalIgnoreCase);
        var r = _services.Library.RenameSetlist(current, name);
        if (!r.Ok) { Alert.Inform("The setlist couldn't be renamed", r.Error ?? ""); return null; }
        if (wasActive) { var s = _services.Settings.Clone(); s.ActiveSetlist = name; _ = _services.ApplySettingsAsync(s); }
        Reload(name);
        return name;
    }

    public string? Duplicate(string? current)
    {
        if (current is null) return null;
        var name = PromptSheet.Ask("Duplicate Setlist", "Name the copy. The songs and their order are copied.", current + " copy", "Duplicate", n => ValidateName(n, null));
        if (string.IsNullOrWhiteSpace(name)) return null;
        var r = _services.Library.DuplicateSetlist(current, name);
        if (!r.Ok) { Alert.Inform("The setlist couldn't be duplicated", r.Error ?? ""); return null; }
        Reload(name);
        return name;
    }

    public bool Delete(string? name)
    {
        if (name is null) return false;
        var choice = Alert.Show(
            $"Delete the setlist “{name}”?",
            "Its songs stay in the library; only this running order is removed. A backup copy stays in the library folder.",
            new AlertButton("delete", "Delete Setlist", AlertRole.Destructive | AlertRole.Leading),
            new AlertButton("cancel", "Cancel", AlertRole.Default | AlertRole.Cancel));
        if (choice != "delete") return false;
        _services.Library.DeleteSetlist(name);
        if (string.Equals(name, _services.Settings.ActiveSetlist, StringComparison.OrdinalIgnoreCase)) { var s = _services.Settings.Clone(); s.ActiveSetlist = null; _ = _services.ApplySettingsAsync(s); }
        Reload(null);
        _shell.ShowToast($"Deleted {name}");
        return true;
    }

    public void MakeActive(string? name)
    {
        if (name is null) return;
        var s = _services.Settings.Clone();
        s.ActiveSetlist = name;
        _ = _services.ApplySettingsAsync(s);
        ActiveName = name;
        IsActive = string.Equals(SelectedName, name, StringComparison.OrdinalIgnoreCase);
        _shell.ShowToast($"{name} is now on show");
    }

    [RelayCommand] private void UseInShow() => MakeActive(SelectedName);

    // ------------------------------------------------------------------ running order

    [RelayCommand]
    private void AddSong(SourceItem? source)
    {
        source ??= SelectedSource;
        if (SelectedName is null || source is null) return;
        var song = source.Song;
        if (song is null)
        {
            // A column whose clip has no song yet: create a stub in the library so the setlist can refer to it.
            var clip = source.Clip;
            if (clip is null) return;
            song = new Song
            {
                Title = source.Title,
                Clip = new ClipRef { FilePath = clip.FilePath, FileName = Path.GetFileName(clip.FilePath), ClipName = clip.Name },
                DurationMs = (long)Math.Round(clip.DurationMs),
            };
            var saved = _services.Library.SaveSong(song);
            if (!saved.Ok) { Alert.Inform($"“{song.Title}” couldn't be added to the library", saved.Error ?? ""); return; }
            Log.Info($"Song \"{song.Title}\" created from Resolume column {clip.Column} with no segments; build them in Edit mode");
        }
        var entry = new SetlistEntry { Song = song };
        Entries.Add(entry);
        SelectedEntry = entry;
        Persist();
        RefreshSources();
    }

    [RelayCommand]
    private void RemoveSong(SetlistEntry? entry)
    {
        entry ??= SelectedEntry;
        if (entry is null) return;
        var idx = Entries.IndexOf(entry);
        Entries.Remove(entry);
        SelectedEntry = Entries.Count == 0 ? null : Entries[Math.Min(idx, Entries.Count - 1)];
        Persist();
    }

    [RelayCommand] private void MoveUp(SetlistEntry? entry) => Move(entry ?? SelectedEntry, -1);
    [RelayCommand] private void MoveDown(SetlistEntry? entry) => Move(entry ?? SelectedEntry, 1);

    private void Move(SetlistEntry? entry, int delta)
    {
        if (entry is null) return;
        var idx = Entries.IndexOf(entry);
        var to = idx + delta;
        if (idx < 0 || to < 0 || to >= Entries.Count) return;
        Entries.Move(idx, to);
        SelectedEntry = entry;
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
