using Paradise.Core.Diagnostics;
using System.Globalization;
using System.Text;

namespace Paradise.ProfilerUI;

/// <summary>
/// Text-Bericht (.md) aus allen Bildern im Puffer: Bildzeiten, Kategorien, teuerste Messpunkte (Self/Total),
/// GC-Allokationen je Messpunkt, Ruckler mit Aufschlüsselung, GC-Pausen, GPU, Rendering, Speicher, Physik, Hinweise.
/// Ziel: Probleme ohne das Fenster nachvollziehen (auch zum Weitergeben).
/// </summary>
internal static class ReportExporter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    sealed class Agg
    {
        public string Name = "";
        public ProfilerCategory Category;
        public string Thread = "";
        public long Total, Self, Alloc, SelfAlloc;
        public int Calls;
        public double MaxTotalMs;   // größte Summe in einem Bild
        public long MaxFrame = -1;
    }

    public static string Write(IReadOnlyList<ProfilerFrame> frames, IReadOnlyList<ProfilerGcEvent> gcAll)
    {
        string dir = Path.Combine(Paradise.Core.Config.UserPaths.Logs, "Profiler");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Profiler_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv) + ".md");
        File.WriteAllText(path, Build(frames, gcAll), new UTF8Encoding(false));
        return path;
    }

    static string F(double v, string fmt = "0.00") => v.ToString(fmt, Inv);
    static string Ms(double v) => F(v) + " ms";
    static string B(double v) => ProfilerCounter.FormatBytes(v);

    static double Pct(double[] sorted, double p) => sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

    public static string Build(IReadOnlyList<ProfilerFrame> frames, IReadOnlyList<ProfilerGcEvent> gcAll)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Paradise Engine – Profiler-Bericht");
        sb.AppendLine();
        sb.AppendLine("Erstellt: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv));
        foreach (var kv in Profiler.GetInfo()) sb.AppendLine("- " + kv.Key + ": " + kv.Value);
        sb.AppendLine("- CPU-Threads: " + Environment.ProcessorCount.ToString(Inv) + ", OS: " + Environment.OSVersion);
#if DEBUG
        sb.AppendLine("- Build: **Debug** (unoptimiert – absolute Zeiten sind höher als im Release; für echte Werte Release mit -p:Profiler=true messen)");
#else
        sb.AppendLine("- Build: Release");
#endif
        sb.AppendLine();
        if (frames.Count == 0) { sb.AppendLine("Keine Bilder aufgenommen."); return sb.ToString(); }

        long t0 = frames[0].Start, t1 = frames[^1].End;
        double durS = Profiler.TicksToMs(t1 - t0) / 1000.0;
        var ms = frames.Select(f => f.Ms).ToArray();
        var sorted = (double[])ms.Clone();
        Array.Sort(sorted);
        double avg = ms.Average();
        var gc = gcAll.Where(e => e.End >= t0 && e.Start <= t1).ToList();

        // ---------------------------------------------------------------- Übersicht
        sb.AppendLine("## Übersicht");
        sb.AppendLine();
        sb.AppendLine("| Wert | |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Bilder | {frames.Count} (#{frames[0].Index} – #{frames[^1].Index}), {F(durS, "0.0")} s |");
        sb.AppendLine($"| FPS Ø | {F(frames.Count / Math.Max(0.001, durS), "0.0")} |");
        sb.AppendLine($"| Bildzeit Ø / Median | {Ms(avg)} / {Ms(Pct(sorted, 0.5))} |");
        sb.AppendLine($"| Bildzeit P95 / P99 / Max | {Ms(Pct(sorted, 0.95))} / {Ms(Pct(sorted, 0.99))} / {Ms(sorted[^1])} |");
        sb.AppendLine($"| Ruckler > 33 ms / > 50 ms / > 100 ms | {ms.Count(v => v > 33.3)} / {ms.Count(v => v > 50)} / {ms.Count(v => v > 100)} |");
        var gpuFrames = frames.Where(f => f.Gpu.Length > 0).ToList();
        if (gpuFrames.Count > 0)
            sb.AppendLine($"| GPU-Zeit Ø / Max | {Ms(gpuFrames.Average(f => f.GpuMs))} / {Ms(gpuFrames.Max(f => f.GpuMs))} |");
        sb.AppendLine($"| GC-Pausen | {gc.Count} (Gen0 {gc.Count(e => e.Generation == 0)}, Gen1 {gc.Count(e => e.Generation == 1)}, Gen2 {gc.Count(e => e.Generation == 2)}), zusammen {Ms(gc.Sum(e => e.Ms))}, längste {Ms(gc.Count > 0 ? gc.Max(e => e.Ms) : 0)} |");
        sb.AppendLine($"| GC Alloc je Bild Ø / Max | {B(frames.Average(f => f.Counter(ProfilerCounters.GcAllocFrame)))} / {B(frames.Max(f => f.Counter(ProfilerCounters.GcAllocFrame)))} |");
        sb.AppendLine();

        // ---------------------------------------------------------------- Kategorien
        sb.AppendLine("## CPU nach Kategorie (Hauptthread, Eigenzeit)");
        sb.AppendLine();
        sb.AppendLine("| Kategorie | Ø ms | Anteil | Max ms |");
        sb.AppendLine("|---|---:|---:|---:|");
        for (int c = 0; c < ProfilerCategories.Count; c++)
        {
            double a = frames.Average(f => f.CategoryMs[c]);
            double mx = frames.Max(f => f.CategoryMs[c]);
            if (mx < 0.005) continue;
            sb.AppendLine($"| {ProfilerCategories.Name((ProfilerCategory)c)} | {F(a)} | {F(a * 100 / Math.Max(0.001, avg), "0.0")} % | {F(mx)} |");
        }
        sb.AppendLine();

        // ---------------------------------------------------------------- Messpunkte aggregieren
        var aggs = new Dictionary<(string thread, int marker), Agg>();
        foreach (var f in frames)
        {
            var perFrame = new Dictionary<(string, int), long>();
            foreach (var t in f.Threads)
            {
                string tn = t.IsMain ? "Main Thread" : t.Name;
                foreach (var e in t.Events)
                {
                    var key = (tn, e.Marker);
                    if (!aggs.TryGetValue(key, out var a))
                    {
                        var m = ProfilerMarker.ById(e.Marker);
                        aggs[key] = a = new Agg { Name = m?.Name ?? "#" + e.Marker, Category = m?.Category ?? ProfilerCategory.Other, Thread = tn };
                    }
                    // Total nur für die äußerste Ebene eines Messpunkts zählen wäre genauer; rekursive Marker sind selten.
                    a.Total += e.Ticks; a.Self += e.SelfTicks; a.Alloc += e.Alloc; a.SelfAlloc += e.SelfAlloc; a.Calls++;
                    perFrame[key] = perFrame.GetValueOrDefault(key) + e.Ticks;
                }
            }
            foreach (var kv in perFrame)
            {
                var a = aggs[kv.Key];
                double v = Profiler.TicksToMs(kv.Value);
                if (v > a.MaxTotalMs) { a.MaxTotalMs = v; a.MaxFrame = f.Index; }
            }
        }
        int n = frames.Count;
        var main = aggs.Values.Where(a => a.Thread == "Main Thread").ToList();

        sb.AppendLine("## Teuerste Messpunkte im Hauptthread (Eigenzeit = was dort selbst passiert)");
        sb.AppendLine();
        AggTable(sb, main.OrderByDescending(a => a.Self).Take(30), n, avg);

        sb.AppendLine("## Teuerste Messpunkte im Hauptthread (Gesamtzeit inkl. Unteraufrufe)");
        sb.AppendLine();
        AggTable(sb, main.OrderByDescending(a => a.Total).Take(20), n, avg);

        var allocs = aggs.Values.Where(a => a.SelfAlloc > 0).OrderByDescending(a => a.SelfAlloc).Take(20).ToList();
        sb.AppendLine("## GC-Allokationen je Messpunkt (Self = direkt dort, nicht in Unteraufrufen)");
        sb.AppendLine();
        if (allocs.Count == 0) sb.AppendLine("Keine Allokationen in Messbereichen.");
        else
        {
            sb.AppendLine("| Messpunkt | Thread | Self Alloc / Bild | Alloc inkl. Kinder / Bild | Aufrufe / Bild |");
            sb.AppendLine("|---|---|---:|---:|---:|");
            foreach (var a in allocs)
                sb.AppendLine($"| {Esc(a.Name)} | {Esc(a.Thread)} | {B(a.SelfAlloc / (double)n)} | {B(a.Alloc / (double)n)} | {F(a.Calls / (double)n, "0.#")} |");
        }
        sb.AppendLine();

        // ---------------------------------------------------------------- Ruckler
        sb.AppendLine("## Langsamste Bilder (Ruckler) mit Aufschlüsselung");
        sb.AppendLine();
        foreach (var f in frames.OrderByDescending(x => x.Ms).Take(10))
        {
            var root = HierarchyModel.Build(new[] { f.Main.Events }, 1);
            var gcHit = gc.Where(e => e.End >= f.Start && e.Start <= f.End).ToList();
            sb.Append($"### Bild #{f.Index}: {Ms(f.Ms)}");
            if (f.Gpu.Length > 0) sb.Append($" (GPU {Ms(f.GpuMs)})");
            sb.AppendLine($", +{F(Profiler.TicksToMs(f.Start - t0) / 1000.0, "0.00")} s nach Aufnahmebeginn");
            if (gcHit.Count > 0)
                sb.AppendLine("- **GC-Pause in diesem Bild:** " + string.Join(", ", gcHit.Select(e => $"Gen{e.Generation} {Ms(e.Ms)} ({e.Reason})")));
            sb.AppendLine("- GC Alloc: " + B(f.Counter(ProfilerCounters.GcAllocFrame)));
            sb.AppendLine("- Kategorien: " + string.Join(", ", Enumerable.Range(0, ProfilerCategories.Count)
                .Where(c => f.CategoryMs[c] >= 0.5).OrderByDescending(c => f.CategoryMs[c])
                .Select(c => ProfilerCategories.Name((ProfilerCategory)c) + " " + Ms(f.CategoryMs[c]))));
            sb.AppendLine();
            sb.AppendLine("```");
            WriteTree(sb, root, f.Ms, 0);
            sb.AppendLine("```");
            // Arbeits-Threads in diesem Bild
            foreach (var t in f.Threads.Where(x => !x.IsMain && x.Events.Length > 0))
            {
                long busy = t.Events.Where(e => e.Depth == 0).Sum(e => e.Ticks);
                sb.AppendLine($"- Thread {t.Name}: {Ms(Profiler.TicksToMs(busy))} beschäftigt");
            }
            sb.AppendLine();
        }

        // ---------------------------------------------------------------- Arbeits-Threads
        var workers = aggs.Values.Where(a => a.Thread != "Main Thread").GroupBy(a => a.Thread).ToList();
        if (workers.Count > 0)
        {
            sb.AppendLine("## Arbeits-Threads");
            sb.AppendLine();
            foreach (var grp in workers)
            {
                sb.AppendLine("### " + grp.Key);
                sb.AppendLine();
                AggTable(sb, grp.OrderByDescending(a => a.Self).Take(10), n, avg);
            }
        }

        // ---------------------------------------------------------------- GPU
        if (gpuFrames.Count > 0)
        {
            sb.AppendLine("## GPU-Durchgänge (Vulkan-Zeitstempel)");
            sb.AppendLine();
            sb.AppendLine("| Durchgang | Ø ms | Max ms |");
            sb.AppendLine("|---|---:|---:|");
            var passes = gpuFrames.SelectMany(f => f.Gpu).GroupBy(g => g.Marker);
            foreach (var p in passes.OrderByDescending(q => q.Average(x => x.Ms)))
                sb.AppendLine($"| {Esc(ProfilerMarker.ById(p.Key)?.Name ?? "?")} | {F(p.Average(x => x.Ms))} | {F(p.Max(x => x.Ms))} |");
            sb.AppendLine();
        }

        // ---------------------------------------------------------------- Zähler
        foreach (var group in new[] { ProfilerCounters.Rendering, ProfilerCounters.SceneDetail, ProfilerCounters.Flash, ProfilerCounters.Memory, ProfilerCounters.Physics })
        {
            var cs = ProfilerCounter.All.Where(c => c.Group == group).ToList();
            if (cs.Count == 0) continue;
            sb.AppendLine("## " + group);
            sb.AppendLine();
            sb.AppendLine("| Zähler | Anfang | Ende | Ø | Min | Max |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|");
            foreach (var c in cs)
            {
                var v = frames.Select(f => f.Counter(c)).ToArray();
                if (v.All(x => x == 0)) continue;
                sb.AppendLine($"| {Esc(c.Name)} | {c.Format(v[0])} | {c.Format(v[^1])} | {c.Format(v.Average())} | {c.Format(v.Min())} | {c.Format(v.Max())} |");
            }
            sb.AppendLine();
        }

        // ---------------------------------------------------------------- GC-Pausen
        if (gc.Count > 0)
        {
            sb.AppendLine("## Längste GC-Pausen");
            sb.AppendLine();
            sb.AppendLine("| # | Gen | Grund | Pause | Zeitpunkt |");
            sb.AppendLine("|---:|---:|---|---:|---:|");
            foreach (var e in gc.OrderByDescending(x => x.Ms).Take(15))
                sb.AppendLine($"| {e.Index} | {e.Generation} | {e.Reason} | {Ms(e.Ms)} | +{F(Profiler.TicksToMs(e.Start - t0) / 1000.0)} s |");
            sb.AppendLine();
        }

        // ---------------------------------------------------------------- Hinweise
        sb.AppendLine("## Automatische Hinweise");
        sb.AppendLine();
        var hints = new List<string>();
        double waitAvg = frames.Average(f => f.CategoryMs[(int)ProfilerCategory.Wait]);
        if (waitAvg > avg * 0.3)
            hints.Add($"Der Hauptthread wartet im Schnitt {Ms(waitAvg)} ({F(waitAvg * 100 / avg, "0")} %) auf GPU/VSync → eher **GPU-begrenzt** (oder VSync aktiv). GPU-Durchgänge oben prüfen.");
        else if (avg > 16.7)
            hints.Add($"Wenig Wartezeit auf die GPU, Bildzeit Ø {Ms(avg)} → **CPU-begrenzt**. Teuerste Messpunkte (Eigenzeit) oben prüfen.");
        var top = main.Where(a => a.Category != ProfilerCategory.Wait).OrderByDescending(a => a.Self).FirstOrDefault();
        if (top != null && Profiler.TicksToMs(top.Self) / n > avg * 0.25)
            hints.Add($"**{Esc(top.Name)}** braucht allein {F(Profiler.TicksToMs(top.Self) / n * 100 / avg, "0")} % der Bildzeit (Eigenzeit) – erster Kandidat für Optimierung bzw. feinere Messpunkte darin.");
        double allocAvg = frames.Average(f => f.Counter(ProfilerCounters.GcAllocFrame));
        if (allocAvg > 256 * 1024)
            hints.Add($"Im Schnitt {B(allocAvg)} GC-Allokation pro Bild – das erzeugt regelmäßig GC-Läufe. Tabelle \"GC-Allokationen je Messpunkt\" zeigt die Verursacher.");
        int gen2 = gc.Count(e => e.Generation == 2);
        if (gen2 > 0)
            hints.Add($"{gen2} Gen2-GC(s) in der Aufnahme (längste {Ms(gc.Where(e => e.Generation == 2).Max(e => e.Ms))}) – vollständige Bereinigung, oft Ursache für spürbare Ruckler (große Arrays/LOH beim Streaming?).");
        int spikesWithGc = frames.Count(f => f.Ms > 33.3 && gc.Any(e => e.End >= f.Start && e.Start <= f.End && e.Ms > 2));
        if (spikesWithGc > 0)
            hints.Add($"{spikesWithGc} Ruckler fallen mit einer GC-Pause > 2 ms zusammen.");
        int spikes = ms.Count(v => v > 33.3);
        if (spikes > 0 && spikes < n / 10)
            hints.Add($"{spikes} einzelne Ruckler bei sonst ruhiger Bildzeit – Abschnitt \"Langsamste Bilder\" zeigt, was dort jeweils lief (Streaming/Laden, Moduswechsel, GC).");
        if (hints.Count == 0) hints.Add("Keine Auffälligkeiten nach den einfachen Regeln.");
        foreach (var h in hints) sb.AppendLine("- " + h);
        sb.AppendLine();
        return sb.ToString();
    }

    static void AggTable(StringBuilder sb, IEnumerable<Agg> rows, int frames, double avgFrame)
    {
        sb.AppendLine("| Messpunkt | Kategorie | Self Ø ms | Total Ø ms | Self % | Max Total/Bild | Aufrufe / Bild | GC Alloc / Bild |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (var a in rows)
        {
            double self = Profiler.TicksToMs(a.Self) / frames, total = Profiler.TicksToMs(a.Total) / frames;
            sb.AppendLine($"| {Esc(a.Name)} | {ProfilerCategories.Name(a.Category)} | {F(self, "0.000")} | {F(total, "0.000")} | {F(self * 100 / Math.Max(0.001, avgFrame), "0.0")} % | {F(a.MaxTotalMs)} ms (#{a.MaxFrame}) | {F(a.Calls / (double)frames, "0.#")} | {B(a.Alloc / (double)frames)} |");
        }
        sb.AppendLine();
    }

    static void WriteTree(StringBuilder sb, HierarchyNode n, double frameMs, int depth)
    {
        foreach (var c in n.Children.OrderByDescending(x => x.TotalTicks))
        {
            double total = Profiler.TicksToMs(c.TotalTicks);
            if (total < Math.Max(0.2, frameMs * 0.02) && depth > 0) continue;   // Kleinkram weglassen
            sb.Append(new string(' ', depth * 2)).Append(c.Name).Append("  ").Append(F(total)).Append(" ms (self ").Append(F(Profiler.TicksToMs(c.SelfTicks)))
              .Append(", ").Append(c.Calls.ToString(Inv)).Append("x");
            if (c.Alloc > 0) sb.Append(", alloc ").Append(B(c.Alloc));
            sb.AppendLine(")");
            if (depth < 8) WriteTree(sb, c, frameMs, depth + 1);
        }
    }

    static string Esc(string s) => s.Replace("|", "\\|");
}
