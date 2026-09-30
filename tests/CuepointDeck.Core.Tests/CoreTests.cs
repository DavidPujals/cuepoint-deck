using System.Text.Json;
using CuepointDeck.Core;
using CuepointDeck.Core.Library;
using CuepointDeck.Core.Matching;
using CuepointDeck.Core.Resolume;
using CuepointDeck.Core.Settings;
using CuepointDeck.Core.Storage;
using Xunit;

namespace CuepointDeck.Core.Tests;

public static class TestData
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", name);
    public static string Read(string name) => File.ReadAllText(Path(name));
    public static JsonElement Json(string name) => JsonDocument.Parse(Read(name)).RootElement.Clone();
}

/// <summary>A throwaway folder per test.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cuepointdeck-tests", Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string relative) => System.IO.Path.Combine(Path, relative);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class SafeFileTests
{
    [Fact]
    public void Write_creates_file_then_keeps_previous_as_bak()
    {
        using var dir = new TempDir();
        var path = dir.File("a.json");
        SafeFile.WriteAllText(path, "one");
        Assert.Equal("one", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bak"));

        SafeFile.WriteAllText(path, "two");
        Assert.Equal("two", File.ReadAllText(path));
        Assert.Equal("one", File.ReadAllText(path + ".bak"));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp-*"));
    }

    [Fact]
    public void Stamp_changes_when_content_changes()
    {
        using var dir = new TempDir();
        var path = dir.File("a.json");
        var s1 = SafeFile.WriteAllText(path, "one");
        Thread.Sleep(20);
        File.WriteAllText(path, "one-edited-elsewhere");
        var s2 = FileStamp.Of(path);
        Assert.NotEqual(s1, s2);
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Missing_file_gives_defaults_and_roundtrip_preserves_values()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        var s = store.Load();
        Assert.Equal("127.0.0.1", s.ResolumeHost);
        Assert.Equal(8080, s.ResolumePort);
        Assert.Equal(AppRole.Controller, s.Role);
        Assert.Equal(TriggerMode.Queue, s.DefaultTrigger);
        Assert.Equal(40, s.LatencyOffsetMs);

        s.ResolumeHost = "10.131.0.5";
        s.LatencyOffsetMs = 90;
        s.ResolumeHost = "127.0.0.1";
        s.LatencyOffsetMs = 25;
        s.PathMappings.Add(new PathMapping { From = @"D:\Media", To = @"\\CITY-VISUALS\Media" });
        s.Role = AppRole.Follow;
        store.Save(s);

        var again = store.Load();
        Assert.Equal(AppRole.Follow, again.Role);
        Assert.Equal(25, again.LatencyOffsetMs);
        again.ResolumeHost = "10.131.0.5";
        Assert.Equal(90, again.LatencyOffsetMs);
        again.ResolumeHost = "unknown-host";
        Assert.Equal(40, again.LatencyOffsetMs);
        Assert.Single(again.PathMappings);
        Assert.Contains("\"role\": \"follow\"", File.ReadAllText(store.Path));
    }

    [Fact]
    public void Corrupt_file_is_set_aside_and_defaults_used()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{ this is not json");
        var s = new SettingsStore(path).Load();
        Assert.Equal(8080, s.ResolumePort);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(dir.Path, "settings.json.corrupt-*"));
    }
}

public class SongLibraryTests
{
    private static Song MakeSong(string title, string file) => new()
    {
        Title = title,
        Clip = new ClipRef { FilePath = file, FileName = System.IO.Path.GetFileName(file), ClipName = title },
        DurationMs = 195240,
        Segments =
        {
            new Segment { Name = "Chorus", StartMs = 60000 },
            new Segment { Name = "Verse 1", StartMs = 0, Lyric = "First line" },
        },
    };

