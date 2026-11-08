using System.Collections.Immutable;
using System.IO;
using PsdParser;
using YukkuriMovieMaker.Plugin.FileSource.Psd;

namespace FastPsdTachiePlugin;

/// <summary>
/// A parsed PSD (layer structure plus a PsdFile holding the whole file). It does not depend on D2D, so it can be shared across
/// devices and sources. PsdFolder's enabled flags and the PsdFile read position are rewritten on every composite, so composing
/// must hold <see cref="ComposeGate"/>. The structure (Items / Id / Path) and the default enabled layers are read-only.
/// </summary>
internal sealed class PsdDoc : IDisposable
{
    public readonly PsdFile Psd;
    public readonly PsdFolder Root;
    public readonly ImmutableList<string> DefaultEnable;   // enabled layers right after load, before any composite
    public readonly string NormalizedPath;
    public readonly DateTime Stamp;
    public readonly long Length;
    public readonly object ComposeGate = new();

    public PsdDoc(PsdFile psd, PsdFolder root, ImmutableList<string> defaultEnable, string normalizedPath, DateTime stamp, long length)
    {
        Psd = psd; Root = root; DefaultEnable = defaultEnable; NormalizedPath = normalizedPath; Stamp = stamp; Length = length;
    }

    public void Dispose() => Psd.Dispose();
}

/// <summary>A source's borrow of a PsdDoc. If not shared (the share path failed) it is private and disposed on Release.</summary>
internal sealed class PsdDocHandle
{
    public readonly PsdDoc Doc;
    internal readonly SharedDocStore<PsdDoc>.Lease? Lease;
    public PsdDocHandle(PsdDoc doc, SharedDocStore<PsdDoc>.Lease? lease) { Doc = doc; Lease = lease; }
}

/// <summary>Process-wide sharing of parsed PSDs: a PSD is loaded once even though sources are recreated per worker x chunk.</summary>
internal static class PsdDocs
{
    // Limits for documents nobody is using (idle): total size, and how long they are kept.
    private const long IdleDocBytes = 1024L * 1024 * 1024;
    private const int IdleDocTtlSeconds = 60;

    private static readonly Lazy<SharedDocStore<PsdDoc>> s_store = new(() =>
        new SharedDocStore<PsdDoc>(d => d.Dispose(), IdleDocBytes, TimeSpan.FromSeconds(IdleDocTtlSeconds)));

    public static (DateTime, long) Stamp(string path)
    {
        try { var fi = new FileInfo(path); return fi.Exists ? (fi.LastWriteTimeUtc, fi.Length) : (DateTime.MinValue, -1); }
        catch { return (DateTime.MinValue, -2); }
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); } catch { return path; }
    }

    /// <summary>Null if the file cannot be loaded (the caller treats it as "no PSD").</summary>
    public static PsdDocHandle? Acquire(string path)
    {
        if (!File.Exists(path)) return null;
        var stamp = Stamp(path);
        var norm = NormalizePath(path);
        {
            try
            {
                var key = new SharedDocStore<PsdDoc>.DocKey(norm, stamp.Item1.Ticks, stamp.Item2);
                var lease = s_store.Value.Acquire(key, () => (Load(path, norm, stamp), stamp.Item2));
                if (lease is not null) return new PsdDocHandle(lease.Value, lease);
                return null;
            }
            catch
            {
                // The sharing machinery failed: load privately instead.
            }
        }
        try
        {
            return new PsdDocHandle(Load(path, norm, stamp), null);
        }
        catch { return null; }
    }

    public static void Release(PsdDocHandle? h)
    {
        if (h is null) return;
        if (h.Lease is not null) s_store.Value.Release(h.Lease);
        else h.Doc.Dispose();
    }

    private static PsdDoc Load(string path, string norm, (DateTime, long) stamp)
    {
        var psd = new PsdFile(path);
        try
        {
            var root = PsdFolder.Parse(psd);
            var def = ImmutableList.CreateRange(root.GetEnableItems());
            return new PsdDoc(psd, root, def, norm, stamp.Item1, stamp.Item2);
        }
        catch
        {
            psd.Dispose();
            throw;
        }
    }
}
