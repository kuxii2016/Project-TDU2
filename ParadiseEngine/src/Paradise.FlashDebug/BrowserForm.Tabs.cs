namespace Paradise.FlashDebug;

internal sealed partial class BrowserForm
{
    string _varSig = "", _fnSig = "", _logSig = "", _usageSig = "";
    string? _varFilterApplied, _fnFilterApplied, _logFilterApplied;
    bool _varForceValues, _lastExpandAll;
    readonly Dictionary<string, TreeNode> _treeNodes = new(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ Baum
    void UpdateTree(FlashSnapshot s)
    {
        _treeCount.Text = s.Nodes.Count + " Knoten"
            + (s.FilterIgnored ? "  (Filter ignoriert)" : "")
            + (s.TreeTruncated ? "  (Baum gekürzt – Grenze erreicht)" : "")
            + "  ·  Root-Objekte: " + s.RootObjects
            + (string.IsNullOrEmpty(s.FilmName) ? "" : "  ·  " + s.FilmName)
            + (s.Nodes.Count <= 1 ? "  –  Filter / 'nur sichtbare' prüfen" : "");

        // INKREMENTELL: vorhandene TreeNode-Objekte bleiben erhalten, damit der
        // Auf-/Zuklappzustand des Benutzers beim Refresh nicht verloren geht.
        var want = new Dictionary<string, FlashNodeInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in s.Nodes) want[n.Path] = n;

        _treeApplying = true;
        _tree.BeginUpdate();
        try
        {
            // Erst verschwundene Knoten entfernen – Kinder vor Eltern.
            var stale = _treeNodes.Keys.Where(k => !want.ContainsKey(k))
                                 .OrderByDescending(k => k.Count(c => c == '/')).ToArray();
            foreach (string path in stale)
            {
                if (_treeNodes.TryGetValue(path, out var old)) old.Remove();
                _treeNodes.Remove(path);
            }

            // Dann in Snapshot-Reihenfolge (Eltern zuerst) ergänzen/aktualisieren.
            foreach (var n in s.Nodes)
            {
                if (!_treeNodes.TryGetValue(n.Path, out var tn))
                {
                    tn = new TreeNode { Tag = n.Path };
                    if (n.Path == "/") _tree.Nodes.Add(tn);
                    else
                    {
                        int slash = n.Path.LastIndexOf('/');
                        string parent = slash <= 0 ? "/" : n.Path.Substring(0, slash);
                        if (_treeNodes.TryGetValue(parent, out var pt)) pt.Nodes.Add(tn);
                        else _tree.Nodes.Add(tn);
                    }
                    _treeNodes[n.Path] = tn;
                    if (n.Expanded || s.ExpandAll) tn.Expand();
                }
                string text = NodeText(n);
                if (tn.Text != text) tn.Text = text;
                Color c = n.Visible ? SystemColors.WindowText : Color.Gray;
                if (tn.ForeColor != c) tn.ForeColor = c;
            }
        }
        finally { _tree.EndUpdate(); _treeApplying = false; }

        if (s.ExpandAll != _lastExpandAll)
        {
            _lastExpandAll = s.ExpandAll;
            SetAllExpanded(s.ExpandAll);
        }

        string sel = s.SelectedPath;
        if (_tree.SelectedNode?.Tag is not string current || !string.Equals(current, sel, StringComparison.OrdinalIgnoreCase))
        {
            if (_treeNodes.TryGetValue(sel, out var node))
            {
                _treeApplying = true;
                _tree.SelectedNode = node;
                node.EnsureVisible();
                _treeApplying = false;
            }
        }
    }

    void SetAllExpanded(bool expand)
    {
        _treeApplying = true;
        try
        {
            foreach (var tn in _treeNodes.Values)
            {
                if (tn.Tag is string p && p == "/") continue;   // Wurzel immer offen
                if (expand) { if (tn.Nodes.Count > 0) tn.Expand(); }
                else tn.Collapse();
            }
        }
        finally { _treeApplying = false; }
    }

    static string NodeText(FlashNodeInfo n) =>
        (n.Path.Length > 1 ? n.Path + "  " : "") + n.Name + "  ·  " + n.Kind + n.Alpha;

