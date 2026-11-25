using Paradise.Core.Net;
using Paradise.Core.Online.Net;
using System.Collections.Concurrent;

namespace Paradise.Core.Diagnostics;

/// <summary>Gruppen der Netzwerk-Zähler im Profiler-Graphen und im Network-Reiter.</summary>
public static class ProfilerNetworkGroups
{
    public const string Tcp = "Server TCP";
    public const string Udp = "Server UDP";
    public const string Direct = "P2P Direct";
    public const string Relay = "Relay";
    public const string Players = "Net Players";
    public const string Packets = "Packets";
}

/// <summary>
/// Eine Zeile des Netzwerk-Snapshots. Counts/Bytes sind kumulative Werte, die der Profiler pro Bild
/// in Differenzen umrechnet (<see cref="Absolute"/> = Momentanwert wie Spielerzahl, bleibt unverändert).
/// </summary>
public readonly struct ProfilerNetworkEntry
{
    public readonly string Group;
    public readonly string Name;
    public readonly string Detail;
    public readonly long InCount, OutCount, InBytes, OutBytes;
    public readonly bool Absolute;

    public ProfilerNetworkEntry(string group, string name, long inCount = 0, long outCount = 0,
        long inBytes = 0, long outBytes = 0, string detail = "", bool absolute = false)
    {
        Group = group; Name = name; InCount = inCount; OutCount = outCount;
        InBytes = inBytes; OutBytes = outBytes; Detail = detail; Absolute = absolute;
    }

    public long TotalBytes => InBytes + OutBytes;
    public long TotalCount => InCount + OutCount;
    public string Key => Group + "|" + Name;
}

/// <summary>Unveränderlicher Netzwerkstand eines Bildes (vom Spiel-Thread gebaut, im UI gelesen).</summary>
public sealed class ProfilerNetworkSnapshot
{
    public static readonly ProfilerNetworkSnapshot Empty = new(Array.Empty<ProfilerNetworkEntry>());

    public ProfilerNetworkEntry[] Entries { get; }

    public ProfilerNetworkSnapshot(ProfilerNetworkEntry[] entries) => Entries = entries ?? Array.Empty<ProfilerNetworkEntry>();

    public long InBytes { get { long s = 0; foreach (var e in Entries) s += e.InBytes; return s; } }
    public long OutBytes { get { long s = 0; foreach (var e in Entries) s += e.OutBytes; return s; } }
    public long InCount { get { long s = 0; foreach (var e in Entries) s += e.InCount; return s; } }
    public long OutCount { get { long s = 0; foreach (var e in Entries) s += e.OutCount; return s; } }

    public long GroupBytes(string group, bool incoming)
    {
        long s = 0;
        foreach (var e in Entries)
            if (e.Group == group) s += incoming ? e.InBytes : e.OutBytes;
        return s;
    }

    public long GroupCount(string group, bool incoming)
    {
        long s = 0;
        foreach (var e in Entries)
            if (e.Group == group) s += incoming ? e.InCount : e.OutCount;
        return s;
    }

    /// <summary>Wert einer benannten Zeile (z. B. "Peers" oder "Direct peers").</summary>
    public long Value(string name, bool incoming = false, bool bytes = false)
    {
        foreach (var e in Entries)
            if (e.Name == name) return bytes ? (incoming ? e.InBytes : e.OutBytes) : (incoming ? e.InCount : e.OutCount);
        return 0;
    }

    public ProfilerNetworkEntry? Find(string group, string name)
    {
        foreach (var e in Entries)
            if (e.Group == group && e.Name == name) return e;
        return null;
    }

    /// <summary>Summe oder Mittel mehrerer Bilder (für den Reiter "Ø all frames" und den Bericht).</summary>
    public static ProfilerNetworkSnapshot Merge(IEnumerable<ProfilerNetworkSnapshot> snapshots, bool average)
    {
        var sum = new Dictionary<string, ProfilerNetworkEntry>();
        var count = new Dictionary<string, int>();
        int frames = 0;
        foreach (var snap in snapshots)
        {
            if (snap == null) continue;
            frames++;
            foreach (var e in snap.Entries)
            {
                sum.TryGetValue(e.Key, out var a);
                count.TryGetValue(e.Key, out int n);
                sum[e.Key] = new ProfilerNetworkEntry(e.Group, e.Name,
                    a.InCount + e.InCount, a.OutCount + e.OutCount,
                    a.InBytes + e.InBytes, a.OutBytes + e.OutBytes, e.Detail, e.Absolute);
                count[e.Key] = n + 1;
            }
        }
        if (frames == 0) return Empty;
        var list = new List<ProfilerNetworkEntry>(sum.Count);
        foreach (var kv in sum)
        {
            var e = kv.Value;
            int n = System.Math.Max(1, count[kv.Key]);
            bool avg = average || e.Absolute;   // Momentanwerte (Spielerzahl) sind auch bei Summation Mittel
            list.Add(new ProfilerNetworkEntry(e.Group, e.Name,
                avg ? e.InCount / n : e.InCount, avg ? e.OutCount / n : e.OutCount,
                avg ? e.InBytes / n : e.InBytes, avg ? e.OutBytes / n : e.OutBytes,
                e.Detail, e.Absolute));
        }
        return new ProfilerNetworkSnapshot(list.ToArray());
    }
}

