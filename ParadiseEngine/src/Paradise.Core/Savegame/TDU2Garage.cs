#nullable disable
using Paradise.Core.Online;
using Paradise.Core.Vehicles;

namespace Paradise.Core.Savegame
{
    /// <summary>
    /// Stellplätze der Garagen (Original: Garage[] im Spielstand, je Eintrag Haus + IndexInGarage + Auto; der Code der Garage in
    /// unpacked.exe 0xAC2E50–0xAC3910 liest/tauscht darüber). Gehalten in SaveData.garage, gemeinsam benutzt von der Garage
    /// (Autos auf die Stellplätze, Fahrzeugliste, Platz wechseln) und dem Savegame-Export (TDU2SaveGame.Build).
    /// </summary>
    public static class TDU2Garage
    {
        /// <summary>Haus eines neuen Spiels (Vorlage: Garage[] mit 2 Plätzen des Wohnwagens auf Ibiza).</summary>
        public const string StarterHouse = "EPH_L0C_1254I";

        /// <summary>
        /// Häuser des Spielers in Kaufreihenfolge (Original: die Häuser, deren Stellplätze in Garage[] stehen; GM_Estates "buyh"
        /// 0xAED200 fügt hinzu, "selh" 0xAED080 entfernt). Nur EPH_-Häuser aus DBHouse.
        /// </summary>
        public static List<HouseInfo> Houses(CarDatabase db) => Houses(db, TDU2Session.Save);

        public static List<HouseInfo> Houses(CarDatabase db, SaveData s)
        {
            var r = new List<HouseInfo>();
            if (db == null) return r;
            foreach (var name in OwnedNames(s))
                foreach (var h in db.Houses)
                    if (Same(h.SpotName, name) && h.SpotName.StartsWith("EPH_", StringComparison.OrdinalIgnoreCase) && !r.Contains(h)) { r.Add(h); break; }
            return r;
        }

        /// <summary>
        /// Besessene Häuser (Spotnamen). Leer = Stand ohne Hausliste (neues Spiel bzw. Server-Stand von vor dem Hauskauf): das Starthaus
        /// und die Häuser, in deren Garagen schon Autos stehen (SaveData.garage) – damit kein Auto seinen Platz verliert.
        /// </summary>
        public static List<string> OwnedNames(SaveData s)
        {
            if (s == null) return new List<string> { StarterHouse };
            s.ownedHouses ??= new List<string>();
            if (s.ownedHouses.Count == 0)
            {
                s.ownedHouses.Add(StarterHouse);
                if (s.garage != null)
                    foreach (var e in s.garage)
                        if (e != null && !string.IsNullOrEmpty(e.house) && !string.IsNullOrEmpty(e.car) && !s.ownedHouses.Exists(n => Same(n, e.house)))
                            s.ownedHouses.Add(e.house);
            }
            return s.ownedHouses;
        }

        /// <summary>Gehört das Haus dem Spieler (Original 0x402261 auf dem Profil)?</summary>
        public static bool Owns(SaveData s, string house)
        {
            if (string.IsNullOrEmpty(house)) return false;
            foreach (var n in OwnedNames(s)) if (Same(n, house)) return true;
            return false;
        }

        /// <summary>Haus kaufen (Original 0x43BED0): ans Ende der Liste.</summary>
        public static bool AddHouse(SaveData s, string house)
        {
            if (s == null || string.IsNullOrEmpty(house) || Owns(s, house)) return false;
            s.ownedHouses.Add(house);
            return true;
        }

        /// <summary>
        /// Haus abgeben (Verkauf/Tausch, Original 0x4268AF): aus der Liste, seine Autos auf freie Plätze der übrigen Häuser
        /// (die Prüfung "seho"/"buho" stellt vorher sicher, dass genug Plätze frei sind). Das letzte Haus wird nie abgegeben.
        /// </summary>
        public static bool RemoveHouse(SaveData s, CarDatabase db, string house)
        {
            if (s == null || !Owns(s, house) || OwnedNames(s).Count <= 1) return false;
            s.ownedHouses.RemoveAll(n => Same(n, house));
            s.garage ??= new List<GarageSlotEntry>();
            foreach (var e in s.garage) if (Same(e.house, house)) e.house = null;   // ohne Platz: Normalize verteilt neu
            s.garage.RemoveAll(e => e.house == null);
            Normalize(s, db, s.ownedHouses[0]);
            return true;
        }

        /// <summary>Stellplätze aller besessenen Häuser (Original 0x407086).</summary>
        public static int TotalSlots(SaveData s, CarDatabase db)
        {
            int n = 0;
            foreach (var h in Houses(db, s)) n += h.CarSlots;
            return n;
        }

