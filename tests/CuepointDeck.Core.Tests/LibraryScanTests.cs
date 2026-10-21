using CuepointDeck.Core.Library;
using Xunit;

namespace CuepointDeck.Core.Tests;

public class LibraryScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cuedeck-scan-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static Song Song(string id, string title) => new() { Id = id, Title = title, Segments = { new Segment { Name = "Intro", StartMs = 0 } } };

    [Fact]
    public void A_rescan_with_nothing_changed_keeps_the_loaded_songs_and_raises_nothing()
    {
        var lib = new SongLibrary(_root);
        lib.Rescan();
        Assert.True(lib.SaveSong(Song("a", "Alpha")).Ok);
        Assert.True(lib.SaveSong(Song("b", "Beta")).Ok);
        lib.SaveSetlist(new Setlist { Name = "Sunday", SongIds = { "a", "b" } });
        lib.Rescan();
        var before = lib.Songs.ToList();
        var setlist = lib.GetSetlist("Sunday");

        int changes = 0;
        lib.Changed += () => changes++;
        lib.Rescan();

        Assert.Equal(0, changes);
        Assert.Equal(before, lib.Songs, ReferenceEqualityComparer.Instance);
        Assert.Same(setlist, lib.GetSetlist("Sunday"));
    }

    [Fact]
    public void A_rescan_picks_up_files_changed_added_and_removed_by_another_pc()
    {
        var lib = new SongLibrary(_root);
        lib.Rescan();
        lib.SaveSong(Song("a", "Alpha"));
        lib.SaveSong(Song("b", "Beta"));
        var beta = lib.GetSong("b");

        // Another PC edits Alpha, adds Gamma and deletes Beta.
        var other = new SongLibrary(_root);
        other.Rescan();
        var alpha = Json.Deserialize<Song>(Json.Serialize(other.GetSong("a")!))!;
        alpha.Title = "Alpha (Live)";
        alpha.Segments.Add(new Segment { Name = "Chorus", StartMs = 30_000 });
        Thread.Sleep(20);   // a distinct write time
        other.SaveSong(alpha, overwrite: true);
        other.SaveSong(Song("c", "Gamma"));
        other.DeleteSong("b");

        int changes = 0;
        lib.Changed += () => changes++;
        lib.Rescan();

        Assert.Equal(1, changes);
        Assert.Equal(new[] { "Alpha (Live)", "Gamma" }, lib.Songs.Select(s => s.Title));
        Assert.Equal(2, lib.GetSong("a")!.Segments.Count);
        Assert.Null(lib.GetSong("b"));
        Assert.NotNull(beta);
    }

    [Fact]
    public void Saving_keeps_a_library_copy_that_later_edits_to_the_saved_object_do_not_touch()
    {
        var lib = new SongLibrary(_root);
        lib.Rescan();
        var song = Song("a", "Alpha");
        lib.SaveSong(song);
        song.Title = "Unsaved title";
        song.Segments[0].StartMs = 5_000;

        Assert.Equal("Alpha", lib.GetSong("a")!.Title);
        Assert.Equal(0, lib.GetSong("a")!.Segments[0].StartMs);
        Assert.False(lib.HasChangedOnDisk("a"));
    }
}
