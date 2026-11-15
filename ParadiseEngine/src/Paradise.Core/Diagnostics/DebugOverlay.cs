using Paradise.Core.Render;
using System.Diagnostics;
using System.Numerics;

namespace Paradise.Core.Diagnostics;

/// <summary>
/// Debug-UI links oben (F12 an/aus): FPS (Ø/Min/Max/1 %-Low), Frame-/Update-/Render-Zeit, RAM, CPU,
/// Render-Statistik (Draw Calls, Dreiecke, Pipelines, Texturen/Meshes/Materialien, GPU-Speicher),
/// GPU-Daten (Name, Treiber, VRAM, Takt/Auslastung über NVML) und frei registrierbare Abschnitte (z. B. Welt-Streaming).
/// </summary>
public sealed class DebugOverlay : IDisposable
{
    public bool Visible;
    /// <summary>Zeitfenster für Ø/Min/Max/1 %-Low (Sekunden).</summary>
    public float WindowSeconds = 5f;
    public float Scale = 1f;

    private readonly IRenderer _renderer;
    private FontAtlas? _font;
    private bool _fontFailed;
    private readonly List<(string title, Func<IEnumerable<string>> lines)> _sections = new();

    // Frame-Zeiten (Sekunden) im Ringpuffer
    private readonly Queue<(double t, float dt)> _frames = new();
    private double _now;
    private float _lastFrameMs, _lastUpdateMs, _lastRenderMs;
    private float _avgFps, _minFps, _maxFps, _low1Fps, _avgFrameMs;
    private double _nextStats;

    // Prozess
    private readonly Process _proc = Process.GetCurrentProcess();
    private TimeSpan _lastCpu;
    private double _lastCpuWall;
    private float _cpuPercent;
    private long _workingSet, _privateBytes, _gcHeap;
    private int _gcGen0, _gcGen1, _gcGen2, _threads;
    private readonly GpuMonitor _gpu = new();

    private readonly List<ObjectVertex> _v = new(4096);
    private readonly List<uint> _i = new(6144);
    private readonly List<(string text, uint color)> _lines = new();

    public static readonly uint White = ObjectVertex.PackColor(235, 235, 235, 255);
    public static readonly uint Yellow = ObjectVertex.PackColor(255, 214, 90, 255);
    public static readonly uint Gray = ObjectVertex.PackColor(160, 160, 160, 255);
    public static readonly uint Green = ObjectVertex.PackColor(120, 230, 120, 255);
    public static readonly uint Red = ObjectVertex.PackColor(255, 110, 100, 255);

    public DebugOverlay(IRenderer renderer)
    {
        _renderer = renderer;
        _lastCpu = _proc.TotalProcessorTime;
    }

    /// <summary>Eigener Abschnitt (z. B. Welt-Streaming). Die Funktion wird nur bei sichtbarer UI aufgerufen.</summary>
    public void AddSection(string title, Func<IEnumerable<string>> lines) => _sections.Add((title, lines));

    /// <summary>Pro Frame vom GameLoop: Gesamtzeit, Update-Zeit und Render-Zeit (CPU) in Sekunden.</summary>
    public void RecordFrame(double now, float frameSeconds, float updateSeconds, float renderSeconds)
    {
        _now = now;
        _lastFrameMs = frameSeconds * 1000f;
        _lastUpdateMs = updateSeconds * 1000f;
        _lastRenderMs = renderSeconds * 1000f;
        if (frameSeconds > 0f) _frames.Enqueue((now, frameSeconds));
        while (_frames.Count > 0 && now - _frames.Peek().t > WindowSeconds) _frames.Dequeue();

        if (now >= _nextStats)
        {
            _nextStats = now + 0.5;
            ComputeFrameStats();
            SampleProcess(now);
            if (Visible) _gpu.Sample();
        }
    }

    private void ComputeFrameStats()
    {
        int n = _frames.Count;
        if (n == 0) return;
        var arr = new float[n];
        int k = 0; double sum = 0; float mn = float.MaxValue, mx = 0f;
        foreach (var (_, dt) in _frames) { arr[k++] = dt; sum += dt; if (dt < mn) mn = dt; if (dt > mx) mx = dt; }
        _avgFps = (float)(n / sum);
        _avgFrameMs = (float)(sum / n * 1000.0);
        _maxFps = 1f / mn;
        _minFps = 1f / mx;
        // 1 %-Low: Ø-FPS der langsamsten 1 % Frames
        Array.Sort(arr);
        int cnt = System.Math.Max(1, n / 100);
        double slow = 0; for (int i = n - cnt; i < n; i++) slow += arr[i];
        _low1Fps = (float)(cnt / slow);
    }

    private void SampleProcess(double now)
    {
        try
        {
            _proc.Refresh();
            var cpu = _proc.TotalProcessorTime;
            double wall = now - _lastCpuWall;
            if (_lastCpuWall > 0 && wall > 0)
                _cpuPercent = (float)((cpu - _lastCpu).TotalSeconds / (wall * Environment.ProcessorCount) * 100.0);
            _lastCpu = cpu; _lastCpuWall = now;
            _workingSet = _proc.WorkingSet64;
            _privateBytes = _proc.PrivateMemorySize64;
            _threads = _proc.Threads.Count;
            _gcHeap = GC.GetTotalMemory(false);
            _gcGen0 = GC.CollectionCount(0); _gcGen1 = GC.CollectionCount(1); _gcGen2 = GC.CollectionCount(2);
        }
        catch (Exception) { }
    }

