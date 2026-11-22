using Paradise.Core.World;

namespace Paradise.Game.Config;

/// <summary>
/// Einstellungen des Cruising-/Road-Discovery-Systems.
/// Achtung: die konkreten Punktwerte und Level-Schwellen sind [nicht aus EXE bestimmt].
/// Sie liegen bewusst hier in der Engine-Konfiguration und nicht in der Doku/EXE-Analyse.
/// Überschreibbar über GamePC.cpr ([CruisingDiscovery], SET_CRUISE_*).
/// </summary>
public static class DiscoveryConfig
{
    /// <summary>Abtastintervall beim Fahren (s). Heuristik aus der C++-Voranalyse, kein EXE-Fakt.</summary>
    public static float TrackInterval = 0.25f;
    /// <summary>Erkennungsradius zu einem Straßenstück (m). Heuristik aus der C++-Voranalyse.</summary>
    public static float DetectRadius = 18f;
    /// <summary>Punkte pro neu befahrenen 50 m. [nicht aus EXE bestimmt]</summary>
    public static int PointsPer50Meters = 1;
    /// <summary>Punkte der Area-Meilensteine (10/25/50/75/100 %). [nicht aus EXE bestimmt]</summary>
    public static int[] MilestonePoints = { 40, 50, 90, 120, 150 };
    /// <summary>Area-Meilensteine in Prozent.</summary>
    public static int[] MilestonePercent = { 10, 25, 50, 75, 100 };
    /// <summary>Cruising-Level-Schwellen (Punkte). [nicht aus EXE bestimmt]</summary>
    public static int[] LevelThresholds =
        { 0, 125, 300, 525, 825, 1200, 1650, 2175, 2795, 3510, 4320, 5225, 6225, 7355, 8615, 10000 };

    /// <summary>Liest optionale SET_CRUISE_*-Einträge aus GamePC.cpr; ohne Datei bleiben die Vorgaben oben.</summary>
    public static void Apply(GamePC? cfg)
    {
        if (cfg == null) return;
        foreach (var e in cfg.Entries)
        {
            if (e.Args.Length == 0 || !float.TryParse(e.Args[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float v)) continue;
            if (e.Key.Equals("SET_CRUISE_TRACK_INTERVAL", System.StringComparison.OrdinalIgnoreCase)) TrackInterval = Clamp(v, 0.05f, 2f);
            else if (e.Key.Equals("SET_CRUISE_DETECT_RADIUS", System.StringComparison.OrdinalIgnoreCase)) DetectRadius = Clamp(v, 2f, 100f);
            else if (e.Key.Equals("SET_CRUISE_POINTS_PER_50M", System.StringComparison.OrdinalIgnoreCase)) PointsPer50Meters = (int)Clamp(v, 0, 1000);
            else if (e.Key.StartsWith("SET_CRUISE_LEVEL_", System.StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(e.Key.AsSpan("SET_CRUISE_LEVEL_".Length), out int lvl) && lvl >= 0 && lvl < LevelThresholds.Length)
                LevelThresholds[lvl] = (int)Clamp(v, 0, 10_000_000);
        }
    }

    static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);

    /// <summary>Level zu einer Punktzahl (höchste erreichte Schwelle).</summary>
    public static int LevelForPoints(int points)
    {
        int level = 0;
        for (int i = 0; i < LevelThresholds.Length; i++)
            if (points >= LevelThresholds[i]) level = i;
        return level;
    }

    /// <summary>Nächste Schwelle (für die Ratio-Anzeige NB-PT * 100 / MX-PT).</summary>
    public static int NextThreshold(int points)
    {
        for (int i = 0; i < LevelThresholds.Length; i++)
            if (LevelThresholds[i] > points) return LevelThresholds[i];
        return LevelThresholds.Length > 0 ? LevelThresholds[^1] : 0;
    }
}
