using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CuepointDeck.Core.Library;
using CuepointDeck.Core.Logging;
using CuepointDeck.Core.Resolume;
using CuepointDeck.Core.Settings;

namespace CuepointDeck.App.ViewModels;

/// <summary>Something that needs the operator's attention, with the one action that fixes it.</summary>
public sealed class IssueItem
{
    public required string Text { get; init; }
    public string ActionLabel { get; init; } = "";
    public ICommand? Action { get; init; }
    public bool HasAction => Action is not null && ActionLabel.Length > 0;
}

public sealed class ShortcutRow
{
    public required string Keys { get; init; }
    public required string Action { get; init; }
}

public sealed class ShortcutGroup
{
    public required string Title { get; init; }
    public required IReadOnlyList<ShortcutRow> Rows { get; init; }
}

/// <summary>The window frame: the toolbar (connection, issues, mode switch, pin, settings, shortcuts), the toast,
/// mode switching, the Settings and Log windows, keyboard routing and the log buffer.</summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _toastTimer = new();
    private readonly List<string> _pendingLog = new();
    private readonly object _logGate = new();
    private DateTime _lastActivationScan = DateTime.MinValue;
    private Views.SettingsWindow? _settingsWindow;
    private Views.LogWindow? _logWindow;
    private string _issuesKey = "";

    // ---- connection pill and its popover
    [ObservableProperty] private Brush _lightBrush = Brushes.Red;
    [ObservableProperty] private string _connectionText = "Offline";
    [ObservableProperty] private string _connectionTitle = "Not connected";
    [ObservableProperty] private string _connectionAddress = "";
    [ObservableProperty] private string _connectionState = "";
    [ObservableProperty] private string _connectionError = "";
    [ObservableProperty] private string _liveClipText = "";
    [ObservableProperty] private bool _isConnectionPopoverOpen;

    // ---- issues indicator and popover
    public ObservableCollection<IssueItem> Issues { get; } = new();
    [ObservableProperty] private int _issueCount;
    [ObservableProperty] private bool _isIssuesPopoverOpen;

    // ---- other toolbar state
    [ObservableProperty] private bool _isFollow;
    [ObservableProperty] private bool _alwaysOnTop;
    [ObservableProperty] private bool _isShortcutsOpen;
    public IReadOnlyList<ShortcutGroup> Shortcuts => BuildShortcuts();

    // ---- toast (transient feedback)
    [ObservableProperty] private string _toastText = "";
    [ObservableProperty] private bool _toastIsWarning;
    [ObservableProperty] private bool _toastVisible;

    // ---- density: the window's height decides how much room the toolbar and header take
    /// <summary>Under 720 px tall: a tighter toolbar (the setlist always stays in it) and a one-line song header.</summary>
    [ObservableProperty] private bool _isCompact;
    /// <summary>Under 460 px tall: the cards also become slim trigger tiles without thumbnails.</summary>
    [ObservableProperty] private bool _isMinimal;
    partial void OnIsCompactChanged(bool value) => Show.IsCompact = value;
    partial void OnIsMinimalChanged(bool value) => Show.IsMinimal = value;

    // ---- content
    [ObservableProperty] private object? _currentView;
    [ObservableProperty] private bool _isShowMode = true;
    public bool IsEditMode => !IsShowMode;
    partial void OnIsShowModeChanged(bool value) => OnPropertyChanged(nameof(IsEditMode));

    public string PinToolTip => AlwaysOnTop ? "Kept on top of Resolume. Click to let other windows cover Cuepoint Deck." : "Keep on Top: stay above Resolume when you click away.";

    /// <summary>The pin in the toolbar: the window's Topmost follows this, and it is saved so it survives a restart.</summary>
    partial void OnAlwaysOnTopChanged(bool value)
    {
        OnPropertyChanged(nameof(PinToolTip));
        if (_services.Settings.AlwaysOnTop == value) return;
        var s = _services.Settings.Clone();
        s.AlwaysOnTop = value;
        _ = _services.ApplySettingsAsync(s);
        ShowToast(value ? "Kept on top" : "No longer kept on top");
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
        _services.LibraryChanged += () => Post(RefreshIssues);
        _services.SettingsChanged += () => Post(() => { AlwaysOnTop = _services.Settings.AlwaysOnTop; RefreshStatus(); OnPropertyChanged(nameof(Shortcuts)); });
        _services.StatusMessage += m => Post(() => ShowToast(m, warn: true));

        Show = new ShowViewModel(services, this, dispatcher);
        CurrentView = Show;

        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastVisible = false; };
        _timer.Tick += (_, _) => { FlushLog(); RefreshIssues(); };
        _timer.Start();
        RefreshStatus();
    }

    private void Post(Action a) => _dispatcher.BeginInvoke(a);

    // ------------------------------------------------------------------ connection and issues

    private bool _captureConnected;

    private void RefreshStatus()
    {
        if (_captureConnected) { RefreshIssues(); return; }
        var conn = _services.Connection;
        var settings = _services.Settings;
        IsFollow = settings.Role == AppRole.Follow;
        ConnectionAddress = $"{settings.ResolumeHost}:{settings.ResolumePort}";
        ConnectionError = conn.State == Core.Resolume.ConnectionState.Connected ? "" : conn.LastError ?? "";
        (LightBrush, ConnectionText, ConnectionState) = conn.State switch
        {
            Core.Resolume.ConnectionState.Connected => (Brush("AccentBrush"), "Resolume", "Connected"),
            Core.Resolume.ConnectionState.Connecting => (Brush("WarnBrush"), "Connecting…", "Connecting, retrying every 2 seconds"),
            _ => (Brush("DangerBrush"), "Offline", "Not connected, retrying every 2 seconds"),
        };
        ConnectionTitle = conn.State == Core.Resolume.ConnectionState.Connected ? conn.ProductInfo ?? "Resolume" : "Resolume not connected";
        var clip = conn.WatchedClip;
        var song = _services.CurrentSong;
        LiveClipText = clip is { IsConnected: true } ? $"{clip.Location} · {song?.Title ?? clip.Name}" : "Nothing live on the song layer";
        RefreshIssues();
    }

    /// <summary>Lasting problems, each with the action that fixes it. Shown behind the toolbar's warning button rather
    /// than as a strip of text, so the toolbar stays calm when all is well.</summary>
    private void RefreshIssues()
    {
        var list = new List<IssueItem>();
        var lib = _services.Library;
        if (!lib.IsReachable)
            list.Add(new IssueItem { Text = $"The library folder can't be reached: {lib.RootPath}", ActionLabel = "Library Settings…", Action = new RelayCommand(() => OpenSettings("library")) });
        else if (lib.Warnings.Count > 0)
            list.Add(new IssueItem { Text = lib.Warnings.Count == 1 ? "1 file in the library couldn't be read and was skipped." : $"{lib.Warnings.Count} files in the library couldn't be read and were skipped.", ActionLabel = "Show Log", Action = ShowLogCommand });
        foreach (var m in _services.Matches.Matches.Where(m => m.Ambiguous))
            list.Add(new IssueItem { Text = $"“{m.Song.Title}”: {m.Warning}." });
        if (!_services.Ffmpeg.IsAvailable && !IsShowMode)
            list.Add(new IssueItem { Text = "ffmpeg isn't set up, so Edit mode has no preview or thumbnails.", ActionLabel = "Media Settings…", Action = new RelayCommand(() => OpenSettings("media")) });

        var key = string.Join("|", list.Select(i => i.Text));
        if (key == _issuesKey) return;
        _issuesKey = key;
        Issues.Clear();
        foreach (var i in list) Issues.Add(i);
        IssueCount = Issues.Count;
        if (IssueCount == 0) IsIssuesPopoverOpen = false;
    }

    // ------------------------------------------------------------------ toast

    /// <summary>Brief, non-blocking feedback at the bottom of the window ("Queued: Chorus"). Warnings stay longer.</summary>
    public void ShowToast(string message, bool warn = false)
    {
        ToastText = message;
        ToastIsWarning = warn;
        ToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(warn ? 5 : 2.2);
        _toastTimer.Start();
    }

    /// <summary>Older call shape; every message is now a toast.</summary>
    public void Flash(string message, bool transient, bool warn = false) => ShowToast(message, warn);

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    private void FlushLog()
    {
        List<string> lines;
        lock (_logGate) { if (_pendingLog.Count == 0) return; lines = new List<string>(_pendingLog); _pendingLog.Clear(); }
        foreach (var l in lines) LogLines.Add(l);
        while (LogLines.Count > 400) LogLines.RemoveAt(0);
    }

    // ------------------------------------------------------------------ modes

    private EditViewModel EnsureEdit()
    {
        if (Edit is null) { Edit = new EditViewModel(_services, this, _dispatcher); OnPropertyChanged(nameof(Edit)); }
        return Edit;
    }

    /// <summary>Edit mode, optionally on a song and one of its segments ("Edit Segment…" on a card).</summary>
    public void EnterEdit(Song? song, string? segmentId = null)
    {
        var edit = EnsureEdit();
        if (song is not null) edit.OpenSong(song, segmentId);
        SwitchTo(edit);
    }

    public void EnterEditForClip(ClipInfo clip)
    {
        var edit = EnsureEdit();
        edit.NewFromClip(clip);
        SwitchTo(edit);
    }

    public void EnterEditSetlist(string? name)
    {
        var edit = EnsureEdit();
        edit.OpenSetlist(name);
        SwitchTo(edit);
    }

    private void SwitchTo(EditViewModel edit)
    {
        if (!IsShowMode) return;
        CurrentView = edit;
        IsShowMode = false;
        RefreshIssues();
        Log.Info("Edit mode");
    }

    public void BackToShow()
    {
        if (IsShowMode) return;
        if (Edit is not null && !Edit.CanLeave()) return;
        CurrentView = Show;
        IsShowMode = true;
        RefreshIssues();
        Log.Info("Show mode");
    }

    [RelayCommand] private void ShowMode() => BackToShow();
    [RelayCommand] private void EditMode() { if (IsShowMode) EnterEdit(_services.CurrentSong); }

    // ------------------------------------------------------------------ windows

    /// <summary>Settings is a normal window, not a dialog: it stays open beside the deck and its changes apply at once.</summary>
    [RelayCommand]
    public void OpenSettings(string? pane)
    {
        IsConnectionPopoverOpen = false;
        IsIssuesPopoverOpen = false;
        if (_settingsWindow is { IsLoaded: true })
        {
            if (pane is not null) _settingsWindow.SelectPane(pane);
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new Views.SettingsWindow(_services, this) { Owner = Application.Current.MainWindow };
        if (pane is not null) _settingsWindow.SelectPane(pane);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    [RelayCommand]
    public void ShowLog()
    {
        IsIssuesPopoverOpen = false;
        if (_logWindow is { IsLoaded: true }) { _logWindow.Activate(); return; }
        _logWindow = new Views.LogWindow(LogLines) { Owner = Application.Current.MainWindow };
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show();
    }

    // ------------------------------------------------------------------ keyboard

    /// <summary>Window-level keys. Text boxes keep their own keys; Ctrl+S saves in Edit mode.</summary>
    public bool HandleKey(KeyEventArgs e, bool inTextBox)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.F1 || (!inTextBox && e.Key == Key.OemQuestion && (Keyboard.Modifiers & ModifierKeys.Shift) != 0))
        {
            IsShortcutsOpen = !IsShortcutsOpen;
            return true;
        }
        if (ctrl && e.Key == Key.OemComma) { OpenSettings(null); return true; }
        if (!IsShowMode && Edit is not null)
        {
            if (e.Key == Key.S && ctrl) { Edit.SaveCommand.Execute(null); return true; }
            if (inTextBox) return false;
            return Edit.HandleKey(e);
        }
        if (inTextBox) return false;
        if (ctrl && (e.Key is Key.OemPlus or Key.Add)) { Show.Zoom(+0.1); return true; }
        if (ctrl && (e.Key is Key.OemMinus or Key.Subtract)) { Show.Zoom(-0.1); return true; }
        if (ctrl && (e.Key is Key.D0 or Key.NumPad0)) { Show.Zoom(0); return true; }
        return Show.HandleKey(e);
    }

    private IReadOnlyList<ShortcutGroup> BuildShortcuts()
    {
        var queueFirst = _services.Settings.DefaultTrigger == TriggerMode.Queue;
        return new[]
        {
            new ShortcutGroup
            {
                Title = "Show Mode",
                Rows = new[]
                {
                    new ShortcutRow { Keys = "1 – 9, 0", Action = queueFirst ? "Queue segment 1–10 (jump at the end of the current one)" : "Jump to segment 1–10 now" },
                    new ShortcutRow { Keys = "Shift + 1 – 9", Action = queueFirst ? "Jump to the segment now" : "Queue the segment" },
                    new ShortcutRow { Keys = "L", Action = "Loop the live segment on or off" },
                    new ShortcutRow { Keys = "Esc", Action = "Clear the queue and the loop" },
                    new ShortcutRow { Keys = "← →", Action = "Choose the previous or next song in the setlist" },
                    new ShortcutRow { Keys = "Return", Action = "Bring up the chosen song in Resolume" },
                    new ShortcutRow { Keys = "Ctrl + / −", Action = "Bigger or smaller cards (Ctrl+0 resets)" },
                    new ShortcutRow { Keys = "E", Action = "Edit the live song" },
                },
            },
            new ShortcutGroup
            {
                Title = "Edit Mode",
                Rows = new[]
                {
                    new ShortcutRow { Keys = "M", Action = "Add a segment at Resolume's playhead" },
                    new ShortcutRow { Keys = "Shift + M", Action = "Add a segment at the preview position" },
                    new ShortcutRow { Keys = "Space", Action = "Play or pause the preview" },
                    new ShortcutRow { Keys = "← →", Action = "Step one frame (Shift: one second)" },
                    new ShortcutRow { Keys = "Delete", Action = "Delete the selected segment" },
                    new ShortcutRow { Keys = "Ctrl + Z", Action = "Undo deleting a segment" },
                    new ShortcutRow { Keys = "Ctrl + S", Action = "Save the song" },
                    new ShortcutRow { Keys = "Esc", Action = "Back to Show mode" },
                },
            },
            new ShortcutGroup
            {
                Title = "Anywhere",
                Rows = new[]
                {
                    new ShortcutRow { Keys = "Ctrl + ,", Action = "Settings" },
                    new ShortcutRow { Keys = "F1", Action = "These shortcuts" },
                },
            },
        };
    }

    /// <summary>Called when the window regains focus: rescan the library, but not more than once every 2 s.</summary>
    public void OnWindowActivated()
    {
        if ((DateTime.Now - _lastActivationScan).TotalSeconds < 2) return;
        _lastActivationScan = DateTime.Now;
        _services.RescanLibrary();
    }

    /// <summary>Test aid (DebugCapture only): the toolbar as it looks when connected.</summary>
    internal void SimulateConnectedForCapture(string product, string live)
    {
        _captureConnected = true;
        LightBrush = Brush("AccentBrush");
        ConnectionText = "Resolume";
        ConnectionState = "Connected";
        ConnectionTitle = product;
        ConnectionError = "";
        LiveClipText = live;
    }

    /// <summary>The quit question. Returns false to stay open.</summary>
    public bool ConfirmQuit()
    {
        if (!_services.Settings.ConfirmQuit) return true;
        var choice = Views.Alert.Show(
            "Quit Cuepoint Deck?",
            "Resolume keeps playing. Nothing is sent to it when Cuepoint Deck closes.",
            "Don't ask again", out var dontAsk,
            new Views.AlertButton("quit", "Quit"),
            new Views.AlertButton("cancel", "Cancel", Views.AlertRole.Default | Views.AlertRole.Cancel));
        if (choice != "quit") return false;
        if (dontAsk)
        {
            var s = _services.Settings.Clone();
            s.ConfirmQuit = false;
            _ = _services.ApplySettingsAsync(s);
        }
        return true;
    }

    public void Shutdown()
    {
        _timer.Stop();
        _toastTimer.Stop();
        Show.Stop();
        Edit?.Stop();
        _settingsWindow?.Close();
        _logWindow?.Close();
    }
}