    [Fact]
    public void Save_sorts_segments_and_load_skips_broken_files()
    {
        using var dir = new TempDir();
        var lib = new SongLibrary(dir.Path);
        Assert.True(lib.EnsureFolders());
        var song = MakeSong("Shout For Joy", @"D:\Media\Songs\Shout For Joy.mov");
        Assert.True(lib.SaveSong(song).Ok);
        Assert.Equal("Verse 1", song.Segments[0].Name);

        File.WriteAllText(System.IO.Path.Combine(lib.SongsDir, "broken.json"), "{ \"id\": ");
        File.WriteAllText(System.IO.Path.Combine(lib.SongsDir, "notes.txt"), "ignored");
        File.WriteAllText(System.IO.Path.Combine(lib.SongsDir, "old.json.bak"), "{}");

        var lib2 = new SongLibrary(dir.Path);
        lib2.Rescan();
        Assert.Single(lib2.Songs);
        Assert.Equal("Shout For Joy", lib2.Songs[0].Title);
        Assert.Equal(2, lib2.Songs[0].Segments.Count);
        var warning = Assert.Single(lib2.Warnings);
        Assert.EndsWith("broken.json", warning.File);
        Assert.True(lib2.IsReachable);
    }

    [Fact]
    public void Save_detects_edits_from_another_pc()
    {
        using var dir = new TempDir();
        var lib = new SongLibrary(dir.Path);
        var song = MakeSong("A", @"D:\a.mov");
        Assert.True(lib.SaveSong(song).Ok);

        // Another PC edits the file.
        Thread.Sleep(20);
        var onDisk = Json.Deserialize<Song>(File.ReadAllText(lib.SongPath(song.Id)))!;
        onDisk.Title = "A (edited elsewhere)";
        File.WriteAllText(lib.SongPath(song.Id), Json.Serialize(onDisk) + " ");

        Assert.True(lib.HasChangedOnDisk(song.Id));
        song.Title = "A (edited here)";
        var result = lib.SaveSong(song);
        Assert.Equal(SaveStatus.Conflict, result.Status);
        Assert.Contains("edited elsewhere", File.ReadAllText(lib.SongPath(song.Id)));

        var reloaded = lib.ReloadSong(song.Id)!;
        Assert.Equal("A (edited elsewhere)", reloaded.Title);
        Assert.False(lib.HasChangedOnDisk(song.Id));

        Assert.True(lib.SaveSong(song, overwrite: true).Ok);
        Assert.Contains("edited here", File.ReadAllText(lib.SongPath(song.Id)));
        Assert.True(File.Exists(lib.SongPath(song.Id) + ".bak"));
    }

    [Fact]
    public void Setlists_save_rename_duplicate_delete()
    {
        using var dir = new TempDir();
        var lib = new SongLibrary(dir.Path);
        var s = new Setlist { Name = "2026-10-04 North AM", SongIds = { "a", "b" } };
        Assert.True(lib.SaveSetlist(s).Ok);
        Assert.True(File.Exists(System.IO.Path.Combine(lib.SetlistsDir, "2026-10-04 North AM.json")));

        Assert.True(lib.DuplicateSetlist("2026-10-04 North AM", "2026-10-04 North PM").Ok);
        Assert.Equal(2, lib.Setlists.Count);
        Assert.True(lib.RenameSetlist("2026-10-04 North PM", "City PM").Ok);
        Assert.NotNull(lib.GetSetlist("City PM"));
        Assert.Null(lib.GetSetlist("2026-10-04 North PM"));
        Assert.True(lib.DeleteSetlist("City PM"));
        Assert.Single(lib.Setlists);

        var lib2 = new SongLibrary(dir.Path);
        lib2.Rescan();
        Assert.Single(lib2.Setlists);
        Assert.Equal(new[] { "a", "b" }, lib2.Setlists[0].SongIds);
    }

    [Fact]
    public void Unreachable_root_is_a_warning_not_a_crash()
    {
        var lib = new SongLibrary(@"\\definitely-not-a-host-1234\share\lib");
        lib.Rescan();
        Assert.False(lib.IsReachable);
        Assert.Empty(lib.Songs);
        Assert.NotEmpty(lib.Warnings);
    }

