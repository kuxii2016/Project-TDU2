using Paradise.Core.Diagnostics;
using System.Globalization;

namespace Paradise.ProfilerUI;

/// <summary>
/// Timeline wie Unity: je Thread eine Spur mit verschachtelten Balken (Farbe = Kategorie), oben GC-Pausen.
/// Mausrad = Zoom um den Mauszeiger, Ziehen = verschieben, Shift+Mausrad = Spuren rollen, Doppelklick = ganzes Bild.
/// </summary>
internal sealed class TimelineView : Control
{
    const int RulerH = 20, LaneHeadW = 150, BarH = 16, LaneGap = 6;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    IReadOnlyList<ProfilerFrame> _frames = Array.Empty<ProfilerFrame>();
    IReadOnlyList<ProfilerGcEvent> _gc = Array.Empty<ProfilerGcEvent>();
    int _sel = -1;
    long _frameStart, _frameEnd;
    double _viewStart, _viewEnd;   // Ticks
    int _scrollY;
    Point _mouse = new(-1, -1);
    Point? _drag;
    double _dragViewStart, _dragViewEnd;
    int _dragScrollY;
    readonly List<(RectangleF rect, ProfilerEvent ev, string thread)> _hits = new();
    readonly List<(RectangleF rect, ProfilerGcEvent ev)> _gcHits = new();
    int _contentH;

