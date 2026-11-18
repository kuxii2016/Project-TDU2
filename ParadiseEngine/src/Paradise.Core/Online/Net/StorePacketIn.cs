using System.Collections.Concurrent;

namespace Paradise.Core.Online.Net;

/// <summary>
/// Store-Paket vom Server [14][u16 Absender (0 = Server)][u16 magic][Nutzdaten] (<see cref="Paradise.Core.Net.Store"/>). Wird im
/// Empfangs-Thread nur eingereiht; Multiplayer.Update verteilt es im Hauptthread über <see cref="Paradise.Core.Net.PacketRouter"/>.
/// </summary>
public sealed class StorePacketIn : IUdpPacketIn
{
    public byte Type => UdpPacketId.PacketOut;
    public int MinLength => UdpPacketId.ServerHeader + 2;
    public readonly ConcurrentQueue<(byte[] data, double t)> Queue = new();
    public void Handle(byte[] data, double receivedAt) { if (Queue.Count < 512) Queue.Enqueue((data, receivedAt)); }
}
