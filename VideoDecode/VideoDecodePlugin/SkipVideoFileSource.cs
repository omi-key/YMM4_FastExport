using System;
using System.IO;
using System.Threading;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource;
using YukkuriMovieMaker.Plugin.FileSource.FFmpeg;
using YukkuriMovieMaker.Plugin.FileSource.FFmpeg.Utilities;

namespace VideoDecode;

/// <summary>
/// Same look as the standard FFmpeg video source (D3D11VA decode + D3D11 VideoProcessor conversion), but frames whose display
/// ends before the requested time are not decoded: packets well before it are sent with skip_frame = AVDISCARD_NONREF, so only
/// non-reference frames are dropped by the decoder. Near the requested time the decoder is back in normal mode, so every displayed
/// frame is decoded the standard way. The end-of-stream case that shows the "last discarded frame" is redone without skipping.
/// </summary>
internal sealed unsafe class SkipVideoFileSource : IVideoFileSource
{
    private const int AV_PIX_FMT_D3D11 = 171;       // AVPixelFormat.AV_PIX_FMT_D3D11
    private const int FrameFlagInterlaced = 8;      // AV_FRAME_FLAG_INTERLACED
    private const uint DecoderShaderBindFlags = 520u; // D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_DECODER
    private const long TicksPerSecond = 10_000_000L;
    private static readonly AVRational TickBase = new() { num = 1, den = 10_000_000 };
    private static readonly TimeSpan MaxSeekBackoff = TimeSpan.FromSeconds(64);

    private static readonly ConcurrentDictionary<string, byte> loggedRejects = new();
    // Files whose first-frame probe already succeeded (same size/mtime/dimensions), keyed by path.
    private static readonly ConcurrentDictionary<string, string> ProbedOk = new();

    private readonly IGraphicsDevicesAndContext devices;
    private readonly DisposeCollector disposer = new();
    private string path = "";

    private DecodeKit? kit;
    private GraphicsDevices? decodeDevices;
    private IGraphicsDevicesAndContext? decodeContext;
    private CrossDeviceFrameBridge? bridge;
    private FFmpegD3D11VideoProcessor? gpuProcessor;
    // Decode and convert directly on the render device (no cross-device copy, no DecodeKit). Decided once by SameDevPolicy.
    private readonly bool SameDev;
    private SameDevProcessor? sameDevProcessor;

    private ID2D1Bitmap? bitmap;
    private Premultiply? premultiplyEffect;
    private AffineTransform2D? centeringEffect;
    private ID2D1Image? output;

    private AVFormatContext* fmt;
    private AVCodecContext* codecCtx;
    private AVBufferRef* hwDeviceCtx;
    private AVFrame* frame;
    private AVPacket* packet;
    private int videoStreamIndex = -1;
    private string fmtSig = "";
    private bool openedOk;
    private AVRational timeBase;
    private AVCodecContext_get_format? getFormatDelegate;
    private bool hwFormatNegotiated;
    private bool pendingResend;

    private int width, height, displayWidth;
    private double pixelAspectRatioX = 1.0;
    private int fpsNumerator = 30, fpsDenominator = 1;
    private TimeSpan keyFrameSpan = TimeSpan.FromSeconds(2);
    private TimeSpan streamStartTime = TimeSpan.Zero;
    private AVColorSpace streamColorSpace;
    private AVColorPrimaries streamColorPrimaries;
    private AVColorRange streamColorRange;

    private TimeSpan currentTime = TimeSpan.FromTicks(-1);
    private TimeSpan currentDuration = TimeSpan.Zero;
    private bool isFirstUpdate = true;
    private bool isCleared;
    private bool skipActive;
    private bool skippedSinceSeek;
    private long skipLimitTicks;      // latest display end of any skipped packet; a later request before this needs the skipped frames
    private TimeSpan lastUpdateTime = TimeSpan.FromTicks(-1);
    private long predictedStepTicks;  // step since the previous Update; the next request is assumed to advance by the same amount
    private long minSkipStart = long.MaxValue;   // smallest start of packets skipped as "after T" in this DecodeUntil
    private byte[] zeroBuffer = Array.Empty<byte>();
    private bool disposed;

    public TimeSpan Duration { get; private set; }
    public ID2D1Image Output => output!;

    private TimeSpan EndTimeAbs => streamStartTime + Duration;
    private long NominalFrameTicks => fpsNumerator > 0 ? TicksPerSecond * fpsDenominator / fpsNumerator : 333333;

    private SkipVideoFileSource(IGraphicsDevicesAndContext devices)
    {
        this.devices = devices;
        SameDev = SameDevPolicy.For(devices);
    }

