using CuepointDeck.Core.Library;
using CuepointDeck.Core.Resolume;
using Xunit;

namespace CuepointDeck.Core.Tests;

public class SongTransferTests
{
    private static Song Campus1Song() => new()
    {
        Id = "song-way-maker",
        Title = "Way Maker",
        Artist = "Sinach",
        DurationMs = 300_000,
        Fps = 25,
        Clip = new ClipRef { FilePath = @"D:\Media\Campus1\WayMaker_2688x512.mov", FileName = "WayMaker_2688x512.mov", ClipName = "WayMaker_2688x512" },
        Segments =
        {
            new Segment { Id = "s1", Name = "Intro", StartMs = 0, Color = "#8E8E93", Thumb = "thumbs/song-way-maker/s1.jpg", ThumbAtMs = 0 },
            new Segment { Id = "s2", Name = "Verse 1", StartMs = 16_000, Lyric = "You are here", Color = "#4A90D9" },
            new Segment { Id = "s3", Name = "Chorus", StartMs = 48_000, EndMs = 70_000, Lyric = "Way maker", Color = "#F5A623" },
        },
    };

    private static ClipInfo Clip(long id, int column, string name, string path, double durationMs = 300_040, double fps = 30)
        => new() { ClipId = id, Layer = 4, Column = column, Name = name, FilePath = path, DurationMs = durationMs, Fps = fps, PosMin = 0, PosMax = durationMs };

    [Fact]
    public void Export_keeps_timings_and_labels_and_leaves_out_paths_and_thumbnails()
    {
        var file = SongTransfer.Create(new[] { Campus1Song() }, _ => "Way Maker", "CAMPUS-1");
        var json = SongTransfer.Serialize(file);

        Assert.DoesNotContain(@"D:\\Media", json);
        Assert.DoesNotContain("thumbs/", json);
        var back = SongTransfer.Parse(json);
        Assert.Equal("CAMPUS-1", back.ExportedFrom);
        var s = Assert.Single(back.Songs);
        Assert.Equal("Way Maker", s.Title);
        Assert.Equal("WayMaker_2688x512.mov", s.FileName);
        Assert.Equal(new long[] { 0, 16_000, 48_000 }, s.Segments.Select(x => x.StartMs));
        Assert.Equal(70_000, s.Segments[2].EndMs);
        Assert.Equal("You are here", s.Segments[1].Lyric);
    }

    [Fact]
    public void Parse_rejects_other_files_and_newer_versions_with_readable_messages()
    {
        Assert.Contains("isn't a Cuepoint Deck songs file", Assert.Throws<FormatException>(() => SongTransfer.Parse("{\"songs\":[]}")).Message);
        Assert.Contains("isn't a Cuepoint Deck songs file", Assert.Throws<FormatException>(() => SongTransfer.Parse("not json")).Message);
        var newer = SongTransfer.Serialize(new TransferFile { Format = SongTransfer.FormatName, Version = SongTransfer.CurrentVersion + 1 });
        Assert.Contains("newer Cuepoint Deck", Assert.Throws<FormatException>(() => SongTransfer.Parse(newer)).Message);
    }

    [Fact]
    public void Existing_songs_are_found_by_id_then_by_title_ignoring_case_and_punctuation()
    {
        var incoming = SongTransfer.Create(new[] { Campus1Song() }).Songs[0];
        var sameId = new Song { Id = "song-way-maker", Title = "Renamed Here" };
        var sameTitle = new Song { Id = "local", Title = "way maker!" };
        var other = new Song { Id = "x", Title = "Goodness of God" };

        Assert.Same(sameId, SongTransfer.FindExisting(incoming, new[] { other, sameTitle, sameId }));
        Assert.Same(sameTitle, SongTransfer.FindExisting(incoming, new[] { other, sameTitle }));
        Assert.Null(SongTransfer.FindExisting(incoming, new[] { other }));
        Assert.Equal(SongTransfer.NormalizeTitle("Way Maker (Live)"), SongTransfer.NormalizeTitle("way  maker - live"));
    }

    [Fact]
    public void Column_suggestion_prefers_the_current_link_then_the_column_name_then_the_file_name()
    {
        var incoming = SongTransfer.Create(new[] { Campus1Song() }).Songs[0];
        var columns = new Dictionary<int, string> { [3] = "Goodness of God", [7] = "WAY MAKER", [9] = "" };
        var clips = new List<ClipInfo>
        {
            Clip(1, 3, "goodness", @"E:\C2\Goodness.mov"),
            Clip(2, 7, "wm_1080", @"E:\C2\WayMaker_1920x1080.mov"),
            Clip(3, 9, "x", @"E:\C2\WayMaker_2688x512.mov"),
        };
        string Name(int c) => columns.TryGetValue(c, out var n) ? n : "";

        Assert.Equal(7, SongTransfer.SuggestColumn(incoming, null, null, clips, Name)!.Column);
        Assert.Equal(3, SongTransfer.SuggestColumn(incoming, null, clips[0], clips, Name)!.Column);   // already linked here: keep it
        var noNames = (Func<int, string>)(_ => "");
        Assert.Equal(9, SongTransfer.SuggestColumn(incoming, null, null, clips, noNames)!.Column);    // same file name
        incoming.FileName = "";
        Assert.Null(SongTransfer.SuggestColumn(incoming, null, null, clips, noNames));
    }

