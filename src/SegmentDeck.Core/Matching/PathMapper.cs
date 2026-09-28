using SegmentDeck.Core.Settings;

namespace SegmentDeck.Core.Matching;

public static class PathNorm
{
    /// <summary>Case-insensitive, slash-insensitive form for comparing Windows paths.</summary>
    public static string Key(string path) => path.Trim().Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();

    public static string FileName(string path)
    {
        var p = path.Trim().Replace('/', '\\');
        var i = p.LastIndexOf('\\');
        return i >= 0 ? p[(i + 1)..] : p;
    }
}

/// <summary>Applies the "Path mappings" setting: pairs like <c>D:\Media</c> → <c>\\CITY-VISUALS\Media</c>.
/// Forward maps a path the Resolume PC reports to one this PC can open; backward does the reverse.
/// Longest matching prefix wins; a prefix only matches at a folder boundary.</summary>
public sealed class PathMapper
{
    private readonly List<(string From, string To)> _pairs;

    public PathMapper(IEnumerable<PathMapping>? mappings)
    {
        _pairs = (mappings ?? Array.Empty<PathMapping>())
            .Where(m => !string.IsNullOrWhiteSpace(m.From) && !string.IsNullOrWhiteSpace(m.To))
            .Select(m => (Trim(m.From), Trim(m.To)))
            .OrderByDescending(p => p.Item1.Length)
            .ToList();
    }

    public bool IsEmpty => _pairs.Count == 0;

    public string MapForward(string path) => Apply(path, _pairs);
    public string MapBackward(string path) => Apply(path, _pairs.Select(p => (p.To, p.From)).OrderByDescending(p => p.To.Length).ToList());

    /// <summary>The path itself plus every mapped form of it, distinct by <see cref="PathNorm.Key"/>.</summary>
    public IEnumerable<string> Variants(string path)
    {
        var seen = new HashSet<string>();
        foreach (var candidate in new[] { path, MapForward(path), MapBackward(path) })
            if (!string.IsNullOrEmpty(candidate) && seen.Add(PathNorm.Key(candidate))) yield return candidate;
    }

    private static string Trim(string p) => p.Trim().Replace('/', '\\').TrimEnd('\\');

    private static string Apply(string path, List<(string From, string To)> pairs)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var normalised = path.Replace('/', '\\');
        foreach (var (from, to) in pairs)
        {
            if (normalised.Length < from.Length) continue;
            if (!normalised.StartsWith(from, StringComparison.OrdinalIgnoreCase)) continue;
            if (normalised.Length > from.Length && normalised[from.Length] != '\\') continue;
            return to + normalised[from.Length..];
        }
        return path;
    }
}
