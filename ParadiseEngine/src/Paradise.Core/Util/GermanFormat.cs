using System.Globalization;

namespace Paradise.Core.Util;

/// <summary>
/// Deutsche Zahlenformatierung, die auch im InvariantGlobalization-Modus funktioniert.
/// Die veröffentlichte Single-File-exe setzt <c>InvariantGlobalization=true</c>; dort wirft
/// <c>CultureInfo.GetCultureInfo("de-DE")</c> eine <see cref="CultureNotFoundException"/>.
/// Dieses Format bildet die relevanten de-DE-Einstellungen nach (Tausenderpunkt, Dezimalkomma).
/// </summary>
public static class GermanFormat
{
    /// <summary>NumberFormatInfo mit de-DE-Trennzeichen (Tausenderpunkt, Dezimalkomma).</summary>
    public static readonly NumberFormatInfo Number = Create();

    private static NumberFormatInfo Create()
    {
        var n = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        n.NumberDecimalSeparator = ",";
        n.NumberGroupSeparator = ".";
        n.NumberGroupSizes = new[] { 3 };
        n.CurrencyDecimalSeparator = ",";
        n.CurrencyGroupSeparator = ".";
        n.CurrencyGroupSizes = new[] { 3 };
        n.PercentDecimalSeparator = ",";
        n.PercentGroupSeparator = ".";
        n.PercentGroupSizes = new[] { 3 };
        n.NegativeSign = "-";
        return n;
    }
}
