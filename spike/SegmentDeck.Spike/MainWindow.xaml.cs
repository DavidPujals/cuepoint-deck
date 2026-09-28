using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SegmentDeck.Spike.Resolume;

namespace SegmentDeck.Spike;

public partial class MainWindow : Window
{
    private enum PosUnit { Auto, Normalised, Seconds, Milliseconds, Raw }

    private ResolumeSocket? _sock;
    private ResolumeRest? _rest;

    private readonly ObservableCollection<ClipInfo> _clips = new();
    private readonly Dictionary<string, ClipInfo> _byConnectedPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, ClipInfo> _byConnectedParamId = new();
    private readonly Dictionary<long, ClipInfo> _byPositionParamId = new();
    private readonly Dictionary<long, ClipInfo> _bySpeedParamId = new();
    private readonly HashSet<string> _seenTypes = new();

    // watched clip (position stream)
    private ClipInfo? _watched;
    private readonly object _statsGate = new();
    private readonly Queue<long> _posStamps = new();
    private long _posCount;
    private long _lastPosStamp;
    private double _lastPosRaw = double.NaN;
    private long _lastSummaryStamp;

    // seek probe
    private sealed class SeekProbe
    {
        public double TargetRaw, TolRaw;
        public long SentStamp;
        public int Updates;
        public string Via = "";
    }
    private SeekProbe? _probe;

    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly List<string> _pendingLog = new();
    private readonly object _logGate = new();

    public MainWindow()
    {
        InitializeComponent();
        ClipGrid.ItemsSource = _clips;
        SpikeLog.Line += line => { lock (_logGate) _pendingLog.Add(line); };
        _uiTimer.Tick += (_, _) => { FlushLog(); RefreshPositionPanel(); };
    }

