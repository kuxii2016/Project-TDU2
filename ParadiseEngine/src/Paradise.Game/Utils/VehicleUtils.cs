using Paradise.Core.Online;
using Paradise.Core.Savegame;
using Paradise.Core.Vehicles;

namespace Paradise.Game.Utils;

/// <summary>
/// Helper functions for vehicles and vehicle-related savegame data (owned cars, configs, odometers,
/// liveries, dirt, upgrades and garage placement). Operates on the current session save by default;
/// most methods also accept an explicit <see cref="SaveData"/>.
/// </summary>
public static class VehicleUtils
{
    static SaveData? Save(SaveData? save) => save ?? TDU2Session.Save;

    static bool IsSessionSave(SaveData? save) => save == null || ReferenceEquals(save, TDU2Session.Save);

    // --------------------------------------------------------------------- Ownership / current car

    /// <summary>Owned car file names in the save (e.g. "delta_integrale_evo_2").</summary>
    public static IReadOnlyList<string> GetOwnedCars(SaveData? save = null)
        => (IReadOnlyList<string>?)Save(save)?.ownedCars ?? Array.Empty<string>();

    /// <summary>Number of owned cars.</summary>
    public static int GetOwnedCarCount(SaveData? save = null) => Save(save)?.ownedCars?.Count ?? 0;