    [Fact]
    public void Segment_end_and_lookup()
    {
        var song = MakeSong("A", @"D:\a.mov");
        song.SortSegments();
        Assert.Equal(60000, song.SegmentEndMs(0));
        Assert.Equal(195240, song.SegmentEndMs(1));
        Assert.Equal(-1, song.SegmentIndexAt(-1));
        Assert.Equal(0, song.SegmentIndexAt(0));
        Assert.Equal(0, song.SegmentIndexAt(59999));
        Assert.Equal(1, song.SegmentIndexAt(60000));
        Assert.Equal(1, song.SegmentIndexAt(999999));
    }
}

public class PathMapperTests
{
    [Fact]
    public void Maps_prefixes_both_ways_at_folder_boundaries()
    {
        var m = new PathMapper(new[]
        {
            new PathMapping { From = @"D:\Media", To = @"\\CITY-VISUALS\Media" },
            new PathMapping { From = @"D:\Media\Songs", To = @"S:\Songs" },
        });
        Assert.Equal(@"S:\Songs\x.mov", m.MapForward(@"D:\Media\Songs\x.mov"));
        Assert.Equal(@"\\CITY-VISUALS\Media\Loops\y.mov", m.MapForward(@"d:/media/Loops/y.mov"));
        Assert.Equal(@"D:\MediaOld\z.mov", m.MapForward(@"D:\MediaOld\z.mov"));
        Assert.Equal(@"D:\Media\Songs\x.mov", m.MapBackward(@"S:\Songs\x.mov"));
        // Forward maps it, nothing maps it backward: the path itself plus one mapped form.
        Assert.Equal(2, m.Variants(@"D:\Media\Songs\x.mov").Count());
        Assert.Contains(@"D:\Media\Songs\x.mov", m.Variants(@"S:\Songs\x.mov"));
        Assert.True(new PathMapper(null).IsEmpty);
    }
}

public class CompositionTests
{
    [Fact]
    public void Parses_recorded_arena_composition()
    {
        var comp = Composition.Parse(TestData.Json("composition-with-clip.json"));
        Assert.Equal("Test NovaCon", comp.Name);
        Assert.Equal(3, comp.Layers.Count);
        Assert.Equal(27, comp.Clips.Count);

        var clip = comp.Clips.First(c => !c.IsEmpty);
        Assert.Equal(1, clip.Layer);
        Assert.Equal(1, clip.Column);
        Assert.Equal(1769995378214, clip.ClipId);
        Assert.StartsWith(@"C:\Users\David\Downloads\Copy of SHOUT FOR JOY", clip.FilePath);
        Assert.Equal(195240, clip.DurationMs, 1);
        Assert.Equal(25, clip.Fps, 3);
        Assert.Equal(1790598829047, clip.PositionParamId);
        Assert.Equal(1790598826956, clip.ConnectedParamId);
        Assert.Equal(1790598829077, clip.SpeedParamId);
        Assert.Equal(1790598829073, clip.PlayDirectionParamId);
        Assert.Equal("Restart", clip.RetriggerMode);
        Assert.Equal("Timeline", clip.TransportType);
        Assert.False(clip.IsConnected);
        Assert.Equal(">", clip.PlayDirection);
        Assert.Same(clip, comp.ByPositionParam(1790598829047));
        Assert.Same(clip, comp.ByConnectedParam(1790598826956));

        // Position range is 0..duration ms on Arena 7.24, so raw == ms.
        Assert.Equal(60000, clip.RawToMs(60000), 1);
        Assert.Equal(60000, clip.MsToRaw(60000), 1);
        Assert.Equal(40, clip.FrameMs, 3);
    }

    [Fact]
    public void Position_conversion_scales_through_the_parameter_range()
    {
        var normalised = new ClipInfo { PosMin = 0, PosMax = 1, DurationMs = 120000 };
        Assert.Equal(60000, normalised.RawToMs(0.5), 3);
        Assert.Equal(0.25, normalised.MsToRaw(30000), 6);
        var seconds = new ClipInfo { PosMin = 0, PosMax = 120, DurationMs = 120000 };
        Assert.Equal(90000, seconds.RawToMs(90), 3);
        var unknown = new ClipInfo { PosMin = 0, PosMax = 0, DurationMs = 0 };
        Assert.Equal(1234, unknown.RawToMs(1234), 3);
    }
}

