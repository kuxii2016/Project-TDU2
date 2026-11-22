using Paradise.Core.Online;

namespace Paradise.Game.Utils
{
    /// <summary>
    /// My-Life-Fortschritt aus dem vorhandenen Server-Spielstand (<see cref="SaveData"/>).
    /// </summary>
    public class UserProgress
    {
        /// <summary>Gebiets-Maxima aus dem Original (Reihenfolge H1..H4, I1..I2); nur Info, kein Level.</summary>
        long[] maxRoads = { 467, 924, 309, 1144, 426, 998 };
        long[] maxCruising = { 2080, 2080, 2425, 2640, 2550, 2725 };

        /// <summary>DBRacingLevel / DBCollectionLevel / DBSocialLevel, Stufen 0..15.</summary>
        private static readonly int[] StandardThresholds = { 0, 125, 300, 525, 825, 1200, 1650, 2175, 2795, 3510, 4320, 5225, 6225, 7355, 8615, 10000 };
        /// <summary>DBCruisingLevel + DBAreaLevel, Stufen 0..17 (pause_menu.cpp kPageMaxPoints[1] = 14500).</summary>
        private static readonly int[] DiscoveryThresholds = { 0, 125, 300, 525, 825, 1200, 1650, 2175, 2795, 3510, 4320, 5225, 6225, 7355, 8615, 10000, 11500, 13000, 14500 };

        /// <summary>DBRewardXP CRUISING_WRECK_PART_* = 25 Punkte je Wrack (Wrecks.XpPerWreck).</summary>
        private const int WreckPoints = 25;
        /// <summary>NB_LVL der Completion-Seite: 19+20+17+17</summary>
        private const int GlobalMaxLevel = 73;

        public class MyLifeCollector
        {
            public uint Level { get; set; } = 0;
            public uint Points { get; set; } = 0;
            public uint NbHouseOwned { get; set; } = 0;
            public uint NbClothesOwned { get; set; } = 0;
            public uint NbFurnituresOwned { get; set; } = 0;
            public uint NbStickersOwned { get; set; } = 0;
            public uint NbHaircutsTried { get; set; } = 0;   // aus Avatar (DBClothe parttype Hair)
            public uint NbVehiclesOwned { get; set; } = 0;
            public uint NbSurgeryTried { get; set; } = 0;    // kein Savegame-Feld
        }

        public class MyLifeCruising
        {
            public uint Level { get; set; } = 0;
            /// <summary>Cruising-Punkte = Savegame cruisingRoadPoints + 25 je gefundenem Wrack.</summary>
            public uint Points { get; set; } = 0;
            public uint UnlockedRoads { get; set; } = 0;
            public uint NbEventsCompleted { get; set; } = 0;
            public uint NbIbizaPhotos { get; set; } = 0;     // kein Savegame-Feld
            public uint NbOhahuPhotos { get; set; } = 0;     // kein Savegame-Feld
            public uint NbDubaiPhotos { get; set; } = 0;     // kein Savegame-Feld
            public uint NbWrecksCollected { get; set; } = 0;
        }

        public class MyLifeFreeride
        {
            public uint Level { get; set; } = 0;
            /// <summary>Gefahrene Strecke aller Odometer in km.</summary>
            public float DrivenDistance { get; set; } = 0;
            public float LongestCruise { get; set; } = 0;    // kein Savegame-Feld
            public uint BestFrogSession { get; set; } = 0;   // kein Savegame-Feld
            public uint BestDrift { get; set; } = 0;         // kein Savegame-Feld
            public uint BestJump { get; set; } = 0;          // kein Savegame-Feld
            public uint BestDodgeChain { get; set; } = 0;    // kein Savegame-Feld
        }

        public class MyLifePlayer
        {
            /// <summary>Global-Level = Racing + Cruising + Collection + Social (max. 73).</summary>
            public uint Level { get; set; } = 0;
            public uint CurrentRLP { get; set; } = 0;   // Racing-Punkte
            public uint CurrentCrLP { get; set; } = 0;  // Cruising-Punkte
            public uint CurrentCoLP { get; set; } = 0;  // Collection-Punkte
            public uint CurrentSLP { get; set; } = 0;   // Social-Punkte
            public uint AchievementPoints { get; set; } = 0;
            public int Money { get; set; } = 0;
            public string PersonalQuote { get; set; } = "Testdrive Highlife";
            public uint MoodId { get; set; } = 0;       // mood ist ein Label, keine Zahl
            public uint NbHouses { get; set; } = 0;
            public uint NbCars { get; set; } = 0;
            public uint NbClubMembers { get; set; } = 0; // kein Savegame-Feld
            public int Country { get; set; } = 0;        // Nationalität liegt im Account, nicht in SaveData
            public string Nick { get; set; } = string.Empty; // liegt im Account, nicht in SaveData
            public uint NbClubCars { get; set; } = 0;    // kein Savegame-Feld
        }

