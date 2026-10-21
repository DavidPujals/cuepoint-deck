using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CuepointDeck.Core.Logging;

namespace CuepointDeck.App;

/// <summary>
/// Test aid for checking the interface without anyone at the screen. Harmless unless CUEPOINTDECK_SCREENSHOT is set.
///   CUEPOINTDECK_SCREENSHOT=&lt;png&gt;            where to save
///   CUEPOINTDECK_SCREENSHOT_DELAY=&lt;s&gt;         wait before capturing (default 8)
///   CUEPOINTDECK_SCREENSHOT_MODE=&lt;mode&gt;      show (default) · edit · edit-setlist · edit-empty ·
///       settings:&lt;pane&gt; · popover:connection|issues|shortcuts · menu:card|chip|row|add|more|addseg ·
///       sheet:quit|unsaved|delete|prompt|pick|export|import|importoff
/// Popovers, menus and sheets are drawn onto the window capture where they would appear, so one PNG shows the whole
/// moment. The window itself is parked off-screen and never activated (see App.ApplyDebugScreenshotSize).
/// </summary>
internal static class DebugCapture
{
    public static void Arm(MainWindow window)
    {
        var path = Environment.GetEnvironmentVariable("CUEPOINTDECK_SCREENSHOT");
        if (string.IsNullOrWhiteSpace(path)) return;
        var delay = int.TryParse(Environment.GetEnvironmentVariable("CUEPOINTDECK_SCREENSHOT_DELAY"), out var d) ? d : 8;
        var mode = (Environment.GetEnvironmentVariable("CUEPOINTDECK_SCREENSHOT_MODE") ?? "show").Trim().ToLowerInvariant();
        var live = Environment.GetEnvironmentVariable("CUEPOINTDECK_SCREENSHOT_LIVE") == "1";
        After(delay * 1000, () =>
        {
            if (live) { SimulateLive(window); After(900, () => Run(window, mode, path)); }
            else Run(window, mode, path);
        });
    }