public class ParameterMessageTests
{
    private static ParameterMessage Parse(string json) => ParameterMessage.Parse(JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public void Position_update_has_fields_at_root()
    {
        var m = Parse("""{"id":1790598829047,"valuetype":"ParamRange","min":0.0,"max":195239.97916666666,"in":0.0,"out":195239.97916666666,"value":127714.99727659521,"path":"/composition/layers/1/clips/1/transport/position","type":"parameter_update"}""");
        Assert.Equal("parameter_update", m.Type);
        Assert.Equal(1790598829047, m.Id);
        Assert.Equal(127714.99727659521, m.Number);
        Assert.Equal(0.0, m.Min);
        Assert.Equal(195239.97916666666, m.Max);
    }

    [Fact]
    public void Connected_update_and_error_and_composition()
    {
        var c = Parse("""{"value":"Connected","valuetype":"ParamState","options":["Empty","Disconnected","Previewing","Connected","Connected & previewing"],"index":3,"id":1790598826956,"path":"/composition/layers/1/clips/1/connect","type":"parameter_update"}""");
        Assert.Equal("Connected", c.ValueString);
        Assert.Null(c.Number);
        Assert.Equal(1790598826956, c.Id);

        var e = Parse("""{"path":"/composition/layers/1/clips/1/transport/position","error":"Invalid parameter path"}""");
        Assert.Equal("error", e.Type);
        Assert.Equal("Invalid parameter path", e.Error);

        var comp = Parse("""{"name":{"value":"x"},"layers":[]}""");
        Assert.True(comp.IsComposition);

        var nested = Parse("""{"type":"parameter_update","path":"/p","value":{"id":5,"value":3.5}}""");
        Assert.Equal(5, nested.Id);
        Assert.Equal(3.5, nested.Number);
    }
}

public class ClipMatcherTests
{
    private static Composition Comp() => Composition.Parse(TestData.Json("composition-with-clip.json"));
    private const string ArenaPath = @"C:\Users\David\Downloads\Copy of SHOUT FOR JOY 169 V2_DXV Normal Quality No Alpha.mov";

    private static Song SongFor(string path) => new()
    {
        Id = "song-" + Guid.NewGuid().ToString("N")[..6],
        Title = "Shout For Joy",
        Clip = new ClipRef { FilePath = path, FileName = System.IO.Path.GetFileName(path) },
    };

    [Fact]
    public void Duplicate_clips_prefer_song_layer_and_warn()
    {
        // The file is on L1 C1 and L2 C3. With the song layer at 2 the L2 clip is the song and the other copy is
        // just another layer: no conflict.
        var song = SongFor(ArenaPath);
        var table = ClipMatcher.Build(Comp(), new[] { song }, new PathMapper(null), songLayer: 2);
        var m = table.For(song)!;
        Assert.True(m.IsAvailable);
        Assert.Equal(MatchKind.Path, m.Kind);
        Assert.False(m.Ambiguous);
        Assert.Null(m.Warning);
        Assert.Equal(2, m.Clip!.Layer);
        Assert.Equal(3, m.Clip.Column);
        Assert.Equal(2, m.AllClips.Count);

        var other = ClipMatcher.Build(Comp(), new[] { song }, new PathMapper(null), songLayer: 1).For(song)!;
        Assert.Equal(1, other.Clip!.Layer);

        // Both duplicates map back to the song so the app follows whichever one Resolume connects.
        Assert.Same(song, table.SongForClip(1769995378214));
        Assert.Same(song, table.SongForClip(999000111));
        Assert.Null(table.SongForClip(42));
    }

