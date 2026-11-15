#nullable disable
namespace Paradise.Core.Online
{
    /// <summary>Stellplatz der Garagen (Original-DATA: Garage[] mit House = Haus-Hashcode, IndexInGarage): Haus (Spot-Name aus DBHouse,
    /// z.B. "EPH_L0C_1254I"), Platz in der Garage dieses Hauses (0-basiert) und das Auto (Dateiname wie in ownedCars).</summary>
    [Serializable]
    public sealed class GarageSlotEntry
    {
        public string house = "";
        public int slot;
        public string car = "";
    }
}
