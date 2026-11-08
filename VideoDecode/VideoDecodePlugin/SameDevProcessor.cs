using System;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using YukkuriMovieMaker.Commons;

namespace VideoDecode;

/// <summary>
/// Runs the D3D11 VideoProcessor colour conversion directly on the render device (used only on ParallelRender's worker
/// devices, see SameDevPolicy). Decoding happens on the same device, which removes the cross-device copy
/// (keyed mutex + CopyResource) and the per-frame D2D bitmap creation. The BGRA output texture and its D2D bitmap are created
/// once per source and reused. Blt goes to the device's immediate context, so GPU ordering with later D2D drawing is preserved.
/// </summary>
internal sealed class SameDevProcessor : IDisposable
{
    private readonly IGraphicsDevicesAndContext rd;
    private readonly ID2D1Multithread mt;
    private readonly int inW, inH, outW, outH;
    private ColorSpaceType inputColorSpace;
    private readonly ID3D11VideoProcessorEnumerator enumerator;
    private readonly ID3D11VideoProcessor processor;
    private readonly ID3D11Texture2D outTexture;
    private readonly ID3D11VideoProcessorOutputView outView;
    private readonly ID2D1Bitmap1 bitmap;
    private bool disposed;

    public ID2D1Bitmap1 OutputBitmap => bitmap;

    public SameDevProcessor(IGraphicsDevicesAndContext renderDevices, int inW, int inH, int outW, int outH, ColorSpaceType cs)
    {
        rd = renderDevices;
        this.inW = inW > 0 ? inW : outW; this.inH = inH > 0 ? inH : outH; this.outW = outW; this.outH = outH; inputColorSpace = cs;
        mt = rd.D2D.Factory.QueryInterface<ID2D1Multithread>();
        var vd = rd.D3D.VideoDevice ?? throw new InvalidOperationException("VideoDevice is not available");
        enumerator = vd.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive, InputWidth = this.inW, InputHeight = this.inH,
            OutputWidth = outW, OutputHeight = outH, Usage = VideoUsage.PlaybackNormal,
        })!;
        processor = vd.CreateVideoProcessor(enumerator, 0)!;
        outTexture = rd.D3D.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = outW, Height = outH, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget, CPUAccessFlags = CpuAccessFlags.None, MiscFlags = ResourceOptionFlags.None,
        })!;
        outView = vd.CreateVideoProcessorOutputView(outTexture, enumerator, new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D })!;
        var vc = rd.D3D.VideoContext;
        vc.VideoProcessorSetOutputColorSpace1(processor, ColorSpaceType.RgbFullG22NoneP709);
        vc.VideoProcessorSetStreamColorSpace1(processor, 0, inputColorSpace);
        vc.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);
        using var surface = outTexture.QueryInterface<IDXGISurface>();
        bitmap = rd.DeviceContext.CreateBitmapFromDxgiSurface(surface,
            new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96f, 96f, BitmapOptions.None));
    }

    public void UpdateInputColorSpace(ColorSpaceType cs)
    {
        if (cs == inputColorSpace) return;
        inputColorSpace = cs;
        mt.Enter();
        try { rd.D3D.VideoContext.VideoProcessorSetStreamColorSpace1(processor, 0, cs); } finally { mt.Leave(); }
    }

    /// <summary>Converts a decoded NV12 texture into the BGRA output texture on the GPU (does not wait for completion).</summary>
    public bool ProcessFrame(nint textureHandle, uint arraySlice)
    {
        if (textureHandle == IntPtr.Zero) return false;
        Marshal.AddRef(textureHandle);
        using var resource = new ID3D11Texture2D(textureHandle);
        ID3D11VideoProcessorInputView? inView = null;
        try
        {
            var vd = rd.D3D.VideoDevice; var vc = rd.D3D.VideoContext;
            inView = vd.CreateVideoProcessorInputView(resource, enumerator, new VideoProcessorInputViewDescription
            {
                FourCC = 0, ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = (int)arraySlice },
            });
            if (inView == null) return false;
            var stream = new VideoProcessorStream { Enable = true, OutputIndex = 0, InputFrameOrField = 0, PastFrames = 0, FutureFrames = 0, InputSurface = inView };
            mt.Enter();   // keep this off the context while D2D (EndDraw etc.) is issuing to it
            try
            {
                vc.VideoProcessorSetStreamSourceRect(processor, 0, true, new RawRect(0, 0, inW, inH));
                vc.VideoProcessorSetStreamDestRect(processor, 0, true, new RawRect(0, 0, outW, outH));
                vc.VideoProcessorSetOutputTargetRect(processor, true, new RawRect(0, 0, outW, outH));
                vc.VideoProcessorBlt(processor, outView, 0, 1, new[] { stream });
            }
            finally { mt.Leave(); }
            return true;
        }
        catch (Exception ex)
        {
            Log.Default.Write("VideoDecode: SameDevProcessor ProcessFrame exception: " + ex.Message, ex);
            return false;
        }
        finally
        {
            inView?.Dispose();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        bitmap.Dispose();
        outView.Dispose(); outTexture.Dispose(); processor.Dispose(); enumerator.Dispose();
        mt.Dispose();
    }
}
