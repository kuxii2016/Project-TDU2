#nullable disable
using System.Globalization;

namespace Paradise.Core.Online
{
    public enum Country : byte
    {
        undefined = 0,
        UNITED_ARAB_EMIRATES,
        ALBANIA,
        ARMENIA,
        ARGENTINA,
        AUSTRIA,
        AUSTRALIA,
        AZERBAIJAN,
        BELGIUM,
        BULGARIA,
        BAHRAIN,
        BRUNEI_DARUSSALAM,
        BOLIVIA,
        BRAZIL,
        BELARUS,
        BELIZE,
        CANADA,
        Island,
        SWITZERLAND,
        CHILE,
        CHINA,
        COLOMBIA,
        COSTA_RICA,
        CZECH_REPUBLIC,
        GERMANY = 24,
        DENMARK,
        DOMINICAN_REPUBLIC,
        ALGERIA,
        ECUADOR,
        ESTONIA,
        EGYPT,
        SPAIN,
        FINLAND,
        FAROE_ISLANDS,
        FRANCE,
        GREAT_BRITAIN,
        GEORGIA,
        GREECE,
        GUATEMALA,
        HONG_KONG,
        HONDURAS,
        CROATIA,
        HUNGARY,
        INDONESIA,
        IRELAND,
        ISRAEL,
        INDIA,
        IRAQ,
        IRAN,
        ICELAND,
        ITALY,
        JAMAICA,
        JORDAN,
        JAPAN,
        KENYA,
        KYRGYZSTAN,
        KOREA,
        KUWAIT,
        KAZAKHSTAN,
        LEBANON,
        LIECHTENSTEIN,
        LITHUANIA,
        LUXEMBOURG,
        LATVIA,
        LIBYA,
        MOROCCO,
        MONACO,
        MACEDONIA,
        MONGOLIA,
        MACAU,
        MALDIVES,
        MEXICO,
        MALAYSIA,
        NICARAGUA,
        NETHERLANDS,
        NORWAY,
        NEW_ZEALAND,
        OMAN,
        PANAMA,
        PERU,
        PHILIPPINES,
        PAKISTAN,
        POLAND,
        PUERTO_RICO,
        PORTUGAL,
        PARAGUAY,
        QATAR,
        ROMANIA,
        RUSSIAN_FEDERATION = 88,
        SAUDI_ARABIA,
        SWEDEN,
        SINGAPORE,
        SLOVENIA,
        SLOVAK_REPUBLIC,
        BANGLADESH,
        EL_SALVADOR,
        SYRIA,
        THAILAND,
        TUNISIA,
        TURKEY,
        TRINIDAD_AND_TOBAGO,
        TAIWAN,
        UKRAINE,
        UNITED_STATES,
        URUGUAY,
        UZBEKISTAN,
        VENEZUELA,
        VIET_NAM,
        YEMEN,
        SOUTH_AFRICA,
        ZIMBABWE,
        SRI_LANKA,
        CYPRUS,
        MALTA
    }

