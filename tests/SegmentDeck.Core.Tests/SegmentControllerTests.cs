using System.Diagnostics;
using SegmentDeck.Core.Library;
using SegmentDeck.Core.Playback;
using SegmentDeck.Core.Resolume;
using SegmentDeck.Core.Settings;
using Xunit;

namespace SegmentDeck.Core.Tests;

/// <summary>A Resolume stand-in that records what the controller sends and lets tests feed position updates.</summary>
public sealed class FakeLink : IResolumeLink
{
    public ConnectionState State { get; set; } = ConnectionState.Connected;
    public bool CanSend => State == ConnectionState.Connected;
    public ClipInfo? WatchedClip { get; set; }
    public List<(ClipInfo Clip, double Ms)> Seeks { get; } = new();
    public List<ClipInfo> Connects { get; } = new();
    public List<(ClipInfo Clip, double Ms)> InPoints { get; } = new();
    public List<string> Order { get; } = new();

    public event Action<ConnectionState>? StateChanged;
    public event Action<ClipInfo>? ClipStateChanged;
    public event Action<PositionUpdate>? PositionUpdated;
    public event Action<ClipInfo>? TransportChanged;

    public Task SetPositionMsAsync(ClipInfo clip, double ms) { lock (Seeks) { Seeks.Add((clip, ms)); Order.Add($"seek {ms}"); } return Task.CompletedTask; }
    public Task ConnectClipAsync(ClipInfo clip) { lock (Seeks) { Connects.Add(clip); Order.Add("connect"); } return Task.CompletedTask; }
    public Task<int> SetInPointMsAsync(ClipInfo clip, double ms) { lock (Seeks) { InPoints.Add((clip, ms)); Order.Add($"in {ms}"); } return Task.FromResult(204); }

    public void Feed(ClipInfo clip, double ms) => PositionUpdated?.Invoke(new PositionUpdate { Clip = clip, PositionMs = ms, Raw = ms, Timestamp = Stopwatch.GetTimestamp() });
    public void Drop() { State = ConnectionState.Disconnected; StateChanged?.Invoke(State); }
    public void Transport(ClipInfo clip, double speed, bool paused) { clip.Speed = speed; clip.PlayDirection = paused ? "||" : ">"; TransportChanged?.Invoke(clip); }
    public void ClipState(ClipInfo clip, string state) { clip.ConnectedState = state; ClipStateChanged?.Invoke(clip); }
}

public class SegmentControllerTests : IDisposable
{
    private readonly FakeLink _link = new();
    private readonly AppSettings _settings = new() { LaunchSongsFromSetlist = false };
    private readonly PlayheadEstimator _estimator = new();
    private readonly ClipInfo _clip;
    private readonly Song _song;
    private readonly SegmentController _ctl;
    private readonly List<string> _warnings = new();
    private readonly List<TriggerRejected> _rejected = new();
    private readonly List<(Segment, TriggerKind)> _fired = new();

    public SegmentControllerTests()
    {
        _clip = new ClipInfo { ClipId = 1, Layer = 1, Column = 1, Name = "Song", DurationMs = 10000, Fps = 25, PosMin = 0, PosMax = 10000, RetriggerMode = "Restart" };
        _clip.ConnectedState = "Connected";
        _link.WatchedClip = _clip;
        _song = new Song
        {
            Title = "Song",
            DurationMs = 10000,
            Segments =
            {
                new Segment { Name = "Verse", StartMs = 0 },
                new Segment { Name = "Chorus", StartMs = 1000 },
                new Segment { Name = "Bridge", StartMs = 5000 },
            },
        };
        _settings.LatencyOffsetMs = 40;
        _ctl = new SegmentController(_link, () => _settings, _ => _clip, _estimator);
        _ctl.Warning += w => { lock (_warnings) _warnings.Add(w); };
        _ctl.Rejected += r => { lock (_rejected) _rejected.Add(r); };
        _ctl.Fired += (s, k) => { lock (_fired) _fired.Add((s, k)); };
        _ctl.SetCurrentSong(_song, _clip);
        _link.Feed(_clip, 0);
    }

    public void Dispose() => _ctl.Dispose();

