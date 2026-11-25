using Paradise.Core.Render;

namespace Paradise.Core.Flash;
// ============================================================================================================
// Small replacement types for the Unity types used by the Flash code (taken directly from the Unity project).
// Same names and semantics as in Unity (x/y lowercase, Rect with xMin/yMax …) so that VmfMovie/VmfScript/VmfPlayer
// run without any changes to the code. Visible only in the Paradise.Core.Flash namespace.
// ============================================================================================================

public struct Vector2 : IEquatable<Vector2>
{
    public float x, y;
    public Vector2(float x, float y) { this.x = x; this.y = y; }
    public static readonly Vector2 zero = new(0f, 0f);
    public static readonly Vector2 one = new(1f, 1f);
    public float magnitude => MathF.Sqrt(x * x + y * y);
    public float sqrMagnitude => x * x + y * y;
    public Vector2 normalized { get { float m = magnitude; return m > 1e-5f ? new Vector2(x / m, y / m) : zero; } }
    public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.x + b.x, a.y + b.y);
    public static Vector2 operator -(Vector2 a, Vector2 b) => new(a.x - b.x, a.y - b.y);
    public static Vector2 operator -(Vector2 a) => new(-a.x, -a.y);
    public static Vector2 operator *(Vector2 a, float s) => new(a.x * s, a.y * s);
    public static Vector2 operator *(float s, Vector2 a) => new(a.x * s, a.y * s);
    public static Vector2 operator /(Vector2 a, float s) => new(a.x / s, a.y / s);
    public static bool operator ==(Vector2 a, Vector2 b) => (a - b).sqrMagnitude < 1e-10f;
    public static bool operator !=(Vector2 a, Vector2 b) => !(a == b);
    public static float Distance(Vector2 a, Vector2 b) => (a - b).magnitude;
    public static float Dot(Vector2 a, Vector2 b) => a.x * b.x + a.y * b.y;
    public static Vector2 Lerp(Vector2 a, Vector2 b, float t) { t = t < 0f ? 0f : t > 1f ? 1f : t; return a + (b - a) * t; }
    public static Vector2 Scale(Vector2 a, Vector2 b) => new(a.x * b.x, a.y * b.y);
    public bool Equals(Vector2 o) => x == o.x && y == o.y;
    public override bool Equals(object? o) => o is Vector2 v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(x, y);
    public override string ToString() => $"({x:0.##}, {y:0.##})";
}

public struct Vector4 : IEquatable<Vector4>
{
    public float x, y, z, w;
    public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
    public static readonly Vector4 zero = new(0f, 0f, 0f, 0f);
    public static readonly Vector4 one = new(1f, 1f, 1f, 1f);
    public static Vector4 operator +(Vector4 a, Vector4 b) => new(a.x + b.x, a.y + b.y, a.z + b.z, a.w + b.w);
    public static Vector4 operator -(Vector4 a, Vector4 b) => new(a.x - b.x, a.y - b.y, a.z - b.z, a.w - b.w);
    public static Vector4 operator *(Vector4 a, float s) => new(a.x * s, a.y * s, a.z * s, a.w * s);
    public static Vector4 operator *(float s, Vector4 a) => a * s;
    public static Vector4 operator /(Vector4 a, float s) => new(a.x / s, a.y / s, a.z / s, a.w / s);
    public static bool operator ==(Vector4 a, Vector4 b) => a.Equals(b);
    public static bool operator !=(Vector4 a, Vector4 b) => !a.Equals(b);
    public static Vector4 Scale(Vector4 a, Vector4 b) => new(a.x * b.x, a.y * b.y, a.z * b.z, a.w * b.w);
    public static Vector4 Lerp(Vector4 a, Vector4 b, float t) { t = t < 0f ? 0f : t > 1f ? 1f : t; return a + (b - a) * t; }
    public static implicit operator Vector4(Color c) => new(c.r, c.g, c.b, c.a);
    public static implicit operator Color(Vector4 v) => new(v.x, v.y, v.z, v.w);
    public System.Numerics.Vector4 ToNumerics() => new(x, y, z, w);
    public bool Equals(Vector4 o) => x == o.x && y == o.y && z == o.z && w == o.w;
    public override bool Equals(object? o) => o is Vector4 v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(x, y, z, w);
    public override string ToString() => $"({x:0.###}, {y:0.###}, {z:0.###}, {w:0.###})";
}

