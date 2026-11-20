// Szenen-Editor: Hierarchy-Fenster (Unity: Hierarchy). Läuft auf dem STA-UI-Thread.
// Der Baum wird aus den Snapshots inkrementell abgeglichen (Schlüssel = Objekt-Id), damit Auswahl, Scroll-Position und
// aufgeklappte Einträge erhalten bleiben. Aufklappen/Zuklappen geht als Kommando an den Spiel-Thread, der nur für
// aufgeklappte Einträge Kinder liefert.

namespace Paradise.Editor;

internal sealed class HierarchyForm : Form
{
    const string Dummy = "__dummy";
    static readonly Color Inactive = Color.FromArgb(150, 150, 150);
    static readonly Color MoreColor = Color.FromArgb(120, 120, 160);

    readonly EditorContext _ctx;
    readonly TreeView _tree;
    readonly ToolStripTextBox _filter;
    readonly ToolStripLabel _count;
    readonly ToolStripStatusLabel _status;
    CheckBox _live = null!, _gizmo = null!, _gizmoKids = null!;
    bool _applying, _inUpdate, _searchMode;
    SceneSnapshot? _snap;

    // Auf-/Zuklappen, das der Spiel-Thread noch nicht bestätigt hat (sonst springt der Eintrag bis zum nächsten Snapshot zurück)
    readonly Dictionary<string, DateTime> _pendingOpen = new(), _pendingClose = new();

    sealed class BufferedTree : TreeView { public BufferedTree() { DoubleBuffered = true; } }

