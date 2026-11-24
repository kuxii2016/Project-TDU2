#nullable disable
// Portiert aus dem Unity-Projekt (Runtime/Online/Protocol/SaveData.cs).
namespace Paradise.Core.Online
{
    /// <summary>Spielstand – liegt auf dem Server (data/accounts/&lt;nick&gt;.json).</summary>
    [Serializable]
    public sealed class SaveData
    {
        public int version = 1;
        public string island = "";
        public bool hasPosition;
        public float px, py, pz, heading;   // Spielkoordinaten (Insel), Richtung in Grad um Y
        public string currentCar = "";
        public List<string> ownedCars = new List<string>();
        public long money;
        public bool starterChosen;
        public List<OdoEntry> odometers = new List<OdoEntry>();
        /// <summary>Lackierung/Beklebung je Auto (Sticker-Shop): Base64 von CarLivery.Encode.</summary>
        public List<LiveryEntry> liveries = new List<LiveryEntry>();
        /// <summary>Gekaufte Sticker-Planchen (DBStickersBanks._hashcode).</summary>
        public List<int> stickerBoards = new List<int>();
        /// <summary>"Meine Farben" des Sticker-Shops (6 Plätze, 0xRRGGBB, -1 = leer).</summary>
        public List<int> favoriteColors = new List<int>();
        /// <summary>Aussehen des Avatars (AvatarConfig.Encode: Geschlecht, Herkunft, Kleidung mit Farben).</summary>
        public string avatar = "";
        /// <summary>Gekaufte Kleidungsstücke (DBClothe._hashcode).</summary>
        public List<int> ownedClothes = new List<int>();
        /// <summary>Gekaufte Farben je Kleidungsstück wie das Original (AvatarSave.ClothesSold: Bitmaske je DBClothe-Id, Bit c = Farbe c,
        /// exe 0x687AC0) – ein Kauf gilt nur für die gekaufte Farbe.</summary>
        public List<ClothesSoldEntry> clothesSold = new List<ClothesSoldEntry>();
        /// <summary>Lesezeichen der großen Karte (je Insel, Spielkoordinaten).</summary>
        public List<BookmarkEntry> bookmarks = new List<BookmarkEntry>();
        /// <summary>Als Favorit markierte Spots der großen Karte (Spot-Code).</summary>
        public List<string> favoriteSpots = new List<string>();
        /// <summary>Gefundene Wracks je Insel – 0-basierte Indizes aus wrecks.bnk/entities.xmb (wie im Original).</summary>
        public List<WreckFoundEntry> foundWrecks = new List<WreckFoundEntry>();
        /// <summary>Ausgezahlte Wreck-Events (CRUISING_WRECK_PART_&lt;ISLAND&gt;_AREA&lt;n&gt;) mit Anzahl.</summary>
        public List<HouseEntry> wreckEvents = new List<HouseEntry>();
        /// <summary>Cruising/Discovery: Version des Road-Holders (0 = alter Spielstand ohne Daten).</summary>
        public int roadDiscoveryVersion = 1;
        /// <summary>Aufgedeckte Straßenstücke je Insel (stabile IDs: CRUISING_&lt;ISLAND&gt;_ROADS_AREA&lt;n&gt;_&lt;id&gt;).</summary>
        public List<RoadDiscoveryEntry> exploredRoads = new List<RoadDiscoveryEntry>();
        /// <summary>Durch aufgedeckte Straßen freigeschaltete Dealer (Spot-Codes ECD/ECDC/EBD).</summary>
        public List<string> unlockedDealers = new List<string>();
        /// <summary>Punkte aus neu befahrenen Straßen (Cruising-Fortschritt, zusätzlich zu den Wreck-Punkten).</summary>
        public int cruisingRoadPoints;
        /// <summary>Ausgezahlte Straßen-Meilensteine (CRUISING_&lt;ISLAND&gt;_ROADS_AREA&lt;n&gt;_&lt;10|25|50|75|100&gt;).</summary>
        public List<HouseEntry> cruisingRoadEvents = new List<HouseEntry>();
        /// <summary>My House: Einrichtung je Haus (Spot-Code) – Möbel-Bank je Entity, Material je Fläche.</summary>
        public List<HouseLayout> houses = new List<HouseLayout>();
        /// <summary>Welches Auto auf welchem Stellplatz welcher Garage steht (Garage -> Fahrzeugliste, Platz wechseln). Leer bzw. unvollständig:
        /// TDU2Garage.Normalize verteilt die übrigen Autos wie der Savegame-Export (Haus des Spielers zuerst).</summary>
        public List<GarageSlotEntry> garage = new List<GarageSlotEntry>();
        /// <summary>Besessene Häuser (DBHouse._spotname) in Kaufreihenfolge – Original: die Häuser mit Plätzen in Garage[] (GM_Estates
        /// buyh/selh). Leer = neues Spiel (Starthaus EPH_L0C_1254I, TDU2Garage.OwnedNames).</summary>
        public List<string> ownedHouses = new List<string>();
        /// <summary>Wohnsitz (Driver.CurrentHouse): Ziel von "Nach Hause"; null = erstes besessenes Haus.</summary>
        public string homeHouse;
        /// <summary>Verkaufte Häuser (StatisticsData.NbHouseSold).</summary>
        public int housesSold;
        /// <summary>Gekaufte Möbel (DBFurniture._hashcode) und Böden/Tapeten (DBMaterial._hashcode).</summary>
        public List<int> ownedFurniture = new List<int>();
        public List<int> ownedMaterials = new List<int>();
        /// <summary>MyTDULife (Tagebuch "My Life"): Stimmung, Profil, Kästen, Avatar-Foto.</summary>
        public MyLifeData myLife = new MyLifeData();
        /// <summary>Zuletzt betretenes Interieur ("SPOT|bank", leer = draußen) – beim Anmelden geht es dort weiter.</summary>
        public string interior = "";
        /// <summary>Ausstattung je Auto (Autohändler: Felgen, Lack, Innenraum).</summary>
        public List<CarConfigEntry> carConfigs = new List<CarConfigEntry>();
        /// <summary>Verschmutzung je Auto (CarDirt: Schmutzstufe 0..1, Kratzer 0..1) – draußen und in der Garage.</summary>
        public List<DirtEntry> carDirt = new List<DirtEntry>();
        /// <summary>Tuningstufen je Auto (Tuner; CarUpgrades, gepackter int).</summary>
        public List<UpgradeEntry> carUpgrades = new List<UpgradeEntry>();
        /// <summary>Schritt der Geschichte (TDU2StoryProgress, -1 = nie begonnen) – stellt den Fortschritt auf einem anderen PC
        /// bzw. nach gelöschten PlayerPrefs wieder her.</summary>
        public int story = -1;
        /// <summary>Autoradio (Original-DATA: RadioOptions.StationHashCode/Volume): Sender (DBRadioStation._hashcode, 0 = aus),
        /// Name des Senders ("off", "hariba", "kalua", "roadtrack" – auch für Sender ohne Datenbankeintrag), Lautstärke (-1 = nie gesetzt).</summary>
        public int radioStation;
        public string radioStationName = "";
        public int radioVolume = -1;
        /// <summary>Optionsmenü ("var=wert;…", Unity PlayerPrefs "TDU2.Opt.*") und abweichende Tastenbelegung ("id=taste1,taste2;…").</summary>
        public string options = "";
        public string keys = "";
        /// <summary>Geschichte: gewählter Intro-Avatar (DBPNJ-Konfiguration, z.B. "AVT_Female_Asia_Intro") und ob die Geschichte das
        /// Story-Auto (California) gegeben hat (Tess nimmt es an der Solar Crown zurück).</summary>
        public string storyAvatar = "";
        public bool storyGaveCar;
        /// <summary>TDU Media: gelesene News/Updates des Servers (höchste gelesene Id).</summary>
        public int newsSeen, updatesSeen;
        /// <summary>Schon gesehene Renn-Kinos (RaceCinematics: Einführungen je Meisterschaft/Prüfung).</summary>
        public List<string> raceCinSeen = new List<string>();
        /// <summary>Rennschule und Meisterschaften (RaceProgress): Schlüssel/Wert wie Unity PlayerPrefs "TDU2.School.*"/"TDU2.Cup.*".</summary>
        public List<HouseEntry> racing = new List<HouseEntry>();
        /// <summary>Zuletzt gesehene Spieler (Pausemenü recent_players).</summary>
        public List<RecentEntry> recentPlayers = new List<RecentEntry>();
        /// <summary>Meldungen der Freunde (TDU Media).</summary>
        public List<FriendNewsEntry> friendNews = new List<FriendNewsEntry>();
        /// <summary>Community Racing Center: vom Race-Editor hochgeladene Challenges (Server-Ids, "Meine Challenges").</summary>
        public List<int> uploadedChallenges = new List<int>();
        /// <summary>Schönheitsklinik (GAME_MODE_SURGERY): Operationen, Verband, gesicherte Kopf-Teile – Original PlayerData/Driver.</summary>
        public SurgeryData surgery = new SurgeryData();
        public string savedAt = "";
    }

