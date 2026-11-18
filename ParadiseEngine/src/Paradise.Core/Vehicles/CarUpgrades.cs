using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;
using Paradise.Core.Online;

namespace Paradise.Core.Vehicles
{
    /// <summary>Tuningstufen eines Autos wie im Spiel (Car Upgrades: je 1 Byte Beschleunigung, Höchstgeschwindigkeit, Bremsen; 0..4).</summary>
    public struct UpgradeLevels
    {
        public byte Acceleration, TopSpeed, Braking;

        /// <summary>Stufe einer Kategorie (<see cref="CarUpgrades.Acceleration"/> …); andere Kategorien 0.</summary>
        public int Get(int type) => type switch { CarUpgrades.Acceleration => Acceleration, CarUpgrades.TopSpeed => TopSpeed, CarUpgrades.Braking => Braking, _ => 0 };

        /// <summary>Stufe setzen (auf 4 begrenzt, wie das Spiel).</summary>
        public void Set(int type, int level)
        {
            byte v = (byte)System.Math.Clamp(level, 0, CarUpgrades.MaxLevel);
            if (type == CarUpgrades.Acceleration) Acceleration = v;
            else if (type == CarUpgrades.TopSpeed) TopSpeed = v;
            else if (type == CarUpgrades.Braking) Braking = v;
        }

        /// <summary>Gesamtstufe = Durchschnitt der drei Kategorien (ganzzahlig, wie "Car Upgrade GlobalLevel").</summary>
        public int Global => (Acceleration + TopSpeed + Braking) / 3;

        /// <summary>Mindestens eine Kategorie aufgerüstet.</summary>
        public bool Any => Acceleration + TopSpeed + Braking > 0;

        /// <summary>Als int (Bitshifting wie die Byte-Folge im Spiel): Beschleunigung | Höchstgeschw. &lt;&lt; 8 | Bremsen &lt;&lt; 16.</summary>
        public int Pack() => Acceleration | (TopSpeed << 8) | (Braking << 16);

        public static UpgradeLevels Unpack(int v) => new UpgradeLevels
        {
            Acceleration = (byte)System.Math.Clamp(v & 0xFF, 0, CarUpgrades.MaxLevel),
            TopSpeed = (byte)System.Math.Clamp((v >> 8) & 0xFF, 0, CarUpgrades.MaxLevel),
            Braking = (byte)System.Math.Clamp((v >> 16) & 0xFF, 0, CarUpgrades.MaxLevel),
        };

        public override string ToString() => $"Beschl. {Acceleration}, Höchstgeschw. {TopSpeed}, Bremsen {Braking} (gesamt {Global})";
    }

    /// <summary>
    /// Tuning wie in der Original-exe (TestDrive2: Car Upgrades, Tuner-Menü GMCarShop/"after_mkt"):
    ///   Kategorien (UpgType): acceleration 0, top_speed 1, braking 2, rims 4 (handling: kein Kit, keine Stufen).
    ///   Stufen 1..4 je Kategorie; Freischaltung: Stufe n verlangt Entdeckungslevel des Gebiets (CruisingAreaLevel) &gt;= n.
    ///   Preis Stufe n = Prozent[Kategorie][n-1] x Autopreis (DBCarPhysicsData._price) / 1000 x 10
    ///     (Prozent: Beschl. 5/10/15/20, Höchstgeschw. 5/10/15/25, Bremsen 3/7/10/15); liegt die Stufe direkt über der
    ///     aktuellen, zahlt man nur die Differenz zum Preis der aktuellen Stufe.
    ///   Wirkung (Fahrzeugaufbau): Faktor je Kategorie = DBEnvironnement.&lt;Accel|TopSpeed|Braking&gt;IncreaseByUpgradeLevel x 0,01 x Stufe
    ///     (Datenbank: je 2.0 -&gt; 2 % je Stufe). T = 1 + Höchstgeschw.-Faktor, A = 1 + Beschl.-Faktor + (T - 1) / 5,5, B = 1 + Bremsfaktor:
    ///     Motormoment x A, Achsübersetzung / T, Höchstgeschwindigkeit x T, Bremsen x B.
    /// Gespeichert je Auto lokal im Spielstand (SaveData.carUpgrades, gepackter int wie oben).
    /// </summary>
    public static class CarUpgrades
    {
        public const int Acceleration = 0, TopSpeed = 1, Braking = 2, Handling = 3, Rims = 4;
        public const int MaxLevel = 4;

        // exe 0x14e3374: [Kategorie * 4 + Stufe - 1]
        static readonly int[,] PricePercent = { { 5, 10, 15, 20 }, { 5, 10, 15, 25 }, { 3, 7, 10, 15 } };

        static float _accelPerLevel = 2f, _topPerLevel = 2f, _brakePerLevel = 2f;
        static bool _init;

        /// <summary>Geändert (Auto-Datei): z.B. Spielerauto neu aufbauen.</summary>
        public static event Action<string>? Changed;