    public static class TDU2CountryHelper
    {
        /// <summary>
        /// Ermittelt die Länder-ID anhand der Windows-Region.
        /// </summary>
        public static byte GetSystemCountryId()
        {
            // Mit InvariantGlobalization (Directory.Build.props) liefert RegionInfo.CurrentRegion immer "IV" (Invariant) ->
            // Land 0, Mitspieler sahen keine Flagge. Daher zuerst das Gebietsschema des Windows-Benutzers (z.B. "de-DE").
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var buf = new char[85];
                    int n = GetUserDefaultLocaleName(buf, buf.Length);
                    if (n > 1)
                    {
                        var c = GetCountryFromCode(new string(buf, 0, n - 1));
                        if (c != Country.undefined) return (byte)c;
                    }
                    int geo = GetUserGeoID(16);   // GEOCLASS_NATION
                    if (geo > 0)
                    {
                        var iso = new char[16];
                        int m = GetGeoInfoW(geo, 4, iso, iso.Length, 0);   // GEO_ISO2
                        if (m > 1)
                        {
                            var c = GetCountryFromCode(new string(iso, 0, m - 1));
                            if (c != Country.undefined) return (byte)c;
                        }
                    }
                }
            }
            catch { }
            try
            {
                string code =
                    RegionInfo.CurrentRegion.TwoLetterISORegionName;

                return (byte)GetCountryFromCode(code);
            }
            catch
            {
                return 0;
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int GetUserDefaultLocaleName([System.Runtime.InteropServices.Out] char[] lpLocaleName, int cchLocaleName);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern int GetUserGeoID(int geoClass);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int GetGeoInfoW(int location, int geoType, [System.Runtime.InteropServices.Out] char[] lpGeoData, int cchData, int langId);

        /// <summary>
        /// Ermittelt das Land anhand eines ISO-Ländercodes,
        /// beispielsweise DE, de-DE oder de_DE.
        /// </summary>
        public static Country GetCountryFromCode(string countryCode)
        {
            if (string.IsNullOrWhiteSpace(countryCode))
                return Country.undefined;

            countryCode = countryCode.Trim().Replace('_', '-');

            // Bei de-DE den Länderteil DE verwenden.
            string[] parts = countryCode.Split('-');

            if (parts.Length > 1)
                countryCode = parts[parts.Length - 1];

            countryCode = countryCode.ToUpperInvariant();

            switch (countryCode)
            {
                case "AE": return Country.UNITED_ARAB_EMIRATES;
                case "AL": return Country.ALBANIA;
                case "AM": return Country.ARMENIA;
                case "AR": return Country.ARGENTINA;
                case "AT": return Country.AUSTRIA;
                case "AU": return Country.AUSTRALIA;
                case "AZ": return Country.AZERBAIJAN;
                case "BE": return Country.BELGIUM;
                case "BG": return Country.BULGARIA;
                case "BH": return Country.BAHRAIN;
                case "BN": return Country.BRUNEI_DARUSSALAM;
                case "BO": return Country.BOLIVIA;
                case "BR": return Country.BRAZIL;
                case "BY": return Country.BELARUS;
                case "BZ": return Country.BELIZE;
                case "CA": return Country.CANADA;
                case "CH": return Country.SWITZERLAND;
                case "CL": return Country.CHILE;
                case "CN": return Country.CHINA;
                case "CO": return Country.COLOMBIA;
                case "CR": return Country.COSTA_RICA;
                case "CZ": return Country.CZECH_REPUBLIC;
                case "DE": return Country.GERMANY;
                case "DK": return Country.DENMARK;
                case "DO": return Country.DOMINICAN_REPUBLIC;
                case "DZ": return Country.ALGERIA;
                case "EC": return Country.ECUADOR;
                case "EE": return Country.ESTONIA;
                case "EG": return Country.EGYPT;
                case "ES": return Country.SPAIN;
                case "FI": return Country.FINLAND;
                case "FO": return Country.FAROE_ISLANDS;
                case "FR": return Country.FRANCE;
                case "GB": return Country.GREAT_BRITAIN;
                case "GE": return Country.GEORGIA;
                case "GR": return Country.GREECE;
                case "GT": return Country.GUATEMALA;
                case "HK": return Country.HONG_KONG;
                case "HN": return Country.HONDURAS;
                case "HR": return Country.CROATIA;
                case "HU": return Country.HUNGARY;
                case "ID": return Country.INDONESIA;
                case "IE": return Country.IRELAND;
                case "IL": return Country.ISRAEL;
                case "IN": return Country.INDIA;
                case "IQ": return Country.IRAQ;
                case "IR": return Country.IRAN;
                case "IS": return Country.ICELAND;
                case "IT": return Country.ITALY;
                case "JM": return Country.JAMAICA;
                case "JO": return Country.JORDAN;
                case "JP": return Country.JAPAN;
                case "KE": return Country.KENYA;
                case "KG": return Country.KYRGYZSTAN;
                case "KR": return Country.KOREA;
                case "KW": return Country.KUWAIT;
                case "KZ": return Country.KAZAKHSTAN;
                case "LB": return Country.LEBANON;
                case "LI": return Country.LIECHTENSTEIN;
                case "LT": return Country.LITHUANIA;
                case "LU": return Country.LUXEMBOURG;
                case "LV": return Country.LATVIA;
                case "LY": return Country.LIBYA;
                case "MA": return Country.MOROCCO;
                case "MC": return Country.MONACO;
                case "MK": return Country.MACEDONIA;
                case "MN": return Country.MONGOLIA;
                case "MO": return Country.MACAU;
                case "MV": return Country.MALDIVES;
                case "MX": return Country.MEXICO;
                case "MY": return Country.MALAYSIA;
                case "NI": return Country.NICARAGUA;
                case "NL": return Country.NETHERLANDS;
                case "NO": return Country.NORWAY;
                case "NZ": return Country.NEW_ZEALAND;
                case "OM": return Country.OMAN;
                case "PA": return Country.PANAMA;
                case "PE": return Country.PERU;
                case "PH": return Country.PHILIPPINES;
                case "PK": return Country.PAKISTAN;
                case "PL": return Country.POLAND;
                case "PR": return Country.PUERTO_RICO;
                case "PT": return Country.PORTUGAL;
                case "PY": return Country.PARAGUAY;
                case "QA": return Country.QATAR;
                case "RO": return Country.ROMANIA;
                case "RU": return Country.RUSSIAN_FEDERATION;
                case "SA": return Country.SAUDI_ARABIA;
                case "SE": return Country.SWEDEN;
                case "SG": return Country.SINGAPORE;
                case "SI": return Country.SLOVENIA;
                case "SK": return Country.SLOVAK_REPUBLIC;
                case "BD": return Country.BANGLADESH;
                case "SV": return Country.EL_SALVADOR;
                case "SY": return Country.SYRIA;
                case "TH": return Country.THAILAND;
                case "TN": return Country.TUNISIA;
                case "TR": return Country.TURKEY;
                case "TT": return Country.TRINIDAD_AND_TOBAGO;
                case "TW": return Country.TAIWAN;
                case "UA": return Country.UKRAINE;
                case "US": return Country.UNITED_STATES;
                case "UY": return Country.URUGUAY;
                case "UZ": return Country.UZBEKISTAN;
                case "VE": return Country.VENEZUELA;
                case "VN": return Country.VIET_NAM;
                case "YE": return Country.YEMEN;
                case "ZA": return Country.SOUTH_AFRICA;
                case "ZW": return Country.ZIMBABWE;
                case "LK": return Country.SRI_LANKA;
                case "CY": return Country.CYPRUS;
                case "MT": return Country.MALTA;

                default: return Country.undefined;
            }
        }
    }
}
