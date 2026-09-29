using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SegmentDeck.Core.Media;
using SegmentDeck.Core.Settings;

namespace SegmentDeck.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;

    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _port = "";
    [ObservableProperty] private int _roleIndex;
    [ObservableProperty] private string _libraryPath = "";
    [ObservableProperty] private string _songLayer = "";
    [ObservableProperty] private string _latencyOffset = "";
    [ObservableProperty] private bool _launchSongsFromSetlist;
    [ObservableProperty] private int _defaultTriggerIndex;
    [ObservableProperty] private string _pathMappingsText = "";
    [ObservableProperty] private string _ffmpegPath = "";
    [ObservableProperty] private string _ffmpegStatus = "";
    [ObservableProperty] private bool _alwaysOnTop;
    [ObservableProperty] private double _cardScale = 1.0;
    [ObservableProperty] private string _error = "";

    public string LatencyLabel => $"Latency offset for {Host} (ms)";

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        var s = services.Settings;
        Host = s.ResolumeHost;
        Port = s.ResolumePort.ToString();
        RoleIndex = s.Role == AppRole.Follow ? 1 : 0;
        LibraryPath = s.LibraryPath;
        SongLayer = s.SongLayer.ToString();
        LatencyOffset = s.LatencyOffsetMs.ToString();
        LaunchSongsFromSetlist = s.LaunchSongsFromSetlist;
        DefaultTriggerIndex = s.DefaultTrigger == TriggerMode.Cut ? 1 : 0;
        PathMappingsText = string.Join(Environment.NewLine, s.PathMappings.Select(m => $"{m.From} => {m.To}"));
        FfmpegPath = s.FfmpegPath;
        FfmpegStatus = services.Ffmpeg.StatusText;
        AlwaysOnTop = s.AlwaysOnTop;
        CardScale = s.CardScale;
    }

    partial void OnHostChanged(string value)
    {
        OnPropertyChanged(nameof(LatencyLabel));
        LatencyOffset = (_services.Settings.LatencyOffsetMsByHost.TryGetValue(value.Trim(), out var v) ? v : AppSettings.DefaultLatencyOffsetMs).ToString();
    }

    public AppSettings? Build()
    {
        Error = "";
        var s = _services.Settings.Clone();
        s.ResolumeHost = string.IsNullOrWhiteSpace(Host) ? "127.0.0.1" : Host.Trim();
        if (!int.TryParse(Port, out var port) || port is < 1 or > 65535) { Error = "Port must be a number between 1 and 65535"; return null; }
        s.ResolumePort = port;
        s.Role = RoleIndex == 1 ? AppRole.Follow : AppRole.Controller;
        s.LibraryPath = string.IsNullOrWhiteSpace(LibraryPath) ? AppSettings.DefaultLibraryPath : LibraryPath.Trim();
        if (!int.TryParse(SongLayer, out var layer) || layer < 1) { Error = "Song layer must be 1 or higher"; return null; }
        s.SongLayer = layer;
        if (!int.TryParse(LatencyOffset, out var latency) || latency < 0 || latency > 2000) { Error = "Latency offset must be between 0 and 2000 ms"; return null; }
        s.LatencyOffsetMsByHost[s.ResolumeHost] = latency;
        s.LaunchSongsFromSetlist = LaunchSongsFromSetlist;
        s.DefaultTrigger = DefaultTriggerIndex == 1 ? TriggerMode.Cut : TriggerMode.Queue;
        s.FfmpegPath = FfmpegPath.Trim().Trim('"');
        s.AlwaysOnTop = AlwaysOnTop;
        s.CardScale = Math.Clamp(CardScale, 0.6, 2.0);
        var mappings = new List<PathMapping>();
        foreach (var line in PathMappingsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split("=>", 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) { Error = $"Path mapping not understood: \"{line}\". Use  D:\\Media => \\\\SERVER\\Media"; return null; }
            mappings.Add(new PathMapping { From = parts[0], To = parts[1] });
        }
        s.PathMappings = mappings;
        return s;
    }

    [RelayCommand]
    private async Task CheckFfmpegAsync()
    {
        var probe = new FfmpegService();
        var ok = await probe.ConfigureAsync(FfmpegPath);
        FfmpegStatus = ok ? probe.StatusText : probe.StatusText + " — download a Windows build from ffmpeg.org and point this at ffmpeg.exe";
    }

    [RelayCommand]
    private void BrowseFfmpeg()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Locate ffmpeg.exe", Filter = "ffmpeg.exe|ffmpeg.exe|All files|*.*" };
        if (dlg.ShowDialog() == true) FfmpegPath = dlg.FileName;
    }

    [RelayCommand]
    private void BrowseLibrary()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the library folder" };
        if (dlg.ShowDialog() == true) LibraryPath = dlg.FolderName;
    }
}
