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
using SegmentDeck.Core;
using SegmentDeck.Core.Library;
using SegmentDeck.Core.Logging;
using SegmentDeck.Core.Matching;
using SegmentDeck.Core.Playback;
using SegmentDeck.Core.Resolume;

namespace SegmentDeck.App.ViewModels;

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
            OnPropertyChanged(); OnPropertyChanged(nameof(StartText));
            _owner.MarkDirty();
            _owner.Resort();
            _owner.OnSegmentStartChanged(this);
        }
    }
    public string StartText
    {
        get => SegmentController.Fmt(Segment.StartMs);
        set { if (TryParseTime(value, out var ms)) StartMs = ms; else OnPropertyChanged(); }
    }

    [ObservableProperty] private BitmapImage? _thumb;
    [ObservableProperty] private string _thumbStatus = "";
    public bool ThumbNeedsUpdate => Segment.Thumb is null || Segment.ThumbAtMs != Segment.StartMs;

    public void RefreshThumb(SongLibrary lib)
    {
        Thumb = Segment.Thumb is null ? null : ThumbCache.Get(lib.ResolveLibraryPath(Segment.Thumb), 320);
        ThumbStatus = Segment.Thumb is null ? "no thumbnail yet" : ThumbNeedsUpdate ? "thumbnail out of date" : "";
        OnPropertyChanged(nameof(ThumbNeedsUpdate));
    }

    public static bool TryParseTime(string text, out long ms)
    {
        ms = 0;
        text = text.Trim();
        if (text.Length == 0) return false;
        double total = 0;
        foreach (var part in text.Split(':'))
        {
            if (!double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return false;
            total = total * 60 + v;
        }
        ms = (long)Math.Round(total * 1000);
        return true;
    }
}

public sealed class FilmstripFrame
{
    public required int Index { get; init; }
    public required double TimeMs { get; init; }
    public required string Path { get; init; }
    public string TimeText => SegmentController.Fmt(TimeMs);
    public BitmapImage? Image => ThumbCache.Get(Path, 192);
}

public sealed class CompositionClipItem
{
    public required ClipInfo Clip { get; init; }
    public string Display => $"{Clip.Location}  {Clip.Name}   ({Path.GetFileName(Clip.FilePath)})";
}

public sealed class SongListItem
{
    public required Song Song { get; init; }
    public string Title => Song.Title;
    public string Detail => $"{Song.Segments.Count} segments · {Song.Clip.FileName}";
}

