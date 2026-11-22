// Baut aus den laufenden VmfPlayern einen kopierten Snapshot für den UI-Thread.
// Läuft ausschließlich auf dem Spiel-Thread (Pump()).

using Paradise.Core.Flash;
using System.Globalization;

namespace Paradise.FlashDebug;

internal static class SnapshotBuilder
{
    const int MaxNodes = 6000, MaxDepth = 64, MaxVars = 600, MaxFns = 600, MaxLogs = 400;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static bool _truncated;

    internal static FlashSnapshot Build(BrowserState st, IReadOnlyList<VmfPlayer> players, IEnumerable<FlashLogInfo> logs)
    {
        var s = new FlashSnapshot
        {
            SelectedPath = st.SelPath,
            Status = st.Status,
            ShowPaths = st.ShowPaths,
            UsageVar = st.UsageVar,
            Usage = new List<string>(st.Usage),
            AutoCall = st.AutoCall,
            ExpandAll = st.ExpandAll,
        };
        int count = 0;
        foreach (var p in players)
        {
            if (p?.Movie == null) continue;
            string file = Path.GetFileName(p.BankPath ?? "");
            s.Players.Add(new FlashPlayerInfo
            {
                Index = count,
                Label = file + (p.Visible ? "" : " (hidden)"),
                Bank = p.BankPath ?? "",
                Film = p.FilmName,
                Visible = p.Visible,
                MouseEnabled = p.MouseEnabled,
                GuiDepth = p.GuiDepth,
            });
            count++;
        }
        // Auswahl zurechtstutzen, wenn ein Film verschwunden ist
        if (st.SelPlayer >= count) st.SelPlayer = count - 1;
        if (st.SelPlayer < 0 && count > 0) st.SelPlayer = 0;
        s.SelectedPlayer = st.SelPlayer;

        var p2 = PlayerAt(players, st.SelPlayer);
        if (p2 != null)
        {
            CurrentPlayer = p2;
            // DebugVariables/DebugFunctions sind nicht billig – nur einmal pro Snapshot holen.
            var vars = p2.DebugVariables();
            var fns = p2.DebugFunctions();
            // Immer einen Wurzelknoten anlegen – auch wenn alle Kinder weggefiltert werden.
            s.Nodes.Add(new FlashNodeInfo
            {
                Path = "/",
                Name = "/",
                Kind = "root",
                Depth = 0,
                Visible = true,
                IsClip = true,
                HasChildren = p2.Root?.List.Count > 0,
                Expanded = true,
            });
            int nodesBefore = s.Nodes.Count;
            _truncated = false;
            Walk(p2.Root, "/", 0, st, s.Nodes);
            s.TreeTruncated = _truncated;
            s.RootObjects = p2.Root?.List.Count ?? 0;
            s.FilmName = p2.FilmName ?? "";
            if (s.Nodes.Count == nodesBefore && s.RootObjects > 0)
            {
                // Der Filter hat alles weggeschnitten: einmal ungefiltert zeigen, damit der Baum nicht leer wirkt.
                var all = new BrowserState
                {
                    OnlyVisible = false,
                    OnlyClips = false,
                    Filter = "",
                    Expanded = st.Expanded,
                    SelPath = st.SelPath,
                };
                Walk(p2.Root, "/", 0, all, s.Nodes);
                s.FilterIgnored = true;
            }
            foreach (var v in vars.Take(MaxVars))
                s.Vars.Add(new FlashVarInfo { Key = v.Key, Value = v.Value });
            foreach (var f in fns.Take(MaxFns))
                s.Fns.Add(new FlashFnInfo { Key = f.Key, Params = f.Value, CallKey = f.Key });
            s.VarsTotal = vars.Count;
            s.FnsTotal = fns.Count;
            s.Detail = BuildDetail(p2, st.SelPath, vars, fns);
            s.Focus = p2.GetVariable("/:Clip") ?? "";
            var l = p2.ActiveList;
            s.ActiveList = l == null ? "" : l.Name + "  rows " + l.Rows.Count + ", selected " + (l.Selected + 1) + ", top " + (l.Top + 1) + ", active " + l.Active;
        }
        else if (s.Nodes.Count == 0)
        {
            s.Nodes.Add(new FlashNodeInfo { Path = "/", Name = "/", Kind = "no movie active", IsClip = true, Expanded = true });
        }

        foreach (var e in logs.Reverse().Take(MaxLogs)) s.Logs.Add(e);

        string bnk = FlashHost.BnkDir;
        s.BnkDir = bnk ?? "";
        s.BankStartDir = string.IsNullOrEmpty(bnk) ? "" : Path.Combine(bnk, "frontend", "hires");
        if (!string.IsNullOrEmpty(bnk))
        {
            foreach (var res in new[] { "hires", "medres", "lowres" })
            {
                string dir = Path.Combine(bnk, "frontend", res);
                if (!Paradise.Core.Files.GameFiles.DirectoryExists(dir)) continue;
                var files = Paradise.Core.Files.GameFiles.GetFiles(dir, "*.bnk");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                s.Banks = files;
                break;
            }
        }
        s.PatchFile = VmfPlayer.PatchFileEditor;
        return s;
    }

