namespace Paradise.Core.Platform;

/// <summary>
/// Einfacher Eingabezustand (Tastatur + Maus-Delta). Vom Fenster befüllt,
/// von der Kamera gelesen. Maus-Deltas werden pro Frame zurückgesetzt.
/// </summary>
public sealed class InputState
{
    // Virtuelle Tastencodes (Win32)
    public const int VK_W = 0x57, VK_A = 0x41, VK_S = 0x53, VK_D = 0x44, VK_Q = 0x51, VK_E = 0x45;
    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_SPACE = 0x20, VK_ESCAPE = 0x1B;

    private readonly bool[] _keys = new bool[256];

    public float MouseDeltaX { get; private set; }
    public float MouseDeltaY { get; private set; }
    public bool RightMouseDown { get; private set; }
    /// <summary>Linke Maustaste gedrückt bzw. in diesem Bild neu gedrückt (Unity Input.GetMouseButtonDown(0)).</summary>
    public bool LeftMouseDown { get; private set; }
    public bool LeftMousePressed { get; private set; }
    /// <summary>Maus gefangen (Unity Cursor.lockState = Locked): Zeiger unsichtbar, bleibt in der Fenstermitte, nur Deltas zählen.</summary>
    public bool CursorLocked;

    private int _lastX, _lastY;
    private bool _haveMouse;

    public bool IsKeyDown(int vk) => vk >= 0 && vk < 256 && _keys[vk];

    // --- vom Fenster aufgerufen ---
    public void SetKey(int vk, bool down) { if (vk >= 0 && vk < 256) _keys[vk] = down; }
    public void SetRightMouse(bool down) => RightMouseDown = down;
    public void SetLeftMouse(bool down) { if (down && !LeftMouseDown) LeftMousePressed = true; LeftMouseDown = down; }

    public void OnMouseMove(int x, int y)
    {
        if (_haveMouse) { MouseDeltaX += x - _lastX; MouseDeltaY += y - _lastY; }
        _lastX = x; _lastY = y; _haveMouse = true;
    }

    /// <summary>Gefangene Maus wurde in die Mitte gesetzt: nächste Bewegung von dort messen.</summary>
    public void Recenter(int x, int y) { _lastX = x; _lastY = y; _haveMouse = true; }

    /// <summary>Zu Frame-Beginn: Maus-Deltas nullen (danach sammeln die Events neue).</summary>
    public void BeginFrame() { MouseDeltaX = 0; MouseDeltaY = 0; LeftMousePressed = false; }
}
