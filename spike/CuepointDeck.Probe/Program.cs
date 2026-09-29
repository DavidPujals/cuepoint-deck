// Cuepoint Deck — Stage 1 console probe.
// Runs the Resolume integration checklist unattended and writes a report you can paste into docs/RESOLUME_NOTES.md.
//
//   CuepointDeck.Probe [--host localhost] [--port 8080] [--layer 1] [--column N] [--open "C:\path\song.mov"]
//                     [--seconds 10] [--seeks 10] [--no-connect-tests] [--no-speed-test]
//
// It never saves the composition. Loading a file with --open changes the composition in memory only.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CuepointDeck.Spike.Resolume;

var args_ = Args.Parse(args);
var report = new Report($"probe-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
var rest = new ResolumeRest(args_.Host, args_.Port);

report.H($"Cuepoint Deck probe — {DateTime.Now:yyyy-MM-dd HH:mm:ss} — target {args_.Host}:{args_.Port}");

// ---------------------------------------------------------------- REST product
try { report.L($"REST /product → {await rest.GetProductAsync()}"); }
catch (Exception ex) { report.L($"REST /product FAILED: {ex.Message} (is Preferences > Webserver enabled?)"); return 1; }

// ---------------------------------------------------------------- WebSocket connect
var bus = new Bus();
using var sock = new ResolumeSocket();
sock.RawMessage += bus.OnRaw;
sock.Closed += r => report.L($"WS CLOSED: {r}");
var swConnect = Stopwatch.StartNew();
await sock.ConnectAsync(args_.Host, args_.Port, new CancellationTokenSource(5000).Token);
report.L($"WS connected in {swConnect.ElapsedMilliseconds} ms");

var compMsg = await bus.WaitForAsync(m => m.Type == "composition", 8000);
if (compMsg is null) { report.L("No composition message within 8 s"); return 1; }
report.L($"composition message arrived +{compMsg.RelMs:0} ms after connect, {compMsg.Text.Length:N0} bytes");
foreach (var t in new[] { "sources_update", "effects_update" })
{
    var m = await bus.WaitForAsync(x => x.Type == t, 3000);
    report.L(m is null ? $"no {t} within 3 s" : $"{t} arrived, {m.Text.Length:N0} bytes");
}

var clips = CompositionParser.Parse(compMsg.Json);
report.H("Composition");
var compName = compMsg.Json.TryGetProperty("name", out var cn) ? CompositionParser.ParamValueString(cn) : "";
report.L($"name=\"{compName}\" layers={clips.Select(c => c.Layer).DefaultIfEmpty(0).Max()} slots={clips.Count} withContent={clips.Count(c => !c.IsEmpty)}");
foreach (var c in clips.Where(c => !c.IsEmpty))
    report.L($"  L{c.Layer} C{c.Column} id={c.ClipId} \"{c.Name}\" connected=\"{c.Connected}\" file=\"{c.FilePath}\" exists={c.FileExists} dur={c.DurationMs:0} ms fps={c.Fps:0.###} pos[{c.PosMin}..{c.PosMax}] posId={c.PositionParamId} transport={c.TransportType} trigger={c.TriggerStyle} faderstart={c.FaderStart} beatsnap={c.BeatSnap} playmode={c.PlayMode}");
var any = clips.FirstOrDefault(c => c.ConnectedOptions.Length > 0);
if (any is not null) report.L($"'connected' options: {string.Join(" | ", any.ConnectedOptions)}");

// ---------------------------------------------------------------- optional: load a file into a slot
ClipInfo? target = null;
if (args_.OpenPath is not null)
{
    var col = args_.Column ?? clips.Where(c => c.Layer == args_.Layer && c.IsEmpty).Select(c => c.Column).DefaultIfEmpty(1).First();
    var uri = new Uri(Path.GetFullPath(args_.OpenPath)).AbsoluteUri; // file:///C:/... with %20 encoding
    report.H($"Loading file into L{args_.Layer} C{col}");
    report.L($"POST /composition/layers/{args_.Layer}/clips/{col}/open  body: {uri}");
    using var http = new HttpClient();
    var resp = await http.PostAsync($"http://{args_.Host}:{args_.Port}/api/v1/composition/layers/{args_.Layer}/clips/{col}/open",
        new StringContent(uri, Encoding.UTF8, "text/plain"));
    report.L($"→ HTTP {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
    var comp2 = await bus.WaitForAsync(m => m.Type == "composition", 4000);
    report.L(comp2 is null ? "no new composition message after open (re-reading via REST)" : "composition message re-sent after open (structural change)");
    JsonElement fresh;
    if (comp2 is not null) fresh = comp2.Json;
    else { using var doc = JsonDocument.Parse(await rest.GetCompositionRawAsync()); fresh = doc.RootElement.Clone(); }
    clips = CompositionParser.Parse(fresh);
    target = clips.FirstOrDefault(c => c.Layer == args_.Layer && c.Column == col);
    if (target is not null) report.L($"slot now: \"{target.Name}\" file=\"{target.FilePath}\" exists={target.FileExists} dur={target.DurationMs:0} ms fps={target.Fps:0.###} pos[{target.PosMin}..{target.PosMax}]");
}
target ??= args_.Column is int cc ? clips.FirstOrDefault(c => c.Layer == args_.Layer && c.Column == cc)
                                  : clips.FirstOrDefault(c => c.Layer == args_.Layer && !c.IsEmpty && c.DurationMs > 0);
if (target is null) { report.L("No target clip with a video file on that layer. Use --open <file> or --column."); return 1; }
report.H($"Target: {target.Display} (id {target.ClipId})");

var units = Units.Detect(target);
report.L($"position param: id={target.PositionParamId} min={target.PosMin} max={target.PosMax} → interpreting as {units.Unit} ({units.Why}); file duration {target.DurationMs:0} ms, transport.controls.duration value={target.TransportDurationValue} max={target.TransportDurationMax}");
report.L($"speed param: id={target.SpeedParamId} value={target.SpeedValue} [{target.SpeedMin}..{target.SpeedMax}]");
double frameMs = target.Fps is > 0 ? 1000.0 / target.Fps.Value : 1000.0 / 30;

// ---------------------------------------------------------------- which paths does the WS accept?
report.H("Path probe (WS 'get' on each path; reply or error)");
string ById(long id) => $"/parameter/by-id/{id}";
foreach (var path in new[]
{
    target.ConnectedPath, target.BasePath + "/connect", target.BasePath + "/name",
    target.PositionPath, target.SpeedPath, target.BasePath + "/transport/controls/playmode",
    ById(target.PositionParamId), ById(target.ConnectedParamId), ById(target.SpeedParamId),
})
{
    var replyTask = bus.WaitForAsync(m => m.Type == "parameter_get" || m.Error is not null, 700);
    await sock.GetAsync(path);
    var reply = await replyTask;
    report.L($"  get {path,-62} → {(reply is null ? "(no reply in 700 ms)" : Trunc(reply.Text, 220))}");
}

// ---------------------------------------------------------------- subscriptions
report.H("Subscriptions (by parameter id)");
foreach (var c in clips.Where(c => !c.IsEmpty && c.ConnectedParamId != 0)) await sock.SubscribeAsync(ById(c.ConnectedParamId));
var subConn = await bus.WaitForAsync(m => m.Type == "parameter_subscribed" && m.IsPath(target.ConnectedPath, target.ConnectedParamId), 3000);
report.L(subConn is null ? "no parameter_subscribed reply for connected" : $"parameter_subscribed (connected) raw: {Trunc(subConn.Text, 400)}");
await sock.SubscribeAsync(ById(target.PositionParamId));
var subPos = await bus.WaitForAsync(m => m.Type == "parameter_subscribed" && m.IsPath(target.PositionPath, target.PositionParamId), 3000);
report.L(subPos is null ? "no parameter_subscribed reply for position" : $"parameter_subscribed (position) raw: {Trunc(subPos.Text, 400)}");
await sock.SubscribeAsync(ById(target.SpeedParamId));
var subSpeed = await bus.WaitForAsync(m => m.Type == "parameter_subscribed" && m.IsPath(target.SpeedPath, target.SpeedParamId), 2000);
report.L(subSpeed is null ? "no parameter_subscribed reply for speed" : $"parameter_subscribed (speed) raw: {Trunc(subSpeed.Text, 300)}");

// position recorder
var posLog = new List<(long stamp, double raw)>();
var posGate = new object();
bus.Listen(m =>
{
    if (m.Type != "parameter_update" || !m.IsPath(target.PositionPath, target.PositionParamId)) return;
    if (m.Number is double v) lock (posGate) posLog.Add((m.Stamp, v));
});
bus.Listen(m =>
{
    if (m.Type == "parameter_update" && Msg.Norm(m.Path).EndsWith("/connect"))
        report.L($"    [connected update] {m.Path} → \"{m.ValueString}\" (+{m.RelMs:0} ms)");
});

// ---------------------------------------------------------------- connect the target
report.H("Connect");
var before = target.Connected;
var swC = Stopwatch.StartNew();
await sock.TriggerAsync(target.ConnectPath);
var connMsg = await bus.WaitForAsync(m => m.Type == "parameter_update" && m.IsPath(target.ConnectedPath, target.ConnectedParamId), 3000);
report.L(connMsg is null ? $"WS trigger {target.ConnectPath}: no connected update within 3 s (was \"{before}\")"
                         : $"WS trigger {target.ConnectPath}: connected \"{before}\" → \"{connMsg.ValueString}\" after {swC.ElapsedMilliseconds} ms. Raw: {Trunc(connMsg.Text, 300)}");
var firstPos = await bus.WaitForAsync(m => m.Type == "parameter_update" && m.IsPath(target.PositionPath, target.PositionParamId), 3000);
report.L(firstPos is null ? "no position update within 3 s of connect" : $"first position update +{swC.ElapsedMilliseconds} ms after connect. Raw: {Trunc(firstPos.Text, 300)}");

// ---------------------------------------------------------------- update rate
report.H($"Position update rate ({args_.Seconds} s at current speed)");
lock (posGate) posLog.Clear();
await Task.Delay(args_.Seconds * 1000);
report.L(Stats.Describe(Snapshot(), units, target));
var fewRaw = Snapshot().Take(4).Select(p => $"{units.ToMs(p.raw):0.0}ms").ToList();
report.L($"  first raw values: {string.Join(", ", fewRaw)}");

// ---------------------------------------------------------------- paused clip
{
    var pdId = FindParamId(await GetClipJson(), "transport", "controls", "playdirection");
    var pdOrig = FindParamValue(await GetClipJson(), "transport", "controls", "playdirection");
    report.H($"Position stream while paused (playdirection param id={pdId}, was \"{pdOrig}\"; set to \"||\" for 2 s)");
    await sock.SubscribeAsync(ById(pdId));
    var pdSub = await bus.WaitForAsync(m => m.Type == "parameter_subscribed" && m.ParamId == pdId, 2000);
    report.L(pdSub is null ? "  no parameter_subscribed reply for playdirection" : $"  parameter_subscribed (playdirection) raw: {Trunc(pdSub.Text, 300)}");
    var pdUpdTask = bus.WaitForAsync(m => m.Type == "parameter_update" && m.ParamId == pdId, 2000);
    await sock.SetAsync(ById(pdId), "||");
    var pdUpd = await pdUpdTask;
    report.L(pdUpd is null ? "  no parameter_update for playdirection after set" : $"  playdirection update: \"{pdUpd.ValueString}\" (+{pdUpd.RelMs:0} ms)");
    await Task.Delay(200);
    lock (posGate) posLog.Clear();
    await Task.Delay(2000);
    var paused = Snapshot();
    report.L(paused.Length == 0 ? "  no position updates at all while paused (stream is silent when the playhead is still)"
                                : $"  {paused.Length} position update(s) in 2 s while paused; first {units.ToMs(paused[0].raw):0} ms, last {units.ToMs(paused[^1].raw):0} ms");
    await sock.SetAsync(ById(pdId), pdOrig);
    await Task.Delay(300);
    report.L($"  playdirection restored to \"{FindParamValue(await GetClipJson(), "transport", "controls", "playdirection")}\"");
}

// ---------------------------------------------------------------- disconnected clip
{
    report.H("Position stream after the layer is cleared (clip Disconnected) for 2 s");
    await ClearLayerAsync();
    lock (posGate) posLog.Clear();
    await Task.Delay(2000);
    var off = Snapshot();
    report.L(off.Length == 0 ? "  no position updates while disconnected"
                             : $"  {off.Length} position update(s) in 2 s while disconnected; first {units.ToMs(off[0].raw):0} ms, last {units.ToMs(off[^1].raw):0} ms (transport keeps running while disconnected: {(units.ToMs(off[^1].raw) - units.ToMs(off[0].raw) > 500 ? "YES" : "no")})");
    await sock.TriggerAsync(target.ConnectPath);
    await bus.WaitForAsync(m => m.Type == "parameter_update" && m.IsPath(target.ConnectedPath, target.ConnectedParamId) && m.ValueString.StartsWith("Connected"), 2000);
    await Task.Delay(300);
    report.L("  clip reconnected");
}

if (!args_.NoSpeedTest && target.SpeedParamId != 0)
{
    var original = target.SpeedValue ?? 1.0;
    var fast = Math.Min(target.SpeedMax ?? 2.0, original * 2);
    report.H($"Position update rate at speed {fast:0.##} (5 s)");
    await sock.SetAsync(ById(target.SpeedParamId), fast);
    await Task.Delay(300);
    lock (posGate) posLog.Clear();
    await Task.Delay(5000);
    report.L(Stats.Describe(Snapshot(), units, target));
    await sock.SetAsync(ById(target.SpeedParamId), original);
    report.L($"  speed restored to {original}");
    await Task.Delay(300);
}

// ---------------------------------------------------------------- seek latency
async Task SeekSeries(string label, int count, Func<double, Task> send)
{
    report.H($"Seek latency: {label} ({count} seeks, tolerance 1 frame = {frameMs:0.0} ms)");
    var rnd = new Random(42);
    var results = new List<double>();
    var offsets = new List<double>();
    double durMs = target.DurationMs ?? 60000;
    for (int i = 0; i < count; i++)
    {
        var targetMs = Math.Round(rnd.NextDouble() * Math.Max(1000, durMs - 2000));
        var targetRaw = units.ToRaw(targetMs);
        var tolRaw = units.ToRaw(frameMs) - units.ToRaw(0);
        lock (posGate) posLog.Clear();
        var sw = Stopwatch.StartNew();
        var sent = Stopwatch.GetTimestamp();
        await send(targetRaw);
        var sendMs = sw.Elapsed.TotalMilliseconds;
        double? confirmedMs = null; int updates = 0; double lastOff = double.NaN; double firstMs = double.NaN;
        while (sw.ElapsedMilliseconds < 1500 && confirmedMs is null)
        {
            await Task.Delay(2);
            (long stamp, double raw)[] snap; lock (posGate) snap = posLog.ToArray();
            foreach (var p in snap)
            {
                if (p.stamp < sent) continue;
                updates++;
                var rel = (p.stamp - sent) * 1000.0 / Stopwatch.Frequency;
                if (double.IsNaN(firstMs)) firstMs = rel;
                lastOff = units.ToMs(p.raw) - targetMs;
                if (Math.Abs(p.raw - targetRaw) <= tolRaw) { confirmedMs = rel; break; }
            }
            lock (posGate) posLog.Clear();
        }
        if (confirmedMs is double c) { results.Add(c); offsets.Add(lastOff); report.L($"  #{i + 1} → {targetMs / 1000:0.000}s  send {sendMs:0.0} ms  confirmed +{c:0.0} ms  (off {lastOff:+0.0;-0.0} ms, first update +{firstMs:0.0} ms)"); }
        else report.L($"  #{i + 1} → {targetMs / 1000:0.000}s  send {sendMs:0.0} ms  NOT confirmed in 1.5 s (last off {lastOff:+0.0;-0.0} ms, {updates} updates)");
        await Task.Delay(400);
    }
    if (results.Count > 0)
        report.L($"  confirmed {results.Count}/{count}: min {results.Min():0.0} / avg {results.Average():0.0} / max {results.Max():0.0} ms; landing offset avg {offsets.Average():+0.0;-0.0} ms, worst {offsets.Select(Math.Abs).Max():0.0} ms");
}

await rest.SetParameterByIdAsync(target.SpeedParamId, target.SpeedValue ?? 1.0); // warm the HttpClient connection
await SeekSeries("WS set /parameter/by-id", args_.Seeks, raw => sock.SetAsync(ById(target.PositionParamId), raw));
await SeekSeries("WS set logical path", 3, raw => sock.SetAsync(target.PositionPath, raw));
await SeekSeries("REST PUT /parameter/by-id", Math.Max(3, args_.Seeks / 2), async raw =>
{
    var code = await rest.SetParameterByIdAsync(target.PositionParamId, raw);
    if (code != 204) report.L($"    REST returned HTTP {code}");
});
await SeekSeries("REST PUT clip (partial body)", 3, async raw =>
{
    var code = await rest.SetClipPositionAsync(target.Layer, target.Column, raw);
    if (code != 204) report.L($"    REST returned HTTP {code}");
});

// ---------------------------------------------------------------- connect-and-seek order
if (!args_.NoConnectTests)
{
    report.H("Connect-and-seek order (clip cleared from the layer before each run, target 60 s or mid-clip)");
    double seekMs = Math.Min(60000, (target.DurationMs ?? 120000) / 2);
    var seekRaw = units.ToRaw(seekMs);
    var tolRaw = units.ToRaw(frameMs) - units.ToRaw(0);

    async Task RunOrder(string name, Func<Task> body)
    {
        await ClearLayerAsync();
        lock (posGate) posLog.Clear();
        report.L($"  {name}:");
        var t0 = Stopwatch.GetTimestamp();
        await body();
        await Task.Delay(1500);
        var snap = Snapshot().Where(p => p.stamp >= t0).ToArray();
        var seq = snap.Take(14).Select(p => $"+{(p.stamp - t0) * 1000.0 / Stopwatch.Frequency:0}ms={units.ToMs(p.raw) / 1000:0.000}s");
        var tail = snap.Length > 14 ? "  …  " + string.Join("  ", snap.Skip(Math.Max(14, snap.Length - 3)).Select(p => $"+{(p.stamp - t0) * 1000.0 / Stopwatch.Frequency:0}ms={units.ToMs(p.raw) / 1000:0.000}s")) : "";
        report.L($"    position updates: {string.Join("  ", seq)}{tail}");
        // "Landed" = the last time the stream arrived within 3 frames of the target and then kept advancing from there.
        int landedIdx = -1;
        for (int i = 0; i < snap.Length; i++)
        {
            if (Math.Abs(snap[i].raw - seekRaw) > tolRaw * 3) continue;
            bool stays = true;
            for (int j = i + 1; j < snap.Length; j++) if (units.ToMs(snap[j].raw) < seekMs - 500) { stays = false; break; }
            if (stays) { landedIdx = i; break; }
        }
        if (landedIdx < 0) report.L("    RESULT: never settled within 3 frames of the target");
        else
        {
            var landedMs = (snap[landedIdx].stamp - t0) * 1000.0 / Stopwatch.Frequency;
            var nearStart = snap.Take(landedIdx).Where(p => units.ToMs(p.raw) < 1000).ToArray();
            var startWindow = nearStart.Length == 0 ? 0 : (snap[landedIdx].stamp - nearStart[0].stamp) * 1000.0 / Stopwatch.Frequency;
            report.L(nearStart.Length == 0
                ? $"    RESULT: settled at target +{landedMs:0} ms after start; the transport never reported the clip start (no flash expected)"
                : $"    RESULT: settled at target +{landedMs:0} ms after start; before that the transport sat near 0 for {nearStart.Length} update(s) spanning {startWindow:0} ms (clip start likely visible for about that long)");
        }
    }

    var posId = ById(target.PositionParamId);
    await RunOrder("A: WS trigger connect, then WS set position immediately", async () =>
    {
        await sock.TriggerAsync(target.ConnectPath);
        await sock.SetAsync(posId, seekRaw);
    });
    await RunOrder("B: WS set position on the disconnected clip, then WS trigger connect", async () =>
    {
        await sock.SetAsync(posId, seekRaw);
        await Task.Delay(50);
        await sock.TriggerAsync(target.ConnectPath);
    });
    await RunOrder("C: WS trigger connect, wait 100 ms, WS set position", async () =>
    {
        await sock.TriggerAsync(target.ConnectPath);
        await Task.Delay(100);
        await sock.SetAsync(posId, seekRaw);
    });
    await RunOrder("D: WS trigger connect, wait for 'connected' update, then WS set position", async () =>
    {
        await sock.TriggerAsync(target.ConnectPath);
        await bus.WaitForAsync(m => m.Type == "parameter_update" && m.IsPath(target.ConnectedPath, target.ConnectedParamId) && m.ValueString.StartsWith("Connected"), 2000);
        await sock.SetAsync(posId, seekRaw);
    });
    await RunOrder("B2: WS set position on the disconnected clip, wait 300 ms, then WS trigger connect", async () =>
    {
        await sock.SetAsync(posId, seekRaw);
        await Task.Delay(300);
        await sock.TriggerAsync(target.ConnectPath);
    });
    await RunOrder("E: REST POST connect, then REST PUT position by-id", async () =>
    {
        await rest.ConnectClipAsync(target.Layer, target.Column);
        await rest.SetParameterByIdAsync(target.PositionParamId, seekRaw);
    });
    await RunOrder("G: WS trigger connect, wait for the position RESET update (value < 1 s), then WS set position", async () =>
    {
        var sw = Stopwatch.StartNew();
        await sock.TriggerAsync(target.ConnectPath);
        var reset = await bus.WaitForAsync(m => m.Type == "parameter_update" && m.IsPath(target.PositionPath, target.PositionParamId) && m.Number is < 1000, 1000);
        var tReset = sw.Elapsed.TotalMilliseconds;
        await sock.SetAsync(posId, seekRaw);
        report.L($"    reset update seen at +{tReset:0.0} ms (value {reset?.Number}), seek sent at +{sw.Elapsed.TotalMilliseconds:0.0} ms");
    });
    // playmodeaway (Restart | Continue | Relative) looks like the retrigger setting. Try seek-then-connect under each.
    var pmaId = CompositionParser.Num(await GetClipJson(), "x") is null ? FindParamId(await GetClipJson(), "transport", "controls", "playmodeaway") : 0;
    var pmaOriginal = FindParamValue(await GetClipJson(), "transport", "controls", "playmodeaway");
    report.L($"  clip playmodeaway param id={pmaId}, current value \"{pmaOriginal}\"");
    foreach (var mode in new[] { "Continue", "Relative" })
    {
        if (pmaId == 0) break;
        await sock.SetAsync(ById(pmaId), mode);
        await Task.Delay(100);
        var now = FindParamValue(await GetClipJson(), "transport", "controls", "playmodeaway");
        await RunOrder($"H-{mode}: playmodeaway=\"{now}\": WS set position on the disconnected clip, wait 50 ms, then WS trigger connect", async () =>
        {
            await sock.SetAsync(posId, seekRaw);
            await Task.Delay(50);
            await sock.TriggerAsync(target.ConnectPath);
        });
        await RunOrder($"I-{mode}: playmodeaway=\"{now}\": WS trigger connect, then WS set position immediately", async () =>
        {
            await sock.TriggerAsync(target.ConnectPath);
            await sock.SetAsync(posId, seekRaw);
        });
    }
    if (pmaId != 0)
    {
        await sock.SetAsync(ById(pmaId), pmaOriginal);
        await Task.Delay(100);
        report.L($"  playmodeaway restored to \"{FindParamValue(await GetClipJson(), "transport", "controls", "playmodeaway")}\"");
    }

    await RunOrder("F: REST PUT position 'in' point = target, then WS trigger connect (in restored to 0 after 700 ms)", async () =>
    {
        var code = await rest.SetParameterFieldsAsync(target.PositionParamId, new { @in = seekRaw });
        var check = await rest.GetRawAsync($"/parameter/by-id/{target.PositionParamId}");
        report.L($"    PUT in={seekRaw} → HTTP {code}; param now: {Trunc(check, 200)}");
        await Task.Delay(50);
        await sock.TriggerAsync(target.ConnectPath);
        await Task.Delay(700);
        var code2 = await rest.SetParameterFieldsAsync(target.PositionParamId, new { @in = 0.0 });
        var check2 = await rest.GetRawAsync($"/parameter/by-id/{target.PositionParamId}");
        report.L($"    restored in=0 → HTTP {code2}; param now: {Trunc(check2, 200)}");
    });

    await ClearLayerAsync();
    report.L("  layer left cleared (as found, if nothing was playing before)");
}

report.H("Done");
report.L($"Report written to {report.Path}");
return 0;

// ================================================================ helpers

(long stamp, double raw)[] Snapshot() { lock (posGate) return posLog.ToArray(); }
static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

async Task ClearLayerAsync()
{
    var waiter = bus.WaitForAsync(x => x.Type == "parameter_update" && x.IsPath(target!.ConnectedPath, target.ConnectedParamId) && !x.ValueString.StartsWith("Connected"), 3000);
    var code = await rest.ClearLayerAsync(target!.Layer);
    var m = await waiter;
    report.L($"  layer {target.Layer} cleared (HTTP {code}) → clip state \"{m?.ValueString ?? "?"}\"");
    await Task.Delay(800);
}

async Task<JsonElement> GetClipJson()
{
    using var doc = JsonDocument.Parse(await rest.GetRawAsync($"/composition/layers/{target!.Layer}/clips/{target.Column}"));
    return doc.RootElement.Clone();
}

static JsonElement Walk(JsonElement el, params string[] keys)
{
    foreach (var k in keys)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(k, out el)) return default;
    }
    return el;
}
static long FindParamId(JsonElement clip, params string[] keys) => CompositionParser.Num(Walk(clip, keys), "id") is double d ? (long)d : 0;
static string FindParamValue(JsonElement clip, params string[] keys) => CompositionParser.ParamValueString(Walk(clip, keys));

