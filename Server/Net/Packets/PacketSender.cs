#nullable enable
using TDU2Server.Core;
using TDU2Server.Net.Udp;
using TDU2Server.Sessions;

namespace TDU2Server.Net.Packets;

/// <summary>
/// Store-Pakete vom Server an Clients: [14][u16 0 = Server][u16 magic][Nutzdaten]. Beim Client landen sie in PacketRouter.Handle
/// (Absender 0). Höchstens <see cref="UdpPacketIds.MaxStorePayload"/> Byte; UDP darf verlieren.
/// </summary>
public static class PacketSender
{
    const double ActiveSeconds = 15;
    static UdpServer? _udp;
    static ServerContext? _ctx;

    internal static void Init(ServerContext ctx, UdpServer udp) { _ctx = ctx; _udp = udp; }

    static byte[]? Build(Store store)
    {
        var seg = store.AsSegment();
        if (seg.Count < 2 || seg.Count > UdpPacketIds.MaxStorePayload)
        {
            Log.Info($"Store-Paket {store.Magic}: {seg.Count} Byte – erlaubt sind 2..{UdpPacketIds.MaxStorePayload}");
            return null;
        }
        var o = new byte[3 + seg.Count];
        o[0] = UdpPacketIds.PacketOut;
        Buffer.BlockCopy(seg.Array!, seg.Offset, o, 3, seg.Count);
        return o;
    }

    /// <summary>An einen Spieler.</summary>
    public static bool Send(Session to, Store store)
    {
        if (_udp == null || to?.UdpEndPoint == null) return false;
        var o = Build(store);
        if (o == null) return false;
        _udp.Send(o, to.UdpEndPoint);
        return true;
    }

    /// <summary>An alle aktiven Spieler der Lobby von <paramref name="member"/> (mit ihm selbst, wenn includeSelf).</summary>
    public static int SendToLobby(Session member, Store store, bool includeSelf = true)
    {
        if (_udp == null || _ctx == null || member == null) return 0;
        var o = Build(store);
        if (o == null) return 0;
        int n = 0;
        var now = DateTime.UtcNow;
        foreach (var p in _ctx.Sessions.Players)
        {
            if ((!includeSelf && p == member) || p.UdpEndPoint == null || !p.UdpActive(now, ActiveSeconds)) continue;
            if (p != member && !LobbyManager.SameLobby(member, p)) continue;
            _udp.Send(o, p.UdpEndPoint); n++;
        }
        return n;
    }

    /// <summary>An alle aktiven Spieler.</summary>
    public static int Broadcast(Store store)
    {
        if (_udp == null || _ctx == null) return 0;
        var o = Build(store);
        if (o == null) return 0;
        int n = 0;
        var now = DateTime.UtcNow;
        foreach (var p in _ctx.Sessions.Players)
        {
            if (p.UdpEndPoint == null || !p.UdpActive(now, ActiveSeconds)) continue;
            _udp.Send(o, p.UdpEndPoint); n++;
        }
        return n;
    }
}
