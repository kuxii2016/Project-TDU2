using Paradise.Core.Flash;
using Paradise.Core.Online;
using Paradise.Game.Config;
using Paradise.Game.Gamemodes;
using FVec = Paradise.Core.Flash.Vector2;
using Vector3 = System.Numerics.Vector3;
using NVec2 = System.Numerics.Vector2;

namespace Paradise.Game.Online;

/// <summary>
/// Namen über den Mitspieler-Autos wie im Original: general.bnk Clips av_name_01..08 (Sprite 557) statt der Unity-OnGUI-Namen.
/// Nach unpacked.exe (selbst disassembliert):
///   * 0xd32ac0: das Spiel zählt die vorhandenen av_name_%02d (bis 32) und dupliziert av_name_01 für die fehlenden
///     (duplicateMovieClip, Name av_name_%02d, Tiefe 10000 + i). Engine: bis <see cref="OnlineConfig.MaxNameTags"/> (16 = Lobbygröße).
///   * 0xd33ec0 / 0xd338d0: Kandidaten = Mitspieler mit Auto, Lage = Auto + 1,0 m nach oben (0,5 m bei 0x41a3d9 – Bedingung
///     ungeklärt, hier Auto mit Fahrer), Bildschirmlage 0xd33600 (y − 0,056), nach Entfernung sortiert (0xd32c40).
///     Faktor 0x434937(Entfernung, 300, 50, 0.7, 1) = 1 bis 50 m, 0,7 ab 300 m; überdecken sich Schilder (0xd32f70), wird das
///     weiter entfernte bis auf 0,3 abgeblendet; unter 0,1 ausgeblendet. [vermutet: Faktor = Größe (_xscale/_yscale),
///     Faktor × Abblendung = _alpha – die beiden Vtable-Aufrufe 0x78/0x84 des Sprite-API sind nicht benannt]
///   * 0xd33080: Label (default/player/club1/club2/npc/club_member/friend) nur bei Wechsel, Text "av_name" = Spielername,
///     "level" mit Label der besten Kategorie und Variable GlobalLevel (Level &lt; 0 = aus), _x = −(Textlänge·0,5 + 18);
///     "distance" ab 0,1 km mit "%0.1f %s" (km, ×0,6215 bei Meilen), _x = Textlänge·0,5 + 2; "club_hierarchy" _x = −(Textlänge·0,5 + 65),
///     sichtbar nur im Club-Modus (GAME_MODE_CLUB) – Clubs gibt es in der Engine noch nicht: aus.
/// Labels (Bildnummer 1-basiert wie im Flash-Editor): player 8, club1 16, club2 23, club_member 53, friend 65.
/// Gegner im Rennen (Racing/RaceOpponents, Unity OnGUI-Namen): Label "npc", ohne Level, solange das Rennen läuft.
/// </summary>
public static class NameTags
{
    const string Prefix = "av_name_";
    const int FilmTags = 8;              // im Film vorhanden (av_name_01..08)
    const int CloneDepthBase = 10000;    // exe: duplicateMovieClip-Tiefe 10000 + i

    sealed class Tag
    {
        public string Path = "";
        public string Label = "";
        public string Nick = "";
        public string Dist = "";
        public int Level = int.MinValue;
        public bool Shown;
        public float TextLen;
    }

    static VmfPlayer? _general;
    static readonly List<Tag> _tags = new();
    static int _charId;

    sealed class Cand { public int Id; public string Nick = ""; public string Label = ""; public int Level; public float Dist; public FVec Pos; public float Factor; public float Alpha; public float W; }
    static readonly List<Cand> _cands = new();

