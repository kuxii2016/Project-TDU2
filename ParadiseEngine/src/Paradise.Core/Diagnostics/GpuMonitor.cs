using System.Runtime.InteropServices;

namespace Paradise.Core.Diagnostics;

/// <summary>
/// Live-Werte der Grafikkarte (Takt, Auslastung, VRAM, Temperatur). Vulkan liefert diese Werte nicht;
/// sie kommen aus NVML (NVIDIA) bzw. ADL2 (atiadlxx.dll des AMD-Treibers). Wenn der Renderer eine
/// bestimmte GPU nutzt, wird deren Name/PCI-ID an ADL übergeben, damit nicht die iGPU einer Workstation
/// ausgelesen wird.
/// </summary>
public sealed class GpuMonitor : IDisposable
{
    private enum Backend { None, Nvml, Adl2 }

    public bool Available { get; private set; }
    public string Source { get; private set; } = "n/a";
    public uint CoreClockMHz, MemClockMHz, MaxCoreClockMHz, GpuUtil, MemUtil, TempC, TempHotspotC, PowerW;
    public bool MemUtilAvailable;
    public ulong VramUsed, VramTotal;

    private Backend _backend;
    private IntPtr _nvmlLib, _dev, _adlLib, _adlContext;
    private int _adlAdapter = -1;
    private bool _nvmlInited;

    public GpuMonitor(string preferredDeviceName = "", uint preferredVendorId = 0, uint preferredDeviceId = 0)
    {
        // Keine NVIDIA-Treiberbibliothek laden, wenn Vulkan eindeutig eine AMD-GPU rendert
        // (sonst könnte eine zweite, alte NVIDIA-Karte ausgelesen werden).
        if (preferredVendorId == 0 || preferredVendorId == 0x10DE)
        {
            try
            {
                if (TryInitNvml())
                    return;
            }
            catch (Exception)
            {
                ShutdownNvml();
            }
        }

        if (preferredVendorId != 0 && preferredVendorId != 0x1002) return;

        try
        {
            TryInitAdl2(preferredDeviceName ?? "", preferredDeviceId);
        }
        catch (Exception)
        {
            ShutdownAdl();
        }
    }

    public void Sample()
    {
        if (!Available) return;
        try
        {
            if (_backend == Backend.Nvml) SampleNvml();
            else if (_backend == Backend.Adl2) SampleAdl2();
        }
        catch (Exception)
        {
            Available = false;
        }
    }

    public void Dispose()
    {
        ShutdownNvml();
        ShutdownAdl();
        _backend = Backend.None;
    }

    // ---------------------------------------------------------------- NVML

