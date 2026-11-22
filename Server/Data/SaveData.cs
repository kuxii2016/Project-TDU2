namespace TDU2Server.Data;

// Spielstand (Feldnamen = JSON-Namen, identisch im Unity-Client TDU2Net.cs)

public sealed class SaveData
{
    public int version = 1;
    public string island = "";
    public bool hasPosition;
    public float px, py, pz, heading;
    public string currentCar = "";
    public List<string> ownedCars = new();
    public long money;
    public bool starterChosen;
    public List<OdoEntry> odometers = new();
    public List<LiveryEntry> liveries = new();      // Sticker-Shop: Lackierung/Beklebung je Auto (Base64, Client CarLivery)
    public List<int> stickerBoards = new();         // gekaufte Sticker-Planchen (DBStickersBanks._hashcode)
    public List<int> favoriteColors = new();        // "Meine Farben" (0xRRGGBB, -1 = leer)
    public string avatar = "";                      // Aussehen des Avatars (Client AvatarConfig.Encode)
    public List<int> ownedClothes = new();          // gekaufte Kleidungsstücke (DBClothe._hashcode)
    public List<BookmarkEntry> bookmarks = new();   // Lesezeichen der großen Karte (je Insel, Spielkoordinaten)
    public List<string> favoriteSpots = new();      // als Favorit markierte Spots (Spot-Code)
    public int roadDiscoveryVersion = 1;            // Cruising/Discovery: 0 = alter Spielstand ohne Straßendaten
    public List<RoadDiscoveryEntry> exploredRoads = new(); // aufgedeckte Straßen je Insel (stabile IDs)
    public List<string> unlockedDealers = new();    // durch Straßen freigeschaltete Dealer (ECD/ECDC/EBD)
    public int cruisingRoadPoints;                  // Punkte aus neu befahrenen Straßen
    public List<HouseEntry> cruisingRoadEvents = new(); // ausgezahlte Straßen-Meilensteine
    public List<HouseLayout> houses = new();        // My House: Einrichtung je Haus (Spot-Code)
    public List<int> ownedFurniture = new();        // gekaufte Möbel (DBFurniture._hashcode)
    public List<int> ownedMaterials = new();        // gekaufte Böden/Tapeten (DBMaterial._hashcode)
    public MyLifeData myLife = new();               // MyTDULife: Stimmung, Profil, Kästen, Avatar-Foto
    public string interior = "";                    // zuletzt betretenes Interieur ("SPOT|bank", leer = draußen)
    public List<CarConfigEntry> carConfigs = new(); // Ausstattung je Auto (Autohändler: Felgen, Lack, Innenraum)
    public int story = -1;                          // Schritt der Geschichte (Client TDU2StoryProgress, -1 = nie begonnen)
    public List<DirtEntry> carDirt = new();         // Verschmutzung je Auto (Client CarDirt)
    public List<UpgradeEntry> carUpgrades = new();  // Tuningstufen je Auto (Client CarUpgrades, gepackt)
    public List<GarageSlotEntry> garage = new();    // Stellplätze der Garagen (Haus, Platz, Auto)
    public int radioStation;                        // Autoradio: Sender-Hashcode (0 = aus)
    public string radioStationName = "";
    public int radioVolume = -1;
    public string options = "";                     // Optionsmenü ("var=wert;…")
    public string keys = "";                        // abweichende Tastenbelegung
    public string storyAvatar = "";                 // Geschichte: gewählter Intro-Avatar (DBPNJ-Konfiguration)
    public bool storyGaveCar;                       // Geschichte: Story-Auto (California) gegeben
    public int newsSeen, updatesSeen;               // TDU Media: gelesene News/Updates (höchste Id)
    public List<string> raceCinSeen = new();        // schon gesehene Renn-Kinos
    public List<HouseEntry> racing = new();         // Rennschule/Meisterschaften (Schlüssel/Wert)
    public List<SavedRecent> recentPlayers = new(); // zuletzt gesehene Spieler
    public List<FriendNewsEntry> friendNews = new();// Meldungen der Freunde
    public List<int> uploadedChallenges = new();     // CRC: vom Race-Editor hochgeladene Challenge-Ids ("Meine Challenges")
    public string savedAt = "";
}

/// <summary>Aufgedeckte Straßen einer Insel (Client RoadDiscoveryEntry).</summary>
public sealed class RoadDiscoveryEntry
{
    public string island = "";
    public List<string> ids = new();
    public List<HouseEntry> areas = new();
}

/// <summary>Ausstattung eines Autos: Lack = Index in DBCarsAndColors, Felge = DBRims._hashcode, Innenraum = DBInterior._hashcode.</summary>
public sealed class CarConfigEntry
{
    public string car = "";
    public int color;
    public int rim;
    public int interior;
}

/// <summary>Verschmutzung eines Autos (Client DirtEntry).</summary>
public sealed class DirtEntry
{
    public string car = "";
    public float level;
    public float scratches;
}

/// <summary>Tuningstufen eines Autos (Client UpgradeEntry): Beschleunigung | Höchstgeschwindigkeit &lt;&lt; 8 | Bremsen &lt;&lt; 16.</summary>
public sealed class UpgradeEntry
{
    public string car = "";
    public int upgrades;
}

/// <summary>Stellplatz einer Garage (Client GarageSlotEntry).</summary>
public sealed class GarageSlotEntry
{
    public string house = "";
    public int slot;
    public string car = "";
}

/// <summary>Zuletzt gesehener Spieler im Spielstand (Feldnamen = Client RecentEntry).</summary>
public sealed class SavedRecent
{
    public string nick = "";
    public string lastSeen = "";
    public bool online;
    public int carId;
    public int color;
    public int nbCars;
    public long money;
    public int nationality;
}

/// <summary>Meldung eines Freundes (Client FriendNewsEntry).</summary>
public sealed class FriendNewsEntry
{
    public int kind;
    public string nick = "";
    public string date = "";
    public bool read;
}

/// <summary>Kilometerstand je Auto.</summary>
public sealed class OdoEntry
{
    public int carId;
    public double meters;
}

/// <summary>Lackierung/Beklebung eines Autos (Sticker-Shop).</summary>
public sealed class LiveryEntry
{
    public string car = "";
    public string data = "";
}

/// <summary>Lesezeichen der großen Karte.</summary>
public sealed class BookmarkEntry
{
    public int id;
    public string island = "";
    public float x, y, z;
}

/// <summary>My House: Einrichtung eines Hauses.</summary>
public sealed class HouseLayout
{
    public string spot = "";
    public List<HouseEntry> furn = new();   // Entity-Name -> Bank
    public List<HouseEntry> mats = new();   // Fläche -> Material
}

/// <summary>Schlüssel/Wert-Paar (Einrichtung, Profilangaben).</summary>
public sealed class HouseEntry
{
    public string k = "";
    public string v = "";
}
