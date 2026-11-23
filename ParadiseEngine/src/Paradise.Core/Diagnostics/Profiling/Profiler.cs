using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Paradise.Core.Diagnostics;

/// <summary>
/// Engine-Profiler (Nachbau Unity-Profiler): Messbereiche je Thread, Bilder im Ringpuffer, Zähler, GPU-Zeiten, GC-Pausen.
/// Daten nimmt das Fenster F1 &gt; Performance &gt; Open Profiler (Projekt Paradise.Profiler) entgegen.
///
/// Nur mit dem Symbol PARADISE_PROFILER aktiv (Debug-Builds, oder Release mit <c>-p:Profiler=true</c>, siehe
/// Directory.Build.targets). Ohne das Symbol sind alle Aufrufe leer und werden vom JIT entfernt.
///
/// Threads: Messen geht von jedem Thread (eigener Puffer je Thread). Bildgrenzen setzt der Hauptthread in
/// <see cref="EndFrame"/> (GameLoop). Bilder sind nach dem Abschluss unveränderlich.
/// </summary>
public static class Profiler
{
    public const string GpuTotalName = "GPU Total";

    /// <summary>Wie viele Bilder der Ringpuffer hält (Unity: 300).</summary>
    public static int MaxFrames { get; set; } = 600;

    static volatile bool s_enabled;
    static readonly object s_framesLock = new();
    static readonly Queue<ProfilerFrame> s_frames = new();
    static long s_frameIndex;
    static long s_frameStart = Stopwatch.GetTimestamp();
    static readonly List<ProfilerGpuSample> s_gpuPending = new();
    static readonly Dictionary<string, string> s_info = new();
    static readonly ProfilerMarker s_collect = new("Profiler.Collect", ProfilerCategory.Profiler);
    static ProfilerGcListener? s_gc;

    // ---------------------------------------------------------------------------------------------------- Threads

    sealed class ThreadBuffer
    {
        public readonly Thread Thread;
        public readonly int ThreadId;
        public bool IsMain;
        public string? FixedName;
        public readonly List<ProfilerEvent> Done = new(1024);   // unter lock(this)

        public Open[] Stack = new Open[32];
        public int Depth;

        public ThreadBuffer(Thread t) { Thread = t; ThreadId = t.ManagedThreadId; }

        public string Name => FixedName ?? Thread.Name ?? ("Thread " + ThreadId);
    }

    struct Open
    {
        public int Marker;
        public long Start, Alloc, ChildTicks, ChildAlloc;
    }

    [ThreadStatic] static ThreadBuffer? t_buf;
    static readonly List<ThreadBuffer> s_threads = new();   // unter lock(s_threads)