public struct Rect : IEquatable<Rect>
{
    float _x, _y, _w, _h;
    public Rect(float x, float y, float width, float height) { _x = x; _y = y; _w = width; _h = height; }
    public Rect(Vector2 position, Vector2 size) : this(position.x, position.y, size.x, size.y) { }
    public static Rect MinMaxRect(float xmin, float ymin, float xmax, float ymax) => new(xmin, ymin, xmax - xmin, ymax - ymin);
    public static readonly Rect zero = new(0, 0, 0, 0);
    public float x { get => _x; set => _x = value; }
    public float y { get => _y; set => _y = value; }
    public float width { get => _w; set => _w = value; }
    public float height { get => _h; set => _h = value; }
    public float xMin { get => _x; set { float xm = xMax; _x = value; _w = xm - _x; } }
    public float yMin { get => _y; set { float ym = yMax; _y = value; _h = ym - _y; } }
    public float xMax { get => _x + _w; set => _w = value - _x; }
    public float yMax { get => _y + _h; set => _h = value - _y; }
    public Vector2 position { get => new(_x, _y); set { _x = value.x; _y = value.y; } }
    public Vector2 size { get => new(_w, _h); set { _w = value.x; _h = value.y; } }
    public Vector2 center { get => new(_x + _w * 0.5f, _y + _h * 0.5f); set { _x = value.x - _w * 0.5f; _y = value.y - _h * 0.5f; } }
    public Vector2 min => new(xMin, yMin);
    public Vector2 max => new(xMax, yMax);
    public bool Contains(Vector2 p) => p.x >= xMin && p.x < xMax && p.y >= yMin && p.y < yMax;
    public bool Overlaps(Rect o) => o.xMax > xMin && o.xMin < xMax && o.yMax > yMin && o.yMin < yMax;
    public static bool operator ==(Rect a, Rect b) => a.Equals(b);
    public static bool operator !=(Rect a, Rect b) => !a.Equals(b);
    public bool Equals(Rect o) => _x == o._x && _y == o._y && _w == o._w && _h == o._h;
    public override bool Equals(object? o) => o is Rect r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(_x, _y, _w, _h);
    public override string ToString() => $"(x:{_x:0.##}, y:{_y:0.##}, w:{_w:0.##}, h:{_h:0.##})";
}

public struct Color
{
    public float r, g, b, a;
    public Color(float r, float g, float b, float a = 1f) { this.r = r; this.g = g; this.b = b; this.a = a; }
    public static Color white => new(1, 1, 1, 1);
    public static Color black => new(0, 0, 0, 1);
    public static Color clear => new(0, 0, 0, 0);
    public static Color operator *(Color c, float s) => new(c.r * s, c.g * s, c.b * s, c.a * s);
    public static Color operator *(Color x, Color y) => new(x.r * y.r, x.g * y.g, x.b * y.b, x.a * y.a);
    public static Color operator +(Color x, Color y) => new(x.r + y.r, x.g + y.g, x.b + y.b, x.a + y.a);
    public static Color Lerp(Color x, Color y, float t) { t = t < 0f ? 0f : t > 1f ? 1f : t; return new(x.r + (y.r - x.r) * t, x.g + (y.g - x.g) * t, x.b + (y.b - x.b) * t, x.a + (y.a - x.a) * t); }
    public static implicit operator Color(Color32 c) => new(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
}

public struct Color32
{
    public byte r, g, b, a;
    public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    static byte B(float v) => (byte)(v <= 0f ? 0 : v >= 1f ? 255 : (int)MathF.Round(v * 255f));
    public static implicit operator Color32(Color c) => new(B(c.r), B(c.g), B(c.b), B(c.a));
    public static Color32 Lerp(Color32 x, Color32 y, float t)
    {
        t = t < 0f ? 0f : t > 1f ? 1f : t;
        return new((byte)(x.r + (y.r - x.r) * t), (byte)(x.g + (y.g - x.g) * t), (byte)(x.b + (y.b - x.b) * t), (byte)(x.a + (y.a - x.a) * t));
    }
}

/// <summary>Time updated by the Flash host every frame.</summary>
public static class Time
{
    static readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    public static float unscaledTime { get; internal set; }
    public static float unscaledDeltaTime { get; internal set; }
    public static float deltaTime => unscaledDeltaTime;
    public static float time => unscaledTime;
    public static int frameCount { get; internal set; }
    public static float realtimeSinceStartup => (float)_sw.Elapsed.TotalSeconds;

