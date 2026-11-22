using TDU2Server.Core;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;

namespace TDU2Server.Net.Handlers;

/// <summary>
/// {"op":"challup","session":..,"chall":{name,description,fee,reward,duration,postTo,data(.ig2 Base64)}} -> challId.
/// Challenge aus dem Race-Editor (GAME_MODE_EDITOR, Upload-Fenster) ins Community Racing Center stellen.
/// Fehler: not_logged_in, bad_file, too_big, max_posted (Film: "max_chall_posted").
/// </summary>
public sealed class ChallengeUploadHandler : RequestHandler
{
    public ChallengeUploadHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "challup";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var me = SessionOf(conn, r);
        if (me == null) return Fail("not_logged_in");
        var up = r.chall;
        if (up == null || string.IsNullOrEmpty(up.data)) return Fail("bad_file");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(up.data); } catch (FormatException) { return Fail("bad_file"); }
        var c = Ctx.Challenges.Add(me.Nick, bytes, up.name, up.description, up.fee, up.reward, up.duration, up.postTo, false, out string err);
        if (c == null) return Fail(err);
        Log.Info($"[CRC] {me.Nick} stellt Challenge #{c.id} \"{c.name}\" ein (Spielart {c.mode}, Gebühr {c.fee}, Belohnung {c.reward}, {c.duration} Tag(e))");
        return new NetResponse { ok = true, online = Online, challId = c.id };
    }
}

/// <summary>{"op":"challlist","session":..,"kind":"crc"|"community"|"","target":Insel (-1 = alle)} -> challenges (ohne Dateien).</summary>
public sealed class ChallengeListHandler : RequestHandler
{
    public ChallengeListHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "challlist";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var me = SessionOf(conn, r);
        if (me == null) return Fail("not_logged_in");
        return new NetResponse { ok = true, online = Online, challenges = Ctx.Challenges.List(r.kind ?? "", r.target) };
    }
}

/// <summary>
/// {"op":"challres","session":..,"target":id,"score":wertung,"kind":auto} -> challId, challenges[0] (Karte mit Bestwertung);
/// friendResult = "best" bei neuer Bestwertung. Wertung: Zeitarten ms (kleiner besser), Tempo/Radar km/h x 100 (größer besser).
/// </summary>
public sealed class ChallengeResultHandler : RequestHandler
{
    public ChallengeResultHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "challres";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var me = SessionOf(conn, r);
        if (me == null) return Fail("not_logged_in");
        bool best = Ctx.Challenges.Submit(r.target, me.Nick, r.score, r.kind, out string err);
        if (err.Length > 0) return Fail(err);
        var c = Ctx.Challenges.Get(r.target);
        Log.Info($"[CRC] {me.Nick} fährt Challenge #{r.target}: Wertung {r.score}{(best ? " – neue Bestwertung" : "")}");
        var resp = new NetResponse { ok = true, online = Online, challId = r.target, friendResult = best ? "best" : "" };
        if (c != null) resp.challenges.Add(Data.ChallengeEntry.Of(c));
        return resp;
    }
}

/// <summary>{"op":"challget","session":..,"target":id} -> challData (.ig2 Base64) + challenges[0] (Karte). Fehler: not_found.</summary>
public sealed class ChallengeGetHandler : RequestHandler
{
    public ChallengeGetHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "challget";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var me = SessionOf(conn, r);
        if (me == null) return Fail("not_logged_in");
        var data = Ctx.Challenges.Data(r.target);
        var c = Ctx.Challenges.Get(r.target);
        if (data == null || c == null) return Fail("not_found");
        var resp = new NetResponse { ok = true, online = Online, challId = c.id, challData = Convert.ToBase64String(data) };
        resp.challenges.Add(Data.ChallengeEntry.Of(c));
        return resp;
    }
}
