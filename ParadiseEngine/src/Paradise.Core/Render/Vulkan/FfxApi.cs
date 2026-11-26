using System.Runtime.InteropServices;

namespace Paradise.Core.Render.Vulkan;

/// <summary>
/// Minimaler C-ABI-Zugriff auf die AMD FidelityFX SDK v2.4 ffx_api DLLs.
/// Geladen wird der Loader (amd_fidelityfx_loader_vk.dll); er findet den Upscaler-Provider im selben Ordner.
/// Alle Strukturen sind x64-Layouts aus ffx_api.h / ffx_api_types.h / ffx_api_vk.h / ffx_upscale.h.
/// </summary>
internal static unsafe class FfxApi
{
    internal const string LoaderDll = "amd_fidelityfx_loader_vk.dll";

    internal const ulong CreateContextUpscaleType = 0x00010000UL;
    internal const ulong DispatchUpscaleType = 0x00010001UL;
    internal const ulong CreateContextBackendVk = 0x01000003UL;
    internal const ulong CreateContextUpscaleVersion = 0x0001000bUL;
    internal const ulong DescOverrideVersion = 5UL;
    internal const ulong QueryGetVersionsType = 4UL;
    internal const ulong QueryUpscaleJitterPhaseCount = 0x00010004UL;
    internal const ulong QueryUpscaleJitterOffset = 0x00010005UL;

    internal const uint UpscalerVersion = (4u << 22) | (1u << 12) | 2u; // FFX_UPSCALER_VERSION 4.1.2

    internal const uint CreateAutoExposure = 1u << 5;
    internal const uint CreateDepthInverted = 1u << 3;
    internal const uint CreateDepthInfinite = 1u << 4;
    internal const uint CreateNonLinearColorspace = 1u << 8;
    internal const uint DispatchNonLinearColorSrgb = 1u << 1;

    internal const uint ResourceUsageReadonly = 0;
    internal const uint ResourceUsageUav = 1u << 1;
    internal const uint ResourceUsageDepthTarget = 1u << 2;

    internal const uint StatePixelComputeRead = (1u << 2) | (1u << 3);
    internal const uint StateUav = 1u << 1;
    internal const uint StateTransferSrc = 1u << 4;

    internal const uint TypeTexture2D = 2;
    internal const uint FormatRgba8Unorm = 10;
    internal const uint FormatRgba8Srgb = 12;
    internal const uint FormatBgra8Unorm = 14;
    internal const uint FormatBgra8Srgb = 15;
    internal const uint FormatR16G16Float = 18;
    internal const uint FormatR32Float = 28;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Header
    {
        public ulong Type;
        public nint PNext;
    }

    [StructLayout(LayoutKind.Sequential, Size = 32)]
    internal struct ResourceDescription
    {
        public uint Type, Format, Width, Height, Depth, MipCount, Flags, Usage;
    }

