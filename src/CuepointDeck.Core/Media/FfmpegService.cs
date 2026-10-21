using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CuepointDeck.Core.Logging;

namespace CuepointDeck.Core.Media;

public sealed record FfmpegResult(bool Ok, string? Error = null);

/// <summary>Runs ffmpeg for thumbnails, filmstrips and durations at below-normal priority, never on the UI thread
/// (file checks included, since media may be on a share). Two lanes: long jobs (filmstrip, preview video, audio, scene
/// cuts) one at a time, and single frames one at a time beside them, so a thumbnail or the exact preview frame never
/// waits minutes behind a preview encode. Only Edit mode calls this; Show mode reads cached JPEGs.</summary>
public sealed class FfmpegService
{
    private readonly SemaphoreSlim _longLane = new(1, 1);
    private readonly SemaphoreSlim _frameLane = new(1, 1);

    /// <summary>Continues on a thread-pool thread, whatever thread called.</summary>
    private readonly struct ToPool : INotifyCompletion
    {
        public ToPool GetAwaiter() => this;
        public bool IsCompleted => Thread.CurrentThread.IsThreadPoolThread;
        public void OnCompleted(Action continuation) => ThreadPool.UnsafeQueueUserWorkItem(c => ((Action)c!)(), continuation);
        public void GetResult() { }
    }
    private string _ffmpegPath = "";

    public string FfmpegPath => _ffmpegPath;
    public string FfprobePath => Path.Combine(Path.GetDirectoryName(_ffmpegPath) ?? "", "ffprobe.exe");
    public bool IsAvailable { get; private set; }
    public string? Version { get; private set; }
    public string StatusText => IsAvailable ? $"ffmpeg found ({Version})" : string.IsNullOrWhiteSpace(_ffmpegPath) ? "ffmpeg path not set" : $"ffmpeg not found at {_ffmpegPath}";

