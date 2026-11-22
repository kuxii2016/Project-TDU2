using TDU2Server.Core;
using TDU2Server.Data;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;

namespace TDU2Server.Net.Handlers;

/// <summary>
/// {"op":"houseget","session":..,"target":id,"spot":".."} -> Einrichtung der Häuser von Spieler id (Gast in seinem Haus, nach UDP [11]
/// oder geändertem PlayerEntry.houseVer). Antwort: save.houses (nur das Haus "spot", leer = alle) und playerId = Änderungszähler.
/// </summary>
public sealed class HouseGetHandler : RequestHandler
{
    public HouseGetHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "houseget";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        if (SessionOf(conn, r) == null) return Fail("not_logged_in");
        if (!Ctx.Sessions.TryGetPlayer(r.target, out var owner)) return Fail("unknown_player");
        var hs = Ctx.Accounts.Load(owner.Nick)?.save?.houses ?? new List<HouseLayout>();
        var list = string.IsNullOrEmpty(r.spot) ? hs : hs.Where(h => h != null && string.Equals(h.spot, r.spot, StringComparison.OrdinalIgnoreCase)).ToList();
        return new NetResponse { ok = true, nick = owner.Nick, playerId = owner.HouseVer, lifeOf = owner.PlayerId, save = new SaveData { houses = list }, online = Online };
    }
}