    // ------------------------------------------------------------------ Variablen
    void UpdateVars(FlashSnapshot s)
    {
        if (!_varGrid.IsCurrentCellInEditMode)
        {
            string sig = string.Join("\n", s.Vars.Select(v => v.Key));
            if (sig != _varSig)
            {
                _varSig = sig;
                _varFilterApplied = null;
                _varGrid.Rows.Clear();
                var rows = new DataGridViewRow[s.Vars.Count];
                for (int i = 0; i < s.Vars.Count; i++)
                {
                    var r = new DataGridViewRow();
                    r.CreateCells(_varGrid, s.Vars[i].Key, s.Vars[i].Value, "?", "⧉");
                    rows[i] = r;
                }
                _varGrid.Rows.AddRange(rows);
            }
            else
            {
                bool valueChanged = false;
                for (int i = 0; i < s.Vars.Count && i < _varGrid.Rows.Count; i++)
                {
                    var row = _varGrid.Rows[i];
                    if (!row.Visible && !_varForceValues) continue;   // nur sichtbare Zeilen rendern
                    if (row.Cells[1].Value?.ToString() != s.Vars[i].Value)
                    {
                        row.Cells[1].Value = s.Vars[i].Value;
                        valueChanged = true;
                    }
                }
                _varForceValues = false;
                if (valueChanged) _varFilterApplied = null;
            }
        }
        string vc = s.Vars.Count + " von " + s.VarsTotal + " Variablen" + (s.Vars.Count >= 600 ? " – Filter benutzen" : "");
        if (_varCount.Text != vc) _varCount.Text = vc;
        FilterVars();
        UpdateUsage(s);
    }

    void FilterVars()
    {
        string f = _varFilter.Text.Trim();
        if (f == _varFilterApplied) return;   // unnötiges Layout vermeiden
        _varFilterApplied = f;
        _varForceValues = true;   // nach Filterwechsel alle Werte einmal nachziehen
        foreach (DataGridViewRow row in _varGrid.Rows)
            row.Visible = f.Length == 0 || Cell(_varGrid, row.Index, 0).IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                       || Cell(_varGrid, row.Index, 1).IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    void UpdateUsage(FlashSnapshot s)
    {
        string sig = s.UsageVar + "\n" + string.Join("\n", s.Usage);
        if (sig == _usageSig) return;
        _usageSig = sig;
        _usageTitle.Text = string.IsNullOrEmpty(s.UsageVar)
            ? "Variable ändern oder \"?\" klicken: zeigt, welche Funktionen sie lesen"
            : "Gelesen von (" + s.Usage.Count + "):  " + s.UsageVar;
        _usageFlow.Controls.Clear();
        if (s.Usage.Count == 0 && !string.IsNullOrEmpty(s.UsageVar))
            _usageFlow.Controls.Add(new Label { Text = "Keine Funktion liest diesen Namen – nur Textfelder (oder das Spiel selbst setzt die Anzeige).", AutoSize = true });
        foreach (var f in s.Usage.Take(12))
        {
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Width = 900 };
            row.Controls.Add(new Label { Text = f, AutoSize = true, Padding = new Padding(0, 5, 8, 0) });
            var call = new Button { Text = "Aufrufen", AutoSize = true, FlatStyle = FlatStyle.System };
            string fn = f;
            call.Click += (_, _) => FlashBrowser.Send("call_fn", fn, "");
            row.Controls.Add(call);
            _usageFlow.Controls.Add(row);
        }
    }

    // ------------------------------------------------------------------ Funktionen
    void UpdateFns(FlashSnapshot s)
    {
        if (!_fnGrid.IsCurrentCellInEditMode)
        {
            string sig = string.Join("\n", s.Fns.Select(f => f.Key));
            if (sig != _fnSig)
            {
                _fnSig = sig;
                _fnFilterApplied = null;
                _fnGrid.Rows.Clear();
                var rows = new DataGridViewRow[s.Fns.Count];
                for (int i = 0; i < s.Fns.Count; i++)
                {
                    var r = new DataGridViewRow();
                    r.CreateCells(_fnGrid, s.Fns[i].Key + "(" + s.Fns[i].Params + ")", "", "Aufrufen");
                    r.Tag = s.Fns[i].CallKey;
                    rows[i] = r;
                }
                _fnGrid.Rows.AddRange(rows);
            }
        }
        string fc = s.Fns.Count + " von " + s.FnsTotal + " Funktionen – Argumente mit Komma trennen (Zahlen, true/false, Text)";
        if (_fnCount.Text != fc) _fnCount.Text = fc;
        FilterFns();
    }

