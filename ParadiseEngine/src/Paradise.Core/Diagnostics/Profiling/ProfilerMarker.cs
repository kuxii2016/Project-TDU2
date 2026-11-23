using System.Collections.Concurrent;

namespace Paradise.Core.Diagnostics;

/// <summary>
/// Ein benannter Messpunkt (wie Unity <c>ProfilerMarker</c>). Einmal als <c>static readonly</c> anlegen, dann
/// <c>using (s_marker.Auto()) { … }</c>. Kostet bei laufender Aufnahme ca. 50–100 ns, ohne Aufnahme nur eine Abfrage;
/// ohne PARADISE_PROFILER (Release) ist der Aufruf leer.
/// </summary>
public sealed class ProfilerMarker
{
    static readonly object s_lock = new();
    static ProfilerMarker[] s_all = Array.Empty<ProfilerMarker>();
    static readonly ConcurrentDictionary<string, ProfilerMarker> s_byName = new(StringComparer.Ordinal);

    public int Id { get; }
    public string Name { get; }
    public ProfilerCategory Category { get; }

    public ProfilerMarker(string name, ProfilerCategory category = ProfilerCategory.Other)
    {
        Name = name;
        Category = category;
        lock (s_lock)
        {
            var arr = new ProfilerMarker[s_all.Length + 1];
            Array.Copy(s_all, arr, s_all.Length);
            Id = s_all.Length;
            arr[Id] = this;
            Volatile.Write(ref s_all, arr);
        }
    }

    /// <summary>Messpunkt über den Namen (einmal angelegt, danach aus dem Zwischenspeicher). Für feste Stellen besser ein static-Feld.</summary>
    public static ProfilerMarker Get(string name, ProfilerCategory category = ProfilerCategory.Other) =>
        s_byName.GetOrAdd(name, static (n, c) => new ProfilerMarker(n, c), category);

    /// <summary>Messpunkt zur Id (für die Auswertung, threadsicher).</summary>
    public static ProfilerMarker? ById(int id)
    {
        var a = Volatile.Read(ref s_all);
        return (uint)id < (uint)a.Length ? a[id] : null;
    }

    public static int Count => Volatile.Read(ref s_all).Length;

    /// <summary>Misst bis zum Ende des using-Blocks.</summary>
    public ProfilerScope Auto() => new(this);

    /// <summary>Manueller Beginn; <see cref="End"/> muss auf demselben Thread folgen.</summary>
    public void Begin() => Profiler.Begin(this);
    public void End() => Profiler.End(this);

    public override string ToString() => Name;
}

/// <summary>Messbereich für <c>using</c>. ref struct: keine Allokation.</summary>
public ref struct ProfilerScope
{
    ProfilerMarker? _marker;

    public ProfilerScope(ProfilerMarker marker)
    {
        _marker = Profiler.Begin(marker) ? marker : null;
    }

    public void Dispose()
    {
        if (_marker != null)
        {
            Profiler.End(_marker);
            _marker = null;
        }
    }
}
