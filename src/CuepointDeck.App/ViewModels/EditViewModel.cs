using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CuepointDeck.App.Views;
using CuepointDeck.Core;
using CuepointDeck.Core.Analysis;
using CuepointDeck.Core.Library;
using CuepointDeck.Core.Logging;
using CuepointDeck.Core.Matching;
using CuepointDeck.Core.Resolume;

namespace CuepointDeck.App.ViewModels;

public partial class SegmentEditItem : ObservableObject
{
    public Segment Segment { get; }
    private readonly EditViewModel _owner;

    public SegmentEditItem(EditViewModel owner, Segment segment) { _owner = owner; Segment = segment; }

    public string Name { get => Segment.Name; set { if (Segment.Name == value) return; Segment.Name = value; OnPropertyChanged(); _owner.MarkDirty(); } }
    public string Lyric { get => Segment.Lyric; set { if (Segment.Lyric == value) return; Segment.Lyric = value; OnPropertyChanged(); _owner.MarkDirty(); } }
    public string Color { get => Segment.Color; set { if (Segment.Color == value) return; Segment.Color = value; OnPropertyChanged(); _owner.MarkDirty(); } }

    public long StartMs
    {
        get => Segment.StartMs;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, _owner.DurationMs));
            if (Segment.StartMs == clamped) return;
            Segment.StartMs = clamped;
            if (Segment.EndMs is long e && e <= clamped) Segment.EndMs = null;
            RaiseTimes();
            _owner.MarkDirty();
            _owner.Resort();
            _owner.OnSegmentStartChanged(this);
        }
    }

    /// <summary>Optional end point. Null means "runs until the next segment".</summary>
    public long? EndMs
    {
        get => Segment.EndMs;
        set
        {
            long? v = value is long e ? Math.Clamp(e, Segment.StartMs + 1, Math.Max(Segment.StartMs + 1, _owner.DurationMs)) : null;
            if (Segment.EndMs == v) return;
            Segment.EndMs = v;
            RaiseTimes();
            _owner.MarkDirty();
            _owner.OnSegmentEndChanged(this);
        }
    }

    public bool HasEnd => Segment.EndMs is not null;

    /// <summary>The "Ends early" check box: on gives the segment an end point (leaving a gap before the next one).</summary>
    public bool EndsEarly
    {
        get => HasEnd;
        set
        {
            if (value == HasEnd) return;
            if (value) _owner.SetDefaultEnd(this); else EndMs = null;
            OnPropertyChanged();
        }
    }

    public string StartText { get => Timecode.Format(Segment.StartMs, _owner.Fps); set { if (Timecode.TryParse(value, _owner.Fps, out var ms)) StartMs = ms; else OnPropertyChanged(); } }
    public string EndText
    {
        get => Segment.EndMs is long e ? Timecode.Format(e, _owner.Fps) : "";
        set
        {
            if (string.IsNullOrWhiteSpace(value)) { EndMs = null; return; }
            if (Timecode.TryParse(value, _owner.Fps, out var ms)) EndMs = ms; else OnPropertyChanged();
        }
    }
    public string RangeText => HasEnd ? $"{StartText} – {EndText}" : StartText;

    public void RaiseTimes()
    {
        OnPropertyChanged(nameof(StartMs)); OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(EndMs)); OnPropertyChanged(nameof(EndText));
        OnPropertyChanged(nameof(HasEnd)); OnPropertyChanged(nameof(EndsEarly)); OnPropertyChanged(nameof(RangeText));
    }

    [ObservableProperty] private BitmapImage? _thumb;
    [ObservableProperty] private string _thumbStatus = "";
    /// <summary>Suggested by the analyser and not yet accepted. Drafts save like any segment; the flag is display only.</summary>
    [ObservableProperty] private bool _isDraft;
    [ObservableProperty] private string _draftText = "";
    public bool ThumbNeedsUpdate => Segment.Thumb is null || Segment.ThumbAtMs != Segment.StartMs;

    public void RefreshThumb(SongLibrary lib)
    {
        Thumb = Segment.Thumb is null ? null : ThumbCache.Get(lib.ResolveLibraryPath(Segment.Thumb), 320);
        ThumbStatus = Segment.Thumb is null ? "No thumbnail yet" : ThumbNeedsUpdate ? "Thumbnail out of date" : "";
        OnPropertyChanged(nameof(ThumbNeedsUpdate));
    }
}

public sealed class FilmstripFrame
{
    public required int Index { get; init; }
    public required double TimeMs { get; init; }
    public required string Path { get; init; }
    public required double Fps { get; init; }
    public string TimeText => Timecode.Format(TimeMs, Fps);
    public BitmapImage? Image => ThumbCache.Get(Path, 320);
}

/// <summary>A clip on the song layer that a new song can be built from, named after its Resolume column.</summary>
public sealed class CompositionClipItem
{
    public required ClipInfo Clip { get; init; }
    public string ColumnName { get; init; } = "";
    /// <summary>The library song this clip already belongs to, if any.</summary>
    public Song? ExistingSong { get; init; }
    public string Title => ColumnName.Length > 0 ? ColumnName : Clip.Name;
    public string Detail => $"Column {Clip.Column} · {Path.GetFileName(Clip.FilePath)}";
}

public sealed class SongListItem
{
    public required Song Song { get; init; }
    public string Title => Song.Title;
    public string Detail => Song.Segments.Count == 0 ? "No segments yet" : Song.Segments.Count == 1 ? "1 segment" : $"{Song.Segments.Count} segments";
}

/// <summary>
/// Edit mode: a sidebar of songs and setlists, and the editor for whichever is selected. Reads from Resolume (the
/// playhead) and runs ffmpeg; never sends anything to Resolume.
/// </summary>
public partial class EditViewModel : ObservableObject
{
    public static readonly string[] QuickNames = { "Intro", "Verse 1", "Verse 2", "Verse 3", "Verse 4", "Pre-Chorus", "Chorus", "Bridge", "Tag", "Instrumental", "Outro" };
    public static readonly string[] Palette = { "#4A90D9", "#F5A623", "#7ED321", "#BD10E0", "#50E3C2", "#FF6B6B", "#F8E71C", "#B8E986", "#9013FE", "#8E8E93" };

    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private Song? _song;
    private bool _isNew;
    private CancellationTokenSource? _filmstripCts;
    private CancellationTokenSource? _exactFrameCts;
    private CancellationTokenSource? _suggestCts;
    private string? _localSourceFile;
    private string? _filmstripDir;
    private bool _suppressSelect;
    private (Segment Segment, int Index)? _lastDeleted;

    // ---- sidebar
    public ObservableCollection<SongListItem> Songs { get; } = new();
    [ObservableProperty] private SongListItem? _selectedSongItem;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _songCount;
    public SetlistsViewModel Setlists { get; }
    [ObservableProperty] private string? _selectedSetlistName;

