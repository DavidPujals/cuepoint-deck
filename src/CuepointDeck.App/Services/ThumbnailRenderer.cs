using System.IO;
using CuepointDeck.Core;
using CuepointDeck.Core.Library;
using CuepointDeck.Core.Logging;
using CuepointDeck.Core.Matching;

namespace CuepointDeck.App.Services;

/// <summary>
/// Renders missing or out-of-date segment thumbnails for songs outside Edit mode (after an import), from this PC's own
/// copy of each song's video. One song at a time at low priority; a song whose media can't be opened here keeps its
/// colour placeholders until it is edited on a PC that can.
/// </summary>
public static class ThumbnailRenderer
{
    public sealed record Result(int Songs, int Rendered, int SongsWithoutMedia, bool FfmpegMissing);

    public static async Task<Result> RenderAsync(AppServices services, IReadOnlyCollection<string> songIds, CancellationToken ct = default)
    {
        if (!services.Ffmpeg.IsAvailable) return new Result(songIds.Count, 0, 0, FfmpegMissing: true);
        var lib = services.Library;
        int rendered = 0, noMedia = 0;
        foreach (var id in songIds)
        {
            if (ct.IsCancellationRequested) break;
            var stored = lib.GetSong(id);
            if (stored is null) continue;
            var song = Json.Deserialize<Song>(Json.Serialize(stored))!;
            var todo = song.Segments.Where(s => s.Thumb is null || s.ThumbAtMs != s.StartMs).ToList();
            if (todo.Count == 0) continue;
            var file = LocalMediaFile(services, song);
            if (file is null) { noMedia++; continue; }

            var done = 0;
            foreach (var seg in todo)
            {
                var rel = $"thumbs/{song.Id}/{seg.Id}.jpg";
                var abs = lib.ResolveLibraryPath(rel);
                var r = await services.Ffmpeg.ThumbnailAsync(file, seg.StartMs, abs, ct);
                if (!r.Ok) { Log.Warn($"Thumbnail for \"{song.Title}\" / {seg.Name} failed: {r.Error}"); continue; }
                seg.Thumb = rel;
                seg.ThumbAtMs = seg.StartMs;
                ThumbCache.Forget(abs);
                done++;
            }
            if (done == 0) continue;
            var saved = lib.SaveSong(song);
            if (saved.Ok) rendered += done;
            else Log.Warn($"Thumbnails for \"{song.Title}\" rendered but not saved: {saved.Error}");
        }
        Log.Info($"Thumbnails after import: {rendered} rendered across {songIds.Count} song(s); {noMedia} song(s) without readable media here");
        return new Result(songIds.Count, rendered, noMedia, FfmpegMissing: false);
    }

    /// <summary>The song's media as this PC can open it: the matched clip's path or the song's own, through any path mappings.</summary>
    public static string? LocalMediaFile(AppServices services, Song song)
    {
        var mapper = new PathMapper(services.Settings.PathMappings);
        var candidates = new List<string>();
        var clip = services.Matches.For(song.Id)?.Clip;
        if (clip is not null && !string.IsNullOrEmpty(clip.FilePath)) candidates.AddRange(mapper.Variants(clip.FilePath));
        if (!string.IsNullOrEmpty(song.Clip.FilePath)) candidates.AddRange(mapper.Variants(song.Clip.FilePath));
        return candidates.FirstOrDefault(File.Exists);
    }
}