        public class MyLifeRacing
        {
            public uint Level { get; set; } = 0;
            /// <summary>Racing-Punkte aus DBRewardXP-Ereignissen (Racing.Points).</summary>
            public uint Points { get; set; } = 0;
            public uint NbLicensesWon { get; set; } = 0;
            public uint NbChampCompleted { get; set; } = 0;
            public uint NbCupsWon { get; set; } = 0;
            public uint NbMultiChalWon { get; set; } = 0;  // kein Savegame-Feld
            public uint NbInstChalWon { get; set; } = 0;   // kein Savegame-Feld
            public uint NbDuelsWon { get; set; } = 0;      // kein Savegame-Feld
            public uint BoxLevel { get; set; } = 0;        // kein Savegame-Feld
        }

        public class MyLifeSocial
        {
            public uint Level { get; set; } = 0;
            /// <summary>Social-Punkte aus DBRewardXP-Ereignissen (ohne Savegame-Zähler derzeit 0).</summary>
            public uint Points { get; set; } = 0;
            public uint NbFriends { get; set; } = 0;          // kein Savegame-Feld
            public uint NbInnerClubChalPlayed { get; set; } = 0;
            public uint NbFriendsInvited { get; set; } = 0;
            public uint NbChasePlayed { get; set; } = 0;
            public uint NbCoopPlayed { get; set; } = 0;
            public float TimeInCoop { get; set; } = 0;
        }

        /// <summary>Die vier Skill-Kategorien der Level-Anzeige (Reihenfolge wie pause_menu.cpp).</summary>
        public enum ProgressCategory { Racing, Cruising, Collection, Social }

        public MyLifeCollector getMyLifeCollector { get; private set; } = new();
        public MyLifeCruising getMyLifeCruising { get; private set; } = new();
        public MyLifeFreeride getMyLifeFreeride { get; private set; } = new();
        public MyLifePlayer getMyLifePlayer { get; private set; } = new();
        public MyLifeRacing getMyLifeRacing { get; private set; } = new();
        public MyLifeSocial getMyLifeSocial { get; private set; } = new();

        // ------------------------------------------------------------------ Getter
        // Kurzer Zugriff auf die wichtigsten Werte, ohne über die DTO-Objekte zu gehen.

        /// <summary>Level Racing (0..15).</summary>
        public uint RacingLevel => getMyLifeRacing.Level;
        /// <summary>Level Cruising/Discovery (0..17).</summary>
        public uint CruisingLevel => getMyLifeCruising.Level;
        /// <summary>Level Collection (0..15).</summary>
        public uint CollectionLevel => getMyLifeCollector.Level;
        /// <summary>Alias zu <see cref="CollectionLevel"/>.</summary>
        public uint CollectorLevel => CollectionLevel;
        /// <summary>Level Social (0..15).</summary>
        public uint SocialLevel => getMyLifeSocial.Level;
        /// <summary>Freeride besitzt laut Doku keine eigene Level-Tabelle – immer 0.</summary>
        public uint FreerideLevel => getMyLifeFreeride.Level;
        /// <summary>Global-Level = Racing + Cruising + Collection + Social (max. 73).</summary>
        public uint TotalLevel => getMyLifePlayer.Level;
        /// <summary>Alias zu <see cref="TotalLevel"/>.</summary>
        public uint GlobalLevel => TotalLevel;

        public uint RacingPoints => getMyLifeRacing.Points;
        public uint CruisingPoints => getMyLifeCruising.Points;
        public uint CollectionPoints => getMyLifeCollector.Points;
        public uint SocialPoints => getMyLifeSocial.Points;
        /// <summary>Summe der vier Skill-Punktestände.</summary>
        public uint TotalPoints => RacingPoints + CruisingPoints + CollectionPoints + SocialPoints;

        /// <summary>Level einer Kategorie über den enum (praktisch für Tabellen/Schleifen).</summary>
        public uint LevelFor(ProgressCategory category) => category switch
        {
            ProgressCategory.Racing => RacingLevel,
            ProgressCategory.Cruising => CruisingLevel,
            ProgressCategory.Collection => CollectionLevel,
            ProgressCategory.Social => SocialLevel,
            _ => 0,
        };

