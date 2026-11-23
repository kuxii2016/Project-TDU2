namespace Paradise.Core.Diagnostics;

/// <summary>
/// Kategorien des CPU-Graphen (wie Unity "CPU Usage": Rendering, Scripts, Physics …). Jeder Messpunkt gehört zu genau einer
/// Kategorie; der Graph stapelt die <b>Eigenzeit</b> (Self) je Kategorie, damit die Summe der Bildzeit entspricht.
/// "Other" = alles, was in keinem Messpunkt steckt (Rest der Bildzeit).
/// </summary>
public enum ProfilerCategory
{
    Other = 0,
    Rendering,
    Scripts,
    Physics,
    Animation,
    UI,
    Streaming,
    Audio,
    Network,
    GarbageCollector,
    /// <summary>Warten auf GPU/Fence/VSync/Present (CPU tut nichts).</summary>
    Wait,
    Profiler,
}

public static class ProfilerCategories
{
    public const int Count = (int)ProfilerCategory.Profiler + 1;

    public static string Name(ProfilerCategory c) => c switch
    {
        ProfilerCategory.Other => "Others",
        ProfilerCategory.Rendering => "Rendering",
        ProfilerCategory.Scripts => "Scripts / Gamemodes",
        ProfilerCategory.Physics => "Physics",
        ProfilerCategory.Animation => "Animation",
        ProfilerCategory.UI => "UI (Flash)",
        ProfilerCategory.Streaming => "Streaming / Loading",
        ProfilerCategory.Audio => "Audio",
        ProfilerCategory.Network => "Network",
        ProfilerCategory.GarbageCollector => "GarbageCollector",
        ProfilerCategory.Wait => "Wait (GPU / VSync)",
        ProfilerCategory.Profiler => "Profiler",
        _ => c.ToString(),
    };
}
