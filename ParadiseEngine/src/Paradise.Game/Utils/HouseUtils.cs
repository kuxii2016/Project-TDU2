using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.Savegame;
using Paradise.Core.Vehicles;

namespace Paradise.Game.Utils;

/// <summary>
/// Helper functions for houses, garages and house customization (My House / furniture / materials).
/// Operates on the current session save by default; most methods also accept an explicit <see cref="SaveData"/>.
/// </summary>
public static class HouseUtils
{
    static SaveData? Save(SaveData? save) => save ?? TDU2Session.Save;

    /// <summary>Returns the layout entry for a house spot, or null if none exists.</summary>
    public static HouseLayout? GetLayout(string spot, SaveData? save = null)
        => GetLayout(Save(save), spot);

    /// <summary>Returns the layout entry for a house spot, or null if none exists.</summary>
    public static HouseLayout? GetLayout(SaveData? save, string spot)
    {
        if (save?.houses == null || string.IsNullOrEmpty(spot)) return null;
        foreach (var h in save.houses)
            if (string.Equals(h.spot, spot, StringComparison.OrdinalIgnoreCase)) return h;
        return null;
    }

    /// <summary>Returns the layout for a house spot, creating and adding it if missing.</summary>
    public static HouseLayout? GetOrCreateLayout(string spot, SaveData? save = null)
        => GetOrCreateLayout(Save(save), spot);

    /// <summary>Returns the layout for a house spot, creating and adding it if missing.</summary>
    public static HouseLayout? GetOrCreateLayout(SaveData? save, string spot)
    {
        if (save == null || string.IsNullOrEmpty(spot)) return null;
        save.houses ??= new List<HouseLayout>();
        var layout = GetLayout(save, spot);
        if (layout == null)
        {
            layout = new HouseLayout { spot = spot };
            save.houses.Add(layout);
        }
        layout.furn ??= new List<HouseEntry>();
        layout.mats ??= new List<HouseEntry>();
        return layout;
    }