    static string Mb(long b) => (b / (1024.0 * 1024.0)).ToString("0") + " MB";

    /// <summary>Zwischen BeginFrame und EndFrame aufrufen (nach den Modulen).</summary>
    public void Draw()
    {
        if (!Visible) return;
        {
            if (_font == null)
            {
                if (_fontFailed) return;
                _font = FontAtlas.Create(_renderer);
                if (_font == null) { _fontFailed = true; return; }
            }
            _lines.Clear();
            var rs = _renderer.Stats;

            Head("Leistung");
            Line($"FPS {_avgFps,6:0.0}   1% Low {_low1Fps,6:0.0}", _low1Fps < 30 ? Red : Green);
            Line($"Min {_minFps,6:0.0}   Max {_maxFps,6:0.0}   ({WindowSeconds:0}s)");
            Line($"Frame {_lastFrameMs,6:0.00} ms (Ø {_avgFrameMs:0.00})");
            Line($"Update {_lastUpdateMs,5:0.00} ms   Render {_lastRenderMs,5:0.00} ms");

            Head("System");
            Line($"CPU   {_cpuPercent,5:0.0} %  ({Environment.ProcessorCount} Threads, Prozess {_threads})");
            Line($"RAM   {Mb(_workingSet)} (privat {Mb(_privateBytes)})");
            Line($"GC    {Mb(_gcHeap)}  Gen0/1/2 {_gcGen0}/{_gcGen1}/{_gcGen2}");

            Head("GPU");
            Line($"{rs.DeviceName} ({rs.DeviceType})");
            Line($"{rs.Api}  Treiber {rs.Driver}  {rs.Width}x{rs.Height}");

            Line($"Engine-Allokation {Mb(rs.GpuMemoryAllocated)}");

            Head("Rendern");
            Line($"Draw Calls {rs.DrawCalls,6}   Dreiecke {rs.Triangles:N0}");
            Line($"Pipeline-Wechsel {rs.PipelineBinds}   Transparent {rs.TransparentDraws}   Schatten {rs.ShadowDraws}");
            Line($"Texturen {rs.Textures}   Materialien {rs.Materials}   Meshes {rs.Meshes}");

            foreach (var (title, fn) in _sections)
            {
                Head(title);
                try { foreach (var l in fn()) Line(l); }
                catch (Exception ex) { Line("Fehler: " + ex.Message, Red); }
            }

        }
        Build();
        if (_i.Count > 0 && _font.Material != null)
            _renderer.DrawOverlay(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_v), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_i), _font.Material);
    }

    private void Head(string t) => _lines.Add(("[" + t + "]", Yellow));
    private void Line(string t, uint color = 0) => _lines.Add((t, color == 0 ? White : color));

    private void Build()
    {
        _v.Clear(); _i.Clear();
        var f = _font!;
        float cw = f.CellW * Scale, ch = f.CellH * Scale;
        float pad = 6f, x0 = 8f, y0 = 8f;
        int maxLen = 0;
        foreach (var (t, _) in _lines) maxLen = System.Math.Max(maxLen, t.Length);
        // Hintergrund
        var w = f.WhiteUv();
        Quad(x0 - pad, y0 - pad, x0 + maxLen * cw + pad, y0 + _lines.Count * ch + pad, w.u0, w.v0, w.u1, w.v1, ObjectVertex.PackColor(0, 0, 0, 170));
        float y = y0;
        foreach (var (t, col) in _lines)
        {
            float x = x0;
            foreach (char c in t)
            {
                if (c != ' ')
                {
                    var uv = f.Uv(c);
                    Quad(x, y, x + cw, y + ch, uv.u0, uv.v0, uv.u1, uv.v1, col);
                }
                x += cw;
            }
            y += ch;
        }
    }

    private void Quad(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, uint col)
    {
        uint b = (uint)_v.Count;
        _v.Add(new ObjectVertex { Position = new Vector3(x0, y0, 0), Normal = Vector3.UnitZ, Color = col, Uv0 = new Vector2(u0, v0) });
        _v.Add(new ObjectVertex { Position = new Vector3(x1, y0, 0), Normal = Vector3.UnitZ, Color = col, Uv0 = new Vector2(u1, v0) });
        _v.Add(new ObjectVertex { Position = new Vector3(x1, y1, 0), Normal = Vector3.UnitZ, Color = col, Uv0 = new Vector2(u1, v1) });
        _v.Add(new ObjectVertex { Position = new Vector3(x0, y1, 0), Normal = Vector3.UnitZ, Color = col, Uv0 = new Vector2(u0, v1) });
        _i.Add(b); _i.Add(b + 1); _i.Add(b + 2); _i.Add(b); _i.Add(b + 2); _i.Add(b + 3);
    }

    public void Dispose()
    {
        _font?.Dispose();
        _gpu.Dispose();
    }
}