    private static ClipInfo Clip(long id, int layer, int column, string path) =>
        new() { ClipId = id, Layer = layer, Column = column, Name = System.IO.Path.GetFileNameWithoutExtension(path), FilePath = path, DurationMs = 1000, Fps = 25, PosMin = 0, PosMax = 1000 };

    [Fact]
    public void Same_file_on_other_layers_of_the_column_is_not_a_conflict_but_two_song_layer_columns_are()
    {
        var song = SongFor(ArenaPath);
        var stacked = new Composition { Clips = new[] { Clip(1, 1, 5, ArenaPath), Clip(2, 2, 5, ArenaPath), Clip(3, 4, 5, ArenaPath) } };
        var m = ClipMatcher.Build(stacked, new[] { song }, new PathMapper(null), songLayer: 4).For(song)!;
        Assert.False(m.Ambiguous);
        Assert.Equal(4, m.Clip!.Layer);
        Assert.Same(song, ClipMatcher.Build(stacked, new[] { song }, new PathMapper(null), songLayer: 4).SongForClip(1));

        var twoColumns = new Composition { Clips = new[] { Clip(1, 4, 2, ArenaPath), Clip(2, 4, 9, ArenaPath), Clip(3, 1, 2, ArenaPath) } };
        var t = ClipMatcher.Build(twoColumns, new[] { song }, new PathMapper(null), songLayer: 4).For(song)!;
        Assert.True(t.Ambiguous);
        Assert.Equal(2, t.Clip!.Column);
        Assert.Contains("C2, C9", t.Warning);

        // Not on the song layer at all: other layers stand in, and several columns is the conflict.
        var elsewhere = new Composition { Clips = new[] { Clip(1, 1, 2, ArenaPath), Clip(2, 2, 2, ArenaPath), Clip(3, 1, 7, ArenaPath) } };
        var e = ClipMatcher.Build(elsewhere, new[] { song }, new PathMapper(null), songLayer: 4).For(song)!;
        Assert.True(e.Ambiguous);
        Assert.Equal(2, e.Candidates.Count);
        Assert.Equal(1, e.Clip!.Layer);
    }

    [Fact]
    public void Mapped_path_and_file_name_fallback_and_unavailable()
    {
        var mapped = SongFor(@"\\NORTH-VISUALS\Downloads\Copy of SHOUT FOR JOY 169 V2_DXV Normal Quality No Alpha.mov");
        var byName = SongFor(@"E:\Somewhere\Else\Copy of SHOUT FOR JOY 169 V2_DXV Normal Quality No Alpha.mov");
        var missing = SongFor(@"D:\Media\Songs\Not In Deck.mov");
        var mapper = new PathMapper(new[] { new PathMapping { From = @"C:\Users\David\Downloads", To = @"\\NORTH-VISUALS\Downloads" } });

        var table = ClipMatcher.Build(Comp(), new[] { mapped, byName, missing }, mapper, 1);
        Assert.Equal(MatchKind.MappedPath, table.For(mapped)!.Kind);
        Assert.Equal(MatchKind.FileName, table.For(byName)!.Kind);
        var none = table.For(missing)!;
        Assert.False(none.IsAvailable);
        Assert.Equal(MatchKind.None, none.Kind);
        Assert.Equal(3, table.Matches.Count);
    }

    [Fact]
    public void No_composition_means_nothing_available()
    {
        var song = SongFor(ArenaPath);
        var table = ClipMatcher.Build(null, new[] { song }, new PathMapper(null), 1);
        Assert.False(table.For(song)!.IsAvailable);
    }
}

public class CompositionColumnTests
{
    [Fact]
    public void Columns_are_parsed_with_placeholder_names_blanked()
    {
        var comp = Composition.Parse(TestData.Json("composition-with-clip.json"));
        Assert.Equal(9, comp.Columns.Count);
        Assert.Equal(1, comp.Columns[0].Index);
        Assert.Equal("", comp.Columns[0].Name);
        Assert.NotNull(comp.Column(9));
        Assert.Null(comp.Column(10));
    }
}