    // ---------------------------------------------------------------- creation

    public static SkipVideoFileSource? TryCreate(IGraphicsDevicesAndContext devices, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
        var source = new SkipVideoFileSource(devices) { path = filePath };
        bool ok = false;
        try
        {
            ok = source.Open(filePath, out string reason);
            if (!ok) LogReject(filePath, reason);
        }
        catch (Exception ex)
        {
            LogReject(filePath, "exception " + ex.Message);
        }
        if (!ok)
        {
            source.Dispose();
            return null;
        }
        source.openedOk = true;
        return source;
    }

    private static void LogReject(string filePath, string reason)
    {
        if (loggedRejects.TryAdd(filePath, 0))
            Log.Default.Write($"VideoDecode: deferring to the standard source ({reason}) path=\"{filePath}\"");
    }

    private bool Open(string filePath, out string reason)
    {
        reason = "";
        FFmpegModule.EnsureInitialized(FFmpegResourceLocator.GetFFmpegDllDirectory());

        fmtSig = FormatPool.Signature(filePath);
        nint pooled = FormatPool.Rent(filePath, fmtSig);
        if (pooled != 0)
        {
            fmt = (AVFormatContext*)pooled;   // reopen of the same file: reuse the parsed state; the first seek sets the position
        }
        else
        {
            AVFormatContext* f = null;
            if (ffmpeg.avformat_open_input(&f, filePath, null, null) < 0) { reason = "open_input"; return false; }
            fmt = f;
            string container = Marshal.PtrToStringAnsi((nint)fmt->iformat->name) ?? "";
            if (!container.StartsWith("mov,mp4", StringComparison.Ordinal) && !container.StartsWith("matroska", StringComparison.Ordinal))
            { reason = "container=" + container; return false; }
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) { reason = "find_stream_info"; return false; }
        }

        AVCodec* codec = null;
        videoStreamIndex = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
        if (videoStreamIndex < 0 || codec == null) { reason = "no video stream"; return false; }
        AVStream* stream = fmt->streams[videoStreamIndex];
        AVCodecParameters* par = stream->codecpar;

        if (par->codec_id != AVCodecID.AV_CODEC_ID_H264 && par->codec_id != AVCodecID.AV_CODEC_ID_HEVC)
        { reason = "codec=" + par->codec_id; return false; }
        if ((AVPixelFormat)par->format != AVPixelFormat.AV_PIX_FMT_YUV420P)
        { reason = "pix_fmt=" + (AVPixelFormat)par->format; return false; }
        if (par->color_trc == AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 || par->color_trc == AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67)
        { reason = "HDR"; return false; }
        if (par->field_order != AVFieldOrder.AV_FIELD_UNKNOWN && par->field_order != AVFieldOrder.AV_FIELD_PROGRESSIVE)
        { reason = "field_order=" + par->field_order; return false; }
        if (par->nb_coded_side_data > 0 && par->coded_side_data != null &&
            ffmpeg.av_packet_side_data_get(par->coded_side_data, par->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX) != null)
        { reason = "rotation (display matrix)"; return false; }

        timeBase = stream->time_base;
        if (stream->start_time != ffmpeg.AV_NOPTS_VALUE && stream->start_time > 0)
            streamStartTime = TimeSpan.FromTicks(ffmpeg.av_rescale_q(stream->start_time, timeBase, TickBase));

        streamColorSpace = par->color_space;
        streamColorPrimaries = par->color_primaries;
        streamColorRange = par->color_range;

        codecCtx = ffmpeg.avcodec_alloc_context3(codec);
        if (codecCtx == null) { reason = "alloc_context"; return false; }
        if (ffmpeg.avcodec_parameters_to_context(codecCtx, par) < 0) { reason = "parameters_to_context"; return false; }
        codecCtx->pkt_timebase = timeBase;
        codecCtx->extra_hw_frames = 3;   // headroom for the frame held back by the end-of-stream fallback

        if (!SetupD3D11Decode(codec, out reason)) return false;
        // Frame threading delays output by the thread count, which blurs which frames can be dropped. Does not affect pixels.
        codecCtx->thread_count = 1;
        if (ffmpeg.avcodec_open2(codecCtx, codec, null) < 0) { reason = "avcodec_open2"; return false; }

        width = codecCtx->width > 0 ? codecCtx->width : par->width;
        height = codecCtx->height > 0 ? codecCtx->height : par->height;
        if (width <= 0 || height <= 0) { reason = "size"; return false; }

