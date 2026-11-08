using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ParallelRender.Encoding;

/// <summary>
/// NvencNativeMethods.cs is shared with the NVEnc plugin (linked from ../../NVEnc, see the csproj). Load NvencNative.dll
/// from our own folder so we never pick up the same-named DLL of the stock NVEnc plugin.
/// </summary>
internal static class NativeLibraryResolver
{
    private const string Dll = "NvencNative.dll";

    [ModuleInitializer]
    internal static void Register()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (!string.Equals(name, Dll, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        var full = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(asm.Location) ?? "", Dll);
        return System.IO.File.Exists(full) && NativeLibrary.TryLoad(full, out var h) ? h : IntPtr.Zero;
    }
}
