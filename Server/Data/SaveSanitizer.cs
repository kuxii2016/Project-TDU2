using System.Text.RegularExpressions;

namespace TDU2Server.Data;

/// <summary>Spielstand vom Client prüfen: Listen begrenzen, ungültige Einträge verwerfen, Längen kürzen.</summary>
public static class SaveSanitizer
{
    /// <summary>Bereinigt <paramref name="sv"/> (in place) gegen den bisherigen Stand <paramref name="old"/>.</summary>
    public static SaveData Clean(SaveData sv, SaveData old)
    {
        if (sv.money < 0) sv.money = 0;
        sv.ownedCars = (sv.ownedCars ?? new List<string>()).Take(500).ToList();
        sv.odometers ??= new List<OdoEntry>();
        sv.liveries = (sv.liveries ?? new List<LiveryEntry>()).Where(l => l != null && !string.IsNullOrEmpty(l.car) && (l.data ?? "").Length <= 8192).Take(500).ToList();
        sv.stickerBoards = (sv.stickerBoards ?? new List<int>()).Take(200).ToList();
        sv.favoriteColors = (sv.favoriteColors ?? new List<int>()).Take(16).ToList();
        sv.ownedClothes = (sv.ownedClothes ?? new List<int>()).Take(2000).ToList();
        sv.avatar = Cut(sv.avatar, 2048);
        // CRC: nur positive, eindeutige Challenge-Ids (vom Editor hochgeladen)
        sv.uploadedChallenges = (sv.uploadedChallenges ?? new List<int>()).Where(id => id > 0).Distinct().Take(64).ToList();

        // große Karte
        sv.bookmarks = (sv.bookmarks ?? new List<BookmarkEntry>())
            .Where(b => b != null && float.IsFinite(b.x) && float.IsFinite(b.y) && float.IsFinite(b.z)).Take(200).ToList();
        foreach (var b in sv.bookmarks) b.island = Cut(b.island, 32);
        sv.favoriteSpots = (sv.favoriteSpots ?? new List<string>()).Where(c => !string.IsNullOrEmpty(c) && c.Length <= 64).Distinct().Take(500).ToList();

        // Cruising / Road Discovery: nur stabile, kurze IDs und bekannte Inseln zulassen
        if (sv.roadDiscoveryVersion < 0) sv.roadDiscoveryVersion = 0;
        sv.exploredRoads = (sv.exploredRoads ?? new List<RoadDiscoveryEntry>())
            .Where(e => e != null && !string.IsNullOrEmpty(e.island) && e.island.Length <= 32).Take(8).ToList();
        foreach (var e in sv.exploredRoads)
        {
            e.ids = (e.ids ?? new List<string>())
                .Where(id => !string.IsNullOrEmpty(id) && id.Length <= 96 && id.StartsWith("CRUISING_", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(5000).ToList();
            e.areas = Entries(e.areas, 32, 64);
        }
        sv.unlockedDealers = (sv.unlockedDealers ?? new List<string>())
            .Where(c => !string.IsNullOrEmpty(c) && c.Length <= 64).Distinct(StringComparer.OrdinalIgnoreCase).Take(500).ToList();
        if (sv.cruisingRoadPoints < 0) sv.cruisingRoadPoints = 0;
        if (sv.cruisingRoadPoints > 10_000_000) sv.cruisingRoadPoints = 10_000_000;
        sv.cruisingRoadEvents = Entries(sv.cruisingRoadEvents, 96, 500);

        // My House
        sv.houses = (sv.houses ?? new List<HouseLayout>()).Where(h => h != null && !string.IsNullOrEmpty(h.spot) && h.spot.Length <= 64).Take(100).ToList();
        foreach (var h in sv.houses) CleanHouse(h);
        sv.ownedFurniture = (sv.ownedFurniture ?? new List<int>()).Distinct().Take(2000).ToList();
        sv.ownedMaterials = (sv.ownedMaterials ?? new List<int>()).Distinct().Take(500).ToList();

        // Ausstattung je Auto (nur eigene Autos, je Auto ein Eintrag)
        sv.carConfigs = (sv.carConfigs ?? new List<CarConfigEntry>())
            .Where(c => c != null && !string.IsNullOrEmpty(c.car) && c.car.Length <= 64 && c.color >= 0 && c.color < 64)
            .GroupBy(c => c.car.ToLowerInvariant()).Select(g => g.Last()).Take(500).ToList();

        // MyTDULife, Interieur
        sv.myLife = CleanLife(sv.myLife, old?.myLife);
        sv.interior ??= "";
        if (sv.interior.Length > 96) sv.interior = "";
        sv.story = Math.Clamp(sv.story, -1, 11);

        // Zustand je Auto, Radio, Optionen
        sv.carDirt = (sv.carDirt ?? new List<DirtEntry>()).Where(d => d != null && !string.IsNullOrEmpty(d.car) && d.car.Length <= 64).Take(500).ToList();
        sv.carUpgrades = (sv.carUpgrades ?? new List<UpgradeEntry>()).Where(u => u != null && !string.IsNullOrEmpty(u.car) && u.car.Length <= 64).Take(500).ToList();
        sv.garage = (sv.garage ?? new List<GarageSlotEntry>()).Where(g => g != null && (g.house ?? "").Length <= 64 && (g.car ?? "").Length <= 64 && g.slot >= 0).Take(1000).ToList();
        sv.radioStationName = Cut(sv.radioStationName, 64);
        sv.options = Cut(sv.options, 8192);
        sv.keys = Cut(sv.keys, 8192);

        // Geschichte, TDU Media, Rennen, Spielerlisten
        sv.storyAvatar = Cut(sv.storyAvatar, 64);
        if (sv.newsSeen < 0) sv.newsSeen = 0;
        if (sv.updatesSeen < 0) sv.updatesSeen = 0;
        sv.raceCinSeen = (sv.raceCinSeen ?? new List<string>()).Where(c => !string.IsNullOrEmpty(c) && c.Length <= 64).Distinct().Take(500).ToList();
        sv.racing = Entries(sv.racing, 128, 4000);
        sv.recentPlayers = (sv.recentPlayers ?? new List<SavedRecent>()).Where(r => r != null && !string.IsNullOrEmpty(r.nick) && r.nick.Length <= 32).Take(50).ToList();
        foreach (var r in sv.recentPlayers) r.lastSeen = Cut(r.lastSeen, 40);
        sv.friendNews = (sv.friendNews ?? new List<FriendNewsEntry>()).Where(n => n != null && (n.nick ?? "").Length <= 32 && (n.date ?? "").Length <= 40).Take(30).ToList();

        sv.savedAt = DateTime.UtcNow.ToString("o");
        return sv;
    }

    /// <summary>MyTDULife prüfen: Längen begrenzen, Foto nur als kleines Base64-Bild; ohne Daten das bisherige behalten.</summary>
    public static MyLifeData CleanLife(MyLifeData l, MyLifeData old)
    {
        if (l == null) return old ?? new MyLifeData();
        l.mood = Cut(l.mood, 16);
        if (!Regex.IsMatch(l.mood, "^[a-z_]*$")) l.mood = "neutral";
        l.moodText = Cut(l.moodText, 60);
        l.boxes = (l.boxes ?? new List<string>()).Select(b => Cut(b, 24)).Take(8).ToList();
        l.fields = Entries(l.fields, 32, 32);
        l.gender = Cut(l.gender, 8);
        l.photo ??= "";
        if (l.photo.Length > 96 * 1024 || !Regex.IsMatch(l.photo, "^[A-Za-z0-9+/=]*$"))
        {
            l.photo = old?.photo ?? "";
            l.photoVer = old?.photoVer ?? 0;
        }
        if (l.photoVer < 0) l.photoVer = 0;
        return l;
    }

    /// <summary>Einrichtung eines Hauses prüfen (in place): Einträge begrenzen. false = ungültig (kein Spot).</summary>
    public static bool CleanHouse(HouseLayout h)
    {
        if (h == null || string.IsNullOrEmpty(h.spot) || h.spot.Length > 64) return false;
        h.furn = Entries(h.furn, 64, 200);
        h.mats = Entries(h.mats, 64, 100);
        return true;
    }

    static string Cut(string v, int n) => string.IsNullOrEmpty(v) ? "" : (v.Length > n ? v[..n] : v);

    static List<HouseEntry> Entries(List<HouseEntry> list, int maxLen, int maxCount) =>
        (list ?? new List<HouseEntry>()).Where(e => e != null && (e.k ?? "").Length <= maxLen && (e.v ?? "").Length <= maxLen).Take(maxCount).ToList();
}
