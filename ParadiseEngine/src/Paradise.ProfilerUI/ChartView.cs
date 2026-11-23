using Paradise.Core.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace Paradise.ProfilerUI;

/// <summary>Eine Datenreihe eines Moduls (z. B. "Rendering" im CPU-Graphen oder "Draw Calls").</summary>
internal sealed class ChartSeries
{
    public string Name = "";
    public Color Color;
    public Func<ProfilerFrame, double> Value = _ => 0;
    public Func<double, string> Format = v => v.ToString("0.##", CultureInfo.InvariantCulture);
    public bool Visible = true;
}

/// <summary>
/// Ein Profiler-Modul wie in Unity: links Legende mit Werten des gewählten Bildes, rechts der Verlauf.
/// Gestapelt (CPU/GPU in ms) oder Linien (jede Reihe auf ihr eigenes Maximum skaliert, wie Unity Rendering/Memory).
/// Klick/Ziehen im Graphen wählt ein Bild.
/// </summary>
internal sealed class ChartView : Control
{
    public const int LegendWidth = 210;
    const int TitleH = 18, RowH = 14;

    public string Title = "";
    public bool Stacked;
    /// <summary>Hilfslinien 16.7 ms (60 FPS) / 33.3 ms (30 FPS) im gestapelten ms-Graphen.</summary>
    public bool MsGuides;
    public readonly List<ChartSeries> Series = new();
    public IReadOnlyList<ProfilerFrame> Frames = Array.Empty<ProfilerFrame>();
    public int Capacity = 600;
    public int Selected = -1;
    /// <summary>Zusätzliche Markierungen (z. B. GC-Pausen) als Bildindizes.</summary>
    public IReadOnlyCollection<int>? Marks;

    public event Action<int>? FrameClicked;

