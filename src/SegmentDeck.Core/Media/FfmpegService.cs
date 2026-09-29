using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SegmentDeck.Core.Logging;

namespace SegmentDeck.Core.Media;

public sealed record FfmpegResult(bool Ok, string? Error = null);

/// <summary>Runs ffmpeg for thumbnails, filmstrips and durations: one job at a time, below-normal priority, never on
/// the UI thread. Only Edit mode calls this; Show mode reads cached JPEGs.</summary>
public sealed class FfmpegService
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private string _ffmpegPath = "";

    public string FfmpegPath => _ffmpegPath;
    public string FfprobePath => Path.Combine(Path.GetDirectoryName(_ffmpegPath) ?? "", "ffprobe.exe");
    public bool IsAvailable { get; private set; }
    public string? Version { get; private set; }
    public string StatusText => IsAvailable ? $"ffmpeg found ({Version})" : string.IsNullOrWhiteSpace(_ffmpegPath) ? "ffmpeg path not set" : $"ffmpeg not found at {_ffmpegPath}";

    /// <summary>Checks the configured path. Cheap enough to call on startup and whenever the setting changes.</summary>
    public async Task<bool> ConfigureAsync(string ffmpegPath)
    {
        _ffmpegPath = ffmpegPath?.Trim().Trim('"') ?? "";
        IsAvailable = false;
        Version = null;
        if (string.IsNullOrWhiteSpace(_ffmpegPath)) { Log.Warn("ffmpeg path is not set; Edit mode cannot make thumbnails"); return false; }
        if (!File.Exists(_ffmpegPath)) { Log.Warn($"ffmpeg not found at {_ffmpegPath}"); return false; }
        try
        {
            var (code, output) = await RunAsync(_ffmpegPath, "-version", TimeSpan.FromSeconds(10));
            var m = Regex.Match(output, @"ffmpeg version (\S+)");
            Version = m.Success ? m.Groups[1].Value : "unknown version";
            IsAvailable = code == 0 || m.Success;
            Log.Info(IsAvailable ? $"ffmpeg OK: {Version} at {_ffmpegPath}" : $"ffmpeg at {_ffmpegPath} did not run (exit {code})");
        }
        catch (Exception ex) { Log.Error("Checking ffmpeg", ex); }
        return IsAvailable;
    }

    /// <summary>One JPEG frame at <paramref name="ms"/>, 480 px wide, quality ~85.</summary>
    public async Task<FfmpegResult> ThumbnailAsync(string sourceFile, double ms, string outputJpeg, CancellationToken ct = default)
    {
        if (!IsAvailable) return new FfmpegResult(false, "ffmpeg is not available");
        if (!File.Exists(sourceFile)) return new FfmpegResult(false, $"Source file not found: {sourceFile}");
        Directory.CreateDirectory(Path.GetDirectoryName(outputJpeg)!);
        var seconds = (ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);
        // -ss before -i seeks by keyframe then decodes forward to the exact time (accurate_seek is the default).
        var args = $"-hide_banner -loglevel error -y -ss {seconds} -i \"{sourceFile}\" -frames:v 1 -vf \"scale=480:-2\" -q:v 3 \"{outputJpeg}\"";
        return await JobAsync(args, outputJpeg, ct);
    }

    /// <summary>One small JPEG every <paramref name="intervalSeconds"/> into <paramref name="outputDir"/> as 00001.jpg, 00002.jpg…</summary>
    public async Task<FfmpegResult> FilmstripAsync(string sourceFile, string outputDir, double intervalSeconds = 2, CancellationToken ct = default)
    {
        if (!IsAvailable) return new FfmpegResult(false, "ffmpeg is not available");
        if (!File.Exists(sourceFile)) return new FfmpegResult(false, $"Source file not found: {sourceFile}");
        Directory.CreateDirectory(outputDir);
        var fps = (1.0 / intervalSeconds).ToString("0.####", CultureInfo.InvariantCulture);
        var pattern = Path.Combine(outputDir, "%05d.jpg");
        var args = $"-hide_banner -loglevel error -y -i \"{sourceFile}\" -vf \"fps={fps},scale=192:-2\" -q:v 6 \"{pattern}\"";
        var result = await JobAsync(args, Path.Combine(outputDir, "00001.jpg"), ct, TimeSpan.FromMinutes(10));
        if (result.Ok) File.WriteAllText(Path.Combine(outputDir, "done.txt"), DateTime.UtcNow.ToString("o"));
        return result;
    }

    /// <summary>Duration in ms via ffprobe, falling back to parsing ffmpeg's banner.</summary>
    public async Task<double?> DurationMsAsync(string sourceFile)
    {
        if (!File.Exists(sourceFile)) return null;
        try
        {
            if (File.Exists(FfprobePath))
            {
                var (_, output) = await RunAsync(FfprobePath, $"-v error -show_entries format=duration -of csv=p=0 \"{sourceFile}\"", TimeSpan.FromSeconds(30));
                if (double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return s * 1000;
            }
            if (IsAvailable)
            {
                var (_, output) = await RunAsync(_ffmpegPath, $"-hide_banner -i \"{sourceFile}\"", TimeSpan.FromSeconds(30));
                var m = Regex.Match(output, @"Duration:\s*(\d+):(\d+):(\d+)\.(\d+)");
                if (m.Success)
                {
                    var h = int.Parse(m.Groups[1].Value); var mi = int.Parse(m.Groups[2].Value); var se = int.Parse(m.Groups[3].Value);
                    var frac = double.Parse("0." + m.Groups[4].Value, CultureInfo.InvariantCulture);
                    return ((h * 60 + mi) * 60 + se + frac) * 1000;
                }
            }
        }
        catch (Exception ex) { Log.Error("Reading duration", ex); }
        return null;
    }

    private async Task<FfmpegResult> JobAsync(string args, string expectedOutput, CancellationToken ct, TimeSpan? timeout = null)
    {
        await _oneAtATime.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            var (code, output) = await RunAsync(_ffmpegPath, args, timeout ?? TimeSpan.FromSeconds(60), ct);
            if (code != 0 || !File.Exists(expectedOutput))
            {
                var err = output.Trim().Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? $"ffmpeg exit code {code}";
                Log.Error($"ffmpeg failed ({sw.ElapsedMilliseconds} ms): {err}  [{args}]");
                return new FfmpegResult(false, err);
            }
            Log.Info($"ffmpeg done in {sw.ElapsedMilliseconds} ms: {Path.GetFileName(expectedOutput)}");
            return new FfmpegResult(true);
        }
        catch (OperationCanceledException) { return new FfmpegResult(false, "cancelled"); }
        catch (Exception ex) { Log.Error("ffmpeg", ex); return new FfmpegResult(false, ex.Message); }
        finally { _oneAtATime.Release(); }
    }

    private static async Task<(int code, string output)> RunAsync(string exe, string args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + exe);
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw;
        }
        return (p.ExitCode, (await stdout) + "\n" + (await stderr));
    }
}
