using System.Runtime.InteropServices;

namespace Paradise.Core.Diagnostics;

/// <summary>
/// Live-Werte der Grafikkarte (Takt, Auslastung, VRAM, Temperatur). Vulkan liefert diese Werte nicht;
/// bei NVIDIA kommen sie aus NVML (nvml.dll, Teil des Treibers). Andere Hersteller: nicht verfügbar.
/// </summary>
public sealed class GpuMonitor : IDisposable
{
    public bool Available { get; private set; }
    public string Source { get; private set; } = "n/a";
    public uint CoreClockMHz, MemClockMHz, MaxCoreClockMHz, GpuUtil, MemUtil, TempC;
    public ulong VramUsed, VramTotal;

    private IntPtr _dev;
    private bool _inited;

    public GpuMonitor()
    {
        try
        {
            if (!NativeLibrary.TryLoad("nvml.dll", out _) &&
                !NativeLibrary.TryLoad(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll"), out _))
                return;
            if (nvmlInit_v2() != 0) return;
            _inited = true;
            if (nvmlDeviceGetHandleByIndex_v2(0, out _dev) != 0) return;
            nvmlDeviceGetMaxClockInfo(_dev, 0, out MaxCoreClockMHz);
            Available = true;
            Source = "NVML";
        }
        catch (Exception) { Available = false; }
    }

    public void Sample()
    {
        if (!Available) return;
        try
        {
            nvmlDeviceGetClockInfo(_dev, 0, out CoreClockMHz);   // NVML_CLOCK_GRAPHICS
            nvmlDeviceGetClockInfo(_dev, 2, out MemClockMHz);    // NVML_CLOCK_MEM
            if (nvmlDeviceGetUtilizationRates(_dev, out var u) == 0) { GpuUtil = u.gpu; MemUtil = u.memory; }
            if (nvmlDeviceGetMemoryInfo(_dev, out var m) == 0) { VramUsed = m.used; VramTotal = m.total; }
            nvmlDeviceGetTemperature(_dev, 0, out TempC);
        }
        catch (Exception) { Available = false; }
    }

    public void Dispose()
    {
        if (_inited) { try { nvmlShutdown(); } catch { } _inited = false; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NvmlUtil { public uint gpu, memory; }
    [StructLayout(LayoutKind.Sequential)] private struct NvmlMem { public ulong total, free, used; }

    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlShutdown();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMaxClockInfo(IntPtr device, int type, out uint mhz);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtil util);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMem mem);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
}