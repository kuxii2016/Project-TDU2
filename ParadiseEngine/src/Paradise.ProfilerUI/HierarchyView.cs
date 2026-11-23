using Paradise.Core.Diagnostics;
using System.Globalization;

namespace Paradise.ProfilerUI;

/// <summary>
/// Hierarchy-Ansicht wie Unity: Baum der Messpunkte mit Total %, Self %, Calls, GC Alloc, Time ms, Self ms.
/// Spaltenkopf = sortieren, Pfeil/Doppelklick = auf-/zuklappen, Pfeiltasten wie im Explorer.
/// </summary>
internal sealed class HierarchyView : Control
{
    const int RowH = 18, HeaderH = 20, Indent = 14;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] Headers = { "Overview", "Total", "Self", "Calls", "GC Alloc", "Time ms", "Self ms" };
    static readonly int[] ColW = { 0, 70, 70, 60, 80, 75, 75 };   // 0 = Rest für den Namen

    readonly VScrollBar _scroll = new() { Dock = DockStyle.Right };
    HierarchyNode? _root;
    readonly List<HierarchyNode> _rows = new();
    readonly HashSet<string> _expanded = new();
    int _sortCol = 1;
    bool _sortDesc = true;
    double _frameTicks = 1;
    string? _selectedPath;
    string _filter = "";
    bool _autoExpanded;

    /// <summary>Gewählte Zeile hat sich geändert (Pfad).</summary>
    public event Action<HierarchyNode?>? SelectionChanged;

    public HierarchyView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        BackColor = ProfilerTheme.Panel;
        ForeColor = ProfilerTheme.Text;
        Controls.Add(_scroll);
        _scroll.Scroll += (_, _) => Invalidate();
        TabStop = true;
    }

    public HierarchyNode? SelectedNode => _selectedPath == null ? null : _rows.FirstOrDefault(r => r.Path == _selectedPath);

    /// <summary>Neuen Baum anzeigen; Auf-/Zuklapp-Zustand und Auswahl bleiben über den Pfad erhalten.</summary>
    public void SetData(HierarchyNode? root, double frameTicks)
    {
        _root = root;
        _frameTicks = Math.Max(1, frameTicks);
        if (root != null && !_autoExpanded && root.Children.Count > 0)
        {
            _autoExpanded = true;
            // Erstes Mal: die obersten zwei Ebenen aufklappen
            foreach (var c in root.Children) { _expanded.Add(c.Path); foreach (var cc in c.Children) if (cc.Children.Count > 0) _expanded.Add(cc.Path); }
        }
        Rebuild();
    }

    public void SetFilter(string text)
    {
        _filter = text.Trim();
        Rebuild();
    }

    public void ExpandAll(bool expand)
    {
        if (_root == null) return;
        if (!expand) _expanded.Clear();
        else HierarchyModel.Walk(_root, n => { if (n.Children.Count > 0) _expanded.Add(n.Path); });
        Rebuild();
    }

    void Rebuild()
    {
        _rows.Clear();
        if (_root != null)
        {
            if (_filter.Length > 0)
            {
                // Suche: flache Liste aller passenden Knoten (wie Unity-Suchfeld)
                var hits = new List<HierarchyNode>();
                HierarchyModel.Walk(_root, n => { if (n.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)) hits.Add(n); });
                Sort(hits);
                _rows.AddRange(hits);
            }
            else Add(_root);
        }
        int visible = Math.Max(1, (Height - HeaderH) / RowH);
        _scroll.Maximum = Math.Max(0, _rows.Count - 1);
        _scroll.LargeChange = visible;
        if (_scroll.Value > Math.Max(0, _rows.Count - visible)) _scroll.Value = Math.Max(0, _rows.Count - visible);
        Invalidate();
    }

    void Add(HierarchyNode n)
    {
        var kids = n.Children.ToList();
        Sort(kids);
        foreach (var c in kids)
        {
            c.Expanded = _expanded.Contains(c.Path);
            _rows.Add(c);
            if (c.Expanded && c.Children.Count > 0) Add(c);
        }
    }

    void Sort(List<HierarchyNode> l)
    {
        Comparison<HierarchyNode> cmp = _sortCol switch
        {
            0 => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            2 or 6 => (a, b) => a.SelfTicks.CompareTo(b.SelfTicks),
            3 => (a, b) => a.Calls.CompareTo(b.Calls),
            4 => (a, b) => a.Alloc.CompareTo(b.Alloc),
            _ => (a, b) => a.TotalTicks.CompareTo(b.TotalTicks),
        };
        l.Sort(_sortDesc ? (a, b) => cmp(b, a) : cmp);
    }

    int NameWidth => Math.Max(120, Width - _scroll.Width - ColW.Sum());

    protected override void OnPaint(PaintEventArgs pe)
    {
        var g = pe.Graphics;
        g.Clear(ProfilerTheme.Panel);
        using var tb = new SolidBrush(ProfilerTheme.Text);
        using var db = new SolidBrush(ProfilerTheme.TextDim);
        using var hb = new SolidBrush(ProfilerTheme.PanelDark);
        using var sb = new SolidBrush(ProfilerTheme.Selection);
        using var ab = new SolidBrush(ProfilerTheme.RowAlt);
        using var gp = new Pen(ProfilerTheme.Border);

        // Kopf
        g.FillRectangle(hb, 0, 0, Width, HeaderH);
        int x = 0;
        for (int c = 0; c < Headers.Length; c++)
        {
            int w = c == 0 ? NameWidth : ColW[c];
            string h = Headers[c] + (c == _sortCol ? (_sortDesc ? " ▼" : " ▲") : "");
            var r = new RectangleF(x + 4, 3, w - 8, HeaderH - 4);
            using var fmt = new StringFormat { Alignment = c == 0 ? StringAlignment.Near : StringAlignment.Far, Trimming = StringTrimming.EllipsisCharacter };
            g.DrawString(h, ProfilerTheme.UiBold, tb, r, fmt);
            x += w;
            g.DrawLine(gp, x - 1, 0, x - 1, HeaderH);
        }

        if (_root == null || _rows.Count == 0)
        {
            g.DrawString(_root == null ? "No frame selected." : "No samples in this frame / thread.", ProfilerTheme.UiFont, db, 8, HeaderH + 6);
            return;
        }

        int first = _scroll.Value;
        int y = HeaderH;
        using var nf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var rf = new StringFormat { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap };
        bool flat = _filter.Length > 0;
        for (int i = first; i < _rows.Count && y < Height; i++, y += RowH)
        {
            var n = _rows[i];
            bool sel = n.Path == _selectedPath;
            if (sel) g.FillRectangle(sb, 0, y, Width, RowH);
            else if ((i & 1) == 1) g.FillRectangle(ab, 0, y, Width, RowH);

            int indent = flat ? 4 : 4 + n.Depth * Indent;
            if (!flat && n.Children.Count > 0)
                g.DrawString(n.Expanded ? "▼" : "▶", ProfilerTheme.Small, db, indent, y + 2);
            using (var cb = new SolidBrush(ProfilerTheme.Category(n.Category))) g.FillRectangle(cb, indent + 13, y + 5, 7, 7);
            g.DrawString(flat ? n.Path : n.Name, ProfilerTheme.UiFont, tb, new RectangleF(indent + 24, y + 2, NameWidth - indent - 26, RowH - 2), nf);

            double frames = Math.Max(1, n.Frames);
            string[] vals =
            {
                (n.TotalTicks * 100.0 / _frameTicks).ToString("0.0", Inv) + "%",
                (n.SelfTicks * 100.0 / _frameTicks).ToString("0.0", Inv) + "%",
                (n.Calls / frames).ToString(frames > 1 ? "0.#" : "0", Inv),
                ProfilerCounter.FormatBytes(n.Alloc / frames),
                (Profiler.TicksToMs(n.TotalTicks) / frames).ToString("0.00", Inv),
                (Profiler.TicksToMs(n.SelfTicks) / frames).ToString("0.00", Inv),
            };
            x = NameWidth;
            for (int c = 1; c < Headers.Length; c++)
            {
                bool hot = c == 4 && n.Alloc > 0;   // GC Alloc hervorheben
                using var vb = new SolidBrush(hot ? Color.FromArgb(240, 150, 90) : ProfilerTheme.Text);
                g.DrawString(vals[c - 1], ProfilerTheme.UiFont, vb, new RectangleF(x, y + 2, ColW[c] - 6, RowH - 2), rf);
                x += ColW[c];
            }
        }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Rebuild(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int v = _scroll.Value - Math.Sign(e.Delta) * 3;
        _scroll.Value = Math.Clamp(v, 0, Math.Max(0, _rows.Count - _scroll.LargeChange));
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Y < HeaderH)
        {
            int x = 0;
            for (int c = 0; c < Headers.Length; c++)
            {
                int w = c == 0 ? NameWidth : ColW[c];
                if (e.X >= x && e.X < x + w)
                {
                    if (_sortCol == c) _sortDesc = !_sortDesc; else { _sortCol = c; _sortDesc = c != 0; }
                    Rebuild();
                    return;
                }
                x += w;
            }
            return;
        }
        int i = _scroll.Value + (e.Y - HeaderH) / RowH;
        if (i < 0 || i >= _rows.Count) return;
        var n = _rows[i];
        SelectNode(n);
        int arrowX = 4 + n.Depth * Indent;
        if (_filter.Length == 0 && e.X >= arrowX - 2 && e.X <= arrowX + 12) Toggle(n);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var n = SelectedNode;
        if (n != null && _filter.Length == 0) Toggle(n);
    }

    void SelectNode(HierarchyNode n)
    {
        _selectedPath = n.Path;
        SelectionChanged?.Invoke(n);
        Invalidate();
    }

    void Toggle(HierarchyNode n)
    {
        if (n.Children.Count == 0) return;
        if (!_expanded.Remove(n.Path)) _expanded.Add(n.Path);
        Rebuild();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int i = _rows.FindIndex(r => r.Path == _selectedPath);
        if (e.KeyCode == Keys.Down && i + 1 < _rows.Count) SelectNode(_rows[i + 1]);
        else if (e.KeyCode == Keys.Up && i > 0) SelectNode(_rows[i - 1]);
        else if (e.KeyCode == Keys.Right && i >= 0 && !_expanded.Contains(_rows[i].Path)) Toggle(_rows[i]);
        else if (e.KeyCode == Keys.Left && i >= 0 && _expanded.Contains(_rows[i].Path)) Toggle(_rows[i]);
        else return;
        i = _rows.FindIndex(r => r.Path == _selectedPath);
        int visible = Math.Max(1, (Height - HeaderH) / RowH);
        if (i >= 0 && i < _scroll.Value) _scroll.Value = i;
        else if (i >= _scroll.Value + visible) _scroll.Value = Math.Min(_scroll.Maximum, i - visible + 1);
        Invalidate();
    }
}
