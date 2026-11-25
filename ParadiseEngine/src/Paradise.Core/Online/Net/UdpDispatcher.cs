using Paradise.Core.Diagnostics;

namespace Paradise.Core.Online.Net;

/// <summary>Verteilt empfangene UDP-Pakete nach ihrem Typ (Byte 0) an die registrierte Paketklasse (Unity UdpDispatcher).</summary>
public sealed class UdpDispatcher
{
    readonly IUdpPacketIn?[] _byType = new IUdpPacketIn?[256];
    public long Received, Dropped;

    public UdpDispatcher Register(IUdpPacketIn? p) { if (p != null) _byType[p.Type] = p; return this; }

    public void Dispatch(byte[]? d, double t)
    {
        if (d == null || d.Length == 0) { Dropped++; return; }
        // Profiler: echter Verkehr je Pakettyp und Mitspieler (auch verworfene Pakete zählen als empfangen)
        ProfilerNetwork.PacketIn(d[0], d.Length);
        int sender = d.Length >= 3 ? d[1] | (d[2] << 8) : 0;
        if (sender > 0) ProfilerNetwork.PeerIn(sender, d.Length);
        // [14] mit Absender != 0 kam über den Server-Relay eines anderen Spielers
        if (d[0] == UdpPacketId.PacketOut && sender > 0) ProfilerNetwork.RelayIn(d.Length);
        var p = _byType[d[0]];
        if (p == null || d.Length < p.MinLength) { Dropped++; return; }
        Received++;
        p.Handle(d, t);
    }
}
