// Szenen-Editor der Paradise Engine (F1 > Tools > Open Editor): Hierarchy + Inspector wie in Unity, als zwei WinForms-Fenster.
// Aufbau wie Paradise.FlashDebug.FlashBrowser: Die Fenster laufen auf einem eigenen STA-Thread im Spielprozess. Alle
// Engine-Zugriffe passieren in Pump() auf dem Spiel-Thread; die UI schickt nur Text-Kommandos und bekommt kopierte Snapshots.
// Das ausgewählte Objekt wird im 3D-Bild über die DebugLines markiert (Box + Name, wie die Unity-Auswahl im Scene-View).

using Paradise.Core.Diagnostics;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Paradise.Editor;

public static class SceneEditor
{
    static readonly ConcurrentQueue<(string Cmd, string[] Args)> _queue = new();
    static readonly object _lock = new();
    static EditorContext? _ctx;
    static Thread? _thread;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly Stopwatch _clock = Stopwatch.StartNew();

    // Immer nur den LETZTEN Snapshot anwenden (wie FlashBrowser): keine gestapelten BeginInvoke bei beschäftigter UI.
    static SceneSnapshot? _pending;
    static int _postPending;
    const double SnapshotInterval = 0.25;   // 4 Hz; Kommandos lösen sofort einen neuen Snapshot aus
    const double SearchInterval = 1.5;
    const int MaxChildren = 4000;           // je aufgeklapptem Eintrag (Rest als "… n weitere")
    const int SearchBudget = 250_000;       // besuchte Einträge je Suchlauf
    const int SearchMax = 500;              // Treffer
    const int GizmoChildMax = 200;

    // ------------------------------------------------------------------ Anmeldung durch das Spiel
    static readonly List<(string Key, Func<SceneNode?> Make)> _roots = new();
    /// <summary>Render-Raum → Spielraum (MapStreamer.RenderToGame), für Autos (CarNode.World) und die DebugLines.</summary>
    public static Func<Vector3, Vector3>? RenderToGame;
    /// <summary>Kamera im Spielraum (LOD-Anzeige).</summary>
    public static Func<Vector3>? CameraGame;

    /// <summary>Wurzel der Hierarchie anmelden/ersetzen (Reihenfolge = Reihenfolge der ersten Anmeldung). null-Ergebnis = gerade nicht vorhanden.</summary>
    public static void SetRoot(string key, Func<SceneNode?> make)
    {
        lock (_roots)
        {
            int i = _roots.FindIndex(r => r.Key == key);
            if (i >= 0) _roots[i] = (key, make); else _roots.Add((key, make));
        }
    }

    // ------------------------------------------------------------------ Zustand (Spiel-Thread)
    static readonly HashSet<string> _expanded = new() { "g:world" };
    static readonly Dictionary<string, SceneNode> _map = new();
    static readonly Dictionary<string, SceneNode> _searchMap = new();
    static readonly Dictionary<string, string[]> _searchAncestors = new();
    static SceneNode? _sel;
    static readonly List<SceneNode> _selChildren = new();
    static string _filter = "";
    static bool _live = true, _gizmo = true, _gizmoChildren;
    static string _status = "";
    static List<TreeItem>? _search;
    static int _searchVisited;
    static bool _searchTrunc, _force;
    static double _nextSnap, _nextSearch;

    public static bool IsOpen { get { lock (_lock) return _ctx != null && _thread != null && _thread.IsAlive; } }

