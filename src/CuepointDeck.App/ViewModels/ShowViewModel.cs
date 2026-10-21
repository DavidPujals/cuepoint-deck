using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CuepointDeck.Core;
using CuepointDeck.Core.Library;
using CuepointDeck.Core.Logging;
using CuepointDeck.Core.Playback;
using CuepointDeck.Core.Resolume;
using CuepointDeck.Core.Settings;

namespace CuepointDeck.App.ViewModels;

public partial class SetlistSongItem : ObservableObject
{
    /// <summary>The library song, or null for a Resolume column whose clip is not in the library yet.</summary>
    public Song? Song { get; init; }
    /// <summary>The clip on the song layer, when the entry came from Resolume's columns.</summary>
    public ClipInfo? Clip { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public bool IsUnknown => Song is null;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isAvailable;
}

/// <summary>Show mode: the view the operator uses during a service.</summary>
public partial class ShowViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private Song? _song;
    private string? _songIdShown;
    private int _liveIndex = -2;
    private int _nextIndex = -2;
    private string? _queuedSegmentId;
    private CancellationTokenSource? _flashCts;

    // ---- current song header
    [ObservableProperty] private bool _hasSong;
    [ObservableProperty] private string _songTitle = "";
    [ObservableProperty] private string _songSubtitle = "";
    [ObservableProperty] private bool _isUnknownClip;
    [ObservableProperty] private string _unknownClipName = "";
    [ObservableProperty] private string _emptyTitle = "No Song Live";

    // ---- density (set by the window from its height)
    [ObservableProperty] private bool _isCompact;
    public bool ShowFullHeader => HasSong && !IsCompact;
    public bool ShowSlimHeader => HasSong && IsCompact;
    partial void OnIsCompactChanged(bool value) { OnPropertyChanged(nameof(ShowFullHeader)); OnPropertyChanged(nameof(ShowSlimHeader)); }
    partial void OnHasSongChanged(bool value) { OnPropertyChanged(nameof(ShowFullHeader)); OnPropertyChanged(nameof(ShowSlimHeader)); }
    [ObservableProperty] private string _emptyMessage = "";
    [ObservableProperty] private double _clipProgress;
    [ObservableProperty] private string _elapsedText = "--:--";
    [ObservableProperty] private string _remainingText = "--:--";
    [ObservableProperty] private IReadOnlyList<double> _ticks = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<Brush> _tickColors = Array.Empty<Brush>();
    [ObservableProperty] private IReadOnlyList<(double from, double to)> _gaps = Array.Empty<(double, double)>();
    [ObservableProperty] private Brush _progressFill = Brushes.DodgerBlue;

    // ---- cards
    public ObservableCollection<SegmentCardViewModel> Cards { get; } = new();
    [ObservableProperty] private bool _canFire;
    [ObservableProperty] private string _cannotFireReason = "";
    [ObservableProperty] private double _cardScale = 1.0;

    // ---- setlist strip
    /// <summary>Dropdown entry that builds the running order live from Resolume's columns on the song layer.</summary>
    [ObservableProperty] private bool _isSetlistVisible = true;
    public ObservableCollection<string> SetlistNames { get; } = new();
    [ObservableProperty] private string? _activeSetlistName;
    public ObservableCollection<SetlistSongItem> SetlistSongs { get; } = new();
    [ObservableProperty] private int _selectedSetlistIndex = -1;
    [ObservableProperty] private bool _canLaunch;
    private bool _suppressSetlistChange;

