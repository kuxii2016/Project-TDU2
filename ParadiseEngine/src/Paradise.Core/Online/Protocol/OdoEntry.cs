#nullable disable
// Portiert aus dem Unity-Projekt (Runtime/Online/Protocol/OdoEntry.cs).
namespace Paradise.Core.Online
{
    // ------------------------------------------------------------------ Datenklassen (Feldnamen = JSON-Namen, identisch im Server TDU2Server/Program.cs)
    [Serializable]
    public sealed class OdoEntry
    {
        public int carId;
        public double meters;
    }
}