    internal static VmfPlayer? PlayerAt(IReadOnlyList<VmfPlayer> players, int index)
    {
        int n = 0;
        foreach (var p in players)
        {
            if (p?.Movie == null) continue;
            if (n++ == index) return p;
        }
        return null;
    }

    // ------------------------------------------------------------------ Baum
    static void Walk(VmfPlayer.Instance inst, string path, int depth, BrowserState st, List<FlashNodeInfo> dest)
    {
        if (depth > MaxDepth || dest.Count >= MaxNodes) { _truncated = true; return; }
        foreach (var d in inst.List.Values)
        {
            if (st.OnlyVisible && (!d.Visible || (d.Cx.Mul.w <= 0.003f && d.Cx.Add.w <= 0.003f))) continue;
            if (st.OnlyClips && d.Sprite == null) continue;
            string name = d.Name ?? ("#" + d.Depth);
            string p = (path == "/" ? "/" : path + "/") + name;
            bool filtered = st.Filter.Length > 0;
            bool self = name.IndexOf(st.Filter, StringComparison.OrdinalIgnoreCase) >= 0;
            bool kids = filtered && d.Sprite != null && SubtreeMatches(d.Sprite, depth + 1, st);
            if (filtered && !self && !kids) continue;

            string kind;
            if (d.Sprite != null)
            {
                var lab = CurrentLabel(d.Sprite);
                kind = "clip f=" + (d.Sprite.Frame + 1) + "/" + d.Sprite.Timeline.FrameCount
                     + (lab != null ? " [" + lab + "]" : "") + (d.Sprite.Playing ? " ▶" : "");
            }
            else
            {
                kind = "?";
                if (inst.Owner != null)
                {
                    var p2 = PlayerOf(inst);
                    if (p2?.Movie != null && p2.Movie.Chars.TryGetValue(d.CharId, out var ch))
                    {
                        kind = ch.GetType().Name;
                        if (ch is VmfMovie.EditText et && p2 != null)
                        {
                            string t = p2.DebugText(d);
                            if (!string.IsNullOrEmpty(t)) kind += " \"" + Short(t, 30) + "\"";
                        }
                    }
                }
            }
            bool hasKids = d.Sprite != null && d.Sprite.List.Count > 0;
            bool expanded = st.ExpandAll || st.Expanded.Contains(p) || (filtered && kids);
            dest.Add(new FlashNodeInfo
            {
                Path = p,
                Name = name,
                Kind = kind,
                Depth = depth,
                Visible = d.Visible,
                IsClip = d.Sprite != null,
                HasChildren = hasKids,
                Expanded = expanded,
                Alpha = d.Cx.Mul.w < 0.999f ? " α" + d.Cx.Mul.w.ToString("0.00", Inv) : "",
            });
            // WICHTIG: Kinder IMMER in den Snapshot aufnehmen – auch wenn der Knoten
            // gerade zugeklappt ist. Nur dann zeigt der TreeView ein "+" und der
            // Benutzer kann ihn überhaupt erst aufklappen (BeforeExpand -> st.Expanded).
            if (hasKids) Walk(d.Sprite!, p, depth + 1, st, dest);
        }
    }