    // ---- sidebar: docked on wide windows (and can be hidden), slides over the content on narrow ones
    [ObservableProperty] private bool _isSidebarOpen = true;
    [ObservableProperty] private bool _isNarrow;
    [ObservableProperty] private bool _isOverlayOpen;
    partial void OnIsNarrowChanged(bool value) { if (!value) IsOverlayOpen = false; }

    [RelayCommand]
    private void ToggleSidebar()
    {
        if (IsNarrow) IsOverlayOpen = !IsOverlayOpen;
        else IsSidebarOpen = !IsSidebarOpen;
    }

    private void CloseOverlay() { if (IsNarrow) IsOverlayOpen = false; }

    /// <summary>What the content area shows: "song", "setlist" or "empty".</summary>
    [ObservableProperty] private string _view = "empty";
    public bool IsSongView => View == "song";
    public bool IsSetlistView => View == "setlist";
    public bool IsEmptyView => View == "empty";
    partial void OnViewChanged(string value) { OnPropertyChanged(nameof(IsSongView)); OnPropertyChanged(nameof(IsSetlistView)); OnPropertyChanged(nameof(IsEmptyView)); }

    public ObservableCollection<CompositionClipItem> CompositionClips { get; } = new();

    // ---- song fields
    [ObservableProperty] private bool _hasSong;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _clipFileText = "";
    [ObservableProperty] private string _clipStatusText = "";
    [ObservableProperty] private string _durationText = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _saveStatusText = "";
    [ObservableProperty] private bool _canDeleteSong;
    public long DurationMs => _song?.DurationMs ?? 0;
    public double Fps => _song?.Fps is > 0 and var f ? f : Timecode.DefaultFps;

    public ObservableCollection<SegmentEditItem> Segments { get; } = new();
    [ObservableProperty] private SegmentEditItem? _selectedSegment;
    [ObservableProperty] private string _segmentCountText = "";
    public string[] QuickNamesList => QuickNames;
    public string[] PaletteList => Palette;

    // ---- suggestions
    [ObservableProperty] private bool _hasDrafts;
    [ObservableProperty] private bool _isSuggesting;
    [ObservableProperty] private string _suggestStatus = "";

    // ---- Resolume playhead
    [ObservableProperty] private bool _playheadAvailable;
    [ObservableProperty] private string _playheadText = "--:--:--:--";
    [ObservableProperty] private string _playheadHint = "";

    // ---- preview player / scrubber
    public ObservableCollection<FilmstripFrame> Frames { get; } = new();
    [ObservableProperty] private double _scrubMs;
    [ObservableProperty] private string _scrubText = "00:00:00:00";
    [ObservableProperty] private BitmapImage? _previewImage;
    /// <summary>Width:height of the clip, so the preview box is the clip's own shape. Learned from the first frame seen
    /// (filmstrip, exact frame or the proxy once it opens) and kept until another song shows a different shape.</summary>
    [ObservableProperty] private double _previewAspect = Controls.AspectBox.DefaultRatio;
    partial void OnPreviewImageChanged(BitmapImage? value) { if (value is { PixelHeight: > 0 }) PreviewAspect = value.PixelWidth / (double)value.PixelHeight; }
    [ObservableProperty] private string _scrubStatus = "";
    [ObservableProperty] private bool _scrubberAvailable;
    [ObservableProperty] private double _frameStepMs = 40;
    [ObservableProperty] private string _sourceFileStatus = "";
    [ObservableProperty] private bool _sourceFileMissing;
    [ObservableProperty] private string _previewKind = "";
    /// <summary>Path of the H.264 proxy once it exists; the view plays this. Null = filmstrip and stills only.</summary>
    [ObservableProperty] private string? _proxyPath;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string _playPauseText = "Play";
    /// <summary>Set by the view while the player drives the position, so the VM does not seek the player back.</summary>
    public bool PositionFromPlayer { get; set; }

    public EditViewModel(AppServices services, ShellViewModel shell, Dispatcher dispatcher)
    {
        _services = services;
        _shell = shell;
        _dispatcher = dispatcher;
        Setlists = new SetlistsViewModel(services, shell);
        _services.LibraryChanged += () => _dispatcher.BeginInvoke(() => { RefreshSongList(); Setlists.Reload(Setlists.SelectedName); });
        _services.LiveChanged += () => _dispatcher.BeginInvoke(RefreshClipStatus);
        _services.MatchesChanged += () => _dispatcher.BeginInvoke(() => { RefreshClipStatus(); Setlists.RefreshSources(); });
        _services.SettingsChanged += () => _dispatcher.BeginInvoke(() => Setlists.RefreshActive());
        RefreshSongList();
        RefreshCompositionClips();
        _timer.Tick += (_, _) => TickPlayhead();
        _timer.Start();
    }

    public void Stop() { _timer.Stop(); _filmstripCts?.Cancel(); _suggestCts?.Cancel(); IsPlaying = false; }

    // ------------------------------------------------------------------ sidebar

    partial void OnSearchTextChanged(string value) => RefreshSongList();

    private void RefreshSongList()
    {
        var keep = SelectedSongItem?.Song.Id ?? (IsSongView ? _song?.Id : null);
        var q = SearchText.Trim();
        _suppressSelect = true;
        try
        {
            Songs.Clear();
            foreach (var s in _services.Library.Songs.OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase))
                if (q.Length == 0 || s.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || s.Artist.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                    Songs.Add(new SongListItem { Song = s });
            SongCount = _services.Library.Songs.Count;
            if (keep is not null && IsSongView) SelectedSongItem = Songs.FirstOrDefault(i => i.Song.Id == keep);
        }
        finally { _suppressSelect = false; }
        RefreshCompositionClips();
    }

    partial void OnSelectedSongItemChanged(SongListItem? value)
    {
        if (_suppressSelect || value is null) return;
        CloseOverlay();
        ClearSetlistSelection();
        if (_song is not null && value.Song.Id == _song.Id) { View = "song"; return; }
        if (!ConfirmDiscard())
        {
            // Stay on what was being edited.
            _suppressSelect = true;
            SelectedSongItem = Songs.FirstOrDefault(i => i.Song.Id == _song?.Id);
            _suppressSelect = false;
            if (_song is not null) View = "song";
            return;
        }
        Load(Json.Deserialize<Song>(Json.Serialize(value.Song))!, isNew: false);
    }

    partial void OnSelectedSetlistNameChanged(string? value)
    {
        if (_suppressSelect || value is null) return;
        CloseOverlay();
        IsPlaying = false;
        _suppressSelect = true;
        SelectedSongItem = null;
        _suppressSelect = false;
        Setlists.SelectedName = value;
        View = "setlist";
    }

    private void ClearSetlistSelection()
    {
        _suppressSelect = true;
        SelectedSetlistName = null;
        _suppressSelect = false;
    }

