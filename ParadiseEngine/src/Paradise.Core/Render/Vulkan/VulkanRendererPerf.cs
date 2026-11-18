using Paradise.Core.Diagnostics;
using Silk.NET.Vulkan;

namespace Paradise.Core.Render.Vulkan;

/// <summary>
/// Messung, wo die Bildzeit bleibt (DIAG Perf im Log): GPU-Zeitstempel je Abschnitt (Schatten, Spiegel/Sonde/Vorschau,
/// Szene, Oberfläche) und auf der CPU das Warten auf die GPU sowie das Aufzeichnen in EndFrame. Ist "Warten auf GPU" groß,
/// begrenzt die Grafikkarte, sonst die CPU (Aufzeichnen/Spiellogik).
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private const int TsPerFrame = 5;
    private QueryPool _tsPool;
    private float _tsPeriodNs;
    private readonly bool[] _tsWritten = new bool[MaxFramesInFlight];
    private static readonly string[] TsNames = { "GPU Schatten", "GPU Spiegel/Sonde/Vorschau", "GPU Szene (opak+transparent)", "GPU Oberfläche (Flash)" };

    private void CreateTimestamps()
    {
        //try
        //{
        //    _vk.GetPhysicalDeviceProperties(_physical, out var p);
        //    _tsPeriodNs = p.Limits.TimestampPeriod;
        //    if (_tsPeriodNs <= 0f) return;
        //    var info = new QueryPoolCreateInfo { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Timestamp, QueryCount = (uint)(MaxFramesInFlight * TsPerFrame) };
        //    if (_vk.CreateQueryPool(_device, in info, null, out _tsPool) != Result.Success) _tsPool = default;
        //}
        //catch (Exception ex) { Log.Warn("GPU-Zeitmessung nicht verfügbar: " + ex.Message); _tsPool = default; }
    }

    private void DestroyTimestamps()
    {
        //if (_tsPool.Handle != 0) _vk.DestroyQueryPool(_device, _tsPool, null);
        //_tsPool = default;
    }

    private void TsBegin(CommandBuffer cmd)
    {
        //if (_tsPool.Handle == 0) return;
        //_vk.CmdResetQueryPool(cmd, _tsPool, (uint)(_currentFrame * TsPerFrame), TsPerFrame);
        //Ts(cmd, 0);
        //_tsWritten[_currentFrame] = true;
    }

    private void Ts(CommandBuffer cmd, int i)
    {
        //if (_tsPool.Handle == 0) return;
        //_vk.CmdWriteTimestamp(cmd, PipelineStageFlags.BottomOfPipeBit, _tsPool, (uint)(_currentFrame * TsPerFrame + i));
    }

    /// <summary>Nach dem Warten auf den Fence dieses Bildes: seine Zeitstempel sind fertig.</summary>
    private void ReadTimestamps()
    {
        //if (_tsPool.Handle == 0 || !_tsWritten[_currentFrame]) return;
        //_tsWritten[_currentFrame] = false;
        //var t = stackalloc ulong[TsPerFrame];
        //var r = _vk.GetQueryPoolResults(_device, _tsPool, (uint)(_currentFrame * TsPerFrame), TsPerFrame, (nuint)(TsPerFrame * sizeof(ulong)), t, sizeof(ulong), QueryResultFlags.Result64Bit);
        //if (r != Result.Success) return;
        //double k = _tsPeriodNs / 1e6;
        //for (int i = 0; i < TsPerFrame - 1; i++)
        //    if (t[i + 1] >= t[i]) PerfLog.Add(TsNames[i], (t[i + 1] - t[i]) * k);
        //if (t[TsPerFrame - 1] >= t[0]) PerfLog.Add("GPU gesamt", (t[TsPerFrame - 1] - t[0]) * k);
    }
}
