#nullable disable
// Schmutzstand je Auto im Spielstand (Paradise Engine, Offline-Spielstand; CarDirt).
namespace Paradise.Core.Online
{
    [Serializable]
    public sealed class DirtEntry
    {
        public string car = "";
        public float level;
        public float scratches;
    }
}
