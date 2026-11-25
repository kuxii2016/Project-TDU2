using Paradise.Core.Avatars;
using Paradise.Core.Formats;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Anprobe-Avatar der Avatar-Läden (Friseur, Kleidungsladen, Garderobe – Unity TDU2Hairdresser/TDU2ClothesShop ShowPreview/Pose):
/// steht an einem Darsteller-Platz der Interieur-Szene (Szenenrahmen, Spielraum), spielt seine Pose in Schleife und wird beim Ändern
/// des Aussehens im Hintergrund neu aufgebaut; der alte bleibt sichtbar, bis der neue fertig ist.
/// </summary>
internal sealed class AvatarPreview : IDisposable
{
    readonly IRenderer _r;
    AvatarModel? _shown, _pending;
    string? _code, _look;
    /// <summary>Lage im Szenenrahmen der Interieur-Szene.</summary>
    public Matrix4x4 Pose = Matrix4x4.Identity;
    public AnmClip? Clip;
    public float T;
    /// <summary>Wurzel-Versatz der Animation übernehmen (Unity ApplyClip positions); Mitspieler beim Laufen: aus (Position kommt aus dem Netz).</summary>
    public bool ClipPositions = true;

    public AvatarPreview(IRenderer r) { _r = r; }

    public AvatarModel? Avatar => _shown;

    /// <summary>Aussehen zeigen (neu aufbauen, wenn es sich geändert hat oder <paramref name="force"/>).</summary>
    public void Show(AvatarConfig cfg, bool force)
    {
        string code = cfg.Encode();
        if (!force && code == _code) return;
        // Nur das Gesicht anders (Schönheitsklinik: Regler, Augen, Brauen, Haut): Modell behalten, Gesicht/Materialien direkt setzen
        string look = cfg.EncodeLook();
        if (!force && look == _look && (_shown != null || _pending != null))
        {
            _code = code;
            _shown?.SetFace(cfg);
            _pending?.SetFace(cfg);
            return;
        }
        _code = code; _look = look;
        _pending?.Dispose();
        _pending = AvatarModel.Create(_r, cfg.Clone());
        _pending.Indoor = true;
    }

    /// <summary>Darsteller-Platz und Clip aus der Szene (Unity PlaceAvatar).</summary>
    public void Place(CineScene.Obj? act, string? clip, int sex, string fallbackM, string fallbackW)
    {
        if (act != null) Pose = act.Matrix;
        string c = !string.IsNullOrEmpty(clip) ? clip! : fallbackM;
        bool woman = sex == 1;
        if (woman && c.EndsWith("-m", StringComparison.Ordinal)) c = c[..^2] + "-w";
        Clip = InteriorNpcs.Clip(c) ?? AvatarLibrary.Clip(woman ? fallbackW : fallbackM);
        T = 0f;
    }

    public void Update(float dt)
    {
        T += dt;
        _shown?.Update();
        if (_pending != null)
        {
            _pending.Update();
            if (_pending.Ready) { _shown?.Dispose(); _shown = _pending; _pending = null; }
        }
    }

    public void Render(Matrix4x4 sceneToRender)
    {
        var a = _shown;
        if (a == null || !a.Ready || a.Rig == null) return;
        a.Rig.BeginPose();
        if (Clip != null)
        {
            a.Rig.ApplyClipLoop(Clip, T, 1f, ClipPositions);   // Schleife mit Überblendung (kein Sprung Ende -> Anfang)
        }
        a.Draw(Pose * sceneToRender);
    }

    /// <summary>Knochenposition im Szenenrahmen (z.B. "HEAD" für die Kamera), null wenn nicht geladen.</summary>
    public Vector3? BoneInScene(string bone)
    {
        var a = _shown;
        if (a?.Rig == null) return null;
        int i = a.Rig.Bone(bone);
        return i >= 0 ? Vector3.Transform(a.Rig.BonePosition(i), Pose) : null;
    }

    public void Dispose()
    {
        _shown?.Dispose(); _pending?.Dispose();
        _shown = _pending = null; _code = null; _look = null;
    }
}
