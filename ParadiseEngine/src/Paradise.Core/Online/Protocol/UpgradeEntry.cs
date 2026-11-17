#nullable disable
// Tuningstufen je Auto im Spielstand (Paradise Engine, Offline-Spielstand; CarUpgrades).
namespace Paradise.Core.Online
{
    [Serializable]
    public sealed class UpgradeEntry
    {
        public string car = "";
        /// <summary>Gepackt: Beschleunigung | Höchstgeschwindigkeit &lt;&lt; 8 | Bremsen &lt;&lt; 16 (je 0..4).</summary>
        public int upgrades;
    }
}
