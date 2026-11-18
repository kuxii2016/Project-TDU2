namespace Paradise.Core.Online.Net;

/// <summary>Verkehrsautos eines anderen Spielers [6][u16 playerId][Nutzdaten] (Format: Unity TDU2Traffic).</summary>
public sealed class TrafficPacketIn : QueuedPacketIn
{
    public override byte Type => UdpPacketId.TrafficOut;
    public override int MinLength => 10;
    protected override int Capacity => 2000;
}
