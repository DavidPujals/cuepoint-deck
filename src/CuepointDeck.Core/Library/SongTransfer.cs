using System.Text;
using System.Text.Json;
using CuepointDeck.Core.Resolume;

namespace CuepointDeck.Core.Library;

/// <summary>
/// A file of songs for another campus (or a backup): timings, segment names, colours and lyric notes. It deliberately
/// leaves out what differs between campuses: media file paths, Resolume columns, thumbnails. On import each song is
/// linked to a column on that campus's song layer, and thumbnails are rendered there from its own video.
/// </summary>
public sealed class TransferFile
{
    /// <summary>Empty unless the file says what it is, so any other JSON is refused rather than read as empty.</summary>
    public string Format { get; set; } = "";
    public int Version { get; set; } = SongTransfer.CurrentVersion;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>The PC that exported it, shown on import so people can tell files apart.</summary>
    public string ExportedFrom { get; set; } = "";
    public List<TransferSong> Songs { get; set; } = new();
}

public sealed class TransferSong
{
    /// <summary>The song's id at the exporting campus. Kept on import as the new song's id, so a later import of the
    /// same song finds it again even if it was renamed.</summary>
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public long DurationMs { get; set; }
    public double Fps { get; set; } = Timecode.DefaultFps;
    /// <summary>Hints for matching at the other campus; never used as paths.</summary>
    public string FileName { get; set; } = "";
    public string ClipName { get; set; } = "";
    public string ColumnName { get; set; } = "";
    public List<TransferSegment> Segments { get; set; } = new();
}

public sealed class TransferSegment
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long StartMs { get; set; }
    public long? EndMs { get; set; }
    public string Color { get; set; } = "#4A90D9";
    public string Lyric { get; set; } = "";
}

public static class SongTransfer
{
    public const string FormatName = "cuepoint-deck-songs";
    public const int CurrentVersion = 1;
    public const string Extension = ".cuedeck";

    // ------------------------------------------------------------------ export

    public static TransferFile Create(IEnumerable<Song> songs, Func<Song, string?>? columnNameOf = null, string? exportedFrom = null)
    {
        var file = new TransferFile { Format = FormatName, ExportedFrom = exportedFrom ?? Environment.MachineName };
        foreach (var song in songs)
        {
            var copy = new TransferSong
            {
                Id = song.Id,
                Title = song.Title,
                Artist = song.Artist,
                DurationMs = song.DurationMs,
                Fps = song.Fps,
                FileName = song.Clip.FileName,
                ClipName = song.Clip.ClipName,
                ColumnName = columnNameOf?.Invoke(song) ?? "",
            };
            foreach (var seg in song.Segments.OrderBy(s => s.StartMs))
                copy.Segments.Add(new TransferSegment { Id = seg.Id, Name = seg.Name, StartMs = seg.StartMs, EndMs = seg.EndMs, Color = seg.Color, Lyric = seg.Lyric });
            file.Songs.Add(copy);
        }
        return file;
    }

    public static string Serialize(TransferFile file) => Json.Serialize(file);

    // ------------------------------------------------------------------ import

    /// <summary>Reads a transfer file. Throws <see cref="FormatException"/> with a message fit to show people.</summary>
    public static TransferFile Parse(string json)
    {
        TransferFile? file;
        try { file = Json.Deserialize<TransferFile>(json); }
        catch (JsonException) { throw new FormatException("This isn't a Cuepoint Deck songs file, or it's damaged."); }
        if (file is null || !string.Equals(file.Format, FormatName, StringComparison.Ordinal))
            throw new FormatException("This isn't a Cuepoint Deck songs file.");
        if (file.Version > CurrentVersion)
            throw new FormatException("This file was made by a newer Cuepoint Deck. Update this copy (Settings → About) and try again.");
        file.Songs.RemoveAll(s => string.IsNullOrWhiteSpace(s.Title) && s.Segments.Count == 0);
        foreach (var s in file.Songs) s.Segments.RemoveAll(seg => seg.StartMs < 0);
        return file;
    }

    /// <summary>The song at this campus that an incoming song updates: the same id (imported before), or else the same
    /// title ignoring case, spacing and punctuation (built separately at each campus).</summary>
    public static Song? FindExisting(TransferSong incoming, IEnumerable<Song> library)
    {
        var songs = library as IList<Song> ?? library.ToList();
        if (!string.IsNullOrEmpty(incoming.Id))
        {
            var byId = songs.FirstOrDefault(s => s.Id == incoming.Id);
            if (byId is not null) return byId;
        }
        var key = NormalizeTitle(incoming.Title);
        return key.Length == 0 ? null : songs.FirstOrDefault(s => NormalizeTitle(s.Title) == key);
    }

