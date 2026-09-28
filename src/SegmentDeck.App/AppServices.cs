using SegmentDeck.Core.Library;
using SegmentDeck.Core.Logging;
using SegmentDeck.Core.Matching;
using SegmentDeck.Core.Resolume;
using SegmentDeck.Core.Settings;

namespace SegmentDeck.App;

/// <summary>Wires settings, library, connection and matching together. One instance per process.
/// Events here fire on background threads; view models marshal to the dispatcher.</summary>
public sealed class AppServices
{
    private readonly SettingsStore _store = new();
    private readonly object _gate = new();

    public AppSettings Settings { get; private set; } = new();
    public SongLibrary Library { get; private set; } = null!;
    public ResolumeConnection Connection { get; private set; } = null!;
    public MatchTable Matches { get; private set; } = new();

    /// <summary>Matches were rebuilt (composition or library changed).</summary>
    public event Action? MatchesChanged;
    /// <summary>Something about the live state changed: connection state, connected clips, watched clip.</summary>
    public event Action? LiveChanged;
    public event Action<PositionUpdate>? PositionUpdated;

    public void Start()
    {
        Settings = _store.Load();
        Log.Info($"Settings: host={Settings.ResolumeHost}:{Settings.ResolumePort} role={Settings.Role} songLayer={Settings.SongLayer} library={Settings.LibraryPath} latency={Settings.LatencyOffsetMs} ms");
        StartLibrary(Settings.LibraryPath);
        StartConnection(Settings.ResolumeHost, Settings.ResolumePort);
    }

    public async Task StopAsync()
    {
        if (Connection is not null) await Connection.StopAsync();
    }

    private void StartLibrary(string path)
    {
        var lib = new SongLibrary(path);
        lib.Changed += () => { RebuildMatches(); };
        Library = lib;
        Task.Run(lib.Rescan);
    }

    private void StartConnection(string host, int port)
    {
        var conn = new ResolumeConnection(host, port);
        conn.StateChanged += _ => RaiseLive();
        conn.CompositionChanged += _ => { RebuildMatches(); AutoWatch(); RaiseLive(); };
        conn.ClipStateChanged += clip => { if (clip.IsConnected) AutoWatch(clip); RaiseLive(); };
        conn.TransportChanged += _ => RaiseLive();
        conn.PositionUpdated += u => { try { PositionUpdated?.Invoke(u); } catch (Exception ex) { Log.Error("PositionUpdated handler", ex); } };
        Connection = conn;
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
            var previous = Connection;
            StartConnection(updated.ResolumeHost, updated.ResolumePort);
            await previous.DisposeAsync();
        }
        RebuildMatches();
        RaiseLive();
    }

    public void RescanLibrary() => Task.Run(() => Library.Rescan());

    private void RebuildMatches()
    {
        MatchTable table;
        lock (_gate)
        {
            table = ClipMatcher.Build(Connection?.Composition, Library.Songs, new PathMapper(Settings.PathMappings), Settings.SongLayer);
            Matches = table;
        }
        var ambiguous = table.Matches.Where(m => m.Ambiguous).ToList();
        foreach (var m in ambiguous) Log.Warn($"Song \"{m.Song.Title}\": {m.Warning}");
        Log.Info($"Matched {table.Matches.Count(m => m.IsAvailable)}/{table.Matches.Count} songs to clips");
        try { MatchesChanged?.Invoke(); } catch (Exception ex) { Log.Error("MatchesChanged handler", ex); }
    }

    /// <summary>The song whose clip is live, if any.</summary>
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

        if (target is null || target.ClipId == Connection!.WatchedClip?.ClipId) return;
        var song = Matches.SongForClip(target.ClipId);
        Log.Info(song is null ? $"Following unknown clip {target.Display}" : $"Following \"{song.Title}\" on {target.Location}");
        _ = Connection.WatchClipAsync(target);
    }

    private void RaiseLive()
    {
        try { LiveChanged?.Invoke(); } catch (Exception ex) { Log.Error("LiveChanged handler", ex); }
    }
}
