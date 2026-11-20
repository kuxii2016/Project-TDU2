using Paradise.Core.Racing;
using System.Globalization;

namespace Paradise.Game.Gamemodes;

/// <summary>Gemeinsames des Race-Editors und der Spielhalle (Community Racing Center): Geldtext und Wertungsrichtung.</summary>
internal static class CrcText
{
    /// <summary>Geldbetrag wie exe 0xc08ad0: Tausender mit Leerzeichen (US: Komma), Text 0x3a4 "§1§ $".</summary>
    public static string Money(long v)
    {
        string n = System.Math.Abs(v).ToString("N0", CultureInfo.InvariantCulture).Replace(",", " ");
        if (v < 0) n = "-" + n;
        return RaceTexts.Get(0x3a4, "§1§ $").Replace("§1§", n);
    }

    /// <summary>Tempo (gmth 10) und Radar (1): größere Wertung ist besser; sonst kleinere (Zeit). Gleich wie der Server (ChallengeStore).</summary>
    public static bool HigherIsBetter(int mode) => mode == 1 || mode == 10;
}
