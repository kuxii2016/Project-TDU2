// Portiert aus dem Unity-Projekt (Runtime/Racing/RaceDefinition.cs).
using Paradise.Core.Math;
using System.Numerics;

namespace Paradise.Core.Racing;

/// <summary>Testart einer Rennschul-Prüfung (ig2 "rsl2"). Reihenfolge = Zeigertabelle der EXE (0x1674d04),
/// die Namen sind die Frame-Labels des Typ-Symbols in r_school.vmf (/tests/lineN/icons/type).</summary>
public enum SchoolTestType { Turn = 0, Turn90, Turn180, Slalom, Overtaking, Drift, Acceleration, Braking, Mixed, Circuit, Manual, Obstacle, Jump }

/// <summary>Ort der Prüfung (ig2 "rsl1"): Schulgelände oder offene Straße (Labels "r_school" / "road").</summary>
public enum SchoolTestPlace { School = 0, Road = 1 }

/// <summary>Wetter der Prüfung (ig2 "rswh"): Labels "sun" / "rain" / "night".</summary>
public enum SchoolTestWeather { Sun = 0, Rain = 1, Night = 2 }

/// <summary>Deko einer Prüfung: einzelner Kegel / Absperrung oder Linie von A nach B (Inselkoordinaten).</summary>
public sealed class RaceDecor
{
    public bool IsSeparator;   // false = Kegel, true = Absperrung ("Separ")
    public bool IsLine;
    public Vector3 From, To;   // bei Einzelobjekten From == To
    public Vector3 Forward;    // Ausrichtung des Einzelobjekts
}

/// <summary>Fahrzeug einer Prüfung (VEHI), z.B. die zu überholenden Autos.</summary>
public sealed class RaceOpponentDef
{
    public int CarId;           // DBCarPhysicsData._id_car
    public Vector3 Position;    // Inselkoordinaten
    public Vector3 Forward;
    public float MaxSpeed;      // m/s (Spmx / 1000)
    public int StartCheckpoint; // Stch (vermutet: fährt los, sobald der Spieler diesen Checkpoint erreicht)
    // --- Meisterschafts-Gegner (Startaufstellung)
    public string? Name;        // Fahrername (DBPNJ)
    public float Accel = 4f;    // m/s²
    public float LatAccel;      // Querbeschleunigung in Kurven (m/s²); 0 = Kurven nicht beachten (Prüfungsfahrzeuge)
    public float LateralOffset; // seitlicher Abstand zur Ideallinie (m)
    public List<Vector3>? PathOverride;   // feste Route (Inselkoordinaten), sonst durch die Tore
    public float StartDistance; // Weg bis zur Startlinie (m, negativ = dahinter) für die Platzierung
    // --- Fahrergesetz (opponents_spec.md): aus _difficulty_bot_n / _aggressiveness_bot_n (0..1)
    public float Difficulty;     // Stärke des Fahrers (0..1)
    public float Aggressiveness; // Bereitschaft zu überholen (0..1)
    public float Brake = 7.5f;   // Bremsverzögerung (m/s²), 7.5 + 3 * Difficulty
}

/// <summary>Spielart einer Challenge (ig2 "gmth"). Werte aus der Spielart-Tabelle der exe (.data 0x164a840: Name, Text-ID, gmth):
/// timeAttack 0 (Text 0x263 "Zeit"), speedtrap 1 (0x264 "Tempo"), driving 2 (0x265), race 3 (0x266 "Rennen"), event 7 (0x267),
/// hotspots 9, speed 10 (0x264), keepYourDistance 11, followTheLeader 12. Bezeichner der HUD-Ansicht (/:cur_gm) siehe
/// Paradise.Game.Racing.ChallengeModes.</summary>
public enum RaceMode { TimeAttack = 0, SpeedTrap = 1, Driving = 2, Race = 3, Event = 7, HotSpots = 9, Speed = 10, KeepYourDistance = 11, FollowTheLeader = 12 }

