using Paradise.Core.Diagnostics;
using System.Globalization;

namespace Paradise.ProfilerUI;

/// <summary>
/// Hauptfenster wie der Unity-Profiler: oben Toolbar (Record, Clear, Bild vor/zurück, Current, Export),
/// darunter die Module (CPU, GPU, Rendering, Memory, Physics) als Graphen, unten Hierarchy oder Timeline des gewählten Bildes.
/// </summary>
internal sealed class ProfilerForm : Form
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly List<ProfilerFrame> _frames = new();
    readonly List<ProfilerGcEvent> _gc = new();
    readonly HashSet<int> _gcFrames = new();
    long _selIndex = -1;          // ProfilerFrame.Index des gewählten Bildes
    bool _follow = true;          // immer das neueste Bild ("Current")
    long _shownFrame = -2;        // Bild, für das Hierarchy/Timeline zuletzt gebaut wurden
    string _shownThread = "";
    bool _shownAll;
    DateTime _nextAllRebuild;

    readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    readonly ToolStripButton _record, _clear, _prev, _next, _current, _export, _openFolder;
    readonly ToolStripLabel _frameLabel, _status;
    readonly List<ChartView> _charts = new();
    readonly ChartView _cpu, _gpu, _rendering, _memory, _physics, _flash;
    readonly ComboBox _view, _thread;
    readonly CheckBox _allFrames;
    readonly TextBox _search;
    readonly Label _summary;
    readonly HierarchyView _hierarchy = new() { Dock = DockStyle.Fill };
    readonly TimelineView _timeline = new() { Dock = DockStyle.Fill, Visible = false };
    readonly SplitContainer _split;
    string? _lastExport;

    public ProfilerForm()
    {
        Text = "Paradise Profiler";
        Width = 1400;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = ProfilerTheme.Back;
        ForeColor = ProfilerTheme.Text;
        Font = ProfilerTheme.UiFont;
        KeyPreview = true;

        // ---------------------------------------------------------------- Toolbar
        var bar = new ToolStrip { RenderMode = ToolStripRenderMode.System, BackColor = ProfilerTheme.PanelDark, ForeColor = ProfilerTheme.Text, GripStyle = ToolStripGripStyle.Hidden };
        _record = new ToolStripButton("● Record") { CheckOnClick = true, Checked = true, ToolTipText = "Aufnahme an/aus" };
        _clear = new ToolStripButton("Clear") { ToolTipText = "Alle Bilder verwerfen" };
        _prev = new ToolStripButton("◀") { ToolTipText = "Vorheriges Bild" };
        _next = new ToolStripButton("▶") { ToolTipText = "Nächstes Bild" };
        _current = new ToolStripButton("Current") { ToolTipText = "Immer das neueste Bild zeigen" };
        _frameLabel = new ToolStripLabel("Frame: –");
        _export = new ToolStripButton("Export Report (.md)") { ToolTipText = "Bericht aller Bilder im Puffer nach Dokumente\\KuxiiSoft\\Test Drive Unlimited 2\\Logs\\Profiler" };
        _openFolder = new ToolStripButton("Open Folder") { ToolTipText = "Bericht-Ordner im Explorer öffnen" };
        _status = new ToolStripLabel("") { Alignment = ToolStripItemAlignment.Right, ForeColor = ProfilerTheme.TextDim };
        bar.Items.AddRange(new ToolStripItem[] { _record, _clear, new ToolStripSeparator(), _prev, _next, _current, _frameLabel, new ToolStripSeparator(), _export, _openFolder, _status });
        foreach (ToolStripItem it in bar.Items) it.ForeColor = ProfilerTheme.Text;
        _status.ForeColor = ProfilerTheme.TextDim;

        _record.CheckedChanged += (_, _) => { Profiler.Enabled = _record.Checked; UpdateRecordButton(); };
        _clear.Click += (_, _) => { Profiler.Clear(); _selIndex = -1; _follow = true; _shownFrame = -2; RefreshData(); };
        _prev.Click += (_, _) => Step(-1);
        _next.Click += (_, _) => Step(1);
        _current.Click += (_, _) => { _follow = true; RefreshData(); };
        _export.Click += (_, _) => Export();
        _openFolder.Click += (_, _) => OpenFolder();

        // ---------------------------------------------------------------- Module (Graphen)
        _cpu = new ChartView { Title = "CPU Usage", Stacked = true, MsGuides = true, Height = 18 + ProfilerCategories.Count * 14 + 6 };
        for (int c = 0; c < ProfilerCategories.Count; c++)
        {
            int ci = c;
            _cpu.Series.Add(new ChartSeries
            {
                Name = ProfilerCategories.Name((ProfilerCategory)c),
                Color = ProfilerTheme.Category((ProfilerCategory)c),
                Value = f => f.CategoryMs[ci],
                Format = v => v.ToString("0.00", Inv) + "ms",
            });
        }
        _gpu = new ChartView { Title = "GPU Usage", Stacked = true, MsGuides = true, Height = 100 };
        _rendering = CounterChart("Rendering", ProfilerCounters.Rendering);
        _memory = CounterChart("Memory", ProfilerCounters.Memory);
        foreach (var s in _memory.Series)
            if (s.Name.StartsWith("GC Gen", StringComparison.Ordinal) || s.Name == ProfilerCounters.Materials.Name) s.Visible = false;
        _physics = CounterChart("Physics", ProfilerCounters.Physics);
        _physics.Series.Insert(0, new ChartSeries
        {
            Name = "Physics CPU (ms)",
            Color = ProfilerTheme.Category(ProfilerCategory.Physics),
            Value = f => f.CategoryMs[(int)ProfilerCategory.Physics],
            Format = v => v.ToString("0.00", Inv) + "ms",
        });
        _physics.Height = Math.Max(_physics.Height, 18 + _physics.Series.Count * 14 + 8);
        _flash = CounterChart("Flash (UI)", ProfilerCounters.Flash);
        foreach (var s in _flash.Series)
            if (s.Name is "Movies loaded" or "Flash draw calls") s.Visible = false;   // per Klick auf die Legende einblendbar
        _charts.AddRange(new[] { _cpu, _gpu, _rendering, _memory, _physics, _flash });

        var chartPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = ProfilerTheme.Graph };
        for (int i = _charts.Count - 1; i >= 0; i--)   // Dock=Top: zuletzt hinzugefügt liegt oben
        {
            var ch = _charts[i];
            ch.FrameClicked += idx => SelectListIndex(idx);
            chartPanel.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 2, BackColor = ProfilerTheme.Border });
            chartPanel.Controls.Add(ch);
        }

        // ---------------------------------------------------------------- unten: Hierarchy / Timeline
        var detailBar = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = ProfilerTheme.PanelDark };
        _view = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 4, Top = 3, Width = 110, BackColor = ProfilerTheme.Panel, ForeColor = ProfilerTheme.Text, FlatStyle = FlatStyle.Flat };
        _view.Items.AddRange(new object[] { "Hierarchy", "Timeline" });
        _view.SelectedIndex = 0;
        _thread = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 120, Top = 3, Width = 200, BackColor = ProfilerTheme.Panel, ForeColor = ProfilerTheme.Text, FlatStyle = FlatStyle.Flat };
        _thread.Items.Add("Main Thread");
        _thread.SelectedIndex = 0;
        _allFrames = new CheckBox { Text = "Ø all frames", Left = 330, Top = 5, Width = 100, ForeColor = ProfilerTheme.Text };
        _search = new TextBox { Left = 440, Top = 3, Width = 200, BackColor = ProfilerTheme.Panel, ForeColor = ProfilerTheme.Text, BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "Search marker…" };
        var expand = new Button { Text = "+", Left = 646, Top = 2, Width = 24, Height = 23, FlatStyle = FlatStyle.Flat, ForeColor = ProfilerTheme.Text };
        var collapse = new Button { Text = "–", Left = 672, Top = 2, Width = 24, Height = 23, FlatStyle = FlatStyle.Flat, ForeColor = ProfilerTheme.Text };
        _summary = new Label { Left = 706, Top = 7, AutoSize = true, ForeColor = ProfilerTheme.Text };
        detailBar.Controls.AddRange(new Control[] { _view, _thread, _allFrames, _search, expand, collapse, _summary });

        _view.SelectedIndexChanged += (_, _) =>
        {
            bool tl = _view.SelectedIndex == 1;
            _timeline.Visible = tl;
            _hierarchy.Visible = !tl;
            _thread.Enabled = _allFrames.Enabled = _search.Enabled = expand.Enabled = collapse.Enabled = !tl;
            _shownFrame = -2;
            RefreshData();
        };
        _thread.SelectedIndexChanged += (_, _) => { _shownFrame = -2; RefreshData(); };
        _allFrames.CheckedChanged += (_, _) => { _shownFrame = -2; RefreshData(); };
        _search.TextChanged += (_, _) => _hierarchy.SetFilter(_search.Text);
        expand.Click += (_, _) => _hierarchy.ExpandAll(true);
        collapse.Click += (_, _) => _hierarchy.ExpandAll(false);

        var detail = new Panel { Dock = DockStyle.Fill, BackColor = ProfilerTheme.Panel };
        detail.Controls.Add(_hierarchy);
        detail.Controls.Add(_timeline);
        detail.Controls.Add(detailBar);

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            BackColor = ProfilerTheme.Border,
            SplitterWidth = 5,
        };
        _split.Panel1.Controls.Add(chartPanel);
        _split.Panel2.Controls.Add(detail);

        Controls.Add(_split);
        Controls.Add(bar);

        Load += (_, _) =>
        {
            try { _split.SplitterDistance = (int)(ClientSize.Height * 0.5); } catch (Exception) { }
            Profiler.Enabled = true;
            UpdateRecordButton();
            if (!Profiler.Available)
                _status.Text = "PARADISE_PROFILER not set in this build – nothing is recorded (Debug build or -p:Profiler=true).";
        };
        FormClosed += (_, _) => { _timer.Stop(); Profiler.Enabled = false; };
        _timer.Tick += (_, _) => RefreshData();
        _timer.Start();
    }

    ChartView CounterChart(string title, string group)
    {
        var ch = new ChartView { Title = title };
        int i = 0;
        foreach (var c in ProfilerCounter.All.Where(x => x.Group == group))
        {
            var counter = c;
            ch.Series.Add(new ChartSeries
            {
                Name = c.Name,
                Color = ProfilerTheme.Series(i++),
                Value = f => f.Counter(counter),
                Format = counter.Format,
            });
        }
        ch.Height = Math.Max(90, 18 + ch.Series.Count * 14 + 8);
        return ch;
    }

    public void FocusMe()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    void UpdateRecordButton()
    {
        _record.Text = _record.Checked ? "● Recording" : "○ Record";
        _record.ForeColor = _record.Checked ? Color.FromArgb(240, 90, 90) : ProfilerTheme.Text;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Bild vor/zurück wie Unity (nicht, wenn die Hierarchy oder ein Eingabefeld die Pfeile braucht)
        if (!_hierarchy.Focused && !_search.Focused && !_thread.Focused && !_view.Focused)
        {
            if (keyData == Keys.Left) { Step(-1); return true; }
            if (keyData == Keys.Right) { Step(1); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------------------------------------------------------------------------------------------------- Auswahl

    int SelectedListIndex()
    {
        if (_frames.Count == 0) return -1;
        if (_follow || _selIndex < 0) return _frames.Count - 1;
        // Bilder sind aufsteigend nach Index
        int lo = 0, hi = _frames.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            long v = _frames[mid].Index;
            if (v == _selIndex) return mid;
            if (v < _selIndex) lo = mid + 1; else hi = mid - 1;
        }
        return Math.Clamp(lo, 0, _frames.Count - 1);   // Bild schon aus dem Puffer gefallen -> nächstes
    }

    void SelectListIndex(int i)
    {
        if (i < 0 || i >= _frames.Count) return;
        _selIndex = _frames[i].Index;
        _follow = false;
        if (_record.Checked) _record.Checked = false;   // wie Unity: Klick in den Graphen pausiert die Aufnahme
        RefreshData();
    }

    void Step(int d)
    {
        if (_frames.Count == 0) return;
        int i = SelectedListIndex();
        SelectListIndex(Math.Clamp(i + d, 0, _frames.Count - 1));
    }

    // ---------------------------------------------------------------------------------------------------- Aktualisieren

    void RefreshData()
    {
        if (IsDisposed) return;
        Profiler.GetFrames(_frames);
        Profiler.GetGcEvents(_gc);
        int sel = SelectedListIndex();
        var selFrame = sel >= 0 ? _frames[sel] : null;

        // GC-Pausen den Bildern zuordnen (Markierung in den Graphen)
        _gcFrames.Clear();
        foreach (var e in _gc)
        {
            int k = FindFrame(e.Start);
            if (k >= 0) _gcFrames.Add(k);
        }

        UpdateGpuSeries();
        foreach (var ch in _charts)
        {
            ch.Frames = _frames;
            ch.Capacity = Profiler.MaxFrames;
            ch.Selected = sel;
            ch.Marks = _gcFrames;
            ch.Invalidate();
        }

        _frameLabel.Text = selFrame == null ? "Frame: –" : "Frame: " + selFrame.Index.ToString(Inv) + "  (" + (sel + 1).ToString(Inv) + " / " + _frames.Count.ToString(Inv) + ")";
        _current.Checked = _follow;

        if (selFrame == null)
        {
            _summary.Text = Profiler.Available ? (Profiler.Enabled ? "Waiting for frames…" : "Recording paused.") : "";
            _hierarchy.SetData(null, 1);
            _timeline.SetData(_frames, _gc, -1);
            return;
        }

        _summary.Text = "CPU: " + selFrame.Ms.ToString("0.00", Inv) + "ms (" + (1000.0 / Math.Max(0.001, selFrame.Ms)).ToString("0", Inv) + " FPS)" +
                        (selFrame.Gpu.Length > 0 ? "   GPU: " + selFrame.GpuMs.ToString("0.00", Inv) + "ms" : "") +
                        "   GC Alloc: " + ProfilerCounter.FormatBytes(selFrame.Counter(ProfilerCounters.GcAllocFrame)) +
                        (_gcFrames.Contains(sel) ? "   [GC]" : "");

        if (_timeline.Visible)
        {
            _timeline.SetData(_frames, _gc, sel);
            return;
        }

        UpdateThreadList(selFrame);
        string thread = _thread.SelectedItem as string ?? "Main Thread";
        bool all = _allFrames.Checked;
        bool rebuild = selFrame.Index != _shownFrame || thread != _shownThread || all != _shownAll;
        if (all && !rebuild && Profiler.Enabled && DateTime.Now >= _nextAllRebuild) rebuild = true;
        if (!rebuild) return;
        _shownFrame = selFrame.Index; _shownThread = thread; _shownAll = all;

        if (all)
        {
            _nextAllRebuild = DateTime.Now.AddSeconds(1);
            var sets = new List<ProfilerEvent[]>();
            long ticks = 0;
            foreach (var f in _frames)
            {
                ticks += f.End - f.Start;
                foreach (var t in f.Threads) if (ThreadName(t) == thread) sets.Add(t.Events);
            }
            _hierarchy.SetData(HierarchyModel.Build(sets, _frames.Count), ticks);
        }
        else
        {
            var t = selFrame.Threads.FirstOrDefault(x => ThreadName(x) == thread);
            _hierarchy.SetData(HierarchyModel.Build(t != null ? new[] { t.Events } : Array.Empty<ProfilerEvent[]>(), 1), selFrame.End - selFrame.Start);
        }
    }

    static string ThreadName(ProfilerThreadData t) => t.IsMain ? "Main Thread" : t.Name;

    void UpdateThreadList(ProfilerFrame f)
    {
        // Alle Threads aus den letzten Bildern anbieten (Arbeits-Threads messen nicht in jedem Bild)
        var names = new SortedSet<string>(StringComparer.Ordinal);
        for (int i = Math.Max(0, _frames.Count - 120); i < _frames.Count; i++)
            foreach (var t in _frames[i].Threads) if (!t.IsMain) names.Add(t.Name);
        foreach (var t in f.Threads) if (!t.IsMain) names.Add(t.Name);
        var want = new List<string> { "Main Thread" };
        want.AddRange(names);
        var have = _thread.Items.Cast<string>().ToList();
        if (want.SequenceEqual(have)) return;
        string cur = _thread.SelectedItem as string ?? "Main Thread";
        _thread.BeginUpdate();
        _thread.Items.Clear();
        foreach (var n in want) _thread.Items.Add(n);
        _thread.SelectedIndex = Math.Max(0, want.IndexOf(cur));
        _thread.EndUpdate();
    }

    int FindFrame(long ts)
    {
        int lo = 0, hi = _frames.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var f = _frames[mid];
            if (ts < f.Start) hi = mid - 1;
            else if (ts > f.End) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    /// <summary>GPU-Graph: eine Reihe je gemeldetem Durchgang (ohne "GPU Total").</summary>
    void UpdateGpuSeries()
    {
        var ids = new SortedSet<int>();
        for (int i = Math.Max(0, _frames.Count - 30); i < _frames.Count; i++)
            foreach (var g in _frames[i].Gpu)
                if (ProfilerMarker.ById(g.Marker)?.Name != Profiler.GpuTotalName) ids.Add(g.Marker);
        if (ids.Count == _gpu.Series.Count) return;
        var keep = _gpu.Series.ToDictionary(s => s.Name, s => s.Visible);
        _gpu.Series.Clear();
        int k = 0;
        foreach (int id in ids)
        {
            int mid = id;
            string name = ProfilerMarker.ById(id)?.Name ?? "#" + id;
            _gpu.Series.Add(new ChartSeries
            {
                Name = name,
                Color = ProfilerTheme.Series(k++),
                Visible = !keep.TryGetValue(name, out bool v) || v,
                Value = f => { foreach (var g in f.Gpu) if (g.Marker == mid) return g.Ms; return 0; },
                Format = v2 => v2.ToString("0.00", Inv) + "ms",
            });
        }
        _gpu.Height = Math.Max(100, 18 + _gpu.Series.Count * 14 + 8);
    }

    // ---------------------------------------------------------------------------------------------------- Export

    void Export()
    {
        try
        {
            var frames = new List<ProfilerFrame>();
            Profiler.GetFrames(frames);
            if (frames.Count == 0) { _status.Text = "Nothing to export – no frames recorded."; return; }
            var gc = new List<ProfilerGcEvent>();
            Profiler.GetGcEvents(gc);
            _lastExport = ReportExporter.Write(frames, gc);
            _status.Text = "Report saved: " + _lastExport;
            OpenFolder();
        }
        catch (Exception ex)
        {
            _status.Text = "Export failed: " + ex.Message;
            Log.Warn("Profiler export: " + ex);
        }
    }

    void OpenFolder()
    {
        try
        {
            if (_lastExport != null && File.Exists(_lastExport))
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + _lastExport + "\"");
            else
            {
                string dir = Path.Combine(Paradise.Core.Config.UserPaths.Logs, "Profiler");
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
        }
        catch (Exception ex) { _status.Text = "Explorer: " + ex.Message; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
