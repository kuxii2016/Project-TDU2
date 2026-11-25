using Paradise.Core.Avatars;
using Paradise.Game.Input;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Gemeinsame Kino-Steuerung der Avatar-Läden (Friseur, Schönheitsklinik, Kleidungsladen) – die Bank des Interieurs (z.B. ihd_b,
/// isur_a, ias_a) über <see cref="SpotCinematics"/>:
///   - Menü-Drehung (Friseur 0xBA1CD0 / Klinik 0xBEEB60): C_360 als Haupt-Kino an fester Zeit halten, Anteil = 0,95 / Länge
///     (double @0x149c098), Zeit = Anteil x Länge; Drehen (0xB9FD90 / 0xBF0EE9): Anteil += (rechts + Maus - links) x dt, [0, 0,95].
///   - Kamera der laufenden Haupt-Cinematic, Figuren des Kinos aus den Raum-NPCs nehmen (InteriorNpcs.Suppressed).
/// </summary>
internal static class ShopCinema
{
    /// <summary>Bank des Interieurs öffnen (Spieler-Darsteller = <paramref name="player"/>); null ohne Bank.</summary>
    public static SpotCinematics? Open(AvatarConfig player, bool castPlayer = true)
    {
        var r = InteriorUtils._v?.Renderer;
        if (r == null) return null;
        var cin = SpotCinematics.Open(r, InteriorUtils._pendingBank);
        if (cin == null) return null;
        cin.Frame = InteriorUtils._holder;
        cin.Indoor = true;
        cin.CastPlayer = castPlayer;
        cin.SetPlayer(player.Clone());
        return cin;
    }

    /// <summary>C_360 halten (plus Hintergründe, z.B. C_HEAD, C_LIFE); false ohne C_360 in der Bank.</summary>
    public static bool Start360(SpotCinematics cin, out float rot, params string[] backgrounds)
    {
        rot = 0f;
        if (cin.Find("C_360") == null) return false;
        cin.StopMain();
        foreach (var b in backgrounds) if (cin.Find(b) != null) cin.PlayBackground(b, true);
        cin.PlayMain("C_360", false);
        float len = MathF.Max(0.05f, cin.MainDuration);
        rot = 0.95f / len;
        cin.MainHold = rot * len;
        return true;
    }

    /// <summary>Drehen mit rechtem Stick / Drehtasten / rechter Maustaste (Aktion 0x6A: -(2 x Mausachse) x Faktor).</summary>
    public static void Rotate(SpotCinematics cin, ref float rot, float dt, float mouseScale)
    {
        var t = InteriorUtils._t;
        if (t == null) return;
        float mouse = 0f;
        var input = t.Input;
        if (input.RightMouseDown) mouse = -2f * input.MouseDeltaX * mouseScale;
        var look = TDU2InputDevices.Look;
        float right = MathF.Max(0f, look.X) + (TDU2Controls.Held("walk_turnr") ? 1f : 0f);
        float left = MathF.Max(0f, -look.X) + (TDU2Controls.Held("walk_turnl") ? 1f : 0f);
        rot += (right + mouse - left) * dt;
        if (rot < 0f) rot = 0f;
        if (rot > 0.95f) rot = 0.95f;
        cin.MainHold = rot * MathF.Max(0.05f, cin.MainDuration);
    }

    /// <summary>Kamera der Haupt-Cinematic übernehmen; false, wenn das Kino keine Kamera liefert.</summary>
    public static bool Camera(SpotCinematics cin, bool outdoor)
    {
        var t = InteriorUtils._t;
        if (t == null || !cin.Camera(out var eye, out var fwd, out var up, out float fov)) return false;
        var cam = t.Camera; var st = t.Streamer;
        if (cam == null || st == null) return false;
        t.FlyCameraEnabled = false;
        cam.Position = st.GameToRender(eye);
        cam.Override = (fwd, up);
        t.FieldOfView = fov;
        t.NearClip = outdoor ? 0.1f : 0.03f;
        return true;
    }

    /// <summary>Kino weiterlaufen lassen und seine Figuren aus den Raum-NPCs nehmen.</summary>
    public static void Update(SpotCinematics? cin, float dt)
    {
        if (cin == null) return;
        cin.Update(dt);
        InteriorNpcs.Suppressed = cin.ActiveActors;
    }

    /// <summary>Überspringen eines Laden-/Raum-Kinos: Bestätigen oder Zurück (Tastatur, Maus, Pad).</summary>
    public static bool SkipPressed() => TDU2Controls.ConfirmPressed() || TDU2Controls.BackPressed();

    /// <summary>Kino beenden (Figuren zurück an die Raum-NPCs).</summary>
    public static void Close(ref SpotCinematics? cin)
    {
        InteriorNpcs.Suppressed = null;
        cin?.Dispose(); cin = null;
    }
}
