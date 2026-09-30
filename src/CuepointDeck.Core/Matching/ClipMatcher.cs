using CuepointDeck.Core.Library;
using CuepointDeck.Core.Resolume;

namespace CuepointDeck.Core.Matching;

public enum MatchKind { None, Path, MappedPath, FileName }

public sealed class SongMatch
{
    public required Song Song { get; init; }
    public ClipInfo? Clip { get; init; }
    public MatchKind Kind { get; init; }
    /// <summary>Clips that could each be "the" song clip: those on the song layer, or, when the song layer has none,
    /// one per column elsewhere. More than one means the operator has to sort it out.</summary>
    public IReadOnlyList<ClipInfo> Candidates { get; init; } = Array.Empty<ClipInfo>();
    /// <summary>Every clip of this file in the composition, including the copies on other layers (overlays, triggers)
    /// that share a column with the song clip. Used to map whatever Resolume connects back to the song.</summary>
    public IReadOnlyList<ClipInfo> AllClips { get; init; } = Array.Empty<ClipInfo>();
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
        foreach (var c in match.AllClips) _byClipId.TryAdd(c.ClipId, match.Song);
    }
}

public static class ClipMatcher
{
    /// <summary>Matches every song to a clip by source file path, then by mapped path, then by file name.
    /// A song's file is normally in several layers of its column (background, overlay, the manual-trigger copy on the
    /// song layer), so only the song layer counts: one clip there is the match, more than one is flagged. Copies on
    /// other layers are never a conflict. Only when the song layer has none do other layers stand in, and then
    /// several columns is the conflict.</summary>
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

            var all = candidates.OrderBy(c => c.Layer).ThenBy(c => c.Column).ToList();
            var onSongLayer = all.Where(c => c.Layer == songLayer).OrderBy(c => c.Column).ToList();
            List<ClipInfo> contenders;
            string? warning = null;
            if (onSongLayer.Count > 0)
            {
                contenders = onSongLayer;
                if (contenders.Count > 1)
                    warning = $"In {contenders.Count} columns on the song layer ({string.Join(", ", contenders.Select(c => $"C{c.Column}"))}); using C{contenders[0].Column}";
            }
            else
            {
                // Not on the song layer at all: one stand-in per column, lowest layer first.
                contenders = all.GroupBy(c => c.Column).Select(g => g.OrderBy(c => c.Layer).First()).OrderBy(c => c.Column).ToList();
                if (contenders.Count > 1)
                    warning = $"Not on the song layer; found in columns {string.Join(", ", contenders.Select(c => $"C{c.Column}"))}; using L{contenders[0].Layer} C{contenders[0].Column}";
            }
            var chosen = contenders[0];

            table.Add(new SongMatch { Song = song, Clip = chosen, Kind = kind, Candidates = contenders, AllClips = all, Warning = warning });
        }
        return table;
    }

    private static void Add(Dictionary<string, List<ClipInfo>> dict, string key, ClipInfo clip)
    {
        if (!dict.TryGetValue(key, out var list)) dict[key] = list = new List<ClipInfo>();
        list.Add(clip);
    }
}
