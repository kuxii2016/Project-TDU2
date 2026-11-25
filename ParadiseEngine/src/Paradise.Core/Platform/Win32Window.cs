using Paradise.Core.Diagnostics;
using System.Runtime.InteropServices;

namespace Paradise.Core.Platform;

/// <summary>
/// Minimales Win32-Fenster per P/Invoke – kein GLFW/SDL, damit die exe ohne
/// Zusatz-DLL auskommt. Liefert ein HWND für die Vulkan-Surface und eine
/// nicht blockierende Nachrichtenschleife.
/// </summary>
public sealed class Win32Window : IGameWindow
{
    public nint Handle { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsClosing { get; private set; }
    public InputState Input { get; } = new();

    public event Action<int, int>? Resized;
    public event Action? CloseRequested;

    // Delegat als Feld halten, sonst sammelt der GC ihn ein -> Absturz im WndProc.
    private readonly WndProcDelegate _wndProc;
    private readonly string _className;

    public Win32Window(string title, int width, int height)
    {
        Width = width;
        Height = height;
        Current = this;
        _wndProc = WindowProc;
        _className = "ParadiseEngineWindow";

        nint hInstance = GetModuleHandle(null);

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = CS_HREDRAW | CS_VREDRAW | CS_OWNDC,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
            // Schwarzer Hintergrund: ohne Pinsel zeigt Windows die Client-Fläche bis zum ersten Vulkan-Bild weiß
            hbrBackground = GetStockObject(BLACK_BRUSH),
            lpszClassName = _className
        };

        if (RegisterClassEx(ref wc) == 0)
            throw new InvalidOperationException($"RegisterClassEx failed (Win32 {Marshal.GetLastWin32Error()}).");

        // Fensterrahmen einrechnen, damit die Client-Fläche genau width×height groß ist.
        var rect = new RECT { left = 0, top = 0, right = width, bottom = height };
        AdjustWindowRectEx(ref rect, WS_OVERLAPPEDWINDOW, false, 0);

        Handle = CreateWindowEx(
            0, _className, title, WS_OVERLAPPEDWINDOW,
            CW_USEDEFAULT, CW_USEDEFAULT,
            rect.right - rect.left, rect.bottom - rect.top,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed (Win32 {Marshal.GetLastWin32Error()}).");

        // Noch nicht anzeigen: GameLoop ruft Show() erst nach dem ersten fertigen Bild (Renderer, Module, erstes Present) –
        // vorher stand ein weißes, nicht reagierendes Fenster während des Ladens da.
        SetTitle(title); // sicherheitshalber nochmals explizit setzen
        TrySetIcon("game.ico");
    }

    /// <summary>
    /// Setzt das Fenster-Icon. Primär aus der eigenen exe (über &lt;ApplicationIcon&gt;
    /// eingebettet – funktioniert auch im gepackten Single-File-Build), ersatzweise
    /// aus einer game.ico-Datei neben der exe / im Projektbaum.
    /// </summary>
    private void TrySetIcon(string fileName)
    {
        // 1) Icon direkt aus der laufenden exe ziehen (groß + klein).
        string? exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            var large = new nint[1];
            var small = new nint[1];
            if (ExtractIconEx(exe, 0, large, small, 1) > 0 && (large[0] != IntPtr.Zero || small[0] != IntPtr.Zero))
            {
                if (large[0] != IntPtr.Zero) SendMessage(Handle, WM_SETICON, ICON_BIG, large[0]);
                if (small[0] != IntPtr.Zero) SendMessage(Handle, WM_SETICON, ICON_SMALL, small[0]);
                return;
            }
        }

        // 2) Fallback: game.ico als Datei.
        string? path = ResolveAsset(fileName);
        if (path == null)
        {
            Log.Info($"No exe icon and ‘{fileName}’ not found – the default icon remains.");
            return;
        }

        nint big = LoadImage(IntPtr.Zero, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
        nint sml = LoadImage(IntPtr.Zero, path, IMAGE_ICON,
            GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), LR_LOADFROMFILE);

        if (big != IntPtr.Zero) SendMessage(Handle, WM_SETICON, ICON_BIG, big);
        if (sml != IntPtr.Zero) SendMessage(Handle, WM_SETICON, ICON_SMALL, sml);

        if (big == IntPtr.Zero && sml == IntPtr.Zero)
            Log.Info($"The icon ‘{path}’ could not be loaded (Win32 {Marshal.GetLastWin32Error()}).");
    }

