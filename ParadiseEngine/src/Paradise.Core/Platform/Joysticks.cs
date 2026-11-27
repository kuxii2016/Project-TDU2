using Paradise.Core.Diagnostics;
using System.Runtime.InteropServices;

// Hinweis: XInput-Vibration und DirectInput-Force-Feedback liegen in derselben Plattform-Schicht wie die Achsen,
// damit Gerätelisten und COM-Schnittstellen nur einmal verwaltet werden.

namespace Paradise.Core.Platform;

/// <summary>
/// Gamepads und Lenkräder (Ersatz für Unitys Input.GetJoystickNames / Input.GetAxisRaw / Input.GetKey(JoystickNButtonM),
/// die TDU2InputDevices, TDU2Nav und TDU2Controls benutzen). Zwei Wege wie unter Windows üblich:
///  * XInput (Xbox-Pads): Unity-Belegung unter Windows – Achse 1/2 linker Stick (oben = −), 3 Trigger (links +, rechts −),
///    4/5 rechter Stick (oben = −), 6/7 Steuerkreuz (rechts/oben = +), 9/10 linker/rechter Trigger 0..1;
///    Knöpfe 0 A, 1 B, 2 X, 3 Y, 4 LB, 5 RB, 6 Back, 7 Start, 8 linker Stick, 9 rechter Stick.
///  * DirectInput 8 (alle anderen Geräte, v.a. Lenkräder; XInput-Geräte werden dort übersprungen, sonst doppelt):
///    Achsen in der Reihenfolge der Original-Kanäle (TDU2InputDevices.ParseChannel): 1 X, 2 Y, 3 Z, 4 Rx, 5 Ry, 6 Rz,
///    7 Schieberegler 0, 8 Schieberegler 1, 9/10 erster Hut-Schalter (rechts/oben = +). Werte −1..+1; Knöpfe 0..19.
/// Joystick-Nummern 1..8 wie Unity (Index in <see cref="Names"/> + 1). Geräte werden alle 2 s neu gesucht.
/// Ohne Gerät/Treiber: leere Namen, alle Werte 0.
/// </summary>
public static class Joysticks
{
    public const int MaxJoysticks = 8, MaxAxes = 10, MaxButtons = 20;

    sealed class Device
    {
        public string Name = "";
        public int XUser = -1;              // XInput-Benutzer 0..3, sonst −1
        public IntPtr Di;                   // IDirectInputDevice8W
        public Guid Instance;
        public readonly float[] Axis = new float[MaxAxes + 1];
        public readonly bool[] Btn = new bool[MaxButtons], Prev = new bool[MaxButtons];
    }

    static readonly Device?[] _dev = new Device?[MaxJoysticks];
    static string[] _names = Array.Empty<string>();
    static long _frame = -1;
    static double _nextScan;
    static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    static bool _xiOk = true, _diOk = true;
    static IntPtr _di;   // IDirectInput8W
    static IntPtr _format, _objs;
    static readonly List<IntPtr> _guids = new();

    /// <summary>Gerätenamen (Index + 1 = Joystick-Nummer), leere Einträge = frei – Unity Input.GetJoystickNames().</summary>
    public static string[] Names { get { Poll(); return _names; } }

    /// <summary>Fenster im Vordergrund (Unity Application.isFocused) – Pause/Vibration/FFB werden sonst beim Alt-Tab nicht gestoppt.</summary>
    public static bool IsFocused
    {
        get
        {
            var w = Win32Window.Current;
            if (w == null || w.Handle == IntPtr.Zero) return true;
            try { return GetForegroundWindow() == w.Handle; }
            catch (Exception) { return true; }
        }
    }

    /// <summary>Joystick ist ein XInput-Gamepad (Vibration möglich), kein DirectInput-Lenkrad.</summary>
    public static bool IsGamepad(int joy) { Poll(); var d = Get(joy); return d != null && d.XUser >= 0; }

    /// <summary>
    /// Vibration eines XInput-Pads setzen (joy = 0: erstes verbundenes Pad). Stärken 0..1, low = großer Motor.
    /// false = kein Pad/XInput nicht verfügbar – der Aufrufer soll dann später erneut suchen.
    /// </summary>
    public static bool SetVibration(int joy, float low, float high)
    {
        Poll();
        if (joy <= 0)
        {
            joy = 0;
            for (int j = 1; j <= MaxJoysticks; j++) if (IsGamepad(j)) { joy = j; break; }
            if (joy == 0) return false;
        }
        var d = Get(joy);
        if (d == null || d.XUser < 0) return false;
        return XInputSetState((uint)d.XUser, low, high) == 0;
    }

