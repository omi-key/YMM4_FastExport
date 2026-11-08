using System.IO;
using System.Text.Json;

namespace ParallelRender;

internal enum NvencCodec { H264, H265, AV1 }
internal enum NvencQuality { Speed, Balanced, Quality }
internal enum NvencRateControl { Fixed, Variable, YouTubeRecommended }
/// <summary>GPU direct (shared textures between devices). Auto = default: use it when possible, falling back to CPU readback on failure.</summary>
internal enum GpuDirectMode { Auto, Off, On }

/// <summary>
/// Parallel export settings, persisted to ParallelRender.settings.json in the plugin folder (write failures are ignored).
/// Environment variables (PR_*, see <see cref="ParallelSettings.WithEnvironment"/>) override saved values.
/// The encoder options mirror the stock NVEnc plugin's settings in meaning and defaults (those are internal types and
/// not persisted, so they cannot be shared and are copied here).
/// </summary>
internal sealed class ParallelSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Parallelism P. 0 = auto: min(4, logical CPUs).</summary>
    public int Parallelism { get; set; }
    /// <summary>Contiguous chunk length in frames. Window = P x chunk. On cards whose VRAM budget cannot hold the window, the chunk shrinks automatically.</summary>
    public int ChunkFrames { get; set; } = 250;
    /// <summary>Upper bound (MB) of the reorder buffer on the CPU-readback path.</summary>
    public int BufferMb { get; set; } = 4096;
    /// <summary>
    /// Encode sessions (1 = single session). With 2+ several NVENC sessions are opened, GOP-sized chunks are assigned
    /// alternately and muxed in order into one mp4 (H.264/HEVC only; continues with fewer if a session cannot be opened).
    /// Set only through PR_ENC_SESSIONS; not shown in the UI or saved.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int EncSessions { get; set; } = 2;
    public NvencCodec Codec { get; set; } = NvencCodec.H264;
    public int BitrateKbps { get; set; } = 12000;
    public NvencQuality Quality { get; set; } = NvencQuality.Balanced;
    public NvencRateControl RateControl { get; set; } = NvencRateControl.YouTubeRecommended;
    /// <summary>GPU direct: workers draw straight into shared textures, skipping CPU readback and upload. Falls back to CPU readback when unavailable.</summary>
    public GpuDirectMode GpuDirect { get; set; } = GpuDirectMode.Auto;
    /// <summary>Adaptive window: grows when workers wait on a full window and budget allows, shrinks under memory pressure.</summary>
    public bool AdaptiveWindow { get; set; } = true;
    /// <summary>Subtitle effect cache (<see cref="TextCache"/>). Set only through PR_TEXTCACHE=0; not saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool TextCache { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public static string FilePath => Path.Combine(Path.GetDirectoryName(typeof(ParallelSettings).Assembly.Location) ?? "", "ParallelRender.settings.json");

    public static ParallelSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ParallelSettings>(File.ReadAllText(FilePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (Exception ex) { Log.Write("settings load failed: " + ex.Message); }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Log.Write("settings save failed: " + ex.Message); }
    }

    public ParallelSettings Clone() => (ParallelSettings)MemberwiseClone();

    /// <summary>
    /// Returns a copy with environment overrides applied. Variables read here:
    ///   PR_DISABLE=1          sequential output through the same encoder
    ///   PR_P=n                worker count (default: min(4, logical CPUs))
    ///   PR_CHUNK=n            chunk length in frames
    ///   PR_ENC_SESSIONS=n     NVENC sessions (1..8)
    ///   PR_GPU_DIRECT=0|1|auto  GPU-direct hand-off off / forced on / automatic
    ///   PR_CODEC=h264|hevc|av1, PR_QUALITY=speed|balanced|quality, PR_BITRATE=kbps (implies fixed rate control),
    ///   PR_RC=fixed|variable|youtuberecommended
    ///                         encoder options for the headless --encode path, which cannot open the settings screen
    ///   PR_TEXTCACHE=0        no subtitle effect cache
    /// PR_LOG is read by Log.
    /// </summary>
    public ParallelSettings WithEnvironment()
    {
        var s = Clone();
        static string? E(string n) { var v = Environment.GetEnvironmentVariable(n); return string.IsNullOrWhiteSpace(v) ? null : v.Trim(); }
        if (E("PR_DISABLE") == "1") s.Enabled = false;
        if (E("PR_P") is { } p && int.TryParse(p, out var pi)) s.Parallelism = pi;
        if (E("PR_CHUNK") is { } c && int.TryParse(c, out var ci) && ci > 0) s.ChunkFrames = ci;
        if (E("PR_ENC_SESSIONS") is { } es && int.TryParse(es, out var esi) && esi >= 1) s.EncSessions = Math.Min(esi, 8);
        if (E("PR_CODEC") is { } co) s.Codec = co.ToLowerInvariant() switch { "hevc" or "h265" => NvencCodec.H265, "av1" => NvencCodec.AV1, _ => NvencCodec.H264 };
        if (E("PR_QUALITY") is { } q && Enum.TryParse<NvencQuality>(q, true, out var qe)) s.Quality = qe;
        if (E("PR_BITRATE") is { } b && int.TryParse(b, out var bi)) { s.BitrateKbps = bi; s.RateControl = NvencRateControl.Fixed; }
        if (E("PR_RC") is { } rc && Enum.TryParse<NvencRateControl>(rc, true, out var rce)) s.RateControl = rce;
        if (E("PR_GPU_DIRECT") is { } gd) s.GpuDirect = gd switch { "1" => GpuDirectMode.On, "0" => GpuDirectMode.Off, _ => s.GpuDirect };
        if (E("PR_TEXTCACHE") == "0") s.TextCache = false;
        return s;
    }

    public int EffectiveParallelism => Parallelism > 0 ? Math.Clamp(Parallelism, 1, 32) : Math.Min(4, Environment.ProcessorCount);
}
