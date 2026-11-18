using Paradise.Core.Render;
using System.Diagnostics;
using System.Numerics;

namespace Paradise.Core.Diagnostics;

public enum DebugOverlayType
{
    Full,
    Minimal,
    FPS
}

/// <summary>
/// Runtime debug overlay.
///
/// Full:
///     Complete performance, system, GPU and rendering statistics.
///
/// Minimal:
///     Compact performance overview with FPS, frame time, CPU and RAM.
///
/// FPS:
///     FPS and frame time only.
///
/// Toggle visibility using F12 and switch display modes using
/// the DebugOverlayType property.
/// </summary>
public sealed class DebugOverlay : IDisposable
{
    public bool Visible;

    /// <summary>
    /// Current overlay display mode.
    /// </summary>
    public DebugOverlayType Type = DebugOverlayType.Minimal;

    /// <summary>
    /// Time window used for FPS statistics, in seconds.
    /// </summary>
    public float WindowSeconds = 5f;

    /// <summary>
    /// Overlay font scale.
    /// </summary>
    public float Scale = 1f;

    private readonly IRenderer _renderer;

    private FontAtlas? _font;
    private bool _fontFailed;

    private readonly List<(string title, Func<IEnumerable<string>> lines)> _sections = new();

    // ---------------------------------------------------------------------
    // Frame statistics
    // ---------------------------------------------------------------------

    private readonly Queue<(double time, float deltaTime)> _frames = new();
    private double _now;
    private double _nextStats;
    private float _lastFrameMs;
    private float _lastUpdateMs;
    private float _lastRenderMs;
    private float _avgFps;
    private float _minFps;
    private float _maxFps;
    private float _low1Fps;
    private float _avgFrameMs;

    // ---------------------------------------------------------------------
    // Process statistics
    // ---------------------------------------------------------------------

    private readonly Process _process = Process.GetCurrentProcess();

    private TimeSpan _lastCpu;
    private double _lastCpuWall;
    private float _cpuPercent;
    private long _workingSet;
    private long _privateBytes;
    private long _gcHeap;
    private int _gcGen0;
    private int _gcGen1;
    private int _gcGen2;
    private int _threads;
    private readonly GpuMonitor _gpu = new();
    private readonly List<ObjectVertex> _vertices = new(4096);
    private readonly List<uint> _indices = new(6144);
    private readonly List<(string text, uint color)> _lines = new();
    public static readonly uint White = ObjectVertex.PackColor(235, 235, 235, 255);
    public static readonly uint Yellow = ObjectVertex.PackColor(255, 214, 90, 255);
    public static readonly uint Gray = ObjectVertex.PackColor(160, 160, 160, 255);
    public static readonly uint Green = ObjectVertex.PackColor(120, 230, 120, 255);
    public static readonly uint Red = ObjectVertex.PackColor(255, 110, 100, 255);
    public static readonly uint Orange = ObjectVertex.PackColor(255, 170, 90, 255);

    public DebugOverlay(IRenderer renderer)
    {
        _renderer = renderer;
        _lastCpu = _process.TotalProcessorTime;
    }

    /// <summary>
    /// Registers a custom debug section.
    /// Custom sections are only evaluated in Full mode while the overlay is visible.
    /// </summary>
    public void AddSection(string title, Func<IEnumerable<string>> lines)
    {
        _sections.Add((title, lines));
    }

    /// <summary>
    /// Records frame timings.
    /// Call once per frame from the game loop.
    /// All timings are specified in seconds.
    /// </summary>
    public void RecordFrame(double now, float frameSeconds, float updateSeconds, float renderSeconds)
    {
        _now = now;

        _lastFrameMs = frameSeconds * 1000f;
        _lastUpdateMs = updateSeconds * 1000f;
        _lastRenderMs = renderSeconds * 1000f;

        if (frameSeconds > 0f)
            _frames.Enqueue((now, frameSeconds));

        while (_frames.Count > 0 &&
               now - _frames.Peek().time > WindowSeconds)
        {
            _frames.Dequeue();
        }

        if (now >= _nextStats)
        {
            _nextStats = now + 0.5;

            ComputeFrameStats();
            SampleProcess(now);

            if (Visible && Type == DebugOverlayType.Full)
                _gpu.Sample();
        }
    }