    /// <summary>Removes the saved layout for a house spot. Returns true if removed.</summary>
    public static bool RemoveLayout(string spot, SaveData? save = null)
    {
        save = Save(save);
        if (save?.houses == null || string.IsNullOrEmpty(spot)) return false;
        return save.houses.RemoveAll(h => string.Equals(h.spot, spot, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    // --------------------------------------------------------------------- Furniture (entity -> bank)

    /// <summary>Gets the furniture bank placed on an entity in a house, or null if none.</summary>
    public static string? GetFurniture(string spot, string entity, SaveData? save = null)
        => GetEntryValue(GetLayout(spot, save)?.furn, entity);

    /// <summary>Sets (or adds) the furniture bank for an entity in a house. Returns false on invalid input.</summary>
    public static bool SetFurniture(string spot, string entity, string bank, SaveData? save = null)
    {
        var layout = GetOrCreateLayout(spot, save);
        if (layout == null || string.IsNullOrEmpty(entity)) return false;
        SetEntryValue(layout.furn, entity, bank ?? "");
        return true;
    }

    /// <summary>Removes the furniture placed on an entity in a house. Returns true if removed.</summary>
    public static bool RemoveFurniture(string spot, string entity, SaveData? save = null)
        => RemoveEntry(GetLayout(spot, save)?.furn, entity);

    /// <summary>Number of furniture objects placed in a house layout.</summary>
    public static int GetPlacedFurnitureCount(string spot, SaveData? save = null)
        => GetLayout(spot, save)?.furn?.Count ?? 0;

    // --------------------------------------------------------------------- Materials (surface -> material)

    /// <summary>Gets the material applied to a surface in a house (e.g. "Floor1", "Wall2"), or null if none.</summary>
    public static string? GetMaterial(string spot, string surface, SaveData? save = null)
        => GetEntryValue(GetLayout(spot, save)?.mats, surface);

    /// <summary>Sets (or adds) the material for a surface in a house. Returns false on invalid input.</summary>
    public static bool SetMaterial(string spot, string surface, string material, SaveData? save = null)
    {
        var layout = GetOrCreateLayout(spot, save);
        if (layout == null || string.IsNullOrEmpty(surface)) return false;
        SetEntryValue(layout.mats, surface, material ?? "");
        return true;
    }

    /// <summary>Removes the material from a surface in a house. Returns true if removed.</summary>
    public static bool RemoveMaterial(string spot, string surface, SaveData? save = null)
        => RemoveEntry(GetLayout(spot, save)?.mats, surface);

    /// <summary>Number of materials applied in a house layout.</summary>
    public static int GetAppliedMaterialCount(string spot, SaveData? save = null)
        => GetLayout(spot, save)?.mats?.Count ?? 0;

    // --------------------------------------------------------------------- Owned furniture / materials (inventory)

    /// <summary>Returns how many furniture items the player owns (DBFurniture hashcodes).</summary>
    public static int GetOwnedFurnitureCount(SaveData? save = null) => Save(save)?.ownedFurniture?.Count ?? 0;

    /// <summary>Returns how many materials the player owns (DBMaterial hashcodes).</summary>
    public static int GetOwnedMaterialCount(SaveData? save = null) => Save(save)?.ownedMaterials?.Count ?? 0;

    /// <summary>True if the player owns the given furniture hashcode.</summary>
    public static bool OwnsFurniture(int hash, SaveData? save = null) => Save(save)?.ownedFurniture?.Contains(hash) == true;

    /// <summary>True if the player owns the given material hashcode.</summary>
    public static bool OwnsMaterial(int hash, SaveData? save = null) => Save(save)?.ownedMaterials?.Contains(hash) == true;

    /// <summary>Adds a furniture hashcode to the owned inventory if not already present. Returns true if added.</summary>
    public static bool AddOwnedFurniture(int hash, SaveData? save = null)
        => AddUnique(Save(save)?.ownedFurniture ?? EnsureOwnedFurniture(Save(save)), hash);

    /// <summary>Adds a material hashcode to the owned inventory if not already present. Returns true if added.</summary>
    public static bool AddOwnedMaterial(int hash, SaveData? save = null)
        => AddUnique(Save(save)?.ownedMaterials ?? EnsureOwnedMaterials(Save(save)), hash);

    /// <summary>Removes a furniture hashcode from the owned inventory. Returns true if removed.</summary>
    public static bool RemoveOwnedFurniture(int hash, SaveData? save = null) => Save(save)?.ownedFurniture?.Remove(hash) == true;

    /// <summary>Removes a material hashcode from the owned inventory. Returns true if removed.</summary>
    public static bool RemoveOwnedMaterial(int hash, SaveData? save = null) => Save(save)?.ownedMaterials?.Remove(hash) == true;

    static List<int>? EnsureOwnedFurniture(SaveData? save)
    {
        if (save == null) return null;
        return save.ownedFurniture ??= new List<int>();
    }

    static List<int>? EnsureOwnedMaterials(SaveData? save)
    {
        if (save == null) return null;
        return save.ownedMaterials ??= new List<int>();
    }

    static bool AddUnique(List<int>? list, int value)
    {
        if (list == null || list.Contains(value)) return false;
        list.Add(value);
        return true;
    }

    // --------------------------------------------------------------------- Houses / garages (database & slots)

    /// <summary>Returns the database house info for a spot code (e.g. "EPH_*"), or null if not found.</summary>
    public static HouseInfo? GetHouseInfo(CarDatabase? db, string? spotCode)
        => db == null || string.IsNullOrEmpty(spotCode) ? null : db.HouseFor(spotCode);

    /// <summary>Returns the database house info for a specific spot (best match by area/sector and island), or null.</summary>
    public static HouseInfo? GetHouseInfo(CarDatabase? db, Spot? spot, string islandName)
        => db == null || spot == null ? null : db.HouseForSpot(spot, islandName);

    /// <summary>Number of car slots/garage capacity for a house (from database). 0 if unknown.</summary>
    public static int GetCarSlots(CarDatabase? db, string? spotCode) => GetHouseInfo(db, spotCode)?.CarSlots ?? 0;

    /// <summary>All player houses with garages known in the database (EPH_* with car slots).</summary>
    public static List<HouseInfo> GetOwnedHouses(CarDatabase? db) => db != null ? TDU2Garage.Houses(db) : new List<HouseInfo>();

    /// <summary>Number of cars currently placed in the given house's garage (savegame slots).</summary>
    public static int GetGarageUsedSlots(string houseSpot, SaveData? save = null)
        => TDU2Garage.Used(Save(save), houseSpot);

    /// <summary>Car file name parked at a specific garage slot, or null if free/unknown.</summary>
    public static string? GetCarAtSlot(string houseSpot, int slot, SaveData? save = null)
        => TDU2Garage.CarAt(Save(save), houseSpot, slot);

    /// <summary>Finds the garage slot entry (house + slot) for a car, or null if not placed.</summary>
    public static GarageSlotEntry? GetGarageSlotOfCar(string carFileName, SaveData? save = null)
        => TDU2Garage.SlotOf(Save(save), carFileName);

    /// <summary>True if the car is parked in the given house's garage.</summary>
    public static bool IsCarInHouse(string carFileName, string houseSpot, SaveData? save = null)
    {
        var e = GetGarageSlotOfCar(carFileName, save);
        return e != null && string.Equals(e.house, houseSpot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Number of houses that have a saved layout/customization.</summary>
    public static int GetCustomizedHouseCount(SaveData? save = null) => Save(save)?.houses?.Count ?? 0;

    // --------------------------------------------------------------------- HouseEntry helpers

    static string? GetEntryValue(List<HouseEntry>? list, string key)
    {
        if (list == null || string.IsNullOrEmpty(key)) return null;
        foreach (var e in list)
            if (string.Equals(e.k, key, StringComparison.OrdinalIgnoreCase)) return e.v;
        return null;
    }

    static void SetEntryValue(List<HouseEntry> list, string key, string value)
    {
        foreach (var e in list)
        {
            if (string.Equals(e.k, key, StringComparison.OrdinalIgnoreCase)) { e.v = value; return; }
        }
        list.Add(new HouseEntry { k = key, v = value });
    }

    static bool RemoveEntry(List<HouseEntry>? list, string key)
    {
        if (list == null || string.IsNullOrEmpty(key)) return false;
        return list.RemoveAll(e => string.Equals(e.k, key, StringComparison.OrdinalIgnoreCase)) > 0;
    }
}