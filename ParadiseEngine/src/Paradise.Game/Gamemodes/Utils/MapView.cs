using System.Numerics;
using FlashInput = Paradise.Core.Flash.FlashInput;
using FlashScreen = Paradise.Core.Flash.Screen;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Kartenansicht von oben auf die echte 3D-Insel – gemeinsam für die große Karte (GAME_MODE_MAP) und den Challenge-Editor
/// (GAME_MODE_EDITOR; im Original liegt der Editor über derselben Kartenansicht, exe GM_GMEditor +0x1be014 = Kartensteuerung aus
/// bei FSCommand "mcof", an bei "mcon"). Port von Unity TDU2BigMap (Kamera/Zoom/GroundPoint):
///   Zoomstufen _root.WhatZoom 1 (nah) .. 4 (ganze Insel), je Stufe Sichtbreite und Neigung; Norden oben; Straßennetz darüber.
/// Während die Ansicht offen ist: Fahrzeuge angehalten, HUD und Minimap aus, Weltkamera in Kartenlage.
/// </summary>
public sealed class MapView
{
    // Kamera je Zoomstufe (WhatZoom 1 = nah … 4 = ganze Insel) – Unity-Inspektorwerte
    public float[] ViewWidth = { 1536f, 6144f, 0f, 0f };
    public float[] Inclination = { 45f, 25f, 0f, 0f };
    public float FarFactor = 0.5f;
    public float FieldOfView = 40f;
    public float Smooth = 4f;
    public float PanSpeed = 0.25f;
    /// <summary>Straßennetz über der Karte zeichnen (Unity TDU2BigMap.ShowRoads).</summary>
    public bool ShowRoads = true;

    /// <summary>Kartenmitte (Render-Raum, Höhe = Meeresspiegel).</summary>
    public Vector3 Target;
    /// <summary>Zoomstufe 1..4 (_root.WhatZoom).</summary>
    public int Zoom = 3;
    /// <summary>Aktuelle (weich nachgeführte) Sichtbreite in Metern.</summary>
    public float CurWidth;
    /// <summary>Bildzeit für die weiche Kamerafahrt.</summary>
    public float LastDt = 1f / 60f;
    public bool IsActive => _active;

    IGameModeContext? _ctx;
    bool _active;
    float _curIncl;
    bool _savedMinimap = true, _savedHud = true;
    float _savedFov, _savedNear, _savedFar;

    /// <summary>Umgebung setzen (vor Begin bzw. vor Aufrufen außerhalb der offenen Ansicht).</summary>
    public void Bind(IGameModeContext ctx) => _ctx = ctx;

    /// <summary>Unity OpenMap (Kamerateil): Fahrzeuge anhalten, HUD/Minimap aus, Weltkamera über die Insel.</summary>
    public bool Begin(IGameModeContext ctx, Vector3 centerRender, int zoom)
    {
        _ctx = ctx;
        var terrain = ctx.Terrain; var cam = terrain?.Camera; var s = terrain?.Streamer;
        if (terrain == null || cam == null || s == null || _active) return false;
        _active = true;
        var vehicles = ctx.Vehicles; var vm = vehicles?.Manager;
        if (vehicles != null) vehicles.Frozen = true;
        if (vm?.Minimap != null) { _savedMinimap = vm.Minimap.ShowMinimap; vm.Minimap.ShowMinimap = false; }
        if (vm != null) { _savedHud = vm.ShowHud; vm.ShowHud = false; vm.ApplyHudVisibility(); }

        _savedFov = terrain.FieldOfView; _savedNear = terrain.NearClip; _savedFar = terrain.FarClip;
        terrain.FlyCameraEnabled = false;
        terrain.StreamFocus = cam.Position;   // Unity: eigene Kartenkamera – Streaming/Ursprung bleiben an der Spielkamera
        terrain.MapView = true;
        s.RequestMapTerrain();   // falls noch nicht im Hintergrund geladen
        terrain.FieldOfView = FieldOfView;
        Target = centerRender;
        Target.Y = SeaY();
        Zoom = System.Math.Clamp(zoom, 1, 4);
        UpdateCamera(true);
        return true;
    }

    /// <summary>Unity Finish (Kamerateil): Weltkamera, Fahrzeuge, HUD und Minimap wie vorher.</summary>
    public void End()
    {
        if (!_active || _ctx == null) return;
        _active = false;
        var terrain = _ctx.Terrain;
        if (terrain != null)
        {
            terrain.MapView = false;
            terrain.StreamFocus = null;
            terrain.FieldOfView = _savedFov; terrain.NearClip = _savedNear; terrain.FarClip = _savedFar;
        }
        var vehicles = _ctx.Vehicles; var vm = vehicles?.Manager;
        if (vehicles != null) vehicles.Frozen = false;
        if (vm?.Minimap != null) vm.Minimap.ShowMinimap = _savedMinimap;
        if (vm != null) vm.ShowHud = _savedHud;
    }

    /// <summary>Unity TDU2BigMap.DrawRoads: Straßennetz (und GPS-Route) über dem Kartengelände (MapMetersPerPixel 0 = ShowRoads aus).</summary>
    public void DrawRoads()
    {
        var t = _ctx?.Terrain; var s = t?.Streamer; var vm = _ctx?.Vehicles?.Manager; var r = _ctx?.Vehicles?.Renderer;
        if (t == null || s == null || vm?.Minimap == null || r == null || !t.MapView || t.MapMetersPerPixel <= 0f) return;
        vm.Minimap.DrawMapRoads(r, s.RootMatrix, t.MapMetersPerPixel, (x, z) => s.TryGetMapGroundHeight(x, z, out float y) ? y + 1f : 0f);
    }