    private void ComputeFrameStats()
    {
        int count = _frames.Count;

        if (count == 0)
            return;

        var values = new float[count];
        int index = 0;
        double sum = 0;
        float min = float.MaxValue;
        float max = 0f;

        foreach (var (_, deltaTime) in _frames)
        {
            values[index++] = deltaTime;
            sum += deltaTime;
            if (deltaTime < min)
                min = deltaTime;
            if (deltaTime > max)
                max = deltaTime;
        }

        _avgFps = (float)(count / sum);
        _avgFrameMs = (float)(sum / count * 1000.0);
        _maxFps = 1f / min;
        _minFps = 1f / max;
        Array.Sort(values);
        int slowCount = System.Math.Max(1, count / 100);
        double slowTime = 0;

        for (int i = count - slowCount; i < count; i++)
            slowTime += values[i];
        _low1Fps = (float)(slowCount / slowTime);
    }

    private void SampleProcess(double now)
    {
        try
        {
            _process.Refresh();

            var cpu = _process.TotalProcessorTime;

            double wall = now - _lastCpuWall;

            if (_lastCpuWall > 0 && wall > 0)
            {
                _cpuPercent =
                    (float)((cpu - _lastCpu).TotalSeconds /
                    (wall * Environment.ProcessorCount) * 100.0);
            }

            _lastCpu = cpu;
            _lastCpuWall = now;
            _workingSet = _process.WorkingSet64;
            _privateBytes = _process.PrivateMemorySize64;
            _threads = _process.Threads.Count;
            _gcHeap = GC.GetTotalMemory(false);
            _gcGen0 = GC.CollectionCount(0);
            _gcGen1 = GC.CollectionCount(1);
            _gcGen2 = GC.CollectionCount(2);
        }
        catch
        {
            // Process statistics are optional.
        }
    }

    private static string Mb(long bytes)
    {
        return (bytes / (1024.0 * 1024.0)).ToString("0") + " MB";
    }