        displayWidth = width;
        AVRational sar = ffmpeg.av_guess_sample_aspect_ratio(fmt, stream, null);
        if (sar.num > 0 && sar.den > 0 && sar.num != sar.den)
        {
            pixelAspectRatioX = (double)sar.num / sar.den;
            displayWidth = Math.Max(1, (int)Math.Round(width * pixelAspectRatioX));
        }

        AVRational fps = stream->avg_frame_rate;
        if (fps.num <= 0 || fps.den <= 0) fps = stream->r_frame_rate;
        if (fps.num > 0 && fps.den > 0) { fpsNumerator = fps.num; fpsDenominator = fps.den; }

        if (stream->duration > 0)
            Duration = SpanFromAbsoluteTicks(ffmpeg.av_rescale_q(stream->duration, timeBase, TickBase));
        else if (fmt->duration > 0)
            Duration = SpanFromAbsoluteTicks(ffmpeg.av_rescale_q(fmt->duration, ffmpeg.av_get_time_base_q(), TickBase));
        else if (stream->nb_frames > 0 && fpsNumerator > 0 && fpsDenominator > 0)
            Duration = TimeSpan.FromTicks(stream->nb_frames * TicksPerSecond * fpsDenominator / fpsNumerator);
        else { reason = "duration unknown"; return false; }

        long nbFrames = stream->nb_frames;
        if (nbFrames <= 0 && Duration > TimeSpan.Zero)
            nbFrames = (long)Math.Round(Duration.TotalSeconds * fpsNumerator / fpsDenominator);
        if (nbFrames == 1) { reason = "single frame"; return false; }

        long gopTicks = (long)(codecCtx->gop_size > 0 ? codecCtx->gop_size : 60) * TicksPerSecond * fpsDenominator / fpsNumerator;
        keyFrameSpan = TimeSpan.FromTicks(Math.Max(gopTicks, TicksPerSecond));
        frame = ffmpeg.av_frame_alloc();
        packet = ffmpeg.av_packet_alloc();
        if (frame == null || packet == null) { reason = "alloc"; return false; }

        CreateOutputResources();