    /// <summary>Checks the configured path. Cheap enough to call on startup and whenever the setting changes.</summary>
    public async Task<bool> ConfigureAsync(string ffmpegPath)
    {
        await new ToPool();
        _ffmpegPath = ffmpegPath?.Trim().Trim('"') ?? "";
        IsAvailable = false;
        Version = null;
        if (string.IsNullOrWhiteSpace(_ffmpegPath)) { Log.Warn("ffmpeg path is not set; Edit mode cannot make thumbnails"); return false; }
        if (!File.Exists(_ffmpegPath)) { Log.Warn($"ffmpeg not found at {_ffmpegPath}"); return false; }
        try
        {
            var (code, output) = await RunAsync(_ffmpegPath, "-version", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var m = Regex.Match(output, @"ffmpeg version (\S+)");
            Version = m.Success ? m.Groups[1].Value : "unknown version";
            IsAvailable = code == 0 || m.Success;
            Log.Info(IsAvailable ? $"ffmpeg OK: {Version} at {_ffmpegPath}" : $"ffmpeg at {_ffmpegPath} did not run (exit {code})");
        }
        catch (Exception ex) { Log.Error("Checking ffmpeg", ex); }
        return IsAvailable;
    }

    /// <summary>One JPEG frame at <paramref name="ms"/>, 480 px wide, quality ~85.</summary>
    public async Task<FfmpegResult> ThumbnailAsync(string sourceFile, double ms, string outputJpeg, CancellationToken ct = default, int width = 480, string? extraFilter = null)
    {
        if (!IsAvailable) return new FfmpegResult(false, "ffmpeg is not available");
        await new ToPool();
        if (!File.Exists(sourceFile)) return new FfmpegResult(false, $"Source file not found: {sourceFile}");
        Directory.CreateDirectory(Path.GetDirectoryName(outputJpeg)!);
        var seconds = (ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);
        var filter = $"scale={width}:-2" + (string.IsNullOrEmpty(extraFilter) ? "" : "," + extraFilter);
        // -ss before -i seeks by keyframe then decodes forward to the exact time (accurate_seek is the default).
        var args = $"-hide_banner -loglevel error -y -ss {seconds} -i \"{sourceFile}\" -frames:v 1 -vf \"{filter}\" -q:v 3 \"{outputJpeg}\"";
        return await JobAsync(_frameLane, args, outputJpeg, ct).ConfigureAwait(false);
    }

    /// <summary>One small JPEG every <paramref name="intervalSeconds"/> into <paramref name="outputDir"/> as 00001.jpg, 00002.jpg…</summary>
    public async Task<FfmpegResult> FilmstripAsync(string sourceFile, string outputDir, double intervalSeconds = 2, CancellationToken ct = default)
    {
        if (!IsAvailable) return new FfmpegResult(false, "ffmpeg is not available");
        await new ToPool();
        if (!File.Exists(sourceFile)) return new FfmpegResult(false, $"Source file not found: {sourceFile}");
        Directory.CreateDirectory(outputDir);
        var fps = (1.0 / intervalSeconds).ToString("0.####", CultureInfo.InvariantCulture);
        var pattern = Path.Combine(outputDir, "%05d.jpg");
        var args = $"-hide_banner -loglevel error -y -i \"{sourceFile}\" -vf \"fps={fps},scale=320:-2\" -q:v 6 \"{pattern}\"";
        var result = await JobAsync(_longLane, args, Path.Combine(outputDir, "00001.jpg"), ct, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        if (result.Ok) File.WriteAllText(Path.Combine(outputDir, "done.txt"), DateTime.UtcNow.ToString("o"));
        return result;
    }

    /// <summary>A small H.264/AAC proxy of the song for the Edit mode player: quick to seek, plays in WPF's media element,
    /// which cannot decode DXV. Written to a temp name and renamed when complete.</summary>
    public async Task<FfmpegResult> PreviewProxyAsync(string sourceFile, string outputMp4, CancellationToken ct = default)
    {
        if (!IsAvailable) return new FfmpegResult(false, "ffmpeg is not available");
        await new ToPool();
        if (!File.Exists(sourceFile)) return new FfmpegResult(false, $"Source file not found: {sourceFile}");
        if (File.Exists(outputMp4)) return new FfmpegResult(true);
        Directory.CreateDirectory(Path.GetDirectoryName(outputMp4)!);
        var tmp = outputMp4 + ".part.mp4";
        // Short GOP so seeking to any frame is fast; 360p is plenty for a preview and keeps the file small.
        var args = $"-hide_banner -loglevel error -y -i \"{sourceFile}\" -vf \"scale=640:-2\" -c:v libx264 -preset veryfast -crf 26 -g 12 -keyint_min 12 -pix_fmt yuv420p -c:a aac -b:a 96k -movflags +faststart \"{tmp}\"";
        var result = await JobAsync(_longLane, args, tmp, ct, TimeSpan.FromMinutes(15)).ConfigureAwait(false);
        if (!result.Ok) { try { File.Delete(tmp); } catch { } return result; }
        try { File.Move(tmp, outputMp4, overwrite: true); }
        catch (Exception ex) { return new FfmpegResult(false, ex.Message); }
        return new FfmpegResult(true);
    }

    /// <summary>Decodes the audio track to mono float samples at <paramref name="sampleRate"/> Hz, for structure analysis.</summary>
    public async Task<float[]?> DecodeAudioAsync(string sourceFile, int sampleRate = 22050, CancellationToken ct = default)
    {
        if (!IsAvailable) return null;
        await new ToPool();
        if (!File.Exists(sourceFile)) return null;
        try { await _longLane.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
        try
        {
            var args = $"-hide_banner -loglevel error -i \"{sourceFile}\" -vn -ac 1 -ar {sampleRate} -f s16le -";
            var (code, pcm, err) = await RunBinaryAsync(_ffmpegPath, args, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            if (code != 0 || pcm.Length < sampleRate * 2) { Log.Warn($"Audio decode failed ({code}): {err.Trim()}"); return null; }
            // Read the 16-bit samples in place: no second copy of the PCM.
            var shorts = MemoryMarshal.Cast<byte, short>(pcm.Span);
            var samples = new float[shorts.Length];
            for (int i = 0; i < samples.Length; i++) samples[i] = shorts[i] / 32768f;
            Log.Info($"Decoded {samples.Length / (double)sampleRate:0.0} s of audio from {Path.GetFileName(sourceFile)}");
            return samples;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Log.Error("Audio decode", ex); return null; }
        finally { _longLane.Release(); }
    }

    /// <summary>Timestamps (seconds) of hard video cuts, from ffmpeg's scene-change detector on a downscaled picture.</summary>
    public async Task<List<double>> SceneCutsAsync(string sourceFile, double threshold = 0.3, CancellationToken ct = default)
    {
        var cuts = new List<double>();
        if (!IsAvailable) return cuts;
        await new ToPool();
        if (!File.Exists(sourceFile)) return cuts;
        try { await _longLane.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return cuts; }
        try
        {
            var thr = threshold.ToString("0.00", CultureInfo.InvariantCulture);
            var args = $"-hide_banner -loglevel info -i \"{sourceFile}\" -an -vf \"scale=160:-2,select='gt(scene,{thr})',showinfo\" -f null -";
            var (_, output) = await RunAsync(_ffmpegPath, args, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            foreach (Match m in Regex.Matches(output, @"pts_time:\s*([0-9.]+)"))
                if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) cuts.Add(t);
            Log.Info($"Scene cuts: {cuts.Count} in {Path.GetFileName(sourceFile)}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Scene cuts", ex); }
        finally { _longLane.Release(); }
        return cuts;
    }

    private static async Task<(int code, Memory<byte> stdout, string stderr)> RunBinaryAsync(string exe, string args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Start(psi);
        var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); await copy.ConfigureAwait(false); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return (p.ExitCode, ms.GetBuffer().AsMemory(0, (int)ms.Length), await stderr.ConfigureAwait(false));
    }

    /// <summary>Starts a helper at below-normal priority inside the job that ends it when this app ends.</summary>
    private static Process Start(ProcessStartInfo psi)
    {
        var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + psi.FileName);
        ChildProcessJob.Add(p);
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        return p;
    }

    /// <summary>Duration in ms via ffprobe, falling back to parsing ffmpeg's banner.</summary>
    public async Task<double?> DurationMsAsync(string sourceFile)
    {
        await new ToPool();
        if (!File.Exists(sourceFile)) return null;
        try
        {
            if (File.Exists(FfprobePath))
            {
                var (_, output) = await RunAsync(FfprobePath, $"-v error -show_entries format=duration -of csv=p=0 \"{sourceFile}\"", TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                if (double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return s * 1000;
            }
            if (IsAvailable)
            {
                var (_, output) = await RunAsync(_ffmpegPath, $"-hide_banner -i \"{sourceFile}\"", TimeSpan.FromSeconds(30)).ConfigureAwait(false);
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

    private async Task<FfmpegResult> JobAsync(SemaphoreSlim lane, string args, string expectedOutput, CancellationToken ct, TimeSpan? timeout = null)
    {
        try { await lane.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new FfmpegResult(false, "cancelled"); }
        try
        {
            var sw = Stopwatch.StartNew();
            var (code, output) = await RunAsync(_ffmpegPath, args, timeout ?? TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
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
        finally { lane.Release(); }
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
        using var p = Start(psi);
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw;
        }
        return (p.ExitCode, (await stdout.ConfigureAwait(false)) + "\n" + (await stderr.ConfigureAwait(false)));
    }
}
