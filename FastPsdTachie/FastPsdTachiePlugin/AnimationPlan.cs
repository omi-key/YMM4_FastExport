using System.Collections.Immutable;
using YukkuriMovieMaker.Plugin.FileSource.Psd;
using YukkuriMovieMaker.Plugin.Tachie.Psd;
using YukkuriMovieMaker.Plugin.Voice;

namespace FastPsdTachiePlugin;

/// <summary>
/// Seed of the blink phase: a stable hash (FNV-1a 64-bit folded to int), so the same path blinks the same way in every process.
/// The standard tachie uses string.GetHashCode(), which changes per process.
/// </summary>
internal static class SeedHash
{
    /// <summary>Tests set this to use string.GetHashCode() like the standard tachie, so outputs can be compared.</summary>
    public static bool Random { get; set; }

    public static int Of(string s) => Random ? s.GetHashCode() : Stable(s);

    public static int Stable(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in s)
        {
            h ^= (byte)c; h *= 1099511628211UL;
            h ^= (byte)(c >> 8); h *= 1099511628211UL;
        }
        return (int)(h ^ (h >> 32));
    }
}

internal enum EyeMode { Default, AlwaysOpen, AlwaysClose }
internal enum MouthMode { VowelPriority, VolumePriority, AlwaysOpen, AlwaysClose, Default }

/// <summary>
/// The frame-independent part derived from the base enabled-layer set and the PSD settings (eye/mouth animations).
/// Per-frame input (volume, mouth shape, time) only picks one layer per animation:
///   final set = (base set - removed set) + the layer each animation picked
/// </summary>
internal sealed class AnimationPlan
{
    public sealed record MouthEntry(string Key, PsdVowelMouthAnimation? Vowel, PsdMouthAnimation? Volume);
    public sealed record EyeEntry(PsdEyeAnimation Anim, double Offset, double Interval, int Hash);

    public int Id { get; }
    public ImmutableList<string> BaseRef { get; }
    public ImmutableList<PsdEyeAnimation> EyeListRef { get; }
    public ImmutableList<PsdMouthAnimation> MouthListRef { get; }
    public ImmutableList<PsdVowelMouthAnimation> VowelListRef { get; }

    public HashSet<string> BaseSet { get; }
    public HashSet<string> Removed { get; } = new(StringComparer.Ordinal);
    public List<MouthEntry> Mouths { get; } = new();
    public List<EyeEntry> Eyes { get; } = new();
    public int EyeSlotStart => Mouths.Count;
    public bool HasAnimation => Mouths.Count > 0 || Eyes.Count > 0;
    public int SlotCount => Mouths.Count + Eyes.Count;
    /// <summary>Order-independent 128-bit content hash of the base and removed sets; equal content gives equal values across sources and plans.</summary>
    public ulong Fp1 { get; private set; }
    public ulong Fp2 { get; private set; }

    public AnimationPlan(int id, PsdFolder root, ImmutableList<string> enableItems, PsdFileSettings settings, string filePath)
    {
        Id = id;
        BaseRef = enableItems;
        EyeListRef = settings.EyeAnimations;
        MouthListRef = settings.MouthAnimations;
        VowelListRef = settings.MouthVowelAnimations;

        // Base: enabled layers whose parent folders are all enabled.
        var enabled = new HashSet<string>(enableItems, StringComparer.Ordinal);
        BaseSet = new HashSet<string>(StringComparer.Ordinal);
        Collect(root, enabled, BaseSet);

        // Resolve the settings against the current PSD and keep only animations active in the base (public API).
        var normalized = settings.ResolveAgainst(root);
        var vowels = normalized.GetActiveVowelMouthAnimations(BaseSet).ToList();
        var volumes = normalized.GetActiveMouthAnimations(BaseSet).ToList();
        var eyes = normalized.GetActiveEyeAnimations(BaseSet).ToList();

        var keys = vowels.Select(v => v.Key).Concat(volumes.Select(m => m.Layers.First())).Distinct().ToList();
        foreach (var key in keys)
        {
            Mouths.Add(new MouthEntry(key,
                vowels.FirstOrDefault(v => v.Key == key),
                volumes.FirstOrDefault(m => m.Layers.First() == key)));
            Removed.Add(key);
        }
        int hash = SeedHash.Of(filePath);
        foreach (var e in eyes)
        {
            Removed.Add(e.Layers.First());
            // 0 means "automatic": 5-10 s from a pseudo-random value derived from the path.
            double offset = e.Offset != 0.0 ? e.Offset : 5.0 + YukkuriMovieMaker.Commons.StatelessRandom.GetDouble(hash, 0L) * 5.0;
            double interval = e.Interval != 0.0 ? e.Interval : 5.0 + YukkuriMovieMaker.Commons.StatelessRandom.GetDouble(hash, 1L) * 5.0;
            Eyes.Add(new EyeEntry(e, offset, interval, hash));
        }
        (Fp1, Fp2) = Fingerprint(BaseSet, Removed);
    }

