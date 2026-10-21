using CuepointDeck.Core.Logging;
using CuepointDeck.Core.Storage;

namespace CuepointDeck.Core.Library;

public sealed record LibraryWarning(string File, string Message);

public enum SaveStatus { Saved, Conflict, Failed }
public sealed record SaveResult(SaveStatus Status, string? Error = null)
{
    public bool Ok => Status == SaveStatus.Saved;
}

/// <summary>The song library folder: songs/*.json, setlists/*.json, thumbs/&lt;songId&gt;/.
/// Loads everything defensively (a bad file is a warning, never a crash), writes safely, and notices
/// when a file changed on disk since it was loaded so the UI can ask before overwriting.</summary>
public sealed class SongLibrary
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Path, FileStamp Stamp)> _songFiles = new();
    private readonly Dictionary<string, (string Path, FileStamp Stamp)> _setlistFiles = new(StringComparer.OrdinalIgnoreCase);
    private List<Song> _songs = new();
    private List<Setlist> _setlists = new();
    private List<LibraryWarning> _warnings = new();

    public string RootPath { get; }
    public string SongsDir => Path.Combine(RootPath, "songs");
    public string ThumbsDir => Path.Combine(RootPath, "thumbs");
    public string SetlistsDir => Path.Combine(RootPath, "setlists");

    public IReadOnlyList<Song> Songs { get { lock (_gate) return _songs; } }
    public IReadOnlyList<Setlist> Setlists { get { lock (_gate) return _setlists; } }
    public IReadOnlyList<LibraryWarning> Warnings { get { lock (_gate) return _warnings; } }
    public bool IsReachable { get; private set; }
    public DateTime LastScan { get; private set; }

    /// <summary>Raised after every rescan or save. May fire on any thread.</summary>
    public event Action? Changed;

    public SongLibrary(string rootPath) => RootPath = rootPath;

    public bool EnsureFolders()
    {
        try
        {
            Directory.CreateDirectory(SongsDir);
            Directory.CreateDirectory(ThumbsDir);
            Directory.CreateDirectory(SetlistsDir);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Library folder {RootPath} could not be created", ex);
            return false;
        }
    }

    private readonly object _scanGate = new();
    /// <summary>Bumped by every save and delete, so a scan that read the folder before a write doesn't put the old
    /// content back over it.</summary>
    private long _writes;
    private void Wrote() => Interlocked.Increment(ref _writes);

    /// <summary>Brings the library up to date with the folder. Call on start, on window focus and on the refresh button.
    /// Only files whose size or time changed are read again; the rest keep their loaded objects. <see cref="Changed"/>
    /// is raised only when something actually changed, so focusing the window doesn't rebuild the deck for nothing.</summary>
    public void Rescan()
    {
        // One scan at a time: a focus scan and a refresh can otherwise interleave their results. A scan that raced a
        // save is simply run again (a couple of tries; then the next focus scan catches up).
        lock (_scanGate)
            for (int attempt = 0; attempt < 3 && !RescanCore(); attempt++) { }
    }

    /// <summary>Returns false (and changes nothing) when a save or delete happened while the folder was being read.</summary>
    private bool RescanCore()
    {
        var writesAtStart = Interlocked.Read(ref _writes);
        var songs = new List<Song>();
        var setlists = new List<Setlist>();
        var warnings = new List<LibraryWarning>();
        var songFiles = new Dictionary<string, (string, FileStamp)>();
        var setlistFiles = new Dictionary<string, (string, FileStamp)>(StringComparer.OrdinalIgnoreCase);

        // What is loaded now, by file path, to reuse anything unchanged on disk.
        Dictionary<string, (FileStamp Stamp, Song Song)> knownSongs;
        Dictionary<string, (FileStamp Stamp, Setlist Setlist)> knownSetlists;
        bool firstScan;
        lock (_gate)
        {
            firstScan = LastScan == default;
            knownSongs = new(StringComparer.OrdinalIgnoreCase);
            foreach (var song in _songs)
                if (_songFiles.TryGetValue(song.Id, out var f)) knownSongs[f.Path] = (f.Stamp, song);
            knownSetlists = new(StringComparer.OrdinalIgnoreCase);
            foreach (var setlist in _setlists)
                if (_setlistFiles.TryGetValue(setlist.Name, out var f)) knownSetlists[f.Path] = (f.Stamp, setlist);
        }
        int read = 0;

        var wasReachable = IsReachable;
        IsReachable = Directory.Exists(RootPath) || EnsureFolders();
        if (!IsReachable)
        {
            warnings.Add(new LibraryWarning(RootPath, "Library folder is not reachable"));
        }
        else
        {
            foreach (var (file, stamp) in JsonFiles(SongsDir))
            {
                try
                {
                    Song? song;
                    if (knownSongs.TryGetValue(file, out var known) && known.Stamp == stamp) song = known.Song;
                    else
                    {
                        song = Json.Deserialize<Song>(File.ReadAllText(file));
                        read++;
                        if (song is null || string.IsNullOrWhiteSpace(song.Id)) { warnings.Add(new LibraryWarning(file, "Not a song file (no id)")); continue; }
                        song.Segments ??= new List<Segment>();
                        song.Clip ??= new ClipRef();
                        song.SortSegments();
                    }
                    if (songFiles.ContainsKey(song.Id)) { warnings.Add(new LibraryWarning(file, $"Duplicate song id {song.Id}; skipped")); continue; }
                    songs.Add(song);
                    songFiles[song.Id] = (file, stamp);
                }
                catch (Exception ex)
                {
                    warnings.Add(new LibraryWarning(file, $"Skipped: {ex.Message}"));
                }
            }

            foreach (var (file, stamp) in JsonFiles(SetlistsDir))
            {
                try
                {
                    Setlist? setlist;
                    if (knownSetlists.TryGetValue(file, out var known) && known.Stamp == stamp) setlist = known.Setlist;
                    else
                    {
                        setlist = Json.Deserialize<Setlist>(File.ReadAllText(file));
                        read++;
                        if (setlist is null) { warnings.Add(new LibraryWarning(file, "Not a setlist file")); continue; }
                        if (string.IsNullOrWhiteSpace(setlist.Name)) setlist.Name = Path.GetFileNameWithoutExtension(file);
                        setlist.SongIds ??= new List<string>();
                    }
                    if (setlistFiles.ContainsKey(setlist.Name)) { warnings.Add(new LibraryWarning(file, $"Duplicate setlist name \"{setlist.Name}\"; skipped")); continue; }
                    setlists.Add(setlist);
                    setlistFiles[setlist.Name] = (file, stamp);
                }
                catch (Exception ex)
                {
                    warnings.Add(new LibraryWarning(file, $"Skipped: {ex.Message}"));
                }
            }
        }

        songs.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
        setlists.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

        bool changed;
        lock (_gate)
        {
            if (Interlocked.Read(ref _writes) != writesAtStart) return false;
            changed = firstScan || read > 0 || wasReachable != IsReachable
                      || !songs.SequenceEqual(_songs, ReferenceEqualityComparer.Instance)
                      || !setlists.SequenceEqual(_setlists, ReferenceEqualityComparer.Instance)
                      || !warnings.SequenceEqual(_warnings);
            LastScan = DateTime.Now;
            if (changed)
            {
                _songs = songs; _setlists = setlists; _warnings = warnings;
                _songFiles.Clear(); foreach (var kv in songFiles) _songFiles[kv.Key] = kv.Value;
                _setlistFiles.Clear(); foreach (var kv in setlistFiles) _setlistFiles[kv.Key] = kv.Value;
            }
        }
        if (!changed) return true;
        foreach (var w in warnings) Log.Warn($"Library: {w.Message} ({w.File})");
        Log.Info($"Library scanned: {songs.Count} songs, {setlists.Count} setlists, {warnings.Count} warnings, {read} file(s) read at {RootPath}");
        RaiseChanged();
        return true;
    }

    /// <summary>The folder's .json files with their stamps, taken from the directory listing itself (one round trip
    /// for the whole folder on a network share, rather than one per file).</summary>
    private static IEnumerable<(string Path, FileStamp Stamp)> JsonFiles(string dir)
    {
        var info = new DirectoryInfo(dir);
        if (!info.Exists) return Array.Empty<(string, FileStamp)>();
        // Explicit extension check so "song.json.bak" and temp files never load.
        return info.EnumerateFiles()
            .Where(f => string.Equals(f.Extension, ".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.FullName)
            .Select(f => (f.FullName, new FileStamp(f.LastWriteTimeUtc.Ticks, f.Length)))
            .ToList();
    }

    // ------------------------------------------------------------------ songs

    public Song? GetSong(string id) { lock (_gate) return _songs.FirstOrDefault(s => s.Id == id); }

    public string SongPath(string id) => Path.Combine(SongsDir, id + ".json");
    public string ThumbDir(string songId) => Path.Combine(ThumbsDir, songId);
    public string ResolveLibraryPath(string relative) => Path.Combine(RootPath, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>True when the song's file on disk is not the version we loaded (edited from another PC).</summary>
    public bool HasChangedOnDisk(string songId)
    {
        (string Path, FileStamp Stamp) known;
        lock (_gate) if (!_songFiles.TryGetValue(songId, out known)) return false;
        var now = FileStamp.TryOf(known.Path);
        return now is not null && now.Value != known.Stamp;
    }

    /// <summary>Saves a song. Returns Conflict (and writes nothing) when the file changed on disk since it was loaded,
    /// unless <paramref name="overwrite"/> is set.</summary>
    public SaveResult SaveSong(Song song, bool overwrite = false)
    {
        try
        {
            if (!EnsureFolders()) return new SaveResult(SaveStatus.Failed, "Library folder is not reachable");
            if (!overwrite && HasChangedOnDisk(song.Id)) return new SaveResult(SaveStatus.Conflict, "The song file changed on disk since it was loaded");

            song.SortSegments();
            song.UpdatedAt = DateTimeOffset.UtcNow;
            var path = SongPath(song.Id);
            var json = Json.Serialize(song);
            var stamp = SafeFile.WriteAllText(path, json);
            // The library keeps its own copy, so a caller that goes on editing its object (Edit mode does) can't change
            // what Show mode, matching and later scans see until it saves again.
            var stored = Json.Deserialize<Song>(json)!;

            Wrote();
            lock (_gate)
            {
                _songFiles[song.Id] = (path, stamp);
                var idx = _songs.FindIndex(s => s.Id == song.Id);
                var list = new List<Song>(_songs);
                if (idx >= 0) list[idx] = stored; else list.Add(stored);
                list.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
                _songs = list;
            }
            Log.Info($"Song saved: \"{song.Title}\" ({song.Segments.Count} segments) → {path}");
            RaiseChanged();
            return new SaveResult(SaveStatus.Saved);
        }
        catch (Exception ex)
        {
            Log.Error($"Saving song \"{song.Title}\"", ex);
            return new SaveResult(SaveStatus.Failed, ex.Message);
        }
    }

    /// <summary>Re-reads one song from disk, replacing the in-memory copy. Returns null if it cannot be read.</summary>
    public Song? ReloadSong(string id)
    {
        var path = SongPath(id);
        try
        {
            var song = Json.Deserialize<Song>(File.ReadAllText(path));
            if (song is null) return null;
            song.SortSegments();
            lock (_gate)
            {
                _songFiles[id] = (path, FileStamp.Of(path));
                var list = new List<Song>(_songs);
                var idx = list.FindIndex(s => s.Id == id);
                if (idx >= 0) list[idx] = song; else list.Add(song);
                _songs = list;
            }
            RaiseChanged();
            return song;
        }
        catch (Exception ex) { Log.Error($"Reloading song {id}", ex); return null; }
    }

    public bool DeleteSong(string id)
    {
        try
        {
            SafeFile.DeleteKeepingBackup(SongPath(id));
            Wrote();
            lock (_gate)
            {
                _songFiles.Remove(id);
                _songs = _songs.Where(s => s.Id != id).ToList();
            }
            Log.Info($"Song deleted: {id} (.bak kept)");
            RaiseChanged();
            return true;
        }
        catch (Exception ex) { Log.Error($"Deleting song {id}", ex); return false; }
    }

    // ------------------------------------------------------------------ setlists

    public Setlist? GetSetlist(string name) { lock (_gate) return _setlists.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)); }

    public string SetlistPath(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (safe.Length == 0) safe = "setlist";
        return Path.Combine(SetlistsDir, safe + ".json");
    }

    public bool SetlistChangedOnDisk(string name)
    {
        (string Path, FileStamp Stamp) known;
        lock (_gate) if (!_setlistFiles.TryGetValue(name, out known)) return false;
        var now = FileStamp.TryOf(known.Path);
        return now is not null && now.Value != known.Stamp;
    }

    public SaveResult SaveSetlist(Setlist setlist, bool overwrite = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(setlist.Name)) return new SaveResult(SaveStatus.Failed, "A setlist needs a name");
            if (!EnsureFolders()) return new SaveResult(SaveStatus.Failed, "Library folder is not reachable");
            if (!overwrite && SetlistChangedOnDisk(setlist.Name)) return new SaveResult(SaveStatus.Conflict, "The setlist file changed on disk since it was loaded");

            string path;
            lock (_gate) path = _setlistFiles.TryGetValue(setlist.Name, out var known) ? known.Path : SetlistPath(setlist.Name);
            var json = Json.Serialize(setlist);
            var stamp = SafeFile.WriteAllText(path, json);
            var stored = Json.Deserialize<Setlist>(json)!;   // the library's own copy, as for songs
            Wrote();
            lock (_gate)
            {
                _setlistFiles[setlist.Name] = (path, stamp);
                var list = _setlists.Where(s => !string.Equals(s.Name, setlist.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                list.Add(stored);
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
                _setlists = list;
            }
            Log.Info($"Setlist saved: \"{setlist.Name}\" ({setlist.SongIds.Count} songs)");
            RaiseChanged();
            return new SaveResult(SaveStatus.Saved);
        }
        catch (Exception ex)
        {
            Log.Error($"Saving setlist \"{setlist.Name}\"", ex);
            return new SaveResult(SaveStatus.Failed, ex.Message);
        }
    }

    public SaveResult RenameSetlist(string oldName, string newName)
    {
        var existing = GetSetlist(oldName);
        if (existing is null) return new SaveResult(SaveStatus.Failed, "Setlist not found");
        if (GetSetlist(newName) is not null) return new SaveResult(SaveStatus.Failed, "A setlist with that name already exists");
        var copy = new Setlist { Name = newName, SongIds = new List<string>(existing.SongIds) };
        var result = SaveSetlist(copy);
        if (result.Ok) DeleteSetlist(oldName);
        return result;
    }

    public SaveResult DuplicateSetlist(string name, string newName)
    {
        var existing = GetSetlist(name);
        if (existing is null) return new SaveResult(SaveStatus.Failed, "Setlist not found");
        if (GetSetlist(newName) is not null) return new SaveResult(SaveStatus.Failed, "A setlist with that name already exists");
        return SaveSetlist(new Setlist { Name = newName, SongIds = new List<string>(existing.SongIds) });
    }

    public bool DeleteSetlist(string name)
    {
        try
        {
            string? path;
            lock (_gate) path = _setlistFiles.TryGetValue(name, out var known) ? known.Path : null;
            if (path is not null) SafeFile.DeleteKeepingBackup(path);
            Wrote();
            lock (_gate)
            {
                _setlistFiles.Remove(name);
                _setlists = _setlists.Where(s => !string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            Log.Info($"Setlist deleted: \"{name}\" (.bak kept)");
            RaiseChanged();
            return true;
        }
        catch (Exception ex) { Log.Error($"Deleting setlist \"{name}\"", ex); return false; }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch (Exception ex) { Log.Error("Library Changed handler", ex); }
    }
}
