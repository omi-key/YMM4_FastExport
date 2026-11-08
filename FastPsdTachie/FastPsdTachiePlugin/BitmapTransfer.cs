using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;

namespace FastPsdTachiePlugin;

/// <summary>A composed bitmap read back to the CPU, with enough metadata to recreate the same image on another device.</summary>
internal sealed class PixelBlock
{
    public byte[] Data = Array.Empty<byte>();
    public int Width, Height, Pitch;          // Pitch = bytes per row (tightly packed)
    public Vortice.DCommon.PixelFormat Format;
    public float DpiX, DpiY;
    public BitmapOptions Options;
    public ID2D1ColorContext? ColorContext;
    public long Bytes => Data.Length;
}

/// <summary>D2D bitmap <-> CPU memory. Call from a thread that uses the device's context.</summary>
internal static class BitmapTransfer
{
    private static int BytesPerPixel(Format f) => f switch
    {
        Format.B8G8R8A8_UNorm or Format.R8G8B8A8_UNorm => 4,
        Format.R32G32B32A32_Float => 16,
        Format.R16G16B16A16_Float or Format.R16G16B16A16_UNorm => 8,
        _ => throw new NotSupportedException("pixel format " + f),
    };

    public static PixelBlock Readback(IGraphicsDevicesAndContext devices, ID2D1Bitmap bmp)
    {
        using var src = bmp.QueryInterface<ID2D1Bitmap1>();
        var dc = devices.DeviceContext;
        var size = src.PixelSize;
        var fmt = src.PixelFormat;
        int bpp = BytesPerPixel(fmt.Format);
        src.GetDpi(out float dpiX, out float dpiY);
        var opts = src.Options;
        ID2D1ColorContext? cc = null;
        try { cc = src.ColorContext; } catch { }
        var props = new BitmapProperties1(fmt, dpiX, dpiY, BitmapOptions.CannotDraw | BitmapOptions.CpuRead);
        using var readable = dc.CreateBitmap(size, IntPtr.Zero, 0, props);
        readable.CopyFromBitmap(src);
        var blk = new PixelBlock
        {
            Width = size.Width, Height = size.Height, Pitch = size.Width * bpp, Format = fmt,
            DpiX = dpiX, DpiY = dpiY, Options = opts & ~(BitmapOptions.CpuRead | BitmapOptions.CannotDraw), ColorContext = cc,
        };
        blk.Data = new byte[(long)blk.Pitch * size.Height];
        var map = readable.Map(MapOptions.Read);
        try
        {
            for (int y = 0; y < size.Height; y++)
                Marshal.Copy(IntPtr.Add(map.Bits, y * (int)map.Pitch), blk.Data, y * blk.Pitch, blk.Pitch);
        }
        finally { readable.Unmap(); }
        return blk;
    }

    public static ID2D1Bitmap Upload(IGraphicsDevicesAndContext devices, PixelBlock blk)
    {
        var h = GCHandle.Alloc(blk.Data, GCHandleType.Pinned);
        try
        {
            var props = new BitmapProperties1(blk.Format, blk.DpiX, blk.DpiY, blk.Options);
            if (blk.ColorContext is not null) props.ColorContext = blk.ColorContext;
            return devices.DeviceContext.CreateBitmap(new SizeI(blk.Width, blk.Height), h.AddrOfPinnedObject(), blk.Pitch, props);
        }
        finally { h.Free(); }
    }
}
