using Paradise.Core.Avatars;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Kinos des Raums beim Betreten (exe 0xA7E200–0xA7E4C0, Interieur-Start): Eingangs-Kino als Haupt-Kino mit Kamera – der Reihe nach
/// C_REMOVAL_I (Umzug, nur nach einem Hauskauf: 0x1722952 = <see cref="RemovalPending"/>), C_INTRO_I, C_INTRO_I_1, C_INTRO_I_2, C_INTRO
/// (das erste, das die Bank hat) –, danach Laufen; jede Definition
/// "C_LIFE" der Bank läuft als Hintergrund (Verkäufer, Friseurin, Ärztin …) ab der Startzeit des Raums. Die Modi der Läden
/// (Friseur, Kleidung, Klinik) haben eigene Kinos; dort wird das des Raums nicht gezeichnet.
/// </summary>
public abstract partial class InteriorUtils
{
    static SpotCinematics? _roomCin;
    static bool _roomIntro;

    /// <summary>Umzug nach einem Hauskauf (exe 0x1722952, gesetzt mit dem Exterior-Kino C_REMOVAL): das nächste Interieur mit C_REMOVAL_I
    /// spielt dieses als Eingangs-Kino.</summary>
    internal static bool RemovalPending;

    /// <summary>Nach dem Aufbau des Interieurs: Bank-Kinos öffnen, Eingangs-Kino und C_LIFE starten.</summary>
    static void StartRoomCinematics()
    {
        EndRoomCinematics();
        if (_v?.Renderer == null || string.IsNullOrEmpty(_pendingBank)) return;
        _roomCin = SpotCinematics.Open(_v.Renderer, _pendingBank);
        if (_roomCin == null) return;
        _roomCin.Frame = _holder;
        _roomCin.Indoor = true;
        _roomCin.SetPlayer(AvatarConfigs.Local.Clone());
        if (RemovalPending && _roomCin.Find("C_REMOVAL_I") != null && _roomCin.PlayMain("C_REMOVAL_I")) { _roomIntro = true; RemovalPending = false; }
        if (!_roomIntro)
            foreach (var n in new[] { "C_INTRO_I", "C_INTRO_I_1", "C_INTRO_I_2", "C_INTRO" })
                if (_roomCin.Find(n) != null && _roomCin.PlayMain(n)) { _roomIntro = true; break; }
        if (_roomCin.Find("C_LIFE") != null) _roomCin.PlayBackground("C_LIFE", true);
    }

    static void EndRoomCinematics()
    {
        if (_roomCin != null) InteriorNpcs.Suppressed = null;
        _roomCin?.Dispose(); _roomCin = null;
        if (_roomIntro && _ui != null) _ui.Visible = true;
        _roomIntro = false;
    }

    /// <summary>
    /// Jedes Bild im Laufmodus des Raums: Kinos weiter, ihre Figuren aus den Raum-NPCs nehmen. true, solange das Eingangs-Kino läuft
    /// (kein Laufen, Kamera des Kinos, Film des Raums aus; Bestätigen überspringt).
    /// </summary>
    static bool RoomCinemaStep(float dt, bool paused)
    {
        var cin = _roomCin;
        if (cin == null) return false;
        if (!paused) { cin.Update(dt); InteriorNpcs.Suppressed = cin.ActiveActors; }
        if (!_roomIntro) return false;
        if (!cin.MainPlaying || (!paused && ShopCinema.SkipPressed()))
        {
            _roomIntro = false;
            cin.StopMain();
            if (_ui != null) _ui.Visible = true;
            return false;
        }
        if (_ui != null) _ui.Visible = false;
        _t.Input.CursorLocked = false;
        ShopCinema.Camera(cin, false);
        return true;
    }

    /// <summary>Raum-Kinos zeichnen (nur im Laufmodus des Raums).</summary>
    protected void RenderRoomCinematics()
    {
        var st = _t?.Streamer;
        if (_roomCin != null && st != null && Mode == RoomMode) _roomCin.Render(st.RootMatrix);
    }
}
