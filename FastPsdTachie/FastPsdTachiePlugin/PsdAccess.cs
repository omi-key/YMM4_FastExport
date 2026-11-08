using System.Reflection;
using YukkuriMovieMaker.Plugin.Tachie.Psd;

namespace FastPsdTachiePlugin;

/// <summary>
/// Minimal reflection access to the standard plugin's internal parameter types (PsdTachieItemParameter / PsdTachieFaceParameter),
/// resolved once. If anything cannot be resolved, <see cref="Available"/> is false and sources forward to the standard source.
/// </summary>
internal static class PsdAccess
{
    public static readonly bool Available;

    private static readonly Type? ItemType, FaceType;
    private static readonly PropertyInfo? ItemHidden, FaceEnabled, FaceEye, FaceMouth;

    static PsdAccess()
    {
        try
        {
            var asm = typeof(PsdTachiePlugin).Assembly;
            ItemType = asm.GetType("YukkuriMovieMaker.Plugin.Tachie.Psd.PsdTachieItemParameter");
            FaceType = asm.GetType("YukkuriMovieMaker.Plugin.Tachie.Psd.PsdTachieFaceParameter");
            ItemHidden = ItemType?.GetProperty("IsHiddenWhenNoSpeech", BindingFlags.Public | BindingFlags.Instance);
            FaceEnabled = FaceType?.GetProperty("IsEnabled", BindingFlags.Public | BindingFlags.Instance);
            FaceEye = FaceType?.GetProperty("EyeAnimation", BindingFlags.Public | BindingFlags.Instance);
            FaceMouth = FaceType?.GetProperty("MouthAnimation", BindingFlags.Public | BindingFlags.Instance);
            Available = ItemType is not null && FaceType is not null && ItemHidden is not null
                        && FaceEnabled is not null && FaceEye is not null && FaceMouth is not null
                        && ItemHidden.PropertyType == typeof(bool) && FaceEnabled.PropertyType == typeof(bool);
        }
        catch
        {
            Available = false;
        }
    }

    public static bool IsItem(object? o) => o is not null && o.GetType() == ItemType;
    public static bool IsFace(object? o) => o is not null && o.GetType() == FaceType;
    public static bool HiddenWhenNoSpeech(object item) => (bool)ItemHidden!.GetValue(item)!;
    public static bool FaceIsEnabled(object face) => (bool)FaceEnabled!.GetValue(face)!;

    /// <summary>False for an unknown value.</summary>
    public static bool TryEye(object face, out EyeMode mode)
    {
        mode = EyeMode.Default;
        switch (FaceEye!.GetValue(face)?.ToString())
        {
            case "Default": mode = EyeMode.Default; return true;
            case "AlwaysOpen": mode = EyeMode.AlwaysOpen; return true;
            case "AlwaysClose": mode = EyeMode.AlwaysClose; return true;
            default: return false;
        }
    }

    public static bool TryMouth(object face, out MouthMode mode)
    {
        mode = MouthMode.VowelPriority;
        switch (FaceMouth!.GetValue(face)?.ToString())
        {
            case "VowelLipSyncPriority": mode = MouthMode.VowelPriority; return true;
            case "VolumeLipSyncPriority": mode = MouthMode.VolumePriority; return true;
            case "AlwaysOpen": mode = MouthMode.AlwaysOpen; return true;
            case "AlwaysClose": mode = MouthMode.AlwaysClose; return true;
            case "Default": mode = MouthMode.Default; return true;
            default: return false;
        }
    }
}
