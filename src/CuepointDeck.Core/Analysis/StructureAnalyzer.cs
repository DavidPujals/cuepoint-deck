namespace CuepointDeck.Core.Analysis;

public sealed record SuggestedSegment(long StartMs, string Name, double Confidence, string Basis)
{
    public string StartText => Timecode.Format(StartMs);
}

public sealed class AnalysisOptions
{
    /// <summary>Feature frame length after aggregation. 0.5 s keeps a 5-minute song at ~600 frames.</summary>
    public double FrameSeconds { get; init; } = 0.5;
    /// <summary>Checkerboard kernel width for the novelty curve. Longer = fewer, more structural boundaries.</summary>
    public double KernelSeconds { get; init; } = 12;
    public double MinSegmentSeconds { get; init; } = 10;
    /// <summary>A boundary weaker than this between two repeats of the same part is dropped (one chorus, not two halves).</summary>
    public double MergeBelowConfidence { get; init; } = 0.4;
    /// <summary>A video cut this close to an audio boundary is taken as the exact boundary.</summary>
    public double CutSnapSeconds { get; init; } = 1.5;
    /// <summary>Segments whose chroma sequences correlate above this are the same part of the song.</summary>
    public double RepeatThreshold { get; init; } = 0.6;
}

/// <summary>
/// Offline song-structure analysis: chroma + timbre features, a self-similarity matrix, Foote novelty for
/// boundaries, video cuts to snap them, and repetition to name the parts. No network, no model files.
/// Output is a draft for a human to check, never ground truth.
/// </summary>
public static class StructureAnalyzer
{
    private const int Window = 2048;
    private const int Hop = 512;
    private const int MelBands = 24;

    public static List<SuggestedSegment> Analyze(float[] pcm, int sampleRate, IReadOnlyList<double> cutSeconds, AnalysisOptions? options = null, IProgress<string>? progress = null)
    {
        var o = options ?? new AnalysisOptions();
        var durationSec = pcm.Length / (double)sampleRate;
        if (durationSec < 20) return new List<SuggestedSegment> { new(0, "Intro", 0.2, "too short to analyse") };

        progress?.Report("Listening (features)…");
        var (chroma, mel, rms, hopSec) = Features(pcm, sampleRate);

        // Aggregate STFT frames into analysis frames.
        int per = Math.Max(1, (int)Math.Round(o.FrameSeconds / hopSec));
        int n = chroma.Length / per;
        if (n < 8) return new List<SuggestedSegment> { new(0, "Intro", 0.2, "too short to analyse") };
        var frameSec = per * hopSec;
        var feat = new double[n][];
        var frameRms = new double[n];
        var frameChroma = new double[n][];
        var frameMel = new double[n][];
        for (int i = 0; i < n; i++)
        {
            var c = new double[12]; var m = new double[MelBands]; double e = 0;
            for (int k = 0; k < per; k++)
            {
                var f = i * per + k;
                for (int b = 0; b < 12; b++) c[b] += chroma[f][b];
                for (int b = 0; b < MelBands; b++) m[b] += mel[f][b];
                e += rms[f];
            }
            Normalize(c); CenterNormalize(m);
            frameChroma[i] = c;
            frameMel[i] = m;
            frameRms[i] = e / per;
            feat[i] = new double[12 + MelBands];
            for (int b = 0; b < 12; b++) feat[i][b] = c[b] * 1.0;
            for (int b = 0; b < MelBands; b++) feat[i][12 + b] = m[b] * 0.7;
        }

        progress?.Report("Finding boundaries…");
        var ssm = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = i; j < n; j++)
            {
                var s = Cosine(feat[i], feat[j]);
                ssm[i, j] = s; ssm[j, i] = s;
            }

        int half = Math.Max(2, (int)Math.Round(o.KernelSeconds / frameSec / 2));
        var novelty = Novelty(ssm, n, half);
        var peaks = PickPeaks(novelty, (int)Math.Round(o.MinSegmentSeconds / frameSec), edgeFrames: (int)Math.Round(4 / frameSec));