    /// <summary>Plays the fake clip in real time from <paramref name="fromMs"/> for <paramref name="forMs"/>, 100 Hz updates.</summary>
    private void Play(double fromMs, double forMs, double speed = 1.0)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < forMs)
        {
            _link.Feed(_clip, fromMs + sw.Elapsed.TotalMilliseconds * speed);
            Thread.Sleep(10);
        }
    }

    [Fact]
    public async Task Cut_seeks_immediately_and_clears_queue()
    {
        await _ctl.QueueAsync(_song, _song.Segments[2]);
        Assert.NotNull(_ctl.Queued);
        await _ctl.CutAsync(_song, _song.Segments[1]);
        Assert.Null(_ctl.Queued);
        Assert.Single(_link.Seeks);
        Assert.Equal(1000, _link.Seeks[0].Ms);
        Assert.Contains(_fired, f => f.Item1.Name == "Chorus" && f.Item2 == TriggerKind.Cut);
    }

    [Fact]
    public async Task Queue_fires_at_boundary_minus_offset()
    {
        // Verse ends at 1000 ms; offset 40 → seek to Bridge should go out when the estimate reaches ~960 ms.
        await _ctl.QueueAsync(_song, _song.Segments[2]);
        var q = _ctl.Queued!;
        Assert.False(q.IsNatural);
        Assert.InRange(_ctl.QueueCountdownMs()!.Value, 900, 970);

        var sw = Stopwatch.StartNew();
        double? firedAt = null;
        while (sw.ElapsedMilliseconds < 1500 && firedAt is null)
        {
            _link.Feed(_clip, sw.Elapsed.TotalMilliseconds);
            Thread.Sleep(10);
            lock (_link.Seeks) if (_link.Seeks.Count > 0) firedAt = sw.Elapsed.TotalMilliseconds;
        }
        Assert.NotNull(firedAt);
        Assert.InRange(firedAt!.Value, 940, 1010);
        Assert.Equal(5000, _link.Seeks[0].Ms);
        Assert.Null(_ctl.Queued);
        Assert.Contains(_fired, f => f.Item2 == TriggerKind.Queue);
    }

    [Fact]
    public async Task Natural_next_sends_nothing_and_clears_at_boundary()
    {
        await _ctl.QueueAsync(_song, _song.Segments[1]);
        Assert.True(_ctl.Queued!.IsNatural);
        Play(0, 1150);
        Assert.Empty(_link.Seeks);
        Assert.Null(_ctl.Queued);
    }

    [Fact]
    public async Task Queue_is_one_slot_and_esc_clears()
    {
        await _ctl.QueueAsync(_song, _song.Segments[2]);
        await _ctl.QueueAsync(_song, _song.Segments[1]);
        Assert.Equal("Chorus", _ctl.Queued!.Segment.Name);
        _ctl.ClearQueue();
        Assert.Null(_ctl.Queued);
        Play(0, 1100);
        Assert.Empty(_link.Seeks);
    }

    [Fact]
    public async Task Paused_clip_holds_the_queue()
    {
        await _ctl.QueueAsync(_song, _song.Segments[2]);
        _link.Feed(_clip, 900);
        _link.Transport(_clip, 1.0, paused: true);
        Thread.Sleep(400); // would have fired at ~960 ms if it were running
        Assert.Empty(_link.Seeks);
        Assert.NotNull(_ctl.Queued);
        _link.Transport(_clip, 1.0, paused: false);
        Play(900, 300);
        Assert.Single(_link.Seeks);
    }

    [Fact]
    public async Task Disconnect_clears_queue_and_reconnect_does_not_fire_it()
    {
        await _ctl.QueueAsync(_song, _song.Segments[2]);
        _link.Drop();
        Assert.Null(_ctl.Queued);
        _link.State = ConnectionState.Connected;
        Play(950, 200);
        Assert.Empty(_link.Seeks);
    }

    [Fact]
    public async Task Clip_not_live_is_rejected_when_launch_is_off_and_launches_when_on()
    {
        _clip.ConnectedState = "Disconnected";
        await _ctl.CutAsync(_song, _song.Segments[1]);
        Assert.Single(_rejected);
        Assert.Equal("Clip not live", _rejected[0].Reason);
        Assert.Empty(_link.Seeks);

        _settings.LaunchSongsFromSetlist = true;
        await _ctl.CutAsync(_song, _song.Segments[1]);
        // Default retrigger (Restart): in-point → connect, then restore in-point.
        Assert.Equal(new[] { "in 1000", "connect" }, _link.Order.Take(2));
        Thread.Sleep(700);
        Assert.Contains("in 0", _link.Order);
        Assert.Contains(_fired, f => f.Item2 == TriggerKind.Launch);
    }

    [Fact]
    public async Task Launch_uses_seek_then_connect_when_clip_retrigger_is_continue()
    {
        var clip = new ClipInfo { ClipId = 2, Layer = 1, Column = 2, DurationMs = 10000, PosMax = 10000, RetriggerMode = "Continue" };
        clip.ConnectedState = "Disconnected";
        using var ctl = new SegmentController(_link, () => new AppSettings { LaunchSongsFromSetlist = true }, _ => clip, new PlayheadEstimator());
        ctl.SetCurrentSong(_song, clip);
        await ctl.CutAsync(_song, _song.Segments[2]);
        Assert.Equal(new[] { "seek 5000", "connect" }, _link.Order);
        Assert.Empty(_link.InPoints);
    }

    [Fact]
    public async Task Launch_from_the_start_only_connects()
    {
        _clip.ConnectedState = "Disconnected";
        _settings.LaunchSongsFromSetlist = true;
        await _ctl.LaunchSongAsync(_song);
        Assert.Equal(new[] { "connect" }, _link.Order);
    }

    [Fact]
    public async Task Seek_verification_warns_when_playhead_does_not_land()
    {
        await _ctl.CutAsync(_song, _song.Segments[2]);
        Thread.Sleep(50);
        _link.Feed(_clip, 100);      // far from 5000, but inside the window: keep waiting
        Thread.Sleep(600);
        _link.Feed(_clip, 200);      // still far after 500 ms → warning
        Thread.Sleep(50);
        Assert.Contains(_warnings, w => w.Contains("not confirmed"));

        _warnings.Clear();
        await _ctl.CutAsync(_song, _song.Segments[1]);
        Thread.Sleep(20);
        _link.Feed(_clip, 1100);     // within 250 ms → verified, no warning
        Thread.Sleep(600);
        _link.Feed(_clip, 1700);
        Thread.Sleep(50);
        Assert.Empty(_warnings);
    }

    [Fact]
    public async Task Follow_role_cannot_fire()
    {
        _settings.Role = AppRole.Follow;
        await _ctl.CutAsync(_song, _song.Segments[1]);
        await _ctl.QueueAsync(_song, _song.Segments[1]);
        Assert.Equal(2, _rejected.Count);
        Assert.Empty(_link.Seeks);
        Assert.Null(_ctl.Queued);
    }

    [Fact]
    public async Task Song_change_clears_queue()
    {
        await _ctl.QueueAsync(_song, _song.Segments[2]);
        _ctl.SetCurrentSong(new Song { Title = "Other" }, null);
        Assert.Null(_ctl.Queued);
    }
}