/// <summary>Edit mode. Reads from Resolume (playhead) and runs ffmpeg; never sends anything to Resolume.</summary>
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

    public ObservableCollection<SongListItem> Songs { get; } = new();
    [ObservableProperty] private SongListItem? _selectedSongItem;
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
    public long DurationMs => _song?.DurationMs ?? 0;

    public ObservableCollection<SegmentEditItem> Segments { get; } = new();
    [ObservableProperty] private SegmentEditItem? _selectedSegment;
    public string[] QuickNamesList => QuickNames;
    public string[] PaletteList => Palette;

    // ---- Resolume playhead
    [ObservableProperty] private bool _playheadAvailable;
    [ObservableProperty] private string _playheadText = "--:--.---";
    [ObservableProperty] private string _playheadHint = "";

    // ---- scrubber
    public ObservableCollection<FilmstripFrame> Frames { get; } = new();
    [ObservableProperty] private double _scrubMs;
    [ObservableProperty] private string _scrubText = "00:00.000";
    [ObservableProperty] private BitmapImage? _previewImage;
    [ObservableProperty] private string _scrubStatus = "";
    [ObservableProperty] private bool _scrubberAvailable;
    [ObservableProperty] private double _frameStepMs = 40;
    [ObservableProperty] private string _sourceFileStatus = "";
    [ObservableProperty] private string _previewKind = "";
    private string? _localSourceFile;
    private CancellationTokenSource? _exactFrameCts;
    private string? _filmstripDir;

    /// <summary>Selecting a segment, or moving its start, shows that exact frame in the scrubber.</summary>
    partial void OnSelectedSegmentChanged(SegmentEditItem? value)
    {
        if (value is not null) ScrubMs = value.StartMs;
    }

    public void OnSegmentStartChanged(SegmentEditItem item)
    {
        if (ReferenceEquals(item, SelectedSegment)) ScrubMs = item.StartMs;
    }

    public EditViewModel(AppServices services, ShellViewModel shell, Dispatcher dispatcher)
    {
        _services = services;
        _shell = shell;
        _dispatcher = dispatcher;
        _services.LibraryChanged += () => _dispatcher.BeginInvoke(RefreshSongList);
        _services.LiveChanged += () => _dispatcher.BeginInvoke(RefreshClipStatus);
        _services.MatchesChanged += () => _dispatcher.BeginInvoke(RefreshClipStatus);
        RefreshSongList();
        RefreshCompositionClips();
        _timer.Tick += (_, _) => TickPlayhead();
        _timer.Start();
    }

    public void Stop() { _timer.Stop(); _filmstripCts?.Cancel(); }

    // ------------------------------------------------------------------ song list

    private void RefreshSongList()
    {
        var keep = SelectedSongItem?.Song.Id;
        Songs.Clear();
        foreach (var s in _services.Library.Songs) Songs.Add(new SongListItem { Song = s });
        if (keep is not null) { var again = Songs.FirstOrDefault(i => i.Song.Id == keep); if (again is not null) { _suppressSelect = true; SelectedSongItem = again; _suppressSelect = false; } }
        RefreshCompositionClips();
    }

    private bool _suppressSelect;
    partial void OnSelectedSongItemChanged(SongListItem? value)
    {
        if (_suppressSelect || value is null) return;
        if (!ConfirmDiscard()) { _suppressSelect = true; SelectedSongItem = Songs.FirstOrDefault(i => i.Song.Id == _song?.Id); _suppressSelect = false; return; }
        Load(Json.Deserialize<Song>(Json.Serialize(value.Song))!, isNew: false);
    }

    private void RefreshCompositionClips()
    {
        CompositionClips.Clear();
        var comp = _services.Connection.Composition;
        if (comp is null) return;
        foreach (var c in comp.Clips.Where(c => !c.IsEmpty && !string.IsNullOrEmpty(c.FilePath)))
            CompositionClips.Add(new CompositionClipItem { Clip = c });
    }

    // ------------------------------------------------------------------ load / new

    public void LoadSong(Song song) => Load(Json.Deserialize<Song>(Json.Serialize(song))!, isNew: false);

    public void NewFromClip(ClipInfo clip)
    {
        if (!ConfirmDiscard()) return;
        var song = new Song
        {
            Title = clip.Name,
            Clip = new ClipRef { FilePath = clip.FilePath, FileName = Path.GetFileName(clip.FilePath), ClipName = clip.Name },
            DurationMs = (long)Math.Round(clip.DurationMs),
        };
        Load(song, isNew: true);
    }

    [RelayCommand]
    private void NewFromComposition()
    {
        var window = new Views.PickClipWindow(CompositionClips.ToList()) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() == true && window.Selected is not null) NewFromClip(window.Selected.Clip);
    }

    [RelayCommand]
    private async Task NewFromFileAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose the song's media file", Filter = "Video files|*.mov;*.mp4;*.avi;*.mkv;*.m4v;*.dxv|All files|*.*" };
        if (dlg.ShowDialog() != true || !ConfirmDiscard()) return;
        var path = dlg.FileName;
        var song = new Song
        {
            Title = Path.GetFileNameWithoutExtension(path),
            Clip = new ClipRef { FilePath = path, FileName = Path.GetFileName(path), ClipName = Path.GetFileNameWithoutExtension(path) },
        };
        var inComp = _services.Connection.Composition?.Clips.FirstOrDefault(c => PathNorm.Key(c.FilePath) == PathNorm.Key(path));
        if (inComp is not null) song.DurationMs = (long)Math.Round(inComp.DurationMs);
        else
        {
            SaveStatusText = "Reading duration with ffprobe…";
            var d = await _services.Ffmpeg.DurationMsAsync(path);
            song.DurationMs = d is double ms ? (long)Math.Round(ms) : 0;
            if (d is null) _shell.Flash("Could not read the file's duration. Set it by hand or load the clip in Resolume.", transient: true, warn: true);
        }
        Load(song, isNew: true);
    }

    private void Load(Song song, bool isNew)
    {
        _filmstripCts?.Cancel();
        _song = song;
        _isNew = isNew;
        song.SortSegments();
        HasSong = true;
        Title = song.Title;
        Artist = song.Artist;
        ClipFileText = song.Clip.FilePath;
        DurationText = SegmentController.Fmt(song.DurationMs);
        Segments.Clear();
        foreach (var s in song.Segments) Segments.Add(new SegmentEditItem(this, s));
        foreach (var s in Segments) s.RefreshThumb(_services.Library);
        SelectedSegment = Segments.FirstOrDefault();
        IsDirty = isNew;
        SaveStatusText = isNew ? "New song (not saved yet)" : $"Loaded from {Path.GetFileName(_services.Library.SongPath(song.Id))}";
        OnPropertyChanged(nameof(DurationMs));
        RefreshClipStatus();
        _ = PrepareScrubberAsync();
    }

    private void RefreshClipStatus()
    {
        if (_song is null) return;
        var clip = FindClip();
        if (clip is null) ClipStatusText = "Not in the current Resolume composition";
        else
        {
            ClipStatusText = $"In Resolume at {clip.Location} · {(clip.IsConnected ? "LIVE" : clip.ConnectedState)} · {clip.Fps:0.##} fps";
            FrameStepMs = clip.Fps > 0 ? 1000.0 / clip.Fps : 40;
            if (_song.DurationMs == 0 && clip.DurationMs > 0) { _song.DurationMs = (long)Math.Round(clip.DurationMs); DurationText = SegmentController.Fmt(_song.DurationMs); OnPropertyChanged(nameof(DurationMs)); }
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

    public void MarkDirty() { IsDirty = true; SaveStatusText = "Unsaved changes"; }

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
        MarkDirty();
        Log.Info($"Segment added at {SegmentController.Fmt(seg.StartMs)} ({source})");
    }

    private string NextName()
    {
        var used = Segments.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var n in new[] { "Intro", "Verse 1", "Chorus", "Verse 2", "Bridge", "Outro" }) if (!used.Contains(n)) return n;
        return $"Segment {Segments.Count + 1}";
    }

    [RelayCommand] private void MarkAtPlayhead() { if (_services.Estimator.EstimateMs() is double ms && PlayheadAvailable) AddSegmentAt(ms, "Resolume playhead"); else _shell.Flash("The song's clip is not live in Resolume. Connect it, or use the scrubber.", transient: true, warn: true); }
    [RelayCommand] private void MarkAtScrubber() => AddSegmentAt(ScrubMs, "scrubber");
    [RelayCommand] private void DeleteSegment()
    {
        if (_song is null || SelectedSegment is null) return;
        var idx = Segments.IndexOf(SelectedSegment);
        _song.Segments.Remove(SelectedSegment.Segment);
        Segments.Remove(SelectedSegment);
        SelectedSegment = Segments.Count == 0 ? null : Segments[Math.Min(idx, Segments.Count - 1)];
        MarkDirty();
    }
    [RelayCommand] private void Nudge(string amount) { if (SelectedSegment is null) return; var delta = amount switch { "-frame" => -FrameStepMs, "+frame" => FrameStepMs, "-100" => -100, "+100" => 100, _ => 0 }; SelectedSegment.StartMs = (long)Math.Round(SelectedSegment.StartMs + delta); }
    [RelayCommand] private void SetName(string name) { if (SelectedSegment is not null) SelectedSegment.Name = name; }
    [RelayCommand] private void SetColor(string color) { if (SelectedSegment is not null) SelectedSegment.Color = color; }
    [RelayCommand] private void SetStartFromScrubber() { if (SelectedSegment is not null) SelectedSegment.StartMs = (long)Math.Round(ScrubMs); }
    [RelayCommand] private void SetStartFromPlayhead() { if (SelectedSegment is not null && _services.Estimator.EstimateMs() is double ms && PlayheadAvailable) SelectedSegment.StartMs = (long)Math.Round(ms); }
    [RelayCommand] private void SeekScrubberToSegment() { if (SelectedSegment is not null) ScrubMs = SelectedSegment.StartMs; }

    private void TickPlayhead()
    {
        var clip = FindClip();
        var watched = _services.Connection.WatchedClip;
        var available = clip is not null && watched is not null && clip.ClipId == watched.ClipId && clip.IsConnected && _services.Estimator.EstimateMs() is not null;
        PlayheadAvailable = available;
        PlayheadText = available ? SegmentController.Fmt(_services.Estimator.EstimateMs()!.Value) : "--:--.---";
        PlayheadHint = available ? (_services.Estimator.IsPaused ? "paused in Resolume" : "playing in Resolume") : clip is null ? "clip not in composition" : !clip.IsConnected ? "clip not live: connect it in Resolume to mark by ear" : "";
    }

    // ------------------------------------------------------------------ scrubber

    private async Task PrepareScrubberAsync()
    {
        Frames.Clear();
        PreviewImage = null;
        ScrubberAvailable = false;
        if (_song is null) return;
        ScrubMs = 0;

        _localSourceFile = ResolveLocalFile();
        if (_localSourceFile is null)
        {
            var mapper = new PathMapper(_services.Settings.PathMappings);
            var tried = string.Join(", ", mapper.Variants(_song.Clip.FilePath));
            SourceFileStatus = $"Can't read the media file on this PC (tried {tried}). Add a path mapping in Settings, or do this edit on the Resolume PC. Thumbnails will be placeholders.";
            ScrubStatus = "";
            return;
        }
        SourceFileStatus = $"Media file: {_localSourceFile}";
        if (!_services.Ffmpeg.IsAvailable)
        {
            ScrubStatus = _services.Ffmpeg.StatusText + ". Set the ffmpeg path in Settings to enable the scrubber and thumbnails.";
            return;
        }

        var dir = FilmstripDir(_song, _localSourceFile);
        _filmstripDir = dir;
        var cts = _filmstripCts = new CancellationTokenSource();
        if (!File.Exists(Path.Combine(dir, "done.txt")))
        {
            ScrubStatus = "Building preview frames (low priority, one every 2 s)…";
            var result = await _services.Ffmpeg.FilmstripAsync(_localSourceFile, dir, 2, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (!result.Ok) { ScrubStatus = $"Preview frames failed: {result.Error}"; return; }
        }
        var files = Directory.GetFiles(dir, "*.jpg").OrderBy(f => f).ToList();
        for (int i = 0; i < files.Count; i++) Frames.Add(new FilmstripFrame { Index = i, TimeMs = i * 2000.0, Path = files[i] });
        ScrubberAvailable = Frames.Count > 0;
        ScrubStatus = $"{Frames.Count} preview frames · drag the playhead, ←/→ one frame, Shift+←/→ one second, M marks";
        UpdatePreview();
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
        ScrubText = SegmentController.Fmt(value);
        UpdatePreview();
        _ = RequestExactFrameAsync(value);
    }

    /// <summary>Immediate feedback: the nearest filmstrip frame (one every 2 s). The exact frame follows shortly after.</summary>
    private void UpdatePreview()
    {
        if (Frames.Count == 0) { PreviewImage = null; PreviewKind = ""; return; }
        var idx = Math.Clamp((int)(ScrubMs / 2000.0), 0, Frames.Count - 1);
        PreviewImage = ThumbCache.Get(Frames[idx].Path, 480);
        PreviewKind = $"nearest preview frame ({Frames[idx].TimeText})";
    }

    /// <summary>After the scrubber settles, pull the exact frame at that time with ffmpeg so the operator sees the frame
    /// the segment will start on. Cached next to the filmstrip; one job at a time, low priority.</summary>
    private async Task RequestExactFrameAsync(double ms)
    {
        _exactFrameCts?.Cancel();
        var cts = _exactFrameCts = new CancellationTokenSource();
        if (_song is null || _localSourceFile is null || _filmstripDir is null || !_services.Ffmpeg.IsAvailable) return;
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
            if (cts.IsCancellationRequested || Math.Abs(ScrubMs - ms) > 0.5) return;
            PreviewImage = ThumbCache.Get(path, 480);
            PreviewKind = $"exact frame at {SegmentController.Fmt(exactMs)}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"Exact frame preview failed: {ex.Message}"); }
    }

    public void ScrubStep(int direction, bool bySecond)
    {
        var step = bySecond ? 1000 : FrameStepMs;
        ScrubMs = Math.Clamp(ScrubMs + direction * step, 0, Math.Max(0, DurationMs));
    }

    public void ScrubTo(FilmstripFrame frame) => ScrubMs = frame.TimeMs;

    /// <summary>Keyboard while Edit mode is active and no text box has focus.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.M: if (PlayheadAvailable && !shift) MarkAtPlayheadCommand.Execute(null); else MarkAtScrubberCommand.Execute(null); return true;
            case Key.Left: ScrubStep(-1, shift); return true;
            case Key.Right: ScrubStep(1, shift); return true;
            case Key.Escape: _shell.BackToShow(); return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ save / delete

    private bool ConfirmDiscard()
    {
        if (!IsDirty || _song is null) return true;
        var r = Views.DarkMessageBox.Show($"\"{_song.Title}\" has unsaved changes. Discard them?", "Segment Deck", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return r == MessageBoxResult.Yes;
    }

    public bool CanLeave() => ConfirmDiscard();

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_song is null) return;
        _song.Title = Title.Trim().Length == 0 ? _song.Clip.ClipName : Title.Trim();
        Title = _song.Title;
        var lib = _services.Library;
        var result = lib.SaveSong(_song);
        if (result.Status == SaveStatus.Conflict)
        {
            var r = Views.DarkMessageBox.Show($"\"{_song.Title}\" was changed on disk since it was loaded (probably from the other PC).\n\nYes = overwrite with your version\nNo = reload the disk version and lose your changes",
                "Segment Deck", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes) result = lib.SaveSong(_song, overwrite: true);
            else if (r == MessageBoxResult.No) { var again = lib.ReloadSong(_song.Id); if (again is not null) Load(again, false); return; }
            else return;
        }
        if (!result.Ok)
        {
            SaveStatusText = $"Save failed: {result.Error}";
            _shell.Flash($"Save failed: {result.Error}", transient: true, warn: true);
            return;
        }
        _isNew = false;
        IsDirty = false;
        SaveStatusText = $"Saved {DateTime.Now:HH:mm:ss}";
        _suppressSelect = true;
        RefreshSongList();
        _suppressSelect = false;
        await GenerateThumbnailsAsync(_song);
    }

    private async Task GenerateThumbnailsAsync(Song song)
    {
        var todo = Segments.Where(s => s.ThumbNeedsUpdate).ToList();
        if (todo.Count == 0) return;
        var file = _localSourceFile ?? ResolveLocalFile();
        if (file is null || !_services.Ffmpeg.IsAvailable)
        {
            foreach (var s in todo) s.ThumbStatus = file is null ? "placeholder (media file not readable here)" : "placeholder (ffmpeg not available)";
            Log.Warn($"Thumbnails skipped for \"{song.Title}\": {(file is null ? "media file not readable on this PC" : "ffmpeg not available")}");
            return;
        }
        var lib = _services.Library;
        int done = 0, failed = 0;
        foreach (var item in todo)
        {
            item.ThumbStatus = "rendering…";
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
            else { item.ThumbStatus = $"placeholder ({result.Error})"; failed++; }
        }
        if (done > 0 && !IsDirty)
        {
            var r = lib.SaveSong(song, overwrite: false);
            if (r.Status == SaveStatus.Conflict) Log.Warn("Thumbnail paths not saved: the song changed on disk meanwhile. Save again to keep them.");
        }
        SaveStatusText = failed == 0 ? $"Saved · {done} thumbnail(s) rendered" : $"Saved · {done} thumbnail(s) rendered, {failed} failed (see log)";
    }

    /// <summary>For songs saved before ffmpeg was set up, or after the media file moved: render every missing or stale thumbnail.</summary>
    [RelayCommand]
    private async Task RenderThumbnailsAsync()
    {
        if (_song is null) return;
        if (IsDirty || _isNew) { await SaveAsync(); return; }
        foreach (var s in Segments) s.RefreshThumb(_services.Library);
        if (!Segments.Any(s => s.ThumbNeedsUpdate)) { SaveStatusText = "All thumbnails are up to date"; return; }
        await GenerateThumbnailsAsync(_song);
    }

    [RelayCommand]
    private void DeleteSong()
    {
        if (_song is null || _isNew) return;
        if (Views.DarkMessageBox.Show($"Delete \"{_song.Title}\" from the library? A .bak copy is kept.", "Segment Deck", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _services.Library.DeleteSong(_song.Id);
        _song = null; HasSong = false; IsDirty = false; Segments.Clear(); Frames.Clear(); PreviewImage = null;
        RefreshSongList();
    }

    [RelayCommand] private void BackToShow() => _shell.BackToShow();
    [RelayCommand] private void OpenLibraryFolder() { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", _services.Library.RootPath) { UseShellExecute = true }); } catch { } }
}
