using CuepointDeck.Core.Analysis;
using CuepointDeck.Core.Media;
using Xunit;
using Xunit.Abstractions;

namespace CuepointDeck.Core.Tests;

public class StructureAnalyzerTests
{
    private readonly ITestOutputHelper _out;
    public StructureAnalyzerTests(ITestOutputHelper output) => _out = output;

    /// <summary>Builds a fake song from chord "parts": each part is a set of note frequencies with an amplitude, a length,
    /// and a slightly different timbre so verse and chorus are distinguishable the way real ones are.</summary>
    private static float[] Synth(int sr, params (double[] freqs, double amp, double seconds, int harmonics)[] parts)
    {
        var total = (int)(parts.Sum(p => p.seconds) * sr);
        var pcm = new float[total];
        int pos = 0;
        var rnd = new Random(1);
        foreach (var (freqs, amp, seconds, harmonics) in parts)
        {
            int len = (int)(seconds * sr);
            for (int i = 0; i < len && pos + i < total; i++)
            {
                double t = i / (double)sr;
                double v = 0;
                foreach (var f in freqs)
                    for (int h = 1; h <= harmonics; h++) v += Math.Sin(2 * Math.PI * f * h * t) / h;
                // a little beat-like pulse and noise so it isn't sterile
                v *= 0.7 + 0.3 * Math.Abs(Math.Sin(2 * Math.PI * 2 * t));
                v += (rnd.NextDouble() - 0.5) * 0.02;
                pcm[pos + i] = (float)(amp * v / (freqs.Length * 1.5));
            }
            pos += len;
        }
        return pcm;
    }

    [Fact]
    public void Finds_boundaries_and_names_repeated_parts()
    {
        const int sr = 22050;
        var verse = (new[] { 130.8, 164.8, 196.0 }, 0.35, 20.0, 3);          // C major, quiet, 20 s
        var chorus = (new[] { 196.0, 246.9, 293.7, 392.0 }, 0.8, 16.0, 6);   // G major, loud, bright, 16 s
        var bridge = (new[] { 146.8, 185.0, 220.0, 293.7 }, 0.55, 14.0, 5);  // D major, new harmony, 14 s
        var intro = (new[] { 130.8, 196.0 }, 0.15, 10.0, 2);                 // quiet, 10 s
        var pcm = Synth(sr, intro, verse, chorus, verse, chorus, bridge, chorus);
        // expected starts: 0 intro, 10 verse, 30 chorus, 46 verse, 66 chorus, 82 bridge, 96 chorus (total 112 s)

        var cuts = new List<double> { 10.0, 30.1, 46.0, 66.1, 82.0, 96.0 };
        var result = StructureAnalyzer.Analyze(pcm, sr, cuts);
        foreach (var s in result) _out.WriteLine($"{s.StartText}  {s.Name,-12} conf {s.Confidence:0.00}  {s.Basis}");

        var expected = new[] { 0.0, 10.0, 30.0, 46.0, 66.0, 82.0, 96.0 };
        Assert.Equal(expected.Length, result.Count);
        for (int i = 0; i < expected.Length; i++)
            Assert.InRange(result[i].StartMs / 1000.0, expected[i] - 2.5, expected[i] + 2.5);

        Assert.Equal("Intro", result[0].Name);
        Assert.Equal("Chorus", result[2].Name);
        Assert.Equal("Chorus", result[4].Name);
        Assert.Equal("Chorus", result[6].Name);
        Assert.StartsWith("Verse", result[1].Name);
        Assert.StartsWith("Verse", result[3].Name);
        Assert.NotEqual(result[1].Name, result[3].Name);
        Assert.Equal("Bridge", result[5].Name);
        Assert.Contains(result.Skip(1), s => s.Basis.Contains("cut"));
    }

    [Fact]
    public void Works_without_video_cuts()
    {
        const int sr = 22050;
        var a = (new[] { 130.8, 164.8, 196.0 }, 0.4, 18.0, 3);
        var b = (new[] { 196.0, 246.9, 293.7 }, 0.8, 18.0, 6);
        var pcm = Synth(sr, a, b, a, b);
        var result = StructureAnalyzer.Analyze(pcm, sr, Array.Empty<double>());
        foreach (var s in result) _out.WriteLine($"{s.StartText}  {s.Name}");
        Assert.InRange(result.Count, 3, 5);
        Assert.Contains(result, s => Math.Abs(s.StartMs / 1000.0 - 18) < 3);
        Assert.Contains(result, s => Math.Abs(s.StartMs / 1000.0 - 36) < 3);
        Assert.Contains(result, s => s.Name == "Chorus");
    }

    /// <summary>Runs on a real file when CUEPOINTDECK_FFMPEG and CUEPOINTDECK_TEST_VIDEO are set; prints the draft for a human to judge.</summary>
    [Fact]
    public async Task Real_file_smoke()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("CUEPOINTDECK_FFMPEG");
        var video = Environment.GetEnvironmentVariable("CUEPOINTDECK_TEST_VIDEO");
        if (string.IsNullOrEmpty(ffmpeg) || string.IsNullOrEmpty(video) || !File.Exists(video)) { _out.WriteLine("skipped"); return; }
        var svc = new FfmpegService();
        Assert.True(await svc.ConfigureAsync(ffmpeg));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pcm = await svc.DecodeAudioAsync(video);
        Assert.NotNull(pcm);
        var cuts = await svc.SceneCutsAsync(video, 0.2);
        _out.WriteLine($"decode+cuts {sw.ElapsedMilliseconds} ms, {cuts.Count} cuts: {string.Join(" ", cuts.Take(20).Select(c => c.ToString("0.0")))}");
        sw.Restart();
        var result = StructureAnalyzer.Analyze(pcm!, 22050, cuts);
        _out.WriteLine($"analysis {sw.ElapsedMilliseconds} ms");
        foreach (var s in result) _out.WriteLine($"{s.StartText}  {s.Name,-12} conf {s.Confidence:0.00}  {s.Basis}");
        Assert.NotEmpty(result);
    }
}