sealed record Args(string Host, int Port, int Layer, int? Column, string? OpenPath, int Seconds, int Seeks, bool NoConnectTests, bool NoSpeedTest)
{
    public static Args Parse(string[] a)
    {
        string host = "localhost"; int port = 8080, layer = 1, seconds = 10, seeks = 10; int? column = null; string? open = null; bool noConn = false, noSpeed = false;
        for (int i = 0; i < a.Length; i++)
        {
            string Next() => i + 1 < a.Length ? a[++i] : throw new ArgumentException($"{a[i]} needs a value");
            switch (a[i])
            {
                case "--host": host = Next(); break;
                case "--port": port = int.Parse(Next()); break;
                case "--layer": layer = int.Parse(Next()); break;
                case "--column": column = int.Parse(Next()); break;
                case "--open": open = Next(); break;
                case "--seconds": seconds = int.Parse(Next()); break;
                case "--seeks": seeks = int.Parse(Next()); break;
                case "--no-connect-tests": noConn = true; break;
                case "--no-speed-test": noSpeed = true; break;
                default: Console.WriteLine($"unknown arg {a[i]}"); break;
            }
        }
        return new Args(host, port, layer, column, open, seconds, seeks, noConn, noSpeed);
    }
}

sealed class Report
{
    public string Path { get; }
    public Report(string path) { Path = System.IO.Path.GetFullPath(path); }
    public void H(string s) => L($"{Environment.NewLine}== {s} ==");
    public void L(string s)
    {
        Console.WriteLine(s);
        try { File.AppendAllText(Path, s + Environment.NewLine); } catch { }
    }
}

