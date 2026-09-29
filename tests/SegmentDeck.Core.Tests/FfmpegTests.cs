using SegmentDeck.Core.Media;
using Xunit;
using Xunit.Abstractions;

namespace SegmentDeck.Core.Tests;

/// <summary>Runs only when SEGMENTDECK_FFMPEG points at ffmpeg.exe and SEGMENTDECK_TEST_VIDEO at a video file.</summary>
public class FfmpegTests
{
    private readonly ITestOutputHelper _out;
    public FfmpegTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Thumbnail_filmstrip_and_duration()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("SEGMENTDECK_FFMPEG");
        var video = Environment.GetEnvironmentVariable("SEGMENTDECK_TEST_VIDEO");
        if (string.IsNullOrEmpty(ffmpeg) || string.IsNullOrEmpty(video) || !File.Exists(video)) { _out.WriteLine("skipped: set SEGMENTDECK_FFMPEG and SEGMENTDECK_TEST_VIDEO"); return; }

        var svc = new FfmpegService();
        Assert.True(await svc.ConfigureAsync(ffmpeg), svc.StatusText);
        Assert.False(await svc.ConfigureAsync(@"C:\nowhere\ffmpeg.exe"));
        Assert.True(await svc.ConfigureAsync(ffmpeg));

        using var dir = new TempDir();
        var jpg = dir.File("thumb.jpg");
        var r = await svc.ThumbnailAsync(video, 60_000, jpg);
        Assert.True(r.Ok, r.Error);
        Assert.True(new FileInfo(jpg).Length > 4000);
        _out.WriteLine($"thumbnail {new FileInfo(jpg).Length} bytes");

        var missing = await svc.ThumbnailAsync(dir.File("nope.mov"), 0, dir.File("x.jpg"));
        Assert.False(missing.Ok);

        var duration = await svc.DurationMsAsync(video);
        Assert.NotNull(duration);
        Assert.InRange(duration!.Value, 1000, 4 * 60 * 60 * 1000);
        _out.WriteLine($"duration {duration:0} ms");

        var strip = dir.File("strip");
        var f = await svc.FilmstripAsync(video, strip, 30);
        Assert.True(f.Ok, f.Error);
        var frames = Directory.GetFiles(strip, "*.jpg");
        Assert.InRange(frames.Length, 1, 1000);
        Assert.True(File.Exists(Path.Combine(strip, "done.txt")));
        _out.WriteLine($"filmstrip {frames.Length} frames");
    }
}