    private static void After(int ms, Action a)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); try { a(); } catch (Exception ex) { Log.Error("Screenshot", ex); } };
        t.Start();
    }

    private static void Run(MainWindow window, string mode, string path)
    {
        if (window.DataContext is not ViewModels.ShellViewModel shell) return;
        var services = App.Services;
        var (kind, arg) = mode.Contains(':') ? (mode[..mode.IndexOf(':')], mode[(mode.IndexOf(':') + 1)..]) : (mode, "");

        switch (kind)
        {
            case "edit":
            case "edit-setlist":
            case "edit-empty":
                if (kind == "edit") shell.EnterEdit(services.CurrentSong ?? services.Library.Songs.FirstOrDefault());
                else if (kind == "edit-setlist") shell.EnterEditSetlist(services.Settings.ActiveSetlist ?? services.Library.Setlists.FirstOrDefault()?.Name);
                else shell.EnterEdit(null);
                if (arg == "sidebar") After(600, () => shell.Edit!.ToggleSidebarCommand.Execute(null));   // edit:sidebar = with the sidebar toggled
                After(1800, () => Save(Composite(window), path));
                return;

            case "settings":
                shell.OpenSettings(arg.Length > 0 ? arg : null);
                After(1200, () =>
                {
                    var w = Application.Current.Windows.OfType<Views.SettingsWindow>().FirstOrDefault();
                    if (w is not null) { Save(Composite(w), path); w.Close(); }
                });
                return;

            case "popover":
                if (arg == "connection") shell.IsConnectionPopoverOpen = true;
                else if (arg == "issues") shell.IsIssuesPopoverOpen = true;
                else shell.IsShortcutsOpen = true;
                After(700, () =>
                {
                    var name = arg == "connection" ? "ConnectionPopup" : arg == "issues" ? "IssuesPopup" : "ShortcutsPopup";
                    if (window.FindName(name) is Popup { Child: FrameworkElement child, PlacementTarget: FrameworkElement target })
                    {
                        var popup = (Popup)window.FindName(name);
                        var at = target.TranslatePoint(new Point(popup.HorizontalOffset, target.ActualHeight + popup.VerticalOffset), window);
                        Save(Composite(window, (child, at)), path);
                    }
                });
                return;

            case "menu":
                if (arg is "row" or "add" or "more" or "addseg") shell.EnterEdit(services.CurrentSong ?? services.Library.Songs.FirstOrDefault());
                After(arg is "row" or "add" or "more" or "addseg" ? 1800 : 300, () =>
                {
                    var target = FindMenuTarget(window, arg);
                    if (target?.ContextMenu is not { } menu) { Log.Warn($"Screenshot: no menu target for {arg}"); return; }
                    menu.PlacementTarget = target;
                    menu.DataContext ??= target.DataContext;
                    if (arg is "add" or "more" or "addseg") menu.DataContext = target.DataContext;
                    menu.IsOpen = true;
                    After(600, () =>
                    {
                        var dropDown = arg is "add" or "more" or "addseg";
                        var at = target.TranslatePoint(dropDown ? new Point(0, target.ActualHeight + 4) : new Point(target.ActualWidth * 0.45, target.ActualHeight * 0.35), window);
                        // Like the real popup, open upwards when there is no room below.
                        var content = (FrameworkElement)window.Content;
                        if (at.Y + menu.ActualHeight > content.ActualHeight) at.Y = target.TranslatePoint(new Point(0, 0), window).Y - menu.ActualHeight - 4;
                        Save(Composite(window, (menu, at)), path);
                        menu.IsOpen = false;
                    });
                });
                return;

            case "sheet":
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    After(1200, () =>
                    {
                        var sheet = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w is Views.Alert or Views.PromptSheet or Views.PickClipSheet or Views.ExportSongsSheet or Views.ImportSongsSheet);
                        if (sheet is null) { Log.Warn("Screenshot: no sheet open"); return; }
                        var content = (FrameworkElement)window.Content;
                        var origin = content.PointToScreen(new Point(0, 0));
                        var src = PresentationSource.FromVisual(window)!.CompositionTarget!.TransformFromDevice;
                        origin = src.Transform(origin);
                        var at = new Point(sheet.Left - origin.X, sheet.Top - origin.Y);
                        Save(Composite(window, ((FrameworkElement)sheet.Content, at)), path);
                        // CUEPOINTDECK_SCREENSHOT_HOLD=1 leaves the sheet up so its real window frame can be inspected.
                        if (Environment.GetEnvironmentVariable("CUEPOINTDECK_SCREENSHOT_HOLD") != "1") sheet.Close();
                    });
                    switch (arg)
                    {
                        case "quit": shell.ConfirmQuit(); break;
                        case "unsaved":
                            shell.EnterEdit(services.CurrentSong ?? services.Library.Songs.FirstOrDefault());
                            shell.Edit!.Title += " ";
                            shell.Edit.CanLeave();
                            shell.Edit.IsDirty = false;   // so the capture run can close without asking again
                            break;
                        case "delete":
                            shell.EnterEdit(services.CurrentSong ?? services.Library.Songs.FirstOrDefault());
                            shell.Edit!.ConfirmDeleteSongForCapture();
                            break;
                        case "prompt":
                            Views.PromptSheet.Ask("New Setlist", "Give it a name; the date is filled in to start you off.", $"{DateTime.Now:yyyy-MM-dd} ", "Create",
                                n => services.Library.GetSetlist(n) is not null ? "There's already a setlist with that name." : null);
                            break;
                        case "pick":
                            shell.EnterEdit(null);
                            shell.Edit!.NewFromCompositionCommand.Execute(null);
                            break;
                        case "export":
                            Views.ExportSongsSheet.Pick(services.Library.Songs.OrderBy(x => x.Title), services.Library.Songs.OrderBy(x => x.Title).Take(3).Select(x => x.Id));
                            break;
                        case "import":
                        case "importoff":
                            ImportForCapture(services, connected: arg == "import");
                            break;
                    }
                });
                return;

            default:
                Save(Composite(window), path);
                return;
        }
    }

    /// <summary>The import sheet for a made-up file from the other campus: the library's songs (one renamed so it
    /// comes in as new) against song-layer columns numbered differently from here, two of them sharing a column.</summary>
    private static void ImportForCapture(AppServices services, bool connected)
    {
        var songs = services.Library.Songs.OrderBy(x => x.Title).Take(6).ToList();
        var file = Core.Library.SongTransfer.Create(songs, exportedFrom: "NOVA-CITY-MEDIA");
        file.ExportedAt = DateTimeOffset.Now.AddHours(-3);
        file.Songs.Add(new Core.Library.TransferSong { Id = "capture-new", Title = "Goodness of God", DurationMs = 312_000, Fps = 25, FileName = "GoodnessOfGod_2688x512.mov",
            Segments = { new() { Name = "Intro", StartMs = 0 }, new() { Name = "Verse 1", StartMs = 18_000 }, new() { Name = "Chorus", StartMs = 52_000 } } });
        var clips = new List<Core.Resolume.ClipInfo>();
        var names = new Dictionary<int, string>();
        int col = 41;
        foreach (var x in songs.Skip(1))
        {
            names[col] = x.Title.ToUpperInvariant();
            clips.Add(new Core.Resolume.ClipInfo { ClipId = 9000 + col, Layer = services.Settings.SongLayer, Column = col, Name = x.Clip.ClipName, FilePath = $@"E:\Campus2\{x.Clip.FileName}", DurationMs = x.DurationMs, Fps = 30 });
            col += 3;
        }
        names[col] = "Goodness of God";
        clips.Add(new Core.Resolume.ClipInfo { ClipId = 9000 + col, Layer = services.Settings.SongLayer, Column = col, Name = "GoG_1920", FilePath = @"E:\Campus2\GoodnessOfGod_1920x1080.mov", DurationMs = 312_040, Fps = 30 });
        if (!connected) clips.Clear();
        var items = Services.SongTransferFlow.BuildItems(file, services.Library.Songs, _ => null, clips, c => names.TryGetValue(c, out var n) ? n : "");
        if (connected && items.Count > 2 && items[2].SelectedColumn?.Clip is { } shared) items[1].SelectedColumn = items[1].Columns.First(c => c.Clip?.ClipId == shared.ClipId);   // show the shared-column warning
        if (items.Count > 3) items[3].Include = false;
        Views.ImportSongsSheet.Ask(file, items, connected, services.Settings.SongLayer);
    }

    /// <summary>Without Resolume running, "live" captures show a library song as if it were playing in its third
    /// segment, with the fourth queued.</summary>
    private static void SimulateLive(MainWindow window)
    {
        if (window.DataContext is not ViewModels.ShellViewModel shell) return;
        var services = App.Services;
        if (services.Connection.State == Core.Resolume.ConnectionState.Connected) return;
        var song = services.Library.Songs.Where(s => s.Segments.Count >= 4).OrderByDescending(s => s.Segments.Count).FirstOrDefault();
        if (song is null) return;
        var seg = song.Segments[2];
        var end = song.SegmentEndMs(2);
        shell.Show.SimulateLiveForCapture(song, seg.StartMs + (end - seg.StartMs) * 0.4, queuedIndex: 3, loopIndex: -1);
        shell.SimulateConnectedForCapture("Arena 7.28.0", $"L{services.Settings.SongLayer} C1 · {song.Title}");
    }

    private static FrameworkElement? FindMenuTarget(Window window, string arg)
    {
        foreach (var fe in Descendants<FrameworkElement>(window))
        {
            if (fe.ContextMenu is null) continue;
            var dc = fe.DataContext;
            if (arg == "card" && dc is ViewModels.SegmentCardViewModel) return fe;
            if (arg == "chip" && dc is ViewModels.SetlistSongItem) return fe;
            if (arg == "row" && dc is ViewModels.SegmentEditItem) return fe;
            if (arg == "add" && fe.Name == "AddMenuButton") return fe;
            if (arg == "more" && fe.Name == "MoreMenuButton") return fe;
            if (arg == "addseg" && fe.Name == "AddSegmentMenuButton") return fe;
        }
        return null;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) yield return t;
            foreach (var x in Descendants<T>(c)) yield return x;
        }
    }

    private static BitmapSource Render(FrameworkElement e)
    {
        var dpi = VisualTreeHelper.GetDpi(e);
        var rtb = new RenderTargetBitmap(Math.Max(1, (int)(e.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)(e.ActualHeight * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(e);
        return rtb;
    }

    private static BitmapSource Composite(Window window, params (FrameworkElement Element, Point At)[] overlays)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var content = (FrameworkElement)window.Content;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, content.ActualWidth, content.ActualHeight),
                                                        ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, content.ActualWidth, content.ActualHeight) },
                             null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            foreach (var (el, at) in overlays)
            {
                // Draw the overlay's whole painted area, shadow included, at its real offset.
                var b = VisualTreeHelper.GetDescendantBounds(el);
                if (b.IsEmpty) b = new Rect(0, 0, el.ActualWidth, el.ActualHeight);
                var brush = new VisualBrush(el) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = b };
                dc.DrawRectangle(brush, null, new Rect(at.X + b.X, at.Y + b.Y, b.Width, b.Height));
            }
        }
        var rtb = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }

    private static void Save(BitmapSource image, string file)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var fs = File.Create(file);
        enc.Save(fs);
        Log.Info($"Screenshot saved to {file}");
    }
}
