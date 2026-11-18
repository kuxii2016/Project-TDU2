namespace Paradise.Core.Online.Net;

/// <summary>
/// My House: Einrichtung eines Spielers geändert [11][u16 Besitzer][u32 Änderungszähler] (Server, nach op "house"). Gäste im Haus
/// holen die neue Einrichtung per op "houseget" (Game/Gamemodes/GAME_MODE_LIVING). Neu gegenüber Unity (Server erweitert).
/// </summary>
public sealed class HousePacketIn : QueuedPacketIn
{
    public override byte Type => UdpPacketId.HouseOut;
    public override int MinLength => UdpPacketId.ServerHeader + 4;
    protected override int Capacity => 64;
}