    void FilterFns()
    {
        string f = _fnFilter.Text.Trim();
        if (f == _fnFilterApplied) return;
        _fnFilterApplied = f;
        foreach (DataGridViewRow row in _fnGrid.Rows)
            row.Visible = f.Length == 0 || Cell(_fnGrid, row.Index, 0).IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // ------------------------------------------------------------------ FSCommands
    void UpdateLogs(FlashSnapshot s)
    {
        string sig = string.Join("\n", s.Logs.Select(l => l.ToString()));
        if (sig == _logSig) return;
        _logSig = sig;
        _logFilterApplied = null;
        FilterLogs();
    }

    void FilterLogs()
    {
        var s = _snap;
        if (s == null) return;
        string f = _logFilter.Text.Trim();
        if (f == _logFilterApplied) return;
        _logFilterApplied = f;
        _logList.BeginUpdate();
        _logList.Items.Clear();
        var items = new List<string>();
        foreach (var l in s.Logs)
            if (f.Length == 0 || l.ToString().IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                items.Add(l.ToString());
        _logList.Items.AddRange(items.ToArray());
        _logList.EndUpdate();
    }

    // ------------------------------------------------------------------ Eingabe
    void BuildInputTab(TabPage page)
    {
        // Früher lagen pad/buttons/custom ungedockt alle bei (0,0) und wurden vom
        // angedockten Info-Panel überdeckt -> "oben abgeschnitten". Jetzt ein
        // einziger scrollbarer TopDown-Container für alle Blöcke.
        var host = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(8),
        };
        page.Controls.Add(host);

        var top = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 10),
        };
        _focus = new Label { AutoSize = true, Width = 1000 };
        _activeList = new Label { AutoSize = true, Width = 1000 };
        top.Controls.Add(_focus);
        top.Controls.Add(_activeList);
        host.Controls.Add(top);

        var pad = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 2,
            Padding = new Padding(0, 0, 0, 10),
        };
        pad.Controls.Add(InputButton("▲", "Move", "up"), 1, 0);
        pad.Controls.Add(InputButton("◀", "Move", "left"), 0, 1);
        pad.Controls.Add(InputButton("▼", "Move", "down"), 1, 1);
        pad.Controls.Add(InputButton("▶", "Move", "right"), 2, 1);
        host.Controls.Add(pad);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 10),
        };
        foreach (var b in new[] { "A", "B", "X", "Y", "LB", "RB" })
        {
            var btn = new Button { Text = b, Width = 56, FlatStyle = FlatStyle.System };
            string f = b;
            btn.Click += (_, _) => FlashBrowser.Send("input", f, "");
            buttons.Controls.Add(btn);
        }
        host.Controls.Add(buttons);

        var custom = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        custom.Controls.Add(new Label { Text = "Eigene Eingabe", AutoSize = true, Padding = new Padding(0, 5, 8, 0) });
        var fn = new TextBox { Width = 200 };
        var args = new TextBox { Width = 240 };
        custom.Controls.Add(fn);
        custom.Controls.Add(args);
        var send = new Button { Text = "Senden", AutoSize = true, FlatStyle = FlatStyle.System };
        send.Click += (_, _) => { if (fn.Text.Length > 0) FlashBrowser.Send("input", fn.Text, args.Text); };
        custom.Controls.Add(send);
        host.Controls.Add(custom);
    }

    static Button InputButton(string text, string fn, string arg)
    {
        var b = new Button { Text = text, Width = 48, Height = 40, FlatStyle = FlatStyle.System };
        b.Click += (_, _) => FlashBrowser.Send("input", fn, arg);
        return b;
    }
}
