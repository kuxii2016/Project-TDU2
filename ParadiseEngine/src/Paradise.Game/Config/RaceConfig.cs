namespace Paradise.Game.Config;

/// <summary>
/// Rennen und Rennschule (Game/Racing, GAME_MODE_SCHOOL). Grundwerte wie im Unity-Projekt (TDU2RaceManager, RacingSchoolMenu,
/// RaceAutopilot, RaceProps, RaceCinematics); das Entwicklermenü (F1 > Race Debug) kann sie zur Laufzeit ändern.
/// </summary>
public static class RaceConfig
{
    // ---- TDU2RaceManager
    /// <summary>Tore (Checkpoint-Modelle aus pack_cp.bnk) zeigen.</summary>
    public static bool ShowGates = true;
    /// <summary>Kegel/Absperrungen (ig2 DEC2, worldactors.bnk) aufstellen.</summary>
    public static bool SpawnProps = true;
    /// <summary>Dauer des 3-2-1-GO-Countdowns (s, Echtzeit) – im HUD-Film bis "go" ca. 3,7 s bei 30 fps.</summary>
    public static float CountdownSeconds = 3.7f;
    /// <summary>Zeitstrafe je Treffer (s) – Original: DBEnvironnement.PenaltyTimeLevel01..03 = 10 s.</summary>
    public static float PenaltySeconds = 10f;
    /// <summary>Strafen auch bei Prüfungen ohne Strafstufe (PeLv = 0) geben.</summary>
    public static bool PenaltiesAlways = false;
    /// <summary>Mindest-Aufprallgeschwindigkeit (m/s) für eine Strafe.</summary>
    public static float PenaltyMinImpact = 2.5f;
    /// <summary>Max. Wartezeit (s) auf das Streaming der Strecke vor dem Start.</summary>
    public static float MaxPrepareSeconds = 20f;

    // ---- RacingSchoolMenu
    /// <summary>Alle Lizenzen freischalten (Test/Debug) – im Original nur über die Voraussetzungen.</summary>
    public static bool UnlockAllLicences = false;
    /// <summary>Immer das Auto der Prüfung verwenden (ig2 VMod) wie im Spiel.</summary>
    public static bool UseTestCar = true;
    /// <summary>Ab so vielen Fehlversuchen hintereinander gibt es mehr Zeit (0 = nie).</summary>
    public static int EaseAfterFails = 2;
    /// <summary>Zusätzliche Zeit je weiterem Fehlversuch (Anteil der Original-Zeit, 0.08 = +8 %).</summary>
    public static float EaseStep = 0.08f;
    /// <summary>Höchstens so viel zusätzliche Zeit (Anteil).</summary>
    public static float EaseMax = 0.50f;
    /// <summary>Schule, falls der Spot-Code nicht ermittelt werden kann ('A', 'B', 'C'; leer = alle der Insel).</summary>
    public static string FallbackSchool = "";
    /// <summary>"Zurück zur Akademie": wieder ins Rennschul-Interieur (wie im Spiel) statt vor die Schule.</summary>
    public static bool ReturnIntoInterior = true;
    /// <summary>Während einer Prüfung Mitspieler ausblenden und selbst unsichtbar sein (Unity TDU2OnlineSettings.HidePlayersInRacingSchool).</summary>
    public static bool HidePlayersInRacingSchool = true;

    // ---- RaceAutopilot
    /// <summary>Tempo, auf das nach dem Ziel heruntergebremst wird (km/h); 0 = anhalten.</summary>
    public static float AutopilotCruiseKmh = 35f;
    /// <summary>Maximaler Abstand zur nächsten Spur (m), sonst nur anhalten.</summary>
    public static float AutopilotSnapDistance = 14f;

    // ---- RaceCinematics
    /// <summary>Kinos vor/nach Rennen abspielen (aus = direkt weiter).</summary>
    public static bool Cinematics = true;

    // ---- RaceRoadPath
    public static float RoadSnapRadius = 30f;     // Wegpunkt -> Straße
    public static float RoadGoalRadius = 18f;     // Spur führt am Ziel vorbei
    public static int RoadMaxExpand = 6000;
}
