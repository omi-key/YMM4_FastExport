// Encoder initialisation / bitrate / buffer-format logic adapted from the NVEnc plugin in this repository (NVEnc/, MIT, see LICENSE).
using System.IO;
using System.Runtime.InteropServices;
using NVEncVideoWriterPlugin;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using YukkuriMovieMaker.Project;

namespace ParallelRender.Encoding;

/// <summary>
/// Entry point to NVENC (NvencNative.dll). Owns a dedicated D3D11 device and input texture; CPU BGRA frames are
/// uploaded with UpdateSubresource and handed to NvencEncode. Submit must be called from one thread per session.
/// WriteAudio may be called from another thread (the native side serialises audio/video muxing).
/// </summary>
internal sealed class EncoderCore : IDisposable
{
    private readonly string _path;
    private readonly VideoInfo _vi;
    private readonly ParallelSettings _s;
    private readonly int _audioChannels = 2;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _ctx;
    private ID3D11Texture2D? _input;
    private IntPtr _handle;
    // Multiple sessions (PR_ENC_SESSIONS>=2): _handle is the primary (owns the mp4 mux), _members are extra sessions.
    // Chunk c of EncChunk frames goes to session c % Sessions with a forced IDR at the chunk start, and the primary
    // muxer orders packets by index (NvencEncodeEx sequence = index).
    private readonly List<IntPtr> _members = new();
    private ID3D11Texture2D?[] _inputs = [];     // per-session CPU upload targets
    private bool _ordered;
    private readonly object _handleLock = new();
    private bool _finalized;
    /// <summary>The encoder's D3D11 device (owner of the GPU-direct shared textures).</summary>
    public ID3D11Device? Device => _device;
    /// <summary>Sessions actually opened (1 = single session).</summary>
    public int Sessions { get; private set; } = 1;
    /// <summary>Frames per chunk assigned to a session; meaningless when Sessions == 1.</summary>
    public int EncChunk { get; private set; }

    public EncoderCore(string path, VideoInfo vi, ParallelSettings s)
    {
        _path = path; _vi = vi; _s = s;
        if ((vi.Width & 1) != 0 || (vi.Height & 1) != 0)
            throw new InvalidOperationException("NVENC は偶数サイズの解像度が必要です。");
        try { Initialize(); }
        catch { Dispose(); throw; }
    }

