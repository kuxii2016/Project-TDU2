using Paradise.Core.Diagnostics;

namespace Paradise.ProfilerUI;

/// <summary>Ein Knoten der Hierarchy (gleichnamige Aufrufe unter demselben Eltern-Knoten zusammengefasst, wie Unity).</summary>
internal sealed class HierarchyNode
{
    public int Marker = -1;
    public string Name = "";
    public ProfilerCategory Category;
    public long TotalTicks, SelfTicks;
    public long Alloc, SelfAlloc;
    public int Calls;
    /// <summary>Über wie viele Bilder summiert (für Mittelwerte bei "alle Bilder").</summary>
    public int Frames = 1;
    public HierarchyNode? Parent;
    public readonly List<HierarchyNode> Children = new();
    public readonly Dictionary<int, HierarchyNode> ByMarker = new();
    public bool Expanded;
    public int Depth;

    public HierarchyNode Child(int marker)
    {
        if (!ByMarker.TryGetValue(marker, out var n))
        {
            var m = ProfilerMarker.ById(marker);
            n = new HierarchyNode { Marker = marker, Name = m?.Name ?? ("#" + marker), Category = m?.Category ?? ProfilerCategory.Other, Parent = this, Depth = Depth + 1 };
            ByMarker[marker] = n;
            Children.Add(n);
        }
        return n;
    }

    /// <summary>Pfad wie "GameLoop.Update/ScreenModule.Update/GAME_MODE_FREERIDE.Run" (zum Wiederfinden nach Neuaufbau).</summary>
    public string Path => _path ??= Parent == null || Parent.Marker < 0 ? Name : Parent.Path + "/" + Name;
    string? _path;
}

internal static class HierarchyModel
{
    /// <summary>Baum aus den Messbereichen eines Threads (Ereignisse eines oder mehrerer Bilder).</summary>
    public static HierarchyNode Build(IEnumerable<ProfilerEvent[]> eventSets, int frames)
    {
        var root = new HierarchyNode { Depth = -1, Frames = Math.Max(1, frames) };
        var stack = new List<HierarchyNode>();
        var sorted = new List<ProfilerEvent>();
        foreach (var events in eventSets)
        {
            sorted.Clear();
            sorted.AddRange(events);
            // Nach Start; bei gleichem Start Eltern (kleinere Tiefe) zuerst
            sorted.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Depth.CompareTo(b.Depth));
            stack.Clear();
            foreach (var e in sorted)
            {
                while (stack.Count > e.Depth) stack.RemoveAt(stack.Count - 1);
                var parent = stack.Count > 0 ? stack[^1] : root;
                var n = parent.Child(e.Marker);
                n.TotalTicks += e.Ticks;
                n.SelfTicks += e.SelfTicks;
                n.Alloc += e.Alloc;
                n.SelfAlloc += e.SelfAlloc;
                n.Calls++;
                n.Frames = root.Frames;
                // Tiefe ohne passende Eltern (Aufnahme mitten in einem Bereich gestartet): flach einhängen
                while (stack.Count < e.Depth) stack.Add(parent);
                stack.Add(n);
            }
        }
        foreach (var c in root.Children) { root.TotalTicks += c.TotalTicks; root.Alloc += c.Alloc; }
        return root;
    }

    /// <summary>Flache Liste aller Knoten (zum Aufsummieren je Messpunkt).</summary>
    public static void Walk(HierarchyNode n, Action<HierarchyNode> visit)
    {
        foreach (var c in n.Children) { visit(c); Walk(c, visit); }
    }
}