        /// <summary>Autos des Spielers, die einen Platz brauchen (Original 0x42C16F).</summary>
        public static int CarCount(SaveData s)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (s?.ownedCars != null) foreach (var c in s.ownedCars) if (!string.IsNullOrEmpty(c)) set.Add(c);
            return set.Count;
        }

        /// <summary>Freie Stellplätze über alle Häuser (Händler: "Garage voll"-Warnung).</summary>
        public static int FreeSlots(SaveData s, CarDatabase db) => TotalSlots(s, db) - CarCount(s);

        /// <summary>
        /// Einträge bereinigen (Auto nicht mehr im Besitz, Haus unbekannt, Platz außerhalb, doppelt) und Autos ohne Platz auf freie Plätze
        /// verteilen: Haus <paramref name="home"/> zuerst, dann die übrigen in DBHouse-Reihenfolge (wie TDU2SaveGame.Build bisher).
        /// </summary>
        public static void Normalize(SaveData s, CarDatabase db, string home)
        {
            if (s == null || db == null) return;
            s.garage ??= new List<GarageSlotEntry>();
            var houses = Houses(db, s);
            var slots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in houses) slots[h.SpotName] = h.CarSlots;
            var owned = new HashSet<string>(s.ownedCars ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var seenCar = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenSlot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            s.garage.RemoveAll(e => e == null || string.IsNullOrEmpty(e.car) || !owned.Contains(e.car) || !slots.TryGetValue(e.house ?? "", out int n)
                                    || e.slot < 0 || e.slot >= n || !seenCar.Add(e.car) || !seenSlot.Add(e.house + "#" + e.slot));
            var order = new List<HouseInfo>(houses);
            if (!string.IsNullOrEmpty(home)) order.Sort((a, b) => (Same(a.SpotName, home) ? 0 : 1).CompareTo(Same(b.SpotName, home) ? 0 : 1));
            foreach (var car in s.ownedCars ?? new List<string>())
            {
                if (string.IsNullOrEmpty(car) || seenCar.Contains(car)) continue;
                bool placed = false;
                foreach (var h in order)
                {
                    for (int k = 0; k < h.CarSlots && !placed; k++)
                        if (seenSlot.Add(h.SpotName + "#" + k)) { s.garage.Add(new GarageSlotEntry { house = h.SpotName, slot = k, car = car }); placed = true; }
                    if (placed) break;
                }
                seenCar.Add(car);
            }
        }

        /// <summary>Auto auf Platz <paramref name="slot"/> der Garage von <paramref name="house"/> (null = frei).</summary>
        public static string CarAt(SaveData s, string house, int slot)
        {
            if (s?.garage == null) return null;
            foreach (var e in s.garage) if (e.slot == slot && Same(e.house, house)) return e.car;
            return null;
        }

        /// <summary>Haus und Platz eines Autos (null = keiner).</summary>
        public static GarageSlotEntry SlotOf(SaveData s, string car)
        {
            if (s?.garage == null || string.IsNullOrEmpty(car)) return null;
            foreach (var e in s.garage) if (Same(e.car, car)) return e;
            return null;
        }

        /// <summary>Belegte Plätze einer Garage.</summary>
        public static int Used(SaveData s, string house)
        {
            int n = 0;
            if (s?.garage != null) foreach (var e in s.garage) if (Same(e.house, house)) n++;
            return n;
        }

        /// <summary>Zwei Plätze tauschen (auch frei <-> belegt, auch zwischen Häusern) – Original: "vali" in Platz wechseln.</summary>
        public static void Swap(SaveData s, string houseA, int slotA, string houseB, int slotB)
        {
            if (s == null) return;
            s.garage ??= new List<GarageSlotEntry>();
            GarageSlotEntry a = null, b = null;
            foreach (var e in s.garage)
            {
                if (e.slot == slotA && Same(e.house, houseA)) a = e;
                else if (e.slot == slotB && Same(e.house, houseB)) b = e;
            }
            if (a != null) { a.house = houseB; a.slot = slotB; }
            if (b != null) { b.house = houseA; b.slot = slotA; }
        }

        /// <summary>Auto in diese Garage stellen, wenn dort ein Platz frei ist (Einfahren mit einem Auto aus einer anderen Garage).</summary>
        public static bool MoveToFreeSlot(SaveData s, CarDatabase db, string car, string house)
        {
            if (s == null || string.IsNullOrEmpty(car) || string.IsNullOrEmpty(house)) return false;
            int n = 0;
            foreach (var h in Houses(db, s)) if (Same(h.SpotName, house)) n = h.CarSlots;
            for (int k = 0; k < n; k++)
                if (CarAt(s, house, k) == null)
                {
                    var e = SlotOf(s, car);
                    if (e == null) { s.garage ??= new List<GarageSlotEntry>(); s.garage.Add(new GarageSlotEntry { house = house, slot = k, car = car }); }
                    else { e.house = house; e.slot = k; }
                    return true;
                }
            return false;
        }

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
