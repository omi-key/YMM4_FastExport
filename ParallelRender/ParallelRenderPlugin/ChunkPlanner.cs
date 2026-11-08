using System.Diagnostics;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Items;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace ParallelRender;

/// <summary>
/// Plans all chunk boundaries before export. A worker seeks the video at the start of its chunk and decodes forward from the
/// preceding source keyframe to the displayed frame (preroll: wasted NVDEC work). Video item placement, playback speed and
/// offset are known from the timeline, so the preroll for "frame e starts a chunk" can be computed exactly for every frame
/// (<see cref="Preroll"/>) and the boundaries chosen globally by dynamic programming. The chunk count stays nominal
/// (ceil(count / chunk)); boundary i stays within +-band of its nominal position i x chunk and each chunk length within +-range of
/// chunk (the window, hence VRAM, is set by P x chunk, so neither the count nor the mean length may change).
/// A boundary at a video item's first frame costs nothing extra: the item's initial seek overlaps it.
/// </summary>
internal static class ChunkPlanner
{
    private const double LengthPenalty = 0.01;   // weight on deviating from the nominal length (source frames per frame): ties go to the nominal

    private sealed class Clip
    {
        public VideoItem Item = null!; public long[] Kf = null!; public long FrameTicks, DurationTicks;
    }

