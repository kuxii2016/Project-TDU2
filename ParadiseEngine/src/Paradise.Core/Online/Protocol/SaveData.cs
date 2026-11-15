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
        /// <summary>Lesezeichen der großen Karte (je Insel, Spielkoordinaten).</summary>
        public List<BookmarkEntry> bookmarks = new List<BookmarkEntry>();
        /// <summary>Als Favorit markierte Spots der großen Karte (Spot-Code).</summary>
        public List<string> favoriteSpots = new List<string>();
        /// <summary>My House: Einrichtung je Haus (Spot-Code) – Möbel-Bank je Entity, Material je Fläche.</summary>
        public List<HouseLayout> houses = new List<HouseLayout>();
        /// <summary>Welches Auto auf welchem Stellplatz welcher Garage steht (Garage -> Fahrzeugliste, Platz wechseln). Leer bzw. unvollständig:
        /// TDU2Garage.Normalize verteilt die übrigen Autos wie der Savegame-Export (Haus des Spielers zuerst).</summary>
        public List<GarageSlotEntry> garage = new List<GarageSlotEntry>();
        /// <summary>Gekaufte Möbel (DBFurniture._hashcode) und Böden/Tapeten (DBMaterial._hashcode).</summary>
        public List<int> ownedFurniture = new List<int>();
        public List<int> ownedMaterials = new List<int>();
        /// <summary>MyTDULife (Tagebuch "My Life"): Stimmung, Profil, Kästen, Avatar-Foto.</summary>
        public MyLifeData myLife = new MyLifeData();
        /// <summary>Zuletzt betretenes Interieur ("SPOT|bank", leer = draußen) – beim Anmelden geht es dort weiter.</summary>
        public string interior = "";
        /// <summary>Ausstattung je Auto (Autohändler: Felgen, Lack, Innenraum).</summary>
        public List<CarConfigEntry> carConfigs = new List<CarConfigEntry>();
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
        public string savedAt = "";
    }
}
