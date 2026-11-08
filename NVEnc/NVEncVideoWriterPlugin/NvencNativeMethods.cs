using System.Runtime.InteropServices;

namespace NVEncVideoWriterPlugin;

internal static partial class NvencNativeMethods
{
    [DllImport("NvencNative.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr NvencCreate(
        IntPtr device,
        int width,
        int height,
        int fps,
        int bitrateKbps,
        int codec,
        int quality,
        int fastPreset,
        int rateControlMode,
        int maxBitrateKbps,
        int bufferFormat,
        int hevcAsync,
        int enableDebugLog,
        string outputPath);

    [DllImport("NvencNative.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr NvencCreateWithPool(
        IntPtr device,
        int width,
        int height,
        int fps,
        int bitrateKbps,
        int codec,
        int quality,
        int fastPreset,
        int rateControlMode,
        int maxBitrateKbps,
        int bufferFormat,
        int hevcAsync,
        int enableDebugLog,
        string outputPath,
        int texturePoolSize);

    [DllImport("NvencNative.dll")]
    public static extern int NvencGetPoolSize(IntPtr handle);

    [DllImport("NvencNative.dll")]
    public static extern int NvencEncode(IntPtr handle, IntPtr texture);

    [DllImport("NvencNative.dll")]
    public static extern int NvencWriteAudio(IntPtr handle, float[] samples, int sampleCount, int sampleRate, int channels);

    [DllImport("NvencNative.dll")]
    public static extern int NvencFinalize(IntPtr handle);

    [DllImport("NvencNative.dll")]
    public static extern void NvencDestroy(IntPtr handle);

    [DllImport("NvencNative.dll")]
    public static extern IntPtr NvencGetLastError(IntPtr handle);

    // Multi-session ordered mux (this repository's NvencNative only; the stock upstream DLL does not export these).
    [DllImport("NvencNative.dll")]
    public static extern int NvencEnableOrderedMux(IntPtr primary);

    [DllImport("NvencNative.dll")]
    public static extern IntPtr NvencCreateMember(IntPtr primary, int texturePoolSize);

    [DllImport("NvencNative.dll")]
    public static extern int NvencEncodeEx(IntPtr handle, IntPtr texture, ulong sequence, int flags);
}
