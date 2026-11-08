using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FastPsdTachiePlugin;
using YukkuriMovieMaker.Plugin.FileSource.Psd;
using YukkuriMovieMaker.Plugin.Tachie.Psd;
using YukkuriMovieMaker.Plugin.Voice;

/// <summary>Determinism of the blink selection (across processes and threads), its distribution, and a mixed stress test of the shared cache.</summary>
internal static class DeterminismTests
{
    private static int s_fail;
    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "ok:   " : "FAIL: ") + name);
        if (!ok) s_fail++;
    }

    private sealed class Fixture
    {
        public PsdFolder Root = new();
        public ImmutableList<string> Enable = ImmutableList<string>.Empty;
        public PsdFileSettings Settings = null!;

        public static Fixture Make()
        {
            var f = new Fixture();
            int id = 0;
            var ids = new List<string>();
            for (int d = 0; d < 3; d++)
            {
                var folder = new PsdFolder($"F{d}", id++, true);
                f.Root.AddItem(folder);
                for (int l = 0; l < 12; l++) { var ly = new PsdLayer($"L{d}_{l}", id++, true); folder.AddItem(ly); ids.Add(ly.Id); }
                ids.Add(folder.Id);
            }
            f.Enable = ImmutableList.CreateRange(ids);
            f.Settings = new PsdFileSettings
            {
                // 1st: automatic (0/0) = seeded by path. 2nd: only Interval explicit. 3rd: fully explicit (seed-independent)
                EyeAnimations = ImmutableList.Create(
                    new PsdEyeAnimation(ImmutableList.Create(ids[0], ids[1], ids[2]), 0.0, 0.0),
                    new PsdEyeAnimation(ImmutableList.Create(ids[14], ids[15]), 0.0, 6.0),
                    new PsdEyeAnimation(ImmutableList.Create(ids[30], ids[31]), 1.3, 4.0)),
            };
            return f;
        }
    }

    // Non-ASCII file names on purpose: the seed hashes UTF-16 code units.
    private static readonly string[] Paths = Enumerable.Range(0, 24).Select(i => $"C:\\psd\\chara{i}\\立ち絵{i}.psd").ToArray();

    /// <summary>Hashes the picks for a set of (path, frame) pairs into one digest. Plans are built inside the call, as sources do.</summary>
    private static string DigestOf(int salt)
    {
        var fx = Fixture.Make();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int pi = 0;
        foreach (var path in Paths)
        {
            var plan = new AnimationPlan(++pi, fx.Root, fx.Enable, fx.Settings, path);
            for (int fr = salt; fr < 60 * 900; fr += 7)
            {
                var picks = plan.Pick(MouthMode.VowelPriority, EyeMode.Default, -1.0, 10.0, MouthShape.A, TimeSpan.FromSeconds(fr / 60.0));
                foreach (var p in picks) sha.AppendData(Encoding.UTF8.GetBytes((p ?? "-") + "|"));
                sha.AppendData(new byte[] { 10 });
            }
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    public static string DumpMode() => DigestOf(0) + " seed0=" + SeedHash.Of(Paths[0]);

    private static string RunChild()
    {
        string self = Environment.ProcessPath!;
        string argv = System.IO.Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? $"\"{typeof(DeterminismTests).Assembly.Location}\" --dump" : "--dump";
        var psi = new ProcessStartInfo(self, argv) { RedirectStandardOutput = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return o;
    }

    public static int Run()
    {
        s_fail = 0;
        Console.WriteLine("== determinism ==");

        // (a) Two separate processes
        string a1 = RunChild(), a2 = RunChild();
        Check(System.Text.RegularExpressions.Regex.IsMatch(a1, "^[0-9A-F]{64} seed0=-?[0-9]+$") && a1 == a2, $"separate processes (default): identical  [{a1[..16]}.. vs {a2[..16]}..] {a1[64..]}");
        Check(a1.Split(' ')[0] == a2.Split(' ')[0] && a1.Split(' ')[1] == a2.Split(' ')[1], "seed value identical across processes");
        Check(a1.Split(' ')[0] == DigestOf(0), "in-process digest equals child digest");

        // Fixed values of the stable hash (a change would shift every blink phase, so this detects regressions)
        Check(SeedHash.Stable("") == unchecked((int)(14695981039346656037UL ^ (14695981039346656037UL >> 32))), "stable hash of empty string");
        Check(SeedHash.Stable("a") != SeedHash.Stable("b") && SeedHash.Stable("C:\\a.psd") == SeedHash.Stable("C:\\a.psd"), "stable hash discriminates / repeatable");

        // (b) 8 parallel threads (each with its own root/plan) must match the sequential result
        string seq = DigestOf(0);
        var res = new string[8];
        using var start = new ManualResetEventSlim(false);
        var ths = Enumerable.Range(0, 8).Select(i => new Thread(() => { start.Wait(); res[i] = DigestOf(0); })).ToList();
        ths.ForEach(t => t.Start()); start.Set(); ths.ForEach(t => t.Join());
        Check(res.All(r => r == seq), "8 threads x full set == sequential");
        // Also when 8 threads share one root/plan and only Pick (read-only)
        {
            var fx = Fixture.Make();
            var plan = new AnimationPlan(1, fx.Root, fx.Enable, fx.Settings, Paths[3]);
            var expect = new string[2000];
            string Sig(int fr) => string.Join(",", plan.Pick(MouthMode.VowelPriority, EyeMode.Default, -1.0, 10.0, MouthShape.A, TimeSpan.FromSeconds(fr * 5 / 60.0)).Select(x => x ?? "-"));
            for (int i = 0; i < expect.Length; i++) expect[i] = Sig(i);
            int bad = 0;
            var ts = Enumerable.Range(0, 8).Select(k => new Thread(() =>
            {
                start.Wait();
                var rr = new Random(k);
                for (int n = 0; n < 200000; n++) { int i = rr.Next(expect.Length); if (Sig(i) != expect[i]) Interlocked.Increment(ref bad); }
            })).ToList();
            ts.ForEach(t => t.Start()); ts.ForEach(t => t.Join());
            Check(bad == 0, $"one shared plan, 8 threads x 200000 random picks: mismatches={bad}");
        }

        // (c) Distribution: automatic (0/0) offset/interval uniform on [5,10], blink rate matching the interval
        Console.WriteLine("== distribution ==");
        var fx2 = Fixture.Make();
        D Dist()
        {
            const int N = 4000;
            double so = 0, si = 0, sb = 0, se = 0, mi = 99, ma = 0, mo = 99, mx = 0;
            for (int n = 0; n < N; n++)
            {
                string path = $"C:\\psd\\p{n}.psd";
                var plan = new AnimationPlan(1, fx2.Root, fx2.Enable, fx2.Settings.WithEyes0(), path);
                double off = plan.Eyes[0].Offset, itv = plan.Eyes[0].Interval;
                so += off; si += itv; mi = Math.Min(mi, itv); ma = Math.Max(ma, itv); mo = Math.Min(mo, off); mx = Math.Max(mx, off);
                // Number of times the eye slot leaves "open" in 600 s at 60 fps
                bool prev = false; int blinks = 0;
                for (int fr = 0; fr < 600 * 60; fr++)
                {
                    bool closed = AnimationPlan.Blink(TimeSpan.FromSeconds(fr / 60.0), off, itv) < 1.0;
                    if (closed && !prev) blinks++;
                    prev = closed;
                }
                sb += blinks;
                se += Math.Floor((600.0 - off) / (0.3 + itv)) + 1;   // expected count: period 0.3+itv counted from offset (negative phase is open)
            }
            return new D(so / N, si / N, sb / N, se / N, mi, ma, mo, mx);
        }
        var stable = Dist();
        Check(Math.Abs(stable.meanInt - 7.5) < 0.1 && Math.Abs(stable.meanOff - 7.5) < 0.1, "stable: mean offset/interval ~ 7.5 (uniform on [5,10])");
        Check(stable.minI >= 5.0 && stable.maxI <= 10.0 && stable.minO >= 5.0 && stable.maxO <= 10.0, "stable: within [5,10]");
        Check(Math.Abs(stable.blinksPer600 - stable.expectedBlinks) < 0.01, "stable: blink count per 600 s matches interval-derived expectation");

        // (d) Mixed stress of the shared cache (eviction + dispose grace + pins): a bitmap in use is never disposed
        Console.WriteLine("== cache stress ==");
        {
            var c = new SharedBitmapCache<FakeBitmap>(64, 1L << 40, b => b.Disposed = true, 5000);
            c.Register(1);
            long usedAfterDispose = 0, acquired = 0;
            var plan = new PlanIdentity(1, "C:\\a.psd", 1, 100, 10, 10, 1, 2);
            int idc = 0;
            var ts = Enumerable.Range(0, 8).Select(k => new Thread(() =>
            {
                var rr = new Random(k + 1);
                start.Wait();
                for (int n = 0; n < 40000; n++)
                {
                    var key = new StateKey(plan, new string?[] { "k" + rr.Next(200) });
                    var e = c.TryAcquire(key) ?? c.Acquire(key, () => (new FakeBitmap { Id = Interlocked.Increment(ref idc) }, 10), out _);
                    if (e is null) continue;
                    Interlocked.Increment(ref acquired);
                    c.Tick();
                    if (e.Value.Disposed) Interlocked.Increment(ref usedAfterDispose);   // disposed while pinned
                    c.Release(e);
                }
            })).ToList();
            ts.ForEach(t => t.Start()); ts.ForEach(t => t.Join());
            var snap = c.Snapshot();
            Check(usedAfterDispose == 0, $"pinned bitmap never disposed (acquired={acquired}, evictions={c.Stats.Evictions}, composes={c.Stats.Composes}, hits={c.Stats.Hits})");
            Check(snap.Entries <= 64, $"count limit held after quiescence (entries={snap.Entries})");
        }

        Console.WriteLine(s_fail == 0 ? "determinism tests: all passed" : $"determinism tests: {s_fail} FAILED");
        return s_fail;
    }
}

internal sealed record D(double meanOff, double meanInt, double blinksPer600, double expectedBlinks, double minI, double maxI, double minO, double maxO);

internal static class SettingsExt
{
    /// <summary>Settings with only the first (automatic 0/0) eye animation, for the distribution test.</summary>
    public static PsdFileSettings WithEyes0(this PsdFileSettings s) => new() { EyeAnimations = s.EyeAnimations.Take(1).ToImmutableList() };
}
