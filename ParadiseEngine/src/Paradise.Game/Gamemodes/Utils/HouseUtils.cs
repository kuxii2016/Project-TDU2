using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.Savegame;
using Paradise.Core.Vehicles;
using Paradise.Game.Screens;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Gemeinsames zu den Häusern (DBHouse) für Makler (GAME_MODE_ESTATES), Garage (GAME_MODE_GARAGE), Karte (GAME_MODE_MAP) und die
/// Einfahrt (InteriorUtils). Besitz und Stellplätze liegen im Kern (Core.Savegame.TDU2Garage, gespeichert im TDU2-Savegame).
/// </summary>
internal static class HouseUtils
{
    static string Island => InteriorUtils._t?.Streamer?.Index?.IslandName ?? "";
    static CarDatabase? Db => InteriorUtils.Vm?.Database;

    /// <summary>Haus-Spot (EPH_*, nicht das Fotostudio EPHOT_*).</summary>
    internal static bool IsHouseSpot(Spot? s)
    {
        string c = s?.Code ?? "";
        return c.StartsWith("EPH", StringComparison.OrdinalIgnoreCase) && !c.StartsWith("EPHOT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>DBHouse-Zeile eines Haus-Spots (null = kein Haus / unbekannt).</summary>
    internal static HouseInfo? HouseOf(Spot? s) => s != null && IsHouseSpot(s) ? Db?.HouseForSpot(s, Island) : null;

    /// <summary>Gehört der Haus-Spot dem Spieler (Original 0x402261 auf dem Profil)? Unbekannte Häuser (ohne DBHouse-Zeile) gelten als offen.</summary>
    internal static bool Owned(Spot? s)
    {
        if (!IsHouseSpot(s)) return true;
        var h = HouseOf(s);
        return h == null || TDU2Garage.Owns(TDU2Session.Save, h.SpotName);
    }

    /// <summary>Symbol eines Spots auf Karte/Einfahrt: Häuser des Spielers "home", fremde Häuser "forsale" (Spots.cpp, Kategorie 0).</summary>
    internal static string? MapLabel(string? label, Spot? s) => label == "home" && IsHouseSpot(s) && !Owned(s) ? "forsale" : label;

    /// <summary>Text der gesperrten Einfahrt eines fremden Hauses (Sprachdatenbank 2266).</summary>
    internal static string ForSaleText() => InteriorUtils.Lang(2266, "Dieses Haus steht zum Verkauf. Sie können das einzige Immobilienbüro besuchen, in dem dieses Haus verkauft wird.");

    /// <summary>Name eines Hauses (DBHouse._housename_hc).</summary>
    internal static string HouseLabel(HouseInfo? h) => h == null ? "" : InteriorUtils.Lang(h.NameHash, h.SpotName ?? "");

    static readonly Dictionary<string, Spot?> _houseSpots = new(StringComparer.OrdinalIgnoreCase);
    static object? _houseSpotsFor;

    /// <summary>Spot eines Hauses auf der aktuellen Insel (null = andere Insel/unbekannt).</summary>
    internal static Spot? HouseSpot(HouseInfo h)
    {
        var spots = InteriorUtils._t?.Streamer?.Spots; var db = Db;
        if (spots == null || db == null || h?.SpotName == null) return null;
        if (!ReferenceEquals(_houseSpotsFor, spots)) { _houseSpots.Clear(); _houseSpotsFor = spots; }
        if (_houseSpots.TryGetValue(h.SpotName, out var hit)) return hit;
        string island = Island;
        Spot? found = null;
        foreach (var s in spots.Spots)
            if (s.Code != null && h.SpotName.StartsWith(s.Code, StringComparison.OrdinalIgnoreCase) && db.HouseForSpot(s, island) == h) { found = s; break; }
        _houseSpots[h.SpotName] = found;
        return found;
    }

    /// <summary>Gebiet eines Hauses (Flash-Label h1..h4 / i1..i2; Original /:WhatZone, /:ExcArea, content/area): Gebiet am Spot des Hauses,
    /// auf der anderen Insel nur die Insel ("h" bzw. "i").</summary>
    internal static string HouseArea(HouseInfo? h)
    {
        if (h == null) return "";
        var s = HouseSpot(h);
        string? a = s != null ? ((GAME_MODE_PAUSE)GameModes.Get(Gamemode.GAME_MODE_PAUSE)).Menu?.AreaAt(s.Position.X, s.Position.Z) : null;
        return a ?? (h.SpotName.EndsWith("I", StringComparison.OrdinalIgnoreCase) ? "i" : "h");
    }

    static readonly Dictionary<int, int> _furniture = new();

    /// <summary>
    /// Möbel eines Hauses dieser Stufe (Makler /:MyHouseFurniture, exe 0xAEF783): Zeilen von DBFurniture mit _isvalid und _level &lt;= Stufe.
    /// </summary>
    internal static int FurnitureCount(int level)
    {
        if (_furniture.TryGetValue(level, out int n)) return n;
        var t = Db?.Root?.Get("DBFurniture");
        if (t == null) return 0;
        n = 0;
        foreach (var r in t.Items) if (r.B("_isvalid", true) && r.I("_level") <= level) n++;
        _furniture[level] = n;
        return n;
    }

    static readonly Dictionary<string, Texture2D?> _pics = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Bild eines Hauses aus FrontEnd/AllRes/RealtorScr/&lt;spotname&gt;.bnk: "IMG_EXT" außen, "IMG_GAR" Garage, "IMG_LVG" Wohnraum
    /// (Makler 0xAEF8xx, Platz wechseln 0xAC3620); null ohne Bank/Bild.
    /// </summary>
    internal static Texture2D? Picture(HouseInfo? h, string kind)
    {
        if (h?.SpotName == null) return null;
        string key = h.SpotName + "|" + kind;
        if (_pics.TryGetValue(key, out var t)) return t;
        try
        {
            string? f = string.IsNullOrEmpty(FlashHost.BnkDir) ? null
                : CarDatabase.FindFile(Path.Combine(FlashHost.BnkDir, "frontend", "AllRes", "RealtorScr"), h.SpotName.ToLowerInvariant() + ".bnk");
            if (f != null)
            {
                t = FlashTextures.TextureNamed(f, kind);
                if (t == null)
                {
                    var names = new List<string>();
                    foreach (var e in BnkArchive.Open(f).All(".2db")) names.Add(e.Name);
                    Log.Warn("Häuser: " + kind + " nicht in " + Path.GetFileName(f) + " (Texturen: " + string.Join(", ", names) + ")");
                }
            }
            else Log.Warn("Häuser: kein Bild RealtorScr/" + h.SpotName.ToLowerInvariant() + ".bnk");
        }
        catch (Exception ex) { Log.Warn("Häuser: Bild " + h.SpotName + " " + kind + ": " + ex.Message); }
        _pics[key] = t;
        return t;
    }

    /// <summary>
    /// Lage eines Hauses auf einer Minikarte des Films: &lt;loc&gt;/circle relativ zu &lt;loc&gt;/lure (Garage 0xAC3550; Makler
    /// realtor_menus/buy_menu/location, sold_menu/datas/location).
    /// </summary>
    internal static void PlaceOnMiniMap(VmfPlayer ui, string loc, HouseInfo h)
    {
        var s = HouseSpot(h);
        var lure = ui.Find(loc + "/lure");
        if (s == null || lure == null) return;
        var r = ui.LocalBounds(lure);
        if (!r.HasValue) return;
        bool hawaii = Island.StartsWith("h", StringComparison.OrdinalIgnoreCase);
        float ax = (hawaii ? 11 : 7) * 6144f, az = (hawaii ? 9 : 6) * 6144f;
        ui.SetPosition(loc + "/circle", new Vector2(r.Value.width * s.Position.X / ax, r.Value.height * s.Position.Z / az));
    }

    /// <summary>Geldbetrag wie die Läden ("$ 1.234.567").</summary>
    internal static string Money(long v) => "$ " + v.ToString("N0", Paradise.Core.Util.GermanFormat.Number);
}