        // Decode the first frame to confirm HW decode is active, the stream is progressive and a VideoProcessor can be created.
        // The result is cached per file (size, mtime, dimensions).
        string sig = "";
        try { var fi = new FileInfo(path); sig = fi.Length + ":" + fi.LastWriteTimeUtc.Ticks + ":" + width + "x" + height; } catch { }
        bool pr;
        if (sig.Length > 0 && ProbedOk.TryGetValue(path, out var prev) && prev == sig) { pr = true; reason = ""; }
        else
        {
            pr = Probe(out reason);
            if (pr && sig.Length > 0) ProbedOk[path] = sig;
        }
        return pr;
    }

    private TimeSpan SpanFromAbsoluteTicks(long ticks)
    {
        long t = ticks - streamStartTime.Ticks;
        return TimeSpan.FromTicks(t > 0 ? t : ticks);
    }

    // ---------------------------------------------------------------- HW decode setup (D3D11VA)

    private bool SetupD3D11Decode(AVCodec* codec, out string reason)
    {
        reason = "";
        bool found = false;
        for (int i = 0; ; i++)
        {
            AVCodecHWConfig* cfg = ffmpeg.avcodec_get_hw_config(codec, i);
            if (cfg == null) break;
            if ((cfg->methods & 1) != 0 && cfg->device_type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA && (int)cfg->pix_fmt == AV_PIX_FMT_D3D11)
            { found = true; break; }
        }
        if (!found) { reason = "no D3D11VA config"; return false; }

        if (!SameDev)
        {
            kit = DecodeKitPool.Rent();
            decodeDevices = kit.Devices;
            decodeContext = kit.Context;
            bridge = new CrossDeviceFrameBridge(decodeDevices, devices);
        }

        AVBufferRef* dev = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (dev == null) { reason = "hwdevice alloc"; return false; }
        AVHWDeviceContext* data = (AVHWDeviceContext*)dev->data;
        AVD3D11VADeviceContext* hwctx = (AVD3D11VADeviceContext*)data->hwctx;
        if (SameDev) devices.D3D.Multithread.SetMultithreadProtected(true);
        nint native = SameDev ? devices.D3D.Device.NativePointer : decodeContext!.D3D.Device.NativePointer;
        Marshal.AddRef(native);
        hwctx->device = (ID3D11Device*)native.ToPointer();
        if (ffmpeg.av_hwdevice_ctx_init(dev) < 0)
        {
            ffmpeg.av_buffer_unref(&dev);
            reason = "hwdevice init";
            return false;
        }
        hwDeviceCtx = dev;
        codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(dev);
        getFormatDelegate = GetHwFormat;
        codecCtx->get_format = new AVCodecContext_get_format_func { Pointer = Marshal.GetFunctionPointerForDelegate(getFormatDelegate) };
        return true;
    }

    private AVPixelFormat GetHwFormat(AVCodecContext* ctx, AVPixelFormat* fmts)
    {
        for (AVPixelFormat* p = fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if ((int)*p != AV_PIX_FMT_D3D11) continue;
            AssignD3D11FramesContext(ctx);
            hwFormatNegotiated = ctx->hw_frames_ctx != null;
            return *p;
        }
        return *fmts;   // no HW format offered: stay in software; Probe rejects it
    }

    private static void AssignD3D11FramesContext(AVCodecContext* ctx)
    {
        if (ctx->hw_device_ctx == null) return;
        AVBufferRef* frames = null;
        if (ffmpeg.avcodec_get_hw_frames_parameters(ctx, ctx->hw_device_ctx, AVPixelFormat.AV_PIX_FMT_D3D11, &frames) < 0 || frames == null) return;
        AVHWFramesContext* fc = (AVHWFramesContext*)frames->data;
        AVD3D11VAFramesContext* hw = (AVD3D11VAFramesContext*)fc->hwctx;
        hw->BindFlags |= DecoderShaderBindFlags;
        if (ffmpeg.av_hwframe_ctx_init(frames) < 0)
        {
            ffmpeg.av_buffer_unref(&frames);
            return;
        }
        if (ctx->hw_frames_ctx != null) ffmpeg.av_buffer_unref(&ctx->hw_frames_ctx);
        ctx->hw_frames_ctx = frames;
    }

    // ---------------------------------------------------------------- output resources (D2D)

    private void CreateOutputResources()
    {
        var dc = devices.DeviceContext;
        bitmap = dc.CreateBitmap(new SizeI(width, height), IntPtr.Zero, 0,
            new BitmapProperties(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
        disposer.Collect(bitmap);
        premultiplyEffect = new Premultiply(dc);
        disposer.Collect(premultiplyEffect);
        premultiplyEffect.SetInput(0, bitmap, true);
        centeringEffect = new AffineTransform2D(dc);
        disposer.Collect(centeringEffect);
        using (ID2D1Image input = premultiplyEffect.Output)
            centeringEffect.SetInput(0, input, true);
        // Rotated sources are rejected in Open, so only pixel aspect ratio and centering are needed.
        Matrix3x2 scale = Matrix3x2.CreateScale((float)pixelAspectRatioX, 1f);
        Matrix3x2 center = Matrix3x2.CreateTranslation(MathF.Floor(-displayWidth / 2f), MathF.Floor(-height / 2f));
        centeringEffect.TransformMatrix = scale * center;
        output = centeringEffect.Output;
        disposer.Collect(output);
    }

    private bool Probe(out string reason)
    {
        reason = "";
        SeekTo(streamStartTime, TimeSpan.Zero);
        bool got = false;
        bool eof = false;
        for (int n = 0; n < 4096 && !got && !eof; n++)
        {
            ffmpeg.av_packet_unref(packet);
            if (ffmpeg.av_read_frame(fmt, packet) < 0) { ffmpeg.avcodec_send_packet(codecCtx, null); eof = true; }
            else if (packet->stream_index != videoStreamIndex) continue;
            else if (ffmpeg.avcodec_send_packet(codecCtx, packet) < 0) continue;
            while (ffmpeg.avcodec_receive_frame(codecCtx, frame) == 0)
            {
                got = true;
                try
                {
                    if (frame->format != AV_PIX_FMT_D3D11 || !hwFormatNegotiated) { reason = "HW decode not active"; return false; }
                    if ((frame->flags & FrameFlagInterlaced) != 0) { reason = "interlaced"; return false; }
                    if (!EnsureProcessor(frame) || !(SameDev ? sameDevProcessor!.ProcessFrame((nint)frame->data[0], (uint)frame->data[1]) : gpuProcessor!.ProcessFrame((nint)frame->data[0], (uint)frame->data[1])))
                    { reason = "VideoProcessor"; return false; }
                }
                finally { ffmpeg.av_frame_unref(frame); }
                break;
            }
        }
        if (!got) { reason = "no frame"; return false; }
        ffmpeg.av_packet_unref(packet);
        pendingResend = false;
        ffmpeg.avcodec_flush_buffers(codecCtx);
        return true;
    }

    // ---------------------------------------------------------------- IVideoFileSource

    public int GetFrameIndex(TimeSpan time) => FrameTime.TimeToFrame(time, fpsNumerator, fpsDenominator);

    public void Update(TimeSpan time)
    {
        if (disposed || codecCtx == null) return;
        time += streamStartTime;
        if (time < streamStartTime || Duration <= TimeSpan.Zero)
        {
            ClearBitmap();
            currentTime = time;
            currentDuration = TimeSpan.Zero;
            return;
        }
        if (time >= EndTimeAbs)
        {
            // Past the end, hold the last frame at the same position as the standard source.
            long half = Math.Max(NominalFrameTicks / 2, 1);
            TimeSpan t = EndTimeAbs - TimeSpan.FromTicks(half);
            time = t < streamStartTime ? streamStartTime : t;
        }
        // Assume the next request advances by the same step and skip frames that will have ended by then.
        predictedStepTicks = lastUpdateTime.Ticks >= 0 ? Math.Clamp((time - lastUpdateTime).Ticks, 0, keyFrameSpan.Ticks) : 0;
        lastUpdateTime = time;
        if (!isFirstUpdate && currentDuration > TimeSpan.Zero && currentTime <= time && time < currentTime + currentDuration)
        {
            return;
        }

        bool needSeek = isFirstUpdate || currentDuration == TimeSpan.Zero || time < currentTime
                        || currentTime + currentDuration + keyFrameSpan < time;
        if (needSeek) predictedStepTicks = 0;   // no step prediction right after a jump
        isFirstUpdate = false;
        try
        {
            bool retry;
            if (needSeek)
            {
                retry = SeekAndDecode(time, true);
            }
            else if (time.Ticks < skipLimitTicks)
            {
                // The request lags the prediction: skipped frames may now be needed, so decode again without skipping.
                retry = SeekAndDecode(time, false);
            }
            else DecodeUntil(time, true, out retry);
            if (retry)
            {
                // Reached the end after skipping with nothing to show: a skipped frame may stand in for the last frame,
                // so redo without skipping.
                SeekAndDecode(time, false);
            }
        }
        catch (Exception ex)
        {
            Log.Default.Write("VideoDecode: Update exception: " + ex.Message, ex);
            ClearBitmap();
            currentTime = time;
            currentDuration = TimeSpan.Zero;
        }
    }

    /// <returns>whether the caller must redo without skipping</returns>
    private bool SeekAndDecode(TimeSpan time, bool allowSkip)
    {
        TimeSpan backoff = TimeSpan.Zero;
        bool atStart = SeekTo(time, backoff);
        bool ok = DecodeUntil(time, allowSkip, out bool retry);
        while (!retry && !ok && !atStart)
        {
            backoff = backoff > TimeSpan.Zero ? backoff + backoff : TimeSpan.FromSeconds(2);
            if (backoff > MaxSeekBackoff) break;
            atStart = SeekTo(time, backoff);
            ok = DecodeUntil(time, allowSkip, out retry);
        }
        return retry;
    }

    /// <returns>whether the seek went to the start of the stream</returns>
    private bool SeekTo(TimeSpan time, TimeSpan backoff)
    {
        skippedSinceSeek = false;
        skipLimitTicks = 0;
        long target = Math.Max(streamStartTime.Ticks, time.Ticks - backoff.Ticks);
        bool atStart = target <= streamStartTime.Ticks;
        long ts = atStart ? 0 : ffmpeg.av_rescale_q(target, TickBase, timeBase);
        ffmpeg.avformat_seek_file(fmt, videoStreamIndex, long.MinValue, ts, ts, 1 /* AVSEEK_FLAG_BACKWARD */);
        ffmpeg.avcodec_flush_buffers(codecCtx);
        if (pendingResend)
        {
            ffmpeg.av_packet_unref(packet);
            pendingResend = false;
        }
        return atStart;
    }

    /// <summary>
    /// Decodes forward from the current position, discarding frames whose display ended at or before the requested time, and
    /// converts the one frame to display.
    /// </summary>
    /// <param name="retryWithoutSkip">skipping left nothing to display; redo without skipping</param>
    /// <returns>same meaning as the standard DecodeUntil: a frame starting at or before the time was found (discarded frames count)</returns>
    private bool DecodeUntil(TimeSpan time, bool allowSkip, out bool retryWithoutSkip)
    {
        retryWithoutSkip = false;
        minSkipStart = long.MaxValue;
        bool eof = false;
        int eagain = 0;
        AVFrame* fb = null;      // last discarded frame, shown if the stream ends first
        bool fbValid = false;
        long fbTicks = 0, fbDurTicks = 0;
        long nominal = NominalFrameTicks;
        try
        {
            do
            {
                if (!eof)
                {
                    if (!pendingResend)
                    {
                        ffmpeg.av_packet_unref(packet);
                        int rr = ffmpeg.av_read_frame(fmt, packet);
                        if (rr < 0)
                        {
                            ffmpeg.avcodec_send_packet(codecCtx, null);
                            eof = true;
                        }
                        else if (packet->stream_index != videoStreamIndex)
                        {
                            continue;
                        }
                    }
                    if (!eof)
                    {
                        bool skip = allowSkip && ShouldSkip(packet, time.Ticks, time.Ticks + predictedStepTicks, nominal);
                        SetSkip(skip);
                        int sr = ffmpeg.avcodec_send_packet(codecCtx, packet);
                        if (sr == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                        {
                            pendingResend = true;
                            eagain++;
                        }
                        else if (sr < 0)
                        {
                            pendingResend = false;
                            continue;   // drop packets that cannot be sent, like the standard source
                        }
                        else
                        {
                            pendingResend = false;
                            if (skip) skippedSinceSeek = true;
                        }
                    }
                }

                while (true)
                {
                    int rv = ffmpeg.avcodec_receive_frame(codecCtx, frame);
                    if (rv == ffmpeg.AVERROR(ffmpeg.EAGAIN)) break;
                    if (rv == ffmpeg.AVERROR_EOF)
                    {
                        return FinishWithoutFrame(fb, fbValid, fbTicks, fbDurTicks, time, out retryWithoutSkip);
                    }
                    if (rv < 0) break;
                    try
                    {
                        long pts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
                        bool hasPts = pts != ffmpeg.AV_NOPTS_VALUE;
                        long startTicks = hasPts ? ffmpeg.av_rescale_q(pts, timeBase, TickBase) : (currentTime.Ticks + currentDuration.Ticks);
                        long durTicks;
                        if (frame->duration > 0)
                        {
                            durTicks = ffmpeg.av_rescale_q(frame->duration, timeBase, TickBase);
                            if (durTicks <= 0) durTicks = nominal;
                        }
                        else durTicks = nominal;
                        long endTicks = hasPts && frame->duration > 0 ? ffmpeg.av_rescale_q(pts + frame->duration, timeBase, TickBase) : startTicks + durTicks;
                        if (endTicks <= startTicks) endTicks = startTicks + durTicks;
                        var frameTime = TimeSpan.FromTicks(startTicks);
                        var frameEnd = TimeSpan.FromTicks(endTicks);

                        if (frameEnd <= time)
                        {
                            // Already ended: discard, keeping only the last one for the end-of-stream case.
                            if (fb == null) fb = ffmpeg.av_frame_alloc();
                            if (fb != null)
                            {
                                ffmpeg.av_frame_unref(fb);
                                fbValid = ffmpeg.av_frame_ref(fb, frame) >= 0;
                                if (fbValid) { fbTicks = startTicks; fbDurTicks = durTicks; }
                            }
                            continue;
                        }
                        if (minSkipStart < startTicks)
                        {
                            // A frame that should precede the displayed one was skipped (T fell in a VFR gap): redo.
                            retryWithoutSkip = true;
                            return false;
                        }
                        Upload(frame);
                        currentTime = frameTime <= time ? frameTime : time;
                        currentDuration = frameEnd - currentTime;
                        isCleared = false;
                        return fbValid || skippedSinceSeek || frameTime <= time;
                    }
                    finally
                    {
                        ffmpeg.av_frame_unref(frame);
                    }
                }
            } while (!eof);
            return FinishWithoutFrame(fb, fbValid, fbTicks, fbDurTicks, time, out retryWithoutSkip);
        }
        finally
        {
            if (fb != null) ffmpeg.av_frame_free(&fb);
        }
    }

    /// <summary>Reached the end with no frame to display.</summary>
    private bool FinishWithoutFrame(AVFrame* fb, bool fbValid, long fbTicks, long fbDurTicks, TimeSpan time, out bool retryWithoutSkip)
    {
        retryWithoutSkip = false;
        if (skippedSinceSeek)
        {
            // Some discarded frames were never decoded, so none can stand in for the last frame.
            retryWithoutSkip = true;
            return false;
        }
        if (fb != null && fbValid)
        {
            TimeSpan dur = TimeSpan.FromTicks(fbDurTicks);
            Upload(fb);
            TimeSpan fbTime = TimeSpan.FromTicks(fbTicks);
            currentTime = fbTime <= time ? fbTime : time;
            TimeSpan end = fbTime + dur;
            if (EndTimeAbs > end) end = EndTimeAbs;
            if (end <= time) end = time + TimeSpan.FromTicks(1);
            currentDuration = end - currentTime;
            if (currentDuration <= TimeSpan.Zero) currentDuration = TimeSpan.FromTicks(1);
            isCleared = false;
            return true;
        }
        ClearBitmap();
        currentTime = time;
        currentDuration = TimeSpan.Zero;
        return false;
    }

    /// <summary>
    /// Whether the frame in this packet may go undecoded. Its display span is estimated as [pts, pts + packet duration)
    /// (one nominal frame if the duration is missing).
    ///  - Ends by the requested time T: the standard source discards it too, so skipping is safe.
    ///  - Starts after T but ends by the predicted next request T': it is not the frame to display now. A wrong prediction is
    ///    caught by the next Update via skipLimitTicks; a skipped frame that was needed because T fell in a VFR gap is caught by
    ///    DecodeUntil via minSkipStart. Both redo without skipping.
    /// </summary>
    private bool ShouldSkip(AVPacket* pkt, long timeTicks, long predictedTicks, long nominalTicks)
    {
        if (pkt->pts == ffmpeg.AV_NOPTS_VALUE) return false;
        long start = ffmpeg.av_rescale_q(pkt->pts, timeBase, TickBase);
        long margin;
        long dur;
        if (pkt->duration > 0)
        {
            dur = ffmpeg.av_rescale_q(pkt->duration, timeBase, TickBase);
            margin = 4;   // rounding of the time-base conversion
        }
        else
        {
            dur = nominalTicks;
            margin = nominalTicks;
        }
        if (dur <= 0) { dur = nominalTicks; margin = nominalTicks; }
        long end = start + dur + margin;
        if (end <= timeTicks) return true;
        if (start >= timeTicks + nominalTicks && end <= predictedTicks)
        {
            if (end > skipLimitTicks) skipLimitTicks = end;
            if (start < minSkipStart) minSkipStart = start;
            return true;
        }
        return false;
    }

    private void SetSkip(bool skip)
    {
        if (skip == skipActive) return;
        codecCtx->skip_frame = skip ? AVDiscard.AVDISCARD_NONREF : AVDiscard.AVDISCARD_DEFAULT;
        skipActive = skip;
    }

    // ---------------------------------------------------------------- colour conversion (D3D11 VideoProcessor, as in the standard source)

    private bool EnsureProcessor(AVFrame* src)
    {
        var cs = ResolveD3D11ColorSpace(src);
        if (SameDev)
        {
            if (sameDevProcessor == null)
            {
                try { sameDevProcessor = new SameDevProcessor(devices, src->width, src->height, width, height, cs); }
                catch (Exception ex)
                {
                    Log.Default.Write("VideoDecode: SameDevProcessor init failed: " + ex.Message, ex);
                    return false;
                }
            }
            sameDevProcessor.UpdateInputColorSpace(cs);
            return true;
        }
        if (gpuProcessor == null)
        {
            try
            {
                gpuProcessor = new FFmpegD3D11VideoProcessor(decodeContext!, bridge!, src->width, src->height, width, height, cs);
            }
            catch (Exception ex)
            {
                Log.Default.Write("VideoDecode: VideoProcessor init failed: " + ex.Message, ex);
                return false;
            }
        }
        gpuProcessor.UpdateInputColorSpace(cs);
        return true;
    }

    private void Upload(AVFrame* src)
    {
        if (SameDev && src->format == AV_PIX_FMT_D3D11)
        {
            if (!(EnsureProcessor(src) && sameDevProcessor!.ProcessFrame((nint)src->data[0], (uint)src->data[1])))
                throw new InvalidOperationException("VideoProcessor failed to convert the frame");
            SetCenteringInput(sameDevProcessor.OutputBitmap);
            return;
        }
        if (SameDev) throw new InvalidOperationException("same-device mode handles HW frames only");
        if (src->format != AV_PIX_FMT_D3D11) throw new InvalidOperationException("HW frames only");
        if (!(EnsureProcessor(src) && gpuProcessor!.ProcessFrame((nint)src->data[0], (uint)src->data[1])))
        {
            if (kit != null) kit.Broken = true;
            throw new InvalidOperationException("VideoProcessor failed to convert the frame");
        }
        SetCenteringInput(gpuProcessor.OutputBitmap);
    }

    /// <summary>The input bitmap is reused; re-setting it and toggling BorderMode tells D2D the GPU rewrote its contents.</summary>
    private void SetCenteringInput(ID2D1Image? input)
    {
        centeringEffect!.SetInput(0, input, true);
        centeringEffect.BorderMode = BorderMode.Soft;
        centeringEffect.BorderMode = BorderMode.Hard;
    }

    private ColorSpaceType ResolveD3D11ColorSpace(AVFrame* src)
    {
        AVColorSpace cs = src->colorspace == AVColorSpace.AVCOL_SPC_UNSPECIFIED ? streamColorSpace : src->colorspace;
        AVColorPrimaries pri = src->color_primaries == AVColorPrimaries.AVCOL_PRI_UNSPECIFIED ? streamColorPrimaries : src->color_primaries;
        AVColorRange range = src->color_range == AVColorRange.AVCOL_RANGE_UNSPECIFIED ? streamColorRange : src->color_range;
        bool full = range == AVColorRange.AVCOL_RANGE_JPEG;

        // Fallback when the matrix is unspecified/unsupported, same as the standard source: BT.2020 primaries -> 2020, SD size -> 601, else 709.
        AVColorSpace matrix;
        switch (cs)
        {
            case AVColorSpace.AVCOL_SPC_BT709:
            case AVColorSpace.AVCOL_SPC_FCC:
            case AVColorSpace.AVCOL_SPC_BT470BG:
            case AVColorSpace.AVCOL_SPC_SMPTE170M:
            case AVColorSpace.AVCOL_SPC_SMPTE240M:
            case AVColorSpace.AVCOL_SPC_BT2020_NCL:
            case AVColorSpace.AVCOL_SPC_BT2020_CL:
                matrix = cs;
                break;
            default:
                if (((int)cs == 12 || (int)cs == 13) && pri == AVColorPrimaries.AVCOL_PRI_BT2020) matrix = AVColorSpace.AVCOL_SPC_BT2020_NCL;
                else if (width < 1280 && height <= 576) matrix = AVColorSpace.AVCOL_SPC_BT470BG;
                else matrix = AVColorSpace.AVCOL_SPC_BT709;
                break;
        }
        switch (matrix)
        {
            case AVColorSpace.AVCOL_SPC_FCC:
            case AVColorSpace.AVCOL_SPC_BT470BG:
            case AVColorSpace.AVCOL_SPC_SMPTE170M:
                return full ? ColorSpaceType.YcbcrFullG22LeftP601 : ColorSpaceType.YcbcrStudioG22LeftP601;
            case AVColorSpace.AVCOL_SPC_BT2020_NCL:
            case AVColorSpace.AVCOL_SPC_BT2020_CL:
                return full ? ColorSpaceType.YcbcrFullG22LeftP2020 : ColorSpaceType.YcbcrStudioG22LeftP2020;
            default:
                return full ? ColorSpaceType.YcbcrFullG22LeftP709 : ColorSpaceType.YcbcrStudioG22LeftP709;
        }
    }

    private void ClearBitmap()
    {
        if (isCleared || bitmap == null) return;
        int stride = width * 4;
        int size = stride * height;
        if (zeroBuffer.Length < size) zeroBuffer = new byte[size];
        fixed (byte* p = zeroBuffer) bitmap.CopyFromMemory((nint)p, stride);
        premultiplyEffect!.SetInput(0, bitmap, true);
        using (ID2D1Image input = premultiplyEffect.Output)
            SetCenteringInput(input);
        isCleared = true;
    }

    // ---------------------------------------------------------------- disposal

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        centeringEffect?.SetInput(0, null, true);
        premultiplyEffect?.SetInput(0, null, true);
        disposer.Dispose();

        sameDevProcessor?.Dispose();
        sameDevProcessor = null;
        gpuProcessor?.Dispose();
        gpuProcessor = null;
        if (packet != null) { AVPacket* p = packet; ffmpeg.av_packet_free(&p); packet = null; }
        if (frame != null) { AVFrame* f = frame; ffmpeg.av_frame_free(&f); frame = null; }
        if (codecCtx != null) { AVCodecContext* c = codecCtx; ffmpeg.avcodec_free_context(&c); codecCtx = null; }
        if (hwDeviceCtx != null) { AVBufferRef* d = hwDeviceCtx; ffmpeg.av_buffer_unref(&d); hwDeviceCtx = null; }
        if (fmt != null)
        {
            if (!(openedOk && FormatPool.Return(path, fmtSig, (nint)fmt))) { AVFormatContext* x = fmt; ffmpeg.avformat_close_input(&x); }
            fmt = null;
        }
        getFormatDelegate = null;
        bridge?.Dispose();
        bridge = null;
        decodeContext = null;
        decodeDevices = null;
        if (kit != null) { var k = kit; kit = null; DecodeKitPool.Return(k); }
    }
}