    internal static void Tick()
    {
        float now = realtimeSinceStartup;
        unscaledDeltaTime = frameCount == 0 ? 0f : MathF.Min(0.25f, now - unscaledTime);
        unscaledTime = now;
        frameCount++;
    }
}

/// <summary>Screen size in pixels (set by the Flash host).</summary>
public static class Screen
{
    public static int width { get; internal set; } = 1280;
    public static int height { get; internal set; } = 720;
}

/// <summary>Mouse/text (populated by the window see <see cref="FlashHost"/>). mousePosition: y from the bottom.</summary>
public static class FlashInput
{
    static readonly bool[] _down = new bool[3], _pressed = new bool[3], _released = new bool[3];
    static readonly bool[] _nextPressed = new bool[3], _nextReleased = new bool[3];
    static readonly System.Text.StringBuilder _text = new(), _nextText = new();
    static float _mx, _my;

    public static Vector2 mousePosition => new(_mx, Screen.height - _my);
    public static string inputString { get; private set; } = "";
    public static bool GetMouseButton(int b) => b >= 0 && b < 3 && _down[b];
    public static bool GetMouseButtonDown(int b) => b >= 0 && b < 3 && _pressed[b];
    public static bool GetMouseButtonUp(int b) => b >= 0 && b < 3 && _released[b];
    /// <summary>Mouse wheel in this image (Unity Input.mouseScrollDelta.y: + = away from the user, one click = 1).</summary>
    public static float mouseScrollDelta { get; private set; }
    static float _nextWheel;
    public static void OnMouseWheel(float notches) { lock (_nextText) _nextWheel += notches; }

    // --- vom Fenster (beliebiger Zeitpunkt im Frame) ---
    public static void OnMouseMove(int x, int y) { _mx = x; _my = y; }
    public static void OnMouseButton(int b, bool down)
    {
        if (b < 0 || b >= 3) return;
        if (down && !_down[b]) _nextPressed[b] = true;
        if (!down && _down[b]) _nextReleased[b] = true;
        _down[b] = down;
    }
    public static void OnChar(char c) { lock (_nextText) _nextText.Append(c); }

    /// <summary>At the start of the frame: apply the collected events for this frame.</summary>
    internal static void BeginFrame()
    {
        for (int i = 0; i < 3; i++) { _pressed[i] = _nextPressed[i]; _released[i] = _nextReleased[i]; _nextPressed[i] = _nextReleased[i] = false; }
        lock (_nextText) { inputString = _nextText.ToString(); _nextText.Clear(); mouseScrollDelta = _nextWheel; _nextWheel = 0f; }
    }
}

/// <summary>UnityEngine.Random replacement.</summary>
public static class FlashRandom
{
    static readonly Random _r = new();
    public static float value => (float)_r.NextDouble();
    public static float Range(float min, float max) => min + (float)_r.NextDouble() * (max - min);
    public static int Range(int min, int max) => max <= min ? min : _r.Next(min, max);
}

public enum TextureWrapMode { Repeat, Clamp, Mirror }

/// <summary>Texture for the Flash Player: GPU texture (renderer) + optional CPU pixels. Replaces UnityEngine.Texture/Texture2D.</summary>
public class Texture : IDisposable
{
    public int width { get; protected set; }
    public int height { get; protected set; }
    public IGpuTexture? Gpu { get; protected set; }
    public virtual void Dispose() { Gpu?.Dispose(); Gpu = null; }

