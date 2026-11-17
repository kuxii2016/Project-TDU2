using System.Numerics;

namespace Paradise.Core.Render;

/// <summary>
/// Port von Unity TDU2DebugLines (Util/TDU2DebugLines.cs): Linien in der Welt für die Debug-Ansichten (Verkehrsrouten,
/// Pfeile, Radstrahlen am eigenen Auto …), auch ohne Editor-Gizmos. Gesammelt wird im Spielraum (Unity-Inselkoordinaten,
/// wie Traffic/VehicleManager.PositionGame) während Update; <see cref="Flush"/> zeichnet alles einmal je Bild und leert den Puffer.
/// Wie das Unity-Material "TDU2/Debug Lines": ohne Tiefentest (ZTest Always), alpha-gemischt, über der Welt, unter HUD und Menü.
/// Zeichnung: je Linie ein zur Kamera gedrehtes schmales Viereck (Vulkan hat keine breiten Linien), Breite ~1,5 Pixel.
/// Beschriftungen (<see cref="Label"/>) zeichnet das Entwicklermenü über den Bildschirmpunkt (Unity GUI.Label).
/// </summary>
public static class DebugLines
{
    public struct Segment { public Vector3 A, B; public Vector4 C; }
    public struct Text { public Vector3 P; public string S; public Vector4 C; }

    static readonly List<Segment> _segments = new(2048);
    static readonly List<Text> _labels = new(64);
    static readonly List<ObjectVertex> _v = new(8192);
    static readonly List<uint> _i = new(12288);
    static IGpuMaterial? _mat;
    static IRenderer? _matOwner;

    public static int Count => _segments.Count;
    public static IReadOnlyList<Text> Labels => _labels;

    public static void Clear() { _segments.Clear(); _labels.Clear(); }
    public static void ClearLabels() => _labels.Clear();

    public static void Line(Vector3 a, Vector3 b, Vector4 c) => _segments.Add(new Segment { A = a, B = b, C = c });

    public static void Ray(Vector3 from, Vector3 direction, float length, Vector4 c)
    {
        if (direction.LengthSquared() < 1e-8f) return;
        Line(from, from + Vector3.Normalize(direction) * length, c);
    }