    /// <summary>Achse 1..10 eines Joysticks 1..8 (−1..+1) – Unity Input.GetAxisRaw("… Joy&lt;j&gt; Axis &lt;i&gt;").</summary>
    public static float Axis(int joy, int axis) { Poll(); var d = Get(joy); return d != null && axis >= 1 && axis <= MaxAxes ? d.Axis[axis] : 0f; }

    /// <summary>Knopf gehalten – Unity Input.GetKey(Joystick&lt;j&gt;Button&lt;b&gt;).</summary>
    public static bool Button(int joy, int button) { Poll(); var d = Get(joy); return d != null && button >= 0 && button < MaxButtons && d.Btn[button]; }

    /// <summary>Knopf in diesem Bild gedrückt – Unity Input.GetKeyDown(Joystick&lt;j&gt;Button&lt;b&gt;).</summary>
    public static bool ButtonDown(int joy, int button) { Poll(); var d = Get(joy); return d != null && button >= 0 && button < MaxButtons && d.Btn[button] && !d.Prev[button]; }

    /// <summary>Irgendein Knopf in diesem Bild gedrückt: (Joystick, Knopf), sonst (0, −1).</summary>
    public static (int joy, int button) AnyButtonDown()
    {
        Poll();
        for (int j = 0; j < MaxJoysticks; j++)
        {
            var d = _dev[j];
            if (d == null) continue;
            for (int b = 0; b < MaxButtons; b++) if (d.Btn[b] && !d.Prev[b]) return (j + 1, b);
        }
        return (0, -1);
    }

    static Device? Get(int joy) => joy >= 1 && joy <= MaxJoysticks ? _dev[joy - 1] : null;

    /// <summary>Bildnummer setzen (einmal je Bild, vor dem Lesen) – mehrfache Abfragen im selben Bild lesen nur einmal.</summary>
    public static long Frame;

    static void Poll()
    {
        if (_frame == Frame) return;
        _frame = Frame;
        double t = _clock.Elapsed.TotalSeconds;
        // Geräte suchen im Hintergrund: DirectInput EnumDevices/XInputGetState freier Plätze dauern bis ~200 ms
        // (vorher alle 2 s ein Ruckler im Hauptthread)
        if (_scanTask == null && t >= _nextScan) { _nextScan = t + 2.0; _scanTask = Task.Run(FindDevices); }
        if (_scanTask != null && _scanTask.IsCompleted)
        {
            var task = _scanTask; _scanTask = null;
            if (!task.IsFaulted) Apply(task.Result);
        }
        foreach (var d in _dev)
        {
            if (d == null) continue;
            Array.Copy(d.Btn, d.Prev, MaxButtons);
            try
            {
                if (d.XUser >= 0) ReadXInput(d);
                else ReadDirectInput(d);
            }
            catch (Exception) { Array.Clear(d.Axis); Array.Clear(d.Btn); }
        }
    }

    // ------------------------------------------------------------------ Geräte suchen
    static Task<List<(string name, int xuser, Guid inst)>>? _scanTask;
    static readonly object _diLock = new();

    /// <summary>Angeschlossene Geräte ermitteln (Hintergrund-Thread).</summary>
    static List<(string name, int xuser, Guid inst)> FindDevices()
    {
        var found = new List<(string name, int xuser, Guid inst)>();
        if (_xiOk)
        {
            for (int u = 0; u < 4; u++)
            {
                try { if (XInputGetState((uint)u, out _) == 0) found.Add(("Controller (XBOX 360 For Windows)", u, Guid.Empty)); }
                catch (Exception) { _xiOk = false; Log.Warn("XInput nicht verfügbar – nur DirectInput-Geräte"); break; }
            }
        }
        if (_diOk)
        {
            try { lock (_diLock) foreach (var (n, g) in EnumDirectInput()) found.Add((n, -1, g)); }
            catch (Exception ex) { _diOk = false; Log.Warn("DirectInput nicht verfügbar: " + ex.Message); }
        }
        return found;
    }