sealed class Msg
{
    public string Text = ""; public long Stamp; public double RelMs; public JsonElement Json; public string Type = ""; public string Path = "";
    public JsonElement Param; public long ParamId; public string? Error; public double? Min, Max;
    public double? Number => Param.ValueKind == JsonValueKind.Object ? CompositionParser.Num(Param, "value") : Param.ValueKind == JsonValueKind.Number ? Param.GetDouble() : null;
    public string ValueString => CompositionParser.ParamValueString(Param);

    /// <summary>Resolume answers with the parameter's internal name, so ".../connected" comes back as ".../connect".</summary>
    public static string Norm(string p) => p.EndsWith("/connected", StringComparison.OrdinalIgnoreCase) ? p[..^2] : p;

    public bool IsPath(string logical, long id)
        => string.Equals(Norm(Path), Norm(logical), StringComparison.OrdinalIgnoreCase)
        || (id != 0 && (ParamId == id || string.Equals(Path, $"/parameter/by-id/{id}", StringComparison.OrdinalIgnoreCase)));
}

sealed class Bus
{
    private readonly long _t0 = Stopwatch.GetTimestamp();
    private readonly List<Action<Msg>> _listeners = new();
    private readonly HashSet<string> _seen = new();

    public void Listen(Action<Msg> l) { lock (_listeners) _listeners.Add(l); }

