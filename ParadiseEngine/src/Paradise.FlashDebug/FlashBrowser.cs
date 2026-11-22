// Flash-Browser für die eigene Engine: Port des Unity "TDU2/Flash Debugger".
// Die WinForms-Oberfläche läuft auf einem eigenen STA-Thread im Spielprozess.
// Alle VmfPlayer-Zugriffe passieren in Pump() auf dem Spiel-Thread; die UI schickt
// nur Text-Kommandos und bekommt kopierte Snapshots (kein Cross-Thread-Zugriff).

using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using System.Collections.Concurrent;
using System.Globalization;

namespace Paradise.FlashDebug;

public static class FlashBrowser
{
    static readonly ConcurrentQueue<(string Cmd, string[] Args)> _queue = new();
    static readonly ConcurrentQueue<FlashLogInfo> _logs = new();
    static readonly HashSet<VmfPlayer> _hooked = new();
    static readonly List<VmfPlayer> _viewers = new();
    static readonly BrowserState _st = new();
    static readonly object _lock = new();
    static BrowserForm? _form;
    static Thread? _thread;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Es wird immer nur der LETZTE Snapshot gebraucht. Alte BeginInvoke-Aufrufe
    // würden sich sonst bei einer beschäftigten UI stapeln und danach nacheinander
    // abgearbeitet werden (Ruckler, Controls springen zurück).
    static FlashSnapshot? _pending;
    static int _postPending;
    const float SnapshotInterval = 0.25f;   // 4 Hz reichen für "realtime"; Edits gehen sofort raus

    public static bool IsOpen { get { lock (_lock) return _form != null && _thread != null && _thread.IsAlive; } }

    /// <summary>F1 > Flash Debug > Browse Flash. Öffnet (oder fokussiert) das Fenster.</summary>
    public static void Open()
    {
        TaskCompletionSource<bool> ready;
        Thread t;
        lock (_lock)
        {
            if (IsOpen)
            {
                var f = _form;
                if (f != null)
                    try { f.BeginInvoke(new Action(f.FocusMe)); } catch (Exception) { }
                return;
            }
            ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            t = new Thread(() =>
            {
                try
                {
                    var form = new BrowserForm();
                    lock (_lock) _form = form;
                    ready.TrySetResult(true);
                    Application.Run(form);
                }
                catch (Exception ex)
                {
                    Log.Error("Flash Browser: " + ex);
                    ready.TrySetResult(false);
                }
                finally { lock (_lock) _form = null; }
            })
            { IsBackground = true, Name = "Paradise Flash Browser" };
            t.SetApartmentState(ApartmentState.STA);
            _thread = t;
            Volatile.Write(ref _pending, null);
            Interlocked.Exchange(ref _postPending, 0);
        }
        // WICHTIG: nicht im lock warten – der UI-Thread setzt _form ebenfalls unter _lock.
        t.Start();
        try { ready.Task.Wait(TimeSpan.FromSeconds(5)); } catch (Exception) { }
    }

    public static void Close()
    {
        var f = _form;
        if (f != null && !f.IsDisposed)
            try { f.BeginInvoke(new Action(f.Close)); } catch (Exception) { }
    }

    /// <summary>Vom UI-Thread: Kommando in die Spiel-Thread-Warteschlange stellen.</summary>
    internal static void Send(string cmd, params string[] args) => _queue.Enqueue((cmd, args ?? Array.Empty<string>()));