        /// <summary>Punktestand einer Kategorie über den enum.</summary>
        public uint PointsFor(ProgressCategory category) => category switch
        {
            ProgressCategory.Racing => RacingPoints,
            ProgressCategory.Cruising => CruisingPoints,
            ProgressCategory.Collection => CollectionPoints,
            ProgressCategory.Social => SocialPoints,
            _ => 0,
        };

        /// <summary>Fortschritt zur nächsten Stufe in Prozent (RATIO = NB-PT * 100 / MX-PT).</summary>
        public int LevelPercentFor(ProgressCategory category) =>
            LevelPercent((int)PointsFor(category), category == ProgressCategory.Cruising);

        /// <summary>Nächste Punkteschwelle der Kategorie.</summary>
        public int NextThresholdFor(ProgressCategory category) =>
            NextThreshold((int)PointsFor(category), category == ProgressCategory.Cruising);

        // Häufig gebrauchte Einzelwerte
        public int Money => getMyLifePlayer.Money;
        public uint NbCars => getMyLifePlayer.NbCars;
        public uint NbHouses => getMyLifePlayer.NbHouses;
        public uint NbWrecks => getMyLifeCruising.NbWrecksCollected;
        public uint UnlockedRoads => getMyLifeCruising.UnlockedRoads;
        public uint NbEventsCompleted => getMyLifeCruising.NbEventsCompleted;
        public uint NbLicensesWon => getMyLifeRacing.NbLicensesWon;
        public uint NbCupsWon => getMyLifeRacing.NbCupsWon;
        public uint NbChampCompleted => getMyLifeRacing.NbChampCompleted;
        /// <summary> Gefahrene Strecke aller Odometer in km.</summary>
        public float DrivenDistanceKm => getMyLifeFreeride.DrivenDistance;
        public string Nick => getMyLifePlayer.Nick;
        public string PersonalQuote => getMyLifePlayer.PersonalQuote;

        public UserProgress() { }

        public UserProgress(SaveData Save) => Refresh(Save);

        /// <summary>Neu aus dem Spielstand füllen (nach Fund/Kauf/Import).</summary>
        public void Refresh(SaveData Save)
        {
            if (Save == null) return;

            // ---- Collection: gezählte Bestände + echte Avatar-Kleidung/Frisuren -------------
            // Collectable wertet ownedClothes UND den Avatar aus (die DATA speichert die
            // gekauften Kleidungsstücke nicht) – dieselbe Quelle wie die Collection-Liste.
            var col = getMyLifeCollector;
            col.NbHouseOwned = Count(Save.houses);
            col.NbClothesOwned = (uint)Paradise.Game.Gameplay.Collectable.ClothesCount;
            col.NbHaircutsTried = (uint)Paradise.Game.Gameplay.Collectable.HaircutCount;
            col.NbFurnituresOwned = Count(Save.ownedFurniture);
            col.NbStickersOwned = Count(Save.stickerBoards);
            col.NbVehiclesOwned = Count(Save.ownedCars);

            // ---- Cruising: Straßenpunkte + Wrackpunkte, beides aus dem Savegame -------------
            var cru = getMyLifeCruising;
            int roads = 0;
            foreach (var e in Save.exploredRoads ?? new List<RoadDiscoveryEntry>())
                if (e != null) roads += (int)Count(e.ids);
            int wrecks = 0;
            foreach (var e in Save.foundWrecks ?? new List<WreckFoundEntry>())
                if (e != null) wrecks += (int)Count(e.indices);
            cru.UnlockedRoads = (uint)roads;
            cru.NbWrecksCollected = (uint)wrecks;
            cru.NbEventsCompleted = (uint)(Count(Save.cruisingRoadEvents) + Count(Save.wreckEvents));

            // ---- Freeride: Odometer sind die einzige vorhandene Distanzangabe ---------------
            var free = getMyLifeFreeride;
            double meters = 0;
            foreach (var o in Save.odometers ?? new List<OdoEntry>())
                if (o != null) meters += Math.Max(0, o.meters);
            free.DrivenDistance = (float)(meters / 1000.0);   // km

            // ---- Player --------------------------------------------------------------------
            var pl = getMyLifePlayer;
            pl.Money = (int)Math.Clamp(Save.money, int.MinValue, int.MaxValue);
            pl.NbHouses = col.NbHouseOwned;
            pl.NbCars = col.NbVehiclesOwned;
            if (Save.myLife != null && !string.IsNullOrWhiteSpace(Save.myLife.moodText))
                pl.PersonalQuote = Save.myLife.moodText;

            // ---- Racing: bevorzugte Prüfungen im Savegame (RaceProgress-Schlüssel) -----------
            var race = getMyLifeRacing;
            var entries = Save.racing ?? new List<HouseEntry>();
            int licences = 0, champs = 0, cupsWon = 0;
            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrEmpty(e.k)) continue;
                if (e.k.StartsWith("TDU2.School.Lic.", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(e.v))
                    licences++;
                else if (e.k.EndsWith(".Done", StringComparison.OrdinalIgnoreCase))
                {
                    champs++;
                    string val = e.v ?? "";
                    int bar = val.IndexOf('|');
                    if (bar >= 0 && int.TryParse(val[(bar + 1)..], out int place) && place == 1) cupsWon++;
                }
            }
            race.NbLicensesWon = (uint)licences;
            race.NbChampCompleted = (uint)champs;
            race.NbCupsWon = (uint)cupsWon;

