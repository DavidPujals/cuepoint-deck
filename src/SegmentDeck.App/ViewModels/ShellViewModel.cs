using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SegmentDeck.Core.Logging;
using SegmentDeck.Core.Resolume;
using SegmentDeck.Core.Settings;

namespace SegmentDeck.App.ViewModels;

public sealed class MatchRow
{
    public string Title { get; init; } = "";
    public string Clip { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Note { get; init; } = "";
    public string File { get; init; } = "";
}

/// <summary>Stage 2 diagnostics shell: connection, library and matching state, plus the settings that drive them.
/// Show mode (Stage 3) replaces the middle of this window; the status bar and log stay.</summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly List<string> _pendingLog = new();
    private readonly object _logGate = new();
    private DateTime _lastActivationScan = DateTime.MinValue;

    // ---- status bar
    [ObservableProperty] private Brush _lightBrush = Brushes.Red;
    [ObservableProperty] private string _connectionText = "Disconnected";
    [ObservableProperty] private string _hostText = "";
    [ObservableProperty] private string _roleText = "CONTROLLER";
    [ObservableProperty] private string _messageText = "";
    [ObservableProperty] private Brush _messageBrush = Brushes.Gray;

    // ---- live
    [ObservableProperty] private string _compositionText = "No composition yet";
    [ObservableProperty] private string _liveLayersText = "—";
    [ObservableProperty] private string _currentSongText = "No song live";
    [ObservableProperty] private string _positionText = "--:--.---";
    [ObservableProperty] private string _transportText = "";

    // ---- library
    [ObservableProperty] private string _libraryText = "";
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<MatchRow> Matches { get; } = new();

    // ---- settings (edited copies; applied on Apply)
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _port = "";
    [ObservableProperty] private int _roleIndex;
    [ObservableProperty] private string _libraryPath = "";
    [ObservableProperty] private string _songLayer = "";
    [ObservableProperty] private string _latencyOffset = "";
    [ObservableProperty] private string _pathMappingsText = "";
    [ObservableProperty] private bool _alwaysOnTop;

    public ObservableCollection<string> LogLines { get; } = new();

    public ShellViewModel(AppServices services, Dispatcher dispatcher)
    {
        _services = services;
        _dispatcher = dispatcher;
        LoadSettingsFields();

        Log.Written += (_, line) => { lock (_logGate) _pendingLog.Add(line); };
        _services.LiveChanged += () => Post(RefreshLive);
        _services.MatchesChanged += () => Post(RefreshMatches);
        _services.Library.Changed += () => Post(RefreshLibrary);

        _timer.Tick += (_, _) => { FlushLog(); RefreshPosition(); };
        _timer.Start();
        RefreshLive();
        RefreshMatches();
        RefreshLibrary();
    }

    private void Post(Action a) => _dispatcher.BeginInvoke(a);

    // ------------------------------------------------------------------ refreshers

    private void RefreshLive()
    {
        var conn = _services.Connection;
        var settings = _services.Settings;
        HostText = $"{settings.ResolumeHost}:{settings.ResolumePort}" + (conn.ProductInfo is null ? "" : $"  ·  {conn.ProductInfo}");
        RoleText = settings.Role == AppRole.Follow ? "FOLLOW" : "CONTROLLER";
        (LightBrush, ConnectionText) = conn.State switch
        {
            ConnectionState.Connected => (Brush("AccentBrush"), "Connected"),
            ConnectionState.Connecting => (Brush("WarnBrush"), conn.LastError is null ? "Connecting…" : $"Reconnecting… ({conn.LastError})"),
            _ => (Brush("DangerBrush"), "Disconnected"),
        };

        var comp = conn.Composition;
        if (comp is null)
        {
            CompositionText = conn.State == ConnectionState.Connected ? "Waiting for composition" : "No composition (not connected)";
            LiveLayersText = "—";
        }
        else
        {
            CompositionText = $"\"{comp.Name}\"  ·  {comp.Layers.Count} layers  ·  {comp.Clips.Count(c => !c.IsEmpty)} clips with content  ·  received {comp.ReceivedAt:HH:mm:ss}";
            LiveLayersText = string.Join(Environment.NewLine, comp.Layers.Select(l =>
            {
                var c = l.ConnectedClip;
                var mark = l.Index == settings.SongLayer ? "★" : " ";
                return c is null ? $"{mark} L{l.Index} {l.Name,-16} —" : $"{mark} L{l.Index} {l.Name,-16} C{c.Column} \"{c.Name}\" [{c.ConnectedState}]";
            }));
        }

        var watched = conn.WatchedClip;
        var song = _services.CurrentSong;
        CurrentSongText = watched is null ? "No clip live on the song layer"
                        : song is null ? $"Unknown clip: {watched.Name}  ({watched.Location})"
                        : $"{song.Title}  ({watched.Location}, {song.Segments.Count} segments)";
        RefreshPosition();
        UpdateMessage();
    }

    private void RefreshPosition()
    {
        var clip = _services.Connection?.WatchedClip;
        if (clip is null) { PositionText = "--:--.---"; TransportText = ""; return; }
        PositionText = clip.PositionMs is double ms ? FormatMs(ms) : "--:--.---";
        var age = clip.PositionTimestamp == 0 ? double.NaN : (Stopwatch.GetTimestamp() - clip.PositionTimestamp) * 1000.0 / Stopwatch.Frequency;
        TransportText = $"speed {clip.Speed:0.##}×  ·  {(clip.IsPaused ? "PAUSED" : "playing")}  ·  retrigger {clip.RetriggerMode}  ·  {(double.IsNaN(age) ? "no updates yet" : $"last update {age:0} ms ago")}  ·  {clip.DurationMs / 1000:0.0} s  ·  {clip.Fps:0.##} fps";
    }

