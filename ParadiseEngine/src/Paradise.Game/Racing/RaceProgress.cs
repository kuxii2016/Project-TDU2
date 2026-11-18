// Portiert aus dem Unity-Projekt (Runtime/Racing/RaceProgress.cs).
using Paradise.Core.Racing;
using Paradise.Game.Online;
using System.Globalization;

namespace Paradise.Game.Racing;

/// <summary>
/// Fortschritt der Rennschule und der Meisterschaften (Unity PlayerPrefs): bestandene Prüfungen, Bestzeiten, Versuche,
/// erworbene Lizenzen mit Datum, Punkte je Meisterschaftsrennen. Engine: alle Schlüssel wie in Unity ("TDU2.School.&lt;Prüfung&gt;",
/// "TDU2.School.Best.&lt;Prüfung&gt;" (ms), "TDU2.School.Lic.&lt;Hash&gt;" (Datum yyyyMMdd), "TDU2.School.Tries.&lt;Prüfung&gt;", "TDU2.Cup.…")
/// im Spielstand des Profils (SaveData.racing, Original-DATA als Umgebungsvariable U3D_Racing – TDU2SaveGame).
/// </summary>
public static class RaceProgress
{
    static List<Paradise.Core.Online.HouseEntry>? Values => Paradise.Core.Online.TDU2Session.Save?.racing;
    static void Save() => SessionAutosave.Schedule(2f);

    static bool TryGet(string k, out string v)
    {
        v = "";
        var l = Values;
        if (l == null) return false;
        foreach (var e in l) if (e != null && string.Equals(e.k, k, StringComparison.Ordinal)) { v = e.v ?? ""; return true; }
        return false;
    }

    static void Put(string k, string v)
    {
        var l = Values;
        if (l == null) return;
        foreach (var e in l) if (e != null && string.Equals(e.k, k, StringComparison.Ordinal)) { e.v = v; return; }
        l.Add(new Paradise.Core.Online.HouseEntry { k = k, v = v });
    }

    static string DoneKey(string test) => "TDU2.School." + (test ?? "").ToUpperInvariant();
    static string BestKey(string test) => "TDU2.School.Best." + (test ?? "").ToUpperInvariant();
    static string TriesKey(string test) => "TDU2.School.Tries." + (test ?? "").ToUpperInvariant();
    static string LicKey(ulong hash) => "TDU2.School.Lic." + hash.ToString(CultureInfo.InvariantCulture);

    static int GetInt(string k, int d) => TryGet(k, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : d;
    static string GetStr(string k, string d) => TryGet(k, out var v) ? v : d;
    static void SetInt(string k, int v) { Put(k, v.ToString(CultureInfo.InvariantCulture)); Save(); }
    static void SetStr(string k, string v) { Put(k, v); Save(); }
    static void Delete(string k) => Values?.RemoveAll(e => e != null && string.Equals(e.k, k, StringComparison.Ordinal));

    public static bool IsTestDone(string? test) => !string.IsNullOrEmpty(test) && GetInt(DoneKey(test), 0) != 0;
    public static void MarkTestDone(string? test) { if (!string.IsNullOrEmpty(test)) SetInt(DoneKey(test), 1); }

    public static int BestTimeMs(string? test) => GetInt(BestKey(test ?? ""), 0);
    public static void ReportTime(string? test, int ms)
    {
        if (string.IsNullOrEmpty(test) || ms <= 0) return;
        int b = BestTimeMs(test);
        if (b <= 0 || ms < b) SetInt(BestKey(test), ms);
    }

    public static int Tries(string? test) => GetInt(TriesKey(test ?? ""), 0);
    public static void AddTry(string? test) { if (!string.IsNullOrEmpty(test)) SetInt(TriesKey(test), Tries(test) + 1); }

    public static bool IsLicenceWon(ulong hash) => !string.IsNullOrEmpty(GetStr(LicKey(hash), ""));
    public static void MarkLicenceWon(ulong hash) { if (!IsLicenceWon(hash)) SetStr(LicKey(hash), DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)); }
    public static DateTime? LicenceDate(ulong hash)
    {
        var s = GetStr(LicKey(hash), "");
        return DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    // ---- Meisterschaften: je Rennen die Punkte aller Teilnehmer ("Spieler,Bot1..Bot7"), Platz des Spielers
    static string CupRaceKey(ulong cup, string race) => "TDU2.Cup." + cup.ToString(CultureInfo.InvariantCulture) + "." + (race ?? "").ToUpperInvariant();
    static string CupDoneKey(ulong cup) => "TDU2.Cup." + cup.ToString(CultureInfo.InvariantCulture) + ".Done";
    static string CupPaidKey(ulong cup, string race) => CupRaceKey(cup, race) + ".Paid";

    /// <summary>Schon ausgezahltes Preisgeld eines Meisterschaftsrennens (bei Wiederholung nur die Verbesserung zahlen).</summary>
    public static long CupPaid(ulong cup, string race) => long.TryParse(GetStr(CupPaidKey(cup, race), "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    public static void SetCupPaid(ulong cup, string race, long v) => SetStr(CupPaidKey(cup, race), v.ToString(CultureInfo.InvariantCulture));

    public static bool IsCupRaceDone(ulong cup, string race) => !string.IsNullOrEmpty(GetStr(CupRaceKey(cup, race), ""));

    /// <summary>Punkte eines Rennens: [0] = Spieler, [1..] = Gegner in Startreihenfolge; null = noch nicht gefahren.</summary>
    public static int[]? CupRacePoints(ulong cup, string race)
    {
        var s = GetStr(CupRaceKey(cup, race), "");
        if (string.IsNullOrEmpty(s)) return null;
        var parts = s.Split(',');
        var r = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++) int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out r[i]);
        return r;
    }

    public static void SetCupRacePoints(ulong cup, string race, int[]? points)
    {
        if (points == null || string.IsNullOrEmpty(race)) return;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < points.Length; i++) { if (i > 0) sb.Append(','); sb.Append(points[i].ToString(CultureInfo.InvariantCulture)); }
        SetStr(CupRaceKey(cup, race), sb.ToString());
    }

    /// <summary>Meisterschaft abgeschlossen (alle Rennen gefahren) – Datum yyyyMMdd und Gesamtplatz "datum|platz".</summary>
    public static bool IsCupDone(ulong cup) => !string.IsNullOrEmpty(GetStr(CupDoneKey(cup), ""));
    public static int CupFinalPlace(ulong cup)
    {
        var s = GetStr(CupDoneKey(cup), "");
        int bar = s.IndexOf('|');
        return bar >= 0 && int.TryParse(s[(bar + 1)..], out int p) ? p : 0;
    }
    public static void MarkCupDone(ulong cup, int place) =>
        SetStr(CupDoneKey(cup), DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "|" + place.ToString(CultureInfo.InvariantCulture));

    public static void ResetCup(LicenseInfo? cup)
    {
        if (cup == null) return;
        Delete(CupDoneKey(cup.Hash));
        foreach (var t in cup.ChallengeFiles) Delete(CupRaceKey(cup.Hash, t));
        Save();
    }

    /// <summary>Alles zurücksetzen (Debug).</summary>
    public static void ResetAll(IEnumerable<LicenseInfo>? licences)
    {
        if (licences == null) return;
        foreach (var l in licences)
        {
            Delete(LicKey(l.Hash));
            foreach (var t in l.ChallengeFiles) { Delete(DoneKey(t)); Delete(BestKey(t)); Delete(TriesKey(t)); }
        }
        Save();
    }
}