    /// <summary>Jedes Bild (ScreenModule, nach RemoteCars).</summary>
    public static void Update(IGameModeContext ctx)
    {
        var g = GeneralOverlay.General;
        if (g == null || g.Root == null) return;
        if (!ReferenceEquals(g, _general)) Setup(g);
        if (_tags.Count == 0) return;

        _cands.Clear();
        var t = ctx.Terrain; var st = t?.Streamer; var cam = t?.Camera;
        bool view = t != null && st != null && cam != null && !t.MapView && !ctx.Paused && !InteriorUtils.Inside;
        bool show = view && OnlineConfig.ShowNames && TDU2Session.LoggedIn && !Multiplayer.Hidden && !Multiplayer.HideRemotes
                    && Screens.GameMode.Current == Screens.Gamemode.GAME_MODE_FREERIDE;
        if (view)
        {
            var camGame = st!.RenderToGame(cam!.Position);
            int sh = System.Math.Max(1, ctx.Vehicles?.Renderer.Height ?? Screen.height);
            // Gegner im Rennen (Unity RaceOpponents.OnGUI): Lage über dem Dach, Label "npc"
            foreach (var (anchor, name) in Paradise.Game.Racing.RaceOpponents.Tags())
            {
                float dist = Vector3.Distance(camGame, anchor);
                if (dist > 250f || !t!.WorldToScreen(System.Numerics.Vector3.Transform(anchor, st.RootMatrix), out NVec2 px)) continue;
                if (px.X < -200f || px.Y < -100f || px.X > (ctx.Vehicles?.Renderer.Width ?? Screen.width) + 200f || px.Y > sh + 100f) continue;
                var local = g.GuiToLocal("/", new FVec(px.X, px.Y));
                if (!local.HasValue) continue;
                float f = Remap(dist, 300f, 50f, 0.7f, 1f);
                _cands.Add(new Cand { Id = -1, Nick = name, Dist = dist, Pos = local.Value, Factor = f, Alpha = f, Label = "npc", Level = -1 });
            }
            foreach (var r in RemoteCars.All)
            {
                if (!show) break;
                if (r.Model == null || !r.Visible || !r.HasVis) continue;
                // Lage: Auto + 1,0 m nach oben (Fahrzeug-Hochachse)
                var up = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, r.VisRot);
                var anchor = r.VisPos + up * 1.0f;
                float dist = Vector3.Distance(camGame, anchor);
                if (!t!.WorldToScreen(System.Numerics.Vector3.Transform(anchor, st.RootMatrix), out NVec2 px)) continue;
                // hinter der Kamera: WorldToScreen = false; außerhalb des Bildes: unten
                px.Y -= 0.056f * sh;   // exe 0xd33645: y + (−0,056)
                if (px.X < -200f || px.Y < -100f || px.X > (ctx.Vehicles?.Renderer.Width ?? Screen.width) + 200f || px.Y > sh + 100f) continue;
                var local = g.GuiToLocal("/", new FVec(px.X, px.Y));
                if (!local.HasValue) continue;
                TDU2GameData.Players.TryGetValue(r.Id, out var p);
                string nick = p != null && !string.IsNullOrEmpty(p.Nick) ? p.Nick : "Spieler " + r.Id;
                float f = Remap(dist, 300f, 50f, 0.7f, 1f);
                _cands.Add(new Cand
                {
                    Id = r.Id, Nick = nick, Dist = dist, Pos = local.Value, Factor = f, Alpha = f,
                    Label = Friends.IsFriend(nick) ? "friend" : "player",
                    Level = p?.Level ?? -1,
                });
            }
            // nach Entfernung (nah zuerst), höchstens so viele wie Schilder
            _cands.Sort((a, b) => a.Dist.CompareTo(b.Dist));
            if (_cands.Count > _tags.Count) _cands.RemoveRange(_tags.Count, _cands.Count - _tags.Count);
        }

