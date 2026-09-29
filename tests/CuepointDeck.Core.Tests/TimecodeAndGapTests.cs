using CuepointDeck.Core;
using CuepointDeck.Core.Library;
using Xunit;

namespace CuepointDeck.Core.Tests;

public class TimecodeTests
{
    [Theory]
    [InlineData(0, 25, "00:00:00:00")]
    [InlineData(40, 25, "00:00:00:01")]
    [InlineData(999, 25, "00:00:00:24")]
    [InlineData(1000, 25, "00:00:01:00")]
    [InlineData(61_500, 25, "00:01:01:12")]
    [InlineData(3_723_000, 30, "01:02:03:00")]
    [InlineData(195_240, 25, "00:03:15:06")]
    public void Formats_like_resolume(double ms, double fps, string expected) => Assert.Equal(expected, Timecode.Format(ms, fps));

    [Fact]
    public void Parses_frames_form_and_legacy_forms()
    {
        Assert.True(Timecode.TryParse("00:01:01:12", 25, out var a)); Assert.Equal(61_480, a);
        Assert.True(Timecode.TryParse("1:01:12", 25, out var b)); Assert.Equal(61_480, b);
        Assert.True(Timecode.TryParse("01:01.500", 25, out var c)); Assert.Equal(61_500, c);
        Assert.True(Timecode.TryParse("90", 25, out var d)); Assert.Equal(90_000, d);
        Assert.True(Timecode.TryParse("00:00:00:29", 30, out var e)); Assert.Equal(967, e);
        Assert.False(Timecode.TryParse("", 25, out _));
        Assert.False(Timecode.TryParse("abc", 25, out _));
        Assert.False(Timecode.TryParse("1:-2", 25, out _));
        // round trip at a frame boundary
        Assert.True(Timecode.TryParse(Timecode.Format(61_480, 25), 25, out var f)); Assert.Equal(61_480, f);
    }

    [Fact]
    public void Short_form_drops_hours() => Assert.Equal("03:15:06", Timecode.FormatShort(195_240, 25));
}

public class SegmentGapTests
{
    private static Song Make() => new()
    {
        DurationMs = 100_000,
        Segments =
        {
            new Segment { Name = "Verse", StartMs = 0 },
            new Segment { Name = "Chorus", StartMs = 20_000, EndMs = 35_000 },   // gap 35–50 s
            new Segment { Name = "Bridge", StartMs = 50_000 },
        },
    };

    [Fact]
    public void End_point_and_gap_rules()
    {
        var s = Make();
        Assert.Equal(20_000, s.SegmentEndMs(0));
        Assert.Equal(35_000, s.SegmentEndMs(1));
        Assert.Equal(100_000, s.SegmentEndMs(2));
        Assert.False(s.HasGapAfter(0));
        Assert.True(s.HasGapAfter(1));
        Assert.False(s.HasGapAfter(2));

        Assert.Equal(1, s.SegmentIndexAt(34_999));
        Assert.Equal(-1, s.SegmentIndexAt(35_000));   // in the gap
        Assert.Equal(-1, s.SegmentIndexAt(49_999));
        Assert.Equal(2, s.SegmentIndexAt(50_000));
        Assert.Equal(2, s.NextSegmentIndexAfter(40_000));
        Assert.Equal(-1, s.NextSegmentIndexAfter(60_000));

        Assert.Equal(35_000, s.BoundaryAfter(30_000));
        Assert.Equal(50_000, s.BoundaryAfter(40_000));    // gap: next start
        Assert.Equal(100_000, s.BoundaryAfter(60_000));
    }

    [Fact]
    public void End_after_next_start_is_ignored()
    {
        var s = Make();
        s.Segments[1].EndMs = 80_000;   // later than Bridge's start: meaningless, so the natural end wins
        Assert.Equal(50_000, s.SegmentEndMs(1));
        Assert.False(s.HasGapAfter(1));
    }
}