    [StructLayout(LayoutKind.Sequential, Size = 48)]
    internal struct Resource
    {
        public ulong Image;
        public ResourceDescription Description;
        public uint State;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Dimensions2D { public uint Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FloatCoords2D { public float X, Y; }

    /// <summary>VkDeviceContext aus ffx_api_vk.h (24 Byte, im Backend-Deskriptor by value).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceContextVk
    {
        public ulong Device;
        public ulong PhysicalDevice;
        public ulong GetDeviceProcAddr;
    }

    /// <summary>ffxCreateBackendVKDesc: Header + VkDeviceContext by value (40 Byte).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CreateBackendVk
    {
        public Header Header;
        public DeviceContextVk DeviceContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CreateContextUpscale
    {
        public Header Header;
        public uint Flags;
        // C-Layout: maxRenderSize folgt direkt auf flags (Offset 20), kein Padding dazwischen.
        public Dimensions2D MaxRenderSize;
        public Dimensions2D MaxUpscaleSize;
        public nint FpMessage;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CreateContextVersion
    {
        public Header Header;
        public uint Version;
        private uint _pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct OverrideVersion
    {
        public Header Header;
        public ulong VersionId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct QueryGetVersions
    {
        public Header Header;
        public ulong CreateDescType;
        public nint Device;
        public nint OutputCount;
        public nint VersionIds;
        public nint VersionNames;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct QueryJitterPhaseCount
    {
        public Header Header;
        public uint RenderWidth, DisplayWidth;
        public nint OutPhaseCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct QueryJitterOffset
    {
        public Header Header;
        public int Index, PhaseCount;
        public nint OutX, OutY;
    }

    /// <summary>x64-Layout von ffxDispatchDescUpscale (432 Byte). bool-Felder sind C-bool = 4 Byte.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 432)]
    internal struct DispatchUpscale
    {
        public Header Header;
        public ulong CommandList;
        public Resource Color, Depth, MotionVectors, Exposure, Reactive, Transparency, Output;
        public FloatCoords2D JitterOffset, MotionVectorScale;
        public Dimensions2D RenderSize, UpscaleSize;
        public uint EnableSharpening;
        public float Sharpness, FrameTimeDelta, PreExposure;
        public uint Reset;
        public float CameraNear, CameraFar, CameraFovAngleVertical, ViewSpaceToMetersFactor;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint CreateContextFn(nint* context, nint desc, nint callbacks);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint DestroyContextFn(nint* context, nint callbacks);
    // ffxQuery/ffxDispatch/ffxConfigure nehmen ffxContext* (Zeiger auf das Handle), nicht den Handle-Wert.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint QueryFn(nint* context, nint desc);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint DispatchFn(nint* context, nint desc);

    private static nint _library;
    private static CreateContextFn? _create;
    private static DestroyContextFn? _destroy;
    private static QueryFn? _query;
    private static DispatchFn? _dispatch;

    internal static string? InitError { get; private set; }
    internal static bool Available => _library != 0 && _create != null && _destroy != null && _query != null && _dispatch != null;

    internal static bool Init()
    {
        if (Available) return true;
        if (_library != 0) return false;
        try
        {
            string? path = FindLoader();
            if (path == null) { InitError = LoaderDll + " nicht gefunden"; return false; }
            _library = NativeLibrary.Load(path);
            _create = Get<CreateContextFn>("ffxCreateContext");
            _destroy = Get<DestroyContextFn>("ffxDestroyContext");
            _query = Get<QueryFn>("ffxQuery");
            _dispatch = Get<DispatchFn>("ffxDispatch");
            if (!Available) throw new Exception("ffx_api Exporte fehlen");
            return true;
        }
        catch (Exception e)
        {
            InitError = e.Message;
            if (_library != 0) NativeLibrary.Free(_library);
            _library = 0; _create = null; _destroy = null; _query = null; _dispatch = null;
            return false;
        }
    }

    private static T Get<T>(string name) where T : Delegate
    {
        nint p = NativeLibrary.GetExport(_library, name);
        if (p == 0) throw new Exception("ffx_api Export fehlt: " + name);
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    private static string? FindLoader()
    {
        string local = Path.Combine(AppContext.BaseDirectory, LoaderDll);
        if (File.Exists(local)) return local;
        // Im Build liegt der Loader in third_party\fidelityfx (neben den Projekten), nicht in bin\.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "third_party", "fidelityfx", LoaderDll);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    internal static uint CreateContext(nint* context, nint desc) => _create!(context, desc, 0);
    internal static uint DestroyContext(nint* context) => _destroy!(context, 0);
    internal static uint Query(nint context, nint desc)
    {
        // Globale Queries (z. B. Versionsliste) erwarten einen NULL-Zeiger, nicht einen Zeiger auf ein 0-Handle.
        if (context == 0) return _query!(null, desc);
        nint ctx = context;
        return _query!(&ctx, desc);
    }

    internal static uint Dispatch(nint context, nint desc)
    {
        nint ctx = context;
        return _dispatch!(&ctx, desc);
    }

    /// <summary>Höchste FSR-3-Version des Loaders (z. B. „3.1.6"); 0, wenn keine gefunden wird.
    /// FSR 4 (4.1.2) wird von der 3.x-Upscaler-DLL nicht angeboten und darum bewusst ausgeschlossen.</summary>
    internal static ulong FindFsr3VersionId(nint deviceContext, out string? versionName)
    {
        versionName = null;
        ulong count = 0;
        var q = new QueryGetVersions
        {
            Header = new Header { Type = QueryGetVersionsType },
            CreateDescType = CreateContextUpscaleType,
            Device = deviceContext,
            OutputCount = (nint)(&count),
        };
        if (Query(0, (nint)(&q)) != 0 || count == 0) return 0;
        ulong* ids = (ulong*)NativeMemory.Alloc((nuint)count * (nuint)sizeof(ulong));
        nint* names = (nint*)NativeMemory.Alloc((nuint)count * (nuint)sizeof(nint));
        try
        {
            q.VersionIds = (nint)ids;
            q.VersionNames = (nint)names;
            if (Query(0, (nint)(&q)) != 0) return 0;
            ulong bestId = 0;
            int bestMajor = 0, bestMinor = 0, bestPatch = 0;
            string? bestName = null;
            for (nuint i = 0; i < count; i++)
            {
                string? name = Marshal.PtrToStringUTF8(names[i]);
                if (name == null) continue;
                // Der Loader liefert z. B. "3.1.6" und "2.3.4" (ohne "FSR" im Namen).
                var parts = name.Split('.');
                if (parts.Length < 2 || !int.TryParse(parts[0], out int major) || major != 3
                    || !int.TryParse(parts[1], out int minor)) continue;
                int patch = parts.Length > 2 && int.TryParse(parts[2], out int p) ? p : 0;
                if (major > bestMajor || (major == bestMajor && (minor > bestMinor
                    || (minor == bestMinor && patch > bestPatch))))
                {
                    bestMajor = major; bestMinor = minor; bestPatch = patch;
                    bestId = ids[i]; bestName = name;
                }
            }
            versionName = bestName;
            return bestId;
        }
        finally
        {
            NativeMemory.Free(ids);
            NativeMemory.Free(names);
        }
    }

    internal static bool QueryJitter(nint context, uint renderWidth, uint displayWidth, int index, out float x, out float y)
    {
        x = y = 0f;
        int phases = 0;
        var phaseDesc = new QueryJitterPhaseCount
        {
            Header = new Header { Type = QueryUpscaleJitterPhaseCount },
            RenderWidth = renderWidth,
            DisplayWidth = displayWidth,
            OutPhaseCount = (nint)(&phases),
        };
        if (Query(context, (nint)(&phaseDesc)) != 0 || phases <= 0) return false;
        float qx = 0f, qy = 0f;
        var offsetDesc = new QueryJitterOffset
        {
            Header = new Header { Type = QueryUpscaleJitterOffset },
            Index = index,
            PhaseCount = phases,
            OutX = (nint)(&qx),
            OutY = (nint)(&qy),
        };
        if (Query(context, (nint)(&offsetDesc)) != 0) return false;
        x = qx; y = qy;
        return true;
    }
}