/// <summary>
/// Eine fahrbare Prüfung/ein Rennen: Startpunkt, geordnete Checkpoints, Runden, Zeiten, Auto, Props.
/// Zusammengesetzt aus DBLicense (Lizenz), spotworld.bin (Startpunkt) und der .ig2-Datei (Tore, Einstellungen).
/// </summary>
public sealed class RaceDefinition
{
    public string Name = "";        // z.B. "RSA1T06"
    public ulong LicenseHash;
    public char School;             // 'A' Asphalt, 'B' Dirt, 'C' Classic (nur Rennschule)
    public int Island;
    public uint Mode;               // ig2 "gmth" (0 = Zeitfahren)
    public int Laps = 1;            // effektive Runden (nblp nur bei Rundkursen, sonst 1)
    public int LapsRaw;             // ig2 "nblp" wie gespeichert
    public bool IsCircuit;          // ig2 "#" = 1
    public float Length;            // ig2 "leme" (Meter)
    public Vector3 StartPosition;   // Startposition (Inselkoordinaten)
    public float StartHeading;      // Startrichtung aus spotworld.bin (Bogenmaß)
    public bool HasSpotStart;       // Startpunkt stammt aus spotworld.bin
    public int TargetTimeMs;        // Referenz-/Zielzeit "timG" (Gold); bestanden, wenn Zeit+Strafen <= Ziel
    public int SilverTimeMs, BronzeTimeMs;
    public bool HasTimeLimit;       // ig2 "ChCD" = 1: Countdown läuft, Ablauf = nicht bestanden
    public int TimeLimitMs;         // ig2 "CtDw" (bei ChCD = 0: Abbruch nach dieser Zeit)
    public int RewardMoney;         // "rewm"
    /// <summary>ig2 "tras" / 1000 (m/s, Einheit wie Fahrzeug +0x1f0). Radar (gmth 1): Mindesttempo, ab dem ein Radar blitzt und
    /// misst (exe 0xb649c4). Tempo (gmth 10): Mindesttempo, über dem Punkte zählen (exe GMCompSpeed Init 0xb60e89).</summary>
    public float SpeedTrapMin;
    /// <summary>ig2 "CpTs": Referenz-Zwischenzeiten (ms, kumuliert) – Eintrag 0 = Startlinie, dann je passiertem Tor einer.
    /// Format: u32 BE Kapazität (100), danach u32 BE Zeiten, 0 = leer (ch1n1a5t: 2366 … 136955 bei timG 138665).</summary>
    public readonly List<int> RefSplitsMs = new();
    /// <summary>ig2 "SPPG" / 1000: Referenzpunkte der Tempo-Prüfung (gmth 10, exe 0xb60eeb -> /:SpeedRef, HUD/ref_points).</summary>
    public float SpeedPointsRef;
    public int CarId;               // "VMod": Auto der Prüfung (DBCarPhysicsData._id_car)
    public int PenaltyLevel;        // "PeLv" (0..2) -> DBEnvironnement.PenaltyTimeLevel01..03
    public bool OffroadPenalty;     // "offR"
    public SchoolTestType TestType;
    public SchoolTestPlace Place;
    public SchoolTestWeather Weather;
    public bool IsGradedTest;       // "TEST"
    public ulong NameHc, DescHc;    // "dbna"/"dbd1": Codes der Textdatenbank (db_<sprache>.cpr)

    public readonly List<RaceCheckpoint> Checkpoints = new();
    public readonly List<RaceDecor> Decors = new();
    public readonly List<RaceOpponentDef> Opponents = new();

    /// <summary>Route entlang der Straßen (Inselkoordinaten), siehe RaceRoadPath (Game/Racing); null = nicht berechnet.</summary>
    public List<Vector3>? RoadPath;
    /// <summary>Je Punkt von <see cref="RoadPath"/>: Checkpoint-Index des Abschnitts (für die Färbung).</summary>
    public List<int>? RoadPathSeg;

    public RaceCheckpoint? Start => Checkpoints.Count > 0 ? Checkpoints[0] : null;
    public RaceCheckpoint? Finish => Checkpoints.Count > 0 ? Checkpoints[^1] : null;
    public bool EndsInBrakingZone => !IsCircuit && Finish != null && Finish.IsBrakingZone;

    /// <summary>Reihenfolge der anzufahrenden Checkpoints (Indizes in <see cref="Checkpoints"/>), ohne die Startlinie.
    /// Punkt-zu-Punkt: 1..n-1. Rundkurs: je Runde 1..n-1 und wieder 0 (Start-/Ziellinie).</summary>
    public List<int> TargetSequence()
    {
        var seq = new List<int>();
        int n = Checkpoints.Count;
        if (n < 2) return seq;
        int laps = IsCircuit ? Mathf.Max(1, Laps) : 1;
        for (int l = 0; l < laps; l++)
        {
            for (int i = 1; i < n; i++) seq.Add(i);
            if (IsCircuit) seq.Add(0);
        }
        return seq;
    }

    public int TargetCount => TargetSequence().Count;
    /// <summary>Radar (gmth 1): Zahl der Radare = Tore ohne die Startlinie (exe GMCompTraps 0xb646e3: Torzahl − 1, mind. 0).</summary>
    public int RadarCount => System.Math.Max(0, Checkpoints.Count - 1);
    public int TargetsPerLap => Checkpoints.Count < 2 ? 0 : (IsCircuit ? Checkpoints.Count : Checkpoints.Count - 1);
    public string Title => string.IsNullOrEmpty(Name) ? Name : Name.ToUpperInvariant();
    public RaceMode Kind => (RaceMode)Mode;
    /// <summary>Eliminator („der Letzte fliegt raus“, exe 0xb3c450): Rennen (gmth 3), Rundkurs ("#" ≥ 1) und nblp = 0.
    /// Die Runden ergeben sich aus den Teilnehmern (n − 1, exe 0xb5a0b8).</summary>
    public bool IsEliminator => Kind == RaceMode.Race && IsCircuit && LapsRaw == 0;
    public int BotCount;            // ig2 "NbIA"

    /// <summary>
    /// Flag für die sichtbare Fahrbahnlinie ("Trajectory" aus pack_cp.bnk): nur bei freigestellten Toren
    /// (ig2 CHK2 Placement != 0, also Prüfungen auf dem Schulgelände). Sobald ein Tor an einer Straße hängt
    /// (Placement 0), zeichnet das Original kein Band, weil eine gerade Verbindung durch Häuser laufen würde.
    /// Radar (gmth = 1) zeigt grundsätzlich nur blaue Portal-Punkte auf der Minimap – kein Band und keine Route
    /// (siehe RaceConfig.MinimapRoute).
    /// </summary>
    public bool ShowDrivingLine => Kind != RaceMode.SpeedTrap && Checkpoints.Count > 1 && !Checkpoints.Exists(c => c.OnRoad);
}