    public void OnRaw(string text, long stamp)
    {
        Msg m;
        try
        {
            var doc = JsonDocument.Parse(text);
            var root = doc.RootElement.Clone();
            doc.Dispose();
            m = new Msg { Text = text, Stamp = stamp, RelMs = (stamp - _t0) * 1000.0 / Stopwatch.Frequency, Json = root };
            m.Type = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var t) ? t.ToString()
                   : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("layers", out _) ? "composition" : "(untyped)";
            m.Path = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("path", out var p) ? p.ToString() : "";
            if (root.ValueKind == JsonValueKind.Object)
            {
                // Arena 7.24 puts the parameter's fields (id, valuetype, value, min, max, options…) at the message root.
                if (root.TryGetProperty("value", out var v)) m.Param = v;
                else if (root.TryGetProperty("param", out var pr)) m.Param = pr;
                if (CompositionParser.Num(root, "id") is double rid) m.ParamId = (long)rid;
                else if (m.Param.ValueKind == JsonValueKind.Object && CompositionParser.Num(m.Param, "id") is double id) m.ParamId = (long)id;
                m.Min = CompositionParser.Num(root, "min") ?? (m.Param.ValueKind == JsonValueKind.Object ? CompositionParser.Num(m.Param, "min") : null);
                m.Max = CompositionParser.Num(root, "max") ?? (m.Param.ValueKind == JsonValueKind.Object ? CompositionParser.Num(m.Param, "max") : null);
                if (root.TryGetProperty("error", out var err)) m.Error = err.ToString();
            }
        }
        catch (Exception ex) { Console.WriteLine($"[bus] bad JSON: {ex.Message}"); return; }

