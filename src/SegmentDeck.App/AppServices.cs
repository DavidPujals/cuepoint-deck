using SegmentDeck.Core.Library;
using SegmentDeck.Core.Logging;
using SegmentDeck.Core.Matching;
using SegmentDeck.Core.Media;
using SegmentDeck.Core.Playback;
using SegmentDeck.Core.Resolume;
using SegmentDeck.Core.Settings;

namespace SegmentDeck.App;

/// <summary>Wires settings, library, connection, matching and the trigger engine together. One per process.
/// Events here fire on background threads; view models marshal to the dispatcher.</summary>
public sealed class AppServices
{
    private readonly SettingsStore _store = new();
    private readonly object _gate = new();

    public AppSettings Settings { get; private set; } = new();
    public SongLibrary Library { get; private set; } = null!;
    public ResolumeConnection Connection { get; private set; } = null!;
    public SegmentController Controller { get; private set; } = null!;
    public PlayheadEstimator Estimator { get; } = new();
    public FfmpegService Ffmpeg { get; } = new();
    public MatchTable Matches { get; private set; } = new();

    /// <summary>Matches were rebuilt (composition or library changed).</summary>
    public event Action? MatchesChanged;
    /// <summary>Connection state, connected clips or the followed clip changed.</summary>
    public event Action? LiveChanged;
    /// <summary>Settings were applied.</summary>
    public event Action? SettingsChanged;
    /// <summary>The library rescanned or saved. Survives a library path change (unlike subscribing to Library.Changed).</summary>
    public event Action? LibraryChanged;
    /// <summary>A message for the status bar (warnings from any layer).</summary>
    public event Action<string>? StatusMessage;

    public void Start()
    {
        Settings = _store.Load();
        Log.Info($"Settings: host={Settings.ResolumeHost}:{Settings.ResolumePort} role={Settings.Role} songLayer={Settings.SongLayer} library={Settings.LibraryPath} latency={Settings.LatencyOffsetMs} ms trigger={Settings.DefaultTrigger} launch={Settings.LaunchSongsFromSetlist}");
        StartLibrary(Settings.LibraryPath);
        StartConnection(Settings.ResolumeHost, Settings.ResolumePort);
        _ = Ffmpeg.ConfigureAsync(Settings.FfmpegPath).ContinueWith(t =>
        {
            if (!t.Result) Status(Ffmpeg.StatusText + ". Thumbnails will be placeholders until it is set in Settings.");
        });
    }

    public async Task StopAsync()
    {
        Controller?.Dispose();
        if (Connection is not null) await Connection.StopAsync();
    }

    private void StartLibrary(string path)
    {
        var lib = new SongLibrary(path);
        lib.Changed += () =>
        {
            if (!ReferenceEquals(Library, lib)) return;
            RebuildMatches();
            try { LibraryChanged?.Invoke(); } catch (Exception ex) { Log.Error("LibraryChanged handler", ex); }
        };
        Library = lib;
        Task.Run(lib.Rescan);
    }

    private void StartConnection(string host, int port)
    {
        var conn = new ResolumeConnection(host, port);
        var ctl = new SegmentController(conn, () => Settings, ResolveClip, Estimator);
        ctl.Warning += Status;
        conn.StateChanged += _ => RaiseLive();
        conn.CompositionChanged += _ => { RebuildMatches(); AutoWatch(); RaiseLive(); };
        conn.ClipStateChanged += clip => { if (clip.IsConnected) AutoWatch(clip); else if (clip.ClipId == conn.WatchedClip?.ClipId) AutoWatch(); RaiseLive(); };
        conn.TransportChanged += _ => RaiseLive();
        Connection = conn;
        Controller = ctl;
        conn.Start();
    }

