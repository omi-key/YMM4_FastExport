using System.Collections.Concurrent;
using System.Reflection;

namespace FastPsdTachiePlugin;

/// <summary>
/// Snapshot of the public property values of a parameter object (possibly an internal type) that may affect the output.
/// PropertyInfo is resolved once per type. Only string / primitives / enum / IReadOnlyList&lt;string&gt; are allowed; a property of
/// any other type (e.g. an animation value) makes the type "not provably safe", so frames are never skipped for it.
/// </summary>
internal sealed class ParamTypeInfo
{
    private static readonly ConcurrentDictionary<Type, ParamTypeInfo> Cache = new();

    // Obviously irrelevant to rendering (dummy for the preset UI)
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal) { "PresetDummy" };

    public PropertyInfo[] Props { get; }
    /// <summary>True if this type has a property that cannot be compared safely by snapshot.</summary>
    public bool IsUnsafe { get; }

    public int IdxEye { get; } = -1;
    public int IdxMouth { get; } = -1;
    public int IdxHiddenWhenNoSpeech { get; } = -1;

    private ParamTypeInfo(Type type)
    {
        var list = new List<PropertyInfo>();
        // Only public instance properties declared on the concrete type (generic Bindable/Animatable base properties are excluded).
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                              .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (!p.CanRead || p.GetIndexParameters().Length != 0 || Ignored.Contains(p.Name)) continue;
            if (!IsAllowed(p.PropertyType)) { IsUnsafe = true; continue; }
            list.Add(p);
        }
        Props = list.ToArray();
        for (int i = 0; i < Props.Length; i++)
        {
            switch (Props[i].Name)
            {
                case "EyeAnimation": IdxEye = i; break;
                case "MouthAnimation": IdxMouth = i; break;
                case "IsHiddenWhenNoSpeech": IdxHiddenWhenNoSpeech = i; break;
            }
        }
    }

    public static ParamTypeInfo For(Type t) => Cache.GetOrAdd(t, static x => new ParamTypeInfo(x));

    private static bool IsAllowed(Type t)
        => t == typeof(string) || t.IsPrimitive || t.IsEnum || typeof(IReadOnlyList<string>).IsAssignableFrom(t);
}

internal sealed class ParamSnapshot
{
    public ParamTypeInfo Info { get; }
    public object?[] Values { get; }

    private ParamSnapshot(ParamTypeInfo info, object?[] values)
    {
        Info = info;
        Values = values;
    }

    /// <summary>Null for a null obj. For a disallowed type, sets isUnsafe and returns null.</summary>
    public static ParamSnapshot? Capture(object? obj, ref bool isUnsafe)
    {
        if (obj is null) return null;
        var info = ParamTypeInfo.For(obj.GetType());
        if (info.IsUnsafe)
        {
            isUnsafe = true;
            return null;
        }
        var vals = new object?[info.Props.Length];
        for (int i = 0; i < vals.Length; i++) vals[i] = info.Props[i].GetValue(obj);
        return new ParamSnapshot(info, vals);
    }

    public bool HiddenWhenNoSpeech => Info.IdxHiddenWhenNoSpeech >= 0 && Values[Info.IdxHiddenWhenNoSpeech] is true;

    /// <summary>Whether automatic eye animation is on: anything but AlwaysOpen/AlwaysClose counts as time-dependent.</summary>
    public bool EyeAnimates => Info.IdxEye >= 0 && !IsFixedMode(Values[Info.IdxEye]);

    /// <summary>Whether lip sync is on: anything but AlwaysOpen/AlwaysClose counts as voice-dependent.</summary>
    public bool MouthAnimates => Info.IdxMouth >= 0 && !IsFixedMode(Values[Info.IdxMouth]);

    private static bool IsFixedMode(object? v)
    {
        var s = v?.ToString();
        return s == "AlwaysOpen" || s == "AlwaysClose";
    }

    public bool SameAs(ParamSnapshot? other)
    {
        if (other is null || !ReferenceEquals(Info, other.Info)) return false;
        for (int i = 0; i < Values.Length; i++)
            if (!ValueEquals(Values[i], other.Values[i])) return false;
        return true;
    }

    private static bool ValueEquals(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a is string) return a.Equals(b);
        if (a is IReadOnlyList<string> la && b is IReadOnlyList<string> lb)
        {
            // Equal by content even if the references differ.
            if (la.Count != lb.Count) return false;
            for (int i = 0; i < la.Count; i++)
                if (!string.Equals(la[i], lb[i], StringComparison.Ordinal)) return false;
            return true;
        }
        return a.Equals(b);
    }
}
