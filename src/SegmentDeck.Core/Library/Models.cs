namespace SegmentDeck.Core.Library;

/// <summary>How a song finds its clip in Resolume. Never a layer or column: operators rearrange the deck.</summary>
public sealed class ClipRef
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ClipName { get; set; } = "";
}

public sealed class Segment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public long StartMs { get; set; }
    /// <summary>Optional. When set, the segment ends here instead of at the next segment's start, and the time up to the
    /// next segment is a gap that belongs to nothing.</summary>
    public long? EndMs { get; set; }
    public string Color { get; set; } = "#4A90D9";
    public string Lyric { get; set; } = "";
    /// <summary>Library-relative path, e.g. <c>thumbs/&lt;songId&gt;/&lt;segmentId&gt;.jpg</c>.</summary>
    public string? Thumb { get; set; }
    /// <summary>Start time the thumbnail was rendered at; regenerate when it differs from StartMs.</summary>
    public long? ThumbAtMs { get; set; }
}

public sealed class Song
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public ClipRef Clip { get; set; } = new();
    public long DurationMs { get; set; }
    /// <summary>Frame rate for hh:mm:ss:ff display, taken from Resolume's clip when known.</summary>
    public double Fps { get; set; } = Timecode.DefaultFps;
    public List<Segment> Segments { get; set; } = new();
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Segments in start order. A segment ends where the next one starts; the last runs to the clip end.</summary>
    public void SortSegments() => Segments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

    /// <summary>Where a segment ends: its own end point if set (and before the next start), else the next start, else the clip end.</summary>
    public long SegmentEndMs(int index)
    {
        var seg = Segments[index];
        var natural = index + 1 < Segments.Count ? Segments[index + 1].StartMs : Math.Max(DurationMs, seg.StartMs);
        if (seg.EndMs is long end && end > seg.StartMs && end < natural) return end;
        return natural;
    }

    /// <summary>True when the segment has an end point before the next segment, leaving a gap that belongs to nothing.</summary>
    public bool HasGapAfter(int index)
    {
        var natural = index + 1 < Segments.Count ? Segments[index + 1].StartMs : Math.Max(DurationMs, Segments[index].StartMs);
        return SegmentEndMs(index) < natural;
    }

    /// <summary>Index of the segment that contains <paramref name="positionMs"/>; -1 before the first one or inside a gap.</summary>
    public int SegmentIndexAt(double positionMs)
    {
        int idx = -1;
        for (int i = 0; i < Segments.Count; i++)
            if (Segments[i].StartMs <= positionMs) idx = i; else break;
        if (idx >= 0 && positionMs >= SegmentEndMs(idx) && HasGapAfter(idx)) return -1;
        return idx;
    }

    /// <summary>The segment that starts next after <paramref name="positionMs"/>, or -1 when none does.</summary>
    public int NextSegmentIndexAfter(double positionMs)
    {
        for (int i = 0; i < Segments.Count; i++)
            if (Segments[i].StartMs > positionMs) return i;
        return -1;
    }

    /// <summary>Where the part playing at <paramref name="positionMs"/> ends: the current segment's end, or in a gap, the next start.</summary>
    public double BoundaryAfter(double positionMs)
    {
        var idx = SegmentIndexAt(positionMs);
        if (idx >= 0) return SegmentEndMs(idx);
        var next = NextSegmentIndexAfter(positionMs);
        return next >= 0 ? Segments[next].StartMs : Math.Max(DurationMs, positionMs);
    }
}

public sealed class Setlist
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public List<string> SongIds { get; set; } = new();
}