    [Fact]
    public void A_new_song_takes_the_incoming_id_and_the_chosen_columns_media_length_and_frame_rate()
    {
        var incoming = SongTransfer.Create(new[] { Campus1Song() }).Songs[0];
        var clip = Clip(2, 7, "wm_1080", @"E:\C2\WayMaker_1920x1080.mov", durationMs: 300_040, fps: 30);

        var song = SongTransfer.Apply(incoming, null, clip);

        Assert.Equal("song-way-maker", song.Id);
        Assert.Equal("Way Maker", song.Title);
        Assert.Equal(@"E:\C2\WayMaker_1920x1080.mov", song.Clip.FilePath);
        Assert.Equal("WayMaker_1920x1080.mov", song.Clip.FileName);
        Assert.Equal(300_040, song.DurationMs);
        Assert.Equal(30, song.Fps);
        Assert.Equal(new long[] { 0, 16_000, 48_000 }, song.Segments.Select(s => s.StartMs));
        Assert.All(song.Segments, s => Assert.Null(s.Thumb));
    }

    [Fact]
    public void Replacing_keeps_the_local_id_title_and_link_and_only_still_valid_thumbnails()
    {
        var incoming = SongTransfer.Create(new[] { Campus1Song() }).Songs[0];
        incoming.Segments[1].StartMs = 17_000;   // the verse moved at campus 1
        var existing = new Song
        {
            Id = "campus2-id",
            Title = "Way Maker",
            DurationMs = 300_040,
            Fps = 30,
            Clip = new ClipRef { FilePath = @"E:\C2\WayMaker_1920x1080.mov", FileName = "WayMaker_1920x1080.mov" },
            Segments =
            {
                new Segment { Id = "s1", Name = "Old Intro", StartMs = 0, Thumb = "thumbs/campus2-id/s1.jpg", ThumbAtMs = 0 },
                new Segment { Id = "s2", Name = "Verse", StartMs = 16_000, Thumb = "thumbs/campus2-id/s2.jpg", ThumbAtMs = 16_000 },
            },
        };

        var song = SongTransfer.Apply(incoming, existing, null);

        Assert.Equal("campus2-id", song.Id);
        Assert.Equal(@"E:\C2\WayMaker_1920x1080.mov", song.Clip.FilePath);
        Assert.Equal(30, song.Fps);
        Assert.Equal(new[] { "Intro", "Verse 1", "Chorus" }, song.Segments.Select(s => s.Name));
        Assert.Equal("thumbs/campus2-id/s1.jpg", song.Segments[0].Thumb);   // same start, same media: still right
        Assert.Null(song.Segments[1].Thumb);                                // moved: re-render
        Assert.Null(song.Segments[2].Thumb);                                // new
        Assert.Equal("Old Intro", existing.Segments[0].Name);               // the library copy is untouched

        // Linking it to a different column's media drops every thumbnail.
        var relinked = SongTransfer.Apply(incoming, existing, Clip(5, 2, "other", @"E:\C2\WayMaker_alt.mov"));
        Assert.All(relinked.Segments, s => Assert.Null(s.Thumb));
        Assert.Equal(@"E:\C2\WayMaker_alt.mov", relinked.Clip.FilePath);
    }
    [Fact]
    public void A_file_from_one_campus_library_imports_into_another_and_survives_a_rescan()
    {
        var root = Path.Combine(Path.GetTempPath(), "cuedeck-transfer-" + Guid.NewGuid().ToString("N"));
        try
        {
            var campus1 = new SongLibrary(Path.Combine(root, "c1"));
            campus1.Rescan();
            Assert.True(campus1.SaveSong(Campus1Song()).Ok);
            var json = SongTransfer.Serialize(SongTransfer.Create(campus1.Songs, _ => "Way Maker", "CAMPUS-1"));

            var campus2 = new SongLibrary(Path.Combine(root, "c2"));
            campus2.Rescan();
            Assert.True(campus2.SaveSong(new Song { Id = "c2-wm", Title = "WAY MAKER", Clip = new ClipRef { FilePath = @"E:\C2\WM.mov", FileName = "WM.mov" } }).Ok);

            var file = SongTransfer.Parse(json);
            var incoming = Assert.Single(file.Songs);
            var existing = SongTransfer.FindExisting(incoming, campus2.Songs);
            Assert.Equal("c2-wm", existing!.Id);
            var clip = Clip(7, 12, "wm", @"E:\C2\WM_new.mov");
            Assert.True(campus2.SaveSong(SongTransfer.Apply(incoming, existing, clip), overwrite: true).Ok);

            var reread = new SongLibrary(Path.Combine(root, "c2"));
            reread.Rescan();
            var song = Assert.Single(reread.Songs);
            Assert.Equal("WAY MAKER", song.Title);
            Assert.Equal(@"E:\C2\WM_new.mov", song.Clip.FilePath);
            Assert.Equal(new[] { "Intro", "Verse 1", "Chorus" }, song.Segments.Select(x => x.Name));
            Assert.Equal(new long[] { 0, 16_000, 48_000 }, song.Segments.Select(x => x.StartMs));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