    // ------------------------------------------------------------------ lifecycle

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _uiTimer.Start();
        SpikeLog.Write($"Spike started. Log folder: {SpikeLog.Dir}");
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _uiTimer.Stop();
        _sock?.Dispose();
    }

    // ------------------------------------------------------------------ connection

    private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (!int.TryParse(PortBox.Text.Trim(), out var port)) { SpikeLog.Write("Port must be a number"); return; }

        ConnectBtn.IsEnabled = false;
        Light.Fill = (Brush)FindResource("WarnBrush");
        StatusText.Text = $"Connecting to {host}:{port}…";
        _rest = new ResolumeRest(host, port);

        try
        {
            var product = await _rest.GetProductAsync();
            SpikeLog.Write($"REST /product → {product}");
            StatusText.Text = product;
        }
        catch (Exception ex)
        {
            SpikeLog.Write($"REST /product failed: {ex.Message}");
            StatusText.Text = "REST failed (is the webserver enabled in Resolume preferences?)";
        }

        _sock = new ResolumeSocket();
        _sock.RawMessage += OnRawMessage;
        _sock.Closed += reason => Dispatcher.BeginInvoke(() =>
        {
            SpikeLog.Write($"WS closed: {reason}");
            SetDisconnectedUi();
        });

        try
        {
            var sw = Stopwatch.StartNew();
            await _sock.ConnectAsync(host, port, new CancellationTokenSource(5000).Token);
            SpikeLog.Write($"WS connected to ws://{host}:{port}/api/v1 in {sw.ElapsedMilliseconds} ms");
            Light.Fill = (Brush)FindResource("AccentBrush");
            DisconnectBtn.IsEnabled = true;
            RefreshBtn.IsEnabled = true;
            RawSendBtn.IsEnabled = true;
            RestGetBtn.IsEnabled = true;
            WatchSelectedBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            SpikeLog.Write($"WS connect failed: {ex.Message}");
            SetDisconnectedUi();
        }
    }

    private void DisconnectBtn_Click(object sender, RoutedEventArgs e)
    {
        _sock?.Dispose();
        SpikeLog.Write("WS disconnected by user");
        SetDisconnectedUi();
    }

    private void SetDisconnectedUi()
    {
        Light.Fill = (Brush)FindResource("DangerBrush");
        ConnectBtn.IsEnabled = true;
        DisconnectBtn.IsEnabled = false;
        RefreshBtn.IsEnabled = false;
        RawSendBtn.IsEnabled = false;
        RestGetBtn.IsEnabled = false;
        WatchSelectedBtn.IsEnabled = false;
        SeekBtn.IsEnabled = false;
        SetCsButtons(false);
        _watched = null;
        _probe = null;
        WatchedText.Text = "Nothing watched (disconnected)";
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_rest is null) return;
        try
        {
            var sw = Stopwatch.StartNew();
            var raw = await _rest.GetCompositionRawAsync();
            SpikeLog.Write($"REST /composition → {raw.Length:N0} bytes in {sw.ElapsedMilliseconds} ms");
            using var doc = JsonDocument.Parse(raw);
            await LoadCompositionAsync(doc.RootElement.Clone(), "REST");
        }
        catch (Exception ex) { SpikeLog.Write($"REST /composition failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ inbound messages (background thread)

    private void OnRawMessage(string text, long stamp)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (Exception ex) { SpikeLog.Write($"WS non-JSON message ({text.Length} bytes): {ex.Message}"); return; }

        using (doc)
        {
            var root = doc.RootElement;
            string type = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var t) ? t.ToString()
                        : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("layers", out _) ? "composition"
                        : "(untyped)";

            bool first;
            lock (_seenTypes) first = _seenTypes.Add(type);
            if (first)
                SpikeLog.Write($"first '{type}' message, {text.Length:N0} bytes: {Truncate(text, 700)}");
            else if (LogAllRaw)
                SpikeLog.Write($"WS {type} {Truncate(text, 400)}");

            switch (type)
            {
                case "composition":
                    var clone = root.Clone();
                    Dispatcher.BeginInvoke(() => _ = LoadCompositionAsync(clone, "WS"));
                    break;
                case "sources_update":
                case "effects_update":
                    break;
                case "parameter_update":
                case "parameter_subscribed":
                case "parameter_get":
                case "parameter_set":
                    HandleParameterMessage(root, type, stamp);
                    break;
                default:
                    if (!first) SpikeLog.Write($"WS {type}: {Truncate(text, 300)}");
                    break;
            }
        }
    }

    private volatile bool _logAllRaw, _logEveryPos;
    private bool LogAllRaw => _logAllRaw;

    private void HandleParameterMessage(JsonElement root, string type, long stamp)
    {
        var path = root.TryGetProperty("path", out var p) ? p.ToString() : "";

        if (root.TryGetProperty("error", out var err))
        {
            SpikeLog.Write($"WS error reply: path={path} error={err}");
            return;
        }

        // Arena 7.24 puts the parameter's own fields (id, valuetype, value, min, max, options…) at the message root,
        // so "value" is the scalar. Older shapes with a nested object are still accepted.
        JsonElement param = default;
        if (root.TryGetProperty("value", out var v)) param = v;
        else if (root.TryGetProperty("param", out var pr)) param = pr;

        double? value = null, min = null, max = null;
        long id = root.TryGetProperty("id", out var rid) && rid.ValueKind == JsonValueKind.Number ? rid.GetInt64() : 0;
        if (param.ValueKind == JsonValueKind.Object)
        {
            value = CompositionParser.Num(param, "value");
            min = CompositionParser.Num(param, "min");
            max = CompositionParser.Num(param, "max");
            if (id == 0) id = CompositionParser.Num(param, "id") is double d ? (long)d : 0;
        }
        else if (param.ValueKind == JsonValueKind.Number)
        {
            value = param.GetDouble();
            min = CompositionParser.Num(root, "min");
            max = CompositionParser.Num(root, "max");
        }
        long byIdFromPath = ParseByIdPath(path);

        // -- position stream
        var watched = _watched;
        if (watched is not null && (PathIs(path, watched.PositionPath) || (id != 0 && id == watched.PositionParamId) || (byIdFromPath != 0 && byIdFromPath == watched.PositionParamId)))
        {
            if (value is double raw) OnPositionUpdate(watched, raw, min, max, stamp, type);
            return;
        }

        // -- speed
        ClipInfo? speedClip = null;
        if (watched is not null && PathIs(path, watched.SpeedPath)) speedClip = watched;
        else if (id != 0) _bySpeedParamId.TryGetValue(id, out speedClip);
        if (speedClip is not null)
        {
            speedClip.SpeedValue = value;
            SpikeLog.Write($"speed {speedClip.Display} → {value}");
            return;
        }

        // -- connected state
        ClipInfo? clip = null;
        if (id != 0) _byConnectedParamId.TryGetValue(id, out clip);
        if (clip is null && byIdFromPath != 0) _byConnectedParamId.TryGetValue(byIdFromPath, out clip);
        if (clip is null) _byConnectedPath.TryGetValue(NormPath(path), out clip);
        if (clip is not null)
        {
            var state = CompositionParser.ParamValueString(param);
            var previous = clip.Connected;
            Dispatcher.BeginInvoke(() =>
            {
                clip.Connected = state;
                if (previous != state)
                {
                    SpikeLog.Write($"connected: L{clip.Layer} C{clip.Column} \"{clip.Name}\" {previous} → {state}");
                    UpdateLiveText();
                    if (clip.IsConnected && clip.Layer == SongLayer && AutoWatchCheck.IsChecked == true && !ReferenceEquals(_watched, clip))
                        _ = WatchAsync(clip, "auto (song layer connect)");
                }
            });
            return;
        }

        SpikeLog.Write($"WS {type} path={path} value={CompositionParser.ParamValueString(param)}");
    }

    /// <summary>Resolume replies with the parameter's internal name, so ".../connected" comes back as ".../connect".</summary>
    private static string NormPath(string p)
    {
        p = p.TrimEnd('/');
        return p.EndsWith("/connected", StringComparison.OrdinalIgnoreCase) ? p[..^2] : p;
    }

    private static bool PathIs(string a, string b) => string.Equals(NormPath(a), NormPath(b), StringComparison.OrdinalIgnoreCase);

    private static long ParseByIdPath(string path)
    {
        const string prefix = "/parameter/by-id/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;
        return long.TryParse(path.AsSpan(prefix.Length), out var id) ? id : 0;
    }

    private void OnPositionUpdate(ClipInfo clip, double raw, double? min, double? max, long stamp, string type)
    {
        double intervalMs = 0;
        lock (_statsGate)
        {
            if (_lastPosStamp != 0) intervalMs = TicksToMs(stamp - _lastPosStamp);
            _lastPosStamp = stamp;
            _lastPosRaw = raw;
            _posCount++;
            _posStamps.Enqueue(stamp);
            while (_posStamps.Count > 0 && TicksToMs(stamp - _posStamps.Peek()) > 3000) _posStamps.Dequeue();
        }
        clip.PosValue = raw;
        NotifyResetWaiter(clip, raw);

        if (type != "parameter_update")
            SpikeLog.Write($"position {type}: raw={raw} min={min} max={max}");
        else if (_logEveryPos)
            SpikeLog.Write($"pos raw={raw:0.######}  Δ{intervalMs:0.0} ms  ≈ {FormatMs(RawToMs(raw, clip))}");

        var probe = _probe;
        if (probe is not null)
        {
            probe.Updates++;
            var sinceSend = TicksToMs(stamp - probe.SentStamp);
            var deltaRaw = raw - probe.TargetRaw;
            var deltaMs = RawToMs(raw, clip) - RawToMs(probe.TargetRaw, clip);
            if (probe.Updates == 1)
                SpikeLog.Write($"  first position update after seek: +{sinceSend:0.0} ms, raw={raw:0.######}, {deltaMs:+0.0;-0.0} ms from target");
            if (Math.Abs(deltaRaw) <= probe.TolRaw)
            {
                SpikeLog.Write($"  SEEK CONFIRMED via {probe.Via}: +{sinceSend:0.0} ms after send, update #{probe.Updates}, {deltaMs:+0.0;-0.0} ms from target");
                SetSeekResult($"Confirmed after {sinceSend:0} ms (update #{probe.Updates}, {deltaMs:+0.0;-0.0} ms off)");
                _probe = null;
            }
            else if (sinceSend > 2000)
            {
                SpikeLog.Write($"  SEEK NOT CONFIRMED via {probe.Via} within 2 s. Last raw={raw:0.######}, {deltaMs:+0.0;-0.0} ms from target after {probe.Updates} updates");
                SetSeekResult($"NOT confirmed within 2 s ({deltaMs:+0.0;-0.0} ms off)");
                _probe = null;
            }
        }

        // periodic rate summary for the notes
        if (_lastSummaryStamp == 0) _lastSummaryStamp = stamp;
        else if (TicksToMs(stamp - _lastSummaryStamp) >= 5000)
        {
            _lastSummaryStamp = stamp;
            var (rate, avg, lo, hi) = RateStats();
            SpikeLog.Write($"position rate: {rate:0.0} Hz, interval avg {avg:0.0} / min {lo:0.0} / max {hi:0.0} ms, speed={clip.SpeedValue}, raw={raw:0.######}");
        }
    }

    private void SetSeekResult(string text) => Dispatcher.BeginInvoke(() => SeekResultText.Text = text);

    private (double rate, double avg, double lo, double hi) RateStats()
    {
        lock (_statsGate)
        {
            if (_posStamps.Count < 2) return (0, 0, 0, 0);
            var arr = _posStamps.ToArray();
            double window = TicksToMs(arr[^1] - arr[0]);
            double lo = double.MaxValue, hi = 0, sum = 0;
            for (int i = 1; i < arr.Length; i++)
            {
                var d = TicksToMs(arr[i] - arr[i - 1]);
                lo = Math.Min(lo, d); hi = Math.Max(hi, d); sum += d;
            }
            return (window > 0 ? (arr.Length - 1) * 1000.0 / window : 0, sum / (arr.Length - 1), lo, hi);
        }
    }

    // ------------------------------------------------------------------ composition

    private async Task LoadCompositionAsync(JsonElement composition, string source)
    {
        List<ClipInfo> parsed;
        try { parsed = CompositionParser.Parse(composition); }
        catch (Exception ex) { SpikeLog.Write($"composition parse failed: {ex}"); return; }

        var previousWatched = _watched;
        _clips.Clear();
        _byConnectedPath.Clear(); _byConnectedParamId.Clear(); _byPositionParamId.Clear(); _bySpeedParamId.Clear();
        foreach (var c in parsed)
        {
            _clips.Add(c);
            _byConnectedPath[NormPath(c.ConnectedPath)] = c;
            if (c.ConnectedParamId != 0) _byConnectedParamId[c.ConnectedParamId] = c;
            if (c.PositionParamId != 0) _byPositionParamId[c.PositionParamId] = c;
            if (c.SpeedParamId != 0) _bySpeedParamId[c.SpeedParamId] = c;
        }

        var layers = parsed.Select(c => c.Layer).DefaultIfEmpty(0).Max();
        var nonEmpty = parsed.Count(c => !c.IsEmpty);
        var name = composition.TryGetProperty("name", out var n) ? CompositionParser.ParamValueString(n) : "";
        SpikeLog.Write($"composition ({source}) \"{name}\": {layers} layers, {parsed.Count} clip slots, {nonEmpty} with content");
        foreach (var c in parsed.Where(c => !c.IsEmpty))
            SpikeLog.Write($"  L{c.Layer} C{c.Column} id={c.ClipId} \"{c.Name}\" connected={c.Connected} dur={c.DurationText} fps={c.Fps:0.###} pos[{c.PosMin}..{c.PosMax}] posId={c.PositionParamId} transport={c.TransportType} file={c.FilePath}");
        var sample = parsed.FirstOrDefault(c => c.ConnectedOptions.Length > 0);
        if (sample is not null) SpikeLog.Write($"  'connected' options: {string.Join(" | ", sample.ConnectedOptions)}");
        UpdateLiveText();

        if (_sock?.IsOpen == true && source == "WS")
        {
            // Subscribe to every clip's connected state so the live panel tracks launches. By id: the logical
            // path also works for 'connected', but transport parameters only resolve by id (Arena 7.24).
            int subs = 0;
            foreach (var c in parsed)
            {
                if (c.IsEmpty || c.ConnectedParamId == 0) continue;
                await _sock.SubscribeAsync(ById(c.ConnectedParamId));
                subs++;
            }
            SpikeLog.Write($"subscribed to {subs} clip 'connected' parameters by id");

            // Re-watch: prefer the previously watched clip, else the connected clip on the song layer.
            var target = previousWatched is not null ? parsed.FirstOrDefault(c => c.ClipId == previousWatched.ClipId) : null;
            target ??= parsed.FirstOrDefault(c => c.Layer == SongLayer && c.IsConnected);
            if (target is not null && AutoWatchCheck.IsChecked == true) await WatchAsync(target, "auto (after composition load)");
        }
    }

    private int SongLayer => int.TryParse(SongLayerBox.Text, out var l) && l > 0 ? l : 1;

    private void UpdateLiveText()
    {
        var lines = _clips.GroupBy(c => c.Layer).OrderBy(g => g.Key).Select(g =>
        {
            var live = g.Where(c => c.IsConnected).ToList();
            var layerName = g.First().LayerName;
            var text = live.Count == 0 ? "—" : string.Join(", ", live.Select(c => $"C{c.Column} \"{c.Name}\" [{c.Connected}]"));
            return $"L{g.Key} {layerName,-14} {text}";
        });
        LiveText.Text = _clips.Count == 0 ? "—" : string.Join(Environment.NewLine, lines);
    }

    // ------------------------------------------------------------------ watching a clip's position

    private async Task WatchAsync(ClipInfo clip, string reason)
    {
        if (_sock?.IsOpen != true) return;
        try
        {
            if (_watched is not null && !ReferenceEquals(_watched, clip))
            {
                await _sock.UnsubscribeAsync(ById(_watched.PositionParamId));
                await _sock.UnsubscribeAsync(ById(_watched.SpeedParamId));
            }
            lock (_statsGate) { _posStamps.Clear(); _lastPosStamp = 0; _posCount = 0; _lastPosRaw = double.NaN; _lastSummaryStamp = 0; }
            _probe = null;
            _watched = clip;
            await _sock.SubscribeAsync(ById(clip.PositionParamId));
            await _sock.SubscribeAsync(ById(clip.SpeedParamId));
            SpikeLog.Write($"watching {clip.Display} position ({reason}); posId={clip.PositionParamId} min={clip.PosMin} max={clip.PosMax} fileDur={clip.DurationMs} ms transportDur={clip.TransportDurationValue} (max {clip.TransportDurationMax}) speed={clip.SpeedValue} [{clip.SpeedMin}..{clip.SpeedMax}]");
            WatchedText.Text = $"{clip.Display}  —  {clip.PositionPath}";
            SeekBtn.IsEnabled = true;
        }
        catch (Exception ex) { SpikeLog.Write($"watch failed: {ex.Message}"); }
    }

    private static string ById(long id) => $"/parameter/by-id/{id}";

    private async void WatchSelectedBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ClipGrid.SelectedItem is ClipInfo clip) await WatchAsync(clip, "manual");
    }

    private void RefreshPositionPanel()
    {
        _logAllRaw = LogAllRawCheck.IsChecked == true;
        _logEveryPos = LogEveryPosCheck.IsChecked == true;

        var clip = _watched;
        if (clip is null) { PosRawText.Text = "—"; PosTimeText.Text = "--:--.---"; RateText.Text = "—"; return; }

        double raw; long count;
        lock (_statsGate) { raw = _lastPosRaw; count = _posCount; }
        PosRangeText.Text = $"{clip.PosMin} .. {clip.PosMax}   (param id {clip.PositionParamId})";
        var (unit, why) = EffectiveUnit(clip);
        UnitDeducedText.Text = $"→ {unit} ({why})";
        if (!double.IsNaN(raw))
        {
            PosRawText.Text = $"{raw:0.######}   ({count} updates)";
            PosTimeText.Text = FormatMs(RawToMs(raw, clip));
        }
        var (rate, avg, lo, hi) = RateStats();
        RateText.Text = rate > 0 ? $"{rate:0.0} Hz   interval avg {avg:0.0} / min {lo:0.0} / max {hi:0.0} ms" : "waiting…";
        SpeedText.Text = $"speed={clip.SpeedValue?.ToString("0.###") ?? "?"}  [{clip.SpeedMin}..{clip.SpeedMax}]   transport.duration={clip.TransportDurationValue} (max {clip.TransportDurationMax})   file={clip.DurationMs:0} ms";
    }

    // ------------------------------------------------------------------ units

    private PosUnit SelectedUnit => (PosUnit)Math.Max(0, UnitCombo.SelectedIndex);
    private volatile PosUnit _unitOverride = PosUnit.Auto;
    private void UnitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => _unitOverride = SelectedUnit;

    private (PosUnit unit, string why) EffectiveUnit(ClipInfo c)
    {
        if (_unitOverride != PosUnit.Auto) return (_unitOverride, "manual");
        if (c.PosMax is not double max) return (PosUnit.Raw, "no max");
        if (max <= 1.0001 && (c.PosMin ?? 0) >= 0) return (PosUnit.Normalised, "max ≈ 1");
        if (c.DurationMs is double d && d > 0)
        {
            if (Math.Abs(max - d) <= d * 0.02 + 2) return (PosUnit.Milliseconds, "max ≈ duration_ms");
            if (Math.Abs(max - d / 1000) <= d / 1000 * 0.02 + 0.05) return (PosUnit.Seconds, "max ≈ duration s");
        }
        return (PosUnit.Raw, "unrecognised max");
    }

    private double ClipDurationMs(ClipInfo c) => c.DurationMs is double d && d > 0 ? d : (c.TransportDurationValue is double t && t > 0 ? t : 1);

    private double RawToMs(double raw, ClipInfo c) => EffectiveUnit(c).unit switch
    {
        PosUnit.Normalised => raw * ClipDurationMs(c),
        PosUnit.Seconds => raw * 1000,
        _ => raw,
    };

    private double MsToRaw(double ms, ClipInfo c) => EffectiveUnit(c).unit switch
    {
        PosUnit.Normalised => ms / ClipDurationMs(c),
        PosUnit.Seconds => ms / 1000,
        _ => ms,
    };

    private double FrameMs(ClipInfo c) => c.Fps is double f && f > 0 ? 1000.0 / f : 1000.0 / 30;

    // ------------------------------------------------------------------ seek

    private async void SeekBtn_Click(object sender, RoutedEventArgs e) => await SeekWatchedAsync();

    private async void SeekTimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SeekWatchedAsync();
    }

    private async Task SeekWatchedAsync()
    {
        var clip = _watched;
        if (clip is null || _sock?.IsOpen != true) return;
        if (!TryParseTimeMs(SeekTimeBox.Text, out var ms)) { SpikeLog.Write("Seek time not understood (use seconds or mm:ss.fff)"); return; }
        var frames = double.TryParse(ToleranceBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 1;
        await SendSeekAsync(clip, ms, SeekViaCombo.SelectedIndex, frames, "SEEK");
    }

    /// <summary>Sends one seek and arms the probe that measures how long the position stream takes to confirm it.</summary>
    private async Task SendSeekAsync(ClipInfo clip, double ms, int via, double toleranceFrames, string label)
    {
        var raw = MsToRaw(ms, clip);
        var tolRaw = MsToRaw(FrameMs(clip) * toleranceFrames, clip);
        var viaName = via switch { 0 => "WS set by-id", 1 => "WS set logical path", 2 => "REST by-id", 3 => "REST clip PUT", _ => "?" };
        var probe = new SeekProbe { TargetRaw = raw, TolRaw = tolRaw, SentStamp = Stopwatch.GetTimestamp(), Via = viaName };
        if (ReferenceEquals(clip, _watched)) _probe = probe;
        SpikeLog.Write($"{label} {clip.Display} → {FormatMs(ms)} = raw {raw:0.######} via {viaName} (tolerance {toleranceFrames} frame = {tolRaw:0.######} raw)");
        SeekResultText.Text = "Waiting for the position stream…";
        try
        {
            var sw = Stopwatch.StartNew();
            switch (via)
            {
                case 0: await _sock!.SetAsync(ById(clip.PositionParamId), raw); break;
                case 1: await _sock!.SetAsync(clip.PositionPath, raw); break;
                case 2: { var code = await _rest!.SetParameterByIdAsync(clip.PositionParamId, raw); SpikeLog.Write($"  REST PUT by-id → HTTP {code} in {sw.ElapsedMilliseconds} ms"); break; }
                case 3: { var code = await _rest!.SetClipPositionAsync(clip.Layer, clip.Column, raw); SpikeLog.Write($"  REST PUT clip → HTTP {code} in {sw.ElapsedMilliseconds} ms"); break; }
            }
        }
        catch (Exception ex) { SpikeLog.Write($"  seek send failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ connect-and-seek tests

    private ClipInfo? Target => ClipGrid.SelectedItem as ClipInfo;

    private void ClipGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var c = Target;
        if (c is null || c.IsEmpty)
        {
            TargetText.Text = "No clip selected";
            TargetSettingsText.Text = "";
            SetCsButtons(false);
            return;
        }
        TargetText.Text = $"{c.Display}  (id {c.ClipId})";
        TargetSettingsText.Text = $"triggerstyle={c.TriggerStyle}  faderstart={c.FaderStart}  beatsnap={c.BeatSnap}  target={c.Target}{Environment.NewLine}playmode={c.PlayMode}  playdirection={c.PlayDirection}  transport={c.TransportType}  fps={c.Fps:0.###}";
        SetCsButtons(_sock?.IsOpen == true);
    }

    private void SetCsButtons(bool enabled)
    {
        CsAButton.IsEnabled = enabled;
        CsBButton.IsEnabled = enabled;
        CsCButton.IsEnabled = enabled;
        CsGButton.IsEnabled = enabled;
        CsFButton.IsEnabled = enabled;
        CsConnectOnlyButton.IsEnabled = enabled;
        CsClearLayerButton.IsEnabled = enabled;
    }

    private async Task ConnectTargetAsync(ClipInfo c, Stopwatch sw)
    {
        if (CsRestConnectCheck.IsChecked == true)
        {
            var code = await _rest!.ConnectClipAsync(c.Layer, c.Column);
            SpikeLog.Write($"  +{sw.Elapsed.TotalMilliseconds:0.0} ms  REST POST {c.ConnectPath} → HTTP {code}");
        }
        else
        {
            await _sock!.TriggerAsync(c.ConnectPath);
            SpikeLog.Write($"  +{sw.Elapsed.TotalMilliseconds:0.0} ms  WS trigger {c.ConnectPath}");
        }
    }

    private async Task SeekTargetAsync(ClipInfo c, double ms, Stopwatch sw)
    {
        var via = CsRestSeekCheck.IsChecked == true ? 2 : 0;
        await SendSeekAsync(c, ms, via, 1, $"  +{sw.Elapsed.TotalMilliseconds:0.0} ms  seek");
    }

    /// <summary>Order G from the probe: connect, wait for the transport to report its reset to 0, then seek at once.</summary>
    private async void CsG_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareCs(out var c, out var ms, out _)) return;
        SpikeLog.Write($"TEST G (connect → wait for reset → seek) on {c.Display}, state before: {c.Connected}");
        var sw = Stopwatch.StartNew();
        var tcs = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        _resetWaiter = (clip, raw) => { if (ReferenceEquals(clip, c) && RawToMs(raw, clip) < 1000) tcs.TrySetResult(raw); };
        await ConnectTargetAsync(c, sw);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(1000));
        _resetWaiter = null;
        SpikeLog.Write(done == tcs.Task ? $"  +{sw.Elapsed.TotalMilliseconds:0.0} ms  transport reset seen (raw {tcs.Task.Result:0.###})" : $"  +{sw.Elapsed.TotalMilliseconds:0.0} ms  no reset update within 1 s, seeking anyway");
        await SeekTargetAsync(c, ms, sw);
    }

    /// <summary>Order F from the probe: set the position in-point to the target, connect, then restore the in-point.</summary>
    private async void CsF_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareCs(out var c, out var ms, out _)) return;
        var raw = MsToRaw(ms, c);
        SpikeLog.Write($"TEST F (in-point → connect → restore in-point) on {c.Display}, state before: {c.Connected}");
        var sw = Stopwatch.StartNew();
        try
        {
            var code = await _rest!.SetParameterFieldsAsync(c.PositionParamId, new { @in = raw });
            SpikeLog.Write($"  +{sw.Elapsed.TotalMilliseconds:0.0} ms  REST PUT in={raw:0.###} → HTTP {code}");
            await ConnectTargetAsync(c, sw);
            await Task.Delay(500);
            code = await _rest.SetParameterFieldsAsync(c.PositionParamId, new { @in = 0.0 });
            SpikeLog.Write($"  +{sw.Elapsed.TotalMilliseconds:0.0} ms  REST PUT in=0 → HTTP {code}");
        }
        catch (Exception ex) { SpikeLog.Write($"  test F failed: {ex.Message}"); }
    }

    private volatile Action<ClipInfo, double>? _resetWaiter;

    private void NotifyResetWaiter(ClipInfo clip, double raw)
    {
        _resetWaiter?.Invoke(clip, raw);
    }

    private bool PrepareCs(out ClipInfo clip, out double ms, out int delay)
    {
        clip = Target!; ms = 0; delay = 0;
        if (clip is null || _sock?.IsOpen != true) return false;
        if (!TryParseTimeMs(CsTimeBox.Text, out ms)) { SpikeLog.Write("Connect-and-seek time not understood"); return false; }
        delay = int.TryParse(CsDelayBox.Text, out var d) ? d : 100;
        if (!ReferenceEquals(_watched, clip)) _ = WatchAsync(clip, "connect-and-seek target");
        return true;
    }

    private async void CsA_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareCs(out var c, out var ms, out _)) return;
        SpikeLog.Write($"TEST A (connect → seek at once) on {c.Display}, state before: {c.Connected}");
        var sw = Stopwatch.StartNew();
        await ConnectTargetAsync(c, sw);
        await SeekTargetAsync(c, ms, sw);
    }

    private async void CsB_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareCs(out var c, out var ms, out _)) return;
        SpikeLog.Write($"TEST B (seek → connect) on {c.Display}, state before: {c.Connected}");
        var sw = Stopwatch.StartNew();
        await SeekTargetAsync(c, ms, sw);
        await ConnectTargetAsync(c, sw);
    }

    private async void CsC_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareCs(out var c, out var ms, out var delay)) return;
        SpikeLog.Write($"TEST C (connect → wait {delay} ms → seek) on {c.Display}, state before: {c.Connected}");
        var sw = Stopwatch.StartNew();
        await ConnectTargetAsync(c, sw);
        await Task.Delay(delay);
        await SeekTargetAsync(c, ms, sw);
    }

    private async void CsConnectOnly_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareCs(out var c, out _, out _)) return;
        SpikeLog.Write($"CONNECT ONLY on {c.Display}, state before: {c.Connected}");
        await ConnectTargetAsync(c, Stopwatch.StartNew());
    }

    /// <summary>Spike-only: disconnects whatever is playing on the target's layer so the connect tests start from a stopped clip.
    /// The real app never gets a control like this.</summary>
    private async void CsClearLayer_Click(object sender, RoutedEventArgs e)
    {
        var c = Target;
        if (c is null || _rest is null) return;
        try
        {
            var code = await _rest.ClearLayerAsync(c.Layer);
            SpikeLog.Write($"REST POST /composition/layers/{c.Layer}/clear → HTTP {code}");
        }
        catch (Exception ex) { SpikeLog.Write($"clear layer failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ raw

    private async void RawSendBtn_Click(object sender, RoutedEventArgs e) => await SendRawAsync();
    private async void RawBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await SendRawAsync(); }

    private async Task SendRawAsync()
    {
        if (_sock?.IsOpen != true) return;
        var json = RawBox.Text.Trim();
        try { JsonDocument.Parse(json).Dispose(); } catch (Exception ex) { SpikeLog.Write($"raw: not valid JSON: {ex.Message}"); return; }
        SpikeLog.Write($"→ WS raw: {json}");
        try { await _sock.SendAsync(json); } catch (Exception ex) { SpikeLog.Write($"raw send failed: {ex.Message}"); }
    }

    private async void RestGetBtn_Click(object sender, RoutedEventArgs e) => await RestGetAsync();
    private async void RestPathBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await RestGetAsync(); }

    private async Task RestGetAsync()
    {
        if (_rest is null) return;
        var path = RestPathBox.Text.Trim();
        try
        {
            var sw = Stopwatch.StartNew();
            var body = await _rest.GetRawAsync(path);
            SpikeLog.Write($"REST GET {path} → {body.Length:N0} bytes in {sw.ElapsedMilliseconds} ms: {Truncate(body, 1500)}");
        }
        catch (Exception ex) { SpikeLog.Write($"REST GET {path} failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ log pane

    private void FlushLog()
    {
        List<string> lines;
        lock (_logGate) { if (_pendingLog.Count == 0) return; lines = new List<string>(_pendingLog); _pendingLog.Clear(); }
        LogBox.AppendText(string.Join(Environment.NewLine, lines) + Environment.NewLine);
        if (LogBox.LineCount > 2500)
        {
            var text = LogBox.Text;
            var cut = text.IndexOf('\n', text.Length / 3);
            if (cut > 0) LogBox.Text = text[(cut + 1)..];
        }
        LogBox.ScrollToEnd();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", SpikeLog.Dir) { UseShellExecute = true }); }
        catch (Exception ex) { SpikeLog.Write($"open log folder failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ helpers

    private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + $"… (+{s.Length - max:N0} chars)";

    private static string FormatMs(double ms)
    {
        if (double.IsNaN(ms) || double.IsInfinity(ms)) return "--:--.---";
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    /// <summary>Accepts "60", "60.5", "1:00", "1:00.5", "01:00.500", "1:02:03.4".</summary>
    private static bool TryParseTimeMs(string text, out double ms)
    {
        ms = 0;
        text = text.Trim();
        if (text.Length == 0) return false;
        var parts = text.Split(':');
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return false;
            total = total * 60 + v;
        }
        ms = total * 1000;
        return true;
    }
}