        // Boundaries in seconds, snapped to nearby video cuts.
        var boundaries = new List<(double sec, double conf, string basis)>();
        foreach (var (idx, height) in peaks)
        {
            var sec = idx * frameSec;
            var basis = "audio";
            double best = double.MaxValue; double snapped = sec;
            foreach (var cut in cutSeconds)
            {
                var d = Math.Abs(cut - sec);
                if (d < o.CutSnapSeconds && d < best) { best = d; snapped = cut; }
            }
            if (best < double.MaxValue) basis = "audio + video cut";
            boundaries.Add((snapped, Math.Min(1, height + (basis.Contains("cut") ? 0.25 : 0)), basis));
        }
        boundaries.Sort((a, b) => a.sec.CompareTo(b.sec));

        // Drop boundaries that leave a segment shorter than the minimum, keeping the stronger one.
        for (int i = 1; i < boundaries.Count;)
        {
            if (boundaries[i].sec - boundaries[i - 1].sec < o.MinSegmentSeconds)
            {
                if (boundaries[i].conf < boundaries[i - 1].conf) boundaries.RemoveAt(i); else boundaries.RemoveAt(i - 1);
            }
            else i++;
        }
        if (boundaries.Count > 0 && durationSec - boundaries[^1].sec < o.MinSegmentSeconds * 0.6) boundaries.RemoveAt(boundaries.Count - 1);

        progress?.Report("Naming the parts…");
        string[] names;
        while (true)
        {
            var starts = new List<double> { 0 };
            starts.AddRange(boundaries.Select(b => b.sec));
            int[] groups;
            (names, groups) = NameSegments(starts, durationSec, frameChroma, frameMel, frameRms, frameSec, o.RepeatThreshold);

            // Merge: a weak boundary between two repeats of the same part is usually a split inside one part.
            int weakest = -1; double weakestConf = o.MergeBelowConfidence;
            for (int i = 0; i < boundaries.Count; i++)
            {
                if (groups[i + 1] != groups[i] || boundaries[i].conf >= weakestConf) continue;
                double from = i == 0 ? 0 : boundaries[i - 1].sec;
                double to = i + 1 < boundaries.Count ? boundaries[i + 1].sec : durationSec;
                if (to - from > 50) continue;   // a single part longer than that is unlikely; keep the split
                weakest = i; weakestConf = boundaries[i].conf;
            }
            if (weakest < 0) break;
            boundaries.RemoveAt(weakest);
        }