    public HierarchyForm(EditorContext ctx)
    {
        _ctx = ctx;
        Text = "Hierarchy – Paradise Engine";
        Name = "SceneHierarchy";
        Size = new Size(440, 800);
        MinimumSize = new Size(300, 300);
        Font = new Font("Segoe UI", 9f);

        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
        _filter = new ToolStripTextBox { Width = 170, ToolTipText = "Suche (Name oder Typ) in der ganzen Szene" };
        _filter.TextBox.PlaceholderText = "Suchen …";
        _filter.TextChanged += (_, _) => { if (!_applying) SceneEditor.Send("filter", _filter.Text); };
        bar.Items.Add(_filter);
        bar.Items.Add(new ToolStripButton("×", null, (_, _) => _filter.Text = "") { ToolTipText = "Suche leeren" });
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(new ToolStripButton("Alle zu", null, (_, _) =>
        {
            SceneEditor.Send("collapse_all");
            _applying = true;
            try { _tree.CollapseAll(); } finally { _applying = false; }
        }) { ToolTipText = "Alle Einträge zuklappen" });
        bar.Items.Add(new ToolStripButton("Inspector", null, (_, _) => _ctx.ShowInspector()) { ToolTipText = "Inspector-Fenster zeigen" });
        _count = new ToolStripLabel { Alignment = ToolStripItemAlignment.Right, ForeColor = SystemColors.GrayText };
        bar.Items.Add(_count);

        var opts = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 2, 4, 2), WrapContents = true };
        _live = Check("Live", true, "Hierarchie/Inspector laufend aktualisieren (4×/s)", v => SceneEditor.Send("live", v ? "1" : "0"));
        _gizmo = Check("Im 3D-Bild markieren", true, "Ausgewähltes Objekt im Spiel mit Box + Name zeigen (DebugLines)", v => SceneEditor.Send("gizmo", v ? "1" : "0"));
        _gizmoKids = Check("Kinder mit", false, "Auch die direkten Kinder (max. 200) blass markieren", v => SceneEditor.Send("gizmo_children", v ? "1" : "0"));
        opts.Controls.Add(_live);
        opts.Controls.Add(_gizmo);
        opts.Controls.Add(_gizmoKids);

        _tree = new BufferedTree { Dock = DockStyle.Fill, HideSelection = false, ShowLines = true, ShowNodeToolTips = true, FullRowSelect = false, BorderStyle = BorderStyle.None };
        _tree.AfterSelect += (_, e) =>
        {
            if (_applying || e.Node?.Tag is not TreeItem it || it.More) return;
            SceneEditor.Send("select", it.Id);
        };
        _tree.BeforeExpand += (_, e) =>
        {
            if (_applying || e.Node?.Tag is not TreeItem it) return;
            _pendingClose.Remove(it.Id);
            _pendingOpen[it.Id] = DateTime.UtcNow;
            SceneEditor.Send("expand", it.Id, "1");
        };
        _tree.BeforeCollapse += (_, e) =>
        {
            if (_applying || e.Node?.Tag is not TreeItem it) return;
            _pendingOpen.Remove(it.Id);
            _pendingClose[it.Id] = DateTime.UtcNow;
            SceneEditor.Send("expand", it.Id, "0");
        };
        _tree.NodeMouseClick += (_, e) => { if (e.Button == MouseButtons.Right) _tree.SelectedNode = e.Node; };
        _tree.KeyDown += (_, e) =>
        {
            // Unity: Alt+Shift+A = Aktiv umschalten; hier zusätzlich Leertaste
            if (e.KeyCode == Keys.Space && !e.Control) { ToggleActive(); e.Handled = e.SuppressKeyPress = true; }
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Aktiv umschalten (Leertaste)", null, (_, _) => ToggleActive());
        menu.Items.Add("Inspector zeigen", null, (_, _) => _ctx.ShowInspector());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Name kopieren", null, (_, _) => { if (_tree.SelectedNode?.Tag is TreeItem it) Copy(it.Name); });
        menu.Items.Add("Pfad kopieren", null, (_, _) => { if (_tree.SelectedNode != null) Copy(PathOf(_tree.SelectedNode)); });
        _tree.ContextMenuStrip = menu;

        var status = new StatusStrip { SizingGrip = true };
        _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        status.Items.Add(_status);

        Controls.Add(_tree);
        Controls.Add(opts);
        Controls.Add(bar);
        Controls.Add(status);
    }

    CheckBox Check(string text, bool on, string tip, Action<bool> changed)
    {
        var c = new CheckBox { Text = text, Checked = on, AutoSize = true, Margin = new Padding(2, 2, 10, 2) };
        new ToolTip().SetToolTip(c, tip);
        c.CheckedChanged += (_, _) => { if (!_applying) changed(c.Checked); };
        return c;
    }

    void ToggleActive()
    {
        if (_tree.SelectedNode?.Tag is not TreeItem it || it.Active == null) return;
        SceneEditor.Send("active", it.Id, it.Active.Value ? "0" : "1");
    }

    static void Copy(string s) { try { Clipboard.SetText(s); } catch (Exception) { } }

    static string PathOf(TreeNode n)
    {
        var parts = new List<string>();
        for (var x = n; x != null; x = x.Parent) parts.Insert(0, (x.Tag as TreeItem)?.Name ?? x.Text);
        return string.Join(" / ", parts);
    }

    // ------------------------------------------------------------------ Snapshot anwenden
    internal void Apply(SceneSnapshot s)
    {
        if (IsDisposed) return;
        _snap = s;
        _applying = true;
        try
        {
            if (_live.Checked != s.Live) _live.Checked = s.Live;
            if (_gizmo.Checked != s.Gizmo) _gizmo.Checked = s.Gizmo;
            if (_gizmoKids.Checked != s.GizmoChildren) _gizmoKids.Checked = s.GizmoChildren;

            var now = DateTime.UtcNow;
            Expire(_pendingOpen, now);
            Expire(_pendingClose, now);

            bool search = s.Search != null;
            if (search != _searchMode) { Begin(); _tree.Nodes.Clear(); _searchMode = search; }
            Sync(_tree.Nodes, search ? s.Search! : s.Roots, search);

            // Auswahl aus dem Spiel-Thread übernehmen (z.B. Treffer der Suche nach dem Leeren des Filters)
            if (s.SelectedId != null && (_tree.SelectedNode?.Tag as TreeItem)?.Id != s.SelectedId)
            {
                var found = _tree.Nodes.Find(s.SelectedId, true);
                if (found.Length > 0) { _tree.SelectedNode = found[0]; found[0].EnsureVisible(); }
            }

            _count.Text = search
                ? s.Search!.Count + " Treffer" + (s.SearchTruncated ? " (abgebrochen)" : "") + " · " + s.SearchVisited.ToString("N0") + " geprüft"
                : s.Visible.ToString("N0") + " Einträge";
            _status.Text = s.Status;
        }
        finally
        {
            if (_inUpdate) { _tree.EndUpdate(); _inUpdate = false; }
            _applying = false;
        }
    }

    static void Expire(Dictionary<string, DateTime> d, DateTime now)
    {
        if (d.Count == 0) return;
        foreach (var k in d.Where(kv => (now - kv.Value).TotalSeconds > 2.0).Select(kv => kv.Key).ToList()) d.Remove(k);
    }

    void Begin()
    {
        if (_inUpdate) return;
        _tree.BeginUpdate();
        _inUpdate = true;
    }

    void Sync(TreeNodeCollection col, List<TreeItem> items, bool search)
    {
        bool same = col.Count == items.Count;
        if (same)
            for (int i = 0; i < items.Count; i++)
                if (col[i].Name != items[i].Id) { same = false; break; }
        if (!same)
        {
            Begin();
            var old = new Dictionary<string, TreeNode>();
            foreach (TreeNode tn in col) old.TryAdd(tn.Name, tn);
            var arr = new TreeNode[items.Count];
            for (int i = 0; i < items.Count; i++)
                arr[i] = old.Remove(items[i].Id, out var tn) ? tn : new TreeNode { Name = items[i].Id };
            col.Clear();
            col.AddRange(arr);
        }
        for (int i = 0; i < items.Count; i++) UpdateNode(col[i], items[i], search);
    }

    void UpdateNode(TreeNode tn, TreeItem it, bool search)
    {
        tn.Tag = it;
        if (tn.Text != it.Name) tn.Text = it.Name;
        var fc = it.More ? MoreColor : it.Active == false ? Inactive : Color.Empty;
        if (tn.ForeColor != fc) tn.ForeColor = fc;
        string tip = it.Type + (it.ChildCount > 0 ? " · " + it.ChildCount.ToString("N0") + " Kinder" : "") + (it.Active == false ? " · inaktiv" : "");
        if (tn.ToolTipText != tip) tn.ToolTipText = tip;

        if (search) { if (tn.Nodes.Count > 0) { Begin(); tn.Nodes.Clear(); } return; }

        bool hasDummy = tn.Nodes.Count == 1 && tn.Nodes[0].Name == Dummy;
        if (it.Children != null && !_pendingClose.ContainsKey(it.Id))
        {
            _pendingOpen.Remove(it.Id);
            if (hasDummy) { Begin(); tn.Nodes.Clear(); }
            Sync(tn.Nodes, it.Children, false);
            if (!tn.IsExpanded) tn.Expand();
        }
        else if (it.ChildCount > 0)
        {
            if (_pendingOpen.ContainsKey(it.Id)) return;   // Kinder kommen mit dem nächsten Snapshot
            if (it.Children == null) _pendingClose.Remove(it.Id);
            if (tn.IsExpanded) tn.Collapse();
            if (!hasDummy) { Begin(); tn.Nodes.Clear(); tn.Nodes.Add(new TreeNode("…") { Name = Dummy }); }
        }
        else if (tn.Nodes.Count > 0) { Begin(); tn.Nodes.Clear(); }
    }

    public void FocusMe()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }
}