    public TimelineView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        BackColor = ProfilerTheme.Graph;
    }

    public void SetData(IReadOnlyList<ProfilerFrame> frames, IReadOnlyList<ProfilerGcEvent> gc, int selected)
    {
        bool newFrame = selected != _sel || (selected >= 0 && selected < frames.Count && frames[selected].Start != _frameStart);
        _frames = frames;
        _gc = gc;
        _sel = selected;
        if (newFrame && selected >= 0 && selected < frames.Count)
        {
            var f = frames[selected];
            _frameStart = f.Start; _frameEnd = f.End;
            double pad = (f.End - f.Start) * 0.03;
            _viewStart = f.Start - pad;
            _viewEnd = f.End + pad;
        }
        Invalidate();
    }

    float XOf(double t, int w) => (float)(LaneHeadW + (t - _viewStart) / Math.Max(1, _viewEnd - _viewStart) * (w - LaneHeadW));
    double TOf(int x, int w) => _viewStart + (x - LaneHeadW) / (double)Math.Max(1, w - LaneHeadW) * (_viewEnd - _viewStart);

    protected override void OnPaint(PaintEventArgs pe)
    {
        var g = pe.Graphics;
        g.Clear(ProfilerTheme.Graph);
        _hits.Clear();
        _gcHits.Clear();
        using var tb = new SolidBrush(ProfilerTheme.Text);
        using var db = new SolidBrush(ProfilerTheme.TextDim);
        using var headB = new SolidBrush(ProfilerTheme.Panel);
        using var gridP = new Pen(ProfilerTheme.Grid);
        using var borderP = new Pen(ProfilerTheme.Border);
        int w = Width;

        if (_sel < 0 || _sel >= _frames.Count)
        {
            g.DrawString("No frame selected – click into a graph above.", ProfilerTheme.UiFont, db, 8, 8);
            return;
        }

        // Threads aus dem gewählten Bild und den Nachbarn (Arbeits-Threads liefern ihre Bereiche, wenn sie fertig sind)
        var lanes = new List<(string name, List<ProfilerEvent> events, int maxDepth)>();
        var byName = new Dictionary<string, int>();
        for (int fi = Math.Max(0, _sel - 1); fi <= Math.Min(_frames.Count - 1, _sel + 1); fi++)
        {
            foreach (var t in _frames[fi].Threads)
            {
                if (t.IsMain && fi != _sel) continue;   // Hauptthread: nur das gewählte Bild
                if (!byName.TryGetValue(t.Name, out int li))
                {
                    li = lanes.Count;
                    byName[t.Name] = li;
                    lanes.Add((t.Name, new List<ProfilerEvent>(), 0));
                }
                var lane = lanes[li];
                foreach (var e in t.Events)
                    if (e.End >= _viewStart && e.Start <= _viewEnd)
                    {
                        lane.events.Add(e);
                        if (e.Depth > lane.maxDepth) lane.maxDepth = e.Depth;
                    }
                lanes[li] = lane;
            }
        }
        // Hauptthread zuerst, Rest nach Name
        lanes.Sort((a, b) => a.name == "Main Thread" ? -1 : b.name == "Main Thread" ? 1 : string.CompareOrdinal(a.name, b.name));

        // Frame-Grenzen
        using (var fb = new SolidBrush(Color.FromArgb(30, 255, 255, 255)))
        {
            float x0 = XOf(_frameStart, w), x1 = XOf(_frameEnd, w);
            g.FillRectangle(fb, x0, RulerH, Math.Max(1, x1 - x0), Height - RulerH);
        }

        int y = RulerH + 4 - _scrollY;

        // GC-Spur
        var gcIn = _gc.Where(e => e.End >= _viewStart && e.Start <= _viewEnd).ToList();
        g.DrawString("GC pauses", ProfilerTheme.UiBold, tb, 4, y);
        using (var gcb = new SolidBrush(ProfilerTheme.Gc))
            foreach (var e in gcIn)
            {
                float x0 = XOf(e.Start, w), x1 = Math.Max(x0 + 2, XOf(e.End, w));
                var r = new RectangleF(x0, y + 1, x1 - x0, BarH - 2);
                g.FillRectangle(gcb, r);
                if (r.Width > 40) g.DrawString("Gen" + e.Generation, ProfilerTheme.Small, tb, r.X + 2, r.Y + 1);
                _gcHits.Add((r, e));
            }
        y += BarH + LaneGap;
        g.DrawLine(borderP, 0, y - LaneGap / 2, w, y - LaneGap / 2);

        using var nf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        foreach (var (name, events, maxDepth) in lanes)
        {
            int laneH = (maxDepth + 1) * BarH;
            g.DrawString(name, ProfilerTheme.UiBold, tb, new RectangleF(4, y, LaneHeadW - 8, BarH), nf);
            if (events.Count == 0) g.DrawString("(idle)", ProfilerTheme.Small, db, 4, y + BarH - 2);
            foreach (var e in events)
            {
                float x0 = XOf(e.Start, w), x1 = XOf(e.End, w);
                if (x1 < LaneHeadW || x0 > w) continue;
                x0 = Math.Max(x0, LaneHeadW);
                float bw = Math.Max(1, x1 - x0);
                var r = new RectangleF(x0, y + e.Depth * BarH, bw, BarH - 1);
                var m = ProfilerMarker.ById(e.Marker);
                var col = ProfilerTheme.Category(m?.Category ?? ProfilerCategory.Other);
                using (var b = new SolidBrush(ProfilerTheme.Darker(col, 0.8f))) g.FillRectangle(b, r);
                if (bw > 3) using (var p = new Pen(ProfilerTheme.Darker(col, 0.5f))) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                if (bw > 30)
                {
                    string label = (m?.Name ?? "?") + " (" + Profiler.TicksToMs(e.Ticks).ToString("0.00", Inv) + "ms)";
                    g.DrawString(label, ProfilerTheme.Small, Brushes.Black, new RectangleF(r.X + 2, r.Y + 1, r.Width - 3, r.Height), nf);
                }
                _hits.Add((r, e, name));
            }
            y += Math.Max(laneH, BarH * 2) + LaneGap;
            g.DrawLine(borderP, 0, y - LaneGap / 2, w, y - LaneGap / 2);
        }
        _contentH = y + _scrollY;

        // Spurköpfe-Hintergrund links (über die Balken gezeichnet, die bis 0 laufen würden)
        g.DrawLine(borderP, LaneHeadW - 1, RulerH, LaneHeadW - 1, Height);

        // Lineal
        g.FillRectangle(headB, 0, 0, w, RulerH);
        double spanMs = Profiler.TicksToMs((long)(_viewEnd - _viewStart));
        double step = NiceStep(spanMs / Math.Max(1, (w - LaneHeadW) / 90.0));
        double startMs = Profiler.TicksToMs((long)(_viewStart - _frameStart));
        double first = Math.Ceiling(startMs / step) * step;
        for (double ms = first; ms <= startMs + spanMs; ms += step)
        {
            double t = _frameStart + ms / 1000.0 * System.Diagnostics.Stopwatch.Frequency;
            float x = XOf(t, w);
            g.DrawLine(gridP, x, RulerH - 6, x, RulerH);
            g.DrawString(ms.ToString(step < 0.1 ? "0.00" : step < 1 ? "0.0" : "0", Inv) + "ms", ProfilerTheme.Small, tb, x + 2, 3);
        }
        var f = _frames[_sel];
        string info = "Frame " + f.Index.ToString(Inv) + "   CPU " + f.Ms.ToString("0.00", Inv) + " ms" +
                      (f.Gpu.Length > 0 ? "   GPU " + f.GpuMs.ToString("0.00", Inv) + " ms" : "");
        g.DrawString(info, ProfilerTheme.UiBold, tb, 4, 3);

        DrawTooltip(g);
    }

    void DrawTooltip(Graphics g)
    {
        if (_mouse.X < 0 || _drag != null) return;
        string? text = null;
        foreach (var (r, e) in _gcHits)
            if (r.Contains(_mouse))
                text = "GC Gen" + e.Generation + " #" + e.Index + "  (" + e.Reason + ")\nPause: " + e.Ms.ToString("0.000", Inv) + " ms";
        if (text == null)
            for (int i = _hits.Count - 1; i >= 0; i--)
            {
                var (r, e, thread) = _hits[i];
                if (!r.Contains(_mouse)) continue;
                var m = ProfilerMarker.ById(e.Marker);
                text = (m?.Name ?? "?") + "\n" +
                       "Thread: " + thread + "   Category: " + ProfilerCategories.Name(m?.Category ?? ProfilerCategory.Other) + "\n" +
                       "Total: " + Profiler.TicksToMs(e.Ticks).ToString("0.000", Inv) + " ms   Self: " + Profiler.TicksToMs(e.SelfTicks).ToString("0.000", Inv) + " ms\n" +
                       "Start: +" + Profiler.TicksToMs(e.Start - _frameStart).ToString("0.000", Inv) + " ms   GC Alloc: " + ProfilerCounter.FormatBytes(e.Alloc);
                break;
            }
        if (text == null) return;
        var sz = g.MeasureString(text, ProfilerTheme.UiFont);
        float x = Math.Min(_mouse.X + 14, Width - sz.Width - 6), y = Math.Min(_mouse.Y + 14, Height - sz.Height - 6);
        using var bb = new SolidBrush(Color.FromArgb(240, 25, 25, 25));
        using var bp = new Pen(ProfilerTheme.Grid);
        g.FillRectangle(bb, x - 4, y - 3, sz.Width + 8, sz.Height + 6);
        g.DrawRectangle(bp, x - 4, y - 3, sz.Width + 8, sz.Height + 6);
        using var tb = new SolidBrush(ProfilerTheme.Text);
        g.DrawString(text, ProfilerTheme.UiFont, tb, x, y);
    }

    static double NiceStep(double raw)
    {
        double[] steps = { 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000 };
        foreach (var s in steps) if (raw <= s) return s;
        return 1000;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((ModifierKeys & Keys.Shift) != 0)
        {
            _scrollY = Math.Clamp(_scrollY - Math.Sign(e.Delta) * BarH * 3, 0, Math.Max(0, _contentH - Height + 20));
            Invalidate();
            return;
        }
        double anchor = TOf(Math.Max(LaneHeadW, e.X), Width);
        double k = e.Delta > 0 ? 0.8 : 1.25;
        double span = Math.Max(Math.Max(1, (_viewEnd - _viewStart) * k), System.Diagnostics.Stopwatch.Frequency / 1_000_000.0);   // min. 1 µs
        double rel = (anchor - _viewStart) / Math.Max(1, _viewEnd - _viewStart);
        _viewStart = anchor - rel * span;
        _viewEnd = _viewStart + span;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        _drag = e.Location;
        _dragViewStart = _viewStart; _dragViewEnd = _viewEnd; _dragScrollY = _scrollY;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _drag = null;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.Location;
        if (_drag is Point d && e.Button != MouseButtons.None)
        {
            double perPx = (_dragViewEnd - _dragViewStart) / Math.Max(1, Width - LaneHeadW);
            double shift = (e.X - d.X) * perPx;
            _viewStart = _dragViewStart - shift;
            _viewEnd = _dragViewEnd - shift;
            _scrollY = Math.Clamp(_dragScrollY - (e.Y - d.Y), 0, Math.Max(0, _contentH - Height + 20));
        }
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = new Point(-1, -1);
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        double pad = (_frameEnd - _frameStart) * 0.03;
        _viewStart = _frameStart - pad;
        _viewEnd = _frameEnd + pad;
        _scrollY = 0;
        Invalidate();
    }
}
