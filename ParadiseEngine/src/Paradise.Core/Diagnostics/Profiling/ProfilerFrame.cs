using System.Diagnostics;

namespace Paradise.Core.Diagnostics;

/// <summary>Ein abgeschlossener Messbereich (Zeiten in Stopwatch-Ticks).</summary>
public readonly struct ProfilerEvent
{
    public readonly int Marker;
    /// <summary>Verschachtelungstiefe auf dem Thread (0 = oberste Ebene).</summary>
    public readonly int Depth;
    public readonly long Start, End;
    /// <summary>Eigenzeit: Dauer ohne die Kind-Messbereiche.</summary>
    public readonly long SelfTicks;
    /// <summary>Speicher, den dieser Thread im Bereich angefordert hat (GC Alloc, inkl. Kinder).</summary>
    public readonly long Alloc;
    /// <summary>GC Alloc ohne Kinder.</summary>
    public readonly long SelfAlloc;

    public ProfilerEvent(int marker, int depth, long start, long end, long selfTicks, long alloc, long selfAlloc)
    {
        Marker = marker; Depth = depth; Start = start; End = end; SelfTicks = selfTicks; Alloc = alloc; SelfAlloc = selfAlloc;
    }

    public long Ticks => End - Start;
}

/// <summary>Messbereiche eines Threads in einem Bild.</summary>
public sealed class ProfilerThreadData
{
    public string Name = "";
    public int ThreadId;
    public bool IsMain;
    public ProfilerEvent[] Events = Array.Empty<ProfilerEvent>();
}

/// <summary>GPU-Zeit eines Render-Durchgangs (Vulkan-Zeitstempel, kommt 1–2 Bilder verzögert).</summary>
public readonly struct ProfilerGpuSample
{
    public readonly int Marker;
    public readonly double Ms;
    public ProfilerGpuSample(int marker, double ms) { Marker = marker; Ms = ms; }
}

/// <summary>Speicherbereinigung (aus den Laufzeit-Ereignissen der .NET-Runtime).</summary>
public readonly struct ProfilerGcEvent
{
    /// <summary>Angehaltene Threads: SuspendEE bis RestartEE (Stopwatch-Ticks).</summary>
    public readonly long Start, End;
    public readonly int Generation;
    public readonly string Reason;
    public readonly int Index;

    public ProfilerGcEvent(long start, long end, int generation, string reason, int index)
    {
        Start = start; End = end; Generation = generation; Reason = reason; Index = index;
    }

    public double Ms => (End - Start) * 1000.0 / Stopwatch.Frequency;
}

/// <summary>
/// Ein aufgezeichnetes Bild. Wird auf dem Spiel-Thread fertig gebaut und danach nicht mehr verändert,
/// darf also ohne Sperre im UI-Thread gelesen werden.
/// </summary>
public sealed class ProfilerFrame
{
    public long Index;
    public long Start, End;
    public double Ms;
    /// <summary>[0] = Hauptthread, danach Arbeits-Threads, die in diesem Bild etwas gemessen haben.</summary>
    public ProfilerThreadData[] Threads = Array.Empty<ProfilerThreadData>();
    /// <summary>Eigenzeit je <see cref="ProfilerCategory"/> im Hauptthread (ms); Others = Rest bis zur Bildzeit.</summary>
    public double[] CategoryMs = new double[ProfilerCategories.Count];
    /// <summary>Werte je Zähler (<see cref="ProfilerCounter.Id"/>), Stand am Bildende.</summary>
    public double[] Counters = Array.Empty<double>();
    public ProfilerGpuSample[] Gpu = Array.Empty<ProfilerGpuSample>();

    public ProfilerThreadData Main => Threads[0];

    public double Counter(ProfilerCounter c) => (uint)c.Id < (uint)Counters.Length ? Counters[c.Id] : 0;

    public double GpuMs
    {
        get
        {
            double s = 0;
            foreach (var g in Gpu)
                if (ProfilerMarker.ById(g.Marker)?.Name == Profiler.GpuTotalName) return g.Ms;
                else s += g.Ms;
            return s;
        }
    }
}
