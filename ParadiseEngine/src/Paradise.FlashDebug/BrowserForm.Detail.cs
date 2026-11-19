using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Paradise.FlashDebug;

internal sealed partial class BrowserForm
{
    TextBox _dPath = null!, _dFilm = null!, _dDepth = null!, _dX = null!, _dY = null!;
    TextBox _dScreen = null!, _dScaleInfo = null!, _dOffX = null!, _dOffY = null!, _dScX = null!, _dScY = null!;
    TextBox _dTextVar = null!, _dTextKey = null!, _dText = null!;
    CheckBox _dVisible = null!;
    NumericUpDown _dAlpha = null!, _dFrame = null!;
    Button _dPlay = null!;
    Label _dFrameLabel = null!;
    FlowLayoutPanel _dLabels = null!;
    DataGridView _dVars = null!, _dFns = null!;
    ListBox _dCode = null!, _dPatches = null!;
    string _dPathValue = "", _dLabelSig = "", _dVarSig = "", _dFnSig = "", _dCodeSig = "", _dPatchSig = "";

    Control BuildDetailPanel()
    {
        var host = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(8),
        };

        _dPath = new TextBox { ReadOnly = true, Width = 760 };
        var pathRow = Row(L("Pfad", 60), _dPath, B("Kopieren", 80, () => Copy(_dPath.Text)));
        _dFilm = new TextBox { ReadOnly = true, Width = 760 };
        _dDepth = new TextBox { ReadOnly = true, Width = 760 };
        _dVisible = new CheckBox { Text = "_visible", AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
        _dVisible.CheckedChanged += (_, _) =>
        {
            if (_applying || _snap?.Detail == null) return;
            FlashBrowser.Send("set_visible", _snap.Detail.Path, _dVisible.Checked ? "1" : "0");
        };

        _dX = new TextBox { Width = 110 };
        _dY = new TextBox { Width = 110 };
        _dX.Validated += (_, _) => PosChanged();
        _dY.Validated += (_, _) => PosChanged();
        _dAlpha = new NumericUpDown { Minimum = 0, Maximum = 100, DecimalPlaces = 1, Width = 110 };
        _dAlpha.ValueChanged += (_, _) =>
        {
            if (_applying || _snap?.Detail == null) return;
            FlashBrowser.Send("set_alpha", _snap.Detail.Path, (_dAlpha.Value / 100m).ToString(Inv));
        };
        _dScreen = new TextBox { ReadOnly = true, Width = 200 };
        _dScaleInfo = new TextBox { ReadOnly = true, Width = 360 };

        _dOffX = new TextBox { Width = 80 };
        _dOffY = new TextBox { Width = 80 };
        _dOffX.Validated += (_, _) => OffsetChanged();
        _dOffY.Validated += (_, _) => OffsetChanged();
        _dScX = new TextBox { Width = 80 };
        _dScY = new TextBox { Width = 80 };
        _dScX.Validated += (_, _) => ScaleChanged();
        _dScY.Validated += (_, _) => ScaleChanged();

        _dTextVar = new TextBox { ReadOnly = true, Width = 200 };
        _dTextKey = new TextBox { ReadOnly = true, Width = 340 };
        _dText = new TextBox { Multiline = true, Width = 760, Height = 60, ScrollBars = ScrollBars.Vertical };

        _dPlay = B("Play", 80, () =>
        {
            var d = _snap?.Detail; if (d == null) return;
            FlashBrowser.Send("set_play", d.Path, d.Playing ? "0" : "1");
        });
        _dFrame = new NumericUpDown { Minimum = 1, Maximum = 1, Width = 90 };
        _dFrame.ValueChanged += (_, _) =>
        {
            if (_applying || _snap?.Detail == null) return;
            FlashBrowser.Send("goto_frame", _snap.Detail.Path, _dFrame.Value.ToString(Inv), _snap.Detail.Playing ? "1" : "0");
        };
        _dFrameLabel = new Label { AutoSize = true, Padding = new Padding(4, 6, 0, 0) };
        _dLabels = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 940 };

