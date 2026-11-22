using TDU2Server.Sessions;

namespace TDU2Server.Net.Packets;

/// <summary>
/// [PacketHandler] des Servers für Store-Pakete (Client: SendTo.SendtoServer). Parameter nach Typ: Store (Nutzdaten hinter der
/// Kennung), Session (Absender), IPEndPoint, PacketContext, int (Spielernummer). Kennungen gleich wie im Client (CustomPacket).
/// Weitere Klassen in UdpServer mit PacketRouter.RegisterHandlers&lt;T&gt;() anmelden.
/// </summary>
public sealed class ServerPackets
{
    public const ushort Ping = 1499, Pong = 1500;

    /// <summary>Beispiel: Ping -> gleiche Zeit als Pong zurück an den Absender.</summary>
    [PacketHandler(Ping)]
    public void OnPing(Store store, Session sender)
    {
        float time = store.PopFloat();
        if (!store.ErrorLess) return;
        using var reply = new Store(Pong);
        reply.PushFloat(time);
        PacketSender.Send(sender, reply);
    }
}