/// <summary>
/// Sammelt echte Netzwerkwerte aus Core und Game. Die Zähler laufen in den Netzwerk-Threads und werden
/// am Bildende (<see cref="Profiler.EndFrame"/>) in Differenzen je Bild umgerechnet. Der Game-Teil meldet
/// seine Zähler über <see cref="RegisterProvider"/> an – Core darf nicht auf Paradise.Game verweisen.
/// </summary>
public static class ProfilerNetwork
{
    static Func<ProfilerNetworkSnapshot?>? s_provider;

    sealed class Counter
    {
        public long InCount, OutCount, InBytes, OutBytes;
    }

    static readonly ConcurrentDictionary<string, Counter> s_packets = new();
    static readonly ConcurrentDictionary<int, Counter> s_peers = new();
    static readonly ConcurrentDictionary<int, string> s_peerInfo = new();
    static long s_relayInCount, s_relayInBytes, s_relayOutCount, s_relayOutBytes;
    static Dictionary<string, ProfilerNetworkEntry>? s_prev;
    static bool s_hasPrev;

    /// <summary>Game-Seite: Zähler des (!)--Netzteils als Momentanwerte liefern. Ein Provider pro Prozess.</summary>
    public static void RegisterProvider(Func<ProfilerNetworkSnapshot?> provider) => Volatile.Write(ref s_provider, provider);

    public static bool HasProvider => Volatile.Read(ref s_provider) != null;

    static void Add(ConcurrentDictionary<string, Counter> map, string key, bool incoming, long bytes)
    {
        var c = map.GetOrAdd(key, _ => new Counter());
        if (incoming) { Interlocked.Increment(ref c.InCount); Interlocked.Add(ref c.InBytes, bytes); }
        else { Interlocked.Increment(ref c.OutCount); Interlocked.Add(ref c.OutBytes, bytes); }
    }

    static void AddPeer(int id, bool incoming, int bytes)
    {
        var c = s_peers.GetOrAdd(id, _ => new Counter());
        if (incoming) { Interlocked.Increment(ref c.InCount); Interlocked.Add(ref c.InBytes, bytes); }
        else { Interlocked.Increment(ref c.OutCount); Interlocked.Add(ref c.OutBytes, bytes); }
    }

    /// <summary>Kummulative Bytes/Pakete eines Mitspielers (für den Game-Provider).</summary>
    public static (long inCount, long outCount, long inBytes, long outBytes) PeerStats(int id)
    {
        if (!s_peers.TryGetValue(id, out var c)) return (0, 0, 0, 0);
        return (Interlocked.Read(ref c.InCount), Interlocked.Read(ref c.OutCount),
                Interlocked.Read(ref c.InBytes), Interlocked.Read(ref c.OutBytes));
    }

    /// <summary>Vom Game-Provider gesetzter Anzeigename/Status eines Mitspielers.</summary>
    public static void SetPeerInfo(int id, string detail) => s_peerInfo[id] = detail ?? "";

    public static void PacketIn(byte type, int bytes) => Add(s_packets, "UDP " + Name(type), true, bytes);
    public static void PacketOut(byte type, int bytes) => Add(s_packets, "UDP " + Name(type), false, bytes);
    public static void StoreIn(ushort magic) => Add(s_packets, StoreName(magic), true, 0);

    public static void PeerIn(int id, int bytes) { if (id > 0) AddPeer(id, true, bytes); }
    public static void PeerOut(int id, int bytes) { if (id > 0) AddPeer(id, false, bytes); }

    public static void RelayIn(int bytes) { Interlocked.Increment(ref s_relayInCount); Interlocked.Add(ref s_relayInBytes, bytes); }
    public static void RelayOut(int bytes) { Interlocked.Increment(ref s_relayOutCount); Interlocked.Add(ref s_relayOutBytes, bytes); }