        _dVars = Grid(new[] { ("Key", "Variable", 220), ("Value", "Wert (editierbar)", 280) });
        _dVars.Height = 140;
        _dVars.CellEndEdit += (_, e) =>
        {
            if (_applying || e.RowIndex < 0 || e.ColumnIndex != 1 || _snap?.Detail == null) return;
            string key = Cell(_dVars, e.RowIndex, 0);
            string value = Cell(_dVars, e.RowIndex, 1);
            int sp = value.IndexOf("  (", StringComparison.Ordinal);
            if (sp >= 0 && (value.EndsWith("(Zahl)", StringComparison.Ordinal) || value.EndsWith("(Bool)", StringComparison.Ordinal)))
                value = value.Substring(0, sp);
            FlashBrowser.Send("set_var", _snap.Detail.Path + ":" + key, value);
        };
        _dFns = Grid(new[] { ("Key", "Funktion (Parameter)", 260), ("Args", "Argumente", 200), ("Call", "Aufrufen", 80) });
        _dFns.Height = 140;
        _dFns.CellClick += (_, e) =>
        {
            if (_applying || e.RowIndex < 0 || e.ColumnIndex != 2) return;
            string key = _dFns.Rows[e.RowIndex].Tag as string ?? "";
            if (key.Length == 0) return;
            FlashBrowser.Send("call_fn", key, Cell(_dFns, e.RowIndex, 1));
        };
        _dCode = new ListBox { Width = 940, Height = 150, Font = new Font("Consolas", 9f) };
        _dCode.DoubleClick += (_, _) => Copy(_dCode.SelectedItem?.ToString() ?? "");
        _dPatches = new ListBox { Width = 700, Height = 70, Font = new Font("Consolas", 9f) };

        host.Controls.Add(pathRow);
        host.Controls.Add(Row(L("Film", 60), _dFilm));
        host.Controls.Add(Row(L("Tiefe / Charakter", 110), _dDepth));
        host.Controls.Add(Row(new Control(), _dVisible));
        host.Controls.Add(Row(L("_x / _y", 60), _dX, L("|", 10), _dY, L("_alpha %", 70), _dAlpha, L("Bildschirm", 70), _dScreen));
        host.Controls.Add(Row(L("Skalierung", 60), _dScaleInfo));
        host.Controls.Add(Row(L("Versatz (px)", 80), _dOffX, _dOffY,
            B("←", 32, () => Nudge(0, -1)), B("→", 32, () => Nudge(0, 1)),
            B("↑", 32, () => Nudge(1, -1)), B("↓", 32, () => Nudge(1, 1)),
            L("Shift = 10 px", 90),
            B("Speichern", 90, () => Patch("offset", OffX() + " " + OffY()))));
        host.Controls.Add(Row(L("Skalierung Faktor", 110), _dScX, _dScY,
            B("Speichern", 90, () => Patch("scale", Num(ScX()) + " " + Num(ScY())))));
        host.Controls.Add(Row(L("Sichtbar / Deckkraft", 130),
            B("sichtbar speichern", 130, () => Patch("visible", _snap?.Detail != null && _snap.Detail.Visible ? "1" : "0")),
            B("Deckkraft speichern", 140, () => Patch("alpha", Num(_snap?.Detail.Alpha ?? 1f)))));
        host.Controls.Add(Row(L("Textvariable", 80), _dTextVar));
        host.Controls.Add(Row(L("Schlüssel", 80), _dTextKey));
        host.Controls.Add(Row(L("Text", 80), _dText));
        host.Controls.Add(Row(new Control(), B("Text übernehmen", 130, () =>
        {
            var d = _snap?.Detail; if (d == null) return;
            FlashBrowser.Send("set_text", d.Path, _dText.Text);
        }), B("Text speichern", 120, () => Patch("text", _dText.Text.Replace("\n", "\\n")))));