        var result = new List<SuggestedSegment> { new(0, names[0], 1.0, "start of clip") };
        for (int i = 0; i < boundaries.Count; i++)
            result.Add(new SuggestedSegment((long)Math.Round(boundaries[i].sec * 1000), names[i + 1], Math.Round(boundaries[i].conf, 2), boundaries[i].basis));
        return result;
    }

    // ------------------------------------------------------------------ features

    private static (double[][] chroma, double[][] mel, double[] rms, double hopSec) Features(float[] pcm, int sr)
    {
        int frames = Math.Max(1, (pcm.Length - Window) / Hop + 1);
        var chroma = new double[frames][];
        var mel = new double[frames][];
        var rms = new double[frames];
        var hann = new double[Window];
        for (int i = 0; i < Window; i++) hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Window - 1));

        int bins = Window / 2;
        var binChroma = new int[bins];
        var binMel = new int[bins];
        double melMax = HzToMel(Math.Min(8000, sr / 2.0));
        for (int b = 1; b < bins; b++)
        {
            double hz = b * sr / (double)Window;
            binChroma[b] = hz is >= 55 and <= 4200 ? ((int)Math.Round(12 * Math.Log2(hz / 440.0)) % 12 + 12 + 9) % 12 : -1;  // A=9 → C=0
            binMel[b] = hz <= 8000 ? Math.Min(MelBands - 1, (int)(HzToMel(hz) / melMax * MelBands)) : -1;
        }
        binChroma[0] = -1; binMel[0] = -1;

        var re = new double[Window]; var im = new double[Window];
        for (int f = 0; f < frames; f++)
        {
            int off = f * Hop;
            double energy = 0;
            for (int i = 0; i < Window; i++)
            {
                var v = off + i < pcm.Length ? pcm[off + i] : 0;
                re[i] = v * hann[i]; im[i] = 0;
                energy += v * v;
            }
            rms[f] = Math.Sqrt(energy / Window);
            Fft(re, im);
            var c = new double[12]; var m = new double[MelBands];
            for (int b = 1; b < bins; b++)
            {
                var mag = re[b] * re[b] + im[b] * im[b];
                if (binChroma[b] >= 0) c[binChroma[b]] += mag;
                if (binMel[b] >= 0) m[binMel[b]] += mag;
            }
            for (int b = 0; b < MelBands; b++) m[b] = Math.Log(1 + m[b]);
            chroma[f] = c; mel[f] = m;
        }
        return (chroma, mel, rms, Hop / (double)sr);
    }

    private static double HzToMel(double hz) => 2595 * Math.Log10(1 + hz / 700);

    private static void Normalize(double[] v)
    {
        double s = 0; foreach (var x in v) s += x * x;
        s = Math.Sqrt(s); if (s <= 1e-12) return;
        for (int i = 0; i < v.Length; i++) v[i] /= s;
    }

    private static void CenterNormalize(double[] v)
    {
        double mean = v.Average();
        for (int i = 0; i < v.Length; i++) v[i] -= mean;
        Normalize(v);
    }

    private static double Cosine(double[] a, double[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na <= 1e-12 || nb <= 1e-12 ? 0 : dot / Math.Sqrt(na * nb);
    }

    /// <summary>In-place iterative radix-2 FFT.</summary>
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }

    // ------------------------------------------------------------------ boundaries

    private static double[] Novelty(double[,] ssm, int n, int half)
    {
        var kernel = new double[2 * half, 2 * half];
        double sigma = half / 2.0;
        for (int p = 0; p < 2 * half; p++)
            for (int q = 0; q < 2 * half; q++)
            {
                double dp = p - half + 0.5, dq = q - half + 0.5;
                double g = Math.Exp(-(dp * dp + dq * dq) / (2 * sigma * sigma));
                kernel[p, q] = Math.Sign(dp) * Math.Sign(dq) * g;
            }
        var nov = new double[n];
        for (int i = half; i < n - half; i++)
        {
            double s = 0;
            for (int p = 0; p < 2 * half; p++)
                for (int q = 0; q < 2 * half; q++)
                    s += kernel[p, q] * ssm[i - half + p, i - half + q];
            nov[i] = s;
        }
        double max = nov.Max(), min = nov.Min();
        if (max > min) for (int i = 0; i < n; i++) nov[i] = (nov[i] - min) / (max - min);
        return nov;
    }

    private static List<(int idx, double height)> PickPeaks(double[] nov, int minDistance, int edgeFrames)
    {
        var peaks = new List<(int, double)>();
        double mean = nov.Average();
        double std = Math.Sqrt(nov.Select(v => (v - mean) * (v - mean)).Average());
        double threshold = Math.Max(0.2, mean + 0.6 * std);
        for (int i = edgeFrames; i < nov.Length - edgeFrames; i++)
        {
            if (nov[i] < threshold) continue;
            bool isMax = true;
            for (int k = Math.Max(0, i - minDistance / 2); k <= Math.Min(nov.Length - 1, i + minDistance / 2); k++)
                if (nov[k] > nov[i]) { isMax = false; break; }
            if (isMax) peaks.Add((i, nov[i]));
        }
        return peaks;
    }

    // ------------------------------------------------------------------ naming

    private static (string[] names, int[] groups) NameSegments(List<double> starts, double duration, double[][] chroma, double[][] mel, double[] rms, double frameSec, double repeatThreshold)
    {
        int count = starts.Count;
        var names = new string[count];
        var ends = new double[count];
        for (int i = 0; i < count; i++) ends[i] = i + 1 < count ? starts[i + 1] : duration;

        // Each segment as a fixed-length chroma sequence (harmony over time) plus an average timbre, for comparing
        // parts regardless of length.
        const int steps = 24;
        var profiles = new double[count][];
        var timbres = new double[count][];
        var energy = new double[count];
        for (int i = 0; i < count; i++)
        {
            int a = (int)(starts[i] / frameSec), b = Math.Max(a + 1, Math.Min(chroma.Length, (int)(ends[i] / frameSec)));
            var prof = new double[steps * 12];
            for (int s = 0; s < steps; s++)
            {
                int f0 = a + (b - a) * s / steps, f1 = Math.Max(f0 + 1, a + (b - a) * (s + 1) / steps);
                for (int f = f0; f < f1 && f < chroma.Length; f++) for (int c = 0; c < 12; c++) prof[s * 12 + c] += chroma[f][c];
            }
            Normalize(prof);
            profiles[i] = prof;
            var tim = new double[MelBands];
            for (int f = a; f < b && f < mel.Length; f++) for (int c = 0; c < MelBands; c++) tim[c] += mel[f][c];
            Normalize(tim);
            timbres[i] = tim;
            double e = 0; int cnt = 0;
            for (int f = a; f < b && f < rms.Length; f++) { e += rms[f]; cnt++; }
            energy[i] = cnt > 0 ? e / cnt : 0;
        }

        // Greedy grouping of repeated parts.
        var group = new int[count];
        for (int i = 0; i < count; i++) group[i] = -1;
        int groups = 0;
        for (int i = 0; i < count; i++)
        {
            if (group[i] >= 0) continue;
            group[i] = groups;
            for (int j = i + 1; j < count; j++)
            {
                if (group[j] >= 0) continue;
                double li = ends[i] - starts[i], lj = ends[j] - starts[j];
                double ratio = li / lj;
                if (ratio < 0.55 || ratio > 1.8) continue;
                // Same part of the song = same harmony at a similar loudness (a quiet intro on the chorus chords is not a chorus).
                double eratio = energy[i] / Math.Max(1e-9, energy[j]);
                if (eratio < 0.5 || eratio > 2.0) continue;
                var sim = 0.65 * Cosine(profiles[i], profiles[j]) + 0.35 * Cosine(timbres[i], timbres[j]);
                if (sim >= repeatThreshold) group[j] = groups;
            }
            groups++;
        }

        var members = Enumerable.Range(0, groups).Select(g => Enumerable.Range(0, count).Where(i => group[i] == g).ToList()).ToList();
        var repeated = members.Where(m => m.Count >= 2).ToList();

        int chorusGroup = -1;
        if (repeated.Count > 0)
        {
            // Chorus: repeats most and plays loudest. Score = count × mean energy.
            chorusGroup = repeated.Select(m => (g: group[m[0]], score: m.Count * m.Average(i => energy[i])))
                                  .OrderByDescending(x => x.score).First().g;
        }
        int verseGroup = -1;
        {
            var others = repeated.Where(m => group[m[0]] != chorusGroup).OrderBy(m => m[0]).ToList();
            if (others.Count > 0) verseGroup = group[others[0][0]];
        }

        double maxEnergy = energy.Max();
        int verseNo = 0, chorusSeen = 0, bridgeGroup = -1, tagNo = 0;
        var chorusPositions = chorusGroup < 0 ? new List<int>() : Enumerable.Range(0, count).Where(i => group[i] == chorusGroup).ToList();
        for (int i = 0; i < count; i++)
        {
            double len = ends[i] - starts[i];
            bool quiet = energy[i] < 0.45 * maxEnergy;
            bool singleton = members[group[i]].Count == 1;
            if (i == 0 && singleton && (len < 25 || quiet)) { names[i] = "Intro"; continue; }
            if (i == count - 1 && count > 2 && singleton && (len < 30 || quiet)) { names[i] = "Outro"; continue; }
            if (group[i] == chorusGroup) { chorusSeen++; names[i] = "Chorus"; continue; }
            if (group[i] == verseGroup) { names[i] = $"Verse {++verseNo}"; continue; }
            if (members[group[i]].Count >= 2)
            {
                // A repeated part that is not verse or chorus: before a chorus each time → Pre-Chorus; the first other
                // one is the Bridge (bridges often repeat); anything else is a tag or instrumental.
                bool beforeChorus = members[group[i]].All(m => m + 1 < count && group[m + 1] == chorusGroup);
                if (beforeChorus) { names[i] = "Pre-Chorus"; continue; }
                if (bridgeGroup < 0 || bridgeGroup == group[i]) { bridgeGroup = group[i]; names[i] = "Bridge"; continue; }
                names[i] = quiet ? "Instrumental" : $"Tag {++tagNo}";
                continue;
            }
            // One-off parts.
            bool afterSecondChorus = chorusSeen >= 2 && chorusPositions.Any(p => p > i);
            if (afterSecondChorus && bridgeGroup < 0) { bridgeGroup = group[i]; names[i] = quiet ? "Instrumental" : "Bridge"; continue; }
            names[i] = quiet && verseNo > 0 ? "Instrumental" : $"Verse {++verseNo}";
        }
        return (names, group);
    }
}
