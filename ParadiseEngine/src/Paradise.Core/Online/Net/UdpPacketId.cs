namespace Paradise.Core.Online.Net;

/// <summary>
/// Pakettypen (Byte 0) der UDP-Synchronisation – Port von Unity TDU2.Online.Net.UdpPacketId, identisch im Server
/// (TDU2Server.Net.Udp.UdpPacketIds). Client -&gt; Server: [Typ][u16 playerId][u32 udpKey][Nutzdaten];
/// Server -&gt; Client: [Typ][u16 playerId][Nutzdaten].
/// </summary>
public static class UdpPacketId
{
    public const byte StateIn = 1, StateOut = 2;       // Spielerzustand
    public const byte ChatOut = 3, ChatIn = 4;          // Chat
    public const byte TrafficIn = 5, TrafficOut = 6;    // Verkehr
    public const byte PingIn = 7, PongOut = 8;          // Laufzeit / NAT-Keepalive
    public const byte PeerPunch = 9;                    // P2P-Hole-Punching (nur Client <-> Client)
    public const byte RelayIn = 10;                     // Relay-Umschlag an den Server (nur an Mitspieler ohne Direktweg)
    public const byte HouseOut = 11;                    // My House: Einrichtung eines Spielers geändert (Server -> Lobby), neu
    public const byte StatusOut = 12;                   // Einladung/Antwort liegt beim Server: sofort "status" holen, neu
    /// <summary>Store-Paket an den Server: [13][u16 playerId][u32 udpKey][u8 Ziel][u16 Zielspieler][u16 magic][Nutzdaten].</summary>
    public const byte PacketIn = 13;
    /// <summary>Store-Paket vom Server: [14][u16 Absender (0 = Server)][u16 magic][Nutzdaten].</summary>
    public const byte PacketOut = 14;
    /// <summary>Ziel-Bits eines Store-Pakets (Byte 7 von <see cref="PacketIn"/>).</summary>
    public const byte ToServer = 1, ToLobby = 2, ToPlayer = 4;
    /// <summary>Größte Nutzlast eines Store-Pakets (Kennung + Daten), damit ein UDP-Datagramm nicht fragmentiert.</summary>
    public const int MaxStorePayload = 1200;

    /// <summary>Kopf der Client-Pakete (Typ, Spielernummer, Schlüssel).</summary>
    public const int ClientHeader = 7;
    /// <summary>Kopf der Server-Pakete (Typ, Spielernummer).</summary>
    public const int ServerHeader = 3;

    public static void WriteClientHeader(byte[] b, byte type, int playerId, uint key)
    {
        b[0] = type;
        b[1] = (byte)playerId; b[2] = (byte)(playerId >> 8);
        b[3] = (byte)key; b[4] = (byte)(key >> 8); b[5] = (byte)(key >> 16); b[6] = (byte)(key >> 24);
    }
}
