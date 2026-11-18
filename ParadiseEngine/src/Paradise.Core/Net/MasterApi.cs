using System.Text.Json.Serialization;

namespace Paradise.Core.Net;

/// <summary>
/// Vertrag zwischen Game-Server, MasterServer und Launcher.
/// Pfade zentral definiert, damit alle drei Seiten dieselben Routen nutzen.
/// </summary>
public static class MasterApi
{
    /// <summary>Game-Server meldet sich an / aktualisiert vollständig. Body: ServerInfo. Antwort: RegisterResponse.</summary>
    public const string Register = "/api/register";

    /// <summary>Game-Server-Lebenszeichen. Body: HeartbeatRequest. Hält den Server in der Liste und aktualisiert Spielerzahl.</summary>
    public const string Heartbeat = "/api/heartbeat";

    /// <summary>Game-Server meldet sich sauber ab. Body: HeartbeatRequest (nur Token/Id nötig).</summary>
    public const string Unregister = "/api/unregister";

    /// <summary>Launcher holt die Liste. Antwort: ServerListResponse.</summary>
    public const string Servers = "/api/servers";

    /// <summary>Wie lange ein Server ohne Heartbeat in der Liste bleibt.</summary>
    public static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Empfohlenes Heartbeat-Intervall für Game-Server.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
}

public sealed class RegisterResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    /// <summary>Geheimes Token; nur hiermit darf der Server seinen Eintrag aktualisieren.</summary>
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class HeartbeatRequest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("players")] public int Players { get; set; }
    [JsonPropertyName("maxPlayers")] public int MaxPlayers { get; set; }
    /// <summary>Optional aktualisierbare Mod-Message, ohne komplette Neuanmeldung.</summary>
    [JsonPropertyName("modMessage")] public string? ModMessage { get; set; }
}

public sealed class ServerListResponse
{
    [JsonPropertyName("servers")] public List<ServerInfo> Servers { get; set; } = new();
    [JsonPropertyName("count")] public int Count { get; set; }
}