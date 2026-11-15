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
        /// <summary>Häuser des Spielers mit Garage – wie im Savegame-Export: alle EPH_-Häuser mit Stellplätzen (Kaufen kommt später).</summary>
        public static List<HouseInfo> Houses(CarDatabase db)
        {
            var r = new List<HouseInfo>();
            if (db == null) return r;
            foreach (var h in db.Houses)
                if (h.SpotName != null && h.SpotName.StartsWith("EPH_", StringComparison.OrdinalIgnoreCase) && h.CarSlots > 0) r.Add(h);
            return r;
        }

        /// <summary>
        /// Einträge bereinigen (Auto nicht mehr im Besitz, Haus unbekannt, Platz außerhalb, doppelt) und Autos ohne Platz auf freie Plätze
        /// verteilen: Haus <paramref name="home"/> zuerst, dann die übrigen in DBHouse-Reihenfolge (wie TDU2SaveGame.Build bisher).
        /// </summary>
        public static void Normalize(SaveData s, CarDatabase db, string home)
        {
            if (s == null || db == null) return;
            s.garage ??= new List<GarageSlotEntry>();
            var houses = Houses(db);
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
            foreach (var h in Houses(db)) if (Same(h.SpotName, house)) n = h.CarSlots;
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