        bool first; lock (_seen) first = _seen.Add(m.Type);
        if (first) Console.WriteLine($"    [first '{m.Type}' message, {text.Length:N0} bytes] {(text.Length > 500 ? text[..500] + "…" : text)}");
        else if (m.Error is not null) Console.WriteLine($"    [error reply] {text}");

        Action<Msg>[] ls; lock (_listeners) ls = _listeners.ToArray();
        foreach (var l in ls) l(m);
    }

    public async Task<Msg?> WaitForAsync(Func<Msg, bool> pred, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<Msg>(TaskCreationOptions.RunContinuationsAsynchronously);
        void L(Msg m) { if (pred(m)) tcs.TrySetResult(m); }
        Listen(L);
        try
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            return done == tcs.Task ? tcs.Task.Result : null;
        }
        finally { lock (_listeners) _listeners.Remove(L); }
    }
}

sealed class Units
{
    public string Unit = "raw", Why = "";
    private double _durMs = 1;
    public static Units Detect(ClipInfo c)
    {
        var u = new Units { _durMs = c.DurationMs is > 0 ? c.DurationMs.Value : (c.TransportDurationValue is > 0 ? c.TransportDurationValue.Value : 1) };
        if (c.PosMax is not double max) { u.Why = "no max"; return u; }
        if (max <= 1.0001) { u.Unit = "normalised"; u.Why = "max ≈ 1, so ms = raw × file duration_ms"; return u; }
        if (Math.Abs(max - u._durMs) <= u._durMs * 0.02 + 2) { u.Unit = "ms"; u.Why = "max ≈ duration_ms"; return u; }
        if (Math.Abs(max - u._durMs / 1000) <= u._durMs / 1000 * 0.02 + 0.05) { u.Unit = "s"; u.Why = "max ≈ duration in seconds"; return u; }
        u.Why = $"max {max} does not match duration {u._durMs:0} ms; treating raw as ms";
        return u;
    }
    public double ToMs(double raw) => Unit switch { "normalised" => raw * _durMs, "s" => raw * 1000, _ => raw };
    public double ToRaw(double ms) => Unit switch { "normalised" => ms / _durMs, "s" => ms / 1000, _ => ms };
}

static class Stats
{
    public static string Describe((long stamp, double raw)[] log, Units u, ClipInfo c)
    {
        if (log.Length < 2) return $"  only {log.Length} position update(s) received";
        var intervals = new List<double>();
        for (int i = 1; i < log.Length; i++) intervals.Add((log[i].stamp - log[i - 1].stamp) * 1000.0 / Stopwatch.Frequency);
        var span = (log[^1].stamp - log[0].stamp) * 1000.0 / Stopwatch.Frequency;
        var sorted = intervals.OrderBy(x => x).ToArray();
        var advancedMs = u.ToMs(log[^1].raw) - u.ToMs(log[0].raw);
        return $"  {log.Length} updates in {span / 1000:0.0} s = {(log.Length - 1) * 1000.0 / span:0.0} Hz; interval avg {intervals.Average():0.0} / median {sorted[sorted.Length / 2]:0.0} / min {sorted[0]:0.0} / max {sorted[^1]:0.0} ms; playhead advanced {advancedMs / 1000:0.00} s in {span / 1000:0.00} s (ratio {advancedMs / span:0.00})";
    }
}