    public ChartView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = ProfilerTheme.Graph;
        Height = 120;
        Dock = DockStyle.Top;
    }

    Rectangle GraphRect => new(LegendWidth, 1, Math.Max(10, Width - LegendWidth - 1), Math.Max(10, Height - 3));

    float Dx(Rectangle g) => g.Width / (float)Math.Max(Math.Max(Frames.Count, Capacity), 2);
    float X(Rectangle g, int i) => g.Right - (Frames.Count - i) * Dx(g);

    protected override void OnPaint(PaintEventArgs pe)
    {
        var gr = pe.Graphics;
        gr.Clear(ProfilerTheme.Graph);
        var g = GraphRect;

        // Legende
        using (var lb = new SolidBrush(ProfilerTheme.Panel)) gr.FillRectangle(lb, 0, 0, LegendWidth - 1, Height);
        using (var tb = new SolidBrush(ProfilerTheme.Text))
        using (var db = new SolidBrush(ProfilerTheme.TextDim))
        {
            gr.DrawString(Title, ProfilerTheme.UiBold, tb, 4, 2);
            var sel = Selected >= 0 && Selected < Frames.Count ? Frames[Selected] : null;
            int y = TitleH + 2;
            foreach (var s in Series)
            {
                if (y + RowH > Height) break;
                using (var cb = new SolidBrush(s.Visible ? s.Color : ProfilerTheme.Grid)) gr.FillRectangle(cb, 8, y + 3, 8, 8);
                gr.DrawString(s.Name, ProfilerTheme.Small, s.Visible ? tb : db, 20, y);
                if (sel != null)
                {
                    string v = s.Format(s.Value(sel));
                    var sz = gr.MeasureString(v, ProfilerTheme.Small);
                    gr.DrawString(v, ProfilerTheme.Small, s.Visible ? tb : db, LegendWidth - 6 - sz.Width, y);
                }
                y += RowH;
            }
        }
        using (var bp = new Pen(ProfilerTheme.Border)) gr.DrawLine(bp, LegendWidth - 1, 0, LegendWidth - 1, Height);

        if (Frames.Count == 0) return;
        gr.SmoothingMode = SmoothingMode.None;
        if (Stacked) DrawStacked(gr, g); else DrawLines(gr, g);

        // GC-Markierungen
        if (Marks != null)
            using (var gp = new Pen(Color.FromArgb(160, ProfilerTheme.Gc)))
                foreach (int i in Marks)
                    if (i >= 0 && i < Frames.Count) { float x = X(g, i) + Dx(g) / 2; gr.DrawLine(gp, x, g.Bottom - 6, x, g.Bottom); }

        // gewähltes Bild
        if (Selected >= 0 && Selected < Frames.Count)
        {
            float x = X(g, Selected) + Dx(g) / 2;
            using var cp = new Pen(ProfilerTheme.Cursor);
            gr.DrawLine(cp, x, g.Top, x, g.Bottom);
        }
    }

    void DrawStacked(Graphics gr, Rectangle g)
    {
        int n = Frames.Count;
        var vis = Series.Where(s => s.Visible).ToList();
        var sums = new double[n];
        double max = 0;
        for (int i = 0; i < n; i++)
        {
            double t = 0;
            foreach (var s in vis) t += Math.Max(0, s.Value(Frames[i]));
            sums[i] = t;
            if (t > max) max = t;
        }
        // Skala: nicht von einem einzelnen Riesen-Ruckler plattdrücken lassen -> 99%-Wert, Ausreißer laufen oben aus dem Bild
        var sortedSums = (double[])sums.Clone();
        Array.Sort(sortedSums);
        double p99 = sortedSums[Math.Min(n - 1, (int)(n * 0.99))];
        double scale = NiceMax(Math.Max(MsGuides ? 16.7 : 0.001, p99 * 1.15));

        var bottom = new double[n];
        float dx = Dx(g);
        foreach (var s in vis)
        {
            var pts = new PointF[n * 2 + 2];
            for (int i = 0; i < n; i++)
            {
                double v = Math.Max(0, s.Value(Frames[i]));
                float x = X(g, i);
                pts[i] = new PointF(x + dx / 2, Y(g, bottom[i] + v, scale));
                bottom[i] += v;
            }
            pts[n] = new PointF(X(g, n - 1) + dx, pts[n - 1].Y);
            // Untere Kante zurück (mit dem alten bottom = bottom - v)
            for (int i = n - 1; i >= 0; i--)
            {
                double v = Math.Max(0, s.Value(Frames[i]));
                pts[n + 1 + (n - 1 - i)] = new PointF(X(g, i) + dx / 2, Y(g, bottom[i] - v, scale));
            }
            pts[n * 2 + 1] = new PointF(X(g, 0), pts[n * 2].Y);
            using var b = new SolidBrush(s.Color);
            gr.FillPolygon(b, pts);
        }

        using var gp = new Pen(ProfilerTheme.Grid) { DashStyle = DashStyle.Dot };
        using var tb = new SolidBrush(ProfilerTheme.Text);
        using var bg = new SolidBrush(Color.FromArgb(200, 20, 20, 20));
        void Guide(double ms, string label)
        {
            if (ms > scale) return;
            float y = Y(g, ms, scale);
            gr.DrawLine(gp, g.Left, y, g.Right, y);
            var sz = gr.MeasureString(label, ProfilerTheme.Small);
            gr.FillRectangle(bg, g.Left + 2, y - sz.Height, sz.Width, sz.Height);
            gr.DrawString(label, ProfilerTheme.Small, tb, g.Left + 2, y - sz.Height);
        }
        if (MsGuides)
        {
            Guide(1000.0 / 60, "16.7ms (60FPS)");
            Guide(1000.0 / 30, "33.3ms (30FPS)");
            Guide(1000.0 / 15, "66.7ms (15FPS)");
        }
        else Guide(scale / 2, (scale / 2).ToString("0.##", CultureInfo.InvariantCulture) + " ms");
        string top = scale.ToString("0.##", CultureInfo.InvariantCulture) + " ms";
        gr.DrawString(top, ProfilerTheme.Small, tb, g.Left + 2, g.Top);
    }

    void DrawLines(Graphics gr, Rectangle g)
    {
        int n = Frames.Count;
        float dx = Dx(g);
        gr.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var s in Series)
        {
            if (!s.Visible) continue;
            double max = 0;
            for (int i = 0; i < n; i++) max = Math.Max(max, s.Value(Frames[i]));
            double scale = max <= 0 ? 1 : max * 1.1;
            var pts = new PointF[n];
            for (int i = 0; i < n; i++)
                pts[i] = new PointF(X(g, i) + dx / 2, Y(g, Math.Max(0, s.Value(Frames[i])), scale));
            using var p = new Pen(s.Color, 1.5f);
            if (n >= 2) gr.DrawLines(p, pts);
        }
        gr.SmoothingMode = SmoothingMode.None;
    }

    static float Y(Rectangle g, double v, double scale) => (float)(g.Bottom - Math.Min(1.05, v / scale) * (g.Height - 2));

    static double NiceMax(double v)
    {
        double[] steps = { 1, 2, 4, 5, 8, 10, 16.7, 20, 33.4, 40, 50, 66.7, 100, 150, 200, 300, 500, 1000, 2000, 5000 };
        foreach (var s in steps) if (v <= s) return s;
        return Math.Ceiling(v / 1000) * 1000;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (e.X < LegendWidth)
        {
            // Klick auf eine Legenden-Zeile: Reihe ein-/ausblenden
            int row = (e.Y - TitleH - 2) / RowH;
            if (row >= 0 && row < Series.Count) { Series[row].Visible = !Series[row].Visible; Invalidate(); }
            return;
        }
        Pick(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button == MouseButtons.Left && e.X >= LegendWidth) Pick(e.X);
    }

    void Pick(int mx)
    {
        if (Frames.Count == 0) return;
        var g = GraphRect;
        float dx = Dx(g);
        int fromRight = (int)((g.Right - mx) / dx);
        int i = Math.Clamp(Frames.Count - 1 - fromRight, 0, Frames.Count - 1);
        FrameClicked?.Invoke(i);
    }
}
