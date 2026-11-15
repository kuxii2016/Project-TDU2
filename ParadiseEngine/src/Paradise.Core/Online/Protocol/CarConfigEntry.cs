#nullable disable
// Portiert aus dem Unity-Projekt (Runtime/Online/Protocol/CarConfigEntry.cs).
namespace Paradise.Core.Online
{
    /// <summary>Ausstattung eines Autos: Lack = Index in DBCarsAndColors des Autos, Felge = DBRims._hashcode, Innenraum = DBInterior._hashcode (0 = Standard).</summary>
    [Serializable]
    public sealed class CarConfigEntry
    {
        public string car = "";
        public int color;
        public int rim;
        public int interior;
    }
}
