using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SegmentDeck.Core.Library;
using SegmentDeck.Core.Logging;
using SegmentDeck.Core.Resolume;
using SegmentDeck.Core.Settings;

namespace SegmentDeck.App.ViewModels;

/// <summary>The window frame: status bar, mode switching (Show / Edit), dialogs, keyboard routing and the log buffer.</summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly List<string> _pendingLog = new();
    private readonly object _logGate = new();
    private DateTime _lastActivationScan = DateTime.MinValue;
    private string _transient = "";
    private DateTime _transientUntil = DateTime.MinValue;
    private bool _transientWarn;
    private Views.LogWindow? _logWindow;

    // ---- status bar
    [ObservableProperty] private Brush _lightBrush = Brushes.Red;
    [ObservableProperty] private string _connectionText = "Disconnected";
    [ObservableProperty] private string _hostText = "";
    [ObservableProperty] private string _roleText = "CONTROLLER";
    [ObservableProperty] private bool _isFollow;
    [ObservableProperty] private string _messageText = "";
    [ObservableProperty] private Brush _messageBrush = Brushes.Gray;
    [ObservableProperty] private bool _alwaysOnTop;

    // ---- content
    [ObservableProperty] private object? _currentView;
    [ObservableProperty] private bool _isShowMode = true;
    [ObservableProperty] private string _modeButtonText = "Edit mode (E)";

    partial void OnIsShowModeChanged(bool value) => ModeButtonText = value ? "Edit mode (E)" : "◀ Back to Show (Esc)";

    public string PinText => AlwaysOnTop ? "Pinned on top" : "Pin on top";

    /// <summary>The pin in the top bar: the window's Topmost follows this, and it is saved so it survives a restart.</summary>
    partial void OnAlwaysOnTopChanged(bool value)
    {
        OnPropertyChanged(nameof(PinText));
        if (_services.Settings.AlwaysOnTop == value) return;
        var s = _services.Settings.Clone();
        s.AlwaysOnTop = value;
        _ = _services.ApplySettingsAsync(s);
    }
    public ShowViewModel Show { get; }
    public EditViewModel? Edit { get; private set; }

    public ObservableCollection<string> LogLines { get; } = new();

    public ShellViewModel(AppServices services, Dispatcher dispatcher)
    {
        _services = services;
        _dispatcher = dispatcher;
        AlwaysOnTop = services.Settings.AlwaysOnTop;

        Log.Written += (_, line) => { lock (_logGate) _pendingLog.Add(line); };
        _services.LiveChanged += () => Post(RefreshStatus);
        _services.MatchesChanged += () => Post(RefreshStatus);
        _services.SettingsChanged += () => Post(() => { AlwaysOnTop = _services.Settings.AlwaysOnTop; RefreshStatus(); });
        _services.StatusMessage += m => Post(() => Flash(m, transient: true, warn: true));

        Show = new ShowViewModel(services, this, dispatcher);
        CurrentView = Show;

        _timer.Tick += (_, _) => { FlushLog(); RefreshMessage(); };
        _timer.Start();
        RefreshStatus();
    }

    private void Post(Action a) => _dispatcher.BeginInvoke(a);

    // ------------------------------------------------------------------ status bar

    private void RefreshStatus()
    {
        var conn = _services.Connection;
        var settings = _services.Settings;
        HostText = $"Resolume at {settings.ResolumeHost}:{settings.ResolumePort}" + (conn.ProductInfo is null ? "" : $"  ·  {conn.ProductInfo}");
        IsFollow = settings.Role == AppRole.Follow;
        RoleText = IsFollow ? "FOLLOW" : "CONTROLLER";
        (LightBrush, ConnectionText) = conn.State switch
        {
            ConnectionState.Connected => (Brush("AccentBrush"), "Connected"),
            ConnectionState.Connecting => (Brush("WarnBrush"), "Connecting…"),
            _ => (Brush("DangerBrush"), "Disconnected"),
        };
        RefreshMessage();
    }

    private void RefreshMessage()
    {
        var problems = new List<string>();
        var conn = _services.Connection;
        if (conn.State != ConnectionState.Connected) problems.Add(conn.LastError is null ? "Not connected to Resolume" : $"Not connected to Resolume ({conn.LastError})");
        if (!_services.Library.IsReachable) problems.Add("Library folder not reachable");
        else if (_services.Library.Warnings.Count > 0) problems.Add($"{_services.Library.Warnings.Count} library file(s) skipped, see log");
        var ambiguous = _services.Matches.Matches.Count(m => m.Ambiguous);
        if (ambiguous > 0) problems.Add($"{ambiguous} song(s) match more than one clip");

        var showTransient = DateTime.Now < _transientUntil && _transient.Length > 0;
        if (showTransient)
        {
            MessageText = _transient + (problems.Count > 0 ? "   ·   " + string.Join("  ·  ", problems) : "");
            MessageBrush = _transientWarn ? Brush("WarnBrush") : Brush("MutedBrush");
        }
        else
        {
            MessageText = problems.Count == 0 ? "" : string.Join("  ·  ", problems);
            MessageBrush = problems.Count == 0 ? Brush("MutedBrush") : Brush("WarnBrush");
        }
    }

    /// <summary>Puts a message in the status bar. Transient messages fade after a few seconds.</summary>
    public void Flash(string message, bool transient, bool warn = false)
    {
        _transient = message;
        _transientWarn = warn;
        _transientUntil = DateTime.Now.AddSeconds(transient ? (warn ? 8 : 3) : 3600);
        RefreshMessage();
    }

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    private void FlushLog()
    {
        List<string> lines;
        lock (_logGate) { if (_pendingLog.Count == 0) return; lines = new List<string>(_pendingLog); _pendingLog.Clear(); }
        foreach (var l in lines) LogLines.Add(l);
        while (LogLines.Count > 400) LogLines.RemoveAt(0);
    }

    // ------------------------------------------------------------------ modes

    public void EnterEdit(Song? song)
    {
        if (Edit is null) Edit = new EditViewModel(_services, this, _dispatcher);
        if (song is not null) Edit.LoadSong(song);
        CurrentView = Edit;
        IsShowMode = false;
        Log.Info("Edit mode");
    }

    public void EnterEditForClip(ClipInfo clip)
    {
        if (Edit is null) Edit = new EditViewModel(_services, this, _dispatcher);
        Edit.NewFromClip(clip);
        CurrentView = Edit;
        IsShowMode = false;
    }

    public void BackToShow()
    {
        if (Edit is not null && !Edit.CanLeave()) return;
        CurrentView = Show;
        IsShowMode = true;
        Log.Info("Show mode");
    }

    [RelayCommand] private void ToggleMode() { if (IsShowMode) EnterEdit(_services.CurrentSong); else BackToShow(); }

    [RelayCommand]
    private void OpenSettings()
    {
        var w = new Views.SettingsWindow(_services) { Owner = Application.Current.MainWindow };
        w.ShowDialog();
    }

    [RelayCommand]
    public void OpenSetlists()
    {
        var w = new Views.SetlistsWindow(_services) { Owner = Application.Current.MainWindow };
        w.ShowDialog();
    }

    [RelayCommand]
    private void OpenLog()
    {
        if (_logWindow is { IsLoaded: true }) { _logWindow.Activate(); return; }
        _logWindow = new Views.LogWindow(LogLines) { Owner = Application.Current.MainWindow };
        _logWindow.Show();
    }

    /// <summary>Window-level keys. Text boxes keep their own keys; Ctrl+S saves in Edit mode.</summary>
    public bool HandleKey(KeyEventArgs e, bool inTextBox)
    {
        if (!IsShowMode && Edit is not null)
        {
            if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { Edit.SaveCommand.Execute(null); return true; }
            if (inTextBox) return false;
            return Edit.HandleKey(e);
        }
        if (inTextBox) return false;
        return Show.HandleKey(e);
    }

    /// <summary>Called when the window regains focus: rescan the library, but not more than once every 2 s.</summary>
    public void OnWindowActivated()
    {
        if ((DateTime.Now - _lastActivationScan).TotalSeconds < 2) return;
        _lastActivationScan = DateTime.Now;
        _services.RescanLibrary();
    }

    public void Shutdown()
    {
        _timer.Stop();
        Show.Stop();
        Edit?.Stop();
    }
}
