using Paradise.Core.Net;
using Paradise.Core.Online.Net;

namespace Paradise.Game.Online
{
    /// <summary>
    /// Store-Pakete senden (Kennung zuerst: <c>new Store(magic)</c>). Läuft über die bestehende UDP-Verbindung (Multiplayer):
    /// [13][u16 Spieler][u32 Schlüssel][u8 Ziel][u16 Zielspieler][Store]. Der Server ruft für ToServer seine [PacketHandler] auf und
    /// leitet ToLobby/ToPlayer als [14][u16 Absender][Store] weiter; beim Empfänger landet es in PacketRouter.Handle.
    /// UDP: Pakete können verloren gehen (wichtige Daten doppelt senden oder bestätigen lassen); höchstens 1200 Byte.
    /// </summary>
    internal static class SendTo
    {
        /// <summary>An alle anderen Spieler der eigenen Lobby (über den Server).</summary>
        public static bool SendtoLobby(Store store) => Multiplayer.SendStore(store, UdpPacketId.ToLobby);

        /// <summary>Nur an den Server ([PacketHandler] im Server).</summary>
        public static bool SendtoServer(Store store) => Multiplayer.SendStore(store, UdpPacketId.ToServer);

        /// <summary>An den Server und an die Lobby in einem Datagramm.</summary>
        public static bool SendtoServerAndLobby(Store store) => Multiplayer.SendStore(store, UdpPacketId.ToServer | UdpPacketId.ToLobby);

        /// <summary>An einen bestimmten Spieler (Spielernummer), über den Server.</summary>
        public static bool SendtoPlayer(Store store, int playerId) => Multiplayer.SendStore(store, UdpPacketId.ToPlayer, playerId);
    }
}