    private void RefreshMatches()
    {
        Matches.Clear();
        foreach (var m in _services.Matches.Matches.OrderBy(m => m.Song.Title))
        {
            Matches.Add(new MatchRow
            {
                Title = m.Song.Title,
                Clip = m.Clip is null ? "unavailable" : m.Clip.Location,
                Kind = m.Kind switch { Core.Matching.MatchKind.Path => "path", Core.Matching.MatchKind.MappedPath => "mapped path", Core.Matching.MatchKind.FileName => "file name", _ => "" },
                Note = m.Warning ?? (m.Clip is null ? "Not in the composition" : ""),
                File = m.Song.Clip.FilePath,
            });
        }
        UpdateMessage();
    }

    private void RefreshLibrary()
    {
        var lib = _services.Library;
        LibraryText = lib.IsReachable
            ? $"{lib.Songs.Count} songs, {lib.Setlists.Count} setlists  ·  scanned {lib.LastScan:HH:mm:ss}  ·  {lib.RootPath}"
            : $"NOT REACHABLE  ·  {lib.RootPath}";
        Warnings.Clear();
        foreach (var w in lib.Warnings) Warnings.Add($"{System.IO.Path.GetFileName(w.File)}: {w.Message}");
        RefreshMatches();
    }

    private void UpdateMessage()
    {
        var conn = _services.Connection;
        var problems = new List<string>();
        if (!_services.Library.IsReachable) problems.Add("Library folder not reachable");
        else if (_services.Library.Warnings.Count > 0) problems.Add($"{_services.Library.Warnings.Count} library file(s) skipped");
        var ambiguous = _services.Matches.Matches.Count(m => m.Ambiguous);
        if (ambiguous > 0) problems.Add($"{ambiguous} song(s) match more than one clip");
        if (conn.State != ConnectionState.Connected) problems.Add("Not connected to Resolume; cards cannot fire");
        MessageText = problems.Count == 0 ? "OK" : string.Join("  ·  ", problems);
        MessageBrush = problems.Count == 0 ? Brush("MutedBrush") : Brush("WarnBrush");
    }

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    private static string FormatMs(double ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    private void FlushLog()
    {
        List<string> lines;
        lock (_logGate) { if (_pendingLog.Count == 0) return; lines = new List<string>(_pendingLog); _pendingLog.Clear(); }
        foreach (var l in lines) LogLines.Add(l);
        while (LogLines.Count > 400) LogLines.RemoveAt(0);
    }

    // ------------------------------------------------------------------ settings

    private void LoadSettingsFields()
    {
        var s = _services.Settings;
        Host = s.ResolumeHost;
        Port = s.ResolumePort.ToString();
        RoleIndex = s.Role == AppRole.Follow ? 1 : 0;
        LibraryPath = s.LibraryPath;
        SongLayer = s.SongLayer.ToString();
        LatencyOffset = s.LatencyOffsetMs.ToString();
        PathMappingsText = string.Join(Environment.NewLine, s.PathMappings.Select(m => $"{m.From} => {m.To}"));
        AlwaysOnTop = s.AlwaysOnTop;
    }

    [RelayCommand]
    private async Task ApplySettingsAsync()
    {
        var s = _services.Settings.Clone();
        s.ResolumeHost = Host.Trim();
        if (int.TryParse(Port, out var port) && port is > 0 and < 65536) s.ResolumePort = port;
        s.Role = RoleIndex == 1 ? AppRole.Follow : AppRole.Controller;
        s.LibraryPath = string.IsNullOrWhiteSpace(LibraryPath) ? AppSettings.DefaultLibraryPath : LibraryPath.Trim();
        if (int.TryParse(SongLayer, out var layer) && layer > 0) s.SongLayer = layer;
        if (int.TryParse(LatencyOffset, out var latency) && latency >= 0) s.LatencyOffsetMs = latency;
        s.AlwaysOnTop = AlwaysOnTop;
        s.PathMappings = PathMappingsText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split("=>", 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            .Select(parts => new PathMapping { From = parts[0], To = parts[1] })
            .ToList();
        try
        {
            await _services.ApplySettingsAsync(s);
            _services.Library.Changed += () => Post(RefreshLibrary);
            LoadSettingsFields();
            RefreshLive();
            RefreshLibrary();
        }
        catch (Exception ex) { Log.Error("Applying settings", ex); }
    }

    [RelayCommand]
    private void RefreshLibrary_() => _services.RescanLibrary();

    /// <summary>Called when the window regains focus: rescan, but not more than once every 2 s.</summary>
    public void OnWindowActivated()
    {
        if ((DateTime.Now - _lastActivationScan).TotalSeconds < 2) return;
        _lastActivationScan = DateTime.Now;
        _services.RescanLibrary();
    }

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(Log.Directory);

    [RelayCommand]
    private void OpenLibraryFolder() => OpenFolder(_services.Library.RootPath);

    private static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"Opening {path}", ex); }
    }
}