    /// <summary>Ergebnis der Suche übernehmen (Hauptthread): Plätze behalten, neue Geräte öffnen.</summary>
    static void Apply(List<(string name, int xuser, Guid inst)> found)
    {
        // Bestehende Geräte behalten (gleiche Nummer), verschwundene freigeben, neue in freie Plätze
        for (int j = 0; j < MaxJoysticks; j++)
        {
            var d = _dev[j];
            if (d == null) continue;
            bool still = found.Exists(f => d.XUser >= 0 ? f.xuser == d.XUser : f.xuser < 0 && f.inst == d.Instance);
            if (!still) { CloseDevice(d); _dev[j] = null; Log.Info($"Eingabegerät getrennt: \"{d.Name}\" (Joystick {j + 1})"); }
        }
        foreach (var f in found)
        {
            bool have = false;
            foreach (var d in _dev) if (d != null && (f.xuser >= 0 ? d.XUser == f.xuser : d.XUser < 0 && d.Instance == f.inst)) have = true;
            if (have) continue;
            int slot = Array.FindIndex(_dev, x => x == null);
            if (slot < 0) break;
            var nd = new Device { Name = f.name, XUser = f.xuser, Instance = f.inst };
            if (f.xuser < 0) { bool ok; lock (_diLock) ok = OpenDirectInput(nd); if (!ok) continue; }
            _dev[slot] = nd;
            Log.Info($"Eingabegerät: \"{nd.Name}\" (Joystick {slot + 1}, {(nd.XUser >= 0 ? "XInput " + nd.XUser : "DirectInput")})");
        }
        int last = -1;
        for (int j = 0; j < MaxJoysticks; j++) if (_dev[j] != null) last = j;
        var names = new string[last + 1];
        for (int j = 0; j <= last; j++) names[j] = _dev[j]?.Name ?? "";
        _names = names;
    }

    // ------------------------------------------------------------------ XInput
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_GAMEPAD { public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_VIBRATION { public ushort wLeftMotorSpeed, wRightMotorSpeed; }
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern int XInputGetState14(uint user, out XINPUT_STATE state);
    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] static extern int XInputGetState910(uint user, out XINPUT_STATE state);
    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")] static extern int XInputSetState14(uint user, ref XINPUT_VIBRATION vibration);
    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputSetState")] static extern int XInputSetState910(uint user, ref XINPUT_VIBRATION vibration);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    static bool _xi910;

    static int XInputGetState(uint user, out XINPUT_STATE s)
    {
        if (!_xi910)
        {
            try { return XInputGetState14(user, out s); }
            catch (DllNotFoundException) { _xi910 = true; }
            catch (EntryPointNotFoundException) { _xi910 = true; }
        }
        return XInputGetState910(user, out s);
    }

    static int XInputSetState(uint user, float low, float high)
    {
        var v = new XINPUT_VIBRATION
        {
            wLeftMotorSpeed = (ushort)(System.Math.Clamp(low, 0f, 1f) * 65535f),
            wRightMotorSpeed = (ushort)(System.Math.Clamp(high, 0f, 1f) * 65535f),
        };
        if (!_xi910)
        {
            try { return XInputSetState14(user, ref v); }
            catch (DllNotFoundException) { _xi910 = true; }
            catch (EntryPointNotFoundException) { _xi910 = true; }
        }
        return XInputSetState910(user, ref v);
    }

    static void ReadXInput(Device d)
    {
        if (XInputGetState((uint)d.XUser, out var s) != 0) { Array.Clear(d.Axis); Array.Clear(d.Btn); return; }
        var g = s.Gamepad;
        static float Th(short v) => System.Math.Clamp(v / 32767f, -1f, 1f);
        float lt = g.bLeftTrigger / 255f, rt = g.bRightTrigger / 255f;
        d.Axis[1] = Th(g.sThumbLX);
        d.Axis[2] = -Th(g.sThumbLY);       // Unity: oben = negativ
        d.Axis[3] = lt - rt;
        d.Axis[4] = Th(g.sThumbRX);
        d.Axis[5] = -Th(g.sThumbRY);
        ushort b = g.wButtons;
        d.Axis[6] = ((b & 0x0008) != 0 ? 1f : 0f) - ((b & 0x0004) != 0 ? 1f : 0f);   // rechts − links
        d.Axis[7] = ((b & 0x0001) != 0 ? 1f : 0f) - ((b & 0x0002) != 0 ? 1f : 0f);   // oben − unten
        d.Axis[8] = 0f;
        d.Axis[9] = lt;
        d.Axis[10] = rt;
        d.Btn[0] = (b & 0x1000) != 0;   // A
        d.Btn[1] = (b & 0x2000) != 0;   // B
        d.Btn[2] = (b & 0x4000) != 0;   // X
        d.Btn[3] = (b & 0x8000) != 0;   // Y
        d.Btn[4] = (b & 0x0100) != 0;   // LB
        d.Btn[5] = (b & 0x0200) != 0;   // RB
        d.Btn[6] = (b & 0x0020) != 0;   // Back
        d.Btn[7] = (b & 0x0010) != 0;   // Start
        d.Btn[8] = (b & 0x0040) != 0;   // linker Stick
        d.Btn[9] = (b & 0x0080) != 0;   // rechter Stick
        for (int i = 10; i < MaxButtons; i++) d.Btn[i] = false;
    }

