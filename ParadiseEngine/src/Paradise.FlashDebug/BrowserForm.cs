using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Paradise.FlashDebug;

/// <summary>Flash-Browser-Fenster (Port des Unity "TDU2/Flash Debugger"). Läuft auf dem STA-UI-Thread.</summary>
internal sealed partial class BrowserForm : Form
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    FlashSnapshot? _snap;
    bool _applying;
    bool _treeApplying;

    // Vom UI-Thread gepflegt, vom Spiel-Thread gelesen (kein Cross-Thread-Control-Zugriff).
    volatile bool _uiBusy;
    bool _modal;
    long _lastVarsUpdate, _lastFnsUpdate;

    ComboBox _players = null!;
    CheckBox _live = null!;
    TextBox _status = null!;

    TabControl _tabs = null!;
    TreeView _tree = null!;
    Label _treeCount = null!;
    TextBox _treeFilter = null!;
    CheckBox _onlyVisible = null!;
    CheckBox _onlyClips = null!;
    CheckBox _showPaths = null!;

    DataGridView _varGrid = null!;
    TextBox _varFilter = null!;
    Label _varCount = null!;
    TextBox _newVarKey = null!, _newVarValue = null!;
    CheckBox _autoCall = null!;
    FlowLayoutPanel _usageFlow = null!;
    Label _usageTitle = null!;

    DataGridView _fnGrid = null!;
    TextBox _fnFilter = null!;
    Label _fnCount = null!;

    ListBox _logList = null!;
    TextBox _logFilter = null!;

    Label _focus = null!, _activeList = null!;

    public BrowserForm()
    {
        Text = "Flash Browser – Paradise Engine";
        Name = "FlashBrowser";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1180, 800);
        MinimumSize = new Size(900, 600);
        Font = new Font("Segoe UI", 9f);
        BuildUi();
        UpdateBusy();
    }

    /// <summary>Spiel-Thread: true, wenn die UI gerade mit einer Eingabe beschäftigt ist.</summary>
    internal bool IsBusy => _uiBusy;

    void UpdateBusy()
    {
        _uiBusy = IsDisposed || WindowState == FormWindowState.Minimized || _modal ||
                  _players.DroppedDown ||
                  _varGrid.IsCurrentCellInEditMode || _fnGrid.IsCurrentCellInEditMode ||
                  _dVars.IsCurrentCellInEditMode || _dFns.IsCurrentCellInEditMode;
    }

    void QueueBusyUpdate()
    {
        if (IsDisposed) return;
        try { BeginInvoke(new Action(UpdateBusy)); } catch (Exception) { }
    }

    void BuildUi()
    {
        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
        bar.Items.Add(new ToolStripLabel("Film:"));
        _players = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 310 };
        _players.SelectedIndexChanged += (_, _) =>
        {
            if (!_applying && _players.SelectedIndex >= 0) FlashBrowser.Send("select_player", _players.SelectedIndex.ToString(Inv));
        };
        _players.DropDown += (_, _) => { _uiBusy = true; };
        _players.DropDownClosed += (_, _) => QueueBusyUpdate();
        bar.Items.Add(new ToolStripControlHost(_players));
        bar.Items.Add(new ToolStripButton("Film laden…", null, (_, _) => LoadBank()) { ToolTipText = "Frontend-Bank (*.bnk) im Spiel öffnen" });
        bar.Items.Add(new ToolStripButton("Viewer schließen", null, (_, _) => FlashBrowser.Send("close_viewers")));
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(new ToolStripButton("Neu laden", null, (_, _) => FlashBrowser.Send("reset_movie")) { ToolTipText = "Ausgewählten Film auf Ausgangszustand zurücksetzen" });
        bar.Items.Add(new ToolStripButton("Baum in Datei", null, (_, _) => SaveTree()));
        bar.Items.Add(new ToolStripSeparator());
        _live = new CheckBox { Text = "Live", Checked = true, AutoSize = true, Padding = new Padding(4, 0, 4, 0) };
        _live.CheckedChanged += (_, _) => { if (!_applying) FlashBrowser.Send("live", _live.Checked ? "1" : "0"); };
        bar.Items.Add(new ToolStripControlHost(_live));
        bar.Items.Add(new ToolStripLabel("F1 > Flash Debug > Browse Flash"));

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_snap == null || _applying) return;
            _lastVarsUpdate = _lastFnsUpdate = 0;   // beim Wechsel sofort aktuell
            Apply(_snap);
        };

        // ---------------- Baum ----------------
        var treePage = new TabPage("Baum") { Padding = new Padding(4) };
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 600, Orientation = Orientation.Vertical };
        var left = new Panel { Dock = DockStyle.Fill };
        var filterRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 0, 0, 4) };
        _treeFilter = new TextBox { Width = 200, PlaceholderText = "Filter (Name)" };
        _treeFilter.TextChanged += (_, _) => { if (!_applying) FlashBrowser.Send("filter", _treeFilter.Text); };
        _onlyVisible = new CheckBox { Text = "nur sichtbare", Checked = false, AutoSize = true };
        _onlyClips = new CheckBox { Text = "nur Clips", AutoSize = true };
        _showPaths = new CheckBox { Text = "Pfade", AutoSize = true };
        _onlyVisible.CheckedChanged += (_, _) => { if (!_applying) FlashBrowser.Send("only_visible", _onlyVisible.Checked ? "1" : "0"); };
        _onlyClips.CheckedChanged += (_, _) => { if (!_applying) FlashBrowser.Send("only_clips", _onlyClips.Checked ? "1" : "0"); };
        _showPaths.CheckedChanged += (_, _) => { if (!_applying) FlashBrowser.Send("show_paths", _showPaths.Checked ? "1" : "0"); };
        _treeCount = new Label { AutoSize = true, Padding = new Padding(10, 5, 0, 0) };
        filterRow.Controls.Add(_treeFilter);
        filterRow.Controls.Add(_onlyVisible);
        filterRow.Controls.Add(_onlyClips);
        filterRow.Controls.Add(_showPaths);
        filterRow.Controls.Add(B("Alle auf", 80, () =>
        {
            FlashBrowser.Send("expand_all", "1");
            SetAllExpanded(true);
        }));
        filterRow.Controls.Add(B("Alle zu", 70, () =>
        {
            FlashBrowser.Send("expand_all", "0");
            SetAllExpanded(false);
        }));
        filterRow.Controls.Add(_treeCount);
        _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, ShowLines = true };
        _tree.AfterSelect += (_, _) =>
        {
            if (_applying || _tree.SelectedNode?.Tag is not string path) return;
            FlashBrowser.Send("select_path", path);
        };
        _tree.BeforeExpand += (_, e) => { if (_treeApplying) return; if (e.Node.Tag is string p) FlashBrowser.Send("expand", p, "1"); };
        _tree.BeforeCollapse += (_, e) => { if (_treeApplying) return; if (e.Node.Tag is string p) FlashBrowser.Send("expand", p, "0"); };
        left.Controls.Add(_tree);
        left.Controls.Add(filterRow);
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(BuildDetailPanel());
        treePage.Controls.Add(split);
        _tabs.TabPages.Add(treePage);

        // ---------------- Variablen ----------------
        var varPage = new TabPage("Variablen") { Padding = new Padding(4) };
        var varTop = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 4) };
        varTop.Controls.Add(new Label { Text = "Filter (Pfad/Name/Wert)", AutoSize = true, Padding = new Padding(0, 5, 4, 0) });
        _varFilter = new TextBox { Width = 260 };
        _varFilter.TextChanged += (_, _) => FilterVars();
        varTop.Controls.Add(_varFilter);
        _varCount = new Label { AutoSize = true, Padding = new Padding(12, 5, 0, 0) };
        varTop.Controls.Add(_varCount);
        _varGrid = Grid(new[] { ("Key", "Name / Schlüssel", 340), ("Value", "Wert (editierbar)", 260), ("Usage", "?", 40), ("Copy", "⧉", 34) });
        _varGrid.CellEndEdit += (_, e) =>
        {
            if (_applying || e.RowIndex < 0 || e.ColumnIndex != 1) return;
            string key = Cell(_varGrid, e.RowIndex, 0);
            string value = Cell(_varGrid, e.RowIndex, 1);
            int sp = value.IndexOf("  (", StringComparison.Ordinal);
            if (sp >= 0 && (value.EndsWith("(Zahl)", StringComparison.Ordinal) || value.EndsWith("(Bool)", StringComparison.Ordinal)))
                value = value.Substring(0, sp);
            FlashBrowser.Send("set_var", key, value);
        };
        _varGrid.CellClick += (_, e) =>
        {
            if (_applying || e.RowIndex < 0) return;
            string key = Cell(_varGrid, e.RowIndex, 0);
            if (e.ColumnIndex == 2) FlashBrowser.Send("usage", key);
            if (e.ColumnIndex == 3) Copy(VarCode(key, Cell(_varGrid, e.RowIndex, 1)));
        };
        var varBottom = new Panel { Dock = DockStyle.Bottom, Height = 112 };
        var setRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 4, 0, 2) };
        setRow.Controls.Add(new Label { Text = "Neu setzen", AutoSize = true, Padding = new Padding(0, 5, 4, 0) });
        _newVarKey = new TextBox { Width = 260 };
        _newVarValue = new TextBox { Width = 180 };
        setRow.Controls.Add(_newVarKey);
        setRow.Controls.Add(_newVarValue);
        setRow.Controls.Add(new Button { Text = "Setzen", AutoSize = true, FlatStyle = FlatStyle.System });
        setRow.Controls[setRow.Controls.Count - 1].Click += (_, _) =>
        {
            if (_newVarKey.Text.Length == 0) return;
            string key = _newVarKey.Text.Contains(':') ? _newVarKey.Text : "/:" + _newVarKey.Text;
            FlashBrowser.Send("set_var", key, _newVarValue.Text);
            FlashBrowser.Send("usage", key);
        };
        _autoCall = new CheckBox { Text = "nach Änderung alle aufrufen", AutoSize = true, Padding = new Padding(10, 5, 0, 0) };
        _autoCall.CheckedChanged += (_, _) => { if (!_applying) FlashBrowser.Send("set_autocall", _autoCall.Checked ? "1" : "0"); };
        setRow.Controls.Add(_autoCall);
        varBottom.Controls.Add(setRow);
        _usageTitle = new Label { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 2, 0, 2), Text = "Variable ändern oder \"?\" klicken: zeigt, welche Funktionen sie lesen" };
        _usageFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoScroll = true, WrapContents = false };
        varBottom.Controls.Add(_usageFlow);
        varBottom.Controls.Add(_usageTitle);
        varPage.Controls.Add(_varGrid);
        varPage.Controls.Add(varBottom);
        varPage.Controls.Add(varTop);
        _tabs.TabPages.Add(varPage);

        // ---------------- Funktionen ----------------
        var fnPage = new TabPage("Funktionen") { Padding = new Padding(4) };
        var fnTop = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 4) };
        fnTop.Controls.Add(new Label { Text = "Filter (Pfad/Name)", AutoSize = true, Padding = new Padding(0, 5, 4, 0) });
        _fnFilter = new TextBox { Width = 260 };
        _fnFilter.TextChanged += (_, _) => FilterFns();
        fnTop.Controls.Add(_fnFilter);
        _fnCount = new Label { AutoSize = true, Padding = new Padding(12, 5, 0, 0) };
        fnTop.Controls.Add(_fnCount);
        _fnGrid = Grid(new[] { ("Key", "Funktion (Parameter)", 440), ("Args", "Argumente (Komma getrennt)", 250), ("Call", "Aufrufen", 90) });
        _fnGrid.CellClick += (_, e) =>
        {
            if (_applying || e.RowIndex < 0 || e.ColumnIndex != 2) return;
            // WICHTIG: DebugCall braucht den rohen "/pfad:name"-Schlüssel, nicht den
            // Anzeigetext "name(param1, param2)" – sonst wird die Funktion nicht gefunden.
            string key = _fnGrid.Rows[e.RowIndex].Tag as string ?? Cell(_fnGrid, e.RowIndex, 0);
            FlashBrowser.Send("call_fn", key, Cell(_fnGrid, e.RowIndex, 1));
        };
        fnPage.Controls.Add(_fnGrid);
        fnPage.Controls.Add(fnTop);
        _tabs.TabPages.Add(fnPage);

        // ---------------- FSCommands ----------------
        var logPage = new TabPage("FSCommands") { Padding = new Padding(4) };
        var logTop = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 4) };
        _logFilter = new TextBox { Width = 260 };
        _logFilter.TextChanged += (_, _) => FilterLogs();
        logTop.Controls.Add(new Label { Text = "Befehle des Films an das Spiel (FSCommand / getURL)", AutoSize = true, Padding = new Padding(0, 5, 8, 0) });
        logTop.Controls.Add(_logFilter);
        logTop.Controls.Add(new Button { Text = "Leeren", AutoSize = true, FlatStyle = FlatStyle.System });
        logTop.Controls[logTop.Controls.Count - 1].Click += (_, _) => FlashBrowser.Send("clear_log");
        _logList = new ListBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };
        logPage.Controls.Add(_logList);
        logPage.Controls.Add(logTop);
        _tabs.TabPages.Add(logPage);

        // ---------------- Eingabe ----------------
        var inPage = new TabPage("Eingabe") { Padding = new Padding(12) };
        BuildInputTab(inPage);
        _tabs.TabPages.Add(inPage);

        _status = new TextBox { Dock = DockStyle.Bottom, ReadOnly = true, Height = 26, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control };

        foreach (var g in new[] { _varGrid, _fnGrid, _dVars, _dFns })
        {
            g.CellBeginEdit += (_, _) => { _uiBusy = true; };
            g.CellEndEdit += (_, _) => QueueBusyUpdate();
        }
        Resize += (_, _) => QueueBusyUpdate();

        Controls.Add(_tabs);
        Controls.Add(_status);
        Controls.Add(bar);
    }

    static DataGridView Grid((string Name, string Header, int Width)[] cols)
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AutoGenerateColumns = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
            BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.Fixed3D,
        };
        foreach (var c in cols)
        {
            if (c.Name == "Call" || c.Name == "?" || c.Name == "⧉")
                g.Columns.Add(new DataGridViewButtonColumn { Name = c.Name, HeaderText = c.Header, Width = c.Width, Text = c.Header, UseColumnTextForButtonValue = true });
            else
                g.Columns.Add(new DataGridViewTextBoxColumn { Name = c.Name, HeaderText = c.Header, Width = c.Width, ReadOnly = c.Name == "Key" });
        }
        return g;
    }

    static string Cell(DataGridView g, int row, int col) => g.Rows.Count > row && g.Columns.Count > col ? g.Rows[row].Cells[col].Value?.ToString() ?? "" : "";

    internal void Apply(FlashSnapshot s)
    {
        if (IsDisposed) return;
        _snap = s;
        _applying = true;
        try
        {
            // Player-Auswahl: Einträge wenn möglich in Place ändern, damit das
            // Dropdown nicht mitten in der Interaktion neu aufgebaut wird.
            var labels = s.Players.Select(p => p.Label).ToArray();
            if (_players.Items.Count != labels.Length)
            {
                _players.Items.Clear();
                foreach (var l in labels) _players.Items.Add(l);
            }
            else
            {
                for (int i = 0; i < labels.Length; i++)
                    if (!string.Equals(_players.Items[i]?.ToString(), labels[i], StringComparison.Ordinal))
                        _players.Items[i] = labels[i];
            }
            if (_players.SelectedIndex != s.SelectedPlayer && s.SelectedPlayer >= 0 && s.SelectedPlayer < _players.Items.Count)
                _players.SelectedIndex = s.SelectedPlayer;

            _status.Text = s.Status;
            _focus.Text = "Fokus (_root.Clip): " + (string.IsNullOrEmpty(s.Focus) ? "-" : s.Focus);
            _activeList.Text = "Aktive Liste: " + (string.IsNullOrEmpty(s.ActiveList) ? "-" : s.ActiveList);
            if (_autoCall.Checked != s.AutoCall) _autoCall.Checked = s.AutoCall;

            // Nur den sichtbaren Tab neu befüllen. Die anderen Tabs holen sich ihre
            // Daten beim Umschalten über SelectedIndexChanged (s. BuildUi).
            long now = Environment.TickCount64;
            switch (_tabs.SelectedIndex)
            {
                case 0: UpdateTree(s); UpdateDetail(s.Detail); break;
                // Große Grids nur ~2 Hz – sonst rendert DataGridView bei jedem
                // sich ändernden Spielwert neu und die Oberfläche ruckelt.
                case 1:
                    if (now - _lastVarsUpdate >= 500) { _lastVarsUpdate = now; UpdateVars(s); }
                    break;
                case 2:
                    if (now - _lastFnsUpdate >= 500) { _lastFnsUpdate = now; UpdateFns(s); }
                    break;
                case 3: UpdateLogs(s); break;
            }
        }
        finally { _applying = false; }
    }

    public void FocusMe()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    static string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

    static string VarCode(string key, string value)
    {
        if (double.TryParse(value, NumberStyles.Float, Inv, out _)) return "ui.SetNumber(" + Q(key) + ", " + value + ");";
        if (value == "true" || value == "false") return "ui.SetBool(" + Q(key) + ", " + value + ");";
        return "ui.SetText(" + Q(key) + ", " + Q(value) + ");";
    }

    static void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Clipboard.SetText(text);
    }

    void LoadBank()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Flash-Film (Frontend-Bank) laden",
            Filter = "Frontend-Bank (*.bnk)|*.bnk|Alle Dateien|*.*",
            InitialDirectory = _snap == null || string.IsNullOrEmpty(_snap.BankStartDir) ? Directory.GetCurrentDirectory() : _snap.BankStartDir,
        };
        _modal = true; UpdateBusy();
        try { if (dlg.ShowDialog(this) == DialogResult.OK) FlashBrowser.Send("load_bank", dlg.FileName); }
        finally { _modal = false; UpdateBusy(); }
    }

    void SaveTree()
    {
        var s = _snap;
        if (s == null) return;
        using var dlg = new SaveFileDialog { Title = "Flash-Baum speichern", Filter = "Textdatei (*.txt)|*.txt", FileName = "flash_tree.txt" };
        _modal = true; UpdateBusy();
        bool ok;
        try { ok = dlg.ShowDialog(this) == DialogResult.OK; }
        finally { _modal = false; UpdateBusy(); }
        if (!ok) return;
        using var w = new StreamWriter(dlg.FileName, false, System.Text.Encoding.UTF8);
        foreach (var n in s.Nodes) w.WriteLine(new string(' ', n.Depth * 2) + n.Path + "  ·  " + n.Kind);
        w.WriteLine();
        w.WriteLine("== Variablen");
        foreach (var v in s.Vars) w.WriteLine(v.Key + " = " + v.Value);
        w.WriteLine();
        w.WriteLine("== Funktionen");
        foreach (var f in s.Fns) w.WriteLine(f.CallKey + "(" + f.Params + ")");
    }
}
