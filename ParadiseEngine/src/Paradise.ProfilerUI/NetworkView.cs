using Paradise.Core.Diagnostics;
using System.Globalization;

namespace Paradise.ProfilerUI;

/// <summary>
/// Network-Reiter des Profilers: Baum mit Server-TCP/UDP, P2P-Direkt, Relay, Mitspielern und
/// Pakettypen. Werte sind Bytes/s bzw. Pakete/s des gewählten Bildes (oder Ø aller Bilder).
/// </summary>
internal sealed class NetworkView : Control
{
    const int RowH = 18, HeaderH = 20, Indent = 14;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] Headers = { "Overview", "In", "Out", "Packets in", "Packets out", "Details" };
    static readonly int[] NumW = { 82, 82, 88, 88 };
    static readonly string[] GroupOrder =
    {
        ProfilerNetworkGroups.Tcp, ProfilerNetworkGroups.Udp, ProfilerNetworkGroups.Direct,
        ProfilerNetworkGroups.Relay, ProfilerNetworkGroups.Players, ProfilerNetworkGroups.Packets,
    };

    sealed class NetRow
    {
        public string Path = "", Name = "", Detail = "";
        public long InBytes, OutBytes, InCount, OutCount;
        public int Depth;
        public bool HasChildren, Expanded, Absolute;
    }

    readonly VScrollBar _scroll = new() { Dock = DockStyle.Right };
    readonly List<NetRow> _rows = new();
    readonly HashSet<string> _expanded = new();
    ProfilerNetworkSnapshot _snap = ProfilerNetworkSnapshot.Empty;
    double _frameMs = 16.7;
    string _filter = "";
    string? _selectedPath;
    bool _autoExpanded;

    public NetworkView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        BackColor = ProfilerTheme.Panel;
        ForeColor = ProfilerTheme.Text;
        Controls.Add(_scroll);
        _scroll.Scroll += (_, _) => Invalidate();
        TabStop = true;
    }

    public void SetData(ProfilerNetworkSnapshot snap, double frameMs)
    {
        _snap = snap ?? ProfilerNetworkSnapshot.Empty;
        _frameMs = Math.Max(0.1, frameMs);
        if (!_autoExpanded && _snap.Entries.Length > 0)
        {
            _autoExpanded = true;
            foreach (var g in GroupOrder) _expanded.Add("G:" + g);
        }
        Rebuild();
    }

    public void SetFilter(string text) { _filter = text.Trim(); Rebuild(); }

    public void ExpandAll(bool expand)
    {
        if (!expand) _expanded.Clear();
        else foreach (var g in GroupOrder) _expanded.Add("G:" + g);
        Rebuild();
    }

    void Rebuild()
    {
        _rows.Clear();
        var groups = new Dictionary<string, List<ProfilerNetworkEntry>>(StringComparer.Ordinal);
        foreach (var e in _snap.Entries)
        {
            if (!groups.TryGetValue(e.Group, out var l)) groups[e.Group] = l = new List<ProfilerNetworkEntry>();
            l.Add(e);
        }
        var names = groups.Keys.OrderBy(n =>
        {
            int i = Array.IndexOf(GroupOrder, n);
            return i < 0 ? GroupOrder.Length : i;
        }).ThenBy(n => n, StringComparer.OrdinalIgnoreCase);

        foreach (string group in names)
        {
            var entries = groups[group];
            if (_filter.Length > 0)
            {
                foreach (var e in entries)
                {
                    if (e.Name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        e.Detail.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        group.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    _rows.Add(Row(e, "G:" + group + "/" + e.Name, 0, false));
                }
                continue;
            }
            long ib = 0, ob = 0, ic = 0, oc = 0;
            bool absolute = false;
            foreach (var e in entries) { ib += e.InBytes; ob += e.OutBytes; ic += e.InCount; oc += e.OutCount; absolute |= e.Absolute; }
            string path = "G:" + group;
            _rows.Add(new NetRow
            {
                Path = path,
                Name = group,
                Detail = entries.Count.ToString(Inv) + " rows",
                InBytes = ib,
                OutBytes = ob,
                InCount = ic,
                OutCount = oc,
                HasChildren = true,
                Expanded = _expanded.Contains(path),
                Absolute = absolute,
            });
            if (!_rows[^1].Expanded) continue;
            foreach (var e in entries.OrderByDescending(x => x.TotalBytes).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                _rows.Add(Row(e, path + "/" + e.Name, 1, false));
        }

        int visible = Math.Max(1, (Height - HeaderH) / RowH);
        _scroll.Maximum = Math.Max(0, _rows.Count - 1);
        _scroll.LargeChange = visible;
        if (_scroll.Value > Math.Max(0, _rows.Count - visible)) _scroll.Value = Math.Max(0, _rows.Count - visible);
        Invalidate();
    }

    static NetRow Row(ProfilerNetworkEntry e, string path, int depth, bool hasChildren) => new()
    {
        Path = path,
        Name = e.Name,
        Detail = e.Detail,
        Depth = depth,
        HasChildren = hasChildren,
        InBytes = e.InBytes,
        OutBytes = e.OutBytes,
        InCount = e.InCount,
        OutCount = e.OutCount,
        Absolute = e.Absolute,
    };

    int NameWidth
    {
        get
        {
            int avail = Math.Max(400, Width - _scroll.Width);
            return Math.Max(150, (int)((avail - NumW.Sum()) * 0.45));
        }
    }

    int DetailWidth => Math.Max(100, Width - _scroll.Width - NumW.Sum() - NameWidth);

    static Color GroupColor(string group)
    {
        int i = Array.IndexOf(GroupOrder, group);
        return i < 0 ? ProfilerTheme.TextDim : ProfilerTheme.Series(i);
    }

    string Bytes(long v, bool absolute) => absolute
        ? ProfilerCounter.FormatBytes(v)
        : ProfilerCounter.FormatBytes(v * 1000.0 / _frameMs) + "/s";

    string Count(long v, bool absolute) => absolute
        ? ProfilerCounter.FormatCount(v)
        : ProfilerCounter.FormatCount(v * 1000.0 / _frameMs) + "/s";

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

        g.FillRectangle(hb, 0, 0, Width, HeaderH);
        int x = 0, nameW = NameWidth;
        for (int c = 0; c < Headers.Length; c++)
        {
            int w = c == 0 ? nameW : c == Headers.Length - 1 ? DetailWidth : NumW[c - 1];
            var r = new RectangleF(x + 4, 3, w - 8, HeaderH - 4);
            using var fmt = new StringFormat { Alignment = c == 0 || c == Headers.Length - 1 ? StringAlignment.Near : StringAlignment.Far, Trimming = StringTrimming.EllipsisCharacter };
            g.DrawString(Headers[c], ProfilerTheme.UiBold, tb, r, fmt);
            x += w;
            g.DrawLine(gp, x - 1, 0, x - 1, HeaderH);
        }

        if (_rows.Count == 0)
        {
            g.DrawString(_snap.Entries.Length == 0 ? "No network data in this frame." : "Nothing matches the filter.",
                ProfilerTheme.UiFont, db, 8, HeaderH + 6);
            return;
        }

        int first = _scroll.Value;
        int y = HeaderH;
        using var nf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var rf = new StringFormat { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap };
        bool flat = _filter.Length > 0;
        for (int i = first; i < _rows.Count && y < Height; i++, y += RowH)
        {
            var r = _rows[i];
            bool sel = r.Path == _selectedPath;
            if (sel) g.FillRectangle(sb, 0, y, Width, RowH);
            else if ((i & 1) == 1) g.FillRectangle(ab, 0, y, Width, RowH);

            int indent = flat ? 4 : 4 + r.Depth * Indent;
            if (!flat && r.HasChildren)
                g.DrawString(r.Expanded ? "▼" : "▶", ProfilerTheme.Small, db, indent, y + 2);
            using (var cb = new SolidBrush(r.Depth == 0 ? GroupColor(r.Name) : ProfilerTheme.TextDim))
                g.FillRectangle(cb, indent + 13, y + 5, 7, 7);
            g.DrawString(r.Name, ProfilerTheme.UiFont, tb,
                new RectangleF(indent + 24, y + 2, nameW - indent - 26, RowH - 2), nf);

            string[] vals = { Bytes(r.InBytes, r.Absolute), Bytes(r.OutBytes, r.Absolute), Count(r.InCount, r.Absolute), Count(r.OutCount, r.Absolute) };
            int px = nameW;
            for (int c = 0; c < vals.Length; c++)
            {
                g.DrawString(vals[c], ProfilerTheme.Small, tb, new RectangleF(px, y + 2, NumW[c] - 5, RowH - 2), rf);
                px += NumW[c];
            }
            g.DrawString(r.Detail, ProfilerTheme.Small, db, new RectangleF(px + 4, y + 2, DetailWidth - 8, RowH - 2), nf);
        }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Rebuild(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scroll.Value = Math.Clamp(_scroll.Value - Math.Sign(e.Delta) * 3, 0, Math.Max(0, _rows.Count - _scroll.LargeChange));
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int i = _scroll.Value + (e.Y - HeaderH) / RowH;
        if (i < 0 || i >= _rows.Count) return;
        var r = _rows[i];
        _selectedPath = r.Path;
        Invalidate();
        int arrow = 4 + r.Depth * Indent;
        if (_filter.Length == 0 && r.HasChildren && e.X >= arrow - 2 && e.X <= arrow + 12)
            Toggle(r);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int i = _scroll.Value + (e.Y - HeaderH) / RowH;
        if (i >= 0 && i < _rows.Count && _filter.Length == 0) Toggle(_rows[i]);
    }

    void Toggle(NetRow r)
    {
        if (!r.HasChildren) return;
        if (!_expanded.Remove(r.Path)) _expanded.Add(r.Path);
        Rebuild();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int i = _rows.FindIndex(r => r.Path == _selectedPath);
        if (e.KeyCode == Keys.Down && i + 1 < _rows.Count) _selectedPath = _rows[i + 1].Path;
        else if (e.KeyCode == Keys.Up && i > 0) _selectedPath = _rows[i - 1].Path;
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