    /// <summary>True if the player owns the given car (file name).</summary>
    public static bool OwnsCar(string? carFile, SaveData? save = null)
    {
        var cars = Save(save)?.ownedCars;
        if (cars == null || string.IsNullOrEmpty(carFile)) return false;
        foreach (var c in cars)
            if (string.Equals(c, carFile, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Adds a car to the owned list if not already present. Returns true if added.</summary>
    public static bool AddOwnedCar(string? carFile, SaveData? save = null)
    {
        if (string.IsNullOrEmpty(carFile)) return false;
        if (IsSessionSave(save) && TDU2Session.Save != null)
        {
            if (OwnsCar(carFile, TDU2Session.Save)) return false;
            TDU2Session.AddOwned(carFile);
            return true;
        }
        var s = Save(save);
        if (s == null) return false;
        s.ownedCars ??= new List<string>();
        if (OwnsCar(carFile, s)) return false;
        s.ownedCars.Add(carFile);
        return true;
    }

    /// <summary>
    /// Removes a car from the owned list. Also removes per-car save data keyed by file name
    /// (config, livery, dirt, upgrades, garage slot). Odometer entries are keyed by car id and are left untouched.
    /// Returns true if the car was owned and removed.
    /// </summary>
    public static bool RemoveOwnedCar(string? carFile, SaveData? save = null)
    {
        var s = Save(save);
        if (s?.ownedCars == null || string.IsNullOrEmpty(carFile)) return false;
        bool removed = s.ownedCars.RemoveAll(c => string.Equals(c, carFile, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed) return false;
        if (string.Equals(s.currentCar, carFile, StringComparison.OrdinalIgnoreCase))
            s.currentCar = s.ownedCars.Count > 0 ? s.ownedCars[0] : "";
        s.carConfigs?.RemoveAll(e => e != null && string.Equals(e.car, carFile, StringComparison.OrdinalIgnoreCase));
        s.liveries?.RemoveAll(e => e != null && string.Equals(e.car, carFile, StringComparison.OrdinalIgnoreCase));
        s.carDirt?.RemoveAll(e => e != null && string.Equals(e.car, carFile, StringComparison.OrdinalIgnoreCase));
        s.carUpgrades?.RemoveAll(e => e != null && string.Equals(e.car, carFile, StringComparison.OrdinalIgnoreCase));
        s.garage?.RemoveAll(e => e != null && string.Equals(e.car, carFile, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    /// <summary>File name of the currently selected car, or null/empty if none.</summary>
    public static string? GetCurrentCar(SaveData? save = null) => Save(save)?.currentCar;

    /// <summary>Sets the currently selected car file name. Returns false if there is no save.</summary>
    public static bool SetCurrentCar(string? carFile, SaveData? save = null)
    {
        if (string.IsNullOrEmpty(carFile)) return false;
        if (IsSessionSave(save) && TDU2Session.Save != null)
        {
            TDU2Session.SetCurrentCar(carFile);
            return true;
        }
        var s = Save(save);
        if (s == null) return false;
        s.currentCar = carFile;
        return true;
    }

    /// <summary>True if the given car is the currently selected car.</summary>
    public static bool IsCurrentCar(string? carFile, SaveData? save = null)
        => !string.IsNullOrEmpty(carFile) && string.Equals(GetCurrentCar(save), carFile, StringComparison.OrdinalIgnoreCase);

    // --------------------------------------------------------------------- Database lookups

    /// <summary>Finds a car in the database by file name (e.g. "delta_integrale_evo_2"), or null.</summary>
    public static CarPhysics? GetCarByFile(CarDatabase? db, string? fileName)
    {
        if (db == null || string.IsNullOrEmpty(fileName)) return null;
        foreach (var c in db.Cars)
            if (string.Equals(c.FileName, fileName, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    /// <summary>Finds a car in the database by id, or null.</summary>
    public static CarPhysics? GetCarById(CarDatabase? db, int id)
        => db != null && db.ById.TryGetValue(id, out var c) ? c : null;

    /// <summary>Base price of a car from the database (_price), or 0 if unknown.</summary>
    public static int GetCarPrice(CarPhysics? car) => car?.Raw?.I("_price") ?? 0;

    /// <summary>Base price of a car looked up by file name, or 0 if unknown.</summary>
    public static int GetCarPrice(CarDatabase? db, string? fileName) => GetCarPrice(GetCarByFile(db, fileName));

    /// <summary>Display model name of a car, or the file name as fallback.</summary>
    public static string GetCarDisplayName(CarPhysics? car)
    {
        if (car == null) return "";
        return !string.IsNullOrEmpty(car.ModelName) ? car.ModelName : (car.FileName ?? "");
    }

    // --------------------------------------------------------------------- Configuration (color / rim / interior)

    /// <summary>Tries to get the saved/persistent configuration choice for a car.</summary>
    public static bool TryGetCarChoice(string? carFile, out CarChoice choice, SaveData? save = null)
    {
        choice = default;
        var s = Save(save);
        if (s == null || string.IsNullOrEmpty(carFile)) return false;
        if (IsSessionSave(save))
            return CarChoices.TryGet(carFile, out choice);
        var e = FindConfig(s, carFile);
        if (e == null) return false;
        choice = new CarChoice(e.color, e.rim, e.interior);
        return true;
    }

    /// <summary>Gets the saved/persistent configuration choice for a car (default if none).</summary>
    public static CarChoice GetCarChoice(string? carFile, SaveData? save = null)
        => TryGetCarChoice(carFile, out var c, save) ? c : default;

    /// <summary>Sets the persistent configuration for a car (purchase/selection).</summary>
    public static void SetCarChoice(string? carFile, CarChoice choice, SaveData? save = null)
    {
        if (string.IsNullOrEmpty(carFile)) return;
        if (IsSessionSave(save) && TDU2Session.Save != null)
        {
            CarChoices.Set(carFile, choice);
            return;
        }
        var s = Save(save);
        if (s == null) return;
        s.carConfigs ??= new List<CarConfigEntry>();
        var e = FindConfig(s, carFile);
        if (e == null) s.carConfigs.Add(e = new CarConfigEntry { car = carFile });
        e.color = choice.Color; e.rim = choice.Rim; e.interior = choice.Interior;
    }

    /// <summary>Returns the raw saved config entry for a car, or null.</summary>
    public static CarConfigEntry? GetCarConfig(string? carFile, SaveData? save = null)
        => FindConfig(Save(save), carFile);

    /// <summary>Removes the saved configuration entry for a car. Returns true if removed.</summary>
    public static bool RemoveCarConfig(string? carFile, SaveData? save = null)
    {
        var s = Save(save);
        if (s?.carConfigs == null || string.IsNullOrEmpty(carFile)) return false;
        return s.carConfigs.RemoveAll(e => e != null && string.Equals(e.car, carFile, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    static CarConfigEntry? FindConfig(SaveData? s, string? carFile)
    {
        if (s?.carConfigs == null || string.IsNullOrEmpty(carFile)) return null;
        foreach (var e in s.carConfigs)
            if (e != null && string.Equals(e.car, carFile, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    // --------------------------------------------------------------------- Livery / stickers

    /// <summary>Gets the saved livery data for a car (Base64, as stored), or null if none.</summary>
    public static string? GetLivery(string? carFile, SaveData? save = null)
    {
        var s = Save(save);
        if (s?.liveries == null || string.IsNullOrEmpty(carFile)) return null;
        foreach (var e in s.liveries)
            if (e != null && string.Equals(e.car, carFile, StringComparison.OrdinalIgnoreCase)) return e.data;
        return null;
    }

    /// <summary>Sets or removes the livery data for a car. Pass null/empty data to remove. Returns true on change.</summary>
    public static bool SetLivery(string? carFile, string? data, SaveData? save = null)
    {
        var s = Save(save);
        if (s == null || string.IsNullOrEmpty(carFile)) return false;
        s.liveries ??= new List<LiveryEntry>();
        var existing = s.liveries.Find(e => e != null && string.Equals(e.car, carFile, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(data))
            return existing != null && s.liveries.Remove(existing);
        if (existing == null) s.liveries.Add(new LiveryEntry { car = carFile, data = data });
        else existing.data = data;
        return true;
    }

    /// <summary>Removes the saved livery for a car. Returns true if removed.</summary>
    public static bool RemoveLivery(string? carFile, SaveData? save = null) => SetLivery(carFile, null, save);

    /// <summary>True if the car has a saved livery.</summary>
    public static bool HasLivery(string? carFile, SaveData? save = null) => !string.IsNullOrEmpty(GetLivery(carFile, save));

    // --------------------------------------------------------------------- Dirt / scratches

    /// <summary>Gets the saved dirt level and scratches for a car (0..1 each).</summary>
    public static (float Level, float Scratches) GetDirt(string? carFile, SaveData? save = null)
    {
        var s = Save(save);
        if (s?.carDirt == null || string.IsNullOrEmpty(carFile)) return (0f, 0f);
        foreach (var e in s.carDirt)
            if (e != null && string.Equals(e.car, carFile, StringComparison.OrdinalIgnoreCase))
                return (Clamp01(e.level), Clamp01(e.scratches));
        return (0f, 0f);
    }

    /// <summary>Sets the saved dirt level and scratches for a car (clamped to 0..1).</summary>
    public static void SetDirt(string? carFile, float level, float scratches = 0f, SaveData? save = null)
    {
        var s = Save(save);
        if (s == null || string.IsNullOrEmpty(carFile)) return;
        s.carDirt ??= new List<DirtEntry>();
        level = Clamp01(level); scratches = Clamp01(scratches);
        foreach (var e in s.carDirt)
            if (e != null && string.Equals(e.car, carFile, StringComparison.OrdinalIgnoreCase))
            { e.level = level; e.scratches = scratches; return; }
        s.carDirt.Add(new DirtEntry { car = carFile, level = level, scratches = scratches });
    }

    /// <summary>True if the car is considered dirty (level above threshold).</summary>
    public static bool IsDirty(string? carFile, float threshold = 0.001f, SaveData? save = null)
        => GetDirt(carFile, save).Level > threshold;

    // --------------------------------------------------------------------- Upgrades / tuning

    /// <summary>Gets the saved upgrade levels for a car.</summary>
    public static UpgradeLevels GetUpgrades(string? carFile, SaveData? save = null)
    {
        if (string.IsNullOrEmpty(carFile)) return default;
        if (IsSessionSave(save))
            return CarUpgrades.Get(carFile);
        var s = Save(save);
        if (s?.carUpgrades == null) return default;
        foreach (var e in s.carUpgrades)
            if (e != null && string.Equals(e.car, carFile, StringComparison.OrdinalIgnoreCase))
                return UpgradeLevels.Unpack(e.upgrades);
        return default;
    }

    /// <summary>Sets the upgrade levels for a car.</summary>
    public static void SetUpgrades(string? carFile, UpgradeLevels levels, SaveData? save = null)
    {
        if (string.IsNullOrEmpty(carFile)) return;
        if (IsSessionSave(save) && TDU2Session.Save != null)
        {
            CarUpgrades.Set(carFile, levels);
            return;
        }
        var s = Save(save);
        if (s == null) return;
        s.carUpgrades ??= new List<UpgradeEntry>();
        var e = s.carUpgrades.Find(x => x != null && string.Equals(x.car, carFile, StringComparison.OrdinalIgnoreCase));
        if (e == null) s.carUpgrades.Add(e = new UpgradeEntry { car = carFile });
        e.upgrades = levels.Pack();
    }

    /// <summary>Returns the buy price for a specific upgrade step on a car (0 if unavailable).</summary>
    public static int GetUpgradeBuyPrice(int upgradeType, int level, string? carFile, CarDatabase? db)
    {
        var cur = GetUpgrades(carFile);
        int carPrice = GetCarPrice(db, carFile);
        return CarUpgrades.BuyPrice(upgradeType, level, cur, carPrice);
    }

    // --------------------------------------------------------------------- Odometer

    /// <summary>Gets the odometer reading in meters for a car id.</summary>
    public static double GetOdometerMeters(int carId, SaveData? save = null)
    {
        var s = Save(save);
        if (s?.odometers == null) return 0;
        foreach (var e in s.odometers)
            if (e != null && e.carId == carId) return Math.Max(0, e.meters);
        return 0;
    }

    /// <summary>Gets the odometer reading in kilometers for a car id.</summary>
    public static double GetOdometerKilometers(int carId, SaveData? save = null) => GetOdometerMeters(carId, save) / 1000.0;

    /// <summary>Gets the odometer reading in meters for a car (by database id), or 0 if unknown.</summary>
    public static double GetOdometerMeters(CarPhysics? car, SaveData? save = null) => car != null ? GetOdometerMeters(car.Id, save) : 0;

    /// <summary>Sets the odometer reading in meters for a car id (clamped to >= 0).</summary>
    public static void SetOdometerMeters(int carId, double meters, SaveData? save = null)
    {
        var s = Save(save);
        if (s == null) return;
        s.odometers ??= new List<OdoEntry>();
        meters = Math.Max(0, meters);
        foreach (var e in s.odometers)
            if (e != null && e.carId == carId) { e.meters = meters; return; }
        s.odometers.Add(new OdoEntry { carId = carId, meters = meters });
    }

    /// <summary>Adds meters to the odometer reading of a car id.</summary>
    public static void AddOdometerMeters(int carId, double meters, SaveData? save = null)
        => SetOdometerMeters(carId, GetOdometerMeters(carId, save) + meters, save);

    /// <summary>Total driven distance in kilometers across all recorded odometers.</summary>
    public static double GetTotalOdometerKilometers(SaveData? save = null)
    {
        var s = Save(save);
        if (s?.odometers == null) return 0;
        double km = 0;
        foreach (var o in s.odometers)
            if (o != null) km += Math.Max(0, o.meters) / 1000.0;
        return km;
    }

    // --------------------------------------------------------------------- Garage placement

    /// <summary>Finds the garage slot (house + slot) for a car file name, or null if not placed.</summary>
    public static GarageSlotEntry? GetGarageSlotOfCar(string? carFile, SaveData? save = null)
        => TDU2Garage.SlotOf(Save(save), carFile);

    /// <summary>Returns the car file name parked at a house garage slot, or null if empty/unknown.</summary>
    public static string? GetCarAtGarageSlot(string? houseSpot, int slot, SaveData? save = null)
        => TDU2Garage.CarAt(Save(save), houseSpot, slot);

    /// <summary>Number of cars currently placed in a house's garage.</summary>
    public static int GetGarageUsedSlots(string? houseSpot, SaveData? save = null)
        => TDU2Garage.Used(Save(save), houseSpot);

    /// <summary>Normalizes garage slot assignments for the save (distributes unplaced cars).</summary>
    public static void NormalizeGarage(SaveData? save = null, CarDatabase? db = null, string? homeHouseSpot = null)
        => TDU2Garage.Normalize(Save(save), db ?? TDU2SaveGame.Database, homeHouseSpot);

    // --------------------------------------------------------------------- Helpers

    static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}