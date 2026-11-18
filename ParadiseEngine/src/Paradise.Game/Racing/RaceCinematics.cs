// Portiert aus dem Unity-Projekt (Runtime/Racing/RaceCinematics.cs).
using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.Racing;
using Paradise.Game.Config;
using Paradise.Game.Online;

namespace Paradise.Game.Racing;

/// <summary>
/// Original-Kinos rund um Rennen (scenarics/cinematics, scenarics/packs):
///  - Meisterschaft: Vorstellung beim ersten Betreten des Treffpunkts (asphalt/sta_a&lt;n&gt;, offroad/stb_b&lt;n&gt;,
///    classic/stc_c&lt;n&gt;, Elite "_high", Insel-Cups cups/cup_&lt;insel&gt;&lt;n&gt;), davor einmal je Schulart "*_firstintro".
///  - Meisterschaft abgeschlossen: "*_winloosepodium" (WIN/LOOSE), Insel-Cups cups/cup_win.
///  - Rennschule: Instruktor nach der Prüfung (pack_racingschool: C_&lt;ASPHALT|DIRT|CLASSIC&gt;_&lt;WIN_0n|LOOSE_0n&gt;).
/// Kino-Namen und Totem (Spot-Code für die Platzierung) werden aus den .cin-Dateinamen der Bank gelesen
/// ("&lt;bank&gt;_M_&lt;NAME&gt;_totem_&lt;SPOT&gt;.cin").
/// Engine: Auswahl wie Unity; das Abspielen braucht den Kino-Ablauf mit Platzierung am Totem (Unity TDU2Cutscenes) – der ist
/// noch nicht portiert, <see cref="Play"/> meldet das und macht direkt weiter (CHECKLIST).
/// </summary>
public static class RaceCinematics
{
    public sealed class Cin
    {
        public string Bank = "";     // relativ zu Bnk, ohne .bnk ("scenarics/cinematics/asphalt/sta_a1")
        public string Name = "";     // "C_A1_COMME_UNE_PENDULE"
        public string? Totem;        // Spot-Code oder null
        public string Id => Bank + ":" + Name;
    }

    static readonly Dictionary<string, List<Cin>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Alle Kinos einer Bank (aus den .cin-Namen), leer wenn die Bank fehlt.</summary>
    public static List<Cin> List(string? bnkDir, string? rel)
    {
        if (string.IsNullOrEmpty(bnkDir) || string.IsNullOrEmpty(rel)) return new List<Cin>();
        lock (_cache)
        {
            if (_cache.TryGetValue(rel, out var c)) return c;
            var list = new List<Cin>();
            try
            {
                string path = Path.Combine(bnkDir, rel.Replace('/', Path.DirectorySeparatorChar) + ".bnk");
                if (GameFiles.Exists(path))
                {
                    var bank = BnkArchive.Open(path);
                    foreach (var e in bank.Entries)
                    {
                        if (e.Extension != ".cin" || e.Name == null) continue;
                        string n = Path.GetFileNameWithoutExtension(e.Name);
                        int m = n.IndexOf("_M_", StringComparison.Ordinal);
                        if (m < 0) continue;
                        string rest = n[(m + 3)..];
                        string? totem = null;
                        int t = rest.IndexOf("_totem_", StringComparison.OrdinalIgnoreCase);
                        if (t >= 0) { totem = rest[(t + 7)..]; rest = rest[..t]; }
                        if (rest.Length == 0) continue;
                        list.Add(new Cin { Bank = rel, Name = "C_" + rest, Totem = totem });
                    }
                }
            }
            catch (Exception ex) { Log.Warn("RaceCinematics " + rel + ": " + ex.Message); }
            _cache[rel] = list;
            return list;
        }
    }

    static Cin? Pick(List<Cin> list, Func<string, bool> match)
    {
        var hits = new List<Cin>();
        foreach (var c in list) if (match(c.Name.ToUpperInvariant())) hits.Add(c);
        return hits.Count == 0 ? null : hits[Random.Shared.Next(hits.Count)];
    }

    static string SchoolLetter(LicenseInfo lic)
    {
        string g = RaceDatabase.CategoryName(lic.VehicleCategory1);
        return g.Length >= 2 && (g[0] == 'A' || g[0] == 'B' || g[0] == 'C') ? g[..1] : "";
    }

    static string? Folder(string letter) => letter == "A" ? "asphalt/sta_" : letter == "B" ? "offroad/stb_" : letter == "C" ? "classic/stc_" : null;