    /// <summary>Jedes Bild aus dem Spiel aufrufen: Kommandos ausführen, Snapshot posten.</summary>
    public static void Pump()
    {
        if (!IsOpen) return;
        var players = FlashHost.Players;
        SyncHooks(players);

        int n = 0;
        while (n++ < 128 && _queue.TryDequeue(out var c))
        {
            try { Execute(c.Cmd, c.Args); }
            catch (Exception ex) { _st.Status = c.Cmd + ": " + ex.Message; Log.Warn("Flash Browser " + c.Cmd + ": " + ex.Message); }
        }
        if (!_st.Live) return;
        if (Time.unscaledTime < _st.NextSnapshot) return;
        _st.NextSnapshot = Time.unscaledTime + SnapshotInterval;

        var snap = SnapshotBuilder.Build(_st, players, _logs);
        var f = _form;
        if (f == null || f.IsDisposed) return;
        // Während der Nutzer ein Dropdown offen hat, eine Zelle editiert oder ein
        // Dialog offen ist, darf kein Snapshot dazwischenfunken. Der nächste Pump
        // schickt dann automatisch den aktuellsten Stand.
        if (f.IsBusy) return;
        Volatile.Write(ref _pending, snap);
        if (Interlocked.Exchange(ref _postPending, 1) != 0) return;   // schon ein Apply unterwegs
        try { f.BeginInvoke(new Action(() => PostApply(f))); }
        catch (Exception) { Interlocked.Exchange(ref _postPending, 0); }
    }

    static void PostApply(BrowserForm f)
    {
        Interlocked.Exchange(ref _postPending, 0);
        if (f.IsDisposed || f.IsBusy) return;   // verwerfen – nächster Pump liefert Neueres
        var s = Volatile.Read(ref _pending);
        Volatile.Write(ref _pending, null);
        if (s != null) f.Apply(s);
    }

    static void SyncHooks(IReadOnlyList<VmfPlayer> players)
    {
        foreach (var p in _hooked.ToArray())
            if (!players.Contains(p)) { p.FSCommand -= OnFsCommand; _hooked.Remove(p); }
        foreach (var p in players)
            if (p != null && !_hooked.Contains(p)) { p.FSCommand += OnFsCommand; _hooked.Add(p); }
    }

    static void OnFsCommand(string cmd, string args)
    {
        _logs.Enqueue(new FlashLogInfo
        {
            Time = Time.unscaledTime.ToString("8:0.00", Inv),
            Command = cmd ?? "",
            Args = args ?? "",
        });
        while (_logs.Count > 2000) _logs.TryDequeue(out _);
    }

    static VmfPlayer? Sel => SnapshotBuilder.PlayerAt(FlashHost.Players, _st.SelPlayer);
    static VmfPlayer.Instance? Inst(VmfPlayer p, string path) =>
        path == "/" || string.IsNullOrEmpty(path) ? p.Root : (p.Find(path)?.Sprite);
    static int I(string[] a, int i, int def = 0) => a.Length > i && int.TryParse(a[i], NumberStyles.Integer, Inv, out int v) ? v : def;
    static float F(string[] a, int i, float def = 0f) => a.Length > i && float.TryParse(a[i], NumberStyles.Float, Inv, out float v) ? v : def;
    static string S(string[] a, int i) => a.Length > i ? a[i] : "";
    static bool B(string[] a, int i) => S(a, i) == "1" || S(a, i).Equals("true", StringComparison.OrdinalIgnoreCase);

