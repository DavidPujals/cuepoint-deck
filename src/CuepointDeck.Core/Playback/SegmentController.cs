using System.Diagnostics;
using CuepointDeck.Core.Library;
using CuepointDeck.Core.Logging;
using CuepointDeck.Core.Resolume;
using CuepointDeck.Core.Settings;

namespace CuepointDeck.Core.Playback;

public enum TriggerKind { Cut, Queue, Launch, Loop }

public sealed class QueuedSegment
{
    public required Song Song { get; init; }
    public required Segment Segment { get; init; }
    public required int SegmentIndex { get; init; }
    /// <summary>True when the queued segment is the one that would play next anyway: nothing is sent at the boundary.</summary>
    public bool IsNatural { get; init; }
    /// <summary>Armed by the loop, not the operator: at the end of this segment, seek back to its own start.</summary>
    public bool IsLoop { get; init; }
    public long CreatedAt { get; init; } = Stopwatch.GetTimestamp();
}

public sealed class TriggerRejected
{
    public required Segment Segment { get; init; }
    public required string Reason { get; init; }
}

/// <summary>The trigger engine. Everything that fires a segment goes through here: cards, keys, later ProPresenter or
/// Companion. Owns the one-slot queue, the boundary timer, seek verification and the launch order.
/// Never pauses or stops Resolume. All events fire on background threads.</summary>
public sealed class SegmentController : ISegmentController, IDisposable
{
    public const double VerifyToleranceMs = 250;
    public const double VerifyWindowMs = 500;

    private readonly IResolumeLink _link;
    private readonly Func<AppSettings> _settings;
    private readonly Func<Song, ClipInfo?> _resolveClip;
    private readonly PlayheadEstimator _estimator;
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _timerThread;
    private volatile bool _disposed;

    private Song? _currentSong;
    private QueuedSegment? _queued;
    private SeekProbe? _probe;
    /// <summary>Index of the segment that repeats at its end, or -1. Cleared by Esc and when the song changes.</summary>
    private int _loopIndex = -1;

    private sealed class SeekProbe
    {
        public required string Label;
        public required double TargetMs;
        public long SentStamp = Stopwatch.GetTimestamp();
        public int Updates;
    }

    public Song? CurrentSong { get { lock (_gate) return _currentSong; } }
    public QueuedSegment? Queued { get { lock (_gate) return _queued; } }
    public int LoopIndex { get { lock (_gate) return _loopIndex; } }
    public event Action? LoopChanged;
    public PlayheadEstimator Estimator => _estimator;

    /// <summary>Follow role: show everything, fire nothing.</summary>
    public bool ReadOnly => _settings().Role == AppRole.Follow;

    public event Action? QueueChanged;
    public event Action<Segment, TriggerKind>? Fired;
    public event Action<TriggerRejected>? Rejected;
    /// <summary>Seek verification failed or another warning for the status bar.</summary>
    public event Action<string>? Warning;
    /// <summary>The song being controlled changed (Resolume connected a different clip).</summary>
    public event Action<Song?>? CurrentSongChanged;

    public SegmentController(IResolumeLink link, Func<AppSettings> settings, Func<Song, ClipInfo?> resolveClip, PlayheadEstimator estimator)
    {
        _link = link;
        _settings = settings;
        _resolveClip = resolveClip;
        _estimator = estimator;

        _link.PositionUpdated += OnPosition;
        _link.TransportChanged += OnTransport;
        _link.StateChanged += OnState;
        _link.ClipStateChanged += OnClipState;

        _timerThread = new Thread(TimerLoop) { IsBackground = true, Name = "CuepointDeck queue timer", Priority = ThreadPriority.AboveNormal };
        _timerThread.Start();
    }

    // ------------------------------------------------------------------ current song

    /// <summary>Called by the app when Resolume's live clip maps to a different song (or none).</summary>
    public void SetCurrentSong(Song? song, ClipInfo? clip)
    {
        bool changed;
        lock (_gate)
        {
            changed = !ReferenceEquals(_currentSong, song) && _currentSong?.Id != song?.Id;
            _currentSong = song;
        }
        if (!changed) return;
        _estimator.Reset(clip?.DurationMs ?? 0);
        if (clip is not null) _estimator.SetTransport(clip.Speed, clip.IsPaused);
        ClearQueue("song changed");
        SetLoop(-1, "song changed");
        try { CurrentSongChanged?.Invoke(song); } catch (Exception ex) { Log.Error("CurrentSongChanged handler", ex); }
    }