    /// <summary>Waagerechter Kreis (XZ-Ebene) um <paramref name="center"/>.</summary>
    public static void Circle(Vector3 center, float radius, Vector4 c, int steps = 24)
    {
        steps = System.Math.Max(4, steps);
        var prev = center + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= steps; i++)
        {
            float a = i / (float)steps * MathF.PI * 2f;
            var p = center + new Vector3(MathF.Cos(a) * radius, 0f, MathF.Sin(a) * radius);
            Line(prev, p, c);
            prev = p;
        }
    }

    /// <summary>Linie entlang <paramref name="direction"/> mit zweiseitiger Pfeilspitze.</summary>
    public static void Arrow(Vector3 from, Vector3 direction, float length, Vector4 c, float head = 1.5f)
    {
        if (direction.LengthSquared() < 1e-8f) return;
        var dir = Vector3.Normalize(direction);
        var tip = from + dir * length;
        Line(from, tip, c);
        var side = Vector3.Cross(Vector3.UnitY, dir);
        if (side.LengthSquared() < 1e-4f) side = Vector3.UnitX;
        side = Vector3.Normalize(side);
        var back = tip - dir * head;
        Line(tip, back + side * head * 0.55f, c);
        Line(tip, back - side * head * 0.55f, c);
    }

    /// <summary>Drahtquader aus acht Ecken.</summary>
    public static void Box(Vector3 center, Quaternion rotation, Vector3 size, Vector4 c)
    {
        var h = size * 0.5f;
        Vector3 P(float x, float y, float z) => center + Vector3.Transform(new Vector3(x * h.X, y * h.Y, z * h.Z), rotation);
        var p000 = P(-1, -1, -1); var p100 = P(1, -1, -1); var p110 = P(1, 1, -1); var p010 = P(-1, 1, -1);
        var p001 = P(-1, -1, 1); var p101 = P(1, -1, 1); var p111 = P(1, 1, 1); var p011 = P(-1, 1, 1);
        Line(p000, p100, c); Line(p100, p110, c); Line(p110, p010, c); Line(p010, p000, c);
        Line(p001, p101, c); Line(p101, p111, c); Line(p111, p011, c); Line(p011, p001, c);
        Line(p000, p001, c); Line(p100, p101, c); Line(p110, p111, c); Line(p010, p011, c);
    }

    /// <summary>Beschriftung an einem Weltpunkt (Spielraum), mehrzeilig mit '\n'.</summary>
    public static void Label(Vector3 at, string text, Vector4 c) => _labels.Add(new Text { P = at, S = text, C = c });

    /// <summary>
    /// Alle Linien zeichnen und den Linienpuffer leeren. <paramref name="gameToRender"/> = Spiel -> Render-Raum
    /// (MapStreamer.RootMatrix), <paramref name="camRender"/> = Kameraposition im Render-Raum, <paramref name="pixelAngle"/> =
    /// Winkel eines Bildpunkts (Bogenmaß, 2·tan(fov/2)/Bildhöhe) für die Linienbreite.
    /// </summary>
    public static void Flush(IRenderer r, Matrix4x4 gameToRender, Vector3 camRender, float pixelAngle)
    {
        if (_segments.Count == 0) return;
        if (_mat == null || !ReferenceEquals(_matOwner, r))
        {
            // TDU2/Minimap-Pipeline: Vertexfarbe × weiß, Alpha-Blend, ZTest Always, als letztes in der Welt gezeichnet
            _mat = r.CreateMaterial(new MaterialDesc { Kind = MaterialKind.MapRoad, Name = "TDU2 Debug Lines" });
            _matOwner = r;
        }
        _v.Clear(); _i.Clear();
        float halfPx = MathF.Max(1e-5f, pixelAngle) * 0.75f;
        foreach (var s in _segments)
        {
            var a = Vector3.Transform(s.A, gameToRender);
            var b = Vector3.Transform(s.B, gameToRender);
            var d = b - a;
            if (d.LengthSquared() < 1e-10f) continue;
            var mid = (a + b) * 0.5f;
            var toCam = camRender - mid;
            var side = Vector3.Cross(d, toCam);
            if (side.LengthSquared() < 1e-12f) continue;
            side = Vector3.Normalize(side);
            float wa = MathF.Max(0.002f, (camRender - a).Length() * halfPx);
            float wb = MathF.Max(0.002f, (camRender - b).Length() * halfPx);
            uint col = ObjectVertex.PackColor((byte)(System.Math.Clamp(s.C.X, 0f, 1f) * 255f), (byte)(System.Math.Clamp(s.C.Y, 0f, 1f) * 255f),
                (byte)(System.Math.Clamp(s.C.Z, 0f, 1f) * 255f), (byte)(System.Math.Clamp(s.C.W, 0f, 1f) * 255f));
            uint n = (uint)_v.Count;
            _v.Add(Vert(a - side * wa, col)); _v.Add(Vert(a + side * wa, col));
            _v.Add(Vert(b + side * wb, col)); _v.Add(Vert(b - side * wb, col));
            _i.Add(n); _i.Add(n + 1); _i.Add(n + 2); _i.Add(n); _i.Add(n + 2); _i.Add(n + 3);
        }
        _segments.Clear();
        if (_i.Count == 0) return;
        r.DrawDynamic(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_v), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_i), _mat, Matrix4x4.Identity);
    }

    static ObjectVertex Vert(Vector3 p, uint c) => new() { Position = p, Normal = Vector3.UnitY, Color = c, Uv0 = new Vector2(0.5f, 0.5f) };
}