    /// <summary>Vorstellungs-Kino einer Meisterschaft (null = keins).</summary>
    public static Cin? ChampionshipIntro(string? bnkDir, LicenseInfo? lic, int island)
    {
        if (lic == null) return null;
        string letter = SchoolLetter(lic);
        var banks = new List<string>();
        if (letter.Length == 0)
        {
            // Insel-Cups: cup_ibiza1 / cup_hawai1 / cup_hawai2 nach Reihenfolge
            int n = System.Math.Max(1, lic.Order);
            banks.Add("scenarics/cinematics/cups/cup_" + (island == 0 ? "ibiza" : "hawai") + n);
            banks.Add("scenarics/cinematics/cups/cup_" + (island == 0 ? "ibiza" : "hawai") + "1");
        }
        else
        {
            string f = Folder(letter)!;
            string g1 = RaceDatabase.CategoryName(lic.VehicleCategory1).ToLowerInvariant();
            string? g2 = lic.VehicleCategory2 > 0 ? RaceDatabase.CategoryName(lic.VehicleCategory2).ToLowerInvariant() : null;
            bool elite = lic.Order >= 2;
            if (elite) banks.Add("scenarics/cinematics/" + f + g1 + "_high");
            banks.Add("scenarics/cinematics/" + f + g1);
            if (g2 != null) { banks.Add("scenarics/cinematics/" + f + g1 + "_" + g2); banks.Add("scenarics/cinematics/" + f + g2 + "_" + g1); }
            // A2 steckt in sta_a7_a2
            banks.Add("scenarics/cinematics/" + f + "a7_" + g1);
        }
        foreach (var b in banks)
        {
            var c = Pick(List(bnkDir, b), n => !n.Contains("LOOP"));
            if (c != null) return c;
        }
        return null;
    }

    /// <summary>Einmaliges Einführungskino der Schulart (Asphalt/Offroad/Classic), null = keins.</summary>
    public static Cin? FirstIntro(string? bnkDir, LicenseInfo? lic)
    {
        string? f = lic != null ? Folder(SchoolLetter(lic)) : null;
        return f == null ? null : Pick(List(bnkDir, "scenarics/cinematics/" + f + "firstintro"), n => !n.Contains("LOOP"));
    }

    /// <summary>Kino nach der letzten Wertung einer Meisterschaft (Sieg/Niederlage), null = keins.</summary>
    public static Cin? ChampionshipEnd(string? bnkDir, LicenseInfo? lic, bool won)
    {
        string? f = lic != null ? Folder(SchoolLetter(lic)) : null;
        string bank = f != null ? "scenarics/cinematics/" + f + "winloosepodium" : "scenarics/cinematics/cups/cup_win";
        var list = List(bnkDir, bank);
        var c = Pick(list, n => !n.Contains("LOOP") && n.Contains(won ? "WIN" : "LOOSE"));
        if (c == null && f == null && won) c = Pick(list, n => !n.Contains("LOOP"));
        return c;
    }

    /// <summary>Instruktor-Kino nach einer Rennschul-Prüfung (pack_racingschool), null = keins.</summary>
    public static Cin? RacingSchoolResult(string? bnkDir, char school, bool passed)
    {
        string kind = school == 'B' ? "DIRT" : school == 'C' ? "CLASSIC" : "ASPHALT";
        string want = "C_" + kind + "_" + (passed ? "WIN_" : "LOOSE_");
        return Pick(List(bnkDir, "scenarics/packs/pack_racingschool"), n => n.StartsWith(want, StringComparison.Ordinal) && !n.Contains("LOOP"));
    }

    /// <summary>
    /// Kino spielen; done wird immer genau einmal gerufen (auch ohne Kino/bei Fehler). true = ein Kino läuft.
    /// Noch ohne Kino-Ablauf (Unity TDU2Cutscenes: Spielerauto/Avatar laden, am Totem platzieren, überspringbar).
    /// </summary>
    public static bool Play(Cin? cin, Action? done)
    {
        if (!RaceConfig.Cinematics || cin == null) { done?.Invoke(); return false; }
        Log.Info("Renn-Kino " + cin.Id + (cin.Totem != null ? " (Totem " + cin.Totem + ")" : "") + " – Kino-Ablauf (TDU2Cutscenes) noch nicht portiert, weiter ohne Kino");
        done?.Invoke();
        return false;
    }

    // ------------------------------------------------------------------ "schon gesehen" (je Spieler, im Spielstand: SaveData.raceCinSeen,
    // Original-DATA als Umgebungsvariable U3D_RaceCin)
    public static bool Seen(string what) => TDU2Session.Save?.raceCinSeen?.Exists(x => string.Equals(x, what, StringComparison.Ordinal)) ?? false;
    public static void MarkSeen(string what)
    {
        var s = TDU2Session.Save;
        if (s == null || string.IsNullOrEmpty(what) || Seen(what)) return;
        (s.raceCinSeen ??= new List<string>()).Add(what);
        SessionAutosave.Schedule(2f);
    }
}