        /// <summary>Werte aus der Datenbank (DBEnvironnement) – einmal beim Laden der Datenbank.</summary>
        public static void Init(XmbNode? root)
        {
            if (_init || root == null) return;
            _init = true;
            var env = root.Get("DBEnvironnement");
            if (env == null) return;
            foreach (var r in env.Items)
            {
                string n = r.S("_variable", "") ?? "";
                float v = r.F("_value", 2f);
                if (n == "AccelIncreaseByUpgradeLevel") _accelPerLevel = v;
                else if (n == "TopSpeedIncreaseByUpgradeLevel") _topPerLevel = v;
                else if (n == "BrakingIncreaseByUpgradeLevel") _brakePerLevel = v;
            }
        }

        /// <summary>Kategorie aus dem Film (/:UpgType, /:BtValid): -1 = keine Stufen (z.B. handling).</summary>
        public static int TypeOf(string? s) => s switch { "acceleration" => Acceleration, "top_speed" => TopSpeed, "braking" => Braking, "rims" => Rims, _ => -1 };

        /// <summary>Zuwachs einer Kategorie bei einer Stufe (0,02 je Stufe).</summary>
        public static float Factor(int type, int level)
        {
            float per = type switch { Acceleration => _accelPerLevel, TopSpeed => _topPerLevel, Braking => _brakePerLevel, _ => 0f };
            return per * 0.01f * level;
        }

        /// <summary>Multiplikatoren für den Fahrzeugaufbau (exe: Motor, Getriebe, Höchstgeschwindigkeit, Bremsen).</summary>
        public static void Multipliers(UpgradeLevels l, out float torque, out float topSpeed, out float braking)
        {
            topSpeed = 1f + Factor(TopSpeed, l.TopSpeed);
            torque = 1f + Factor(Acceleration, l.Acceleration) + (topSpeed - 1f) / 5.5f;
            braking = 1f + Factor(Braking, l.Braking);
        }

        /// <summary>Label der Gesamtstufe in den Filmen (car_datas upg: level0 … level6).</summary>
        public static string LevelLabel(int level) => level >= 1 && level <= 6 ? "level" + level : "level0";

        /// <summary>Preis einer Stufe (exe 0xa2f5d0): Prozent x Autopreis / 1000 x 10.</summary>
        public static int LevelPrice(int type, int level, int carPrice)
        {
            if (level <= 0 || type < 0 || type > 2 || carPrice <= 0) return 0;
            uint p = (uint)PricePercent[type, System.Math.Min(level, MaxLevel) - 1] * (uint)carPrice;
            return (int)(p / 1000u * 10u);
        }

        /// <summary>Kaufpreis (exe 0xa2f630): direkt nächste Stufe = Differenz zur aktuellen, sonst voller Stufenpreis.</summary>
        public static int BuyPrice(int type, int level, UpgradeLevels cur, int carPrice)
        {
            int price = LevelPrice(type, level, carPrice);
            int c = cur.Get(type);
            if (c == level - 1) price -= LevelPrice(type, c, carPrice);
            return price;
        }

        /// <summary>Preis eines Autos aus der Datenbank (_price).</summary>
        public static int CarPrice(CarPhysics? car) => car?.Raw != null ? car.Raw.I("_price") : 0;

        // ------------------------------------------------------------------ Speichern (lokal, Spielstand)
        static readonly Dictionary<string, UpgradeLevels> _session = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Stufen eines Autos (Auto-Datei, z.B. "delta_integrale_evo_2").</summary>
        public static UpgradeLevels Get(string? car)
        {
            if (string.IsNullOrEmpty(car)) return default;
            var list = TDU2Session.Save?.carUpgrades;
            if (list != null)
                foreach (var e in list)
                    if (e != null && string.Equals(e.car, car, StringComparison.OrdinalIgnoreCase)) return UpgradeLevels.Unpack(e.upgrades);
            return _session.TryGetValue(car, out var l) ? l : default;
        }

        /// <summary>Stufen setzen und speichern (Spielstand; ohne Spielstand nur für die Sitzung).</summary>
        public static void Set(string? car, UpgradeLevels levels)
        {
            if (string.IsNullOrEmpty(car)) return;
            var sv = TDU2Session.Save;
            if (sv != null)
            {
                var list = sv.carUpgrades ??= new List<UpgradeEntry>();
                var e = list.Find(x => x != null && string.Equals(x.car, car, StringComparison.OrdinalIgnoreCase));
                if (e == null) list.Add(e = new UpgradeEntry { car = car });
                e.upgrades = levels.Pack();
            }
            else _session[car] = levels;
            Changed?.Invoke(car);
        }

        /// <summary>Wirkung auf ein Fahrzeug (Physik) übernehmen.</summary>
        public static void ApplyTo(Vehicle? veh)
        {
            if (veh?.Car == null) return;
            Multipliers(Get(veh.Car.FileName), out veh.UpgradeTorque, out veh.UpgradeTopSpeed, out veh.UpgradeBraking);
        }
    }
}