#nullable disable
// Portiert aus dem Unity-Projekt (Runtime/Online/Protocol/LobbyEntry.cs).
namespace Paradise.Core.Online
{
    /// <summary>Lobby (op "lobbies"). Feldnamen = Server Net/Protocol/LobbyEntry.cs.</summary>
    [Serializable]
    public sealed class LobbyEntry
    {
        public int id;
        public int island;   // 1 Ibiza, 2 Hawaii, 0 unbekannt
        public int count;
        public int max;      // 0 = unbegrenzt
    }
}
