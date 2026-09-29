using System.Diagnostics;

namespace CuepointDeck.Core.Playback;

/// <summary>Local estimate of the playhead between position updates: advanced by the high-resolution clock at the
/// clip's speed, corrected every time a real position arrives. Holds still while paused or when updates stop.</summary>
public sealed class PlayheadEstimator
{
    private readonly object _gate = new();
    private double _lastMs;
    private long _lastStamp;
    private double _speed = 1.0;
    private bool _paused;
    private bool _hasFix;
    private double _durationMs;

    /// <summary>If no update arrives for this long while supposedly playing, stop advancing (the stream is silent when
    /// Resolume pauses, and dead when the socket is gone).</summary>
    public static readonly double StaleAfterMs = 1500;

    public double Speed { get { lock (_gate) return _speed; } }
    public bool IsPaused { get { lock (_gate) return _paused; } }
    public bool HasFix { get { lock (_gate) return _hasFix; } }

    public void Reset(double durationMs = 0)
    {
        lock (_gate) { _hasFix = false; _lastStamp = 0; _lastMs = 0; _durationMs = durationMs; }
    }

    public void Update(double positionMs, long stamp)
    {
        lock (_gate) { _lastMs = positionMs; _lastStamp = stamp; _hasFix = true; }
    }

    public void SetTransport(double speed, bool paused)
    {
        lock (_gate)
        {
            // Re-anchor so the speed change applies from now, not from the last update.
            if (_hasFix) { _lastMs = EstimateLocked(Stopwatch.GetTimestamp()); _lastStamp = Stopwatch.GetTimestamp(); }
            _speed = speed;
            _paused = paused;
        }
    }

    /// <summary>Best guess of the playhead now, or null before the first real position.</summary>
    public double? EstimateMs(long? now = null)
    {
        lock (_gate)
        {
            if (!_hasFix) return null;
            return EstimateLocked(now ?? Stopwatch.GetTimestamp());
        }
    }

    /// <summary>Milliseconds since the last real position update.</summary>
    public double MsSinceUpdate(long? now = null)
    {
        lock (_gate)
        {
            if (!_hasFix) return double.PositiveInfinity;
            return TicksToMs((now ?? Stopwatch.GetTimestamp()) - _lastStamp);
        }
    }

    public bool IsStale => MsSinceUpdate() > StaleAfterMs;

    private double EstimateLocked(long now)
    {
        var elapsed = TicksToMs(now - _lastStamp);
        if (_paused || elapsed > StaleAfterMs || elapsed < 0) return _lastMs;
        var est = _lastMs + elapsed * _speed;
        if (_durationMs > 0) est = Math.Clamp(est, 0, _durationMs);
        return est;
    }

    public static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
