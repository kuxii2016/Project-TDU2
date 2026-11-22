using System.Net;
using TDU2Server.Core;
using TDU2Server.Net.Packets;
using TDU2Server.Sessions;

namespace TDU2Server.Net.Udp;

/// <summary>
/// Store-Pakete (Client Paradise.Core.Net.Store / SendTo): [13][u16 playerId][u32 udpKey][u8 Ziel][u16 Zielspieler][u16 magic][Nutzdaten].
/// Ziel-Bits: 1 = Server (<see cref="PacketRouter"/> ruft die [PacketHandler] auf, Sender = <see cref="Session"/>),
/// 2 = alle anderen der Lobby, 4 = nur an den Zielspieler. Weitergeleitet wird [14][u16 Absender][u16 magic][Nutzdaten].
/// </summary>
public sealed class StorePacket : UdpPacketHandler
{
    const double ActiveSeconds = 15;
    const int Body = HeaderSize + 3;   // ab hier: u16 magic + Nutzdaten

    readonly PacketContext _pc = new();

    public StorePacket(ServerContext ctx, UdpServer udp) : base(ctx, udp) { }

    public override byte Type => UdpPacketIds.PacketIn;
    public override int MinLength => Body + 2;
    public override int MaxLength => Body + UdpPacketIds.MaxStorePayload;

    public override Task HandleAsync(byte[] d, IPEndPoint from)
    {
        int pid = PlayerIdOf(d);
        var s = Ctx.Sessions.Authenticate(pid, KeyOf(d));
        if (s == null) return Task.CompletedTask;
        s.UdpEndPoint = from;
        s.LastUdp = DateTime.UtcNow;

        byte target = d[HeaderSize];
        int toPid = d[HeaderSize + 1] | (d[HeaderSize + 2] << 8);
        int len = d.Length - Body;

        if ((target & (UdpPacketIds.ToLobby | UdpPacketIds.ToPlayer)) != 0)
        {
            var o = new byte[3 + len];
            WriteHeader(o, UdpPacketIds.PacketOut, pid);
            Buffer.BlockCopy(d, Body, o, 3, len);
            if ((target & UdpPacketIds.ToLobby) != 0)
                Udp.Relay(s, o, ActiveSeconds);
            else if (toPid != pid && Ctx.Sessions.TryGetPlayer(toPid, out var r) && r.UdpEndPoint != null
                     && r.UdpActive(DateTime.UtcNow, ActiveSeconds) && LobbyManager.SameLobby(s, r))
                Udp.Send(o, r.UdpEndPoint);
        }

        if ((target & UdpPacketIds.ToServer) != 0)
        {
            // Empfangsschleife ist sequentiell (LoopAsync wartet jedes Paket ab) -> ein Kontext reicht
            _pc.EndPoint = from;
            _pc.SenderId = pid;
            _pc.Sender = s;
            PacketRouter.HandleRaw(d, Body, len, _pc);
        }
        return Task.CompletedTask;
    }
}
