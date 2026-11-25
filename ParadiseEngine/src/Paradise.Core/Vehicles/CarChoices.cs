using Paradise.Core.Online;

namespace Paradise.Core.Vehicles
{
    /// <summary>Ausstattung eines Autos (Autohändler): Lack = Index in DBCarsAndColors des Autos, Felge = DBRims._hashcode, Innenraum = DBInterior._hashcode.</summary>
    public struct CarChoice
    {
        public int Color, Rim, Interior;
        public CarChoice(int color, int rim, int interior) { Color = color; Rim = rim; Interior = interior; }
        public override string ToString() => $"Paint {Color}, Wheel {Rim}, Interior {Interior}";
    }

    /// <summary>
    /// Gekaufte Ausstattung je Auto – Port von Unity TDU2CarChoices. Gespeichert im Spielstand (SaveData.carConfigs, auch im
    /// Original-Spielstand PLAYERSAVE über TDU2SaveGame); ohne Spielstand nur für diese Sitzung (die Engine hat keine PlayerPrefs).
    /// Eine vorübergehende Ausstattung (Testfahrt beim Händler) geht vor und wird nicht gespeichert.
    /// Nur im Hauptthread benutzen (die Ladeaufgaben bekommen die Werte als Kopie).
    /// </summary>
    public static class CarChoices
    {
        static readonly Dictionary<string, CarChoice> _temp = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, CarChoice> _session = new(StringComparer.OrdinalIgnoreCase);

        public static bool TryGet(string? car, out CarChoice choice)
        {
            choice = default;
            if (string.IsNullOrEmpty(car)) return false;
            if (_temp.TryGetValue(car, out choice)) return true;
            var save = TDU2Session.Save;
            if (save != null)
            {
                var e = save.carConfigs?.Find(c => string.Equals(c.car, car, StringComparison.OrdinalIgnoreCase));
                if (e == null) return false;
                choice = new CarChoice(e.color, e.rim, e.interior);
                return true;
            }
            return _session.TryGetValue(car, out choice);
        }

        /// <summary>Ausstattung dauerhaft speichern (Kauf).</summary>
        public static void Set(string? car, CarChoice choice)
        {
            if (string.IsNullOrEmpty(car)) return;
            _temp.Remove(car);
            var save = TDU2Session.Save;
            if (save != null)
            {
                var list = save.carConfigs ??= new List<CarConfigEntry>();
                var e = list.Find(c => string.Equals(c.car, car, StringComparison.OrdinalIgnoreCase));
                if (e == null) list.Add(e = new CarConfigEntry { car = car });
                e.color = choice.Color; e.rim = choice.Rim; e.interior = choice.Interior;
                return;
            }
            _session[car] = choice;
        }

        /// <summary>Vorübergehende Ausstattung (Testfahrt), wird nicht gespeichert.</summary>
        public static void SetTemporary(string? car, CarChoice choice) { if (!string.IsNullOrEmpty(car)) _temp[car] = choice; }

        public static void ClearTemporary(string? car) { if (!string.IsNullOrEmpty(car)) _temp.Remove(car); }
    }
}