    /// <summary>External GPU texture (e.g., render target/minimap) for SetDynamicTexture.</summary>
    public static Texture Wrap(IGpuTexture gpu, int w, int h) => new() { Gpu = gpu, width = w, height = h };
}

public sealed class Texture2D : Texture
{
    Color32[]? _pixels;
    public TextureWrapMode wrapMode = TextureWrapMode.Clamp;

    public Texture2D(int w, int h) { width = w; height = h; }
    /// <summary>Unity new Texture2D(w, h, fmt, mips, linear): linear = Werte ungewandelt (UNORM). Für Bilder, die wie die Datei
    /// erscheinen sollen (Fotos): Flash schreibt den gelesenen Wert unkodiert ins Bild – eine sRGB-Textur würde dort um die
    /// Gammakurve abgedunkelt (Foto-Vorschau im Test 24.11. 08:52: mittlere Helligkeit 63 -> 19).</summary>
    public Texture2D(int w, int h, bool linear) { width = w; height = h; _linear = linear; }
    readonly bool _linear;

    static Texture2D? _white;
    /// <summary>1×1 white (UnityEngine.Texture2D.whiteTexture).</summary>
    public static Texture2D whiteTexture
    {
        get
        {
            if (_white != null && _white.Gpu != null) return _white;
            _white = new Texture2D(1, 1);
            _white.SetPixels32(new[] { new Color32(255, 255, 255, 255) });
            _white.Apply();
            return _white;
        }
    }

    public void SetPixels32(Color32[] px) => _pixels = px;

    /// <summary>Load pixels onto the GPU (line 0 = v 0, as in Unity).</summary>
    public void Apply()
    {
        var r = FlashHost.Renderer;
        if (r == null || _pixels == null) return;
        // Zeile 0 der Pixel = v 0 – in Unity wie in Vulkan, daher unverändert hochladen
        var data = new byte[width * height * 4];
        for (int i = 0; i < _pixels.Length && i < width * height; i++)
        {
            var c = _pixels[i];
            int o = i * 4;
            data[o] = c.r; data[o + 1] = c.g; data[o + 2] = c.b; data[o + 3] = c.a;
        }
        var wrap = wrapMode == TextureWrapMode.Repeat ? TextureWrap.Repeat : TextureWrap.Clamp;
        Gpu?.Dispose();
        Gpu = r.CreateTexture(new TextureUpload { Width = width, Height = height, Format = GpuTextureFormat.Rgba8, Mips = 1, Blocks = data, WrapU = wrap, WrapV = wrap, Name = "Flash", Deferred = true, Linear = _linear });
        _pixels = null;   // nicht lesbar halten (wie Apply(false, true) im Unity-Projekt)
    }

    /// <summary>Texture from a completed GPU upload description (e.g., .2db).</summary>
    internal static Texture2D? FromUpload(TextureUpload? up)
    {
        var r = FlashHost.Renderer;
        if (up == null || r == null) return null;
        return new Texture2D(up.Width, up.Height) { Gpu = r.CreateTexture(up) };
    }
}

/// <summary>Replacement for DebugManager (Unity project) – Output to the Paradise log.</summary>
public static class DebugManager
{
    public static void Info(string s) => Diagnostics.Log.Info(s);
    public static void Warning(string s) => Diagnostics.Log.Warn(s);
    public static void Error(string s) => Diagnostics.Log.Error(s);
}

/// <summary>Replacement for UnityEngine.Debug.</summary>
public static class Debug
{
    public static void Log(object? s) => Diagnostics.Log.Info(s?.ToString() ?? "");
    public static void LogWarning(object? s) => Diagnostics.Log.Warn(s?.ToString() ?? "");
    public static void LogError(object? s) => Diagnostics.Log.Error(s?.ToString() ?? "");
}

[AttributeUsage(AttributeTargets.Field)] public sealed class TooltipAttribute : Attribute { public TooltipAttribute(string t) { } }
[AttributeUsage(AttributeTargets.Field)] public sealed class RangeAttribute : Attribute { public RangeAttribute(float a, float b) { } }
