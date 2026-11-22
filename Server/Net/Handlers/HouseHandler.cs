using TDU2Server.Core;
using TDU2Server.Data;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;

namespace TDU2Server.Net.Handlers;

/// <summary>
/// {"op":"house","session":..,"house":{spot,furn,mats}} -> Einrichtung eines eigenen Hauses sofort speichern (My House, Besitzer hat
/// ein Möbelstück/einen Boden/eine Tapete geändert). Der Änderungszähler der Sitzung steigt; Spieler derselben Lobby bekommen
/// UDP [11] (UdpServer.NotifyHouse), Gäste im Haus holen die neue Einrichtung per "houseget" und sehen die Änderung sofort.
/// Der vollständige Spielstand ("save") enthält die Einrichtung weiterhin; dieser Weg ist nur schneller und klein.
/// </summary>
public sealed class HouseHandler : RequestHandler
{
    public HouseHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "house";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var s = SessionOf(conn, r);
        if (s == null) return Fail("not_logged_in");
        if (!SaveSanitizer.CleanHouse(r.house)) return Fail("no_house");
        var a = Ctx.Accounts.Load(s.Nick);
        if (a == null) return Fail("unknown_nick");

        a.save ??= new SaveData();
        a.save.houses ??= new List<HouseLayout>();
        a.save.houses.RemoveAll(h => h == null || string.Equals(h.spot, r.house.spot, StringComparison.OrdinalIgnoreCase));
        if (a.save.houses.Count >= 100) return Fail("too_many_houses");
        a.save.houses.Add(r.house);
        Ctx.Accounts.Store(a);

        s.HouseVer++;
        Ctx.Udp?.NotifyHouse(s);
        return new NetResponse { ok = true, online = Online };
    }
}
