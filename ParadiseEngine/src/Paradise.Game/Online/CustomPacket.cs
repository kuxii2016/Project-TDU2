using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Net;
using System.Net;

namespace Paradise.Game.Online
{
    /// <summary>
    /// Beispiel für Store-Pakete: Ping an Server und Lobby. Der Server antwortet auf 1499 mit 1500 (gleiche Zeit zurück),
    /// die Lobby bekommt 1499 vom Absender weitergeleitet. Kennungen sind in Client und Server gleich (Server: Net/Packets/ServerPackets.cs).
    /// Handler-Parameter nach Typ: Store, IPEndPoint, PacketContext, int (Absender-Nr., 0 = Server).
    /// </summary>
    internal class CustomPacket
    {
        public const ushort Ping = 1499, Pong = 1500;

        public static void SendPing()
        {
            using var packet = new Store(Ping);
            packet.PushFloat(Time.realtimeSinceStartup);
            SendTo.SendtoServerAndLobby(packet);
        }

        /// <summary>Ping eines Mitspielers (über den Server weitergeleitet).</summary>
        [PacketHandler(Ping)]
        public void OnPing(Store store, int sender)
        {
            float time = store.PopFloat();
            Log.Debug($"Store-Ping von Spieler {sender} ({time:0.00})");
        }

        /// <summary>Antwort des Servers: Laufzeit.</summary>
        [PacketHandler(Pong)]
        public void OnPong(Store store, IPEndPoint endPoint)
        {
            float time = store.PopFloat();
            if (store.ErrorLess) Log.Debug($"Store-Pong von {endPoint}: {(Time.realtimeSinceStartup - time) * 1000f:0} ms");
        }
    }
}
