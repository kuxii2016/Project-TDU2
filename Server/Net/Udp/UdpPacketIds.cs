namespace TDU2Server.Net.Udp;

/// <summary>
/// Pakettypen (Byte 0) der UDP-Synchronisation – identisch im Client (TDU2.Online.Net.UdpPacketId).
/// Client -&gt; Server: [Typ][u16 playerId][u32 udpKey][Nutzdaten]; Server -&gt; Client: [Typ][u16 playerId][Nutzdaten].
/// </summary>
public static class UdpPacketIds
{
    public const byte StateIn = 1, StateOut = 2;       // Spielerzustand (PlayerStatePacket)
    public const byte ChatOut = 3, ChatIn = 4;          // Chat (ChatPacket)
    public const byte TrafficIn = 5, TrafficOut = 6;    // Verkehr (TrafficPacket)
    public const byte PingIn = 7, PongOut = 8;          // Laufzeit/NAT-Keepalive (PingPacket)
    public const byte PeerPunch = 9;                    // nur Client &lt;-&gt; Client (Hole-Punching), nie über den Server
    public const byte RelayIn = 10;                     // Umschlag: Zustand/Verkehr nur an die genannten Spieler (RelayPacket)
    public const byte HouseOut = 11;                    // My House: Einrichtung geändert [11][u16 Besitzer][u32 Änderungszähler] (HouseHandler)
    public const byte StatusOut = 12;                   // Neue Einladung/Antwort/Meldung: [12][u16 0] -> Client holt sofort "status" (statt bis zu 3 s zu warten)
    public const byte PacketIn = 13;                    // Store-Paket: [13][u16 pid][u32 key][u8 Ziel][u16 Zielspieler][u16 magic][Daten] (StorePacket)
    public const byte PacketOut = 14;                   // Store-Paket an Client: [14][u16 Absender, 0 = Server][u16 magic][Daten]
    public const byte ToServer = 1, ToLobby = 2, ToPlayer = 4;   // Ziel-Bits von PacketIn
    public const int MaxStorePayload = 1200;            // magic + Daten
}
