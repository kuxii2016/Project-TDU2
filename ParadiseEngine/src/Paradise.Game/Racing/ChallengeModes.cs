using Paradise.Core.Flash;
using Paradise.Core.Racing;
using System.Globalization;

namespace Paradise.Game.Racing;

/// <summary>
/// Gemeinsame Daten der Challenge-Spielarten (Rennschule, Meisterschaften, Community Racing Center, Editor-Test) aus der exe.
///
/// HUD-Ansicht: Das Original setzt beim Aufbau des Renn-HUD (CGameMech, exe 0xb40039) die Film-Variablen <c>/:cur_gm</c> und
/// <c>/:whatcha</c> auf den Namen der Spielart. hud.vmf richtet danach Zeit/Referenz/Restzeit aus (/HUD/time, /HUD/time_cd,
/// /HUD/ref_score "open": "up"/"down") und wählt das Symbol der Platzierung (/HUD/position/cart/ic). Namen aus der Tabelle
/// .rdata 0x14d7460 (25 Einträge zu 20 Byte: Name, gmth, Unterart 0xc0c0 solo / 0xc0c1 mehrere Spieler, Eliminator-Flag):
/// timeAttack, speedtrap(_m), race(_m), eliminator(_m), hotspots(_m), event, speed(_m), kyd, ftl.
///
/// Beschreibung der Spielart (Tooltip der Karte /:GMDescription, exe 0x96fa80): Text-ID je gmth.
/// </summary>
public static class ChallengeModes
{
    /// <summary>Name der HUD-Ansicht (/:cur_gm, /:whatcha) wie die exe-Tabelle 0x14d7460.</summary>
    public static string HudName(RaceMode mode, bool multi = false, bool eliminator = false) => mode switch
    {
        RaceMode.TimeAttack => "timeAttack",
        RaceMode.SpeedTrap => multi ? "speedtrap_m" : "speedtrap",
        RaceMode.Race => eliminator ? (multi ? "eliminator_m" : "eliminator") : (multi ? "race_m" : "race"),
        RaceMode.HotSpots => multi ? "hotspots_m" : "hotspots",
        RaceMode.Event => "event",
        RaceMode.Speed => multi ? "speed_m" : "speed",
        RaceMode.KeepYourDistance => "kyd",
        RaceMode.FollowTheLeader => "ftl",
        _ => "empty",
    };

    /// <summary>exe 0xb40039/0xb40062: /:cur_gm und /:whatcha vor dem Öffnen der HUD-Teile setzen.</summary>
    public static void ApplyHud(VmfPlayer? h, RaceDefinition? race, bool multi = false)
    {
        if (h == null || race == null) return;
        string n = HudName(race.Kind, multi, race.IsEliminator);
        h.SetVariable("/:cur_gm", n);
        h.SetVariable("/:whatcha", n);
    }

    /// <summary>Text-ID der Spielart-Beschreibung (exe 0x96fa80): 0 -> 0xc2, 1 -> 0xc7, 3 -> 0xc5, 10 -> 0x88e.</summary>
    public static ulong DescriptionHc(RaceMode mode) => mode switch
    {
        RaceMode.TimeAttack => 0xc2,
        RaceMode.SpeedTrap => 0xc7,
        RaceMode.Race => 0xc5,
        RaceMode.Speed => 0x88e,
        _ => 0,
    };

    /// <summary>Beschreibung inkl. Eliminator (exe 0x96fb38: Rennen mit Rundkurs und nblp = 0 -> 0x1b43).</summary>
    public static ulong DescriptionHc(RaceDefinition d) => d.IsEliminator ? 0x1b43UL : DescriptionHc(d.Kind);

    /// <summary>Tempo m/s -> Anzeigeeinheit (exe 0x44b8a3; die Engine kennt nur km/h).</summary>
    public static float ToUnit(float metersPerSecond) => metersPerSecond * 3.6f;

    /// <summary>Name der Anzeigeeinheit (exe 0x435d0a).</summary>
    public const string UnitName = "km/h";

    /// <summary>Ganzzahl wie die exe (_ftol nach der Umrechnung).</summary>
    public static string UnitInt(float metersPerSecond) => ((int)ToUnit(metersPerSecond)).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Platz mit Exponent wie exe 0xb3c710: Exponent-Text aus der Textdatenbank (0x2fe 1., 0x2ff 2., 0x300 3., 0x301 sonst).
    /// Beginnt er mit '.', steht der Platz als "%d." und der Exponent leer (" "), sonst "%d" + Exponent (z.B. "st").
    /// </summary>
    public static void SetRank(VmfPlayer? h, int pos, string posVar = "/:Pos", string expVar = "/:Exp")
    {
        if (h == null) return;
        ulong hc = pos == 1 ? 0x2feUL : pos == 2 ? 0x2ffUL : pos == 3 ? 0x300UL : 0x301UL;
        string ex = RaceTexts.Get(hc, ".");
        if (pos < 1) { h.SetVariable(posVar, ""); h.SetVariable(expVar, " "); return; }
        if (ex.StartsWith(".", StringComparison.Ordinal)) { h.SetVariable(posVar, pos.ToString(CultureInfo.InvariantCulture) + "."); h.SetVariable(expVar, " "); }
        else { h.SetVariable(posVar, pos.ToString(CultureInfo.InvariantCulture)); h.SetVariable(expVar, ex); }
    }

    /// <summary>exe 0xb3c6b0: Clip sichtbar machen und "open" spielen bzw. nur unsichtbar schalten.</summary>
    public static void ShowClip(VmfPlayer? h, string path, bool on)
    {
        if (h == null) return;
        h.SetVisible(path, on);
        if (on) h.GotoLabel(path, "open", true);
    }
}