    // ------------------------------------------------------------------ DirectInput 8 (COM über die Funktionstabellen, wie TDU2DirectInputFfb)
    const uint DIRECTINPUT_VERSION = 0x0800, DI8DEVCLASS_GAMECTRL = 4, DIEDFL_ATTACHEDONLY = 1;
    const uint DISCL_NONEXCLUSIVE = 2, DISCL_BACKGROUND = 8;
    static readonly Guid IID_IDirectInput8W = new("BF798031-483A-4DA2-AA99-5D64ED369700");

    [DllImport("dinput8.dll")] static extern int DirectInput8Create(IntPtr hinst, uint version, ref Guid riid, out IntPtr ppv, IntPtr outer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint ReleaseFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateDeviceFn(IntPtr self, ref Guid guid, out IntPtr dev, IntPtr outer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumDevicesFn(IntPtr self, uint devType, EnumCb cb, IntPtr pvRef, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumCb(IntPtr instance, IntPtr pvRef);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int NoArgFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PtrArgFn(IntPtr self, IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PropFn(IntPtr self, IntPtr guidProp, IntPtr header);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetCoopFn(IntPtr self, IntPtr hwnd, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetStateFn(IntPtr self, uint size, IntPtr data);

    static T Fn<T>(IntPtr com, int slot) where T : Delegate
    {
        IntPtr vtbl = Marshal.ReadIntPtr(com);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size));
    }

    static List<(string name, Guid inst)> EnumDirectInput()
    {
        var list = new List<(string, Guid)>();
        if (_di == IntPtr.Zero)
        {
            var iid = IID_IDirectInput8W;
            int hr = DirectInput8Create(GetModuleHandle(null), DIRECTINPUT_VERSION, ref iid, out _di, IntPtr.Zero);
            if (hr < 0 || _di == IntPtr.Zero) throw new InvalidOperationException("DirectInput8Create 0x" + hr.ToString("X8"));
        }
        EnumCb cb = (inst, pv) =>
        {
            // DIDEVICEINSTANCEW: +4 guidInstance, +20 guidProduct, +36 dwDevType, +40 tszInstanceName[260], +560 tszProductName[260]
            var gb = new byte[16];
            Marshal.Copy(inst + 4, gb, 0, 16);
            string name = (Marshal.PtrToStringUni(inst + 560) ?? "").Trim();
            if (name.Length == 0) name = (Marshal.PtrToStringUni(inst + 40) ?? "").Trim();
            list.Add((name, new Guid(gb)));
            return 1;   // DIENUM_CONTINUE
        };
        Fn<EnumDevicesFn>(_di, 4)(_di, DI8DEVCLASS_GAMECTRL, cb, IntPtr.Zero, DIEDFL_ATTACHEDONLY);
        GC.KeepAlive(cb);
        // XInput-Geräte laufen über XInput (Gerätepfad enthält "IG_")
        list.RemoveAll(e => _xiOk && IsXInputDevice(e.Item2));
        return list;
    }

    /// <summary>DIPROP_GUIDANDPATH: Gerätepfad mit "IG_" = XInput-Gerät.</summary>
    static bool IsXInputDevice(Guid inst)
    {
        IntPtr dev = IntPtr.Zero, p = IntPtr.Zero;
        try
        {
            var g = inst;
            if (Fn<CreateDeviceFn>(_di, 3)(_di, ref g, out dev, IntPtr.Zero) < 0 || dev == IntPtr.Zero) return false;
            // DIPROPGUIDANDPATH { DIPROPHEADER(16), GUID guidClass, WCHAR wszPath[260] } = 16 + 16 + 520
            const int size = 16 + 16 + 520;
            p = Marshal.AllocHGlobal(size);
            Marshal.WriteInt32(p, 0, size); Marshal.WriteInt32(p, 4, 16); Marshal.WriteInt32(p, 8, 0); Marshal.WriteInt32(p, 12, 0);
            if (Fn<PropFn>(dev, 5)(dev, new IntPtr(12), p) < 0) return false;   // GetProperty(DIPROP_GUIDANDPATH = MAKEDIPROP(12))
            string path = Marshal.PtrToStringUni(p + 32) ?? "";
            return path.IndexOf("IG_", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch (Exception) { return false; }
        finally
        {
            if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            if (dev != IntPtr.Zero) try { Fn<ReleaseFn>(dev, 2)(dev); } catch (Exception) { }
        }
    }

    // Eigenes Datenformat wie c_dfDIJoystick (dinput8.lib liegt nicht als DLL-Export vor):
    // 6 Achsen (LONG) X Y Z Rx Ry Rz, 2 Schieberegler, 4 Hut-Schalter (DWORD), 32 Knöpfe (BYTE) = 80 Bytes
    const int StateSize = 80;
    static void BuildFormat()
    {
        if (_format != IntPtr.Zero) return;
        Guid[] axisGuids =
        {
            new("A36D02E0-C9F3-11CF-BFC7-444553540000"),   // X
            new("A36D02E1-C9F3-11CF-BFC7-444553540000"),   // Y
            new("A36D02E2-C9F3-11CF-BFC7-444553540000"),   // Z
            new("A36D02F4-C9F3-11CF-BFC7-444553540000"),   // Rx
            new("A36D02F5-C9F3-11CF-BFC7-444553540000"),   // Ry
            new("A36D02E3-C9F3-11CF-BFC7-444553540000"),   // Rz
        };
        var slider = new Guid("A36D02E4-C9F3-11CF-BFC7-444553540000");
        var pov = new Guid("A36D02F2-C9F3-11CF-BFC7-444553540000");
        IntPtr G(Guid g) { var m = Marshal.AllocHGlobal(16); Marshal.Copy(g.ToByteArray(), 0, m, 16); _guids.Add(m); return m; }
        const uint ANYINSTANCE = 0x00FFFF00, OPTIONAL = 0x80000000, AXIS = 0x03, POV = 0x10, BUTTON = 0x0C, ASPECTPOSITION = 0x100;
        var objs = new List<(IntPtr guid, int ofs, uint type, uint flags)>();
        for (int i = 0; i < 6; i++) objs.Add((G(axisGuids[i]), i * 4, AXIS | ANYINSTANCE | OPTIONAL, ASPECTPOSITION));
        for (int i = 0; i < 2; i++) objs.Add((G(slider), 24 + i * 4, AXIS | ANYINSTANCE | OPTIONAL, ASPECTPOSITION));
        for (int i = 0; i < 4; i++) objs.Add((G(pov), 32 + i * 4, POV | ANYINSTANCE | OPTIONAL, 0));
        for (int i = 0; i < 32; i++) objs.Add((IntPtr.Zero, 48 + i, BUTTON | ANYINSTANCE | OPTIONAL, 0));
        int objSize = IntPtr.Size == 8 ? 24 : 16;   // DIOBJECTDATAFORMAT {const GUID*, DWORD ofs, DWORD type, DWORD flags}
        _objs = Marshal.AllocHGlobal(objSize * objs.Count);
        for (int i = 0; i < objs.Count; i++)
        {
            IntPtr o = _objs + i * objSize;
            Marshal.WriteIntPtr(o, 0, objs[i].guid);
            Marshal.WriteInt32(o, IntPtr.Size, objs[i].ofs);
            Marshal.WriteInt32(o, IntPtr.Size + 4, unchecked((int)objs[i].type));
            Marshal.WriteInt32(o, IntPtr.Size + 8, unchecked((int)objs[i].flags));
        }
        int fmtSize = IntPtr.Size == 8 ? 32 : 24;   // DIDATAFORMAT {dwSize, dwObjSize, dwFlags, dwDataSize, dwNumObjs, rgodf}
        _format = Marshal.AllocHGlobal(fmtSize);
        Marshal.WriteInt32(_format, 0, fmtSize);
        Marshal.WriteInt32(_format, 4, objSize);
        Marshal.WriteInt32(_format, 8, 1);            // DIDF_ABSAXIS
        Marshal.WriteInt32(_format, 12, StateSize);
        Marshal.WriteInt32(_format, 16, objs.Count);
        Marshal.WriteIntPtr(_format, IntPtr.Size == 8 ? 24 : 20, _objs);
    }

    static bool OpenDirectInput(Device d)
    {
        try
        {
            BuildFormat();
            var g = d.Instance;
            int hr = Fn<CreateDeviceFn>(_di, 3)(_di, ref g, out d.Di, IntPtr.Zero);
            if (hr < 0 || d.Di == IntPtr.Zero) { Log.Warn($"DirectInput CreateDevice \"{d.Name}\": 0x{hr:X8}"); return false; }
            hr = Fn<PtrArgFn>(d.Di, 11)(d.Di, _format);   // SetDataFormat
            if (hr < 0) { Log.Warn($"DirectInput SetDataFormat \"{d.Name}\": 0x{hr:X8}"); CloseDevice(d); return false; }
            IntPtr hwnd = Win32Window.Current?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero) Fn<SetCoopFn>(d.Di, 13)(d.Di, hwnd, DISCL_NONEXCLUSIVE | DISCL_BACKGROUND);
            // DIPROPRANGE { DIPROPHEADER { 24, 16, 0, DIPH_DEVICE }, lMin, lMax } – alle Achsen −32768..32767
            IntPtr p = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt32(p, 0, 24); Marshal.WriteInt32(p, 4, 16); Marshal.WriteInt32(p, 8, 0); Marshal.WriteInt32(p, 12, 0);
                Marshal.WriteInt32(p, 16, -32768); Marshal.WriteInt32(p, 20, 32767);
                Fn<PropFn>(d.Di, 6)(d.Di, new IntPtr(4), p);   // SetProperty(DIPROP_RANGE = MAKEDIPROP(4))
            }
            finally { Marshal.FreeHGlobal(p); }
            Fn<NoArgFn>(d.Di, 7)(d.Di);   // Acquire
            return true;
        }
        catch (Exception ex) { Log.Warn($"DirectInput \"{d.Name}\": {ex.Message}"); CloseDevice(d); return false; }
    }

    static readonly IntPtr _state = Marshal.AllocHGlobal(StateSize);

    static void ReadDirectInput(Device d)
    {
        if (d.Di == IntPtr.Zero) return;
        Fn<NoArgFn>(d.Di, 25)(d.Di);   // Poll
        int hr = Fn<GetStateFn>(d.Di, 9)(d.Di, StateSize, _state);   // GetDeviceState
        if (hr < 0)
        {
            Fn<NoArgFn>(d.Di, 7)(d.Di);   // Acquire (nach Fokuswechsel/Neustecken)
            hr = Fn<GetStateFn>(d.Di, 9)(d.Di, StateSize, _state);
            if (hr < 0) { Array.Clear(d.Axis); Array.Clear(d.Btn); return; }
        }
        static float A(int v) => System.Math.Clamp(v / 32767f, -1f, 1f);
        for (int i = 0; i < 6; i++) d.Axis[1 + i] = A(Marshal.ReadInt32(_state, i * 4));
        d.Axis[7] = A(Marshal.ReadInt32(_state, 24));
        d.Axis[8] = A(Marshal.ReadInt32(_state, 28));
        uint pov = unchecked((uint)Marshal.ReadInt32(_state, 32));
        if ((pov & 0xFFFF) == 0xFFFF) { d.Axis[9] = 0f; d.Axis[10] = 0f; }
        else
        {
            double a = pov / 100.0 * System.Math.PI / 180.0;   // Hundertstel Grad, 0 = oben, im Uhrzeigersinn
            float x = (float)System.Math.Sin(a), y = (float)System.Math.Cos(a);
            d.Axis[9] = x > 0.38f ? 1f : x < -0.38f ? -1f : 0f;
            d.Axis[10] = y > 0.38f ? 1f : y < -0.38f ? -1f : 0f;
        }
        for (int b = 0; b < MaxButtons; b++) d.Btn[b] = (Marshal.ReadByte(_state, 48 + b) & 0x80) != 0;
    }

    static void CloseDevice(Device d)
    {
        if (d.Di == IntPtr.Zero) return;
        try { Fn<NoArgFn>(d.Di, 8)(d.Di); } catch (Exception) { }   // Unacquire
        try { Fn<ReleaseFn>(d.Di, 2)(d.Di); } catch (Exception) { }
        d.Di = IntPtr.Zero;
    }
}