    private void SelectSidebarSong(string? id)
    {
        _suppressSelect = true;
        SelectedSongItem = id is null ? null : Songs.FirstOrDefault(i => i.Song.Id == id);
        SelectedSetlistName = null;
        _suppressSelect = false;
    }

    /// <summary>Clips a new song can be built from: only the song layer (Settings → Song layer). Other layers hold the
    /// same media as overlays and manual triggers, and listing them would offer every song twice.</summary>
    private void RefreshCompositionClips()
    {
        CompositionClips.Clear();
        var comp = _services.Connection.Composition;
        if (comp is null) return;
        var layer = _services.Settings.SongLayer;
        foreach (var c in comp.Clips.Where(c => c.Layer == layer && !c.IsEmpty && !string.IsNullOrEmpty(c.FilePath)))
            CompositionClips.Add(new CompositionClipItem { Clip = c, ColumnName = comp.Column(c.Column)?.Name ?? "", ExistingSong = _services.Matches.SongForClip(c.ClipId) });
    }

    // ------------------------------------------------------------------ open / new

    /// <summary>Opens a song (from Show mode's "Edit Song" / "Edit Segment", or E), optionally on one segment.</summary>
    public void OpenSong(Song song, string? segmentId = null)
    {
        if (_song is null || _song.Id != song.Id)
        {
            if (!ConfirmDiscard()) { if (_song is not null) View = "song"; return; }
            Load(Json.Deserialize<Song>(Json.Serialize(song))!, isNew: false);
        }
        else View = "song";
        SelectSidebarSong(song.Id);
        if (segmentId is not null) SelectedSegment = Segments.FirstOrDefault(s => s.Segment.Id == segmentId) ?? SelectedSegment;
    }

    /// <summary>Opens a setlist in the editor; with no name, the active one (or the first).</summary>
    public void OpenSetlist(string? name)
    {
        name ??= _services.Settings.ActiveSetlist ?? Setlists.Names.FirstOrDefault();
        if (name is null || !Setlists.Names.Contains(name)) { View = HasSong ? "song" : "empty"; return; }
        SelectedSetlistName = null;
        SelectedSetlistName = name;
    }

    public void NewFromClip(ClipInfo clip)
    {
        var existing = _services.Matches.SongForClip(clip.ClipId);
        if (existing is not null) { OpenSong(existing); return; }
        if (!ConfirmDiscard()) return;
        // Songs are laid out one per column and the column carries the song's name; the clip name is the file name.
        var columnName = _services.Connection.Composition?.Column(clip.Column)?.Name;
        var song = new Song
        {
            Title = string.IsNullOrWhiteSpace(columnName) ? clip.Name : columnName,
            Clip = new ClipRef { FilePath = clip.FilePath, FileName = Path.GetFileName(clip.FilePath), ClipName = clip.Name },
            DurationMs = (long)Math.Round(clip.DurationMs),
            Fps = clip.Fps > 0 ? clip.Fps : Timecode.DefaultFps,
        };
        Load(song, isNew: true);
    }

    [RelayCommand]
    private async Task NewFromCompositionAsync()
    {
        RefreshCompositionClips();
        if (CompositionClips.Count == 0)
        {
            // Nothing to pick from: say why, and offer the other way in, rather than an empty list.
            var connected = _services.Connection.State == ConnectionState.Connected;
            var choice = Alert.Show(
                connected ? $"There are no clips on layer {_services.Settings.SongLayer}" : "Resolume isn't connected",
                connected ? "New songs come from the song layer. Check the layer number in Settings → Resolume, or start from a media file instead."
                          : "Songs are picked from Resolume's columns, so it needs to be running with its web server on. You can start from a media file instead.",
                new AlertButton("file", "New Song from File…", AlertRole.Leading),
                new AlertButton("ok", "OK", AlertRole.Default | AlertRole.Cancel));
            if (choice == "file") await NewFromFileAsync();
            return;
        }
        var hint = $"Choose a column on layer {_services.Settings.SongLayer}, the song layer. Columns already in the library are marked.";
        var picked = PickClipSheet.Pick(CompositionClips.ToList(), hint);
        if (picked is null) return;
        if (picked.ExistingSong is not null) OpenSong(picked.ExistingSong);
        else NewFromClip(picked.Clip);
    }

