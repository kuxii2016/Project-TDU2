using Paradise.Core.Render;

namespace Paradise.Core.Diagnostics;

public enum ProfilerUnit { Count, Bytes, Ms }

/// <summary>
/// Ein Zahlenwert je Bild (Draw Calls, Speicher, Körper …) für die Graphen "Rendering", "Memory", "Physics".
/// Entweder per <see cref="Poll"/> am Bildende abgefragt oder vom Code über <see cref="Set"/>/<see cref="Add"/> gesetzt.
/// </summary>
public sealed class ProfilerCounter
{
    static readonly object s_lock = new();
    static ProfilerCounter[] s_all = Array.Empty<ProfilerCounter>();

    public int Id { get; }
    public string Name { get; }
    /// <summary>Graph, in dem der Zähler erscheint ("Rendering", "Memory", "Physics", …).</summary>
    public string Group { get; }
    public ProfilerUnit Unit { get; }
    /// <summary>Am Bildende abfragen (Hauptthread). null = Wert wird über Set/Add geliefert.</summary>
    public Func<double>? Poll { get; set; }
    /// <summary>Nach jedem Bild auf 0 setzen (Zähler "pro Bild", z. B. Physikschritte).</summary>
    public bool ResetEachFrame { get; }

    double _value;

    public ProfilerCounter(string name, string group, ProfilerUnit unit = ProfilerUnit.Count, Func<double>? poll = null, bool resetEachFrame = false)
    {
        Name = name; Group = group; Unit = unit; Poll = poll; ResetEachFrame = resetEachFrame;
        lock (s_lock)
        {
            var arr = new ProfilerCounter[s_all.Length + 1];
            Array.Copy(s_all, arr, s_all.Length);
            Id = s_all.Length;
            arr[Id] = this;
            Volatile.Write(ref s_all, arr);
        }
    }

    public static ProfilerCounter[] All => Volatile.Read(ref s_all);

    public double Value => Volatile.Read(ref _value);

    public void Set(double v) => Volatile.Write(ref _value, v);

    /// <summary>Hochzählen (threadsicher genug für Statistik: nur Hauptthread oder selten).</summary>
    public void Add(double v) => Volatile.Write(ref _value, Volatile.Read(ref _value) + v);

    internal double Sample()
    {
        if (Poll != null)
        {
            try { _value = Poll(); } catch (Exception) { }
        }
        double v = _value;
        if (ResetEachFrame) _value = 0;
        return v;
    }

    public string Format(double v) => Unit switch
    {
        ProfilerUnit.Bytes => FormatBytes(v),
        ProfilerUnit.Ms => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " ms",
        _ => FormatCount(v),
    };

    public static string FormatBytes(double b)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double a = System.Math.Abs(b);
        if (a >= 1024.0 * 1024 * 1024) return (b / (1024.0 * 1024 * 1024)).ToString("0.00", inv) + " GB";
        if (a >= 1024.0 * 1024) return (b / (1024.0 * 1024)).ToString("0.0", inv) + " MB";
        if (a >= 1024.0) return (b / 1024.0).ToString("0.0", inv) + " KB";
        return b.ToString("0", inv) + " B";
    }

    public static string FormatCount(double v)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double a = System.Math.Abs(v);
        if (a >= 1_000_000) return (v / 1_000_000.0).ToString("0.00", inv) + "M";
        if (a >= 10_000) return (v / 1000.0).ToString("0.0", inv) + "k";
        return v.ToString(v == System.Math.Floor(v) ? "0" : "0.##", inv);
    }
}

/// <summary>Feste Zähler der Engine (Graphen Rendering / Memory / Physics).</summary>
public static class ProfilerCounters
{
    public const string Rendering = "Rendering", Memory = "Memory", Physics = "Physics";
    /// <summary>Objekte / Draw Calls / Dreiecke je Objektart im Hauptbild (nur im Bericht, passend zu "GPU Scene: …").</summary>
    public const string SceneDetail = "Scene Detail";
    /// <summary>Flash-Oberflächen (FlashHost): CPU, Rasterung, Uploads, Einblend-Wartezeit.</summary>
    public const string Flash = "Flash";

    // ---- Memory (vom Profiler selbst abgefragt) -------------------------------------------------------------
    static long s_lastAlloc = -1;
    static int s_lastGc0 = -1, s_lastGc1 = -1, s_lastGc2 = -1;

