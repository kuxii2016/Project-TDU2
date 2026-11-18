namespace Paradise.Core.Online.Net;

/// <summary>
/// Ein empfangener Pakettyp (Server -&gt; Client) – Port von Unity IUdpPacketIn. Je Typ eine Klasse/Datei; <see cref="UdpDispatcher"/>
/// verteilt nach Byte 0. Handle läuft im Empfangs-Thread: nur einreihen/zählen.
/// </summary>
public interface IUdpPacketIn
{
    byte Type { get; }
    int MinLength { get; }
    void Handle(byte[] data, double receivedAt);
}