    private static ulong Fnv(string s, ulong seed)
    {
        ulong h = 14695981039346656037UL ^ seed;
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }

    private static ulong Mix(ulong x)
    {
        x ^= x >> 33; x *= 0xff51afd7ed558ccdUL; x ^= x >> 33; x *= 0xc4ceb9fe1a85ec53UL; x ^= x >> 33;
        return x;
    }

    internal static (ulong, ulong) Fingerprint(HashSet<string> baseSet, HashSet<string> removed)
    {
        ulong a = 0, b = 0;
        foreach (var s in baseSet) { a += Mix(Fnv(s, 0x1111)); b += Mix(Fnv(s, 0x2222) + 1); }
        ulong c = 0, d = 0;
        foreach (var s in removed) { c += Mix(Fnv(s, 0x3333)); d += Mix(Fnv(s, 0x4444) + 2); }
        return (Mix(a ^ Mix(c + (ulong)baseSet.Count)), Mix(b ^ Mix(d + (ulong)removed.Count * 31)));
    }

    private static void Collect(PsdFolder folder, HashSet<string> enabled, HashSet<string> result)
    {
        foreach (var item in folder.Items)
        {
            if (!enabled.Contains(item.Id)) continue;
            if (item is PsdFolder sub) Collect(sub, enabled, result);
            else if (item is PsdLayer) result.Add(item.Id);
        }
    }

    /// <summary>Picks a layer per slot from the frame input; null means "add nothing".</summary>
    public string?[] Pick(MouthMode mouthMode, EyeMode eyeMode, double kuchipaku, double sensitivity, MouthShape shape, TimeSpan time)
    {
        var picks = new string?[SlotCount];
        double rate = mouthMode switch
        {
            MouthMode.AlwaysClose => 0.0,
            MouthMode.AlwaysOpen => 1.0,
            _ => kuchipaku == -1.0 ? 0.0 : Math.Clamp(kuchipaku * 10.0 * sensitivity / 100.0, 0.0, 1.0),
        };
        int i = 0;
        foreach (var m in Mouths)
        {
            picks[i++] = PickMouth(m, mouthMode, rate, shape);
        }
        foreach (var e in Eyes)
        {
            double open = eyeMode switch
            {
                EyeMode.AlwaysClose => 0.0,
                EyeMode.AlwaysOpen => 1.0,
                _ => Blink(time, e.Offset, e.Interval),
            };
            picks[i++] = Select(e.Anim.Layers, open);
        }
        return picks;
    }

    private static string? PickMouth(MouthEntry m, MouthMode mode, double rate, MouthShape shape)
    {
        switch (mode)
        {
            case MouthMode.VowelPriority:
                if (m.Vowel is not null) return VowelLayer(m.Vowel, shape);
                if (m.Volume is not null) return Select(m.Volume.Layers, rate);
                return m.Key;
            case MouthMode.VolumePriority:
                if (m.Volume is not null) return Select(m.Volume.Layers, rate);
                if (m.Vowel is not null) return VowelLayer(m.Vowel, shape);
                return m.Key;
        }
        // AlwaysOpen / AlwaysClose / Default
        if (m.Volume is not null) return Select(m.Volume.Layers, rate);
        if (m.Vowel is not null)
        {
            return mode switch
            {
                MouthMode.AlwaysOpen => m.Vowel.Key,
                MouthMode.AlwaysClose => m.Vowel.Silence,
                _ => null,
            };
        }
        return m.Key;
    }

    private static string VowelLayer(PsdVowelMouthAnimation v, MouthShape shape) => shape switch
    {
        MouthShape.A => v.A,
        MouthShape.I => v.I,
        MouthShape.U => v.U,
        MouthShape.E => v.E,
        MouthShape.O => v.O,
        _ => v.Silence,
    };

    /// <summary>The larger open (0..1) is, the nearer to the first layer the pick.</summary>
    private static string Select(ImmutableList<string> layers, double open)
    {
        int n = layers.Count;
        int idx = Math.Clamp((int)(n * (1.0 - open)), 0, n - 1);
        return layers[idx];
    }

    // Blink: within each period (0.3 s + interval), the first 0.3 s is a triangle wave open -> closed -> open. 1.0 = open.
    internal static double Blink(TimeSpan time, double offset, double interval)
    {
        const double dur = 0.3;
        double p = (time.TotalSeconds - offset) % (dur + interval) / dur;
        if (p >= 0.0 && p <= 0.5) return 1.0 - p * 2.0;
        if (p >= 0.5 && p <= 1.0) return (p - 0.5) * 2.0;
        return 1.0;
    }

    /// <summary>Builds the final enabled-layer set (only on a cache miss).</summary>
    public HashSet<string> BuildFinalSet(string?[] picks)
    {
        var set = new HashSet<string>(BaseSet, StringComparer.Ordinal);
        set.ExceptWith(Removed);
        foreach (var p in picks) if (p is not null) set.Add(p);
        return set;
    }
}
