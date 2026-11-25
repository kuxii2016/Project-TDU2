using Paradise.Core.Diagnostics;
using Paradise.Core.Online;
using System.Globalization;

namespace Paradise.Game.Online;

/// <summary>
/// Liefert die Zähler des laufenden Spiels an den Profiler (F1 &gt; Performance). Wird beim Laden des
/// ScreenModule einmal registriert; der Profiler fragt am Bildende ab und rechnet dort die Differenzen.
/// Die pkt-/byte-genauen Zähler je Typ und Spieler sammelt Paradise.Core selbst.
/// </summary>
public static class ProfilerNetworkProvider
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static bool s_registered;

    public static void Register()
    {
        if (s_registered) return;
        s_registered = true;
        ProfilerNetwork.RegisterProvider(Snapshot);
    }

    static ProfilerNetworkSnapshot Snapshot()
    {
        var list = new List<ProfilerNetworkEntry>();

        // ------------------------------------------------------------- Server-TCP (Login/Status/Save)
        var tc = TDU2Session.Client;
        if (tc != null)
        {
            string conn = TDU2Session.Offline ? "offline profile"
                : tc.Connected ? "connected" : "disconnected";
            string detail = conn + " · " + tc.Host + ":" + tc.Port.ToString(Inv) +
                " · RTT " + (tc.LastRttMs < 0 ? "-" : tc.LastRttMs.ToString("0", Inv) + " ms") +
                " · errors " + tc.Failures.ToString(Inv) + " · gen " + tc.Generation.ToString(Inv);
            if (!string.IsNullOrEmpty(tc.LastError)) detail += " · " + tc.LastError;
            list.Add(new ProfilerNetworkEntry(ProfilerNetworkGroups.Tcp, "Connection",
                tc.Responses, tc.Requests, tc.BytesIn, tc.BytesOut, detail));
        }

        // ------------------------------------------------------------- Server-UDP (Zustand/Chat/Store/Relay)
        if (Multiplayer.ServerSocketUp)
        {
            string detail = Multiplayer.ServerEndpoint + " · ping " +
                (Multiplayer.PingMs < 0 ? "-" : Multiplayer.PingMs.ToString("0", Inv) + " ms") +
                " · dispatch " + Multiplayer.DispatchReceived.ToString(Inv) + "/" + Multiplayer.DispatchDropped.ToString(Inv) +
                " · queue " + Multiplayer.TrafficInboxCount.ToString(Inv) +
                " · UDP errors " + Multiplayer.UdpSendErrors.ToString(Inv) + "/" + Multiplayer.UdpRecvErrors.ToString(Inv);
            list.Add(new ProfilerNetworkEntry(ProfilerNetworkGroups.Udp, "Server",
                Multiplayer.ServerPacketsIn, Multiplayer.ServerPacketsOut,
                Multiplayer.ServerBytesIn, Multiplayer.ServerBytesOut, detail));
        }

        // ------------------------------------------------------------- P2P direkt / Relay
        var mesh = Multiplayer.Mesh;
        if (mesh != null && mesh.Active)
        {
            list.Add(new ProfilerNetworkEntry(ProfilerNetworkGroups.Direct, "Direct data",
                mesh.DirectPacketsIn, mesh.DirectPacketsOut, mesh.DirectBytesIn, mesh.DirectBytesOut,
                mesh.PeerCount.ToString(Inv) + " peers · " + mesh.DirectCount.ToString(Inv) + " direct · " +
                mesh.RelayCount.ToString(Inv) + " relay"));
            list.Add(new ProfilerNetworkEntry(ProfilerNetworkGroups.Direct, "Hole punch",
                mesh.PunchPacketsIn, mesh.PunchPacketsOut, 0, 0,
                "send errors " + mesh.DirectSendErrors.ToString(Inv)));
        }

        // ------------------------------------------------------------- Mitspieler (Momentanwerte für den Graphen)
        int peers = mesh?.PeerCount ?? 0, direct = mesh?.DirectCount ?? 0, relay = mesh?.RelayCount ?? 0;
        int remotes = 0;
        foreach (var _ in RemoteCars.Info()) remotes++;
        list.Add(new ProfilerNetworkEntry(ProfilerNetworkGroups.Players, "Lobby peers", outCount: peers, absolute: true));
        list.Add(new ProfilerNetworkEntry(ProfilerNetworkGroups.Players, "Direct peers", outCount: direct, absolute: true));
        list.Add(new ProfilerNetworkEntry(ProfilerNetworkGroups.Players, "Relay peers", outCount: relay, absolute: true));
        list.Add(new ProfilerNetworkEntry(ProfilerNetworkGroups.Players, "Remote cars", outCount: remotes, absolute: true));
        list.Add(new ProfilerNetworkEntry(ProfilerNetworkGroups.Players, "Players online", outCount: TDU2Session.Online, absolute: true,
            detail: "lobby " + TDU2GameData.Lobby.ToString(Inv) + " · " +
                    TDU2GameData.LobbyCount.ToString(Inv) + "/" + TDU2GameData.LobbyMax.ToString(Inv)));

        // Namen/Status der Mitspieler an die Core-Zähler hängen (dort steht/pkg bytes je Id)
        var names = new Dictionary<int, string>();
        foreach (var p in TDU2GameData.Players.Values)
            if (p.Id > 0) names[p.Id] = string.IsNullOrEmpty(p.Nick) ? "#" + p.Id.ToString(Inv) : p.Nick;
        foreach (var r in RemoteCars.Info())
        {
            string car = r.loaded ? r.car : "loading/none";
            names[r.id] = names.TryGetValue(r.id, out string? n) ? n + " · " + car + " · " + r.age.ToString("0.0", Inv) + " s"
                : "#" + r.id.ToString(Inv) + " · " + car + " · " + r.age.ToString("0.0", Inv) + " s";
        }
        foreach (var kv in names)
        {
            string info = kv.Value + " · " + (mesh != null && mesh.IsDirect(kv.Key) ? "direct" : "relay/server");
            ProfilerNetwork.SetPeerInfo(kv.Key, info);
        }

        return new ProfilerNetworkSnapshot(list.ToArray());
    }
}
