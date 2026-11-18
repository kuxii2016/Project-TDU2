namespace Paradise.Core.Online.Net;

/// <summary>
/// Zustand eines Mitspielers [2][u16 playerId][Zustand] (Format: Game/Online/Multiplayer, Unity TDU2Multiplayer).
/// Ausgewertet im Hauptthread (Interpolation, Auto laden, Lackierung …).
/// </summary>
public sealed class PlayerStatePacketIn : QueuedPacketIn
{
    public const int StateSizeOld = 80;
    public override byte Type => UdpPacketId.StateOut;
    public override int MinLength => UdpPacketId.ServerHeader + StateSizeOld;
    protected override int Capacity => 4000;
}