    public ShowViewModel(AppServices services, ShellViewModel shell, Dispatcher dispatcher)
    {
        _services = services;
        _shell = shell;
        _dispatcher = dispatcher;

        _services.LiveChanged += () => Post(RefreshLive);
        _services.MatchesChanged += () => Post(RefreshLive);
        _services.SettingsChanged += () => Post(() => { HookController(); ApplySettings(); });
        _services.LibraryChanged += () => Post(RefreshSetlists);
        HookController();

        ApplySettings();
        RefreshSetlists();
        RefreshLive();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private SegmentController? _hooked;

    /// <summary>Subscribes to the current controller once; a new controller appears when the Resolume host changes.</summary>
    private void HookController()
    {
        var ctl = _services.Controller;
        if (ReferenceEquals(ctl, _hooked)) return;
        _hooked = ctl;
        ctl.CurrentSongChanged += _ => Post(RefreshLive);
        ctl.QueueChanged += () => Post(RefreshQueue);
        ctl.LoopChanged += () => Post(RefreshLoop);
        ctl.Rejected += r => Post(() => OnRejected(r));
        ctl.Fired += (s, k) => Post(() => { if (k != TriggerKind.Loop) _shell.ShowToast(k switch { TriggerKind.Cut => $"Jumped to {s.Name}", TriggerKind.Queue => $"Jumped to {s.Name} at the boundary", _ => $"Brought up at {s.Name}" }); });
    }

    private void Post(Action a) => _dispatcher.BeginInvoke(a);

    private void ApplySettings()
    {
        var scale = Math.Clamp(_services.Settings.CardScale, 0.6, 2.0);
        var rebuild = Math.Abs(scale - CardScale) > 0.001 || _gestureMode != _services.Settings.DefaultTrigger;
        CardScale = scale;
        _gestureMode = _services.Settings.DefaultTrigger;
        if (rebuild && _song is not null) BuildSong(_song, _services.Connection.WatchedClip);
        RefreshLive();
        RefreshSetlists();
    }

    // ------------------------------------------------------------------ live state

    private TriggerMode _gestureMode = TriggerMode.Queue;

    // Test aid (DebugCapture only): show a library song as if it were live, so the deck can be captured without Resolume.
    private Song? _captureSong;
    private double? _capturePos;
    internal void SimulateLiveForCapture(Song song, double positionMs, int queuedIndex, int loopIndex)
    {
        _captureSong = song;
        _capturePos = positionMs;
        BuildSong(song, null);
        RefreshLive();
        SongSubtitle = $"L{_services.Settings.SongLayer} C1  ·  {song.Segments.Count} segments  ·  {Timecode.Format(song.DurationMs, song.Fps)}";
        if (queuedIndex >= 0 && queuedIndex < Cards.Count) { Cards[queuedIndex].IsQueued = true; Cards[queuedIndex].QueueText = "QUEUED · 4.2 s"; }
        if (loopIndex >= 0 && loopIndex < Cards.Count) Cards[loopIndex].IsLooping = true;
    }

    /// <summary>Ctrl+ / Ctrl- / Ctrl+0 from the keyboard: bigger, smaller, or back to 1.0. Saved like any setting.</summary>
    public void Zoom(double delta)
    {
        var s = _services.Settings.Clone();
        s.CardScale = delta == 0 ? 1.0 : Math.Round(Math.Clamp(s.CardScale + delta, 0.6, 2.0), 1);
        if (Math.Abs(s.CardScale - _services.Settings.CardScale) < 0.001) return;
        _ = _services.ApplySettingsAsync(s);
        _shell.ShowToast($"Card size {s.CardScale:0.0}×");
    }

    private void RefreshLive()
    {
        var conn = _services.Connection;
        var clip = conn.WatchedClip;
        var song = _captureSong ?? _services.CurrentSong;

        // Rebuild when the song changes, and also when the library handed us a fresh copy of the same song
        // (a rescan after an edit, or thumbnails rendered), so the cards pick up new thumbnails and notes.
        if (song is null || song.Id != _songIdShown || !ReferenceEquals(_song, song)) BuildSong(song, clip);

        IsUnknownClip = song is null && clip is not null && clip.IsConnected;
        UnknownClipName = clip?.Name ?? "";
        var online = conn.State == ConnectionState.Connected;
        EmptyTitle = !online ? "Resolume Isn't Connected" : IsUnknownClip ? "This Clip Isn't in the Library" : "No Song Live";
        EmptyMessage = !online ? "Cuepoint Deck keeps trying every 2 seconds. Click the status at the top left for details."
                     : IsUnknownClip ? $"{clip!.Location} is playing “{clip.Name}”. Add it to the library to give it segments."
                     : SetlistSongs.Count > 0 ? "Click a song in the setlist above to bring it up in Resolume, or start one there."
                     : "Start a song in Resolume, or make a setlist in Edit mode.";

        var readOnly = _services.Settings.Role == AppRole.Follow;
        var connected = conn.State == ConnectionState.Connected;
        var live = clip is { IsConnected: true } && song is not null;
        CanFire = !readOnly && connected && song is not null && (live || _services.Settings.LaunchSongsFromSetlist);
        CannotFireReason = readOnly ? "" : !connected ? "Not connected to Resolume" : song is null ? "" : !live ? "Clip not live in Resolume" : "";
        CanLaunch = !readOnly && connected && SelectedSetlistIndex >= 0;

        foreach (var item in SetlistSongs)
        {
            item.IsCurrent = item.Clip is not null ? clip is { IsConnected: true } && item.Clip.ClipId == clip.ClipId
                                                   : song is not null && item.Song?.Id == song.Id;
            item.IsAvailable = item.Clip is not null || (item.Song is not null && _services.Matches.For(item.Song.Id)?.IsAvailable == true);
        }
        if (SelectedSetlistIndex < 0)
        {
            var idx = SetlistSongs.ToList().FindIndex(i => i.IsCurrent);
            if (idx >= 0) SetSelected(idx);
        }
        if (_captureSong is not null)
        {
            CanFire = true;
            CannotFireReason = "";
            foreach (var item in SetlistSongs) item.IsAvailable = true;
            return;
        }
        RefreshQueue();
        RefreshLoop();
    }

    private void BuildSong(Song? song, ClipInfo? clip)
    {
        _song = song;
        _songIdShown = song?.Id;
        _liveIndex = -2; _nextIndex = -2;
        Cards.Clear();
        HasSong = song is not null;
        if (song is null)
        {
            SongTitle = clip is { IsConnected: true } ? $"Unknown clip: {clip.Name}" : "No song live";
            SongSubtitle = clip is { IsConnected: true } ? $"{clip.Location} is playing but is not in the library" : "Connect a song clip in Resolume, or pick one from the setlist";
            Ticks = Array.Empty<double>();
            TickColors = Array.Empty<Brush>();
            Gaps = Array.Empty<(double, double)>();
            ClipProgress = 0;
            return;
        }

        SongTitle = song.Title;
        var match = _services.Matches.For(song.Id);
        SongSubtitle = match?.Clip is { } c ? $"{c.Location}  ·  {song.Segments.Count} segments  ·  {Timecode.Format(song.DurationMs, song.Fps)}" + (match.Ambiguous ? $"  ·  ⚠ {match.Warning}" : "")
                                              : "Not in the composition";
        var lib = _services.Library;
        for (int i = 0; i < song.Segments.Count; i++)
        {
            var seg = song.Segments[i];
            var thumbPath = seg.Thumb is null ? null : lib.ResolveLibraryPath(seg.Thumb);
            var image = thumbPath is null ? null : ThumbCache.Get(thumbPath, (int)(480 * Math.Max(1, CardScale)));
            var key = i < 9 ? (i + 1).ToString() : i == 9 ? "0" : "";
            var cutFirst = _services.Settings.DefaultTrigger == TriggerMode.Cut;
            Cards.Add(new SegmentCardViewModel(this, song, seg, i, image)
            {
                CutGesture = key.Length == 0 ? "" : cutFirst ? key : "Shift+" + key,
                QueueGesture = key.Length == 0 ? "" : cutFirst ? "Shift+" + key : key,
            });
        }
        var dur = Math.Max(1, (double)song.DurationMs);
        Ticks = song.Segments.Select(s => Math.Clamp(s.StartMs / dur, 0, 1)).ToList();
        var conv = new HexToBrushConverter();
        TickColors = song.Segments.Select(s => (Brush)conv.Convert(s.Color, typeof(Brush), null!, null!)).ToList();
        var gaps = new List<(double, double)>();
        for (int i = 0; i < song.Segments.Count; i++)
        {
            if (!song.HasGapAfter(i)) continue;
            var from = song.SegmentEndMs(i) / dur;
            var to = (i + 1 < song.Segments.Count ? song.Segments[i + 1].StartMs : song.DurationMs) / dur;
            gaps.Add((Math.Clamp(from, 0, 1), Math.Clamp(to, 0, 1)));
        }
        Gaps = gaps;
    }

    private void RefreshQueue()
    {
        var q = _services.Controller.Queued;
        _queuedSegmentId = q?.Segment.Id;
        foreach (var card in Cards)
        {
            card.IsQueued = card.Segment.Id == _queuedSegmentId;
            if (!card.IsQueued) card.QueueText = "";
        }
    }

    private void RefreshLoop()
    {
        var loop = _services.Controller.LoopIndex;
        foreach (var card in Cards) card.IsLooping = card.Index == loop;
    }

    public void ToggleLoop(SegmentCardViewModel card)
    {
        if (_services.Settings.Role == AppRole.Follow) { _shell.ShowToast("This PC follows, so it can't loop segments", warn: true); return; }
        var wasLooping = _services.Controller.LoopIndex == card.Index;
        _services.Controller.ToggleLoop(card.Song, card.Segment);
        if (_services.Controller.LoopIndex == card.Index) _shell.ShowToast($"Looping {card.Name}. Press L or Esc to stop.");
        else if (wasLooping) _shell.ShowToast($"Stopped looping {card.Name}");
    }

    public void EditSegment(SegmentCardViewModel card) => _shell.EnterEdit(card.Song, card.Segment.Id);

    /// <summary>30 Hz: progress bars, times, live/next highlights and the queue countdown.</summary>
    private void Tick()
    {
        var song = _song;
        if (song is null) return;
        var est = _capturePos ?? _services.Estimator.EstimateMs();
        if (est is not double pos)
        {
            ElapsedText = "--:--"; RemainingText = "--:--";
            return;
        }
        var dur = Math.Max(1, (double)song.DurationMs);
        ClipProgress = Math.Clamp(pos / dur, 0, 1);
        ElapsedText = Timecode.FormatShort(pos, song.Fps);
        RemainingText = "-" + Timecode.FormatShort(Math.Max(0, dur - pos), song.Fps);

        var live = song.SegmentIndexAt(pos);
        var next = live >= 0 ? (live + 1 < song.Segments.Count ? live + 1 : -1) : song.NextSegmentIndexAfter(pos);
        if (live != _liveIndex || next != _nextIndex)
        {
            _liveIndex = live; _nextIndex = next;
            foreach (var card in Cards) { card.IsLive = card.Index == live; card.IsNext = card.Index == next; }
            if (live >= 0 && live < Cards.Count)
            {
                var conv = new HexToBrushConverter();
                ProgressFill = (Brush)conv.Convert(song.Segments[live].Color, typeof(Brush), null!, null!);
            }
        }
        foreach (var card in Cards)
        {
            if (card.Index == live)
            {
                var start = song.Segments[live].StartMs;
                var end = song.SegmentEndMs(live);
                card.Progress = end > start ? Math.Clamp((pos - start) / (end - start), 0, 1) : 0;
            }
            else
            {
                var done = card.Index < live || (live < 0 && song.SegmentEndMs(card.Index) <= pos);
                if (card.Progress != (done ? 1 : 0)) card.Progress = done ? 1 : 0;
            }

            if (card.IsQueued)
            {
                var countdown = _services.Controller.QueueCountdownMs();
                var word = _services.Controller.Queued is { IsLoop: true } ? "LOOP" : "QUEUED";
                card.QueueText = countdown is double c ? (_services.Estimator.IsPaused ? $"{word} · paused" : $"{word} · {c / 1000:0.0} s") : word;
            }
        }
    }

    // ------------------------------------------------------------------ firing

    public async Task FireAsync(SegmentCardViewModel card, TriggerMode mode)
    {
        if (_services.Settings.Role == AppRole.Follow) { _shell.ShowToast("This PC follows, so it can't fire segments", warn: true); return; }
        var ctl = _services.Controller;
        if (mode == TriggerMode.Cut) await ctl.CutAsync(card.Song, card.Segment);
        else await ctl.QueueAsync(card.Song, card.Segment);
    }

    public Task FireDefaultAsync(SegmentCardViewModel card, bool other)
    {
        var mode = _services.Settings.DefaultTrigger;
        if (other) mode = mode == TriggerMode.Cut ? TriggerMode.Queue : TriggerMode.Cut;
        return FireAsync(card, mode);
    }

    private void OnRejected(TriggerRejected r)
    {
        _shell.Flash(r.Reason, transient: true, warn: true);
        var card = Cards.FirstOrDefault(c => c.Segment.Id == r.Segment.Id);
        if (card is null) return;
        _flashCts?.Cancel();
        var cts = _flashCts = new CancellationTokenSource();
        card.IsFlashing = true;
        _ = Task.Delay(700, cts.Token).ContinueWith(_ => Post(() => { if (!cts.IsCancellationRequested) card.IsFlashing = false; }), TaskScheduler.Default);
    }

    /// <summary>Keyboard while the Show view is active. Returns true when handled.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        int? number = e.Key switch
        {
            >= Key.D1 and <= Key.D9 => e.Key - Key.D1,
            Key.D0 => 9,
            >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1,
            Key.NumPad0 => 9,
            _ => null,
        };
        if (number is int n)
        {
            if (n < Cards.Count) _ = FireDefaultAsync(Cards[n], shift);
            return true;
        }
        var hadQueueOrLoop = _services.Controller.Queued is not null || _services.Controller.LoopIndex >= 0;
        switch (e.Key)
        {
            case Key.Escape: _services.Controller.ClearQueue(); _services.Controller.ClearLoop(); if (hadQueueOrLoop) _shell.ShowToast("Queue and loop cleared"); return true;
            case Key.L: { var live = Cards.FirstOrDefault(c => c.IsLive); if (live is not null) ToggleLoop(live); return true; }
            case Key.Left: MoveSelection(-1); return true;
            case Key.Right: MoveSelection(1); return true;
            case Key.Enter: if (CanLaunch) _ = LaunchSelectedAsync(); return true;
            case Key.E: _shell.EnterEdit(_song); return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ setlist strip

    private void RefreshSetlists()
    {
        var lib = _services.Library;
        _suppressSetlistChange = true;
        try
        {
            SetlistNames.Clear();
            foreach (var s in lib.Setlists) SetlistNames.Add(s.Name);
            var wanted = _services.Settings.ActiveSetlist;
            ActiveSetlistName = wanted is not null && SetlistNames.Contains(wanted) ? wanted : SetlistNames.FirstOrDefault();
        }
        finally { _suppressSetlistChange = false; }
        LoadActiveSetlist();
    }

    partial void OnActiveSetlistNameChanged(string? value)
    {
        if (_suppressSetlistChange) return;
        if (_services.Settings.ActiveSetlist != value)
        {
            var s = _services.Settings.Clone();
            s.ActiveSetlist = value;
            _ = _services.ApplySettingsAsync(s);
        }
        LoadActiveSetlist();
    }

    private void LoadActiveSetlist()
    {
        var selectedKey = SelectedSetlistIndex >= 0 && SelectedSetlistIndex < SetlistSongs.Count ? ItemKey(SetlistSongs[SelectedSetlistIndex]) : null;
        SetlistSongs.Clear();
        var setlist = ActiveSetlistName is null ? null : _services.Library.GetSetlist(ActiveSetlistName);
        if (setlist is not null)
        {
            foreach (var id in setlist.SongIds)
            {
                var song = _services.Library.GetSong(id);
                if (song is null) continue;
                SetlistSongs.Add(new SetlistSongItem { Song = song, Title = song.Title, IsAvailable = _services.Matches.For(id)?.IsAvailable == true, IsCurrent = _song?.Id == id });
            }
        }
        var idx = selectedKey is null ? -1 : SetlistSongs.ToList().FindIndex(i => ItemKey(i) == selectedKey);
        SelectedSetlistIndex = -1;
        if (idx >= 0) SetSelected(idx);
        RefreshLive();
    }

    private static string ItemKey(SetlistSongItem i) => i.Clip is not null ? "clip:" + i.Clip.ClipId : "song:" + i.Song?.Id;

    public void SetSelected(int index)
    {
        if (index < 0 || index >= SetlistSongs.Count) return;
        for (int i = 0; i < SetlistSongs.Count; i++) SetlistSongs[i].IsSelected = i == index;
        SelectedSetlistIndex = index;
        CanLaunch = _services.Settings.Role != AppRole.Follow && _services.Connection.State == ConnectionState.Connected;
    }

    private void MoveSelection(int delta)
    {
        if (SetlistSongs.Count == 0) return;
        var idx = SelectedSetlistIndex < 0 ? (delta > 0 ? 0 : SetlistSongs.Count - 1) : Math.Clamp(SelectedSetlistIndex + delta, 0, SetlistSongs.Count - 1);
        SetSelected(idx);
    }

    [RelayCommand]
    private void ToggleSetlist() => IsSetlistVisible = !IsSetlistVisible;

    [RelayCommand]
    private async Task LaunchSelectedAsync()
    {
        if (!CanLaunch || SelectedSetlistIndex < 0 || SelectedSetlistIndex >= SetlistSongs.Count) return;
        var item = SetlistSongs[SelectedSetlistIndex];
        if (item.Song is not null) await _services.Controller.LaunchSongAsync(item.Song);
        else if (item.Clip is not null) await _services.Controller.LaunchClipAsync(item.Clip);
    }

    /// <summary>A click on a song in the top bar: select it and bring up its column in Resolume.</summary>
    public async Task LaunchItemAsync(SetlistSongItem item)
    {
        SelectSetlistSong(item);
        if (_services.Settings.Role == AppRole.Follow) { _shell.ShowToast("This PC follows, so it can't bring up songs", warn: true); return; }
        if (_services.Connection.State != ConnectionState.Connected) { _shell.ShowToast("Resolume isn't connected", warn: true); return; }
        if (!item.IsAvailable) { _shell.ShowToast($"“{item.Title}” isn't in the Resolume composition", warn: true); return; }
        await LaunchSelectedAsync();
        _shell.ShowToast($"Bringing up {item.Title}");
    }

    [RelayCommand] private Task LaunchItem(SetlistSongItem? item) => item is null ? Task.CompletedTask : LaunchItemAsync(item);
    [RelayCommand] private void EditSong(SetlistSongItem? item) { if (item?.Song is not null) _shell.EnterEdit(item.Song); }
    [RelayCommand] private void EditSetlist() => _shell.EnterEditSetlist(ActiveSetlistName);

    [RelayCommand]
    private void AddUnknownClip()
    {
        var clip = _services.Connection.WatchedClip;
        if (clip is null) return;
        _shell.EnterEditForClip(clip);
    }

    public void SelectSetlistSong(SetlistSongItem item)
    {
        var idx = SetlistSongs.IndexOf(item);
        if (idx >= 0) SetSelected(idx);
    }

    public void Stop() => _timer.Stop();
}
