using Paradise.Core.Diagnostics;
using Silk.NET.Vulkan;

namespace Paradise.Core.Render.Vulkan;

/// <summary>
/// GPU-Zeitmessung je Abschnitt (Schatten, Spiegel/Sonde/Vorschau, Szene, Oberfläche) über Vulkan-Zeitstempel.
/// Die Werte gehen an den Engine-Profiler (F1 &gt; Performance &gt; Open Profiler, Graph "GPU Usage").
/// Zeitstempel werden nur geschrieben, solange der Profiler aufnimmt (sonst keinerlei Kosten auf der GPU).
/// Die Ergebnisse eines Bildes sind erst nach seinem Fence fertig, kommen also 1–2 Bilder verzögert an.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private const int TsPerFrame = 5;
    private QueryPool _tsPool;
    private float _tsPeriodNs;
    private readonly bool[] _tsWritten = new bool[MaxFramesInFlight];
    private static readonly ProfilerMarker[] TsMarkers =
    {
        new("GPU Shadows", ProfilerCategory.Rendering),
        new("GPU Mirror / Probe / Preview", ProfilerCategory.Rendering),
        new("GPU Scene (opaque + transparent)", ProfilerCategory.Rendering),
        new("GPU UI (Flash) + Post", ProfilerCategory.Rendering),
    };
    private static readonly ProfilerMarker TsTotal = new(Profiler.GpuTotalName, ProfilerCategory.Rendering);

    // CPU-Messpunkte im Renderer (Timeline/Hierarchy): Warten auf die GPU steht in der Kategorie "Wait"
    private static readonly ProfilerMarker s_mFence = new("Vulkan.WaitForFences (GPU busy)", ProfilerCategory.Wait);
    private static readonly ProfilerMarker s_mAcquire = new("Vulkan.AcquireNextImage", ProfilerCategory.Wait);
    private static readonly ProfilerMarker s_mShadowRec = new("Vulkan.RecordShadows", ProfilerCategory.Rendering);
    private static readonly ProfilerMarker s_mShadowJoin = new("Vulkan.JoinShadows (wait for worker)", ProfilerCategory.Wait);
    private static readonly ProfilerMarker s_mSubmit = new("Vulkan.QueueSubmit", ProfilerCategory.Rendering);
    private static readonly ProfilerMarker s_mPresent = new("Vulkan.QueuePresent (VSync)", ProfilerCategory.Wait);
    // Abschnitte in EndFrame (CPU-Aufzeichnung)
    private static readonly ProfilerMarker s_mEfUploads = new("EndFrame.TextureUploads", ProfilerCategory.Rendering);
    private static readonly ProfilerMarker s_mEfSort = new("EndFrame.SortOpaque", ProfilerCategory.Rendering);
    private static readonly ProfilerMarker s_mEfEnv = new("EndFrame.RenderEnvironment (mirror/probe)", ProfilerCategory.Rendering);
    private static readonly ProfilerMarker s_mEfOpaque = new("EndFrame.Sky + Opaque batches", ProfilerCategory.Rendering);
    private static readonly ProfilerMarker s_mEfTransp = new("EndFrame.Additive + Transparent", ProfilerCategory.Rendering);
    private static readonly ProfilerMarker s_mEfUi = new("EndFrame.Photoshop + Flash/UI + Overlay", ProfilerCategory.Rendering);

    // GPU-Zeit je Objektart im Hauptbild (Zusatz-Zeitstempel an jedem Pipeline-Wechsel der sortierten opaken Liste, dazu Himmel,
    // additive und transparente Objekte). Ersetzt im Profiler "GPU Scene" durch "GPU Scene: Terrain/Object/Foliage/…".
    private const int SegCap = 64;
    private QueryPool _segPool;
    private readonly int[] _segWritten = new int[MaxFramesInFlight];
    private readonly ProfilerMarker?[,] _segMarkers = new ProfilerMarker?[MaxFramesInFlight, SegCap];
    private bool _segActive;
    private static readonly ProfilerMarker s_gSky = new("GPU Scene: Sky / Background", ProfilerCategory.Rendering);
    private static readonly ProfilerMarker s_gAdditive = new("GPU Scene: Additive", ProfilerCategory.Rendering);
    private static readonly ProfilerMarker s_gTransparent = new("GPU Scene: Transparent", ProfilerCategory.Rendering);
    private static readonly ProfilerMarker?[] s_gPipe = new ProfilerMarker?[PipeCount];

    // Objekte, Draw Calls und Dreiecke je Abschnitt (zeigt, ob eine Objektart an der Zahl der Aufrufe oder an der Geometrie hängt)
    private sealed class SegCount { public ProfilerCounter Obj = null!, Draws = null!, Tris = null!; }
    private static readonly Dictionary<int, SegCount> s_segCount = new();
    private int _segDraw0, _segTris0;

    private static SegCount SegCountFor(ProfilerMarker m)
    {
        if (s_segCount.TryGetValue(m.Id, out var c)) return c;
        string n = m.Name.StartsWith("GPU Scene: ", StringComparison.Ordinal) ? m.Name.Substring(11) : m.Name;
        c = new SegCount
        {
            Obj = new ProfilerCounter(n + ": objects", ProfilerCounters.SceneDetail, resetEachFrame: true),
            Draws = new ProfilerCounter(n + ": draw calls", ProfilerCounters.SceneDetail, resetEachFrame: true),
            Tris = new ProfilerCounter(n + ": triangles", ProfilerCounters.SceneDetail, resetEachFrame: true),
        };
        s_segCount[m.Id] = c;
        return c;
    }

    private static ProfilerMarker SegMarkerFor(PipeId id)
    {
        int i = (int)id;
        if ((uint)i >= (uint)s_gPipe.Length) return ProfilerMarker.Get("GPU Scene: " + id, ProfilerCategory.Rendering);
        return s_gPipe[i] ??= ProfilerMarker.Get("GPU Scene: " + id, ProfilerCategory.Rendering);
    }

    /// <summary>Erster Zeitstempel der Szenen-Abschnitte (im Hauptdurchgang, nach BeginRenderPass).</summary>
    private void SegBegin(CommandBuffer cmd)
    {
        _segWritten[_currentFrame] = 0;
        _segDraw0 = _cDraw; _segTris0 = _cTris;
        if (!_segActive) return;
        _vk.CmdWriteTimestamp(cmd, PipelineStageFlags.BottomOfPipeBit, _segPool, (uint)(_currentFrame * SegCap));
        _segWritten[_currentFrame] = 1;
    }

    /// <summary>Abschnitt endet hier (alle bisher aufgezeichneten Draws gehören zu <paramref name="m"/>).
    /// <paramref name="objects"/>: Zahl der Objekte des Abschnitts (vor dem Zusammenfassen zu Instanz-Aufrufen).</summary>
    private void SegMark(CommandBuffer cmd, ProfilerMarker m, int objects)
    {
        int w = _segWritten[_currentFrame];
        if (!_segActive || w == 0 || w >= SegCap) return;
        var c = SegCountFor(m);
        c.Obj.Add(objects);
        c.Draws.Add(_cDraw - _segDraw0);
        c.Tris.Add(_cTris - _segTris0);
        _segDraw0 = _cDraw; _segTris0 = _cTris;
        _vk.CmdWriteTimestamp(cmd, PipelineStageFlags.BottomOfPipeBit, _segPool, (uint)(_currentFrame * SegCap + w));
        _segMarkers[_currentFrame, w - 1] = m;
        _segWritten[_currentFrame] = w + 1;
    }

    private void CreateTimestamps()
    {
        if (!Profiler.Available) return;   // ohne PARADISE_PROFILER keine Abfrage-Objekte
        try
        {
            _vk.GetPhysicalDeviceProperties(_physical, out var p);
            _tsPeriodNs = p.Limits.TimestampPeriod;
            if (_tsPeriodNs <= 0f) return;
            var info = new QueryPoolCreateInfo { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Timestamp, QueryCount = (uint)(MaxFramesInFlight * TsPerFrame) };
            if (_vk.CreateQueryPool(_device, in info, null, out _tsPool) != Result.Success) _tsPool = default;
            var segInfo = new QueryPoolCreateInfo { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Timestamp, QueryCount = (uint)(MaxFramesInFlight * SegCap) };
            if (_vk.CreateQueryPool(_device, in segInfo, null, out _segPool) != Result.Success) _segPool = default;
        }
        catch (Exception ex) { Log.Warn("GPU-Zeitmessung nicht verfügbar: " + ex.Message); _tsPool = default; }
    }

    private void DestroyTimestamps()
    {
        if (_tsPool.Handle != 0) _vk.DestroyQueryPool(_device, _tsPool, null);
        _tsPool = default;
        if (_segPool.Handle != 0) _vk.DestroyQueryPool(_device, _segPool, null);
        _segPool = default;
    }

    /// <summary>Erster Zeitstempel des Bildes (Schatten-Command-Buffer, wird zuerst abgeschickt). Nur bei laufender Aufnahme.</summary>
    private void TsBegin(CommandBuffer cmd)
    {
        _tsWritten[_currentFrame] = false;
        _segActive = false;
        _segWritten[_currentFrame] = 0;
        if (_tsPool.Handle == 0 || !Profiler.Enabled) return;
        _vk.CmdResetQueryPool(cmd, _tsPool, (uint)(_currentFrame * TsPerFrame), TsPerFrame);
        if (_segPool.Handle != 0)
        {
            _vk.CmdResetQueryPool(cmd, _segPool, (uint)(_currentFrame * SegCap), SegCap);
            _segActive = true;
        }
        _tsWritten[_currentFrame] = true;
        Ts(cmd, 0);
    }

    private void Ts(CommandBuffer cmd, int i)
    {
        if (_tsPool.Handle == 0 || !_tsWritten[_currentFrame]) return;
        _vk.CmdWriteTimestamp(cmd, PipelineStageFlags.BottomOfPipeBit, _tsPool, (uint)(_currentFrame * TsPerFrame + i));
    }

    /// <summary>Nach dem Warten auf den Fence dieses Bildes: seine Zeitstempel sind fertig.</summary>
    private void ReadTimestamps()
    {
        if (_tsPool.Handle == 0 || !_tsWritten[_currentFrame]) return;
        _tsWritten[_currentFrame] = false;
        var t = stackalloc ulong[TsPerFrame];
        var r = _vk.GetQueryPoolResults(_device, _tsPool, (uint)(_currentFrame * TsPerFrame), TsPerFrame, (nuint)(TsPerFrame * sizeof(ulong)), t, sizeof(ulong), QueryResultFlags.Result64Bit);
        if (r != Result.Success) return;
        double k = _tsPeriodNs / 1e6;
        // Szenen-Abschnitte (je Objektart); wenn vorhanden, ersetzen sie den Gesamtwert "GPU Scene"
        bool segOk = false;
        int segN = _segWritten[_currentFrame] - 1;
        _segWritten[_currentFrame] = 0;
        if (segN > 0 && _segPool.Handle != 0)
        {
            var st = stackalloc ulong[SegCap];
            var sr = _vk.GetQueryPoolResults(_device, _segPool, (uint)(_currentFrame * SegCap), (uint)(segN + 1), (nuint)((segN + 1) * sizeof(ulong)), st, sizeof(ulong), QueryResultFlags.Result64Bit);
            if (sr == Result.Success)
            {
                segOk = true;
                for (int s = 0; s < segN; s++)
                {
                    var m = _segMarkers[_currentFrame, s];
                    if (m != null && st[s + 1] >= st[s]) Profiler.ReportGpu(m, (st[s + 1] - st[s]) * k);
                }
            }
        }
        for (int i = 0; i < TsPerFrame - 1; i++)
        {
            if (i == 2 && segOk) continue;   // "GPU Scene" ist in die Abschnitte aufgeteilt
            if (t[i + 1] >= t[i]) Profiler.ReportGpu(TsMarkers[i], (t[i + 1] - t[i]) * k);
        }
        if (t[TsPerFrame - 1] >= t[0]) Profiler.ReportGpu(TsTotal, (t[TsPerFrame - 1] - t[0]) * k);
    }
}