    private void Initialize()
    {
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.DisableGpuTimeout;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, null, out _device, out _ctx).CheckError();
        _input = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = _vi.Width, Height = _vi.Height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
        });

        var fps = Math.Max(1, _vi.FPS);
        var bitrate = TargetBitrateKbps();
        var codec = _s.Codec switch { NvencCodec.H265 => 1, NvencCodec.AV1 => 2, _ => 0 };
        var rc = _s.RateControl == NvencRateControl.Fixed ? 0 : 1;
        var maxBitrate = rc == 1 ? Math.Clamp((int)(bitrate * 1.2), 100, 300000) : bitrate;
        const int BufferFormatArgb = 0x01000000; // DXGI B8G8R8A8
        _handle = NvencNativeMethods.NvencCreateWithPool(_device.NativePointer, _vi.Width, _vi.Height, fps, bitrate,
            codec, (int)_s.Quality, 0, rc, maxBitrate, BufferFormatArgb,
            _s.Codec == NvencCodec.H265 ? 1 : 0, 0, _path, NvencPoolSize);
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("NVENC 初期化に失敗しました。");
        var err = LastError();
        if (!string.IsNullOrWhiteSpace(err)) throw new InvalidOperationException(Explain(err));
        if (_s.EncSessions > 1) TryOpenMembers();
    }

    /// <summary>Adds what the user can do about the NVENC errors that come from the environment rather than from a bug.</summary>
    private static string Explain(string err)
    {
        // NV_ENC_ERR_INVALID_VERSION: the driver's NVENC API is older than the one this DLL was built with
        if (err.Contains("failed (15)"))
            return err + "\nNVIDIA ドライバーが古いため、NVENC を開けません。ドライバーを更新してください (522.25 以降が必要です)。";
        // NV_ENC_ERR_NO_ENCODE_DEVICE / UNSUPPORTED_DEVICE: the D3D device is not an NVIDIA GPU
        if (err.Contains("failed (1)") || err.Contains("failed (2)"))
            return err + "\nYMM4 が NVIDIA 以外の GPU (内蔵 GPU など) で動いている可能性があります。Windows の「グラフィック設定」で YukkuriMovieMaker.exe を高パフォーマンス GPU (NVIDIA) に固定してください。";
        return err;
    }

    /// <summary>Size of NVENC's input texture pool.</summary>
    private const int NvencPoolSize = 6;

    /// <summary>Opens the extra sessions; on any failure (AV1, driver session limit) continues with what opened (worst case a single session).</summary>
    private void TryOpenMembers()
    {
        if (_s.Codec == NvencCodec.AV1) { Log.Write("EncSessions: AV1 does not support multiple sessions -> 1 session"); return; }
        if (NvencNativeMethods.NvencEnableOrderedMux(_handle) == 0) { Log.Write("EncSessions: native cannot enable ordered mux -> 1 session"); return; }
        _ordered = true;
        for (int i = 1; i < _s.EncSessions; i++)
        {
            var m = NvencNativeMethods.NvencCreateMember(_handle, NvencPoolSize);
            if (m == IntPtr.Zero) { Log.Write($"EncSessions: cannot create session {i}"); break; }
            var err = LastError(m);
            if (!string.IsNullOrWhiteSpace(err))
            {
                Log.Write($"EncSessions: session {i} init failed ({err}) -> continuing with {i}");
                NvencNativeMethods.NvencDestroy(m);
                break;
            }
            _members.Add(m);
        }
        Sessions = 1 + _members.Count;
        var fps = Math.Max(1, _vi.FPS);
        EncChunk = fps * 2;   // = the encoder's GOP length (NvencNative uses gopLength = fps x 2), so IDR positions match a single session
        _inputs = new ID3D11Texture2D?[Sessions];
        Log.Write($"EncSessions: {Sessions} sessions, chunk {EncChunk} frames (GOP {fps * 2})");
    }

    private IntPtr SessionHandle(int session) => session == 0 ? _handle : _members[session - 1];
    private int SessionOf(int index) => Sessions == 1 ? 0 : (index / EncChunk) % Sessions;

    /// <summary>Uploads one tightly packed BGRA frame (pitch = width*4) and encodes it.</summary>
    public unsafe void Submit(byte[] bgra, int index)
    {
        var handle = _handle;
        if (handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(EncoderCore));
        if (bgra.Length < _vi.Width * _vi.Height * 4) throw new ArgumentException("フレームのサイズが不足");
        int session = SessionOf(index);
        var input = session == 0 ? _input! : InputFor(session);
        fixed (byte* p = bgra)
            _ctx!.UpdateSubresource(input, 0, null, (IntPtr)p, _vi.Width * 4, 0);
        EncodeOn(session, input.NativePointer, index);
    }

    /// <summary>Upload target of sessions 1+; created and used only by the thread driving that session.</summary>
    private ID3D11Texture2D InputFor(int session)
    {
        var t = _inputs[session];
        if (t is null)
        {
            t = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = _vi.Width, Height = _vi.Height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            });
            _inputs[session] = t;
        }
        return t;
    }

    /// <summary>Encodes frame index on its session; with several sessions forces an IDR at chunk starts and uses index as the mux sequence.</summary>
    private void EncodeOn(int session, IntPtr texture, int index)
    {
        var handle = SessionHandle(session);
        int ok;
        if (!_ordered) ok = NvencNativeMethods.NvencEncode(handle, texture);
        else ok = NvencNativeMethods.NvencEncodeEx(handle, texture, (ulong)index, Sessions > 1 && index % EncChunk == 0 ? 1 : 0);
        if (ok == 0) throw new InvalidOperationException(Fallback(LastError(handle), "NvencEncode が失敗しました。"));
    }

    /// <summary>
    /// GPU direct: encodes a worker-drawn shared texture (opened on the encoder's device) without any upload.
    /// NvencEncode copies it GPU-side into its own input texture, so the slot can be recycled once this returns.
    /// </summary>
    public void SubmitShared(SharedSlot slot, int index)
    {
        if (_handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(EncoderCore));
        slot.OwnerAcquire();
        try { EncodeOn(SessionOf(index), slot.OwnerTexturePtr, index); }
        finally { slot.OwnerRelease(); }
    }

    public void WriteAudio(float[] samples)
    {
        if (samples.Length == 0) return;
        lock (_handleLock)
        {
            if (_handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(EncoderCore));
            if (NvencNativeMethods.NvencWriteAudio(_handle, samples, samples.Length, Math.Max(8000, _vi.Hz), _audioChannels) == 0)
                throw new InvalidOperationException(Fallback(LastError(), "NvencWriteAudio が失敗しました。"));
        }
    }

    /// <summary>Drains the encoders and finalises the MP4.</summary>
    public void Finish()
    {
        lock (_handleLock)
        {
            if (_handle == IntPtr.Zero || _finalized) return;
            _finalized = true;
            // Drain extra sessions first: the primary's finalize fails if packets are still waiting on ordering.
            bool ok = true; string err = "";
            for (int i = 0; i < _members.Count && ok; i++)
            {
                ok = NvencNativeMethods.NvencFinalize(_members[i]) != 0;
                if (!ok) err = $"セッション {i + 1}: " + LastError(_members[i]);
            }
            if (ok)
            {
                ok = NvencNativeMethods.NvencFinalize(_handle) != 0;
                if (!ok) err = LastError();
            }
            if (!ok) throw new InvalidOperationException(Fallback(err, "NVENC 出力の終了処理に失敗しました。"));
        }
    }

    /// <summary>Abort: destroys the encoder and deletes the partial output file.</summary>
    public void Abort()
    {
        Release();
        try { if (!_finalized && File.Exists(_path)) File.Delete(_path); } catch (Exception ex) { Log.Write("abort: delete failed " + ex.Message); }
    }

    private void Release()
    {
        lock (_handleLock)
        {
            // Destroy extra sessions before the primary: they write into its muxer.
            for (int i = _members.Count - 1; i >= 0; i--) NvencNativeMethods.NvencDestroy(_members[i]);
            _members.Clear();
            if (_handle != IntPtr.Zero) { NvencNativeMethods.NvencDestroy(_handle); _handle = IntPtr.Zero; }
        }
        foreach (var t in _inputs) t?.Dispose();
        _inputs = [];
        _input?.Dispose(); _input = null;
        _ctx?.Dispose(); _ctx = null;
        _device?.Dispose(); _device = null;
    }

    public void Dispose() => Release();

    private static string Fallback(string s, string fb) => string.IsNullOrWhiteSpace(s) ? fb : s;

    private string LastError() => LastError(_handle);

    private static string LastError(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return string.Empty;
        var ptr = NvencNativeMethods.NvencGetLastError(handle);
        return ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(ptr) ?? string.Empty;
    }

    private int TargetBitrateKbps()
    {
        if (_s.RateControl != NvencRateControl.YouTubeRecommended) return Math.Clamp(_s.BitrateKbps, 100, 200000);
        var highFps = _vi.FPS >= 48;
        return Math.Max(1, _vi.Height) switch
        {
            >= 2160 => (highFps ? 60 : 40) * 1000,
            >= 1440 => (highFps ? 24 : 16) * 1000,
            >= 1080 => (highFps ? 12 : 8) * 1000,
            >= 720 => highFps ? 7500 : 5000,
            _ => (highFps ? 4 : 3) * 1000,
        };
    }
}