    /// <summary>Loop one segment: when the playhead reaches its end it seeks back to the segment's start, every time,
    /// until the loop is turned off, Esc is pressed, or the song changes. An operator's Queue takes precedence at the
    /// boundary, and once another segment is live nothing repeats until this one is live again.</summary>
    public void ToggleLoop(Song song, Segment segment)
    {
        if (ReadOnly) { Reject(segment, "Follow role: this instance cannot loop segments"); return; }
        var index = song.Segments.IndexOf(segment);
        if (index < 0) return;
        if (CurrentSong?.Id != song.Id) { Reject(segment, "Song is not live"); return; }
        SetLoop(LoopIndex == index ? -1 : index, LoopIndex == index ? "turned off" : $"on {segment.Name}");
    }

    public void ClearLoop() => SetLoop(-1, "cleared");

    private void SetLoop(int index, string why)
    {
        int had; QueuedSegment? loopQueue = null;
        lock (_gate)
        {
            had = _loopIndex;
            if (had == index) return;
            _loopIndex = index;
            if (_queued is { IsLoop: true }) { loopQueue = _queued; _queued = null; }
        }
        Log.Info(index < 0 ? $"Loop off ({why})" : $"LOOP {why}: repeats at {Fmt(CurrentSong?.SegmentEndMs(index) ?? 0)}");
        if (loopQueue is not null) Raise(() => QueueChanged?.Invoke());
        Raise(() => LoopChanged?.Invoke());
        _wake.Set();
    }

    /// <summary>Called from the timer: while the looped segment is live and nothing else is queued, hold an internal
    /// queue entry that seeks back to its start at the boundary. Not re-armed inside the firing window, so the seek
    /// that was just sent has time to move the playhead before the next pass is scheduled.</summary>
    private void ArmLoopIfDue()
    {
        Song? song; int loop;
        lock (_gate) { song = _currentSong; loop = _loopIndex; if (_queued is not null) return; }
        if (song is null || loop < 0 || loop >= song.Segments.Count) return;
        if (_estimator.EstimateMs() is not double pos || _estimator.IsStale) return;
        if (song.SegmentIndexAt(pos) != loop) return;
        var fireAt = song.BoundaryAfter(pos) - _settings().LatencyOffsetMs;
        if (pos >= fireAt - 150) return;
        var q = new QueuedSegment { Song = song, Segment = song.Segments[loop], SegmentIndex = loop, IsLoop = true };
        lock (_gate) { if (_queued is not null || _loopIndex != loop) return; _queued = q; }
        Raise(() => QueueChanged?.Invoke());
    }

    // ------------------------------------------------------------------ ISegmentController

    public async Task CutAsync(Song song, Segment segment)
    {
        if (!Guard(song, segment, out var clip)) return;
        if (clip is null || !clip.IsConnected)
        {
            await LaunchOrRejectAsync(song, segment, clip);
            return;
        }
        ClearQueue("cut");
        Log.Info($"CUT \"{song.Title}\" -> {segment.Name} @ {Fmt(segment.StartMs)}");
        await SeekAsync(clip, segment.StartMs, $"{segment.Name}");
        Raise(() => Fired?.Invoke(segment, TriggerKind.Cut));
    }

    public async Task QueueAsync(Song song, Segment segment)
    {
        if (!Guard(song, segment, out var clip)) return;
        if (clip is null || !clip.IsConnected)
        {
            await LaunchOrRejectAsync(song, segment, clip);
            return;
        }

        var index = song.Segments.IndexOf(segment);
        var est = _estimator.EstimateMs();
        var currentIndex = est is double e ? song.SegmentIndexAt(e) : -1;
        // "Natural next" only when the current segment runs straight into the queued one (no gap in between).
        var natural = currentIndex >= 0 ? index == currentIndex + 1 && !song.HasGapAfter(currentIndex)
                                        : est is double e2 && index == song.NextSegmentIndexAfter(e2);
        var q = new QueuedSegment { Song = song, Segment = segment, SegmentIndex = index, IsNatural = natural };
        lock (_gate) _queued = q;
        Log.Info($"QUEUE \"{song.Title}\" -> {segment.Name} @ {Fmt(segment.StartMs)} (current segment {currentIndex + 1}, {(natural ? "natural next, nothing will be sent" : "will seek at the boundary")}, offset {_settings().LatencyOffsetMs} ms)");
        Raise(() => QueueChanged?.Invoke());
        _wake.Set();
    }

