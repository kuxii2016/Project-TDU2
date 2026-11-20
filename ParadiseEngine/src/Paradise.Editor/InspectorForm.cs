// Szenen-Editor: Inspector-Fenster (Unity: Inspector). Läuft auf dem STA-UI-Thread.
// Kopf: Aktiv-Haken + Name + Typ. Darunter einklappbare Abschnitte (Transform, Bounds, LOD Group, Mesh Renderer, Materialien,
// Shader-Parameter). Gleicher Aufbau wie beim letzten Snapshot → Werte an Ort und Stelle aktualisieren (Feld mit Fokus bleibt
// unangetastet), sonst neu aufbauen. Eingeklappte Abschnitte erzeugen keine Steuerelemente.

using System.Globalization;

namespace Paradise.Editor;

internal sealed class InspectorForm : Form
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly CheckBox _active;
    readonly Label _name, _type;
    readonly FlowLayoutPanel _body;
    readonly ToolStripStatusLabel _status;
    readonly Label _empty;
    InspectorData? _data;
    string _sig = "";
    bool _applying;
    // Vom Nutzer gewählter Klapp-Zustand je Abschnittstitel (bleibt beim Wechsel der Auswahl erhalten, wie in Unity)
    readonly Dictionary<string, bool> _collapsed = new();
    readonly List<SectionView> _sections = new();

    public InspectorForm()
    {
        Text = "Inspector – Paradise Engine";
        Name = "SceneInspector";
        Size = new Size(520, 800);
        MinimumSize = new Size(360, 300);
        Font = new Font("Segoe UI", 9f);

        var head = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, RowCount = 2, Padding = new Padding(6, 6, 6, 4) };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _active = new CheckBox { Text = "Aktiv", AutoSize = true, Margin = new Padding(0, 4, 10, 0), Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        _active.CheckedChanged += (_, _) => { if (!_applying && _data != null) SceneEditor.Send("active", _data.Id, _active.Checked ? "1" : "0"); };
        new ToolTip().SetToolTip(_active, "Aktiv (Unity: activeSelf)");
        _name = new Label { AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Margin = new Padding(0, 2, 0, 0) };
        _type = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 2) };
        var copy = new Button { Text = "Kopieren", AutoSize = true };
        copy.Click += (_, _) => { if (_data != null) try { Clipboard.SetText(SceneEditor.ToText(_data)); } catch (Exception) { } };
        new ToolTip().SetToolTip(copy, "Alle Werte als Text in die Zwischenablage");
        var focus = new Button { Text = "Fokus", AutoSize = true };
        focus.Click += (_, _) => { if (_data != null) SceneEditor.Send("focus", _data.Id); };
        new ToolTip().SetToolTip(focus, "Kamera auf dieses Objekt fahren (Free-fly)");
        head.Controls.Add(_active, 0, 0);
        head.Controls.Add(_name, 1, 0);
        head.Controls.Add(focus, 2, 0);
        head.Controls.Add(copy, 3, 0);
        head.Controls.Add(_type, 1, 1);
        head.SetColumnSpan(_type, 3);

        _body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(4, 0, 4, 8) };
        _body.Resize += (_, _) => FitWidths();

        _empty = new Label { Text = "Nichts ausgewählt – in der Hierarchy ein Objekt anklicken.", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(8) };

        var status = new StatusStrip { SizingGrip = true };
        _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        status.Items.Add(_status);

        Controls.Add(_body);
        Controls.Add(new Label { Dock = DockStyle.Top, Height = 1, BackColor = SystemColors.ControlDark });
        Controls.Add(head);
        Controls.Add(status);
        ShowEmpty();
    }

    void ShowEmpty()
    {
        _body.Controls.Clear();
        foreach (var s in _sections) s.Dispose();
        _sections.Clear();
        _body.Controls.Add(_empty);
        _active.Visible = false;
        _name.Text = "Inspector";
        _type.Text = "";
        _sig = "";
    }

    int SectionWidth => System.Math.Max(200, _body.ClientSize.Width - _body.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 2);

    void FitWidths()
    {
        int w = SectionWidth;
        foreach (var s in _sections) if (s.Width != w) s.Width = w;
    }

    internal void Apply(InspectorData? d, string status)
    {
        if (IsDisposed) return;
        _status.Text = status;
        _applying = true;
        try
        {
            if (d == null) { if (_data != null || _sig.Length > 0) { _data = null; ShowEmpty(); } return; }
            _data = d;
            _name.Text = d.Name;
            _type.Text = d.Type;
            // Wie Unity immer sichtbar; ohne Schalter (Gruppe, LOD, Knochen …) ausgegraut
            _active.Visible = true;
            _active.Enabled = d.Active != null;
            bool on = d.Active ?? true;
            if (_active.Checked != on) _active.Checked = on;

            if (d.Signature == _sig)
            {
                for (int i = 0; i < _sections.Count && i < d.Sections.Count; i++) _sections[i].Update(d.Sections[i]);
                return;
            }
            Rebuild(d);
        }
        finally { _applying = false; }
    }

    void Rebuild(InspectorData d)
    {
        _sig = d.Signature;
        int scroll = _body.VerticalScroll.Value;
        bool sameObject = _sections.Count > 0 && _sections[0].OwnerId == d.Id;
        _body.SuspendLayout();
        try
        {
            _body.Controls.Clear();
            foreach (var s in _sections) s.Dispose();
            _sections.Clear();
            int w = SectionWidth;
            foreach (var sec in d.Sections)
            {
                bool collapsed = _collapsed.TryGetValue(sec.Title, out var c) ? c : sec.Collapsed;
                var v = new SectionView(this, d.Id, sec, collapsed) { Width = w };
                _sections.Add(v);
                _body.Controls.Add(v);
            }
        }
        finally { _body.ResumeLayout(true); }
        if (sameObject) try { _body.VerticalScroll.Value = System.Math.Min(scroll, _body.VerticalScroll.Maximum); } catch (Exception) { }
    }

    internal void SetCollapsed(string title, bool collapsed) => _collapsed[title] = collapsed;
    internal bool Applying => _applying;

    // ------------------------------------------------------------------ Abschnitt
    sealed class SectionView : TableLayoutPanel
    {
        readonly InspectorForm _form;
        readonly Label _header;
        TableLayoutPanel? _rows;
        readonly List<RowView> _views = new();
        InspectorSection _sec;
        bool _collapsedState;
        public readonly string OwnerId;

        public SectionView(InspectorForm form, string ownerId, InspectorSection sec, bool collapsed)
        {
            _form = form; _sec = sec; OwnerId = ownerId; _collapsedState = collapsed;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ColumnCount = 1;
            Margin = new Padding(0, 4, 0, 0);
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _header = new Label
            {
                AutoSize = false,
                Height = 22,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                BackColor = SystemColors.ControlLight,
                Margin = new Padding(0),
                Padding = new Padding(2, 0, 0, 0),
            };
            _header.Click += (_, _) => Toggle();
            Controls.Add(_header, 0, 0);
            UpdateHeader();
            if (!_collapsedState) BuildRows();
        }

        void UpdateHeader() => _header.Text = (_collapsedState ? "▶  " : "▼  ") + _sec.Title + (_collapsedState ? "  (" + _sec.Rows.Count + ")" : "");

        void Toggle()
        {
            _collapsedState = !_collapsedState;
            _form.SetCollapsed(_sec.Title, _collapsedState);
            SuspendLayout();
            if (_collapsedState) { if (_rows != null) { Controls.Remove(_rows); _rows.Dispose(); _rows = null; } _views.Clear(); }
            else BuildRows();
            UpdateHeader();
            ResumeLayout(true);
        }

        void BuildRows()
        {
            _rows = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 0, 2),
                Padding = new Padding(6, 0, 0, 0),
            };
            _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170f));
            _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _rows.SuspendLayout();
            _views.Clear();
            for (int i = 0; i < _sec.Rows.Count; i++)
            {
                var r = new RowView(_form, OwnerId, _sec.Rows[i]);
                _views.Add(r);
                _rows.Controls.Add(r.LabelControl, 0, i);
                _rows.Controls.Add(r.ValueControl, 1, i);
            }
            _rows.ResumeLayout(true);
            Controls.Add(_rows, 0, 1);
        }

        public void Update(InspectorSection sec)
        {
            _sec = sec;
            if (_collapsedState) { UpdateHeader(); return; }
            for (int i = 0; i < _views.Count && i < sec.Rows.Count; i++) _views[i].Update(sec.Rows[i]);
        }
    }

    // ------------------------------------------------------------------ Zeile
    sealed class RowView
    {
        readonly InspectorForm _form;
        readonly string _owner;
        InspectorRow _row;
        public readonly Control LabelControl;
        public readonly Control ValueControl;
        readonly TextBox? _text;
        readonly CheckBox? _check;
        readonly TextBox[]? _vec;
        string _sent = "";

        public RowView(InspectorForm form, string owner, InspectorRow row)
        {
            _form = form; _owner = owner; _row = row;
            LabelControl = new Label { Text = row.Label, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = new Padding(0, 1, 4, 1) };
            new ToolTip().SetToolTip(LabelControl, row.Label);
            bool editable = row.Key != null;
            switch (row.Kind)
            {
                case RowKind.Bool:
                    {
                        var cb = new CheckBox { Checked = row.Value == "1", Enabled = editable, AutoSize = true, Margin = new Padding(0, 3, 0, 1) };
                        cb.CheckedChanged += (_, _) =>
                        {
                            if (_form.Applying || _row.Key == null) return;
                            SceneEditor.Send("set", _owner, _row.Key, cb.Checked ? "1" : "0");
                        };
                        _check = cb;
                        ValueControl = cb;
                        break;
                    }

                case RowKind.Vec3:
                    {
                        var p = new TableLayoutPanel { ColumnCount = 6, RowCount = 1, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0) };
                        for (int i = 0; i < 3; i++)
                        {
                            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
                        }
                        _vec = new TextBox[3];
                        var parts = Split(row.Value);
                        string[] axes = { "X", "Y", "Z" };
                        Color[] colors = { Color.FromArgb(200, 60, 60), Color.FromArgb(60, 160, 60), Color.FromArgb(60, 90, 210) };
                        for (int i = 0; i < 3; i++)
                        {
                            p.Controls.Add(new Label { Text = axes[i], AutoSize = true, ForeColor = colors[i], Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Margin = new Padding(i == 0 ? 0 : 4, 4, 1, 0) }, i * 2, 0);
                            var tb = new TextBox { Text = parts[i], ReadOnly = !editable, Dock = DockStyle.Fill, Margin = new Padding(0, 1, 0, 1) };
                            if (!editable) { tb.BorderStyle = BorderStyle.None; tb.BackColor = SystemColors.Control; }
                            else Commit(tb, CommitVec);
                            _vec[i] = tb;
                            p.Controls.Add(tb, i * 2 + 1, 0);
                        }
                        ValueControl = p;
                        _sent = row.Value;
                        break;
                    }

                case RowKind.Text:
                    {
                        var tb = new TextBox { Text = row.Value, ReadOnly = !editable, Dock = DockStyle.Fill, Margin = new Padding(0, 1, 0, 1) };
                        if (editable) Commit(tb, () =>
                        {
                            if (_row.Key == null || tb.Text == _sent) return;
                            _sent = tb.Text;
                            SceneEditor.Send("set", _owner, _row.Key, tb.Text);
                        });
                        _sent = row.Value;
                        _text = tb;
                        ValueControl = tb;
                        break;
                    }

                default:
                    _text = new TextBox { Text = row.Value, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 1) };
                    ValueControl = _text;
                    break;
            }
        }

        static string[] Split(string v)
        {
            var p = (v ?? "").Split(';');
            return p.Length == 3 ? p : new[] { "", "", "" };
        }

        /// <summary>Übernehmen mit Enter oder beim Verlassen des Feldes (wie Unity).</summary>
        static void Commit(TextBox tb, Action commit)
        {
            tb.KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                commit();
            };
            tb.Leave += (_, _) => commit();
        }

        void CommitVec()
        {
            if (_vec == null || _row.Key == null) return;
            var v = new float[3];
            for (int i = 0; i < 3; i++)
            {
                string t = _vec[i].Text.Trim().Replace(',', '.');
                if (!float.TryParse(t, NumberStyles.Float, Inv, out v[i])) { _vec[i].BackColor = Color.MistyRose; return; }
                _vec[i].BackColor = SystemColors.Window;
            }
            string s = v[0].ToString("0.####", Inv) + ";" + v[1].ToString("0.####", Inv) + ";" + v[2].ToString("0.####", Inv);
            if (s == _sent) return;
            _sent = s;
            SceneEditor.Send("set", _owner, _row.Key, s);
        }

        public void Update(InspectorRow row)
        {
            _row = row;
            switch (row.Kind)
            {
                case RowKind.Bool:
                    if (_check != null && _check.Checked != (row.Value == "1")) _check.Checked = row.Value == "1";
                    break;
                case RowKind.Vec3:
                    if (_vec == null || ValueControl.ContainsFocus) return;
                    var p = Split(row.Value);
                    for (int i = 0; i < 3; i++) if (_vec[i].Text != p[i]) _vec[i].Text = p[i];
                    _sent = row.Value;
                    break;
                default:
                    if (_text == null || _text.Focused) return;
                    if (_text.Text != row.Value) _text.Text = row.Value;
                    if (row.Kind == RowKind.Text) _sent = row.Value;
                    break;
            }
        }
    }
}