    static ThreadBuffer Buf
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => t_buf ?? CreateBuf();
    }

    static ThreadBuffer CreateBuf()
    {
        var b = new ThreadBuffer(Thread.CurrentThread);
        lock (s_threads) s_threads.Add(b);
        t_buf = b;
        return b;
    }

    /// <summary>Anzeigename des aktuellen Threads in der Timeline (sonst Thread.Name).</summary>
    public static void SetThreadName(string name)
    {
#if PARADISE_PROFILER
        Buf.FixedName = name;
#endif
    }

    // ---------------------------------------------------------------------------------------------------- Steuerung

    /// <summary>Ob das Symbol PARADISE_PROFILER beim Bauen gesetzt war (sonst misst nichts).</summary>
    public static bool Available
    {
        get
        {
#if PARADISE_PROFILER
            return true;
#else
            return false;
#endif
        }
    }

    /// <summary>Aufnahme an/aus (Record-Knopf).</summary>
    public static bool Enabled
    {
        get => s_enabled;
        set
        {
#if PARADISE_PROFILER
            if (s_enabled == value) return;
            if (value)
            {
                ProfilerCounters.Touch();
                try { s_gc ??= new ProfilerGcListener(); } catch (Exception ex) { Log.Warn("Profiler: GC-Ereignisse nicht verfügbar: " + ex.Message); }
            }
            s_enabled = value;
#endif
        }
    }

    public static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>Zusatzinfos für den Bericht (Engine-Version, Grafikkarte …).</summary>
    public static void SetInfo(string key, string value) { lock (s_info) s_info[key] = value; }

    public static KeyValuePair<string, string>[] GetInfo() { lock (s_info) return s_info.ToArray(); }

    // ---------------------------------------------------------------------------------------------------- Messen

    /// <summary>Messbereich über einen Namen. Für Stellen, die oft laufen, besser ein static readonly <see cref="ProfilerMarker"/>.</summary>
    public static ProfilerScope Sample(string name, ProfilerCategory category = ProfilerCategory.Other) =>
        new(ProfilerMarker.Get(name, category));

    /// <summary>Beginn eines Messbereichs. false = nicht aufgezeichnet (dann kein <see cref="End"/> aufrufen).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Begin(ProfilerMarker marker)
    {
#if PARADISE_PROFILER
        if (!s_enabled) return false;
        BeginCore(marker.Id);
        return true;
#else
        return false;
#endif
    }

    static void BeginCore(int marker)
    {
        var b = Buf;
        if (b.Depth == b.Stack.Length) Array.Resize(ref b.Stack, b.Stack.Length * 2);
        ref var o = ref b.Stack[b.Depth++];
        o.Marker = marker;
        o.ChildTicks = 0;
        o.ChildAlloc = 0;
        o.Alloc = GC.GetAllocatedBytesForCurrentThread();
        o.Start = Stopwatch.GetTimestamp();   // zuletzt: die eigene Verwaltung zählt nicht mit
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void End(ProfilerMarker marker)
    {
#if PARADISE_PROFILER
        EndCore(marker.Id);
#endif
    }

    static void EndCore(int marker)
    {
        long end = Stopwatch.GetTimestamp();
        long alloc = GC.GetAllocatedBytesForCurrentThread();
        var b = t_buf;
        if (b == null || b.Depth == 0) return;
        // Ungleiche Begin/End (z. B. Ausnahme ohne using): bis zum passenden Bereich abbauen.
        int d = b.Depth - 1;
        while (d > 0 && b.Stack[d].Marker != marker) d--;
        if (b.Stack[d].Marker != marker) return;
        while (b.Depth - 1 > d) Close(b, end, alloc);
        Close(b, end, alloc);
    }

    static void Close(ThreadBuffer b, long end, long allocNow)
    {
        int depth = --b.Depth;
        ref var o = ref b.Stack[depth];
        long ticks = end - o.Start;
        long alloc = allocNow - o.Alloc;
        if (depth > 0)
        {
            ref var p = ref b.Stack[depth - 1];
            p.ChildTicks += ticks;
            p.ChildAlloc += alloc;
        }
        // max(0): nachgetragene Abschnitte (Record) können sich mit echten Kindern überlappen
        var e = new ProfilerEvent(o.Marker, depth, o.Start, end, System.Math.Max(0, ticks - o.ChildTicks), alloc, alloc - o.ChildAlloc);
        lock (b)
        {
            // Schutz, falls niemand EndFrame aufruft (z. B. Ladebildschirm ohne GameLoop): nicht unbegrenzt wachsen.
            if (b.Done.Count > 500_000) b.Done.Clear();
            b.Done.Add(e);
        }
    }

    /// <summary>
    /// Bereits gemessenen Abschnitt nachtragen (Dauer in ms, endet jetzt) – Brücke für die vorhandenen PerfLog.Add-Messpunkte
    /// (MapStreamer-Laps, Kollider, Kacheln bauen …). Wird als Kind des gerade offenen Messbereichs eingehängt und erbt dessen Kategorie.
    /// </summary>
    public static void Record(string name, double ms)
    {
#if PARADISE_PROFILER
        if (!s_enabled || !(ms > 0)) return;
        var b = Buf;
        long end = Stopwatch.GetTimestamp();
        long ticks = (long)(ms * Stopwatch.Frequency / 1000.0);
        long start = end - ticks;
        int depth = b.Depth;
        var cat = depth > 0 ? ProfilerMarker.ById(b.Stack[depth - 1].Marker)?.Category ?? ProfilerCategory.Other : ProfilerCategory.Other;
        var m = ProfilerMarker.Get(name, cat);
        // Abschnitte, die innerhalb dieses Zeitraums schon nachgetragen wurden (z. B. "Sektor bauen: …" in "Kacheln bauen"),
        // werden zu Kindern: eine Ebene tiefer, und nur ihre Zeit fehlt in der Eigenzeit dieses Abschnitts.
        long childSum = 0;
        lock (b)
        {
            var list = b.Done;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var e = list[i];
                if (e.End <= start || e.Start < start || e.Depth < depth) break;   // Done ist nach Ende geordnet
                if (e.Depth == depth) childSum += e.Ticks;
                list[i] = new ProfilerEvent(e.Marker, e.Depth + 1, e.Start, e.End, e.SelfTicks, e.Alloc, e.SelfAlloc);
            }
            list.Add(new ProfilerEvent(m.Id, depth, start, end, System.Math.Max(0, ticks - childSum), 0, 0));
        }
        // Die Kinder waren schon beim Eltern-Bereich abgezogen, jetzt nur noch der Rest dieses Abschnitts
        if (depth > 0) b.Stack[depth - 1].ChildTicks += System.Math.Max(0, ticks - childSum);
#endif
    }

    /// <summary>GPU-Zeit eines Durchgangs melden (Renderer, Hauptthread). Landet im nächsten abgeschlossenen Bild.</summary>
    public static void ReportGpu(ProfilerMarker pass, double ms)
    {
#if PARADISE_PROFILER
        if (!s_enabled) return;
        lock (s_gpuPending) s_gpuPending.Add(new ProfilerGpuSample(pass.Id, ms));
#endif
    }

    // ---------------------------------------------------------------------------------------------------- Bilder

    /// <summary>Bildende (GameLoop, Hauptthread): Messwerte aller Threads einsammeln und als Bild ablegen.</summary>
    public static void EndFrame()
    {
#if PARADISE_PROFILER
        long now = Stopwatch.GetTimestamp();
        var main = Buf;
        if (!main.IsMain) { main.IsMain = true; main.FixedName ??= "Main Thread"; }
        if (!s_enabled)
        {
            s_frameStart = now;
            lock (main) main.Done.Clear();
            return;
        }
        using (s_collect.Auto())
            Collect(main, s_frameStart, now);
        s_frameStart = now;
#endif
    }

    static void Collect(ThreadBuffer main, long start, long end)
    {
        var f = new ProfilerFrame { Index = s_frameIndex++, Start = start, End = end, Ms = TicksToMs(end - start) };

        var threads = new List<ProfilerThreadData>();
        ThreadBuffer[] all;
        lock (s_threads) all = s_threads.ToArray();
        var mainData = new ProfilerThreadData { Name = main.Name, ThreadId = main.ThreadId, IsMain = true };
        lock (main) { mainData.Events = main.Done.ToArray(); main.Done.Clear(); }
        threads.Add(mainData);
        foreach (var b in all)
        {
            if (b == main) continue;
            ProfilerEvent[] ev;
            lock (b)
            {
                if (b.Done.Count == 0) continue;
                ev = b.Done.ToArray();
                b.Done.Clear();
            }
            threads.Add(new ProfilerThreadData { Name = b.Name, ThreadId = b.ThreadId, Events = ev });
        }
        // Beendete Threads ohne offene Daten vergessen
        lock (s_threads)
            s_threads.RemoveAll(b => b != main && !b.Thread.IsAlive && b.Done.Count == 0);
        f.Threads = threads.ToArray();

        // CPU-Graph: Eigenzeit je Kategorie im Hauptthread, Rest = Others
        double sum = 0;
        foreach (var e in mainData.Events)
        {
            var m = ProfilerMarker.ById(e.Marker);
            double ms = TicksToMs(e.SelfTicks);
            f.CategoryMs[(int)(m?.Category ?? ProfilerCategory.Other)] += ms;
            sum += ms;
        }
        f.CategoryMs[(int)ProfilerCategory.Other] += System.Math.Max(0, f.Ms - sum);

        var counters = ProfilerCounter.All;
        f.Counters = new double[counters.Length];
        for (int i = 0; i < counters.Length; i++) f.Counters[i] = counters[i].Sample();

        lock (s_gpuPending) { f.Gpu = s_gpuPending.ToArray(); s_gpuPending.Clear(); }

        lock (s_framesLock)
        {
            s_frames.Enqueue(f);
            while (s_frames.Count > System.Math.Max(10, MaxFrames)) s_frames.Dequeue();
        }
    }

    /// <summary>Alle Bilder im Puffer (älteste zuerst) in <paramref name="dst"/> kopieren.</summary>
    public static void GetFrames(List<ProfilerFrame> dst)
    {
        dst.Clear();
        lock (s_framesLock) dst.AddRange(s_frames);
    }

    public static int FrameCount { get { lock (s_framesLock) return s_frames.Count; } }

    public static void Clear()
    {
        lock (s_framesLock) s_frames.Clear();
        s_gc?.Clear();
    }

    /// <summary>GC-Pausen im Zeitraum (Stopwatch-Ticks).</summary>
    public static void GetGcEvents(List<ProfilerGcEvent> dst) { dst.Clear(); s_gc?.CopyTo(dst); }
}