    public static readonly ProfilerCounter GcUsed = new("GC Used Memory", Memory, ProfilerUnit.Bytes, () => GC.GetTotalMemory(false));
    public static readonly ProfilerCounter WorkingSet = new("Process Working Set", Memory, ProfilerUnit.Bytes, () => Environment.WorkingSet);
    public static readonly ProfilerCounter GcAllocFrame = new("GC Allocated In Frame", Memory, ProfilerUnit.Bytes, () =>
    {
        long a = GC.GetTotalAllocatedBytes(false);
        long d = s_lastAlloc < 0 ? 0 : a - s_lastAlloc;
        s_lastAlloc = a;
        return d < 0 ? 0 : d;
    });
    public static readonly ProfilerCounter Gc0 = new("GC Gen0 Collections", Memory, ProfilerUnit.Count, () => Delta(0, ref s_lastGc0));
    public static readonly ProfilerCounter Gc1 = new("GC Gen1 Collections", Memory, ProfilerUnit.Count, () => Delta(1, ref s_lastGc1));
    public static readonly ProfilerCounter Gc2 = new("GC Gen2 Collections", Memory, ProfilerUnit.Count, () => Delta(2, ref s_lastGc2));

    static double Delta(int gen, ref int last)
    {
        int c = GC.CollectionCount(gen);
        int d = last < 0 ? 0 : c - last;
        last = c;
        return d;
    }

    // ---- Rendering / GPU-Speicher (Quelle: IRenderer.Stats, siehe RegisterRenderer) -------------------------
    public static readonly ProfilerCounter DrawCalls = new("Draw Calls", Rendering);
    public static readonly ProfilerCounter PipelineBinds = new("SetPass (Pipeline Binds)", Rendering);
    public static readonly ProfilerCounter Triangles = new("Triangles", Rendering);
    public static readonly ProfilerCounter ShadowDraws = new("Shadow Draws", Rendering);
    public static readonly ProfilerCounter TransparentDraws = new("Transparent Draws", Rendering);
    /// <summary>Schattenstärke aus dem Wetter (sun_shadow_opacity) und Sonnenlicht (größter Kanal) – zeigt, ob Schatten nachts sichtbar sind.</summary>
    public static readonly ProfilerCounter ShadowStrength = new("Sun Shadow Strength", Rendering);
    public static readonly ProfilerCounter SunLight = new("Sun Light (max channel)", Rendering);
    public static readonly ProfilerCounter Textures = new("Textures", Memory);
    public static readonly ProfilerCounter Meshes = new("Meshes", Memory);
    public static readonly ProfilerCounter Materials = new("Materials", Memory);
    public static readonly ProfilerCounter GpuAllocated = new("GPU Allocated (Vulkan)", Memory, ProfilerUnit.Bytes);
    public static readonly ProfilerCounter VramUsage = new("VRAM Usage", Memory, ProfilerUnit.Bytes);

    // ---- Physik (gesetzt von PhysicsWorld.Step) -------------------------------------------------------------
    public static readonly ProfilerCounter PhysicsSteps = new("Physics Steps", Physics, ProfilerUnit.Count, null, resetEachFrame: true);
    public static readonly ProfilerCounter PhysicsActiveBodies = new("Active Bodies", Physics);
    public static readonly ProfilerCounter PhysicsStatics = new("Static Colliders", Physics);

    /// <summary>Zähler aus den Renderer-Statistiken abfragen (vom GameLoop nach Initialize aufgerufen).</summary>
    public static void RegisterRenderer(IRenderer renderer)
    {
        DrawCalls.Poll = () => renderer.Stats.DrawCalls;
        PipelineBinds.Poll = () => renderer.Stats.PipelineBinds;
        Triangles.Poll = () => renderer.Stats.Triangles;
        ShadowDraws.Poll = () => renderer.Stats.ShadowDraws;
        TransparentDraws.Poll = () => renderer.Stats.TransparentDraws;
        Textures.Poll = () => renderer.Stats.Textures;
        Meshes.Poll = () => renderer.Stats.Meshes;
        Materials.Poll = () => renderer.Stats.Materials;
        GpuAllocated.Poll = () => renderer.Stats.GpuMemoryAllocated;
        VramUsage.Poll = () => renderer.Stats.VramUsage;
    }

    /// <summary>Sorgt dafür, dass alle festen Zähler angelegt sind (static-Initialisierung).</summary>
    internal static void Touch() { _ = GcUsed; }
}