    /// <summary>
    /// Draws the debug overlay.
    /// Call between BeginFrame and EndFrame, after rendering modules.
    /// </summary>
    public void Draw()
    {
        if (!Visible)
            return;

        if (_font == null)
        {
            if (_fontFailed)
                return;

            _font = FontAtlas.Create( _renderer, face: "Segoe UI", pixelHeight: 16, weight: 400);

            if (_font == null)
            {
                _fontFailed = true;
                return;
            }
        }

        _lines.Clear();

        switch (Type)
        {
            case DebugOverlayType.Full:
                DrawFull();
                break;

            case DebugOverlayType.Minimal:
                DrawMinimal();
                break;

            case DebugOverlayType.FPS:
                DrawFPS();
                break;
        }

        Build();

        if (_indices.Count > 0 && _font.Material != null)
        {
            _renderer.DrawOverlay(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_vertices), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_indices), _font.Material);
        }
    }

    private void DrawFull()
    {
        DrawPerformance();

        Head("System");
        Line($"CPU       {_cpuPercent,5:0.0}%  ({Environment.ProcessorCount} Cores / {_threads} Threads)");
        Line($"Memory    {Mb(_workingSet)} Working Set");
        Line($"Private   {Mb(_privateBytes)}");
        Line($"GC Heap   {Mb(_gcHeap)}");
        Line($"GC        Gen 0/1/2: {_gcGen0}/{_gcGen1}/{_gcGen2}");
        var stats = _renderer.Stats;
        Head("Graphics");
        Line($"{stats.DeviceName} ({stats.DeviceType})");
        Line($"{stats.Api}  |  Driver: {stats.Driver}");
        Line($"Resolution {stats.Width} x {stats.Height}");
        Line($"GPU Memory {Mb(stats.GpuMemoryAllocated)}");
        Head("Rendering");
        Line($"Draw Calls       {stats.DrawCalls,8:N0}");
        Line($"Triangles        {stats.Triangles,8:N0}");
        Line($"Pipeline Binds   {stats.PipelineBinds,8:N0}");
        Line($"Transparent      {stats.TransparentDraws,8:N0}");
        Line($"Shadow Draws     {stats.ShadowDraws,8:N0}");
        Line($"Textures         {stats.Textures,8:N0}");
        Line($"Materials        {stats.Materials,8:N0}");
        Line($"Meshes           {stats.Meshes,8:N0}");

        foreach (var (title, callback) in _sections)
        {
            Head(title);

            try
            {
                foreach (var line in callback())
                    Line(line);
            }
            catch (Exception ex)
            {
                Line("Section error: " + ex.Message, Red);
            }
        }
    }

    private void DrawMinimal()
    {
        Head("Performance");

        Line(
            $"FPS {_avgFps:0.0}  |  Frame {_lastFrameMs:0.00} ms",
            GetFpsColor(_avgFps));

        Line(
            $"1% Low {_low1Fps:0.0}  |  Min {_minFps:0.0}  |  Max {_maxFps:0.0}");

        Line(
            $"CPU {_cpuPercent:0.0}%  |  RAM {Mb(_workingSet)}",
            Gray);
    }

    private void DrawFPS()
    {
        Line($"FPS {_avgFps:0.0}", GetFpsColor(_avgFps));

        Line($"{_lastFrameMs:0.00} ms", Gray);
    }

    private void DrawPerformance()
    {
        Head("Performance");

        Line($"FPS       {_avgFps,7:0.0}   |   1% Low {_low1Fps,7:0.0}", GetFpsColor(_avgFps));
        Line($"Minimum   {_minFps,7:0.0}   |   Maximum {_maxFps,7:0.0}");
        Line($"Frame     {_lastFrameMs,7:0.00} ms   |   Average {_avgFrameMs:0.00} ms");
        Line($"Update    {_lastUpdateMs,7:0.00} ms   |   Render {_lastRenderMs:0.00} ms");
    }

    private static uint GetFpsColor(float fps)
    {
        if (fps >= 60f)
            return Green;

        if (fps >= 30f)
            return Yellow;

        return Red;
    }

    private void Head(string title)
    {
        _lines.Add(("[" + title + "]", Yellow));
    }

    private void Line(string text, uint color = 0)
    {
        _lines.Add((text, color == 0 ? White : color));
    }

    private void Build()
    {
        _vertices.Clear();
        _indices.Clear();

        var font = _font!;

        float scale = Scale;
        float lineHeight = font.LineHeight * scale;

        float padding = 8f;
        float x0 = 10f;
        float y0 = 10f;

        float maxWidth = 0f;

        foreach (var (text, _) in _lines)
            maxWidth = System.Math.Max(maxWidth, font.Measure(text, scale));

        var white = font.WhiteUv();

        // Background.
        Quad(x0 - padding, y0 - padding, x0 + maxWidth + padding, y0 + _lines.Count * lineHeight + padding, white.u0, white.v0, white.u1, white.v1, ObjectVertex.PackColor(0, 0, 0, 115));

        float y = y0;

        foreach (var (text, color) in _lines)
        {
            float x = x0;

            foreach (char c in text)
            {
                var glyph = font.GetGlyph(c);

                if (glyph.Width > 0 && glyph.Height > 0)
                {
                    Quad(x, y, x + glyph.Width * scale, y + glyph.Height * scale, glyph.U0, glyph.V0, glyph.U1, glyph.V1, color);
                }

                x += glyph.Advance * scale;
            }

            y += lineHeight;
        }
    }

    private void Quad(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, uint color)
    {
        uint index = (uint)_vertices.Count;

        _vertices.Add(new ObjectVertex
        {
            Position = new Vector3(x0, y0, 0),
            Normal = Vector3.UnitZ,
            Color = color,
            Uv0 = new Vector2(u0, v0)
        });

        _vertices.Add(new ObjectVertex
        {
            Position = new Vector3(x1, y0, 0),
            Normal = Vector3.UnitZ,
            Color = color,
            Uv0 = new Vector2(u1, v0)
        });

        _vertices.Add(new ObjectVertex
        {
            Position = new Vector3(x1, y1, 0),
            Normal = Vector3.UnitZ,
            Color = color,
            Uv0 = new Vector2(u1, v1)
        });

        _vertices.Add(new ObjectVertex
        {
            Position = new Vector3(x0, y1, 0),
            Normal = Vector3.UnitZ,
            Color = color,
            Uv0 = new Vector2(u0, v1)
        });

        _indices.Add(index);
        _indices.Add(index + 1);
        _indices.Add(index + 2);
        _indices.Add(index);
        _indices.Add(index + 2);
        _indices.Add(index + 3);
    }

    public void Dispose()
    {
        _font?.Dispose();
        _gpu.Dispose();
        _process.Dispose();
    }
}