    static string Name(byte t) => t switch
    {
        UdpPacketId.StateIn => "State in",
        UdpPacketId.StateOut => "State out",
        UdpPacketId.ChatIn => "Chat in",
        UdpPacketId.ChatOut => "Chat out",
        UdpPacketId.TrafficIn => "Traffic in",
        UdpPacketId.TrafficOut => "Traffic out",
        UdpPacketId.PingIn => "Ping",
        UdpPacketId.PongOut => "Pong",
        UdpPacketId.PeerPunch => "Peer punch",
        UdpPacketId.RelayIn => "Relay envelope",
        UdpPacketId.HouseOut => "House",
        UdpPacketId.StatusOut => "Status",
        UdpPacketId.PacketIn => "Store in",
        UdpPacketId.PacketOut => "Store out",
        _ => "Type " + t.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    static string StoreName(ushort magic)
    {
        try
        {
            if (PacketRouter.handlers.TryGetValue(magic, out var h) && h?.Method != null)
                return "Store " + h.Method.Name + " (" + magic + ")";
        }
        catch (Exception) { }
        return "Store magic " + magic;
    }

    /// <summary>Bildabschluss: Provider abfragen, Zähler einsammeln und gegen das letzte Bild differenzieren.</summary>
    public static ProfilerNetworkSnapshot Sample()
    {
        var list = new List<ProfilerNetworkEntry>();
        var provider = Volatile.Read(ref s_provider);
        if (provider != null)
            try { var s = provider(); if (s != null) list.AddRange(s.Entries); } catch (Exception) { }

        foreach (var kv in s_packets)
        {
            var c = kv.Value;
            list.Add(new ProfilerNetworkEntry(ProfilerNetworkGroups.Packets, kv.Key,
                Interlocked.Read(ref c.InCount), Interlocked.Read(ref c.OutCount),
                Interlocked.Read(ref c.InBytes), Interlocked.Read(ref c.OutBytes)));
        }
        foreach (var kv in s_peers)
        {
            var c = kv.Value;
            s_peerInfo.TryGetValue(kv.Key, out string? info);
            list.Add(new ProfilerNetworkEntry(ProfilerNetworkGroups.Players, "#" + kv.Key,
                Interlocked.Read(ref c.InCount), Interlocked.Read(ref c.OutCount),
                Interlocked.Read(ref c.InBytes), Interlocked.Read(ref c.OutBytes), info ?? ""));
        }
        list.Add(new ProfilerNetworkEntry(ProfilerNetworkGroups.Relay, "Relay traffic",
            Interlocked.Read(ref s_relayInCount), Interlocked.Read(ref s_relayOutCount),
            Interlocked.Read(ref s_relayInBytes), Interlocked.Read(ref s_relayOutBytes)));

        var current = new Dictionary<string, ProfilerNetworkEntry>(list.Count, StringComparer.Ordinal);
        foreach (var e in list) current[e.Key] = e;
        var result = new List<ProfilerNetworkEntry>(list.Count);
        if (!s_hasPrev)
        {
            foreach (var e in list)
                result.Add(e.Absolute ? e : new ProfilerNetworkEntry(e.Group, e.Name, 0, 0, 0, 0, e.Detail));
            s_prev = current;
            s_hasPrev = true;
            return new ProfilerNetworkSnapshot(result.ToArray());
        }
        var prev = s_prev ?? current;
        foreach (var e in list)
        {
            if (e.Absolute) { result.Add(e); continue; }
            prev.TryGetValue(e.Key, out var p);
            long di = System.Math.Max(0, e.InCount - p.InCount), dO = System.Math.Max(0, e.OutCount - p.OutCount);
            long dIB = System.Math.Max(0, e.InBytes - p.InBytes), dOB = System.Math.Max(0, e.OutBytes - p.OutBytes);
            if (di != 0 || dO != 0 || dIB != 0 || dOB != 0)
                result.Add(new ProfilerNetworkEntry(e.Group, e.Name, di, dO, dIB, dOB, e.Detail));
        }
        s_prev = current;
        return new ProfilerNetworkSnapshot(result.ToArray());
    }

    /// <summary>Neue Aufnahme: Differenzbasis vergessen (sonst springt der erste Graphwert).</summary>
    public static void Reset()
    {
        s_prev = null;
        s_hasPrev = false;
        s_packets.Clear();
        s_peers.Clear();
        s_peerInfo.Clear();
        Interlocked.Exchange(ref s_relayInCount, 0);
        Interlocked.Exchange(ref s_relayInBytes, 0);
        Interlocked.Exchange(ref s_relayOutCount, 0);
        Interlocked.Exchange(ref s_relayOutBytes, 0);
    }
}