    [RelayCommand]
    private async Task NewFromFileAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose the Song's Media File", Filter = "Video files|*.mov;*.mp4;*.avi;*.mkv;*.m4v;*.dxv|All files|*.*" };
        if (dlg.ShowDialog() != true || !ConfirmDiscard()) return;
        var path = dlg.FileName;
        var song = new Song
        {
            Title = Path.GetFileNameWithoutExtension(path),
            Clip = new ClipRef { FilePath = path, FileName = Path.GetFileName(path), ClipName = Path.GetFileNameWithoutExtension(path) },
        };
        var inComp = _services.Connection.Composition?.Clips.FirstOrDefault(c => PathNorm.Key(c.FilePath) == PathNorm.Key(path));
        if (inComp is not null) { song.DurationMs = (long)Math.Round(inComp.DurationMs); if (inComp.Fps > 0) song.Fps = inComp.Fps; }
        else
        {
            SaveStatusText = "Reading the duration…";
            var d = await _services.Ffmpeg.DurationMsAsync(path);
            song.DurationMs = d is double ms ? (long)Math.Round(ms) : 0;
            if (d is null) _shell.ShowToast("Couldn't read the file's duration. Load the clip in Resolume to fill it in.", warn: true);
        }
        Load(song, isNew: true);
    }

    private void Load(Song song, bool isNew)
    {
        _filmstripCts?.Cancel();
        _suggestCts?.Cancel();
        IsPlaying = false;
        ProxyPath = null;
        _song = song;
        _isNew = isNew;
        _lastDeleted = null;
        song.SortSegments();
        HasSong = true;
        Title = song.Title;
        Artist = song.Artist;
        ClipFileText = song.Clip.FilePath;
        Segments.Clear();
        foreach (var s in song.Segments) Segments.Add(new SegmentEditItem(this, s));
        foreach (var s in Segments) s.RefreshThumb(_services.Library);
        HasDrafts = false;
        SuggestStatus = "";
        IsDirty = isNew;
        CanDeleteSong = !isNew;
        SaveStatusText = isNew ? "New song, not saved yet" : "";
        RefreshSegmentCount();
        RefreshClipStatus();
        RaiseTimeFormat();
        SelectedSegment = Segments.FirstOrDefault();
        View = "song";
        SelectSidebarSong(isNew ? null : song.Id);
        _ = PrepareScrubberAsync();
    }

    private void RefreshSegmentCount() => SegmentCountText = Segments.Count == 0 ? "" : Segments.Count == 1 ? "1" : Segments.Count.ToString();

    private void RaiseTimeFormat()
    {
        OnPropertyChanged(nameof(DurationMs));
        OnPropertyChanged(nameof(Fps));
        DurationText = $"{Timecode.Format(DurationMs, Fps)} · {Fps:0.##} fps";
        FrameStepMs = Timecode.FrameMs(Fps);
        ScrubText = Timecode.Format(ScrubMs, Fps);
        foreach (var s in Segments) s.RaiseTimes();
    }

    private void RefreshClipStatus()
    {
        if (_song is null) return;
        var clip = FindClip();
        if (clip is null) ClipStatusText = "Not in the Resolume composition";
        else
        {
            ClipStatusText = $"{clip.Location} in Resolume{(clip.IsConnected ? " · live" : "")}";
            bool changed = false;
            if (clip.Fps > 0 && Math.Abs(_song.Fps - clip.Fps) > 0.01) { _song.Fps = clip.Fps; changed = true; }
            if (_song.DurationMs == 0 && clip.DurationMs > 0) { _song.DurationMs = (long)Math.Round(clip.DurationMs); changed = true; }
            if (changed) RaiseTimeFormat();
        }
        RefreshCompositionClips();
    }

    private ClipInfo? FindClip()
    {
        if (_song is null) return null;
        var byId = _services.Matches.For(_song.Id)?.Clip;
        if (byId is not null) return byId;
        var mapper = new PathMapper(_services.Settings.PathMappings);
        var keys = mapper.Variants(_song.Clip.FilePath).Select(PathNorm.Key).ToHashSet();
        var comp = _services.Connection.Composition;
        return comp?.Clips.FirstOrDefault(c => !c.IsEmpty && (keys.Contains(PathNorm.Key(c.FilePath)) || string.Equals(Path.GetFileName(c.FilePath), _song.Clip.FileName, StringComparison.OrdinalIgnoreCase)));
    }

    partial void OnTitleChanged(string value) { if (_song is not null && _song.Title != value) { _song.Title = value; MarkDirty(); } }
    partial void OnArtistChanged(string value) { if (_song is not null && _song.Artist != value) { _song.Artist = value; MarkDirty(); } }

    public void MarkDirty() { IsDirty = true; SaveStatusText = _isNew ? "New song, not saved yet" : "Edited"; }

    public void Resort()
    {
        if (_song is null) return;
        _song.SortSegments();
        var selected = SelectedSegment?.Segment;
        var ordered = Segments.OrderBy(s => s.StartMs).ToList();
        for (int i = 0; i < ordered.Count; i++)
            if (!ReferenceEquals(Segments[i], ordered[i])) Segments.Move(Segments.IndexOf(ordered[i]), i);
        if (selected is not null) SelectedSegment = Segments.FirstOrDefault(s => ReferenceEquals(s.Segment, selected));
    }

    // ------------------------------------------------------------------ segments

    private void AddSegmentAt(double ms, string source)
    {
        if (_song is null) return;
        var seg = new Segment { Name = NextName(), StartMs = (long)Math.Round(Math.Clamp(ms, 0, Math.Max(0, _song.DurationMs))), Color = Palette[_song.Segments.Count % Palette.Length] };
        _song.Segments.Add(seg);
        var item = new SegmentEditItem(this, seg);
        Segments.Add(item);
        item.RefreshThumb(_services.Library);
        Resort();
        SelectedSegment = item;
        RefreshSegmentCount();
        MarkDirty();
        Log.Info($"Segment added at {Timecode.Format(seg.StartMs, Fps)} ({source})");
    }

    private string NextName()
    {
        var used = Segments.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var n in new[] { "Intro", "Verse 1", "Chorus", "Verse 2", "Bridge", "Outro" }) if (!used.Contains(n)) return n;
        return $"Segment {Segments.Count + 1}";
    }

    private double? PlayheadMs => PlayheadAvailable ? _services.Estimator.EstimateMs() : null;

    [RelayCommand]
    private void MarkAtPlayhead()
    {
        if (PlayheadMs is double ms) AddSegmentAt(ms, "Resolume playhead");
        else _shell.ShowToast("This song's clip isn't live in Resolume. Start it there, or add at the preview position (Shift+M).", warn: true);
    }

    [RelayCommand] private void MarkAtScrubber() => AddSegmentAt(ScrubMs, "preview");

    /// <summary>Delete with no question asked: Ctrl+Z brings it back, and nothing is saved until Save.</summary>
    [RelayCommand]
    private void DeleteSegment()
    {
        if (_song is null || SelectedSegment is null) return;
        var item = SelectedSegment;
        var idx = Segments.IndexOf(item);
        _lastDeleted = (item.Segment, idx);
        _song.Segments.Remove(item.Segment);
        Segments.Remove(item);
        SelectedSegment = Segments.Count == 0 ? null : Segments[Math.Min(idx, Segments.Count - 1)];
        RefreshSegmentCount();
        MarkDirty();
        _shell.ShowToast($"Deleted {item.Name}. Press Ctrl+Z to bring it back.");
    }

    [RelayCommand]
    private void UndoDelete()
    {
        if (_song is null || _lastDeleted is not { } d) return;
        _lastDeleted = null;
        _song.Segments.Add(d.Segment);
        var item = new SegmentEditItem(this, d.Segment);
        Segments.Insert(Math.Clamp(d.Index, 0, Segments.Count), item);
        item.RefreshThumb(_services.Library);
        Resort();
        SelectedSegment = item;
        RefreshSegmentCount();
        MarkDirty();
        _shell.ShowToast($"Restored {item.Name}");
    }

    /// <summary>Frame nudges. Shift moves five frames at a time.</summary>
    [RelayCommand] private void Nudge(string amount) { if (SelectedSegment is null) return; SelectedSegment.StartMs = (long)Math.Round(SelectedSegment.StartMs + Delta(amount)); }
    [RelayCommand] private void NudgeEnd(string amount) { if (SelectedSegment?.EndMs is long e) SelectedSegment.EndMs = (long)Math.Round(e + Delta(amount)); }
    private double Delta(string amount)
    {
        var frames = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 5 : 1;
        return amount switch { "-frame" => -FrameStepMs * frames, "+frame" => FrameStepMs * frames, "-100" => -100, "+100" => 100, _ => 0 };
    }
    [RelayCommand] private void SetName(string name) { if (SelectedSegment is not null) SelectedSegment.Name = name; }
    [RelayCommand] private void SetColor(string color) { if (SelectedSegment is not null) SelectedSegment.Color = color; }
    [RelayCommand] private void SetStartFromScrubber() { if (SelectedSegment is not null) SelectedSegment.StartMs = (long)Math.Round(ScrubMs); }
    [RelayCommand] private void SetStartFromPlayhead() { if (SelectedSegment is not null && PlayheadMs is double ms) SelectedSegment.StartMs = (long)Math.Round(ms); else if (SelectedSegment is not null) _shell.ShowToast("The clip isn't live in Resolume, so there's no playhead to use.", warn: true); }
    [RelayCommand] private void SetEndFromScrubber() { if (SelectedSegment is not null) SelectedSegment.EndMs = (long)Math.Round(ScrubMs); }
    [RelayCommand] private void SetEndFromPlayhead() { if (SelectedSegment is not null && PlayheadMs is double ms) SelectedSegment.EndMs = (long)Math.Round(ms); else if (SelectedSegment is not null) _shell.ShowToast("The clip isn't live in Resolume, so there's no playhead to use.", warn: true); }
    [RelayCommand] private void ClearEnd() { if (SelectedSegment is not null) SelectedSegment.EndMs = null; }
    [RelayCommand] private void SeekScrubberToSegment() { if (SelectedSegment is not null) MoveScrubberTo(SelectedSegment.StartMs); }
    [RelayCommand] private void SeekScrubberToEnd() { if (SelectedSegment?.EndMs is long e) MoveScrubberTo(e); }

    /// <summary>Turning "Ends early" on: end at the preview position if it is inside the segment, otherwise a frame
    /// before the next segment (or 8 s in for the last one), and show that frame.</summary>
    public void SetDefaultEnd(SegmentEditItem item)
    {
        if (_song is null) return;
        var idx = Segments.IndexOf(item);
        var next = idx >= 0 && idx + 1 < Segments.Count ? Segments[idx + 1].StartMs : (long?)null;
        long end;
        if (ScrubMs > item.StartMs + FrameStepMs && (next is null || ScrubMs < next)) end = (long)Math.Round(ScrubMs);
        else if (next is long n && n - (long)FrameStepMs > item.StartMs) end = n - (long)Math.Round(FrameStepMs);
        else end = Math.Min(item.StartMs + 8000, Math.Max(item.StartMs + 1, _song.DurationMs));
        item.EndMs = end;
    }

    private void TickPlayhead()
    {
        var clip = FindClip();
        var watched = _services.Connection.WatchedClip;
        var available = clip is not null && watched is not null && clip.ClipId == watched.ClipId && clip.IsConnected && _services.Estimator.EstimateMs() is not null;
        PlayheadAvailable = available;
        PlayheadText = available ? Timecode.Format(_services.Estimator.EstimateMs()!.Value, Fps) : "--:--:--:--";
        PlayheadHint = available ? (_services.Estimator.IsPaused ? "paused" : "playing") : clip is null ? "not in the composition" : !clip.IsConnected ? "not live; start it in Resolume to mark by ear" : "";
    }

    // ------------------------------------------------------------------ preview: filmstrip, proxy, player

    /// <summary>Selecting a segment, or moving its start, shows that exact frame in the preview.</summary>
    partial void OnSelectedSegmentChanged(SegmentEditItem? value)
    {
        if (value is not null) MoveScrubberTo(value.StartMs);
    }

    public void OnSegmentStartChanged(SegmentEditItem item) { if (ReferenceEquals(item, SelectedSegment)) MoveScrubberTo(item.StartMs); }
    public void OnSegmentEndChanged(SegmentEditItem item) { if (ReferenceEquals(item, SelectedSegment) && item.EndMs is long e) MoveScrubberTo(e); }

    private void MoveScrubberTo(double ms)
    {
        IsPlaying = false;
        if (Math.Abs(ScrubMs - ms) < 0.5) _ = RequestExactFrameAsync(ms);
        else ScrubMs = ms;
    }

    private async Task PrepareScrubberAsync()
    {
        Frames.Clear();
        PreviewImage = null;
        ScrubberAvailable = false;
        ProxyPath = null;
        SourceFileMissing = false;
        if (_song is null) return;
        ScrubMs = 0;

        _localSourceFile = ResolveLocalFile();
        if (_localSourceFile is null)
        {
            var mapper = new PathMapper(_services.Settings.PathMappings);
            var tried = string.Join(", ", mapper.Variants(_song.Clip.FilePath));
            SourceFileMissing = true;
            SourceFileStatus = $"This PC can't open the media file (tried {tried}). Add a path mapping in Settings → Library, or edit on the Resolume PC. Thumbnails stay as colour placeholders.";
            ScrubStatus = "";
            return;
        }
        SourceFileStatus = _localSourceFile;
        if (!_services.Ffmpeg.IsAvailable)
        {
            ScrubStatus = "ffmpeg isn't set up, so there's no preview. Choose ffmpeg.exe in Settings → Media.";
            return;
        }

        var dir = FilmstripDir(_song, _localSourceFile);
        _filmstripDir = dir;
        var cts = _filmstripCts = new CancellationTokenSource();
        var song = _song;

        if (!File.Exists(Path.Combine(dir, "done.txt")))
        {
            ScrubStatus = "Building preview frames…";
            var result = await _services.Ffmpeg.FilmstripAsync(_localSourceFile, dir, 2, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (!result.Ok) { ScrubStatus = $"The preview frames failed: {result.Error}"; return; }
        }
        var files = Directory.GetFiles(dir, "*.jpg").OrderBy(f => f).ToList();
        for (int i = 0; i < files.Count; i++) Frames.Add(new FilmstripFrame { Index = i, TimeMs = i * 2000.0, Path = files[i], Fps = Fps });
        ScrubberAvailable = Frames.Count > 0;
        ScrubStatus = "Building the preview video…";
        if (SelectedSegment is not null && Math.Abs(ScrubMs - SelectedSegment.StartMs) > 0.5) ScrubMs = SelectedSegment.StartMs;
        else { UpdatePreview(); _ = RequestExactFrameAsync(ScrubMs); }

        // The proxy video: real play/pause and instant frame stepping once it exists.
        var proxy = Path.Combine(dir, "preview.mp4");
        var pr = await _services.Ffmpeg.PreviewProxyAsync(_localSourceFile, proxy, cts.Token);
        if (cts.IsCancellationRequested || !ReferenceEquals(_song, song)) return;
        if (pr.Ok)
        {
            ProxyPath = proxy;
            PreviewKind = "";
            ScrubStatus = "";
        }
        else ScrubStatus = $"The preview video failed ({pr.Error}); stepping uses stills.";
    }

    private string FilmstripDir(Song song, string file)
    {
        var fi = new FileInfo(file);
        var key = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"{fi.FullName.ToUpperInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}")))[..12];
        return Path.Combine(_services.Library.ThumbDir(song.Id), key + ".filmstrip");
    }

    private string? ResolveLocalFile()
    {
        if (_song is null) return null;
        var mapper = new PathMapper(_services.Settings.PathMappings);
        var candidates = new List<string>();
        var clip = FindClip();
        if (clip is not null && !string.IsNullOrEmpty(clip.FilePath)) candidates.AddRange(mapper.Variants(clip.FilePath));
        candidates.AddRange(mapper.Variants(_song.Clip.FilePath));
        return candidates.FirstOrDefault(File.Exists);
    }

    partial void OnScrubMsChanged(double value)
    {
        ScrubText = Timecode.Format(value, Fps);
        if (ProxyPath is not null) { PreviewKind = ""; return; }
        UpdatePreview();
        _ = RequestExactFrameAsync(value);
    }

    partial void OnIsPlayingChanged(bool value) => PlayPauseText = value ? "Pause" : "Play";

    [RelayCommand]
    private void PlayPause()
    {
        if (ProxyPath is null) { _shell.ShowToast("The preview video is still being built. Stepping uses stills until then."); return; }
        IsPlaying = !IsPlaying;
    }

    /// <summary>Immediate feedback before the proxy exists: the nearest filmstrip frame (one every 2 s).</summary>
    private void UpdatePreview()
    {
        if (Frames.Count == 0) { PreviewImage = null; PreviewKind = ""; return; }
        var idx = Math.Clamp((int)(ScrubMs / 2000.0), 0, Frames.Count - 1);
        PreviewImage = ThumbCache.Get(Frames[idx].Path, 480);
        PreviewKind = "nearest still";
    }

    /// <summary>Without the proxy: after the scrubber settles, pull the exact frame with ffmpeg.</summary>
    private async Task RequestExactFrameAsync(double ms)
    {
        _exactFrameCts?.Cancel();
        var cts = _exactFrameCts = new CancellationTokenSource();
        if (_song is null || _localSourceFile is null || _filmstripDir is null || !_services.Ffmpeg.IsAvailable || ProxyPath is not null) return;
        try
        {
            await Task.Delay(350, cts.Token);
            var exactMs = (long)Math.Round(ms);
            var path = Path.Combine(_filmstripDir, "exact", $"{exactMs}.jpg");
            if (!File.Exists(path))
            {
                var r = await _services.Ffmpeg.ThumbnailAsync(_localSourceFile, exactMs, path, cts.Token);
                if (!r.Ok || cts.IsCancellationRequested) return;
            }
            if (cts.IsCancellationRequested || Math.Abs(ScrubMs - ms) > 0.5 || ProxyPath is not null) return;
            PreviewImage = ThumbCache.Get(path, 480);
            PreviewKind = "exact frame";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"Exact frame preview failed: {ex.Message}"); }
    }

    public void ScrubStep(int direction, bool bySecond)
    {
        IsPlaying = false;
        // Land exactly on a frame boundary so hh:mm:ss:ff reads clean.
        var frames = Math.Round(ScrubMs / FrameStepMs) + direction * (bySecond ? Math.Round(1000 / FrameStepMs) : 1);
        ScrubMs = Math.Clamp(frames * FrameStepMs, 0, Math.Max(0, DurationMs));
    }

    public void ScrubTo(FilmstripFrame frame) => MoveScrubberTo(frame.TimeMs);

    /// <summary>Keyboard while Edit mode is active and no text box has focus.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.Z) { if (IsSongView) UndoDeleteCommand.Execute(null); return true; }
        if (e.Key == Key.Escape)
        {
            if (IsOverlayOpen) { IsOverlayOpen = false; return true; }
            if (IsPlaying) { IsPlaying = false; return true; }
            _shell.BackToShow();
            return true;
        }
        if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            if (IsSongView) DeleteSegmentCommand.Execute(null);
            else if (IsSetlistView) Setlists.RemoveSongCommand.Execute(null);
            return true;
        }
        if (!IsSongView) return false;
        switch (e.Key)
        {
            case Key.M: if (PlayheadAvailable && !shift) MarkAtPlayheadCommand.Execute(null); else MarkAtScrubberCommand.Execute(null); return true;
            case Key.Left: ScrubStep(-1, shift); return true;
            case Key.Right: ScrubStep(1, shift); return true;
            case Key.Space: PlayPauseCommand.Execute(null); return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ suggestions

    /// <summary>Audio structure + video cuts → draft segments. Offline, low priority, a human keeps or discards them.</summary>
    [RelayCommand]
    private async Task SuggestSegmentsAsync()
    {
        if (_song is null || IsSuggesting) return;
        var file = _localSourceFile ?? ResolveLocalFile();
        if (file is null) { _shell.ShowToast("This PC can't open the media file, so it can't be analysed. Add a path mapping, or do this on the Resolume PC.", warn: true); return; }
        if (!_services.Ffmpeg.IsAvailable) { _shell.ShowToast("Suggestions need ffmpeg. Choose it in Settings → Media.", warn: true); return; }

        bool replace = false;
        if (Segments.Count > 0)
        {
            var choice = Alert.Show(
                "Replace the existing segments?",
                $"“{_song.Title}” already has {Segments.Count} segment{(Segments.Count == 1 ? "" : "s")}. The suggestions can replace them, or be added alongside as drafts for you to review.",
                new AlertButton("replace", "Replace", AlertRole.Destructive | AlertRole.Leading),
                new AlertButton("cancel", "Cancel", AlertRole.Cancel),
                new AlertButton("add", "Add as Drafts", AlertRole.Default));
            if (choice == "cancel") return;
            replace = choice == "replace";
        }

        IsSuggesting = true;
        var cts = _suggestCts = new CancellationTokenSource();
        var progress = new Progress<string>(m => SuggestStatus = m);
        try
        {
            SuggestStatus = "Listening to the audio…";
            var pcm = await _services.Ffmpeg.DecodeAudioAsync(file, 22050, cts.Token);
            if (pcm is null) { SuggestStatus = "The audio couldn't be decoded (see the log)."; return; }
            SuggestStatus = "Finding the video cuts…";
            var cuts = await _services.Ffmpeg.SceneCutsAsync(file, 0.2, cts.Token);
            var result = await Task.Run(() => StructureAnalyzer.Analyze(pcm, 22050, cuts, null, progress), cts.Token);
            if (cts.IsCancellationRequested) return;

            if (replace) { _song.Segments.Clear(); Segments.Clear(); }
            foreach (var s in result)
            {
                var seg = new Segment { Name = s.Name, StartMs = s.StartMs, Color = ColorFor(s.Name) };
                _song.Segments.Add(seg);
                var item = new SegmentEditItem(this, seg) { IsDraft = true, DraftText = $"Suggested from the {s.Basis} · confidence {s.Confidence:0.00}" };
                Segments.Add(item);
                item.RefreshThumb(_services.Library);
            }
            Resort();
            RefreshSegmentCount();
            HasDrafts = Segments.Any(x => x.IsDraft);
            SelectedSegment = Segments.FirstOrDefault(x => x.IsDraft);
            MarkDirty();
            SuggestStatus = $"{result.Count} segments suggested from the audio and {cuts.Count} video cuts. Check each one, then keep or discard them.";
            Log.Info($"Suggested {result.Count} segments for \"{_song.Title}\": {string.Join(", ", result.Select(x => $"{x.Name}@{Timecode.Format(x.StartMs, Fps)}"))}");
            await ReadLyricsAsync(file, onlyEmpty: true, cts.Token);
        }
        catch (OperationCanceledException) { SuggestStatus = "Cancelled."; }
        catch (Exception ex) { Log.Error("Suggest segments", ex); SuggestStatus = "The analysis failed: " + ex.Message; }
        finally { IsSuggesting = false; }
    }

    /// <summary>Fills empty lyric notes by reading the on-screen text a moment after each segment starts (Windows OCR, offline).</summary>
    [RelayCommand]
    private async Task ReadLyricsFromVideoAsync()
    {
        if (_song is null || IsSuggesting) return;
        var file = _localSourceFile ?? ResolveLocalFile();
        if (file is null) { _shell.ShowToast("This PC can't open the media file. Add a path mapping, or do this on the Resolume PC.", warn: true); return; }
        if (!_services.Ffmpeg.IsAvailable) { _shell.ShowToast("Reading lyrics needs ffmpeg. Choose it in Settings → Media.", warn: true); return; }
        IsSuggesting = true;
        var cts = _suggestCts = new CancellationTokenSource();
        try { await ReadLyricsAsync(file, onlyEmpty: true, cts.Token); }
        finally { IsSuggesting = false; }
    }

    private async Task ReadLyricsAsync(string file, bool onlyEmpty, CancellationToken ct)
    {
        if (!Services.WindowsOcr.IsAvailable) { SuggestStatus = "Windows text recognition isn't available on this PC (no language pack), so lyric notes weren't read."; return; }
        var todo = Segments.Where(s => !onlyEmpty || string.IsNullOrWhiteSpace(s.Lyric)).ToList();
        if (todo.Count == 0) { SuggestStatus = "Every segment already has a lyric note."; return; }
        var dir = Path.Combine(Path.GetTempPath(), "CuepointDeck", "ocr");
        Directory.CreateDirectory(dir);
        int filled = 0;
        for (int i = 0; i < todo.Count; i++)
        {
            if (ct.IsCancellationRequested) return;
            var item = todo[i];
            SuggestStatus = $"Reading lyrics from the video… {i + 1} of {todo.Count} ({item.Name})";
            var end = _song!.SegmentEndMs(_song.Segments.IndexOf(item.Segment));
            string note = "";
            // Two moments after the section starts, each read as-is and then as inverted high-contrast grey (helps light text on dark video).
            foreach (var offset in new[] { 2000.0, 4500.0 })
            {
                var at = Math.Min(item.StartMs + offset, Math.Max(item.StartMs, end - 500));
                foreach (var filter in new string?[] { null, "format=gray,negate,eq=contrast=1.8" })
                {
                    var jpg = Path.Combine(dir, $"{_song.Id}-{item.Segment.Id}-{(long)at}-{(filter is null ? "raw" : "inv")}.jpg");
                    var r = await _services.Ffmpeg.ThumbnailAsync(file, at, jpg, ct, width: 1280, extraFilter: filter);
                    if (!r.Ok) break;
                    var lines = await Services.WindowsOcr.ReadLinesAsync(jpg);
                    try { File.Delete(jpg); } catch { }
                    note = Services.WindowsOcr.ToLyricNote(lines);
                    if (note.Length > 0) break;
                }
                if (note.Length > 0) break;
            }
            if (note.Length > 0) { item.Lyric = note; filled++; }
        }
        SuggestStatus = filled == 0 ? "No readable lyrics were found in the video." : $"Lyric notes read for {filled} of {todo.Count} segment{(todo.Count == 1 ? "" : "s")}. Check the wording; text recognition is a first draft.";
        Log.Info($"OCR lyric notes: {filled}/{todo.Count} for \"{_song!.Title}\"");
    }

    [RelayCommand]
    private void DiscardDrafts()
    {
        if (_song is null) return;
        foreach (var d in Segments.Where(x => x.IsDraft).ToList()) { _song.Segments.Remove(d.Segment); Segments.Remove(d); }
        HasDrafts = false;
        SuggestStatus = "";
        RefreshSegmentCount();
        MarkDirty();
        SelectedSegment = Segments.FirstOrDefault();
    }

    [RelayCommand]
    private void AcceptDrafts()
    {
        foreach (var d in Segments) { d.IsDraft = false; d.DraftText = ""; }
        HasDrafts = false;
        SuggestStatus = "";
    }

    [RelayCommand] private void DismissSuggestStatus() { if (!HasDrafts && !IsSuggesting) SuggestStatus = ""; }

    private static string ColorFor(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.StartsWith("intro") || n.StartsWith("outro")) return "#8E8E93";
        if (n.StartsWith("pre")) return "#50E3C2";
        if (n.StartsWith("chorus")) return "#F5A623";
        if (n.StartsWith("bridge")) return "#BD10E0";
        if (n.StartsWith("instrumental")) return "#7ED321";
        if (n.StartsWith("tag")) return "#F8E71C";
        return "#4A90D9";
    }

    // ------------------------------------------------------------------ save / leave / delete

    /// <summary>Unsaved changes: Save, Don't Save, or Cancel (stay). Returns true when it is fine to move on.</summary>
    private bool ConfirmDiscard()
    {
        if (!IsDirty || _song is null) return true;
        var choice = Alert.Show(
            $"Do you want to save the changes to “{(Title.Trim().Length > 0 ? Title.Trim() : _song.Clip.ClipName)}”?",
            "Your changes will be lost if you don't save them.",
            new AlertButton("discard", "Don't Save", AlertRole.Destructive | AlertRole.Leading),
            new AlertButton("cancel", "Cancel", AlertRole.Cancel),
            new AlertButton("save", "Save", AlertRole.Default));
        return choice switch
        {
            "discard" => DiscardEdits(),
            "save" => TrySave(),
            _ => false,
        };
    }

    private bool DiscardEdits()
    {
        IsDirty = false;
        _lastDeleted = null;
        if (_isNew) { _song = null; HasSong = false; Segments.Clear(); Frames.Clear(); PreviewImage = null; ProxyPath = null; View = "empty"; }
        else if (_song is not null && _services.Library.GetSong(_song.Id) is { } saved) Load(Json.Deserialize<Song>(Json.Serialize(saved))!, isNew: false);
        return true;
    }

    public bool CanLeave() => ConfirmDiscard();

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_song is null) return;
        if (!TrySave()) return;
        await GenerateThumbnailsAsync(_song);
    }

    /// <summary>Writes the song now. Thumbnails follow in the background. Returns false if it didn't save.</summary>
    private bool TrySave()
    {
        if (_song is null) return false;
        _song.Title = Title.Trim().Length == 0 ? _song.Clip.ClipName : Title.Trim();
        Title = _song.Title;
        var lib = _services.Library;
        var result = lib.SaveSong(_song);
        if (result.Status == SaveStatus.Conflict)
        {
            var choice = Alert.Show(
                $"“{_song.Title}” was changed on another computer",
                "It was saved somewhere else after you opened it here. Keep your version, or use theirs and lose your changes.",
                new AlertButton("theirs", "Use Their Version", AlertRole.Destructive | AlertRole.Leading),
                new AlertButton("cancel", "Cancel", AlertRole.Cancel),
                new AlertButton("mine", "Save Mine", AlertRole.Default));
            if (choice == "mine") result = lib.SaveSong(_song, overwrite: true);
            else if (choice == "theirs") { var again = lib.ReloadSong(_song.Id); if (again is not null) Load(again, false); return true; }
            else return false;
        }
        if (!result.Ok)
        {
            SaveStatusText = "Not saved";
            Alert.Inform($"“{_song.Title}” couldn't be saved", result.Error ?? "The library folder couldn't be written to.");
            return false;
        }
        var wasNew = _isNew;
        _isNew = false;
        IsDirty = false;
        CanDeleteSong = true;
        if (HasDrafts) AcceptDrafts();
        SaveStatusText = $"Saved {DateTime.Now:HH:mm}";
        RefreshSongList();
        if (wasNew) SelectSidebarSong(_song.Id);
        if (_song is { } song) _ = GenerateThumbnailsAsync(song);
        return true;
    }

    private bool _renderingThumbs;

    private async Task GenerateThumbnailsAsync(Song song)
    {
        if (_renderingThumbs) return;
        var todo = Segments.Where(s => s.ThumbNeedsUpdate).ToList();
        if (todo.Count == 0) return;
        var file = _localSourceFile ?? ResolveLocalFile();
        if (file is null || !_services.Ffmpeg.IsAvailable)
        {
            foreach (var s in todo) s.ThumbStatus = file is null ? "Placeholder: the media file isn't readable here" : "Placeholder: ffmpeg isn't set up";
            Log.Warn($"Thumbnails skipped for \"{song.Title}\": {(file is null ? "media file not readable on this PC" : "ffmpeg not available")}");
            return;
        }
        _renderingThumbs = true;
        try
        {
            var lib = _services.Library;
            int done = 0, failed = 0;
            foreach (var item in todo)
            {
                item.ThumbStatus = "Rendering thumbnail…";
                var rel = $"thumbs/{song.Id}/{item.Segment.Id}.jpg";
                var abs = lib.ResolveLibraryPath(rel);
                var result = await _services.Ffmpeg.ThumbnailAsync(file, item.Segment.StartMs, abs);
                if (result.Ok)
                {
                    item.Segment.Thumb = rel;
                    item.Segment.ThumbAtMs = item.Segment.StartMs;
                    ThumbCache.Forget(abs);
                    item.RefreshThumb(lib);
                    done++;
                }
                else { item.ThumbStatus = $"Placeholder ({result.Error})"; failed++; }
            }
            if (done > 0 && !IsDirty)
            {
                var r = lib.SaveSong(song, overwrite: false);
                if (r.Status == SaveStatus.Conflict) Log.Warn("Thumbnail paths not saved: the song changed on disk meanwhile. Save again to keep them.");
            }
            SaveStatusText = failed == 0 ? $"Saved {DateTime.Now:HH:mm}" : $"Saved · {failed} thumbnail{(failed == 1 ? "" : "s")} failed (see the log)";
        }
        finally { _renderingThumbs = false; }
    }

    /// <summary>For songs saved before ffmpeg was set up, or after the media file moved: render every missing or stale thumbnail.</summary>
    [RelayCommand]
    private async Task RenderThumbnailsAsync()
    {
        if (_song is null) return;
        if (IsDirty || _isNew) { await SaveAsync(); return; }
        foreach (var s in Segments) s.RefreshThumb(_services.Library);
        if (!Segments.Any(s => s.ThumbNeedsUpdate)) { _shell.ShowToast("All thumbnails are up to date"); return; }
        await GenerateThumbnailsAsync(_song);
    }

    /// <summary>Deletes a song from the library: the open one, or one right-clicked in the sidebar.</summary>
    [RelayCommand]
    private void DeleteSong(SongListItem? item)
    {
        var target = item?.Song ?? (_isNew ? null : _song);
        if (target is null) return;
        var title = item is null ? Title : target.Title;
        var choice = Alert.Show(
            $"Delete “{title}”?",
            "It's removed from the library and from any setlists that use it, on every PC that shares the library. A backup copy stays in the library folder.",
            new AlertButton("delete", "Delete Song", AlertRole.Destructive | AlertRole.Leading),
            new AlertButton("cancel", "Cancel", AlertRole.Default | AlertRole.Cancel));
        if (choice != "delete") return;
        _services.Library.DeleteSong(target.Id);
        if (_song?.Id == target.Id)
        {
            _song = null; HasSong = false; IsDirty = false; Segments.Clear(); Frames.Clear(); PreviewImage = null; ProxyPath = null; IsPlaying = false;
            View = "empty";
        }
        RefreshSongList();
        _shell.ShowToast($"Deleted {title}");
    }

    /// <summary>Test aid for screenshots: shows the delete question for the open song without acting on it.</summary>
    internal void ConfirmDeleteSongForCapture()
    {
        Alert.Show($"Delete “{Title}”?",
            "It's removed from the library and from any setlists that use it, on every PC that shares the library. A backup copy stays in the library folder.",
            new AlertButton("delete", "Delete Song", AlertRole.Destructive | AlertRole.Leading),
            new AlertButton("cancel", "Cancel", AlertRole.Default | AlertRole.Cancel));
    }

    [RelayCommand] private void OpenLibraryFolder() { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", _services.Library.RootPath) { UseShellExecute = true }); } catch { } }

    // ------------------------------------------------------------------ setlists (sidebar + editor)

    [RelayCommand]
    private void NewSetlist()
    {
        var name = Setlists.CreateNew();
        if (name is null) return;
        SelectedSetlistName = null;
        SelectedSetlistName = name;
    }

    [RelayCommand] private void RenameSetlist(string? name) { var renamed = Setlists.Rename(name ?? SelectedSetlistName); if (renamed is not null) { SelectedSetlistName = null; SelectedSetlistName = renamed; } }
    [RelayCommand] private void DuplicateSetlist(string? name) { var copy = Setlists.Duplicate(name ?? SelectedSetlistName); if (copy is not null) { SelectedSetlistName = null; SelectedSetlistName = copy; } }
    [RelayCommand]
    private void DeleteSetlist(string? name)
    {
        name ??= SelectedSetlistName;
        if (!Setlists.Delete(name)) return;
        if (SelectedSetlistName == name || IsSetlistView) { SelectedSetlistName = null; View = HasSong ? "song" : "empty"; if (HasSong) SelectSidebarSong(_song?.Id); }
    }
    [RelayCommand] private void UseSetlistInShow(string? name) => Setlists.MakeActive(name ?? SelectedSetlistName);
}
