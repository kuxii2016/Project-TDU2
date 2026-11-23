namespace Paradise.Core.Diagnostics;

/// <summary>
/// Zeit-Diagnose ([DIAG Perf] im Log, alle <see cref="IntervalSeconds"/> s): Ø/Max je Messpunkt und Bild (Flash je Film, Streamer,
/// Physik …) sowie die Zahl der Ruckler (Bilder &gt; 33 ms). Zeigt, wo die Hauptthread-Zeit hingeht.
/// </summary>
public static class PerfLog
{
    public static double IntervalSeconds = 10;
    sealed class Acc { public double Sum, Max; public int N; }
    static readonly Dictionary<string, Acc> _acc = new();
    static double _next = 15;
    static int _frames, _hitches;
    static double _frameMax;
    static int _gc0, _gc2; static long _alloc;

    /// <summary>Messwert in ms (Hauptthread).</summary>
    public static void Add(string name, double ms)
    {
        Profiler.Record(name, ms);   // Engine-Profiler (F1 > Performance > Open Profiler): erscheint als Unterpunkt in Hierarchy/Timeline
        //if (!_acc.TryGetValue(name, out var a)) _acc[name] = a = new Acc();
        //a.Sum += ms; a.N++; if (ms > a.Max) a.Max = ms;
    }

    /// <summary>Einmal je Bild (Bildzeit in s).</summary>
    public static void Frame(double time, float dt)
    {
        //_frames++;
        //double ms = dt * 1000.0;
        //if (ms > 33.0) _hitches++;
        //if (ms > _frameMax) _frameMax = ms;
        //if (time < _next) return;
        //_next = time + IntervalSeconds;
        //var sb = new System.Text.StringBuilder();
        //double fps = _acc.TryGetValue("BILD GESAMT", out var bg) && bg.Sum > 0 ? 1000.0 * bg.N / bg.Sum : 0;
        //int gc0 = GC.CollectionCount(0), gc2 = GC.CollectionCount(2);
        //sb.Append($"[DIAG Perf] {_frames} Bilder, {fps:0} FPS, Ruckler > 33 ms: {_hitches}, längstes Bild {_frameMax:0} ms, GC Gen0 +{gc0 - _gc0} Gen2 +{gc2 - _gc2}, Speicher belegt {GC.GetAllocatedBytesForCurrentThread() / 1048576 - _alloc} MB im Hauptthread");
        //_gc0 = gc0; _gc2 = gc2; _alloc = GC.GetAllocatedBytesForCurrentThread() / 1048576;
        //foreach (var kv in _acc.OrderByDescending(k => k.Value.Sum))
        //{
        //    var a = kv.Value;
        //    if (a.N == 0) continue;
        //    sb.Append($" | {kv.Key} Ø {a.Sum / System.Math.Max(1, _frames):0.00} ms/Bild, max {a.Max:0.0} ms ({a.N}x)");
        //}
        //Log.Info(sb.ToString());
        //_acc.Clear(); _frames = 0; _hitches = 0; _frameMax = 0;
    }
}