    static void Execute(string cmd, string[] a)
    {
        switch (cmd)
        {
            case "select_player":
                {
                    var old = Sel; if (old != null) old.Highlight = null;
                    _st.SelPlayer = I(a, 0); _st.SelPath = "/"; _st.Status = "";
                    Highlight(); break;
                }
            case "select_path":
                _st.SelPath = S(a, 0); if (_st.SelPath.Length == 0) _st.SelPath = "/";
                Highlight(); break;
            case "expand":
                if (B(a, 1)) _st.Expanded.Add(S(a, 0)); else _st.Expanded.Remove(S(a, 0));
                break;
            case "expand_all":
                _st.ExpandAll = B(a, 0);
                if (!_st.ExpandAll) _st.Expanded.Clear();
                break;
            case "filter": _st.Filter = S(a, 0); break;
            case "only_visible": _st.OnlyVisible = B(a, 0); break;
            case "only_clips": _st.OnlyClips = B(a, 0); break;
            case "show_paths": _st.ShowPaths = B(a, 0); break;
            case "live": _st.Live = B(a, 0); break;
            case "clear_status": _st.Status = ""; break;

            case "set_visible":
                {
                    var p = Sel; p?.SetVisible(S(a, 0), B(a, 1)); break;
                }
            case "set_pos":
                {
                    var d = Sel?.Find(S(a, 0));
                    if (d != null) { d.Matrix.Tx = F(a, 1); d.Matrix.Ty = F(a, 2); d.Position = null; d.ScriptMatrix = true; }
                    break;
                }
            case "set_alpha": Sel?.SetAlpha(S(a, 0), F(a, 1)); break;
            case "set_offset": Sel?.SetOffset(S(a, 0), new Vector2(F(a, 1), F(a, 2))); break;
            case "set_scale": Sel?.SetScale(S(a, 0), new Vector2(F(a, 1), F(a, 2))); break;
            case "set_text":
                {
                    string t = S(a, 1);
                    Sel?.SetObjectText(S(a, 0), t.Length == 0 ? null : t); break;
                }
            case "set_var":
                {
                    var p = Sel; string key = S(a, 0);
                    p?.DebugSetVariable(key, S(a, 1));
                    _st.Status = key + " = " + S(a, 1);
                    _st.UsageVar = key; _st.Usage = p?.DebugFunctionsUsing(key) ?? new List<string>();
                    if (_st.AutoCall && p != null) foreach (var f in _st.Usage) p.DebugCall(f, "");
                    break;
                }
            case "usage":
                {
                    var p = Sel; _st.UsageVar = S(a, 0);
                    _st.Usage = p?.DebugFunctionsUsing(_st.UsageVar) ?? new List<string>();
                    break;
                }
            case "set_autocall": _st.AutoCall = B(a, 0); break;
            case "call_fn":
                {
                    var p = Sel; bool ok = p != null && p.DebugCall(S(a, 0), S(a, 1));
                    _st.Status = S(a, 0) + "(" + S(a, 1) + ")" + (ok ? " called" : " not found"); break;
                }
            case "goto_label":
                {
                    bool ok = Sel?.GotoLabel(S(a, 0), S(a, 1), B(a, 2)) == true;
                    _st.Status = S(a, 0) + " -> " + S(a, 1) + (ok ? "" : " (label not found)");
                    break;
                }
            case "goto_frame":
                {
                    var p = Sel; var inst = p == null ? null : Inst(p, S(a, 0));
                    if (p != null && inst != null)
                    {
                        int f = Math.Clamp(I(a, 1, 1) - 1, 0, Math.Max(0, inst.Timeline.FrameCount - 1));
                        p.GotoFrame(inst, f, true); inst.Playing = B(a, 2, true);
                    }
                    break;
                }
            case "set_play":
                {
                    var p = Sel; var inst = p == null ? null : Inst(p, S(a, 0));
                    if (inst != null) inst.Playing = B(a, 1, true);
                    break;
                }
            case "input":
                {
                    var p = Sel; if (p == null) break;
                    string args = S(a, 1);
                    object[] values = Array.Empty<object>();
                    if (args.Length > 0)
                        values = args.Split(',').Select(v =>
                        {
                            string t = v.Trim();
                            if (double.TryParse(t, NumberStyles.Float, Inv, out double d)) return (object)d;
                            if (t == "true" || t == "false") return t == "true";
                            return t.Trim('"', '\'');
                        }).ToArray();
                    bool ok = p.Input(S(a, 0), values);
                    _st.Status = S(a, 0) + (ok ? " processed" : ": no function"); break;
                }
            case "highlight": Highlight(); break;

            case "load_bank":
                {
                    string path = S(a, 0);
                    var np = FlashHost.Load(path, -20);
                    if (np == null) { _st.Status = "Could not load " + Path.GetFileName(path); break; }
                    np.MouseEnabled = true;
                    _viewers.Add(np);
                    int idx = -1, i = 0;
                    foreach (var q in FlashHost.Players)
                    {
                        if (q?.Movie == null) continue;
                        if (ReferenceEquals(q, np)) { idx = i; break; }
                        i++;
                    }
                    _st.SelPlayer = idx; _st.SelPath = "/"; _st.Status = "Loaded " + Path.GetFileName(path);
                    break;
                }
            case "close_viewers":
                {
                    foreach (var v in _viewers) FlashHost.Remove(v);
                    _viewers.Clear(); _st.SelPlayer = -1; _st.SelPath = "/"; _st.Status = "Viewers closed"; break;
                }
            case "reset_movie":
                {
                    var p = Sel;
                    if (p?.BankPath == null) break;
                    string path = p.BankPath; bool vis = p.Visible;
                    p.Highlight = null; p.Load(path); p.Visible = vis;
                    if (!string.IsNullOrEmpty(FlashHost.BnkDir))
                        FlashLocalization.Apply(p, FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language));
                    _st.SelPath = "/"; _st.Status = "Reloaded " + Path.GetFileName(path); break;
                }
            case "set_player_mouse": { var p = Sel; if (p != null) p.MouseEnabled = B(a, 0); break; }
            case "set_player_visible": { var p = Sel; if (p != null) p.Visible = B(a, 0); break; }
            case "set_player_depth": { var p = Sel; if (p != null) p.GuiDepth = I(a, 0); break; }

            case "save_patch": SavePatch(S(a, 0), S(a, 1), S(a, 2), S(a, 3)); break;
            case "remove_patch":
                {
                    string pa = S(a, 1);
                    RemovePatch(S(a, 0), pa, S(a, 2));
                    // Live-Zustand zurücksetzen (wie im Unity-Debugger)
                    if (S(a, 2) == "alpha") Sel?.SetAlpha(pa, 1f);
                    if (S(a, 2) == "visible") Sel?.SetVisible(pa, true);
                    if (S(a, 2) == "offset") Sel?.SetOffset(pa, new Vector2(0f, 0f));
                    if (S(a, 2) == "scale") Sel?.SetScale(pa, new Vector2(1f, 1f));
                    break;
                }
            case "reload_patches": VmfPlayer.ReloadPatches(); _st.Status = "Patches reloaded"; break;
            case "clear_log": while (_logs.TryDequeue(out _)) { } break;

            default: _st.Status = "Unknown command: " + cmd; break;
        }
    }

    static void Highlight()
    {
        var p = Sel; if (p == null) return;
        p.Highlight = _st.SelPath == "/" ? null : p.Find(_st.SelPath);
    }

    static bool B(string[] a, int i, bool def) => a.Length > i ? B(a, i) : def;

    // ------------------------------------------------------------------ Patches (wie im Unity-Debugger)
    static void EnsurePatchFile()
    {
        string f = VmfPlayer.PatchFileEditor;
        if (File.Exists(f)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f,
            "# TDU2 Flash-Korrekturen: film | pfad | befehl | werte\r\n" +
            "# befehle: offset dx dy | pos x y | scale sx sy | alpha a | visible 0/1 | text ...   (film * = alle Filme)\r\n");
    }

    static void SavePatch(string film, string path, string command, string value)
    {
        EnsurePatchFile();
        string f = VmfPlayer.PatchFileEditor;
        var lines = File.ReadAllLines(f).ToList();
        string line = film + " | " + path + " | " + command + " | " + value;
        bool replaced = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var ps = VmfPlayer.ParsePatches(lines[i]);
            if (ps.Count == 1 && string.Equals(ps[0].Film, film, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ps[0].Path, path, StringComparison.OrdinalIgnoreCase) && ps[0].Command == command)
            { lines[i] = line; replaced = true; }
        }
        if (!replaced) lines.Add(line);
        File.WriteAllText(f, string.Join("\r\n", lines) + "\r\n");
        VmfPlayer.ReloadPatches();
        _st.Status = "Saved: " + line;
    }

    static void RemovePatch(string film, string path, string command)
    {
        string f = VmfPlayer.PatchFileEditor;
        if (!File.Exists(f)) return;
        var lines = File.ReadAllLines(f).Where(l =>
        {
            var ps = VmfPlayer.ParsePatches(l);
            return !(ps.Count == 1 && ps[0].Film == film && ps[0].Path == path && ps[0].Command == command);
        }).ToList();
        File.WriteAllText(f, string.Join("\r\n", lines) + "\r\n");
        VmfPlayer.ReloadPatches();
        _st.Status = "Removed: " + film + " | " + path + " | " + command;
    }
}