    /// <summary>F1 > Tools > Open Editor. Öffnet beide Fenster (oder holt sie nach vorne).</summary>
    public static void Open()
    {
        TaskCompletionSource<bool> ready;
        Thread t;
        lock (_lock)
        {
            if (IsOpen)
            {
                var c = _ctx;
                if (c != null)
                    try { c.Hierarchy.BeginInvoke(new Action(c.FocusAll)); } catch (Exception) { }
                return;
            }
            ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            t = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    var ctx = new EditorContext();
                    lock (_lock) _ctx = ctx;
                    ready.TrySetResult(true);
                    Application.Run(ctx);
                }
                catch (Exception ex)
                {
                    Log.Error("Scene Editor: " + ex);
                    ready.TrySetResult(false);
                }
                finally { lock (_lock) _ctx = null; }
            })
            { IsBackground = true, Name = "Paradise Scene Editor" };
            t.SetApartmentState(ApartmentState.STA);
            _thread = t;
            Volatile.Write(ref _pending, null);
            Interlocked.Exchange(ref _postPending, 0);
            _force = true;
        }
        // nicht im lock warten – der UI-Thread setzt _ctx ebenfalls unter _lock
        t.Start();
        try { ready.Task.Wait(TimeSpan.FromSeconds(5)); } catch (Exception) { }
    }

    public static void Close()
    {
        var c = _ctx;
        if (c != null)
            try { c.Hierarchy.BeginInvoke(new Action(c.ExitThread)); } catch (Exception) { }
    }

    /// <summary>Vom UI-Thread: Kommando für den Spiel-Thread.</summary>
    internal static void Send(string cmd, params string[] args) => _queue.Enqueue((cmd, args ?? Array.Empty<string>()));

    /// <summary>Jedes Bild aus dem Spiel (DevMenu.Update, nach VehicleModule: DebugLines.Clear) aufrufen.</summary>
    public static void Pump()
    {
        if (!IsOpen) { if (_sel != null) { _sel = null; _selChildren.Clear(); } return; }

        int n = 0;
        while (n++ < 128 && _queue.TryDequeue(out var c))
        {
            try { Execute(c.Cmd, c.Args); }
            catch (Exception ex) { _status = c.Cmd + ": " + ex.Message; Log.Warn("Scene Editor " + c.Cmd + ": " + ex.Message); }
            _force = true;
        }

        if (_sel != null && !_sel.IsAlive)
        {
            _status = "\"" + _sel.Name + "\" wurde entladen";
            _sel = null; _selChildren.Clear(); _force = true;
        }
        if (_gizmo && _sel != null) DrawGizmo();

        double now = _clock.Elapsed.TotalSeconds;
        if (!_force && (!_live || now < _nextSnap)) return;
        _nextSnap = now + SnapshotInterval;
        _force = false;

        SceneSnapshot snap;
        try { snap = Build(now); }
        catch (Exception ex) { Log.Warn("Scene Editor Snapshot: " + ex.Message); return; }

        var ctx = _ctx;
        if (ctx == null || ctx.Hierarchy.IsDisposed) return;
        Volatile.Write(ref _pending, snap);
        if (Interlocked.Exchange(ref _postPending, 1) != 0) return;   // schon ein Apply unterwegs
        try { ctx.Hierarchy.BeginInvoke(new Action(() => PostApply(ctx))); }
        catch (Exception) { Interlocked.Exchange(ref _postPending, 0); }
    }

    static void PostApply(EditorContext ctx)
    {
        Interlocked.Exchange(ref _postPending, 0);
        var s = Volatile.Read(ref _pending);
        Volatile.Write(ref _pending, null);
        if (s != null) ctx.Apply(s);
    }

    static SceneNode? Find(string id)
    {
        if (_sel != null && _sel.Id == id) return _sel;
        if (_map.TryGetValue(id, out var n)) return n;
        return _searchMap.TryGetValue(id, out n) ? n : null;
    }

    static string S(string[] a, int i) => a.Length > i ? a[i] : "";
    static bool B(string[] a, int i) => S(a, i) == "1";

    static void Execute(string cmd, string[] a)
    {
        switch (cmd)
        {
            case "select":
            {
                var node = Find(S(a, 0));
                _sel = node; _selChildren.Clear();
                _status = node == null ? "" : node.Type;
                // Treffer aus der Suche: Pfad in der Hierarchie aufklappen (Unity: Ping im Hierarchy-Fenster)
                if (node != null && _searchAncestors.TryGetValue(node.Id, out var anc))
                    foreach (var id in anc) _expanded.Add(id);
                break;
            }
            case "deselect": _sel = null; _selChildren.Clear(); break;
            case "expand":
                if (B(a, 1)) _expanded.Add(S(a, 0)); else _expanded.Remove(S(a, 0));
                break;
            case "collapse_all": _expanded.Clear(); break;
            case "filter":
                _filter = S(a, 0).Trim();
                _nextSearch = 0;
                if (_filter.Length == 0) { _search = null; _searchMap.Clear(); }
                break;
            case "live": _live = B(a, 0); break;
            case "gizmo": _gizmo = B(a, 0); break;
            case "gizmo_children": _gizmoChildren = B(a, 0); _selChildren.Clear(); break;
            case "refresh": _nextSearch = 0; break;
            case "active":
            {
                var node = Find(S(a, 0));
                if (node?.SetActive == null) { _status = "Objekt hat keinen Aktiv-Schalter"; break; }
                node.SetActive(B(a, 1));
                _status = node.Name + (B(a, 1) ? " aktiviert" : " deaktiviert");
                break;
            }
            case "set":
            {
                var node = Find(S(a, 0));
                if (node == null) { _status = "Objekt nicht mehr vorhanden"; break; }
                bool ok = node.Edit != null && node.Edit(S(a, 1), S(a, 2));
                _status = ok ? node.Name + ": " + S(a, 1) + " geändert" : node.Name + ": " + S(a, 1) + " nicht änderbar / ungültiger Wert";
                break;
            }
            default: _status = "Unbekanntes Kommando: " + cmd; break;
        }
    }

    // ------------------------------------------------------------------ 3D-Markierung (DebugLines)
    static void DrawGizmo()
    {
        var sel = _sel!;
        var r2g = RenderToGame ?? (v => v);
        try
        {
            if (_gizmoChildren && _selChildren.Count > 0)
            {
                var cg = new SceneGizmo(r2g) { ChildMode = true };
                foreach (var c in _selChildren) if (c.IsAlive) c.Gizmo?.Invoke(cg);
            }
            var g = new SceneGizmo(r2g) { Label = sel.Name };
            sel.Gizmo?.Invoke(g);
        }
        catch (Exception) { /* Objekt wird gerade umgebaut – nächstes Bild */ }
    }

    // ------------------------------------------------------------------ Snapshot
    static List<(string Key, Func<SceneNode?> Make)> Roots() { lock (_roots) return _roots.ToList(); }

    static SceneSnapshot Build(double now)
    {
        _map.Clear();
        var snap = new SceneSnapshot { Live = _live, Gizmo = _gizmo, GizmoChildren = _gizmoChildren };
        int visible = 0;
        var rootNodes = new List<SceneNode>();
        foreach (var (key, make) in Roots())
        {
            SceneNode? n = null;
            try { n = make(); } catch (Exception ex) { _status = key + ": " + ex.Message; }
            if (n != null) rootNodes.Add(n);
        }
        foreach (var n in rootNodes) snap.Roots.Add(BuildItem(n, ref visible));
        snap.Visible = visible;

        // Auswahl auf den frischen Eintrag gleicher Id umsetzen (aktueller Name/Typ)
        if (_sel != null && _map.TryGetValue(_sel.Id, out var fresh)) _sel = fresh;

        if (_filter.Length > 0)
        {
            if (_search == null || now >= _nextSearch)
            {
                _nextSearch = now + SearchInterval;
                RunSearch(rootNodes);
            }
            snap.Search = _search;
            snap.SearchVisited = _searchVisited;
            snap.SearchTruncated = _searchTrunc;
        }

        if (_sel != null)
        {
            snap.SelectedId = _sel.Id;
            snap.Inspector = BuildInspector(_sel);
            _selChildren.Clear();
            if (_gizmoChildren) foreach (var c in Kids(_sel, GizmoChildMax)) _selChildren.Add(c);
        }
        snap.Status = _status;
        return snap;
    }

    static List<SceneNode> Kids(SceneNode n, int max)
    {
        var l = new List<SceneNode>();
        if (n.Children == null) return l;
        try { foreach (var c in n.Children()) { l.Add(c); if (l.Count >= max) break; } }
        catch (Exception ex) { _status = n.Name + ": " + ex.Message; }
        return l;
    }

    static int Count(SceneNode n)
    {
        try { return n.CountChildren?.Invoke() ?? 0; } catch (Exception) { return 0; }
    }

    static bool? Active(SceneNode n)
    {
        try { return n.GetActive?.Invoke(); } catch (Exception) { return null; }
    }

    static TreeItem BuildItem(SceneNode n, ref int visible)
    {
        _map[n.Id] = n;
        visible++;
        var t = new TreeItem { Id = n.Id, Name = n.Name, Type = n.Type, Active = Active(n), ChildCount = Count(n) };
        if (t.ChildCount > 0 && _expanded.Contains(n.Id))
        {
            var kids = Kids(n, MaxChildren + 1);
            t.Children = new List<TreeItem>(System.Math.Min(kids.Count, MaxChildren + 1));
            for (int i = 0; i < kids.Count; i++)
            {
                if (i >= MaxChildren)
                {
                    t.Children.Add(new TreeItem { Id = n.Id + "/__more", Name = "… " + (t.ChildCount - MaxChildren).ToString("N0", Inv) + " weitere", More = true });
                    break;
                }
                t.Children.Add(BuildItem(kids[i], ref visible));
            }
        }
        return t;
    }

    static void RunSearch(List<SceneNode> roots)
    {
        _searchMap.Clear();
        _searchAncestors.Clear();
        var result = new List<TreeItem>();
        int visited = 0;
        bool trunc = false;
        var names = new List<string>();
        var ids = new List<string>();

        void Walk(SceneNode n, int depth)
        {
            if (trunc) return;
            if (++visited > SearchBudget || result.Count >= SearchMax) { trunc = true; return; }
            if (n.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) || n.Type.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            {
                var path = new StringBuilder();
                foreach (var p in names) path.Append(p).Append(" / ");
                path.Append(n.Name);
                result.Add(new TreeItem { Id = n.Id, Name = path.ToString(), Type = n.Type, Active = Active(n) });
                _searchMap[n.Id] = n;
                _searchAncestors[n.Id] = ids.ToArray();
            }
            if (depth >= 32 || Count(n) == 0) return;
            names.Add(n.Name); ids.Add(n.Id);
            foreach (var c in Kids(n, int.MaxValue)) { Walk(c, depth + 1); if (trunc) break; }
            names.RemoveAt(names.Count - 1); ids.RemoveAt(ids.Count - 1);
        }

        foreach (var r in roots) { Walk(r, 0); if (trunc) break; }
        _search = result;
        _searchVisited = visited;
        _searchTrunc = trunc;
    }

    static InspectorData BuildInspector(SceneNode n)
    {
        var b = new InspectorBuilder();
        try { n.Inspect?.Invoke(b); }
        catch (Exception ex) { b.Section("Fehler"); b.Info("Exception", ex.GetType().Name + ": " + ex.Message); }
        var d = new InspectorData { Id = n.Id, Name = n.Name, Type = n.Type, Active = Active(n), CanRename = false, Sections = b.Sections };
        var sig = new StringBuilder(n.Id).Append('|').Append(n.SetActive != null ? 'A' : '-');
        foreach (var s in d.Sections)
        {
            sig.Append("§").Append(s.Title);
            foreach (var r in s.Rows) sig.Append('¦').Append((int)r.Kind).Append(r.Label).Append(r.Key == null ? "" : "=" + r.Key);
        }
        d.Signature = sig.ToString();
        return d;
    }

    /// <summary>Inspector als Text (Knopf "Kopieren").</summary>
    internal static string ToText(InspectorData d)
    {
        var sb = new StringBuilder();
        sb.Append(d.Name).Append("  [").Append(d.Type).Append(']').AppendLine();
        foreach (var s in d.Sections)
        {
            sb.AppendLine().Append("== ").Append(s.Title).AppendLine();
            foreach (var r in s.Rows)
                sb.Append(r.Label).Append(": ").Append(r.Kind == RowKind.Vec3 ? r.Value.Replace(";", "  ") : r.Kind == RowKind.Bool ? (r.Value == "1" ? "ja" : "nein") : r.Value).AppendLine();
        }
        return sb.ToString();
    }
}
