using System.Text.Json.Serialization;

namespace Paradise.Core.Net;

/// <summary>
/// Ein Eintrag in der Serverliste. Diese Struktur ist geteilt zwischen
/// MasterServer (hält sie), Game-Server (meldet sie) und Launcher (zeigt sie).
/// Darum liegt sie in Core — kein Auseinanderdriften der Felder.
/// </summary>
public sealed class ServerInfo
{
    /// <summary>Stabile ID des Servers (vom Betreiber vergeben oder GUID).</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Freie Mod-Nachricht, die im Launcher erscheint ("Season 2 läuft!").</summary>
    [JsonPropertyName("modMessage")] public string ModMessage { get; set; } = "";

    /// <summary>Öffentliche Adresse, mit der sich der Client verbindet.</summary>
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }

    [JsonPropertyName("players")] public int Players { get; set; }
    [JsonPropertyName("maxPlayers")] public int MaxPlayers { get; set; }

    /// <summary>Spielstand/Version, damit inkompatible Server markiert werden können.</summary>
    [JsonPropertyName("version")] public string Version { get; set; } = "";

    /// <summary>Optional: passwortgeschützt?</summary>
    [JsonPropertyName("locked")] public bool Locked { get; set; }

    /// <summary>Wird vom MasterServer gesetzt (serverseitig), nicht vom Game-Server.</summary>
    [JsonIgnore] public DateTime LastSeenUtc { get; set; }

    public string Endpoint => $"{Host}:{Port}";
    public bool IsFull => MaxPlayers > 0 && Players >= MaxPlayers;
}
