using Paradise.Core.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Paradise.Core.Formats;

/// <summary>
/// Decode images (JPEG, PNG, BMP, etc.) using the Windows Imaging Component—e.g., the original loading images in frontend/tips_*.bnk
/// Result: RGBA8, row 0 = top.
/// s97: Direct vtable calls with HRESULT checking instead of [ComImport] interfaces (the .NET COM marshaller caused the game to
/// crash or hang during decoding). Each step checks its result; error = null + log, never a crash due to invalid pointers.
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe class WicImage
{
    static readonly Guid ClsidFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");   // CLSID_WICImagingFactory
    static readonly Guid IidFactory = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");     // IID_IWICImagingFactory
    static readonly Guid FmtRGBA = new("f5c7ad2d-6a8d-43dd-a7a8-a29935261ae9");       // GUID_WICPixelFormat32bppRGBA (ab Windows 8)
    static readonly Guid FmtBGRA = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");       // GUID_WICPixelFormat32bppBGRA (Fallback)

    const int COINIT_MULTITHREADED = 0, CLSCTX_INPROC_SERVER = 1;
    const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved, int coInit);
    [DllImport("ole32.dll")] static extern int CoCreateInstance(in Guid clsid, IntPtr outer, int ctx, in Guid iid, out IntPtr obj);
    [DllImport("shlwapi.dll", EntryPoint = "SHCreateMemStream")] static extern IntPtr SHCreateMemStream(byte* p, uint cb);

    // Vtable-Plätze (wincodec.h; 0..2 = IUnknown)
    const int Release = 2;
    const int Factory_CreateDecoderFromStream = 4, Factory_CreateFormatConverter = 10;
    const int Decoder_GetFrame = 13;
    const int Source_GetSize = 3, Source_CopyPixels = 7, Converter_Initialize = 8;

    static void** Vtbl(IntPtr o) => *(void***)o;
    static void Rel(IntPtr o) { if (o != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(o)[Release])(o); }

    /// <summary>Bilddaten dekodieren; null bei Fehler.</summary>
    public static byte[]? Decode(byte[] data, out int width, out int height)
    {
        width = height = 0;
        if (data == null || data.Length == 0) return null;
        IntPtr factory = IntPtr.Zero, stream = IntPtr.Zero, decoder = IntPtr.Zero, frame = IntPtr.Zero, conv = IntPtr.Zero;
        string step = "CoInitializeEx";
        try
        {
            int hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);   // S_OK/S_FALSE/RPC_E_CHANGED_MODE: COM ist nutzbar
            if (hr < 0 && hr != RPC_E_CHANGED_MODE) return Fail(step, hr);

            step = "CoCreateInstance";
            hr = CoCreateInstance(ClsidFactory, IntPtr.Zero, CLSCTX_INPROC_SERVER, IidFactory, out factory);
            if (hr < 0 || factory == IntPtr.Zero) return Fail(step, hr);

            step = "SHCreateMemStream";
            fixed (byte* p = data) stream = SHCreateMemStream(p, (uint)data.Length);   // kopiert die Daten
            if (stream == IntPtr.Zero) return Fail(step, 0);

            step = "CreateDecoderFromStream";
            IntPtr d;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, int, IntPtr*, int>)Vtbl(factory)[Factory_CreateDecoderFromStream])(factory, stream, null, 0, &d);
            decoder = d;
            if (hr < 0 || decoder == IntPtr.Zero) return Fail(step, hr);

            step = "GetFrame";
            IntPtr f;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vtbl(decoder)[Decoder_GetFrame])(decoder, 0, &f);
            frame = f;
            if (hr < 0 || frame == IntPtr.Zero) return Fail(step, hr);

            step = "CreateFormatConverter";
            IntPtr c;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(factory)[Factory_CreateFormatConverter])(factory, &c);
            conv = c;
            if (hr < 0 || conv == IntPtr.Zero) return Fail(step, hr);

            step = "Initialize";
            var init = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, int, IntPtr, double, int, int>)Vtbl(conv)[Converter_Initialize];
            Guid fmt = FmtRGBA; bool bgra = false;
            hr = init(conv, frame, &fmt, 0, IntPtr.Zero, 0.0, 0);
            if (hr < 0)
            {
                // ältere WIC ohne 32bppRGBA: BGRA holen und Kanäle tauschen (neuer Konverter, der alte ist verbraucht)
                Rel(conv); conv = IntPtr.Zero;
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(factory)[Factory_CreateFormatConverter])(factory, &c);
                conv = c;
                if (hr < 0 || conv == IntPtr.Zero) return Fail(step + " (BGRA)", hr);
                fmt = FmtBGRA; bgra = true;
                init = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, int, IntPtr, double, int, int>)Vtbl(conv)[Converter_Initialize];
                hr = init(conv, frame, &fmt, 0, IntPtr.Zero, 0.0, 0);
                if (hr < 0) return Fail(step + " (BGRA)", hr);
            }

            step = "GetSize";
            uint w, h;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint*, uint*, int>)Vtbl(conv)[Source_GetSize])(conv, &w, &h);
            if (hr < 0 || w == 0 || h == 0 || w > 16384 || h > 16384) return Fail(step + $" {w}x{h}", hr);

            step = "CopyPixels";
            var px = new byte[checked((int)(w * h * 4))];
            fixed (byte* p = px)
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, void*, uint, uint, byte*, int>)Vtbl(conv)[Source_CopyPixels])(conv, null, w * 4, (uint)px.Length, p);
            if (hr < 0) return Fail(step, hr);
            if (bgra) for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);
            width = (int)w; height = (int)h;
            return px;
        }
        catch (Exception ex)
        {
            Log.Error($"Image (WIC) cannot be decoded at {step}: {ex.Message}");
            return null;
        }
        finally
        {
            Rel(conv); Rel(frame); Rel(decoder); Rel(stream); Rel(factory);
        }
    }

    static byte[]? Fail(string step, int hr)
    {
        Log.Warn($"Image (WIC) cannot be decoded: {step} HRESULT 0x{hr:X8}");
        return null;
    }
}