    private bool TryInitNvml()
    {
        if (!NativeLibrary.TryLoad("nvml.dll", out _nvmlLib) &&
            !NativeLibrary.TryLoad(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation", "NVSMI", "nvml.dll"), out _nvmlLib))
            return false;
        if (nvmlInit_v2() != 0) return false;
        _nvmlInited = true;
        if (nvmlDeviceGetHandleByIndex_v2(0, out _dev) != 0) return false;
        nvmlDeviceGetMaxClockInfo(_dev, 0, out MaxCoreClockMHz);
        MemUtilAvailable = true;
        _backend = Backend.Nvml;
        Available = true;
        Source = "NVML";
        Sample();
        return true;
    }

    private void SampleNvml()
    {
        nvmlDeviceGetClockInfo(_dev, 0, out CoreClockMHz);   // NVML_CLOCK_GRAPHICS
        nvmlDeviceGetClockInfo(_dev, 2, out MemClockMHz);    // NVML_CLOCK_MEM
        if (nvmlDeviceGetUtilizationRates(_dev, out var u) == 0) { GpuUtil = u.gpu; MemUtil = u.memory; }
        if (nvmlDeviceGetMemoryInfo(_dev, out var m) == 0) { VramUsed = m.used; VramTotal = m.total; }
        nvmlDeviceGetTemperature(_dev, 0, out TempC);
    }

    private void ShutdownNvml()
    {
        if (_nvmlInited) { try { nvmlShutdown(); } catch { } _nvmlInited = false; }
        if (_nvmlLib != IntPtr.Zero) { try { NativeLibrary.Free(_nvmlLib); } catch { } _nvmlLib = IntPtr.Zero; }
        _dev = IntPtr.Zero;
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

    // ---------------------------------------------------------------- AMD ADL2

    private sealed class AdlCandidate
    {
        public int Index;
        public AdlAdapterInfo Info;
        public bool Active;
    }

    private bool TryInitAdl2(string preferredName, uint preferredDeviceId)
    {
        if (!NativeLibrary.TryLoad("atiadlxx.dll", out _adlLib) &&
            !NativeLibrary.TryLoad("atiadlxy.dll", out _adlLib))
            return false;
        if (AdlMainCreate(AdlAlloc, 1, out _adlContext) != 0) { ShutdownAdl(); return false; }

        int count = 0;
        if (AdlAdapterCount(_adlContext, out count) != 0 || count <= 0) { ShutdownAdl(); return false; }

        int size = Marshal.SizeOf(typeof(AdlAdapterInfo));
        IntPtr buffer = Marshal.AllocHGlobal(size * count);
        try
        {
            var empty = new AdlAdapterInfo[count];
            for (int i = 0; i < count; i++)
            {
                empty[i].Size = size;
                Marshal.StructureToPtr(empty[i], IntPtr.Add(buffer, i * size), false);
            }
            if (AdlGetAdapterInfo(_adlContext, buffer, size * count) != 0) { ShutdownAdl(); return false; }

            var candidates = new List<AdlCandidate>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<AdlAdapterInfo>(IntPtr.Add(buffer, i * size));
                int active = 0;
                AdlAdapterActive(_adlContext, i, out active);
                candidates.Add(new AdlCandidate { Index = i, Info = info, Active = active != 0 });
            }

            // Erst die GPU suchen, die Vulkan rendert (PCI-ID/Name). Nur wenn es keine Übereinstimmung
            // gibt, auf aktive Adapter bzw. alle Adapter zurückfallen. ADL listet pro Display einen
            // Eintrag – mehrfach gleiche PCI-ID ist also normal.
            var pool = candidates.Where(c => MatchesPreferred(c, preferredName, preferredDeviceId)).ToList();
            if (pool.Count == 0) pool = candidates.Where(c => c.Active).ToList();
            if (pool.Count == 0) pool = candidates;

            AdlCandidate best = null;
            int bestScore = int.MinValue;
            foreach (var c in pool)
            {
                uint vendor = ParseUdidHex(c.Info.UDID, "VEN_");
                uint device = ParseUdidHex(c.Info.UDID, "DEV_");
                if (vendor != 0 && vendor != 0x1002) continue;   // nur AMD-Adapter

                int score = c.Active ? 100 : 0;
                if (preferredDeviceId != 0 && device == preferredDeviceId) score += 10000;
                if (!string.IsNullOrEmpty(preferredName) && c.Info.AdapterName != null &&
                    c.Info.AdapterName.IndexOf(preferredName, StringComparison.OrdinalIgnoreCase) >= 0) score += 1000;
                if (AdlMemoryInfo(_adlContext, c.Index, out var mem) == 0 && mem.MemorySize > 0)
                    score += (int)System.Math.Min(200, mem.MemorySize / (1024L * 1024L * 1024L));   // diskret > iGPU
                if (score > bestScore) { bestScore = score; best = c; }
            }
            if (best == null) { ShutdownAdl(); return false; }

            _adlAdapter = best.Index;
            Source = "AMD ADL2: " + (best.Info.AdapterName ?? ("adapter " + best.Index));
            _backend = Backend.Adl2;
            Available = true;
            MemUtilAvailable = false;
            Sample();
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool MatchesPreferred(AdlCandidate c, string name, uint deviceId)
    {
        if (string.IsNullOrEmpty(name) && deviceId == 0) return false;
        if (deviceId != 0 && ParseUdidHex(c.Info.UDID, "DEV_") == deviceId) return true;
        return !string.IsNullOrEmpty(name) && c.Info.AdapterName != null &&
               c.Info.AdapterName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static uint ParseUdidHex(string udid, string key)
    {
        if (string.IsNullOrEmpty(udid)) return 0;
        int p = udid.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (p < 0) return 0;
        p += key.Length;
        uint value = 0;
        while (p < udid.Length && Uri.IsHexDigit(udid[p]))
        {
            value = value * 16 + (uint)Convert.ToInt32(udid[p].ToString(), 16);
            p++;
        }
        return value;
    }

    private void SampleAdl2()
    {
        bool haveClocks = false;

        // RDNA/Vega (ADL Overdrive 8) liefert keine OD-N-PerformanceStatus mehr; die Werte
        // kommen aus dem Performance-Monitoring-Log (PMLog). Werte sind dort direkt MHz/°C/%.
        AdlPmLogDataOutput log = new();
        if (AdlPmLog(_adlContext, _adlAdapter, out log) == 0 && log.sensors != null)
        {
            CoreClockMHz = AdlSensor(log, 1);    // ADL_PMLOG_CLK_GFXCLK
            MemClockMHz = AdlSensor(log, 2);     // ADL_PMLOG_CLK_MEMCLK
            GpuUtil = AdlSensor(log, 19);        // ADL_PMLOG_INFO_ACTIVITY_GFX
            MemUtil = AdlSensor(log, 20);        // ADL_PMLOG_INFO_ACTIVITY_MEM
            MemUtilAvailable = AdlSensorSupported(log, 20);
            TempC = AdlSensor(log, 8);           // ADL_PMLOG_TEMPERATURE_EDGE
            if (TempC == 0) TempC = AdlSensor(log, 28);  // ADL_PMLOG_TEMPERATURE_GFX
            TempHotspotC = AdlSensor(log, 27);   // ADL_PMLOG_TEMPERATURE_HOTSPOT
            PowerW = AdlSensor(log, 73);         // ADL_PMLOG_BOARD_POWER
            haveClocks = CoreClockMHz != 0 || MemClockMHz != 0;
        }

        // Fallback für ältere GCN-Karten: OverdriveN (10 kHz-Einheiten).
        if (!haveClocks && AdlOverdriveNStatus(_adlContext, _adlAdapter, out var odn) == 0)
        {
            CoreClockMHz = From10Khz(odn.CoreClock != 0 ? odn.CoreClock : odn.GfxClock);
            MemClockMHz = From10Khz(odn.MemoryClock);
            GpuUtil = Percent(odn.ActivityPercent);
            haveClocks = CoreClockMHz != 0 || MemClockMHz != 0;
        }

        // Letzter Fallback: Overdrive5.
        if (!haveClocks)
        {
            var activity = new AdlOdActivity { Size = Marshal.SizeOf(typeof(AdlOdActivity)) };
            if (AdlOverdrive5Activity(_adlContext, _adlAdapter, ref activity) == 0)
            {
                CoreClockMHz = From10Khz(activity.EngineClock);
                MemClockMHz = From10Khz(activity.MemoryClock);
                GpuUtil = Percent(activity.ActivityPercent);
            }
        }

        if (TempC == 0)
        {
            if (AdlOverdriveNTemperature(_adlContext, _adlAdapter, 1, out int milli) == 0)
                TempC = (uint)System.Math.Max(0, milli / 1000);
            else
            {
                var temp = new AdlTemperature { Size = Marshal.SizeOf(typeof(AdlTemperature)) };
                if (AdlOverdrive5Temperature(_adlContext, _adlAdapter, 0, ref temp) == 0)
                    TempC = (uint)System.Math.Max(0, temp.Temperature / 1000);
            }
        }

        if (AdlDedicatedVramUsage(_adlContext, _adlAdapter, out int usedMb) == 0 && usedMb >= 0)
            VramUsed = (ulong)usedMb * 1024UL * 1024UL;
        if (AdlMemoryInfo(_adlContext, _adlAdapter, out var mem) == 0 && mem.MemorySize > 0)
            VramTotal = (ulong)mem.MemorySize;
    }

    private static uint AdlSensor(AdlPmLogDataOutput log, int id) =>
        AdlSensorSupported(log, id) ? (uint)System.Math.Max(0, log.sensors[id].value) : 0;

    private static bool AdlSensorSupported(AdlPmLogDataOutput log, int id) =>
        log.sensors != null && id >= 0 && id < log.sensors.Length && log.sensors[id].supported != 0;

    private static uint From10Khz(int value) => value > 0 ? (uint)(value / 100) : 0;
    private static uint Percent(int value) => (uint)System.Math.Clamp(value, 0, 100);

    private void ShutdownAdl()
    {
        if (_adlContext != IntPtr.Zero) { try { AdlMainDestroy(_adlContext); } catch { } _adlContext = IntPtr.Zero; }
        if (_adlLib != IntPtr.Zero) { try { NativeLibrary.Free(_adlLib); } catch { } _adlLib = IntPtr.Zero; }
        _adlAdapter = -1;
        GC.KeepAlive(AdlAlloc);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr AdlMalloc(int size);
    private static readonly AdlMalloc AdlAlloc = Marshal.AllocHGlobal;

    private const int AdlMaxPath = 256;
    private const int AdlPmlogMaxSensors = 256;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct AdlAdapterInfo
    {
        public int Size, AdapterIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)] public string UDID;
        public int BusNumber, DeviceNumber, FunctionNumber, VendorID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)] public string AdapterName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)] public string DisplayName;
        public int Present, Exist;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)] public string DriverPath;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)] public string DriverPathExt;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)] public string PNPString;
        public int OSDisplayIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdlSensorData { public int supported, value; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdlPmLogDataOutput
    {
        public int size;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = AdlPmlogMaxSensors)] public AdlSensorData[] sensors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdlOdNPerformanceStatus
    {
        public int CoreClock, MemoryClock, DcefClock, GfxClock, UvdClock, VceClock, ActivityPercent;
        public int CoreLevel, MemoryLevel, DcefLevel, GfxLevel, UvdLevel, VceLevel;
        public int BusSpeed, BusLanes, MaxBusLanes, Vddc, Vddci;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdlOdActivity
    {
        public int Size, EngineClock, MemoryClock, Vddc, ActivityPercent, CurrentLevel;
        public int BusSpeed, BusLanes, MaxBusLanes, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdlTemperature { public int Size, Temperature; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct AdlMemoryInfoX4
    {
        public long MemorySize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)] public string MemoryType;
        public long MemoryBandwidth, HyperMemorySize, InvisibleMemorySize, VisibleMemorySize;
        public long VramVendorRevId, MemoryBandwidthX2, MemoryBitRateX2;
    }

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_Main_Control_Create(AdlMalloc callback, int connectedAdapters, out IntPtr context);
    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_Main_Control_Destroy(IntPtr context);
    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, out int count);
    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_Adapter_AdapterInfo_Get(IntPtr context, IntPtr info, int size);
    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_Adapter_Active_Get(IntPtr context, int adapterIndex, out int status);
    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_New_QueryPMLogData_Get(IntPtr context, int adapterIndex, out AdlPmLogDataOutput log);
    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_OverdriveN_PerformanceStatus_Get(IntPtr context, int adapterIndex, out AdlOdNPerformanceStatus status);
    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_OverdriveN_Temperature_Get(IntPtr context, int adapterIndex, int temperatureType, out int milliDegrees);
    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_Overdrive5_CurrentActivity_Get(IntPtr context, int adapterIndex, ref AdlOdActivity activity);
    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_Overdrive5_Temperature_Get(IntPtr context, int adapterIndex, int thermalController, ref AdlTemperature temperature);
    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_Adapter_DedicatedVRAMUsage_Get(IntPtr context, int adapterIndex, out int usageMb);
    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADL2_Adapter_MemoryInfoX4_Get(IntPtr context, int adapterIndex, out AdlMemoryInfoX4 memory);

    // Kurze Namen, damit die Sample-Funktion lesbar bleibt.
    private static int AdlMainCreate(AdlMalloc cb, int connected, out IntPtr ctx) => ADL2_Main_Control_Create(cb, connected, out ctx);
    private static int AdlMainDestroy(IntPtr ctx) => ADL2_Main_Control_Destroy(ctx);
    private static int AdlAdapterCount(IntPtr ctx, out int count) => ADL2_Adapter_NumberOfAdapters_Get(ctx, out count);
    private static int AdlGetAdapterInfo(IntPtr ctx, IntPtr info, int size) => ADL2_Adapter_AdapterInfo_Get(ctx, info, size);
    private static int AdlAdapterActive(IntPtr ctx, int index, out int active) => ADL2_Adapter_Active_Get(ctx, index, out active);
    private static int AdlPmLog(IntPtr ctx, int index, out AdlPmLogDataOutput log) => ADL2_New_QueryPMLogData_Get(ctx, index, out log);
    private static int AdlOverdriveNStatus(IntPtr ctx, int index, out AdlOdNPerformanceStatus status) => ADL2_OverdriveN_PerformanceStatus_Get(ctx, index, out status);
    private static int AdlOverdriveNTemperature(IntPtr ctx, int index, int type, out int temp) => ADL2_OverdriveN_Temperature_Get(ctx, index, type, out temp);
    private static int AdlOverdrive5Activity(IntPtr ctx, int index, ref AdlOdActivity activity) => ADL2_Overdrive5_CurrentActivity_Get(ctx, index, ref activity);
    private static int AdlOverdrive5Temperature(IntPtr ctx, int index, int controller, ref AdlTemperature temp) => ADL2_Overdrive5_Temperature_Get(ctx, index, controller, ref temp);
    private static int AdlDedicatedVramUsage(IntPtr ctx, int index, out int mb) => ADL2_Adapter_DedicatedVRAMUsage_Get(ctx, index, out mb);
    private static int AdlMemoryInfo(IntPtr ctx, int index, out AdlMemoryInfoX4 mem) => ADL2_Adapter_MemoryInfoX4_Get(ctx, index, out mem);
}
