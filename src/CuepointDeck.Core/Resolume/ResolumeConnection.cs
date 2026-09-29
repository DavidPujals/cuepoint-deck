using System.Diagnostics;
using System.Text.Json;
using CuepointDeck.Core.Logging;

namespace CuepointDeck.Core.Resolume;

public enum ConnectionState { Disconnected, Connecting, Connected }

public sealed class PositionUpdate
{
    public required ClipInfo Clip { get; init; }
    public double PositionMs { get; init; }
    public double Raw { get; init; }
    /// <summary>Stopwatch timestamp when the message was received. Feed this to the playhead estimator.</summary>
    public long Timestamp { get; init; }
}

/// <summary>Owns the link to one Resolume instance: connects, retries every 2 s forever, parses the composition,
/// keeps clip connected states current, streams the watched clip's position, and sends seeks and triggers.
/// All events fire on background threads; the UI marshals to its dispatcher.</summary>
public sealed class ResolumeConnection : IResolumeLink, IAsyncDisposable
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private ResolumeSocket? _socket;
    private ResolumeRest? _rest;
    private readonly HashSet<long> _subscribed = new();
    private TaskCompletionSource<Composition>? _compositionArrived;
    private long _watchedClipId;
    private int _sessionCount;

    public string Host { get; }
    public int Port { get; }
    /// <summary>"localhost" is rewritten to 127.0.0.1: Arena listens on IPv4 only and the IPv6 attempt costs 2 s.</summary>
    public string EffectiveHost => Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : Host;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string? ProductInfo { get; private set; }
    public Composition? Composition { get; private set; }
    public ClipInfo? WatchedClip => Composition?.ClipById(Interlocked.Read(ref _watchedClipId));
    public string? LastError { get; private set; }

    public event Action<ConnectionState>? StateChanged;
    public event Action<Composition>? CompositionChanged;
    /// <summary>A clip's connected state changed (launched, cleared, previewing…).</summary>
    public event Action<ClipInfo>? ClipStateChanged;
    public event Action<PositionUpdate>? PositionUpdated;
    /// <summary>Speed or play direction of the watched clip changed.</summary>
    public event Action<ClipInfo>? TransportChanged;

    public ResolumeConnection(string host, int port)
    {
        Host = host;
        Port = port;
    }

    public void Start()
    {
        if (_loop is not null) return;
        _loop = Task.Run(RunAsync);
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        _socket?.Dispose();
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch { } }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _rest?.Dispose();
        _stop.Dispose();
    }

    // ------------------------------------------------------------------ connect / retry loop

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        bool quiet = false; // after the first failure, keep retrying without spamming the log
        while (!ct.IsCancellationRequested)
        {
            SetState(ConnectionState.Connecting);
            ResolumeSocket? socket = null;
            try
            {
                _rest?.Dispose();
                _rest = new ResolumeRest(EffectiveHost, Port);
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));

                ProductInfo = await _rest.GetProductAsync(connectTimeout.Token).ConfigureAwait(false);

                socket = new ResolumeSocket();
                _compositionArrived = new TaskCompletionSource<Composition>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_subscribed) _subscribed.Clear();
                socket.RawMessage += OnRawMessage;
                await socket.ConnectAsync(EffectiveHost, Port, connectTimeout.Token).ConfigureAwait(false);
                _socket = socket;

                var arrived = await Task.WhenAny(_compositionArrived.Task, Task.Delay(10_000, ct)).ConfigureAwait(false);
                if (arrived != _compositionArrived.Task) throw new TimeoutException("Resolume did not send the composition within 10 s");

                _sessionCount++;
                quiet = false;
                LastError = null;
                Log.Info($"Connected to {ProductInfo} at {EffectiveHost}:{Port} (session {_sessionCount})");
                SetState(ConnectionState.Connected);

                var reason = await socket.Closed.ConfigureAwait(false);
                if (!ct.IsCancellationRequested) Log.Warn($"Connection to Resolume lost: {reason}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                LastError = ex.Message;
                if (!quiet) Log.Warn($"Cannot reach Resolume at {EffectiveHost}:{Port}: {ex.Message}. Retrying every {RetryDelay.TotalSeconds:0} s.");
                quiet = true;
            }
            finally
            {
                if (socket is not null) { socket.RawMessage -= OnRawMessage; socket.Dispose(); }
                _socket = null;
                SetState(ConnectionState.Disconnected);
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(RetryDelay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        SetState(ConnectionState.Disconnected);
    }

    private void SetState(ConnectionState state)
    {
        if (State == state) return;
        State = state;
        try { StateChanged?.Invoke(state); } catch (Exception ex) { Log.Error("StateChanged handler", ex); }
    }

    // ------------------------------------------------------------------ inbound

    private void OnRawMessage(string text, long stamp)
    {
        ParameterMessage msg;
        try
        {
            using var doc = JsonDocument.Parse(text);
            msg = ParameterMessage.Parse(doc.RootElement.Clone());
        }
        catch (Exception ex) { Log.Warn($"Unreadable message from Resolume ({text.Length} bytes): {ex.Message}"); return; }

        try
        {
            switch (msg.Type)
            {
                case "composition": HandleComposition(msg.Root); break;
                case "parameter_update":
                case "parameter_subscribed":
                case "parameter_get":
                case "parameter_set": HandleParameter(msg, stamp); break;
                case "error": Log.Warn($"Resolume rejected a request: {msg.Error} ({msg.Path})"); break;
                case "sources_update":
                case "effects_update":
                case "thumbnail_update":
                case "parameter_unsubscribed": break;
                default: Log.Info($"Unhandled message type '{msg.Type}' ({text.Length} bytes)"); break;
            }
        }
        catch (Exception ex) { Log.Error($"Handling '{msg.Type}' message", ex); }
    }

    private void HandleComposition(JsonElement root)
    {
        var comp = Composition.Parse(root);
        var previous = Composition;
        Composition = comp;
        Log.Info($"Composition \"{comp.Name}\": {comp.Layers.Count} layers, {comp.Clips.Count(c => !c.IsEmpty)} clips with content" +
                 (previous is null ? "" : " (re-sent after a structural change)"));

        _ = SubscribeConnectedStatesAsync(comp);
        _compositionArrived?.TrySetResult(comp);

        // Keep watching the same clip across the reload; its param ids survive moves and reloads.
        var watchedId = Interlocked.Read(ref _watchedClipId);
        if (watchedId != 0)
        {
            var again = comp.ClipById(watchedId);
            if (again is not null) _ = SubscribeTransportAsync(again);
            else { Interlocked.Exchange(ref _watchedClipId, 0); Log.Warn("The watched clip is no longer in the composition"); }
        }

        try { CompositionChanged?.Invoke(comp); } catch (Exception ex) { Log.Error("CompositionChanged handler", ex); }
    }

    private async Task SubscribeConnectedStatesAsync(Composition comp)
    {
        var socket = _socket;
        if (socket is null) return;
        int count = 0;
        foreach (var clip in comp.Clips)
        {
            if (clip.IsEmpty || clip.ConnectedParamId == 0) continue;
            if (!MarkSubscribed(clip.ConnectedParamId)) continue;
            try { await socket.SubscribeAsync(ById(clip.ConnectedParamId)).ConfigureAwait(false); count++; }
            catch (Exception ex) { Log.Warn($"Subscribe failed: {ex.Message}"); return; }
        }
        if (count > 0) Log.Info($"Subscribed to {count} clip connected states");
    }

    private bool MarkSubscribed(long id)
    {
        lock (_subscribed) return _subscribed.Add(id);
    }

    private void HandleParameter(ParameterMessage msg, long stamp)
    {
        var comp = Composition;
        if (comp is null || msg.Id == 0) return;

        var clip = comp.ByPositionParam(msg.Id);
        if (clip is not null)
        {
            if (msg.Number is not double raw) return;
            var ms = clip.RawToMs(raw);
            clip.PositionMs = ms;
            clip.PositionTimestamp = stamp;
            if (clip.ClipId == Interlocked.Read(ref _watchedClipId))
                try { PositionUpdated?.Invoke(new PositionUpdate { Clip = clip, PositionMs = ms, Raw = raw, Timestamp = stamp }); }
                catch (Exception ex) { Log.Error("PositionUpdated handler", ex); }
            return;
        }

        clip = comp.ByConnectedParam(msg.Id);
        if (clip is not null)
        {
            var state = msg.ValueString;
            if (state == clip.ConnectedState) return;
            var before = clip.ConnectedState;
            clip.ConnectedState = state;
            Log.Info($"Clip {clip.Display}: {before} -> {state}");
            try { ClipStateChanged?.Invoke(clip); } catch (Exception ex) { Log.Error("ClipStateChanged handler", ex); }
            return;
        }

        clip = comp.BySpeedParam(msg.Id);
        if (clip is not null)
        {
            if (msg.Number is double s && Math.Abs(s - clip.Speed) > 1e-9)
            {
                clip.Speed = s;
                Log.Info($"Clip {clip.Display} speed -> {s:0.###}");
                try { TransportChanged?.Invoke(clip); } catch (Exception ex) { Log.Error("TransportChanged handler", ex); }
            }
            return;
        }

        clip = comp.ByPlayDirectionParam(msg.Id);
        if (clip is not null)
        {
            var dir = msg.ValueString;
            if (dir != clip.PlayDirection)
            {
                clip.PlayDirection = dir;
                Log.Info($"Clip {clip.Display} play direction -> {dir}");
                try { TransportChanged?.Invoke(clip); } catch (Exception ex) { Log.Error("TransportChanged handler", ex); }
            }
        }
    }

    // ------------------------------------------------------------------ watching

    /// <summary>Streams position, speed and play direction for one clip (the live song). Pass null to stop.</summary>
    public async Task WatchClipAsync(ClipInfo? clip)
    {
        var socket = _socket;
        var previousId = Interlocked.Exchange(ref _watchedClipId, clip?.ClipId ?? 0);
        if (socket is null) return;
        try
        {
            if (previousId != 0 && previousId != clip?.ClipId && Composition?.ClipById(previousId) is ClipInfo prev)
            {
                foreach (var id in new[] { prev.PositionParamId, prev.SpeedParamId, prev.PlayDirectionParamId })
                {
                    if (id == 0) continue;
                    lock (_subscribed) _subscribed.Remove(id);
                    await socket.UnsubscribeAsync(ById(id)).ConfigureAwait(false);
                }
            }
            if (clip is not null)
            {
                await SubscribeTransportAsync(clip).ConfigureAwait(false);
                Log.Info($"Watching {clip.Display} (position id {clip.PositionParamId}, range {clip.PosMin}..{clip.PosMax}, duration {clip.DurationMs:0} ms)");
            }
        }
        catch (Exception ex) { Log.Warn($"Watch failed: {ex.Message}"); }
    }

    private async Task SubscribeTransportAsync(ClipInfo clip)
    {
        var socket = _socket;
        if (socket is null) return;
        foreach (var id in new[] { clip.PositionParamId, clip.SpeedParamId, clip.PlayDirectionParamId })
        {
            if (id == 0 || !MarkSubscribed(id)) continue;
            await socket.SubscribeAsync(ById(id)).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ outbound

    public bool CanSend => State == ConnectionState.Connected && _socket?.IsOpen == true;

    /// <summary>Seek: writes the clip's position parameter. Sub-millisecond locally, see docs/RESOLUME_NOTES.md §5.</summary>
    public async Task SetPositionMsAsync(ClipInfo clip, double ms)
    {
        var socket = _socket ?? throw new InvalidOperationException("Not connected to Resolume");
        var raw = clip.MsToRaw(Math.Clamp(ms, 0, Math.Max(0, clip.DurationMs)));
        await socket.SetAsync(ById(clip.PositionParamId), raw).ConfigureAwait(false);
    }

    /// <summary>Connect (launch) a clip: the same as clicking it in Resolume.</summary>
    public async Task ConnectClipAsync(ClipInfo clip)
    {
        var socket = _socket ?? throw new InvalidOperationException("Not connected to Resolume");
        await socket.TriggerAsync(clip.ConnectPath).ConfigureAwait(false);
    }

    /// <summary>Sets the clip's position in-point (REST; the WebSocket only writes values). Used by the flash-free launch order.</summary>
    public async Task<int> SetInPointMsAsync(ClipInfo clip, double ms)
    {
        var rest = _rest ?? throw new InvalidOperationException("Not connected to Resolume");
        return await rest.SetParameterFieldsAsync(clip.PositionParamId, new { @in = clip.MsToRaw(ms) }).ConfigureAwait(false);
    }

    public async Task SetParameterAsync(long parameterId, object value)
    {
        var socket = _socket ?? throw new InvalidOperationException("Not connected to Resolume");
        await socket.SetAsync(ById(parameterId), value).ConfigureAwait(false);
    }

    public static string ById(long id) => $"/parameter/by-id/{id}";
}