    bool _shown;

    /// <summary>Fenster sichtbar machen (einmalig, nach dem ersten gezeichneten Bild).</summary>
    public void Show()
    {
        if (_shown || Handle == IntPtr.Zero) return;
        _shown = true;
        ShowWindow(Handle, SW_SHOW);
        UpdateWindow(Handle);
        SetForegroundWindow(Handle);
    }

    private static string? ResolveAsset(string fileName)
    {
        foreach (var start in Paradise.Core.Config.AppPaths.SearchDirs())
        {
            string? dir = start;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                string candidate = System.IO.Path.Combine(dir, fileName);
                if (System.IO.File.Exists(candidate)) return candidate;
                dir = System.IO.Directory.GetParent(dir)?.FullName;
            }
        }
        return null;
    }

    public void SetTitle(string title)
    {
        nint ptr = Marshal.StringToHGlobalUni(title);
        try { SetWindowTextW(Handle, ptr); }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public void PumpEvents()
    {
        Input.BeginFrame(); // Maus-Deltas für diesen Frame zurücksetzen
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            if (msg.message == WM_QUIT) { IsClosing = true; break; }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        UpdateCursorLock();
    }

    bool _cursorHidden;
    /// <summary>Unity Cursor.lockState = Locked: Zeiger ausblenden und jedes Bild in die Fenstermitte setzen (nur mit Fokus).</summary>
    void UpdateCursorLock()
    {
        bool want = Input.CursorLocked && GetForegroundWindow() == Handle;
        if (want != _cursorHidden) { ShowCursor(!want); _cursorHidden = want; }
        if (!want || Width <= 0 || Height <= 0) return;
        var p = new POINT { x = Width / 2, y = Height / 2 };
        ClientToScreen(Handle, ref p);
        SetCursorPos(p.x, p.y);
        Input.Recenter(Width / 2, Height / 2);
    }

    private struct POINT { public int x, y; }
    [DllImport("user32.dll")] private static extern int ShowCursor(bool show);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hWnd, ref POINT p);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();

    private nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_SIZE:
                int w = (int)(lParam & 0xFFFF);
                int h = (int)((lParam >> 16) & 0xFFFF);
                if (w > 0 && h > 0 && (w != Width || h != Height))
                {
                    Width = w; Height = h;
                    Resized?.Invoke(w, h);
                }
                return 0;

            case WM_CLOSE:
                CloseRequested?.Invoke();
                IsClosing = true;
                DestroyWindow(hWnd);
                return 0;

            case WM_DESTROY:
                RestoreDisplayMode();
                PostQuitMessage(0);
                return 0;

            case WM_KEYDOWN:
                Input.SetKey((int)wParam, true);
                return 0;
            case WM_KEYUP:
                Input.SetKey((int)wParam, false);
                return 0;
            // F10 und Alt-Kombinationen kommen als Systemtasten (WM_SYSKEY*). Zustand merken; F10 nicht an
            // DefWindowProc geben (würde sonst den Menümodus aktivieren), Alt+F4 usw. weiter an Windows.
            case WM_SYSKEYDOWN:
            case WM_SYSKEYUP:
                // Alt+Enter: Vollbild an/aus (wie Unity-Player). Bit 30 = Taste war schon gedrückt (Wiederholung) -> ignorieren.
                if ((int)wParam == 0x0D)
                {
                    if (msg == WM_SYSKEYDOWN && ((long)lParam & (1L << 30)) == 0) ToggleFullscreen();
                    return 0;
                }
                Input.SetKey((int)wParam, msg == WM_SYSKEYDOWN);
                if ((int)wParam == 0x79) return 0;
                break;
            case WM_SYSCHAR:
                if ((int)wParam == '\r') return 0;   // Alt+Enter ohne Systemton
                break;
            case WM_MOUSEMOVE:
                Input.OnMouseMove((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                Flash.FlashInput.OnMouseMove((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                return 0;
            case WM_RBUTTONDOWN:
                Input.SetRightMouse(true);
                Flash.FlashInput.OnMouseButton(1, true);
                return 0;
            case WM_RBUTTONUP:
                Input.SetRightMouse(false);
                Flash.FlashInput.OnMouseButton(1, false);
                return 0;
            // Flash-Oberfläche (Menüs): linke/mittlere Maustaste und Texteingabe
            case WM_LBUTTONDOWN: Input.SetLeftMouse(true); Flash.FlashInput.OnMouseButton(0, true); return 0;
            case WM_LBUTTONUP: Input.SetLeftMouse(false); Flash.FlashInput.OnMouseButton(0, false); return 0;
            case WM_MBUTTONDOWN: Flash.FlashInput.OnMouseButton(2, true); return 0;
            case WM_MBUTTONUP: Flash.FlashInput.OnMouseButton(2, false); return 0;
            case WM_MOUSEWHEEL:   // Mausrad (Unity Input.mouseScrollDelta.y: eine Raste = 1)
                Flash.FlashInput.OnMouseWheel((short)(((long)wParam >> 16) & 0xFFFF) / 120f);
                return 0;
            case WM_CHAR:
                if ((int)wParam >= 8) Flash.FlashInput.OnChar((char)(int)wParam);
                return 0;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
        UnregisterClass(_className, GetModuleHandle(null));
    }


    public bool IsFullscreen { get; private set; }
    private WINDOWPLACEMENT _windowedPlacement;

    /// <summary>Zwischen Fenster und Vollbild wechseln (Alt+Enter). Die Swapchain folgt über WM_SIZE.</summary>
    public void ToggleFullscreen()
    {
        if (IsFullscreen) RestoreDisplayMode();   // zurück ins Fenster: Desktop-Auflösung wiederherstellen
        if (!IsFullscreen)
        {
            _windowedPlacement.length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>();
            GetWindowPlacement(Handle, ref _windowedPlacement);
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST), ref mi)) return;
            SetWindowLongPtr(Handle, GWL_STYLE, (nint)(WS_POPUP | WS_VISIBLE));
            SetWindowPos(Handle, HWND_TOP, mi.rcMonitor.left, mi.rcMonitor.top,
                mi.rcMonitor.right - mi.rcMonitor.left, mi.rcMonitor.bottom - mi.rcMonitor.top, SWP_FRAMECHANGED | SWP_NOOWNERZORDER);
            IsFullscreen = true;
        }
        else
        {
            SetWindowLongPtr(Handle, GWL_STYLE, (nint)(WS_OVERLAPPEDWINDOW | WS_VISIBLE));
            SetWindowPlacement(Handle, ref _windowedPlacement);
            SetWindowPos(Handle, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
            IsFullscreen = false;
        }
    }

    // ---------------- Auflösung (Optionsmenü, Unity Screen.resolutions / Screen.SetResolution) ----------------

    /// <summary>Zuletzt erzeugtes Fenster (für das Optionsmenü).</summary>
    public static Win32Window? Current { get; private set; }
    private bool _modeChanged;

    /// <summary>Auflösungen des Monitors (ohne doppelte), aufsteigend wie Unity Screen.resolutions; dazu die höchste Bildfrequenz je Größe.</summary>
    public static List<(int W, int H, int Hz)> DisplayResolutions()
    {
        var map = new Dictionary<(int, int), int>();
        var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        for (int i = 0; EnumDisplaySettings(null, i, ref dm); i++)
        {
            if (dm.dmBitsPerPel < 24) continue;
            var k = ((int)dm.dmPelsWidth, (int)dm.dmPelsHeight);
            if (!map.TryGetValue(k, out var hz) || dm.dmDisplayFrequency > hz) map[k] = (int)dm.dmDisplayFrequency;
        }
        var list = new List<(int W, int H, int Hz)>();
        foreach (var kv in map) list.Add((kv.Key.Item1, kv.Key.Item2, kv.Value));
        list.Sort((a, b) => a.W != b.W ? a.W.CompareTo(b.W) : a.H.CompareTo(b.H));
        return list;
    }

    public void SetResolution(int w, int h)
    {
        if (IsFullscreen)
        {
            int hz = 0;
            foreach (var r in DisplayResolutions()) if (r.W == w && r.H == h) hz = r.Hz;
            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>(), dmPelsWidth = (uint)w, dmPelsHeight = (uint)h, dmFields = DM_PELSWIDTH | DM_PELSHEIGHT };
            if (hz > 0) { dm.dmDisplayFrequency = (uint)hz; dm.dmFields |= DM_DISPLAYFREQUENCY; }
            int res = ChangeDisplaySettings(ref dm, CDS_FULLSCREEN);
            if (res != 0) { Log.Warn($"Auflösung {w}x{h}@{hz}: ChangeDisplaySettings {res}"); return; }
            _modeChanged = true;
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST), ref mi))
                SetWindowPos(Handle, HWND_TOP, mi.rcMonitor.left, mi.rcMonitor.top,
                    mi.rcMonitor.right - mi.rcMonitor.left, mi.rcMonitor.bottom - mi.rcMonitor.top, SWP_FRAMECHANGED | SWP_NOOWNERZORDER);
        }
        else
        {
            var rect = new RECT { left = 0, top = 0, right = w, bottom = h };
            AdjustWindowRectEx(ref rect, WS_OVERLAPPEDWINDOW, false, 0);
            SetWindowPos(Handle, 0, 0, 0, rect.right - rect.left, rect.bottom - rect.top, SWP_NOMOVE | SWP_NOZORDER | SWP_NOOWNERZORDER);
        }
    }

    /// <summary>Geänderten Anzeigemodus zurück auf die Desktop-Einstellung (Fenster, Beenden).</summary>
    public void RestoreDisplayMode()
    {
        if (!_modeChanged) return;
        _modeChanged = false;
        ChangeDisplaySettings(IntPtr.Zero, 0);
    }

    private const uint DM_PELSWIDTH = 0x00080000, DM_PELSHEIGHT = 0x00100000, DM_DISPLAYFREQUENCY = 0x00400000;
    private const uint CDS_FULLSCREEN = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")] private static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsW")] private static extern int ChangeDisplaySettings(ref DEVMODE dm, uint flags);
    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsW")] private static extern int ChangeDisplaySettings(nint dm, uint flags);

    private const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;
    private const int GWL_STYLE = -16;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private static readonly nint HWND_TOP = 0;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_FRAMECHANGED = 0x0020, SWP_NOOWNERZORDER = 0x0200;
    private const uint WM_SYSCHAR = 0x0106;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public uint length, flags, showCmd;
        public POINT ptMinPosition, ptMaxPosition;
        public RECT rcNormalPosition;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hWnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO mi);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);

    // ---------------- Win32-Interop ----------------

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    private const uint CS_VREDRAW = 0x0001, CS_HREDRAW = 0x0002, CS_OWNDC = 0x0020;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int CW_USEDEFAULT = unchecked((int)0x80000000);
    private const int SW_SHOW = 5;
    private const uint PM_REMOVE = 0x0001;
    private const uint WM_SIZE = 0x0005, WM_CLOSE = 0x0010, WM_DESTROY = 0x0002, WM_QUIT = 0x0012;
    private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105, WM_MOUSEMOVE = 0x0200,
                       WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
                       WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_CHAR = 0x0102, WM_MOUSEWHEEL = 0x020A;
    private static readonly nint IDC_ARROW = 32512;
    private const uint WM_SETICON = 0x0080;
    private const nint ICON_SMALL = 0, ICON_BIG = 1;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010, LR_DEFAULTSIZE = 0x0040;
    private const int SM_CXSMICON = 49, SM_CYSMICON = 50;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd; public uint message; public nint wParam; public nint lParam;
        public uint time; public int ptX; public int ptY;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint hInstance);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, nint parent, nint menu, nint hInstance, nint param);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int obj);
    private const int BLACK_BRUSH = 4;
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hWnd);
    [DllImport("user32.dll", EntryPoint = "SetWindowTextW")] private static extern bool SetWindowTextW(nint hWnd, nint text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern bool AdjustWindowRectEx(ref RECT rect, uint style, bool menu, uint exStyle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PeekMessage(out MSG msg, nint hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint hInstance, nint cursor);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint hInst, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, nint[] large, nint[] small, uint count);
}