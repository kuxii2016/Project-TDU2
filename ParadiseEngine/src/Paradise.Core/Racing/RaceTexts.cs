// Portiert aus dem Unity-Projekt (Runtime/Racing/RaceTexts.cs).
using Paradise.Core.Flash;

namespace Paradise.Core.Racing;

/// <summary>
/// Texte der Spieldatenbank nach HashCode (database/db_&lt;sprache&gt;.cpr), z.B. Prüfungsnamen (ig2 "dbna"),
/// Beschreibungen ("dbd1"), Lizenzbeschreibung (DBLicense._championshipdesc_hc) und Ergebnismeldungen (1625..1628).
/// Engine: dieselbe Tabelle wie die Flash-Texte (<see cref="FlashLocalization.Text"/>, Sprache <see cref="FlashHost.Language"/>) –
/// Unity hatte dafür eine eigene Kopie.
/// </summary>
public static class RaceTexts
{
    public const ulong MsgTestFailed = 1625, MsgTestPassed = 1626, MsgLicenceWon = 1627, MsgLicenceWonLong = 1628;
    public const ulong MsgStopVehicle = 998, MsgStopVehicleAgain = 1000;

    public static string Get(ulong hc, string fallback = "")
    {
        if (hc == 0 || hc > int.MaxValue) return fallback;
        return FlashLocalization.Text(FlashHost.BnkDir, FlashHost.Language, (int)hc) ?? fallback;
    }
}
