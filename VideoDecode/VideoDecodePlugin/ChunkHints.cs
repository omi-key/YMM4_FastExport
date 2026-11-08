using System;
using System.Collections.Concurrent;
using FFmpeg.AutoGen;
using YukkuriMovieMaker.Plugin.FileSource.FFmpeg;
using YukkuriMovieMaker.Plugin.FileSource.FFmpeg.Utilities;

namespace VideoDecode;

/// <summary>
/// Keyframe positions for ParallelRender, which places its chunk boundaries so that each chunk starts close after a
/// keyframe (less decoding after the seek). Published through AppDomain data "VideoDecode.KeyframeProbe".
/// </summary>
internal static class ChunkHints
{
    public const string ProbeKey = "VideoDecode.KeyframeProbe";

    private static readonly ConcurrentDictionary<string, long[]?> ProbeCache = new();

    internal static void RegisterProbe()
    {
        AppDomain.CurrentDomain.SetData(ProbeKey, (Func<string, long[]?>)Probe);
    }

    /// <summary>path -> [frame length, source length, keyframe times...] in 100 ns units, packed in one long[] so the type can cross AppDomain data; null if the file cannot be opened or is not H.264/HEVC.</summary>
    private static unsafe long[]? Probe(string path)
        => ProbeCache.GetOrAdd(path, p =>
        {
            try
            {
                FFmpegModule.EnsureInitialized(FFmpegResourceLocator.GetFFmpegDllDirectory());
                AVFormatContext* f = null;
                if (ffmpeg.avformat_open_input(&f, p, null, null) < 0) return null;
                try
                {
                    if (ffmpeg.avformat_find_stream_info(f, null) < 0) return null;
                    AVCodec* codec = null;
                    int si = ffmpeg.av_find_best_stream(f, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
                    if (si < 0 || codec == null) return null;
                    AVStream* st = f->streams[si];
                    if (st->codecpar->codec_id != AVCodecID.AV_CODEC_ID_H264 && st->codecpar->codec_id != AVCodecID.AV_CODEC_ID_HEVC) return null;
                    AVRational fps = st->avg_frame_rate;
                    if (fps.num <= 0 || fps.den <= 0) fps = st->r_frame_rate;
                    if (fps.num <= 0 || fps.den <= 0) return null;
                    var tb = new AVRational { num = 1, den = 10_000_000 };
                    long start = st->start_time != ffmpeg.AV_NOPTS_VALUE && st->start_time > 0 ? ffmpeg.av_rescale_q(st->start_time, st->time_base, tb) : 0;
                    long abs = st->duration > 0 ? ffmpeg.av_rescale_q(st->duration, st->time_base, tb)
                        : f->duration > 0 ? ffmpeg.av_rescale_q(f->duration, ffmpeg.av_get_time_base_q(), tb) : 0;
                    if (abs <= 0) return null;
                    long dur = abs - start > 0 ? abs - start : abs;   // same rule as SkipVideoFileSource.SpanFromAbsoluteTicks
                    var kf = Keyframes(f, si);
                    if (kf.Length == 0) return null;
                    var r = new long[2 + kf.Length];
                    r[0] = 10_000_000L * fps.den / fps.num; r[1] = dur;
                    Array.Copy(kf, 0, r, 2, kf.Length);
                    return r;
                }
                finally { ffmpeg.avformat_close_input(&f); }
            }
            catch { return null; }
        });

    /// <summary>Keyframe times (100 ns, same coordinates as stream pts) from the demuxer index.</summary>
    private static unsafe long[] Keyframes(AVFormatContext* fmt, int streamIndex)
    {
        AVStream* st = fmt->streams[streamIndex];
        int n = ffmpeg.avformat_index_get_entries_count(st);
        var list = new System.Collections.Generic.List<long>();
        var tb = new AVRational { num = 1, den = 10_000_000 };
        for (int i = 0; i < n; i++)
        {
            AVIndexEntry* e = ffmpeg.avformat_index_get_entry(st, i);
            if (e == null) continue;
            // AVIndexEntry.flags is a C bitfield that FFmpeg.AutoGen does not expose: the low bits of the int after the
            // 8-byte pos and timestamp. 1 = AVINDEX_KEYFRAME.
            if (((*(int*)((byte*)e + 16)) & 1) == 0) continue;
            list.Add(ffmpeg.av_rescale_q(e->timestamp, st->time_base, tb));
        }
        list.Sort();
        return list.ToArray();
    }
}