public class PlayheadEstimatorTests
{
    [Fact]
    public void Advances_with_time_and_speed_and_holds_when_paused_or_stale()
    {
        var e = new PlayheadEstimator();
        Assert.Null(e.EstimateMs());
        var t0 = Stopwatch.GetTimestamp();
        e.Update(1000, t0);
        var later = t0 + (long)(0.5 * Stopwatch.Frequency);
        Assert.Equal(1500, e.EstimateMs(later)!.Value, 0);

        e.SetTransport(2.0, paused: false);
        var t1 = Stopwatch.GetTimestamp();
        e.Update(1000, t1);
        Assert.Equal(2000, e.EstimateMs(t1 + (long)(0.5 * Stopwatch.Frequency))!.Value, 0);

        e.SetTransport(1.0, paused: true);
        var t2 = Stopwatch.GetTimestamp();
        e.Update(3000, t2);
        Assert.Equal(3000, e.EstimateMs(t2 + Stopwatch.Frequency)!.Value, 0);

        e.SetTransport(1.0, paused: false);
        var t3 = Stopwatch.GetTimestamp();
        e.Update(3000, t3);
        Assert.Equal(3000, e.EstimateMs(t3 + 5 * Stopwatch.Frequency)!.Value, 0); // stale: holds
    }
}