    /// <summary>Kartenmitte verschieben (Richtung im Render-Raum, Länge 1 = PanSpeed × Sichtbreite pro Sekunde).</summary>
    public void Pan(Vector3 move, float dt, bool fast) => Target += move * (PanSpeed * CurWidth * (fast ? 2.5f : 1f)) * dt;

    /// <summary>Kartenmitte so verschieben, dass der Weltpunkt unter <paramref name="from"/> unter <paramref name="to"/> liegt (Ziehen).</summary>
    public void Drag(Vector2 from, Vector2 to)
    {
        var a = GroundPoint(from); var b = GroundPoint(to);
        if (a.HasValue && b.HasValue) Target += a.Value - b.Value;
    }

    // Spiel -Z = oben auf der Karte (Unity North: MapRoot.TransformVector(Vector3.back)); Render-Raum = Spiel + Verschiebung
    public static Vector3 North => new(0f, 0f, -1f);
    public static Vector3 East => new(1f, 0f, 0f);

    /// <summary>Maus in Pixeln, Ursprung oben links.</summary>
    public static Vector2 MousePixel()
    {
        var m = FlashInput.mousePosition;   // Unity: y von unten
        return new Vector2(m.x, FlashScreen.height - m.y);
    }

    /// <summary>Meeresspiegel (Spiel-y 0) im Render-Raum.</summary>
    public float SeaY() => _ctx?.Terrain?.Streamer?.GameToRender(Vector3.Zero).Y ?? 0f;

    /// <summary>Schnittpunkt des Strahls durch den Bildpunkt mit der Meeresebene (Unity GroundPoint).</summary>
    public Vector3? GroundPoint(Vector2 pixel)
    {
        var t = _ctx?.Terrain;
        if (t == null || !t.ScreenRay(pixel.X, pixel.Y, out var o, out var d)) return null;
        if (MathF.Abs(d.Y) < 1e-4f) return null;
        float k = (SeaY() - o.Y) / d.Y;
        return k > 0f ? o + d * k : null;
    }

    /// <summary>Bildpunkt (GUI-Pixel, y nach unten) eines Punkts im Render-Raum; null hinter der Kamera.</summary>
    public Vector2? ToGui(Vector3 render) => _ctx?.Terrain != null && _ctx.Terrain.WorldToScreen(render, out var p) ? p : null;

    /// <summary>Kartenmitte auf die Insel begrenzen.</summary>
    public void ClampTarget()
    {
        var s = _ctx?.Terrain?.Streamer; var idx = s?.Index;
        if (s == null || idx == null) return;
        var g = s.RenderToGame(Target);
        g.X = System.Math.Clamp(g.X, 0f, System.Math.Max(1, idx.AreasX) * 6144f);
        g.Z = System.Math.Clamp(g.Z, 0f, System.Math.Max(1, idx.AreasZ) * 6144f);
        var r = s.GameToRender(g); Target.X = r.X; Target.Z = r.Z;
    }

    float IslandWidth()
    {
        var idx = _ctx?.Terrain?.Streamer?.Index;
        float a = idx != null ? System.Math.Max(idx.AreasX, idx.AreasZ) : 8;
        return a * 6144f;
    }

    /// <summary>Sichtbreite (Meter) der Zoomstufe 1..4.</summary>
    public float WidthFor(int z)
    {
        int i = System.Math.Clamp(z, 1, 4) - 1;
        float w = i < ViewWidth.Length ? ViewWidth[i] : 0f;
        if (w > 0f) return w;
        return z >= 4 ? IslandWidth() : IslandWidth() * FarFactor;
    }

    float InclFor(int z)
    {
        int i = System.Math.Clamp(z, 1, 4) - 1;
        return i < Inclination.Length ? Inclination[i] : 0f;
    }

    /// <summary>Unity UpdateCamera: Abstand so, dass die Sichtbreite genau ins Bild passt; Neigung je Zoomstufe, Norden oben.</summary>
    public void UpdateCamera(bool snap)
    {
        var terrain = _ctx?.Terrain; var cam = terrain?.Camera;
        if (terrain == null || cam == null) return;
        float tw = WidthFor(Zoom), ti = InclFor(Zoom);
        if (snap || CurWidth <= 0f) { CurWidth = tw; _curIncl = ti; }
        else
        {
            float k = 1f - MathF.Exp(-Smooth * LastDt);
            CurWidth = MathF.Exp(MathF.Log(CurWidth) + (MathF.Log(tw) - MathF.Log(CurWidth)) * k);
            _curIncl += (ti - _curIncl) * k;
        }
        float aspect = FlashScreen.height > 0 ? (float)FlashScreen.width / FlashScreen.height : 16f / 9f;
        float vfov = FieldOfView * MathF.PI / 180f;
        float hfov = 2f * MathF.Atan(MathF.Tan(vfov * 0.5f) * MathF.Max(0.1f, aspect));
        float dist = CurWidth * 0.5f / MathF.Tan(hfov * 0.5f);
        float incl = _curIncl * MathF.PI / 180f;
        var north = North;
        var pos = Target + Vector3.UnitY * (dist * MathF.Cos(incl)) - north * (dist * MathF.Sin(incl));
        var fwd = Vector3.Normalize(Target - pos);
        // Oben = Norden, senkrecht zur Blickrichtung (Unity LookRotation(fwd, north))
        var up = Vector3.Normalize(north - fwd * Vector3.Dot(north, fwd));
        cam.Position = pos;
        cam.Override = (fwd, up);
        terrain.MapMetersPerPixel = ShowRoads ? CurWidth / MathF.Max(1f, FlashScreen.width) : 0f;
        terrain.FieldOfView = FieldOfView;
        terrain.NearClip = MathF.Max(1f, dist * 0.02f);
        terrain.FarClip = dist * 3f + 20000f;
    }
}
