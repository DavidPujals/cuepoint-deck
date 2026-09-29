using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SegmentDeck.Core;
using SegmentDeck.Core.Library;
using SegmentDeck.Core.Logging;
using SegmentDeck.Core.Playback;
using SegmentDeck.Core.Resolume;
using SegmentDeck.Core.Settings;

namespace SegmentDeck.App.ViewModels;

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
    [ObservableProperty] private string _defaultTriggerHint = "";

    // ---- setlist strip
    /// <summary>Dropdown entry that builds the running order live from Resolume's columns on the song layer.</summary>
    public const string ResolumeSetlistName = "◆ From Resolume columns";
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
        _services.MatchesChanged += () => Post(() => { if (ActiveSetlistName == ResolumeSetlistName) LoadActiveSetlist(); else RefreshLive(); });
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
        ctl.Rejected += r => Post(() => OnRejected(r));
        ctl.Fired += (s, k) => Post(() => _shell.Flash($"{k}: {s.Name}", transient: true));
    }

    private void Post(Action a) => _dispatcher.BeginInvoke(a);

    private void ApplySettings()
    {
        CardScale = Math.Clamp(_services.Settings.CardScale, 0.6, 2.0);
        DefaultTriggerHint = _services.Settings.DefaultTrigger == TriggerMode.Queue
            ? "Click or number key = Queue · Shift = Cut · Right-click for both · Esc clears the queue"
            : "Click or number key = Cut · Shift = Queue · Right-click for both · Esc clears the queue";
        RefreshLive();
        RefreshSetlists();
    }

    // ------------------------------------------------------------------ live state

    private void RefreshLive()
    {
        var conn = _services.Connection;
        var clip = conn.WatchedClip;
        var song = _services.CurrentSong;

        // Rebuild when the song changes, and also when the library handed us a fresh copy of the same song
        // (a rescan after an edit, or thumbnails rendered), so the cards pick up new thumbnails and notes.
        if (song is null || song.Id != _songIdShown || !ReferenceEquals(_song, song)) BuildSong(song, clip);

        IsUnknownClip = song is null && clip is not null && clip.IsConnected;
        UnknownClipName = clip?.Name ?? "";

        var readOnly = _services.Settings.Role == AppRole.Follow;
        var connected = conn.State == ConnectionState.Connected;
        var live = clip is { IsConnected: true } && song is not null;
        CanFire = !readOnly && connected && song is not null && (live || _services.Settings.LaunchSongsFromSetlist);
        CannotFireReason = readOnly ? "FOLLOW: read-only" : !connected ? "Not connected to Resolume" : song is null ? "" : !live ? "Clip not live" : "";
        CanLaunch = !readOnly && connected && _services.Settings.LaunchSongsFromSetlist && SelectedSetlistIndex >= 0;

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
        RefreshQueue();
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
            Cards.Add(new SegmentCardViewModel(this, song, seg, i, image));
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

    /// <summary>30 Hz: progress bars, times, live/next highlights and the queue countdown.</summary>
    private void Tick()
    {
        var song = _song;
        if (song is null) return;
        var est = _services.Estimator.EstimateMs();
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
                card.QueueText = countdown is double c ? (_services.Estimator.IsPaused ? "QUEUED · paused" : $"QUEUED · {c / 1000:0.0} s") : "QUEUED";
            }
        }
    }

    // ------------------------------------------------------------------ firing

    public async Task FireAsync(SegmentCardViewModel card, TriggerMode mode)
    {
        if (_services.Settings.Role == AppRole.Follow) { _shell.Flash("FOLLOW role: this instance cannot fire segments", transient: true); return; }
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
        switch (e.Key)
        {
            case Key.Escape: _services.Controller.ClearQueue(); return true;
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
            SetlistNames.Add(ResolumeSetlistName);
            foreach (var s in lib.Setlists) SetlistNames.Add(s.Name);
            var wanted = _services.Settings.ActiveSetlist;
            ActiveSetlistName = wanted is not null && SetlistNames.Contains(wanted) ? wanted : null;
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
        if (ActiveSetlistName == ResolumeSetlistName) BuildFromResolumeColumns();
        else
        {
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
        }
        var idx = selectedKey is null ? -1 : SetlistSongs.ToList().FindIndex(i => ItemKey(i) == selectedKey);
        SelectedSetlistIndex = -1;
        if (idx >= 0) SetSelected(idx);
        RefreshLive();
    }

    private static string ItemKey(SetlistSongItem i) => i.Clip is not null ? "clip:" + i.Clip.ClipId : "song:" + i.Song?.Id;

    /// <summary>One entry per column that has a clip on the song layer, in column order, named after the column.
    /// Entries whose clip is in the library link to the song; the rest can still be launched from the top.</summary>
    private void BuildFromResolumeColumns()
    {
        var comp = _services.Connection.Composition;
        var layer = comp?.Layer(_services.Settings.SongLayer);
        if (comp is null || layer is null) return;
        foreach (var clip in layer.Clips)
        {
            if (clip.IsEmpty) continue;
            var song = _services.Matches.SongForClip(clip.ClipId);
            var columnName = comp.Column(clip.Column)?.Name ?? "";
            var title = columnName.Length > 0 ? columnName : song?.Title ?? clip.Name;
            SetlistSongs.Add(new SetlistSongItem
            {
                Song = song,
                Clip = clip,
                Title = title,
                Detail = song is null ? $"Column {clip.Column} · not in the library" : $"Column {clip.Column} · {song.Title}",
                IsAvailable = true,
            });
        }
    }

    public void SetSelected(int index)
    {
        if (index < 0 || index >= SetlistSongs.Count) return;
        for (int i = 0; i < SetlistSongs.Count; i++) SetlistSongs[i].IsSelected = i == index;
        SelectedSetlistIndex = index;
        CanLaunch = _services.Settings.Role != AppRole.Follow && _services.Connection.State == ConnectionState.Connected && _services.Settings.LaunchSongsFromSetlist;
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

    [RelayCommand]
    private void ManageSetlists() => _shell.OpenSetlists();

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