    public void ClearQueue() => ClearQueue("cleared");

    private void ClearQueue(string why)
    {
        QueuedSegment? had;
        lock (_gate) { had = _queued; _queued = null; }
        if (had is null) return;
        if (!had.IsLoop) Log.Info($"Queue cleared ({why}): {had.Segment.Name}");
        Raise(() => QueueChanged?.Invoke());
    }

    public Task LaunchSongAsync(Song song) => LaunchAtAsync(song, song.Segments.FirstOrDefault()?.StartMs ?? 0, null);

    /// <summary>Connect a clip that has no library song (a setlist entry taken from Resolume's columns). Starts from the top.</summary>
    public async Task LaunchClipAsync(ClipInfo clip)
    {
        if (ReadOnly) { Warn("Follow role: this instance cannot launch clips"); return; }
        if (!_link.CanSend) { Warn("Not connected to Resolume"); return; }
        ClearQueue("launch");
        try
        {
            Log.Info($"LAUNCH clip {clip.Display}: connect");
            await _link.ConnectClipAsync(clip);
        }
        catch (Exception ex) { Log.Error($"Launching clip {clip.Display}", ex); Warn($"Launch failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ guards and launch

    private bool Guard(Song song, Segment segment, out ClipInfo? clip)
    {
        clip = null;
        if (ReadOnly) { Reject(segment, "Follow role: this instance cannot fire segments"); return false; }
        if (!_link.CanSend) { Reject(segment, "Not connected to Resolume"); return false; }
        clip = _resolveClip(song);
        if (clip is null) { Reject(segment, $"\"{song.Title}\" is not in the composition"); return false; }
        return true;
    }

    private async Task LaunchOrRejectAsync(Song song, Segment segment, ClipInfo? clip)
    {
        if (!_settings().LaunchSongsFromSetlist)
        {
            Reject(segment, "Clip not live");
            return;
        }
        ClearQueue("launch");
        await LaunchAtAsync(song, segment.StartMs, segment);
    }

    /// <summary>Connect the song's clip and start it at <paramref name="startMs"/> without showing the clip's first frame.
    /// The order comes from Stage 1 (docs/RESOLUME_NOTES.md §6).</summary>
    public async Task LaunchAtAsync(Song song, double startMs, Segment? segment)
    {
        if (ReadOnly) { if (segment is not null) Reject(segment, "Follow role: this instance cannot launch songs"); return; }
        if (!_link.CanSend) { Warn("Not connected to Resolume"); return; }
        var clip = _resolveClip(song);
        if (clip is null) { Warn($"\"{song.Title}\" is not in the composition"); return; }

        ClearQueue("launch");
        var label = segment?.Name ?? "start";
        try
        {
            if (startMs <= 0)
            {
                Log.Info($"LAUNCH \"{song.Title}\" from the start on {clip.Location}: connect");
                await _link.ConnectClipAsync(clip);
            }
            else if (string.Equals(clip.RetriggerMode, "Continue", StringComparison.OrdinalIgnoreCase))
            {
                // Order H: the clip keeps its position across connect, so seek first.
                Log.Info($"LAUNCH \"{song.Title}\" at {label} {Fmt(startMs)} on {clip.Location}: seek then connect (clip retrigger = Continue)");
                await SeekAsync(clip, startMs, label, verify: false);
                await Task.Delay(30);
                await _link.ConnectClipAsync(clip);
                ArmVerification(startMs, label);
            }
            else
            {
                // Order F: in-point -> connect -> restore. Works with the default Restart retrigger, no flash.
                Log.Info($"LAUNCH \"{song.Title}\" at {label} {Fmt(startMs)} on {clip.Location}: in-point, connect, restore (clip retrigger = {clip.RetriggerMode})");
                var code = await _link.SetInPointMsAsync(clip, startMs);
                if (code is < 200 or > 299) Warn($"Resolume refused the in-point write (HTTP {code}); launching from the start instead");
                await _link.ConnectClipAsync(clip);
                ArmVerification(startMs, label);
                _ = RestoreInPointAsync(clip);
            }
            if (segment is not null) Raise(() => Fired?.Invoke(segment, TriggerKind.Launch));
        }
        catch (Exception ex)
        {
            Log.Error($"Launching \"{song.Title}\"", ex);
            Warn($"Launch failed: {ex.Message}");
        }
    }

    private async Task RestoreInPointAsync(ClipInfo clip)
    {
        // Give the transport a moment to start at the in-point, then put the in-point back so the clip loops normally.
        await Task.Delay(400);
        try
        {
            var code = await _link.SetInPointMsAsync(clip, 0);
            Log.Info($"In-point restored to 0 on {clip.Location} (HTTP {code})");
            if (code is < 200 or > 299) Warn($"Could not restore the clip's in-point (HTTP {code}). Check the clip in Resolume.");
        }
        catch (Exception ex)
        {
            Log.Error("Restoring in-point", ex);
            Warn("Could not restore the clip's in-point. Check the clip in Resolume.");
        }
    }

    // ------------------------------------------------------------------ seek and verification

    private async Task SeekAsync(ClipInfo clip, double ms, string label, bool verify = true)
    {
        try
        {
            if (verify) ArmVerification(ms, label);
            await _link.SetPositionMsAsync(clip, ms);
            Log.Info($"Seek sent: {clip.Location} -> {Fmt(ms)} ({label})");
        }
        catch (Exception ex)
        {
            lock (_gate) _probe = null;
            Log.Error("Seek", ex);
            Warn($"Seek failed: {ex.Message}");
        }
    }

    private void ArmVerification(double targetMs, string label)
    {
        lock (_gate) _probe = new SeekProbe { Label = label, TargetMs = targetMs };
        _wake.Set();
    }

    private void OnPosition(PositionUpdate u)
    {
        _estimator.Update(u.PositionMs, u.Timestamp);

        SeekProbe? probe;
        lock (_gate) probe = _probe;
        if (probe is not null && u.Timestamp > probe.SentStamp)
        {
            probe.Updates++;
            var since = PlayheadEstimator.TicksToMs(u.Timestamp - probe.SentStamp);
            var off = u.PositionMs - probe.TargetMs;
            if (Math.Abs(off) <= VerifyToleranceMs)
            {
                lock (_gate) if (ReferenceEquals(_probe, probe)) _probe = null;
                Log.Info($"Seek verified: {probe.Label} landed {off:+0;-0} ms from target after {since:0} ms");
            }
            else if (since > VerifyWindowMs)
            {
                lock (_gate) if (ReferenceEquals(_probe, probe)) _probe = null;
                var msg = $"Seek to {probe.Label} not confirmed: playhead is at {Fmt(u.PositionMs)}, expected {Fmt(probe.TargetMs)}";
                Log.Warn(msg);
                Warn(msg);
            }
        }

        // A queued jump may be due; the timer thread re-evaluates on every update.
        if (_queued is not null) _wake.Set();
    }

    private void OnTransport(ClipInfo clip)
    {
        if (clip.ClipId != _link.WatchedClip?.ClipId) return;
        _estimator.SetTransport(clip.Speed, clip.IsPaused);
        if (clip.IsPaused) Log.Info("Clip paused: a queued segment will wait");
        _wake.Set();
    }

    private void OnState(ConnectionState state)
    {
        if (state != ConnectionState.Connected)
        {
            // Never fire a queue late after reconnecting.
            ClearQueue("connection lost");
            lock (_gate) _probe = null;
        }
    }

    private void OnClipState(ClipInfo clip)
    {
        var current = CurrentSong;
        if (current is null) return;
        if (!clip.IsConnected && _resolveClip(current)?.ClipId == clip.ClipId)
            ClearQueue("clip no longer live");
    }

    // ------------------------------------------------------------------ boundary timer

    private void TimerLoop()
    {
        while (!_disposed)
        {
            QueuedSegment? q;
            SeekProbe? probe;
            lock (_gate) { q = _queued; probe = _probe; }

            if (probe is not null && PlayheadEstimator.TicksToMs(Stopwatch.GetTimestamp() - probe.SentStamp) > VerifyWindowMs + 100 && probe.Updates == 0)
            {
                lock (_gate) if (ReferenceEquals(_probe, probe)) _probe = null;
                var msg = $"Seek to {probe.Label} not confirmed: no position updates arrived";
                Log.Warn(msg);
                Warn(msg);
            }

            if (q is null)
            {
                ArmLoopIfDue();
                lock (_gate) q = _queued;
                if (q is null) { _wake.WaitOne(probe is null ? 500 : 100); continue; }
            }

            var waitMs = Evaluate(q);
            if (waitMs <= 0) continue;            // fired or cleared; loop again
            if (waitMs > 2) _wake.WaitOne((int)Math.Min(waitMs - 1, 50));
            else Thread.SpinWait(200);
        }
    }

    /// <summary>Returns ms until the next check, or 0 when the queue was fired or dropped.</summary>
    private double Evaluate(QueuedSegment q)
    {
        var song = q.Song;
        if (!ReferenceEquals(CurrentSong, song) && CurrentSong?.Id != song.Id) { ClearQueue("song changed"); return 0; }
        var est = _estimator.EstimateMs();
        if (est is not double pos) return 50;
        if (_estimator.IsPaused) return 50;
        if (_estimator.IsStale) return 100;

        var currentIndex = song.SegmentIndexAt(pos);
        if (q.IsLoop && currentIndex != q.SegmentIndex)
        {
            // The operator cut elsewhere (or the loop was turned off): this pass is over; ArmLoopIfDue re-arms if needed.
            lock (_gate) if (ReferenceEquals(_queued, q)) _queued = null;
            Raise(() => QueueChanged?.Invoke());
            return 0;
        }
        if (!q.IsLoop && currentIndex == q.SegmentIndex)
        {
            // We are already inside the queued segment (someone seeked there, or the natural boundary passed).
            ClearQueue(q.IsNatural ? "reached naturally" : "already there");
            return 0;
        }

        var boundary = song.BoundaryAfter(pos);
        var natural = !q.IsLoop && (currentIndex >= 0 ? q.SegmentIndex == currentIndex + 1 && !song.HasGapAfter(currentIndex)
                                                      : q.SegmentIndex == song.NextSegmentIndexAfter(pos));
        var fireAt = natural ? boundary : boundary - _settings().LatencyOffsetMs;
        var speed = Math.Max(0.05, _estimator.Speed);
        var remaining = (fireAt - pos) / speed;
        if (remaining > 0) return remaining;

        // Time to act.
        QueuedSegment? still;
        lock (_gate) { still = _queued; if (ReferenceEquals(still, q)) _queued = null; }
        if (!ReferenceEquals(still, q)) return 0;

        if (natural)
        {
            Log.Info($"Queue reached naturally: {q.Segment.Name} (nothing sent)");
            Raise(() => QueueChanged?.Invoke());
            return 0;
        }

        var clip = _resolveClip(song);
        if (clip is null || !_link.CanSend)
        {
            Log.Warn($"Queued jump to {q.Segment.Name} dropped: {(clip is null ? "clip not found" : "not connected")}");
            Raise(() => QueueChanged?.Invoke());
            return 0;
        }
        Log.Info($"{(q.IsLoop ? "LOOP" : "QUEUE FIRE")} -> {q.Segment.Name} @ {Fmt(q.Segment.StartMs)} (estimate {Fmt(pos)}, boundary {Fmt(boundary)}, offset {_settings().LatencyOffsetMs} ms)");
        _ = SeekAsync(clip, q.Segment.StartMs, q.Segment.Name);
        Raise(() => QueueChanged?.Invoke());
        Raise(() => Fired?.Invoke(q.Segment, q.IsLoop ? TriggerKind.Loop : TriggerKind.Queue));
        return 0;
    }

    /// <summary>Milliseconds until the queued segment fires, for the countdown on the card. Null when nothing is queued.</summary>
    public double? QueueCountdownMs()
    {
        var q = Queued;
        if (q is null || _estimator.EstimateMs() is not double pos) return null;
        var song = q.Song;
        var boundary = song.BoundaryAfter(pos);
        var fireAt = q.IsNatural ? boundary : boundary - _settings().LatencyOffsetMs;
        return Math.Max(0, (fireAt - pos) / Math.Max(0.05, _estimator.Speed));
    }

    // ------------------------------------------------------------------ helpers

    private void Reject(Segment segment, string reason)
    {
        Log.Warn($"Trigger rejected ({segment.Name}): {reason}");
        Raise(() => Rejected?.Invoke(new TriggerRejected { Segment = segment, Reason = reason }));
    }

    private void Warn(string message) => Raise(() => Warning?.Invoke(message));

    private static void Raise(Action a)
    {
        try { a(); } catch (Exception ex) { Log.Error("SegmentController event handler", ex); }
    }

    public static string Fmt(double ms) => Timecode.Format(ms);

    public void Dispose()
    {
        _disposed = true;
        _wake.Set();
        _link.PositionUpdated -= OnPosition;
        _link.TransportChanged -= OnTransport;
        _link.StateChanged -= OnState;
        _link.ClipStateChanged -= OnClipState;
    }
}