            // ---- Punkte: nur was das Savegame/die Doku hergibt ------------------------------
            // Cruising: Discovery.TotalPoints – cruisingRoadPoints enthält bereits die
            // Meilenstein-Prämien; Wracks zahlen laut DBRewardXP 25 Punkte je Stück.
            int cruisePoints = Math.Max(0, Save.cruisingRoadPoints) + wrecks * WreckPoints;
            // Racing/Collectable/Social werten dieselben Savegame-Keys und die echte DBRewardXP-Tabelle aus.
            // Frisuren kommen aus ownedClothes/Avatar, Operationen und alle Social-Aktivitäten
            // haben kein Savegame-Feld -> 0.
            int racePoints = Paradise.Game.Gameplay.Racing.Current?.Points ?? 0;
            int colPoints = Paradise.Game.Gameplay.Collectable.Current?.Points ?? 0;
            int socPoints = Paradise.Game.Gameplay.Social.Current?.Points ?? 0;

            cru.Points = pl.CurrentCrLP = (uint)cruisePoints;
            race.Points = pl.CurrentRLP = (uint)racePoints;
            col.Points = pl.CurrentCoLP = (uint)colPoints;
            getMyLifeSocial.Points = pl.CurrentSLP = (uint)socPoints;

            // ---- Level-Stufen ---------------------------------------------------------------
            col.Level = (uint)LevelForPoints(colPoints, discovery: false);
            cru.Level = (uint)LevelForPoints(cruisePoints, discovery: true);
            race.Level = (uint)LevelForPoints(racePoints, discovery: false);
            getMyLifeSocial.Level = (uint)LevelForPoints(socPoints, discovery: false);

            // Freeride hat keine Tabelle in der Doku.
            free.Level = 0;

            // Global = Summe der vier Skill-Levels (pause_menu.cpp:1032). Der optionale
            // Legacy-Key TDU2.RacingLevel aus SaveData.racing ist ein Level, keine Punktzahl.
            int global = (int)(race.Level + cru.Level + col.Level + getMyLifeSocial.Level);
            foreach (var e in entries)
                if (e != null && string.Equals(e.k, "TDU2.RacingLevel", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(e.v, out int legacy) && legacy > global)
                    global = legacy;
            pl.Level = (uint)Math.Clamp(global, 0, GlobalMaxLevel);
        }

        /// <summary>Level zu einer Punktzahl: höchste Schwelle, die erreicht ist (FindSkillLevelIndex).</summary>
        public static int LevelForPoints(int points, bool discovery = false)
        {
            var t = discovery ? DiscoveryThresholds : StandardThresholds;
            int level = 0;
            for (int i = 0; i < t.Length; i++)
                if (points >= t[i]) level = i;
            return level;
        }

        /// <summary>Nächste Schwelle für die Anzeige NB-PT * 100 / MX-PT (USERLEVEL_Dokumentation §5.1).</summary>
        public static int NextThreshold(int points, bool discovery = false)
        {
            var t = discovery ? DiscoveryThresholds : StandardThresholds;
            for (int i = 0; i < t.Length; i++)
                if (t[i] > points) return t[i];
            return t.Length > 0 ? t[^1] : 0;
        }

        /// <summary>Fortschritt in Prozent zur nächsten Stufe (RATIO = NB-PT * 100 / MX-PT).</summary>
        public static int LevelPercent(int points, bool discovery = false)
        {
            int next = NextThreshold(points, discovery);
            return next > 0 ? Math.Min(100, Math.Max(0, points) * 100 / next) : 100;
        }

        static uint Count<T>(List<T>? list) => (uint)(list?.Count ?? 0);
    }
}