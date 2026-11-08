namespace ParallelRender;

/// <summary>
/// Link to FastPsdTachie (a separate DLL) through AppDomain data "ParallelRender.MultiDeviceExport" (bool).
/// While it is true, several worker devices render at once, so FastPsdTachie composes a tachie image on one device and
/// copies it to the others. Do not rename the key or change its value type.
/// </summary>
internal static class TachieLink
{
    private const string Key = "ParallelRender.MultiDeviceExport";

    public static void SetMultiDevice(bool on) => AppDomain.CurrentDomain.SetData(Key, on);
}
