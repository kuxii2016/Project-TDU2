using Paradise.Core.Net;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Paradise.MasterServer;

/// <summary>
/// Hält die angemeldeten Server im Speicher. Thread-sicher.
/// Server ohne Heartbeat innerhalb von MasterApi.ServerTimeout fliegen raus.
/// </summary>
public sealed class ServerRegistry
{
    private sealed record Entry(ServerInfo Info, string Token);

    private readonly ConcurrentDictionary<string, Entry> _byId = new();

    public RegisterResponse Register(ServerInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.Name) || string.IsNullOrWhiteSpace(info.Host) || info.Port <= 0)
            return new RegisterResponse { Ok = false, Error = "Name, Host und Port sind erforderlich." };

        if (string.IsNullOrWhiteSpace(info.Id))
            info.Id = Guid.NewGuid().ToString("N");

        info.LastSeenUtc = DateTime.UtcNow;

        // Bestehenden Token behalten, falls derselbe Server neu meldet; sonst neuen erzeugen.
        string token = _byId.TryGetValue(info.Id, out var existing)
            ? existing.Token
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        _byId[info.Id] = new Entry(info, token);
        return new RegisterResponse { Ok = true, Token = token };
    }

    public bool Heartbeat(HeartbeatRequest hb)
    {
        if (!_byId.TryGetValue(hb.Id, out var e) || e.Token != hb.Token)
            return false;

        e.Info.Players = hb.Players;
        if (hb.MaxPlayers > 0) e.Info.MaxPlayers = hb.MaxPlayers;
        if (hb.ModMessage != null) e.Info.ModMessage = hb.ModMessage;
        e.Info.LastSeenUtc = DateTime.UtcNow;
        return true;
    }

    public bool Unregister(HeartbeatRequest hb)
    {
        if (_byId.TryGetValue(hb.Id, out var e) && e.Token == hb.Token)
            return _byId.TryRemove(hb.Id, out _);
        return false;
    }

    public List<ServerInfo> Active()
    {
        var now = DateTime.UtcNow;
        return _byId.Values
            .Where(e => now - e.Info.LastSeenUtc <= MasterApi.ServerTimeout)
            .Select(e => e.Info)
            .OrderByDescending(s => s.Players)
            .ThenBy(s => s.Name)
            .ToList();
    }

    public int Sweep()
    {
        var now = DateTime.UtcNow;
        int removed = 0;
        foreach (var kv in _byId)
            if (now - kv.Value.Info.LastSeenUtc > MasterApi.ServerTimeout)
                if (_byId.TryRemove(kv.Key, out _)) removed++;
        return removed;
    }
}