        host.Controls.Add(Head("Korrekturen (bleiben nach Speichern erhalten)"));
        host.Controls.Add(Row(new Control(), _dPatches, B("Entfernen", 90, RemovePatch)));
        host.Controls.Add(Head("Zeitleiste"));
        host.Controls.Add(Row(new Control(), _dPlay, _dFrame, _dFrameLabel));
        host.Controls.Add(Row(L("Labels (Klick = Play, Shift+Klick = Stop)", 260)));
        host.Controls.Add(Row(new Control(), _dLabels));
        host.Controls.Add(Head("Variablen dieses Clips"));
        host.Controls.Add(Row(new Control(), _dVars));
        host.Controls.Add(Head("Funktionen dieses Clips"));
        host.Controls.Add(Row(new Control(), _dFns));
        host.Controls.Add(Head("Code (ui = VmfPlayer des Films)"));
        host.Controls.Add(Row(new Control(), _dCode, B("Kopieren", 80, () => Copy(string.Join("\r\n", _dCode.Items.Cast<object>())))));
        return host;
    }

    // ------------------------------------------------------------------ Werte übernehmen
    void UpdateDetail(FlashDetailInfo? d)
    {
        if (d == null)
        {
            _dPath.Text = _dFilm.Text = _dDepth.Text = "";
            _dLabels.Controls.Clear(); _dVars.Rows.Clear(); _dFns.Rows.Clear(); _dCode.Items.Clear(); _dPatches.Items.Clear();
            _dPathValue = "";
            return;
        }
        bool changed = _dPathValue != d.Path;
        _dPathValue = d.Path;
        Set(_dPath, d.Path); Set(_dFilm, d.Film);
        Set(_dDepth, "Tiefe " + d.Depth + "  /  #" + d.CharId + (d.ClipDepth > 0 ? "  (Maske bis " + d.ClipDepth + ")" : "") + "  ·  " + d.Type);
        if (_dVisible.Checked != d.Visible) _dVisible.Checked = d.Visible;
        Set(_dX, Num(d.X)); Set(_dY, Num(d.Y));
        if (_dAlpha.Value != (decimal)d.Alpha * 100m) _dAlpha.Value = Math.Max(0, Math.Min(100, (decimal)d.Alpha * 100m));
        Set(_dScreen, d.Screen); Set(_dScaleInfo, d.ScaleInfo);
        if (changed) { Set(_dOffX, Num(d.OffsetX)); Set(_dOffY, Num(d.OffsetY)); Set(_dScX, Num(d.ScaleX)); Set(_dScY, Num(d.ScaleY)); }
        Set(_dTextVar, d.TextVariable.Length == 0 ? "-" : d.TextVariable);
        Set(_dTextKey, d.TextKey.Length == 0 ? "-" : d.TextKey);
        if (!_dText.Focused && _dText.Text != d.Text) _dText.Text = d.Text;

        _dPlay.Text = d.Playing ? "Stop" : "Play";
        int maxFrame = Math.Max(1, d.FrameCount);
        if (_dFrame.Maximum != maxFrame) _dFrame.Maximum = maxFrame;
        // d.Frame kann 0 sein (kein Clip gewählt / Frame noch -1), Minimum ist aber 1.
        int frame = Math.Clamp(d.Frame, (int)_dFrame.Minimum, (int)_dFrame.Maximum);
        if (_dFrame.Value != frame) _dFrame.Value = frame;
        _dFrameLabel.Text = "Label: " + (string.IsNullOrEmpty(d.CurrentLabel) ? "-" : d.CurrentLabel)
                          + "   Frame " + d.Frame + "/" + d.FrameCount + (d.Playing ? "  ▶" : "");

        string ls = string.Join("\n", d.Labels.Select(l => l.Name + "@" + l.Frame));
        if (changed || ls != _dLabelSig)
        {
            _dLabelSig = ls;
            _dLabels.Controls.Clear();
            foreach (var l in d.Labels)
            {
                var b = new Button { Text = l.Name + "  (Frame " + l.Frame + ")", AutoSize = true, FlatStyle = FlatStyle.System };
                string lab = l.Name;
                b.Click += (_, _) => FlashBrowser.Send("goto_label", d.Path, lab, (ModifierKeys & Keys.Shift) != 0 ? "0" : "1");
                _dLabels.Controls.Add(b);
            }
        }
        if (!_dVars.IsCurrentCellInEditMode)
        {
            string vs = string.Join("\n", d.ClipVars.Select(v => v.Key));
            if (changed || vs != _dVarSig)
            {
                _dVarSig = vs; _dVars.Rows.Clear();
                var rows = new DataGridViewRow[d.ClipVars.Count];
                for (int i = 0; i < d.ClipVars.Count; i++)
                {
                    var r = new DataGridViewRow();
                    r.CreateCells(_dVars, d.ClipVars[i].Key, d.ClipVars[i].Value);
                    rows[i] = r;
                }
                _dVars.Rows.AddRange(rows);
            }
            else for (int i = 0; i < d.ClipVars.Count && i < _dVars.Rows.Count; i++)
                if (_dVars.Rows[i].Cells[1].Value?.ToString() != d.ClipVars[i].Value) _dVars.Rows[i].Cells[1].Value = d.ClipVars[i].Value;
        }
        string fs = string.Join("\n", d.ClipFns.Select(f => f.Key));
        if (changed || fs != _dFnSig)
        {
            _dFnSig = fs; _dFns.Rows.Clear();
            var fnRows = new DataGridViewRow[d.ClipFns.Count];
            for (int i = 0; i < d.ClipFns.Count; i++)
            {
                var r = new DataGridViewRow();
                r.CreateCells(_dFns, d.ClipFns[i].Key + "(" + d.ClipFns[i].Params + ")", "", "Aufrufen");
                r.Tag = d.ClipFns[i].CallKey;
                fnRows[i] = r;
            }
            _dFns.Rows.AddRange(fnRows);
        }
        string cs = string.Join("\n", d.CodeLines);
        if (changed || cs != _dCodeSig)
        {
            _dCodeSig = cs; _dCode.Items.Clear();
            foreach (var l in d.CodeLines) _dCode.Items.Add(l);
        }
        string ps = string.Join("\n", d.Patches.Select(p => p.Line));
        if (changed || ps != _dPatchSig)
        {
            _dPatchSig = ps; _dPatches.Items.Clear();
            foreach (var p in d.Patches) _dPatches.Items.Add(p.Line);
        }
    }

    // ------------------------------------------------------------------ Aktionen
    void PosChanged()
    {
        if (_applying || _snap?.Detail == null) return;
        FlashBrowser.Send("set_pos", _snap.Detail.Path, Num(Number(_dX.Text)), Num(Number(_dY.Text)));
    }
    void OffsetChanged()
    {
        if (_applying || _snap?.Detail == null) return;
        FlashBrowser.Send("set_offset", _snap.Detail.Path, Num(Number(_dOffX.Text)), Num(Number(_dOffY.Text)));
    }
    void ScaleChanged()
    {
        if (_applying || _snap?.Detail == null) return;
        FlashBrowser.Send("set_scale", _snap.Detail.Path, Num(Number(_dScX.Text)), Num(Number(_dScY.Text)));
    }
    void Nudge(int axis, int dir)
    {
        var t = axis == 0 ? _dOffX : _dOffY;
        float step = (ModifierKeys & Keys.Shift) != 0 ? 10f : 1f;
        t.Text = Num(Number(t.Text) + dir * step);
        OffsetChanged();
    }
    void Patch(string cmd, string value)
    {
        var d = _snap?.Detail; if (d == null) return;
        FlashBrowser.Send("save_patch", d.Film, d.Path, cmd, value);
    }
    void RemovePatch()
    {
        var d = _snap?.Detail; if (d == null || _dPatches.SelectedItem == null) return;
        var q = VmfPlayerPatch.Parse(_dPatches.SelectedItem.ToString() ?? "");
        FlashBrowser.Send("remove_patch", q.Film, q.Path, q.Command);
        switch (q.Command)
        {
            case "offset": FlashBrowser.Send("set_offset", d.Path, "0", "0"); break;
            case "pos": FlashBrowser.Send("set_pos", d.Path, Num(d.X), Num(d.Y)); break;
            case "scale": FlashBrowser.Send("set_scale", d.Path, "1", "1"); break;
            case "text": FlashBrowser.Send("set_text", d.Path, ""); break;
        }
    }

    static float Number(string s) => float.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : 0f;
    float OffX() => Number(_dOffX.Text);
    float OffY() => Number(_dOffY.Text);
    float ScX() => Number(_dScX.Text);
    float ScY() => Number(_dScY.Text);
    static string Num(float v) => v.ToString("0.###", Inv);
    static void Set(TextBox t, string s) { if (!t.Focused && t.Text != s) t.Text = s; }
    static Label L(string text, int width) => new() { Text = text, AutoSize = false, Width = width, Padding = new Padding(0, 5, 4, 0) };
    static Label Head(string text) => new() { Text = text, AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Padding = new Padding(0, 8, 0, 3) };
    static Button B(string text, int width, Action action)
    {
        var b = new Button { Text = text, Width = width, FlatStyle = FlatStyle.System, Padding = new Padding(2, 0, 2, 0) };
        b.Click += (_, _) => action();
        return b;
    }
    static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Width = 1050 };
        foreach (var c in controls) p.Controls.Add(c);
        return p;
    }
}

/// <summary>Nur zum Zerlegen einer Patch-Zeile im UI (die Engine nutzt VmfPlayer.Patch).</summary>
internal static class VmfPlayerPatch
{
    internal static (string Film, string Path, string Command) Parse(string line)
    {
        var p = (line ?? "").Split('|');
        return (p.Length > 0 ? p[0].Trim() : "", p.Length > 1 ? p[1].Trim() : "", p.Length > 2 ? p[2].Trim() : "");
    }
}
