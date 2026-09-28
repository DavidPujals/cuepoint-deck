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
    public string Color { get; set; } = "#4A90D9";
    public string Lyric { get; set; } = "";
    /// <summary>Library-relative path, e.g. <c>thumbs/&lt;songId&gt;/&lt;segmentId&gt;.jpg</c>.</summary>
    public string? Thumb { get; set; }
}

public sealed class Song
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public ClipRef Clip { get; set; } = new();
    public long DurationMs { get; set; }
    public List<Segment> Segments { get; set; } = new();
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Segments in start order. A segment ends where the next one starts; the last runs to the clip end.</summary>
    public void SortSegments() => Segments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

    public long SegmentEndMs(int index)
        => index + 1 < Segments.Count ? Segments[index + 1].StartMs : Math.Max(DurationMs, Segments[index].StartMs);

    /// <summary>Index of the segment that contains <paramref name="positionMs"/>, or -1 before the first one.</summary>
    public int SegmentIndexAt(double positionMs)
    {
        int idx = -1;
        for (int i = 0; i < Segments.Count; i++)
            if (Segments[i].StartMs <= positionMs) idx = i; else break;
        return idx;
    }
}

public sealed class Setlist
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public List<string> SongIds { get; set; } = new();
}
