namespace Paradise.Core.Online.Net;

/// <summary>
/// Neue Einladung/Antwort/Meldung liegt beim Server [12][u16 0] (nach op "invite"/"invreply"): sofort "status" holen statt auf das
/// nächste reguläre Abfrage-Intervall zu warten (Game/Online/TDU2GameData.RequestStatusNow). Neu gegenüber Unity (Server erweitert).
/// </summary>
public sealed class StatusPacketIn : QueuedPacketIn
{
    public override byte Type => UdpPacketId.StatusOut;
    public override int MinLength => UdpPacketId.ServerHeader;
    protected override int Capacity => 16;
}
