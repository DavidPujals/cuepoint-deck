using SegmentDeck.Core.Library;
using SegmentDeck.Core.Resolume;

namespace SegmentDeck.Core.Matching;

public enum MatchKind { None, Path, MappedPath, FileName }

public sealed class SongMatch
{
    public required Song Song { get; init; }
    public ClipInfo? Clip { get; init; }
    public MatchKind Kind { get; init; }
    public IReadOnlyList<ClipInfo> Candidates { get; init; } = Array.Empty<ClipInfo>();
    public bool IsAvailable => Clip is not null;
    public bool Ambiguous => Candidates.Count > 1;
    public string? Warning { get; init; }
}

/// <summary>Song ↔ clip lookup for one composition snapshot. Rebuilt on every composition message.</summary>
public sealed class MatchTable
{
    private readonly Dictionary<string, SongMatch> _bySong = new();
    private readonly Dictionary<long, Song> _byClipId = new();

    public IReadOnlyCollection<SongMatch> Matches => _bySong.Values;

    public SongMatch? For(string songId) => _bySong.TryGetValue(songId, out var m) ? m : null;
    public SongMatch? For(Song song) => For(song.Id);

    /// <summary>The song a clip belongs to, for following whatever Resolume connects. Includes duplicate clips.</summary>
    public Song? SongForClip(long clipId) => _byClipId.TryGetValue(clipId, out var s) ? s : null;

    internal void Add(SongMatch match)
    {
        _bySong[match.Song.Id] = match;
        foreach (var c in match.Candidates) _byClipId.TryAdd(c.ClipId, match.Song);
    }
}

public static class ClipMatcher
{
    /// <summary>Matches every song to a clip by source file path, then by mapped path, then by file name.
    /// More than one clip for a song: prefer the one on <paramref name="songLayer"/> and flag it.</summary>
    public static MatchTable Build(Composition? composition, IEnumerable<Song> songs, PathMapper mapper, int songLayer)
    {
        var table = new MatchTable();
        var clips = composition?.Clips.Where(c => !c.IsEmpty).ToList() ?? new List<ClipInfo>();

        var byPath = new Dictionary<string, List<ClipInfo>>();
        var byName = new Dictionary<string, List<ClipInfo>>();
        foreach (var c in clips)
        {
            if (string.IsNullOrEmpty(c.FilePath)) continue;
            Add(byPath, PathNorm.Key(c.FilePath), c);
            Add(byName, PathNorm.FileName(c.FilePath).ToUpperInvariant(), c);
        }

        foreach (var song in songs)
        {
            var kind = MatchKind.None;
            List<ClipInfo>? candidates = null;

            if (!string.IsNullOrEmpty(song.Clip.FilePath))
            {
                if (byPath.TryGetValue(PathNorm.Key(song.Clip.FilePath), out candidates)) kind = MatchKind.Path;
                else
                {
                    foreach (var variant in mapper.Variants(song.Clip.FilePath))
                        if (byPath.TryGetValue(PathNorm.Key(variant), out candidates)) { kind = MatchKind.MappedPath; break; }
                }
            }
            if (candidates is null)
            {
                var name = !string.IsNullOrEmpty(song.Clip.FileName) ? song.Clip.FileName : PathNorm.FileName(song.Clip.FilePath);
                if (!string.IsNullOrEmpty(name) && byName.TryGetValue(name.ToUpperInvariant(), out candidates)) kind = MatchKind.FileName;
            }

            if (candidates is null || candidates.Count == 0)
            {
                table.Add(new SongMatch { Song = song, Kind = MatchKind.None });
                continue;
            }

            var ordered = candidates.OrderBy(c => c.Layer).ThenBy(c => c.Column).ToList();
            var chosen = ordered.FirstOrDefault(c => c.Layer == songLayer) ?? ordered[0];
            string? warning = null;
            if (ordered.Count > 1)
                warning = $"Matches {ordered.Count} clips ({string.Join(", ", ordered.Select(c => $"L{c.Layer} C{c.Column}"))}); using L{chosen.Layer} C{chosen.Column}";

            table.Add(new SongMatch { Song = song, Clip = chosen, Kind = kind, Candidates = ordered, Warning = warning });
        }
        return table;
    }

    private static void Add(Dictionary<string, List<ClipInfo>> dict, string key, ClipInfo clip)
    {
        if (!dict.TryGetValue(key, out var list)) dict[key] = list = new List<ClipInfo>();
        list.Add(clip);
    }
}
