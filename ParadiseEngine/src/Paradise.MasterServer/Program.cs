using Paradise.Core.Net;
using Paradise.MasterServer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ServerRegistry>();
builder.Services.AddHostedService<SweepService>();

var app = builder.Build();

var registry = app.Services.GetRequiredService<ServerRegistry>();

// --- Game-Server meldet sich an ---
app.MapPost(MasterApi.Register, (ServerInfo info, HttpContext ctx) =>
{
    // Wenn kein Host angegeben, die Absender-IP verwenden (NAT-freundlich).
    if (string.IsNullOrWhiteSpace(info.Host))
        info.Host = ctx.Connection.RemoteIpAddress?.ToString() ?? "";

    var resp = registry.Register(info);
    return resp.Ok ? Results.Ok(resp) : Results.BadRequest(resp);
});

// --- Lebenszeichen + Spielerzahl aktualisieren ---
app.MapPost(MasterApi.Heartbeat, (HeartbeatRequest hb) =>
    registry.Heartbeat(hb) ? Results.Ok() : Results.NotFound());

// --- Sauber abmelden ---
app.MapPost(MasterApi.Unregister, (HeartbeatRequest hb) =>
    registry.Unregister(hb) ? Results.Ok() : Results.NotFound());

// --- Launcher holt die Liste ---
app.MapGet(MasterApi.Servers, () =>
{
    var list = registry.Active();
    return Results.Ok(new ServerListResponse { Servers = list, Count = list.Count });
});

// Einfache Statusseite
app.MapGet("/", () => Results.Text(
    $"Paradise Engine MasterServer\nAktive Server: {registry.Active().Count}\n", "text/plain"));

app.Run();