    /// <summary>Saves settings and restarts whatever they affect.</summary>
    public async Task ApplySettingsAsync(AppSettings updated)
    {
        var old = Settings;
        Settings = updated;
        _store.Save(updated);

        if (!string.Equals(old.LibraryPath, updated.LibraryPath, StringComparison.OrdinalIgnoreCase))
        {
            Log.Info($"Library path changed to {updated.LibraryPath}");
            StartLibrary(updated.LibraryPath);
        }
        if (!string.Equals(old.ResolumeHost, updated.ResolumeHost, StringComparison.OrdinalIgnoreCase) || old.ResolumePort != updated.ResolumePort)
        {
            Log.Info($"Resolume address changed to {updated.ResolumeHost}:{updated.ResolumePort}; reconnecting");
            var previousConn = Connection;
            var previousCtl = Controller;
            StartConnection(updated.ResolumeHost, updated.ResolumePort);
            previousCtl.Dispose();
            await previousConn.DisposeAsync();
        }
        if (!string.Equals(old.FfmpegPath, updated.FfmpegPath, StringComparison.OrdinalIgnoreCase))
            await Ffmpeg.ConfigureAsync(updated.FfmpegPath);

        RebuildMatches();
        AutoWatch();
        RaiseLive();
        try { SettingsChanged?.Invoke(); } catch (Exception ex) { Log.Error("SettingsChanged handler", ex); }
    }

    public void RescanLibrary() => Task.Run(() => Library.Rescan());

    public ClipInfo? ResolveClip(Song song) => Matches.For(song.Id)?.Clip;

    private void RebuildMatches()
    {
        MatchTable table;
        lock (_gate)
        {
            table = ClipMatcher.Build(Connection?.Composition, Library.Songs, new PathMapper(Settings.PathMappings), Settings.SongLayer);
            Matches = table;
        }
        foreach (var m in table.Matches.Where(m => m.Ambiguous)) Log.Warn($"Song \"{m.Song.Title}\": {m.Warning}");
        Log.Info($"Matched {table.Matches.Count(m => m.IsAvailable)}/{table.Matches.Count} songs to clips");
        try { MatchesChanged?.Invoke(); } catch (Exception ex) { Log.Error("MatchesChanged handler", ex); }
        SyncCurrentSong();
    }

    /// <summary>The song whose clip is being followed, if it is in the library.</summary>
    public Song? CurrentSong
    {
        get
        {
            var clip = Connection?.WatchedClip;
            return clip is null ? null : Matches.SongForClip(clip.ClipId);
        }
    }

    /// <summary>Follows whatever Resolume connects: the clip on the song layer first, else any connected clip that
    /// belongs to a library song, else the clip that just connected.</summary>
    private void AutoWatch(ClipInfo? justConnected = null)
    {
        var comp = Connection?.Composition;
        if (comp is null) return;

        ClipInfo? target = justConnected is { IsConnected: true } && (justConnected.Layer == Settings.SongLayer || Matches.SongForClip(justConnected.ClipId) is not null)
            ? justConnected
            : comp.ConnectedClipOn(Settings.SongLayer)
              ?? comp.ConnectedClips.FirstOrDefault(c => Matches.SongForClip(c.ClipId) is not null)
              ?? justConnected;

        if (target is null)
        {
            // Nothing live any more: keep showing the last song but drop the queue.
            SyncCurrentSong();
            return;
        }
        if (target.ClipId == Connection!.WatchedClip?.ClipId) { SyncCurrentSong(); return; }
        var song = Matches.SongForClip(target.ClipId);
        Log.Info(song is null ? $"Following unknown clip {target.Display}" : $"Following \"{song.Title}\" on {target.Location}");
        _ = Connection.WatchClipAsync(target).ContinueWith(_ => { SyncCurrentSong(); RaiseLive(); });
    }

    private void SyncCurrentSong()
    {
        var clip = Connection?.WatchedClip;
        Controller?.SetCurrentSong(clip is null ? null : Matches.SongForClip(clip.ClipId), clip);
    }

    private void RaiseLive()
    {
        try { LiveChanged?.Invoke(); } catch (Exception ex) { Log.Error("LiveChanged handler", ex); }
    }

    private void Status(string message)
    {
        try { StatusMessage?.Invoke(message); } catch (Exception ex) { Log.Error("StatusMessage handler", ex); }
    }
}
