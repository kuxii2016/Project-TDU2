// Portiert aus dem Unity-Projekt (Runtime/Story/TDU2Story.cs: StoryWalk).
using Paradise.Core.World;
using System.Numerics;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Laufmodus auf einer Story-Szene (OpenTDU2 WalkMode, Unity StoryWalk): Ich-Perspektive (<see cref="InteriorUtils.Walker"/>, Unity
/// TDU2FirstPerson) ab einer Entity__Camera, Auswahl der FpSelectables (vor dem Auge, näher als 10 m, Strahl trifft das Volumen,
/// nicht verdeckt; 0,2 s Sperre) mit INTRO-Film ItemType('over'/'out') und /:SelItemType.
/// </summary>
internal sealed class StoryWalk
{
    const float SelectRadius = 10f, Cooldown = 0.2f;
    static readonly Dictionary<int, string> TypeNames = new()
    {
        { 0, "exit" }, { 1, "carseller" }, { 2, "player" }, { 3, "carrenter" }, { 4, "stolencar" }, { 5, "garage" }, { 6, "living" },
        { 7, "realtor" }, { 8, "photographer" }, { 9, "diary" }, { 10, "dressing" }, { 17, "npc" }, { 20, "tutor" }, { 22, "goodies" },
        { 49, "game_progress" }, { 50, "bar" }, { 51, "office" }, { 52, "photos" }, { 54, "club" }, { 56, "jacuzzi" },
    };

    InteriorUtils.Walker? _fp;
    bool _enabled;
    float _fov = 60f;
    StoryScene? _scene;
    float _cooldown;
    public int Selected { get; private set; } = -1;
    public Func<int, bool>? Filter;

    public bool Active => _fp != null && _enabled;

    public bool Start(StoryScene scene, string cameraName, float fovDeg)
    {
        _scene = scene;
        if (!scene.EntityCamera(cameraName, out var eye, out var fwd))
            Paradise.Core.Diagnostics.Log.Warn("Laufmodus: Kamera " + cameraName + " fehlt");
        // Boden unter der Startkamera (10 m), Auge 1,6 m darüber
        var phys = Story.Terrain?.Physics;
        if (phys != null && phys.Raycast(eye + Vector3.UnitY * 0.1f, -Vector3.UnitY, 10f, out var hit)) eye.Y = hit.Point.Y + 1.6f;
        _fp ??= new InteriorUtils.Walker { EyeHeight = 1.6f };
        _fov = fovDeg;
        _enabled = true;
        _fp.LookEnabled = true;
        _fp.Place(eye, fwd);
        scene.CameraOverride = true;
        Selected = -1;
        ApplyCamera();
        return true;
    }

    public void Resume() { if (_fp != null) { _enabled = true; _fp.LookEnabled = true; } if (_scene != null) _scene.CameraOverride = true; }
    public void Pause() { _enabled = false; if (_scene != null) _scene.CameraOverride = false; }

    public void Stop()
    {
        _fp = null; _enabled = false;
        if (_scene != null) _scene.CameraOverride = false;
    }

    /// <summary>Auge/Blick im Spielraum.</summary>
    public Vector3 EyeWorld => _fp != null ? _fp.Eye : Vector3.Zero;
    public Vector3 ForwardWorld => _fp != null ? _fp.Forward : Vector3.UnitZ;
    public Vector3 EyeLocal => _scene != null ? _scene.ToLocal(EyeWorld) : EyeWorld;

    public string TypeOf(int i)
    {
        var sel = _scene?.Selectables;
        if (sel == null || i < 0 || i >= sel.Count) return "";
        var v = sel[i];
        if (v.Type == 57) return v.Str;
        return TypeNames.TryGetValue(v.Type, out var n) ? n : (string.IsNullOrEmpty(v.Str) ? "" : v.Str);
    }

    public string SelectedType => TypeOf(Selected);

    public void Clear()
    {
        if (Selected >= 0) _scene?.Ui?.Call("/", "ItemType", "out");
        Selected = -1;
    }

    void ApplyCamera()
    {
        if (_fp == null || _scene == null) return;
        _scene.SetCamera(_fp.Eye, _fp.Forward, _fp.Up, _fov);
    }

    public void Update(float dt)
    {
        if (!Active || _scene == null || _fp == null) return;
        var t = Story.Terrain;
        if (t != null) _fp.Update(dt, t.Input, t.Physics);
        ApplyCamera();
        _cooldown -= dt;
        var o = EyeLocal;
        var d = _scene.ToLocalDir(ForwardWorld);
        if (d.LengthSquared() > 1e-8f) d = Vector3.Normalize(d);
        var sel = _scene.Selectables;
        int best = -1; float bestD2 = SelectRadius * SelectRadius;
        for (int i = 0; i < sel.Count; i++)
        {
            if (Filter != null && !Filter(i)) continue;
            var v = sel[i];
            var to = Center(v) - o;
            float d2 = to.LengthSquared();
            if (Vector3.Dot(to, d) <= 0f || d2 >= bestD2) continue;
            if (!RayVolume(v, o, d, SelectRadius * 2f, out float tExit)) continue;
            // verdeckt? (Szenen-Kollision bis zur Rückseite des Volumens)
            var phys = t?.Physics;
            if (phys != null && phys.Raycast(EyeWorld, ForwardWorld, tExit, out var hit) && !v.Contains(_scene.ToLocal(hit.Point))) continue;
            best = i; bestD2 = d2;
        }
        if (best == Selected || (_cooldown > 0f && best != -1)) return;
        var ui = _scene.Ui;
        if (Selected >= 0) ui?.Call("/", "ItemType", "out");
        Selected = best;
        _cooldown = Cooldown;
        if (best >= 0 && ui != null)
        {
            ui.SetVariable("/:SelItemType", TypeOf(best));
            ui.Call("/", "ItemType", "over");
        }
    }

    /// <summary>Mittelpunkt des Auswahlvolumens (Szenenraum): Volume bildet auf den Einheitswürfel ab, Mitte = Volume⁻¹ · 0.</summary>
    static Vector3 Center(InteriorScene.Selectable v) => Matrix4x4.Invert(v.Volume, out var inv) ? inv.Translation : v.Position;

    /// <summary>Strahl (Szenenraum) gegen den Einheitswürfel ±0,5 des Volumens: trifft er innerhalb maxDist, mit dem Austrittspunkt.</summary>
    static bool RayVolume(InteriorScene.Selectable v, Vector3 o, Vector3 d, float maxDist, out float tExit)
    {
        tExit = 0f;
        var lo = Vector3.Transform(o, v.Volume);
        var ld = Vector3.TransformNormal(d, v.Volume);
        float t0 = 0f, t1 = maxDist;
        for (int i = 0; i < 3; i++)
        {
            float oi = i == 0 ? lo.X : i == 1 ? lo.Y : lo.Z, di = i == 0 ? ld.X : i == 1 ? ld.Y : ld.Z;
            if (MathF.Abs(di) < 1e-8f) { if (oi < -0.5f || oi > 0.5f) return false; continue; }
            float a = (-0.5f - oi) / di, b = (0.5f - oi) / di;
            if (a > b) (a, b) = (b, a);
            t0 = MathF.Max(t0, a); t1 = MathF.Min(t1, b);
            if (t0 > t1) return false;
        }
        tExit = t1;
        return true;
    }
}
