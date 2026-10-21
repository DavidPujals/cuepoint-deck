using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CuepointDeck.App.Services;
using CuepointDeck.Core;
using CuepointDeck.Core.Logging;
using CuepointDeck.Core.Media;
using CuepointDeck.Core.Settings;

namespace CuepointDeck.App.ViewModels;

/// <summary>
/// Settings for this PC. Every change applies as soon as it is made, as in a Mac settings window: switches and
/// pop-ups at once, text fields when you press Return or move on. Nothing to save, nothing to cancel. A value that
/// isn't valid is held back and explained under its group until it is fixed.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly DispatcherTimer _applyTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _loading;
    private bool _applying;
    private bool _installed;

    [ObservableProperty] private string _pane = "general";

    // ---- General
    [ObservableProperty] private int _roleIndex;
    [ObservableProperty] private int _defaultTriggerIndex;
    [ObservableProperty] private bool _launchSongsFromSetlist;
    [ObservableProperty] private double _cardScale = 1.0;
    [ObservableProperty] private bool _confirmQuit = true;

    // ---- Resolume
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _port = "";
    [ObservableProperty] private string _songLayer = "";
    [ObservableProperty] private string _latencyOffset = "";
    [ObservableProperty] private string _connectionSummary = "";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _resolumeError = "";

    // ---- Library
    [ObservableProperty] private string _libraryPath = "";
    [ObservableProperty] private string _pathMappingsText = "";
    [ObservableProperty] private string _libraryError = "";
    [ObservableProperty] private string _librarySummary = "";

    // ---- Media
    [ObservableProperty] private string _ffmpegPath = "";
    [ObservableProperty] private string _ffmpegStatus = "";
    [ObservableProperty] private bool _ffmpegOk;

    // ---- About
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private string _updateButtonText = "Check for Updates";
    [ObservableProperty] private bool _canCheckForUpdates = true;

    public string VersionText => "Version " + UpdateService.Format(UpdateService.CurrentVersion);
    public string RepoUrl => UpdateService.RepoUrl;
    public string RepoText => UpdateService.RepoUrl.Replace("https://", "");
    public string LatencyLabel => $"Latency offset for {(string.IsNullOrWhiteSpace(Host) ? "this host" : Host.Trim())}";
    public string CardScaleText => $"{CardScale:0.0}×";

    public SettingsViewModel(AppServices services, ShellViewModel shell)
    {
        _services = services;
        _shell = shell;
        _applyTimer.Tick += (_, _) => { _applyTimer.Stop(); _ = ApplyAsync(); };
        LoadFrom(services.Settings, includeText: true);
        RefreshConnection();
        RefreshLibrarySummary();
        FfmpegOk = services.Ffmpeg.IsAvailable;
        FfmpegStatus = services.Ffmpeg.StatusText;
        if (!UpdateService.CanSelfUpdate)
        {
            UpdateStatus = "This is a development build, so it can't update itself. Releases are a single CuepointDeck.exe.";
            CanCheckForUpdates = false;
        }
        else UpdateStatus = "Checks GitHub for a newer release, downloads it and swaps the exe in place. Settings and the library are untouched.";

        _services.LiveChanged += OnLive;
        _services.SettingsChanged += OnSettingsChanged;
        _services.LibraryChanged += OnLibrary;
        SongTransferFlow.LastExportChanged += OnLastExport;
        OnLastExport();
    }

    /// <summary>Applies anything still waiting on the short debounce (the window is closing).</summary>
    public void FlushPending()
    {
        if (!_applyTimer.IsEnabled) return;
        _applyTimer.Stop();
        _ = ApplyAsync();
    }

    public void Detach()
    {
        _applyTimer.Stop();
        _services.LiveChanged -= OnLive;
        _services.SettingsChanged -= OnSettingsChanged;
        _services.LibraryChanged -= OnLibrary;
        SongTransferFlow.LastExportChanged -= OnLastExport;
    }

    // ---- Transfer
    [ObservableProperty] private string _lastExportText = "";
    [ObservableProperty] private bool _hasLastExport;

    private void OnLastExport()
    {
        var path = SongTransferFlow.LastExportPath;
        HasLastExport = path is not null;
        LastExportText = path is null ? "" : $"Last export: {System.IO.Path.GetFileName(path)}";
    }

    [RelayCommand] private void ExportSongs() => SongTransferFlow.Export(_services, _shell);
    [RelayCommand] private Task ImportSongsAsync() => SongTransferFlow.ImportAsync(_services, _shell);
    [RelayCommand] private void ShowLastExport() => SongTransferFlow.ShowLastExport();

    private void OnLive() => Application.Current?.Dispatcher.BeginInvoke(RefreshConnection);
    private void OnLibrary() => Application.Current?.Dispatcher.BeginInvoke(RefreshLibrarySummary);

    /// <summary>Changes made elsewhere (the toolbar pin, Ctrl +/− card size, "Don't ask again") show up here too.
    /// Text fields are left alone so nothing typed is overwritten.</summary>
    private void OnSettingsChanged()
    {
        if (_applying) return;
        Application.Current?.Dispatcher.BeginInvoke(() => LoadFrom(_services.Settings, includeText: false));
    }

    private void LoadFrom(AppSettings s, bool includeText)
    {
        _loading = true;
        try
        {
            RoleIndex = s.Role == AppRole.Follow ? 1 : 0;
            DefaultTriggerIndex = s.DefaultTrigger == TriggerMode.Cut ? 1 : 0;
            LaunchSongsFromSetlist = s.LaunchSongsFromSetlist;
            CardScale = s.CardScale;
            ConfirmQuit = s.ConfirmQuit;
            if (!includeText) return;
            Host = s.ResolumeHost;
            Port = s.ResolumePort.ToString();
            SongLayer = s.SongLayer.ToString();
            LatencyOffset = s.LatencyOffsetMs.ToString();
            LibraryPath = s.LibraryPath;
            PathMappingsText = string.Join(Environment.NewLine, s.PathMappings.Select(m => $"{m.From} => {m.To}"));
            FfmpegPath = s.FfmpegPath;
        }
        finally { _loading = false; }
    }

    private void RefreshConnection()
    {
        var conn = _services.Connection;
        var s = _services.Settings;
        IsConnected = conn.State == Core.Resolume.ConnectionState.Connected;
        ConnectionSummary = conn.State switch
        {
            Core.Resolume.ConnectionState.Connected => $"Connected to {conn.ProductInfo ?? "Resolume"} at {s.ResolumeHost}:{s.ResolumePort}",
            Core.Resolume.ConnectionState.Connecting => $"Connecting to {s.ResolumeHost}:{s.ResolumePort}…",
            _ => $"Not connected to {s.ResolumeHost}:{s.ResolumePort}" + (conn.LastError is null ? "" : $". {conn.LastError}"),
        };
    }

    private void RefreshLibrarySummary()
    {
        var lib = _services.Library;
        LibrarySummary = !lib.IsReachable ? "This folder can't be reached from this PC."
                       : $"{lib.Songs.Count} songs and {lib.Setlists.Count} setlists" + (lib.Warnings.Count > 0 ? $"; {lib.Warnings.Count} file(s) skipped (see the log)" : "");
    }

    // ------------------------------------------------------------------ apply as you go

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(Host): OnPropertyChanged(nameof(LatencyLabel)); if (!_loading) LatencyOffset = (_services.Settings.LatencyOffsetMsByHost.TryGetValue(Host.Trim(), out var v) ? v : AppSettings.DefaultLatencyOffsetMs).ToString(); break;
            case nameof(CardScale): OnPropertyChanged(nameof(CardScaleText)); break;
        }
        if (_loading) return;
        if (e.PropertyName is nameof(RoleIndex) or nameof(DefaultTriggerIndex) or nameof(LaunchSongsFromSetlist) or nameof(CardScale) or nameof(ConfirmQuit)
            or nameof(Host) or nameof(Port) or nameof(SongLayer) or nameof(LatencyOffset) or nameof(LibraryPath) or nameof(PathMappingsText) or nameof(FfmpegPath))
        {
            _applyTimer.Stop();
            _applyTimer.Start();
        }
    }

    private async Task ApplyAsync()
    {
        var s = Build();
        if (s is null) return;
        var old = _services.Settings;
        if (Json.Serialize(old) == Json.Serialize(s)) return;
        _applying = true;
        try
        {
            await _services.ApplySettingsAsync(s);
            if (!string.Equals(old.FfmpegPath, s.FfmpegPath, StringComparison.OrdinalIgnoreCase)) { FfmpegOk = _services.Ffmpeg.IsAvailable; FfmpegStatus = _services.Ffmpeg.StatusText; }
            RefreshConnection();
            RefreshLibrarySummary();
            Log.Info("Settings changed");
        }
        finally { _applying = false; }
    }

    private AppSettings? Build()
    {
        ResolumeError = "";
        LibraryError = "";
        var s = _services.Settings.Clone();
        s.Role = RoleIndex == 1 ? AppRole.Follow : AppRole.Controller;
        s.DefaultTrigger = DefaultTriggerIndex == 1 ? TriggerMode.Cut : TriggerMode.Queue;
        s.LaunchSongsFromSetlist = LaunchSongsFromSetlist;
        s.CardScale = Math.Clamp(CardScale, 0.6, 2.0);
        s.ConfirmQuit = ConfirmQuit;

        s.ResolumeHost = string.IsNullOrWhiteSpace(Host) ? "127.0.0.1" : Host.Trim();
        if (!int.TryParse(Port, out var port) || port is < 1 or > 65535) { ResolumeError = "The port must be a number from 1 to 65535. Resolume's default is 8080."; return null; }
        s.ResolumePort = port;
        if (!int.TryParse(SongLayer, out var layer) || layer < 1) { ResolumeError = "The song layer must be a layer number, 1 or higher."; return null; }
        s.SongLayer = layer;
        if (!int.TryParse(LatencyOffset, out var latency) || latency < 0 || latency > 2000) { ResolumeError = "The latency offset must be between 0 and 2000 ms."; return null; }
        s.LatencyOffsetMsByHost[s.ResolumeHost] = latency;

        s.LibraryPath = string.IsNullOrWhiteSpace(LibraryPath) ? AppSettings.DefaultLibraryPath : LibraryPath.Trim().Trim('"');
        var mappings = new List<PathMapping>();
        foreach (var line in PathMappingsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split("=>", 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) { LibraryError = $"“{line}” isn't a mapping. Write one per line, like  D:\\Media => \\\\CITY-VISUALS\\Media"; return null; }
            mappings.Add(new PathMapping { From = parts[0], To = parts[1] });
        }
        s.PathMappings = mappings;
        s.FfmpegPath = FfmpegPath.Trim().Trim('"');
        return s;
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private async Task CheckFfmpegAsync()
    {
        var probe = new FfmpegService();
        var ok = await probe.ConfigureAsync(FfmpegPath.Trim().Trim('"'));
        FfmpegOk = ok;
        FfmpegStatus = ok ? probe.StatusText : probe.StatusText + ". Download a Windows build from ffmpeg.org, unzip it, and choose its bin\\ffmpeg.exe.";
    }

    [RelayCommand]
    private void BrowseFfmpeg()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Locate ffmpeg.exe", Filter = "ffmpeg.exe|ffmpeg.exe|All files|*.*" };
        if (dlg.ShowDialog() == true) { FfmpegPath = dlg.FileName; _ = CheckFfmpegAsync(); }
    }

    [RelayCommand]
    private void BrowseLibrary()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the library folder" };
        if (dlg.ShowDialog() == true) LibraryPath = dlg.FolderName;
    }

    [RelayCommand]
    private void ShowLibraryFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", _services.Library.RootPath) { UseShellExecute = true }); } catch { }
    }

    [RelayCommand] private void ShowLog() => _shell.ShowLog();

    [RelayCommand]
    private void ShowLogFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", Log.Directory) { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void OpenRepo()
    {
        try { Process.Start(new ProcessStartInfo(UpdateService.RepoUrl) { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (_installed)
        {
            // The swap already happened on disk: start the new exe and bow out without the quit question.
            App.SkipQuitConfirmation = true;
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
            Application.Current.Shutdown();
            return;
        }
        CanCheckForUpdates = false;
        UpdateStatus = "Checking…";
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info is null) UpdateStatus = "Cuepoint Deck is up to date.";
            else
            {
                var ver = "v" + UpdateService.Format(info.Version);
                UpdateStatus = $"Downloading {ver}…";
                var progress = new Progress<double>(p => UpdateStatus = $"Downloading {ver}… {p:P0}");
                await UpdateService.DownloadAndInstallAsync(info, progress);
                _installed = true;
                UpdateStatus = $"{ver} is installed. Restart to finish; Resolume keeps playing while Cuepoint Deck restarts.";
                UpdateButtonText = "Restart Now";
            }
        }
        catch (Exception ex)
        {
            Log.Error("Update", ex);
            UpdateStatus = "The update didn't work: " + ex.Message;
        }
        CanCheckForUpdates = true;
    }
}