    /// <summary>
    /// Schönheitsklinik, Felder wie im Original-Spielstand (PlayerData/Driver: NbSurgeries, Bandage, BandageTime, NeedBandageNews,
    /// Helmet/BikeHelmet/Glasses/Beard/HairBandage + *ColorBandage, BeenInSurgeryShop, MultiChallengesWithBandages;
    /// StatisticsData._time_spent_in_surgery). Die gesicherten Teile sind DBClothe-Hashcodes (0 = nichts getragen), die DATA bekommt
    /// ihre DBClothe-Ids (TDU2SaveGame).
    /// </summary>
    [Serializable]
    public sealed class SurgeryData
    {
        /// <summary>Gekaufte Operationen (Driver.NbSurgeries, exe Profil +0xD8).</summary>
        public int nbSurgeries;
        /// <summary>Klinik schon betreten (Driver.BeenInSurgeryShop).</summary>
        public bool beenInSurgeryShop;
        /// <summary>Verband an (Driver.Bandage, Profil +0x10F) und Ablauf als Unix-Zeit in s (Driver.BandageTime u64, Profil +0xF8).</summary>
        public bool bandage;
        public long bandageTime;
        /// <summary>Meldung "Ihre Verbände wurden entfernt!" steht aus (Driver.NeedBandageNews, Profil +0x110).</summary>
        public bool needBandageNews;
        /// <summary>Vor dem Verband getragene Teile und Farben (Profil +0x100…+0x10E), beim Abnehmen zurückgesetzt.</summary>
        public int helmetBandage, bikeHelmetBandage, glassesBandage, beardBandage, hairBandage;
        public int helmetColorBandage, bikeHelmetColorBandage, glassesColorBandage, beardColorBandage, hairColorBandage;
        /// <summary>Multiplayer-Herausforderungen mit Verband (Driver.MultiChallengesWithBandages).</summary>
        public int multiChallengesWithBandages;
        /// <summary>Zeit in der Klinik in s (StatisticsData._time_spent_in_surgery).</summary>
        public float timeSpentInSurgery;
    }

    /// <summary>Gekaufte Farben eines Kleidungsstücks (DBClothe._hashcode, Bit c = Farbe c).</summary>
    [Serializable]
    public sealed class ClothesSoldEntry
    {
        public int clothe;
        public int colors;
    }

    /// <summary>Gefundene Wracks einer Insel – Indizes sind die 0-basierten Array-Indizes aus wrecks.bnk.</summary>
    [Serializable]
    public sealed class WreckFoundEntry
    {
        public string island = "";
        public List<int> indices = new List<int>();
    }

    /// <summary>
    /// Aufgedeckte Straßen einer Insel. Die IDs sind stabil gegenüber Listenänderungen (keine reinen Array-Indizes):
    /// CRUISING_IBIZA_ROADS_AREA{area}_{segment}, CRUISING_HAWAI_ROADS_AREA{area}_{segment}.
    /// areas enthält optional die Zähler je Gebiet (k = Area-Index, v = Anzahl aufgedeckter Segmente).
    /// </summary>
    [Serializable]
    public sealed class RoadDiscoveryEntry
    {
        public string island = "";
        public List<string> ids = new List<string>();
        public List<HouseEntry> areas = new List<HouseEntry>();
    }
}