    /// <summary>"Way Maker (Live)" and "way maker - live" compare equal: letters and digits only, lower case.</summary>
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var sb = new StringBuilder(title.Length);
        foreach (var ch in title.Normalize(NormalizationForm.FormKD))
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>
    /// The column to link an incoming song to: the column its existing song already plays from, else a song-layer
    /// column named like the song, else one whose clip has the same file name, else none.
    /// </summary>
    public static ClipInfo? SuggestColumn(TransferSong incoming, Song? existing, ClipInfo? existingClip,
                                          IReadOnlyList<ClipInfo> songLayerClips, Func<int, string> columnName)
    {
        if (existingClip is not null && songLayerClips.Any(c => c.ClipId == existingClip.ClipId)) return existingClip;
        var titles = new[] { incoming.Title, existing?.Title, incoming.ColumnName }.Select(NormalizeTitle).Where(t => t.Length > 0).Distinct().ToList();
        foreach (var t in titles)
        {
            var byName = songLayerClips.FirstOrDefault(c => NormalizeTitle(columnName(c.Column)) == t);
            if (byName is not null) return byName;
        }
        foreach (var t in titles)
        {
            var byClip = songLayerClips.FirstOrDefault(c => NormalizeTitle(c.Name) == t);
            if (byClip is not null) return byClip;
        }
        if (!string.IsNullOrWhiteSpace(incoming.FileName))
        {
            var byFile = songLayerClips.FirstOrDefault(c => string.Equals(Path.GetFileName(c.FilePath), incoming.FileName, StringComparison.OrdinalIgnoreCase));
            if (byFile is not null) return byFile;
        }
        return null;
    }

    /// <summary>
    /// The song to save for one incoming song. With <paramref name="existing"/>: that song with its segments replaced
    /// (its id, title and, unless a column is given, its clip link stay). Without: a new song. A chosen
    /// <paramref name="clip"/> links the song to that column's media at this campus and supplies its length and
    /// frame rate. Thumbnails are kept only where a segment still starts on the frame it was rendered at.
    /// </summary>
    public static Song Apply(TransferSong incoming, Song? existing, ClipInfo? clip)
    {
        Song song;
        if (existing is not null)
        {
            song = Json.Deserialize<Song>(Json.Serialize(existing))!;
            if (incoming.Artist.Length > 0) song.Artist = incoming.Artist;
            if (song.DurationMs <= 0) song.DurationMs = incoming.DurationMs;
        }
        else
        {
            song = new Song
            {
                Title = incoming.Title.Trim().Length > 0 ? incoming.Title.Trim() : incoming.ClipName,
                Artist = incoming.Artist,
                DurationMs = incoming.DurationMs,
                Fps = incoming.Fps > 0 ? incoming.Fps : Timecode.DefaultFps,
                Clip = new ClipRef { FileName = incoming.FileName, ClipName = incoming.ClipName },
            };
            if (!string.IsNullOrWhiteSpace(incoming.Id)) song.Id = incoming.Id;
        }

        if (clip is not null)
        {
            song.Clip = new ClipRef { FilePath = clip.FilePath, FileName = Path.GetFileName(clip.FilePath), ClipName = clip.Name };
            if (clip.DurationMs > 0) song.DurationMs = (long)Math.Round(clip.DurationMs);
            if (clip.Fps > 0) song.Fps = clip.Fps;
        }

        // A thumbnail stays valid only for the same media file and the same start frame.
        var sameMedia = existing is not null && (clip is null || string.Equals(existing.Clip.FilePath, clip.FilePath, StringComparison.OrdinalIgnoreCase));
        var oldThumbs = sameMedia ? existing!.Segments.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First()) : new Dictionary<string, Segment>();
        song.Segments = incoming.Segments.OrderBy(s => s.StartMs).Select(s =>
        {
            var seg = new Segment
            {
                Name = s.Name,
                StartMs = Math.Max(0, s.StartMs),
                EndMs = s.EndMs is long e && e > s.StartMs ? e : null,
                Color = string.IsNullOrWhiteSpace(s.Color) ? "#4A90D9" : s.Color,
                Lyric = s.Lyric,
            };
            if (!string.IsNullOrWhiteSpace(s.Id)) seg.Id = s.Id;
            if (oldThumbs.TryGetValue(seg.Id, out var old) && old.Thumb is not null && old.ThumbAtMs == seg.StartMs)
            {
                seg.Thumb = old.Thumb;
                seg.ThumbAtMs = old.ThumbAtMs;
            }
            return seg;
        }).ToList();
        return song;
    }
}