    static VmfPlayer? PlayerOf(VmfPlayer.Instance inst)
    {
        // Instance kennt seinen Player nicht; der Snapshot kennt aber genau einen aktiven Film.
        return CurrentPlayer;
    }

    static VmfPlayer? CurrentPlayer;

    static bool SubtreeMatches(VmfPlayer.Instance inst, int depth, BrowserState st)
    {
        if (depth > MaxDepth) return false;
        foreach (var d in inst.List.Values)
        {
            if (st.OnlyVisible && !d.Visible) continue;
            if ((d.Name ?? ("#" + d.Depth)).IndexOf(st.Filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (d.Sprite != null && SubtreeMatches(d.Sprite, depth + 1, st)) return true;
        }
        return false;
    }

    static string Short(string s, int n)
    {
        s = s.Replace("\n", "⏎");
        return s.Length > n ? s.Substring(0, n) + "…" : s;
    }

    static string? CurrentLabel(VmfPlayer.Instance inst)
    {
        string? lab = null; int best = -1;
        foreach (var kv in inst.Timeline.Labels) if (kv.Value <= inst.Frame && kv.Value > best) { best = kv.Value; lab = kv.Key; }
        return lab;
    }

    // ------------------------------------------------------------------ Details
    static FlashDetailInfo BuildDetail(VmfPlayer p, string path,
        List<KeyValuePair<string, string>> vars, List<KeyValuePair<string, string>> fns)
    {
        CurrentPlayer = p;
        var d = path == "/" || string.IsNullOrEmpty(path) ? null : p.Find(path);
        var inst = d != null ? d.Sprite : (path == "/" ? p.Root : null);
        var det = new FlashDetailInfo { Path = path, Film = p.FilmName };
        if (d != null)
        {
            det.Depth = d.Depth; det.CharId = d.CharId; det.ClipDepth = d.ClipDepth;
            det.Visible = d.Visible; det.IsClip = d.Sprite != null;
            det.X = d.Matrix.Tx; det.Y = d.Matrix.Ty;
            det.Alpha = d.Cx.Mul.w;
            det.OffsetX = d.Offset.x; det.OffsetY = d.Offset.y;
            det.ScaleX = d.ScaleMul.x; det.ScaleY = d.ScaleMul.y;
            det.ScaleInfo = MathF.Sqrt(d.Matrix.A * d.Matrix.A + d.Matrix.B * d.Matrix.B).ToString("0.###", Inv) + " x "
                          + MathF.Sqrt(d.Matrix.C * d.Matrix.C + d.Matrix.D * d.Matrix.D).ToString("0.###", Inv)
                          + (d.ScriptMatrix ? "   (per script)" : "");
            var sb = p.ScreenBounds(d);
            det.Screen = sb.HasValue ? sb.Value.x.ToString("0", Inv) + "," + sb.Value.y.ToString("0", Inv) + "  "
                                     + sb.Value.width.ToString("0", Inv) + "x" + sb.Value.height.ToString("0", Inv) + " px" : "-";
            if (p.Movie.Chars.TryGetValue(d.CharId, out var ch) && ch is VmfMovie.EditText et)
            {
                det.Type = "EditText";
                det.TextVariable = et.Variable ?? "";
                det.Text = p.DebugText(d) ?? "";
                det.TextKey = string.IsNullOrEmpty(et.Variable) ? "" : VmfPlayer.ResolveVar(d.Parent, et.Variable);
            }
            else if (d.Sprite != null) det.Type = "Clip";
            else det.Type = p.Movie.Chars.TryGetValue(d.CharId, out var c2) ? c2.GetType().Name : "?";
        }
        else det.Type = path == "/" ? "Root" : "-";

        if (inst != null)
        {
            det.Frame = inst.Frame + 1; det.FrameCount = inst.Timeline.FrameCount;
            det.Playing = inst.Playing;
            det.CurrentLabel = CurrentLabel(inst) ?? "";
            var labs = new List<FlashLabelInfo>();
            foreach (var kv in inst.Timeline.Labels)
                labs.Add(new FlashLabelInfo { Name = kv.Key, Frame = kv.Value + 1 });
            labs.Sort((a, b) => a.Frame.CompareTo(b.Frame));
            det.Labels = labs.ToArray();
        }

        if (!string.IsNullOrEmpty(path))
        {
            string prefix = path + ":";
            foreach (var v in vars)
                if (v.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    det.ClipVars.Add(new FlashVarInfo { Key = v.Key.Substring(prefix.Length), Value = v.Value });
            foreach (var f in fns)
                if (f.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    det.ClipFns.Add(new FlashFnInfo
                    {
                        Key = f.Key.Substring(prefix.Length),
                        Params = f.Value,
                        CallKey = f.Key
                    });
        }

        det.CodeLines = BuildCode(p, d, path, det);
        foreach (var q in VmfPlayer.AllPatches)
            if (string.Equals(q.Path, path, StringComparison.OrdinalIgnoreCase)
                && (q.Film == "*" || string.Equals(q.Film, p.FilmName, StringComparison.OrdinalIgnoreCase)))
                det.Patches.Add(new FlashPatchInfo { Film = q.Film, Path = q.Path, Command = q.Command, Value = q.Value });
        return det;
    }

    static string[] BuildCode(VmfPlayer p, VmfPlayer.DisplayObject? d, string path, FlashDetailInfo det)
    {
        var lines = new List<string>();
        if (d == null) return lines.ToArray();
        string q = Q(path);
        lines.Add("ui.SetOffset(" + q + ", new Vector2(" + Fl(det.OffsetX) + ", " + Fl(det.OffsetY) + "));");
        lines.Add("ui.SetPosition(" + q + ", new Vector2(" + Fl(det.X + det.OffsetX) + ", " + Fl(det.Y + det.OffsetY) + "));");
        lines.Add("ui.SetScale(" + q + ", new Vector2(" + Fl(det.ScaleX) + ", " + Fl(det.ScaleY) + "));");
        lines.Add("ui.SetAlpha(" + q + ", " + Fl(det.Alpha) + ");");
        lines.Add(det.Visible ? "ui.Show(" + q + ");" : "ui.Hide(" + q + ");");
        lines.Add("ui.ForceVisible(" + q + ", " + (det.Visible ? "true" : "false") + ");");
        if (det.Type == "EditText")
        {
            if (!string.IsNullOrEmpty(det.TextKey)) lines.Add("ui.SetText(" + Q(det.TextKey) + ", " + Q(det.Text) + ");");
            lines.Add("ui.SetObjectText(" + q + ", " + Q(det.Text) + ");");
        }
        if (d.Sprite != null)
        {
            if (!string.IsNullOrEmpty(det.CurrentLabel))
            {
                lines.Add("ui.SetLabel(" + q + ", " + Q(det.CurrentLabel) + ");");
                lines.Add("ui.PlayLabel(" + q + ", " + Q(det.CurrentLabel) + ");");
            }
            lines.Add("ui.SetFrame(" + q + ", " + det.Frame + ");");
        }
        lines.Add(det.Film + " | " + path + " | offset | " + Num(det.OffsetX) + " " + Num(det.OffsetY));
        return lines.ToArray();
    }

    static string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
    static string Fl(float v) => v.ToString("0.###", Inv) + "f";
    static string Num(float v) => v.ToString("0.###", Inv);
}