    /// <summary>Returns the boundary array [0, b1, b2, ..., count], or null when planning is impossible (e.g. no source info).</summary>
    public static int[]? Build(Scene scene, int from, int count, int chunk, out string info)
    {
        info = "";
        var probe = VideoDecodeLink.KeyframeProbe;
        if (probe is null) { info = "no probe"; return null; }
        if (chunk < 6 || count <= chunk) { info = "short"; return null; }
        var sw = Stopwatch.StartNew();
        int fps = scene.Timeline.VideoInfo.FPS;
        var items = scene.Timeline.Items.OfType<VideoItem>()
            .Where(it => !string.IsNullOrEmpty(it.FilePath) && it.Length > 0 && it.Frame + it.Length > from && it.Frame < from + count).ToList();
        var cache = new Dictionary<string, long[]?>();
        foreach (var f in items.Select(it => it.FilePath!).Distinct()) cache[f] = probe(f);
        long tProbe = sw.ElapsedMilliseconds;
        var clips = new List<Clip>();
        foreach (var it in items)
            if (cache[it.FilePath!] is { } k)
                clips.Add(new Clip { Item = it, FrameTicks = Math.Max(1, k[0]), DurationTicks = k[1], Kf = k[2..] });
        if (clips.Count == 0) { info = "no video items"; return null; }

        // cost[e] = total preroll (source frames) if frame e (0 = start of the output range) begins a chunk
        var cost = new double[count + 1];
        foreach (var c in clips)
        {
            var it = c.Item;
            int itemFrame = it.Frame;
            int itemLen = Math.Max(1, it.Length);
            int lo = Math.Max(itemFrame + 1, from + 1), hi = Math.Min(itemFrame + itemLen, from + count);   // excludes the item start frame (handled by the look-ahead cost below)
            var dur = TimeSpan.FromTicks(c.DurationTicks);
            // it.ContentLength opens the video file on first access (even creating GraphicsDevices, slow per clip); the probed duration is the same value.
            // Read the animated properties once outside the loop; going through Animation per frame is slow.
            var map = it.PlaybackRateMap; var offset = it.ContentOffset; var contentLen = dur; bool looped = it.IsLooped;
            bool constant = map.IsConstant; double rate = constant ? map.FirstRate : 0;
            TimeSpan SourceTime(int tf)
            {
                var itemTime = TimeSpan.FromTicks((long)(tf - itemFrame) * 10_000_000L / fps);
                TimeSpan t;
                if (!constant) t = VideoSource.CalculateSourceTime(map, itemTime, itemLen, fps, offset, contentLen, looped, dur);
                else
                {
                    // Constant-speed fast path; loop and bias handling match CalculateSourceTime.
                    t = rate == 100.0 ? itemTime + offset
                      : rate == 0.0 ? offset
                      : rate > 0.0 ? TimeSpan.FromTicks((long)((double)itemTime.Ticks * rate / 100.0)) + offset
                      : contentLen + TimeSpan.FromTicks((long)((double)itemTime.Ticks * rate / 100.0)) - offset;
                    if (looped) t = VideoSourceTime.GetLoopedTime(t, dur);
                    t = VideoSourceTime.AddBias(t, dur);
                }
                return t;
            }
            for (int tf = lo; tf < hi; tf++) cost[tf - from] += Preroll(c, SourceTime(tf).Ticks);
            // The host prefetches sources of items starting within the next second (fps frames) and does the initial seek to the item start.
            // A boundary e within [start - (fps - 1), start] makes both the previous and the next chunk do that seek (the previous one never shows it), costing one extra start preroll.
            if (itemFrame > from)
            {
                double startCost = Preroll(c, SourceTime(itemFrame).Ticks);
                for (int e = Math.Max(1, itemFrame - (fps - 1) - from); e <= Math.Min(count - 1, itemFrame - from); e++) cost[e] += startCost;
            }
        }

        long tCost = sw.ElapsedMilliseconds;
        // Boundaries within +-band chunks of nominal, lengths within chunk x (1 +- range).
        const double band = 1.0, range = 0.75;
        int M = (count + chunk - 1) / chunk;
        int d = (int)(chunk * range), lmin = Math.Max(1, chunk - d), lmax = chunk + d;
        int D = (int)(chunk * band);
        // candidates for boundary i (1..M-1) = [i*chunk - D, i*chunk + D] clipped to [1, count-1]
        int Lo(int i) => Math.Max(1, i * chunk - D);
        int Hi(int i) => Math.Min(count - 1, i * chunk + D);
        var dp = new double[M][]; var prev = new int[M][];
        dp[0] = new[] { 0.0 }; prev[0] = new[] { 0 };
        for (int i = 1; i < M; i++)
        {
            int l = Lo(i), h = Hi(i);
            dp[i] = new double[Math.Max(0, h - l + 1)]; prev[i] = new int[dp[i].Length];
            int pl = i == 1 ? 0 : Lo(i - 1);
            for (int e = l; e <= h; e++)
            {
                double best = double.PositiveInfinity; int bp = -1;
                int pa = i == 1 ? 0 : Math.Max(Lo(i - 1), e - lmax), pb = i == 1 ? 0 : Math.Min(Hi(i - 1), e - lmin);
                if (i == 1) { if (e - 0 >= lmin && e <= lmax) { best = Math.Abs(e - chunk) * LengthPenalty; bp = 0; } }
                else
                    for (int p = pa; p <= pb; p++)
                    {
                        double v = dp[i - 1][p - pl] + Math.Abs(e - p - chunk) * LengthPenalty;
                        if (v < best) { best = v; bp = p; }
                    }
                dp[i][e - l] = bp < 0 ? double.PositiveInfinity : best + cost[e];
                prev[i][e - l] = bp;
            }
        }
        // last chunk: length 1..lmax
        double bestEnd = double.PositiveInfinity; int be = -1;
        { int l = Lo(M - 1); for (int e = l; e <= Hi(M - 1); e++) { if (count - e < 1 || count - e > lmax) continue; double v = dp[M - 1][e - l]; if (v < bestEnd) { bestEnd = v; be = e; } } }
        if (M == 1 || be < 0) { info = "no feasible plan"; return null; }
        var plan = new int[M + 1]; plan[M] = count; plan[M - 1] = be;
        for (int i = M - 1; i >= 2; i--) plan[i - 1] = prev[i][plan[i] - Lo(i)];
        plan[0] = 0;
        double nominalCost = 0, planCost = 0;
        for (int i = 1; i < M; i++) { nominalCost += cost[i * chunk]; planCost += cost[plan[i]]; }
        info = $"chunks={M} clips={clips.Count} files={cache.Count} est-preroll nominal={nominalCost:F0} plan={planCost:F0} build={sw.ElapsedMilliseconds}ms (probe {tProbe}, cost {tCost - tProbe}, dp {sw.ElapsedMilliseconds - tCost})";
        return plan;
    }

    /// <summary>Frames decoded forward from the preceding keyframe to show source time t.</summary>
    private static double Preroll(Clip c, long t)
    {
        var kf = c.Kf;
        int i = Array.BinarySearch(kf, t);
        if (i < 0) i = ~i - 1;
        if (i < 0) return 0;
        if (i >= kf.Length - 1 && t > kf[^1] + 10 * 10_000_000L) return 0;
        if (t < kf[i] + 4 * c.FrameTicks && i > 0) i--;   // the index holds dts; with B-frames the pts is later, so step back one more keyframe when close
        return (double)(t - kf[i]) / c.FrameTicks;
    }
}