        // Schilder belegen (Unity-Reihenfolge egal: das Original füllt nach Entfernung)
        for (int i = 0; i < _tags.Count; i++)
        {
            var tag = _tags[i];
            var d = g.Find(tag.Path);
            if (d == null) continue;
            if (i >= _cands.Count) { if (tag.Shown || d.Visible) { d.Visible = false; tag.Shown = false; } continue; }
            Apply(g, d, tag, _cands[i]);
        }
        // Überdeckung: weiter entfernte Schilder (hinten in der Liste) abblenden, die ein näheres überlappen (exe 0xd34130)
        for (int i = _cands.Count - 1; i >= 1; i--)
        {
            var c = _cands[i];
            for (int j = 0; j < i; j++)
            {
                float ov = Overlap(_cands[j], c);
                if (ov >= 0.1f) continue;
                float k = 0.3f + 0.7f * System.Math.Clamp(ov / 0.1f, 0f, 1f);
                c.Alpha *= System.Math.Clamp(k, 0.3f, 1f);
            }
        }
        for (int i = 0; i < _cands.Count && i < _tags.Count; i++)
        {
            var d = g.Find(_tags[i].Path);
            if (d == null) continue;
            var c = _cands[i];
            bool vis = c.Alpha >= 0.1f;
            d.Visible = vis;
            _tags[i].Shown = vis;
            d.Cx = new ColorTransform { Mul = new Vector4(1f, 1f, 1f, System.Math.Clamp(c.Alpha, 0f, 1f)), Add = Vector4.zero };
            d.ScriptCx = true;
        }
    }

    /// <summary>exe 0x434937: lineare Abbildung von [a, b] auf [c, d], begrenzt.</summary>
    static float Remap(float x, float a, float b, float c, float d)
    {
        if (MathF.Abs(b - a) < 1e-6f) return d;
        float k = System.Math.Clamp((x - a) / (b - a), 0f, 1f);
        return c + (d - c) * k;
    }

    /// <summary>
    /// exe 0xd32f70: Abstand zweier Schilder (Bühnenkoordinaten, Breite = Textlänge, Höhe 22) – 0 bei Überdeckung, sonst Länge des
    /// Abstandsvektors der Rechtecke; hier normiert auf die Bühnenbreite 1280 (Schwelle 0,1 wie im Original auf normierte Werte).
    /// </summary>
    static float Overlap(Cand a, Cand b)
    {
        float ax0 = a.Pos.x - a.W * 0.5f * a.Factor, ax1 = a.Pos.x + a.W * 0.5f * a.Factor, ay0 = a.Pos.y - 11f * a.Factor, ay1 = a.Pos.y + 11f * a.Factor;
        float bx0 = b.Pos.x - b.W * 0.5f * b.Factor, bx1 = b.Pos.x + b.W * 0.5f * b.Factor, by0 = b.Pos.y - 11f * b.Factor, by1 = b.Pos.y + 11f * b.Factor;
        float dx = ax1 < bx0 ? bx0 - ax1 : bx1 < ax0 ? ax0 - bx1 : 0f;
        float dy = ay1 < by0 ? by0 - ay1 : by1 < ay0 ? ay0 - by1 : 0f;
        return MathF.Sqrt(dx * dx + dy * dy) / VmfMovie.StageWidth;
    }

    static void Apply(VmfPlayer g, VmfPlayer.DisplayObject d, Tag tag, Cand c)
    {
        string p = tag.Path;
        // Label nur bei Wechsel (exe vergleicht den gemerkten Zustand)
        if (tag.Label != c.Label && g.GotoLabel(p, c.Label, false)) tag.Label = c.Label;
        if (tag.Nick != c.Nick)
        {
            tag.Nick = c.Nick;
            g.SetVariable(p + ":av_name", c.Nick);
            tag.TextLen = g.MeasureVarText(p + ":av_name", "av_name");
        }
        c.W = MathF.Max(40f, tag.TextLen);
        float half = tag.TextLen * 0.5f;
        // Level (Spielerlevel): GlobalLevel im Clip "level", Kategorie-Label "default"; < 0 = aus (exe)
        if (tag.Level != c.Level)
        {
            tag.Level = c.Level;
            if (c.Level >= 0)
            {
                g.GotoLabel(p + "/level", "default", false);
                g.SetVariable(p + "/level:GlobalLevel", c.Level.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            g.SetVisible(p + "/level", c.Level >= 0);
        }
        SetX(g, p + "/level", -(half + 18f));
        // Entfernung (km) ab 0,1 km
        float km = c.Dist / 1000f;
        bool showDist = km >= 0.1f;
        string ds = showDist ? km.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " km" : "";
        if (tag.Dist != ds)
        {
            tag.Dist = ds;
            if (showDist) g.SetVariable(p + "/distance:distance", ds);
            g.SetVisible(p + "/distance", showDist);
        }
        SetX(g, p + "/distance", half + 2f);
        // Clubs: noch nicht in der Engine
        g.SetVisible(p + "/club_hierarchy", false);
        SetX(g, p + "/club_hierarchy", -(half + 65f));
        // Lage und Größe
        d.Position = c.Pos;
        d.ScaleMul = new FVec(c.Factor, c.Factor);
    }

    static void SetX(VmfPlayer g, string path, float x)
    {
        var d = g.Find(path);
        if (d == null) return;
        var m = d.Matrix;
        d.Position = new FVec(x, d.Position?.y ?? m.Ty);
    }

    /// <summary>general.bnk übernommen: vorhandene Schilder sammeln, fehlende wie das Original duplizieren, alle ausblenden.</summary>
    static void Setup(VmfPlayer g)
    {
        _general = g;
        _tags.Clear();
        var first = g.Find("/" + Prefix + "01");
        if (first == null) { Paradise.Core.Diagnostics.Log.Warn("Namensschilder: general.bnk ohne /av_name_01"); return; }
        _charId = first.CharId;
        int max = System.Math.Clamp(OnlineConfig.MaxNameTags, 1, 32);
        for (int i = 1; i <= max; i++)
        {
            string name = Prefix + i.ToString("00");
            var d = g.Find("/" + name);
            if (d == null)
            {
                if (i <= FilmTags) continue;
                d = g.AttachSprite("/", _charId, name, CloneDepthBase + i);
                if (d == null) continue;
                // duplicateMovieClip übernimmt die Lage von av_name_01
                d.Matrix = first.Matrix;
            }
            d.Visible = false;
            _tags.Add(new Tag { Path = "/" + name });
        }
        Paradise.Core.Diagnostics.Log.Info($"Namensschilder: {_tags.Count} (av_name_01..{_tags.Count:00}, Sprite {_charId})");
    }
}
