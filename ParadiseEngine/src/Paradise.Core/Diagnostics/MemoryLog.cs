namespace Paradise.Core.Diagnostics;

/// <summary>
/// Speicher-Diagnose ([DIAG Mem] im Log, alle <see cref="IntervalSeconds"/> s): Prozess, .NET-Heap (lebende Daten nach der letzten
/// Speicherbereinigung, Fragmentierung, Allokationsrate) und registrierte Zähler (Streamer, Physik, GPU …). Zeigt nach einer Fahrt,
/// welcher Teil wächst.
/// </summary>
public static class MemoryLog
{
    public static double IntervalSeconds = 30;
    static readonly List<(string name, Func<string> value)> _sources = new();
    static double _next = 10, _lastTime;
    static long _lastAllocated;
    static readonly System.Diagnostics.Process _proc = System.Diagnostics.Process.GetCurrentProcess();

    public static void Register(string name, Func<string> value) => _sources.Add((name, value));

    public static void Update(double time)
    {
        //if (time < _next) return;
        //_next = time + IntervalSeconds;
        //try
        //{
        //    _proc.Refresh();
        //    var gi = GC.GetGCMemoryInfo(GCKind.Any);
        //    var full = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        //    var bg = GC.GetGCMemoryInfo(GCKind.Background);
        //    long alloc = GC.GetTotalAllocatedBytes(false);
        //    double rate = _lastTime > 0 && time > _lastTime ? (alloc - _lastAllocated) / (time - _lastTime) : 0;
        //    _lastAllocated = alloc; _lastTime = time;
        //    // lebende Daten nach der letzten vollständigen Bereinigung (Gen2 vollständig oder Hintergrund)
        //    var last2 = full.Index > bg.Index ? full : bg;
        //    long live2 = 0;
        //    if (last2.Index > 0) foreach (var g in last2.GenerationInfo) live2 += g.SizeAfterBytes - g.FragmentationAfterBytes;
        //    var gens = new List<long>();
        //    foreach (var g in gi.GenerationInfo) gens.Add(g.SizeAfterBytes);
        //    string GenMb(int i) => i < gens.Count ? Mb(gens[i]) : "-";
        //    var sb = new System.Text.StringBuilder();
        //    sb.Append($"[DIAG Mem] Prozess privat {Mb(_proc.PrivateMemorySize64)}, Arbeitssatz {Mb(_proc.WorkingSet64)}");
        //    sb.Append($" | .NET belegt {Mb(GC.GetTotalMemory(false))}, Heap {Mb(gi.HeapSizeBytes)}, frag. {Mb(gi.FragmentedBytes)}, committed {Mb(gi.TotalCommittedBytes)}");
        //    sb.Append($", lebend nach Gen2 #{last2.Index} {Mb(live2)}, Gen0/1/2/LOH/POH {GenMb(0)}/{GenMb(1)}/{GenMb(2)}/{GenMb(3)}/{GenMb(4)}");
        //    sb.Append($", Allokation {rate / (1024 * 1024):0.0} MB/s, GCs {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}, Pause {gi.PauseTimePercentage:0.0} %");
        //    sb.Append($", Modus {(GCSettings.IsServerGC ? "Server" : "Workstation")}/{GCSettings.LatencyMode}, ConserveMemory {AppContext.GetData("System.GC.ConserveMemory") ?? "-"}");
        //    foreach (var (name, value) in _sources)
        //    {
        //        string v;
        //        try { v = value(); } catch (Exception ex) { v = "Fehler " + ex.Message; }
        //        sb.Append(" | ").Append(name).Append(' ').Append(v);
        //    }
        //    Log.Info(sb.ToString());
        //}
        //catch (Exception ex) { Log.Warn("[DIAG Mem] " + ex.Message); }
    }

    public static string Mb(long b) => (b / (1024.0 * 1024.0)).ToString("0") + " MB";
    public static string Mb(ulong b) => (b / (1024.0 * 1024.0)).ToString("0") + " MB";
}
