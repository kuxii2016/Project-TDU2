using Paradise.Core.Avatars;
using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Flash;
using Paradise.Core.Online;
using Paradise.Game.Screens;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Kleidungsladen (Port Unity TDU2ClothesShop, ShopMode.Shop): Spots EAS_A..E, Interieurs ias_a..e, Verkäuferin = Auswahlpunkt
/// "Seller_area" Typ 22. Original-Film frontend/&lt;res&gt;/shopsavatar.bnk ab Label clothes_shop -> cl_in/cl_stop (menu_square,
/// _root.SelItemType "goodies") -> item_selection_in/_stop (menu_item: kaufen). FSCommands: came, prit/neit, buit (kaufen), seit,
/// moup, nsit, dbxo, csou/mcou/wmon/gofr/shko (verlassen). Kollektion aus der Verkäuferin (DBPNJ "Clothes seller Highlife" …),
/// sonst aus der Spot-Kategorie (spots.bin TypeA high byte 15..19). Gemeinsames mit der Garderobe: Gamemodes/Utils/AvatarShop.cs.
/// </summary>
public class GAME_MODE_GOODIES : AvatarShop
{
    public override Gamemode Mode => Gamemode.GAME_MODE_GOODIES;
    static readonly ScreenDef[] _screens = { ScreenDefs.Avatar };
    public override IReadOnlyList<ScreenDef> Screens => _screens;
    protected override ScreenDef Screen => ScreenDefs.Avatar;
    protected override bool IsShop => true;
    protected override string DefaultPose => "clot_standloop-m";

    int _brand;
    InteriorNpcs.Npc? _seller;
    /// <summary>Kinos der Laden-Bank (ias_*): C_LIFE beim Betreten, C_LOOP im Menü, C_BUY_ITEM nach einem Kauf (exe 0xAE8A3B, 0xAE8B50, 0xAE95A0).</summary>
    SpotCinematics? _cin;
    bool _bought, _outro;   // exe +0x1B8518: gekauft; Kauf-Kino läuft

    /// <summary>Auswahlpunkt Typ 22 in einem EAS-Laden (InteriorUtils.Activate): Laden öffnen; false = kein Kleidungsladen.</summary>
    internal static bool Open()
    {
        if (!(_spot?.Code ?? "").StartsWith("EAS", StringComparison.OrdinalIgnoreCase)) return false;
        if (!AvatarLibrary.Ready) { Log.Info("Kleidungsladen: Avatar-Daten werden noch geladen …"); return true; }
        if (_t == null || _scene == null) return true;
        GameMode.Set(Gamemode.GAME_MODE_GOODIES);
        return true;
    }

    protected override void OnOpen()
    {
        _seller = InteriorNpcs.First("Seller") ?? InteriorNpcs.First(null);
        // Kollektion wie das Original: DBCarShop._clothesshopbrand des Spots (0x39 Hiro Hisaishi EAS_A, 0x53 MYW Paris EAS_B,
        // 0x54 Practice Sportswear EAS_C, 0x55 B-Miami EAS_D, 0x57 Casino); ohne Zeile die Verkäuferin, sonst die Spot-Kategorie
        var db = Vm?.Database;
        var shop = db != null && _spot != null ? db.ShopForSpot(_spot, _t?.Streamer?.Index?.IslandName ?? "") : null;
        _brand = shop != null && shop.ClothesBrand > 0 ? shop.ClothesBrand : 0;
        if (_brand == 0) _brand = BrandFromSeller(_seller);
        if (_brand == 0) _brand = BrandFor(((_spot?.TypeA ?? 0) >> 8) & 0xFF);
        // Verkäuferin hinter die Theke (M_LOOP)
        if (_seller != null) { var loop = InteriorNpcs.FindActor(_seller.Hc, "LOOP"); if (loop != null) InteriorNpcs.SetPose(_seller.ConfigName, loop); }
        // exe 0xAE8A3B: C_LIFE als Hintergrund (Verkäuferin); der Spieler steht als Anprobe-Avatar im Laden, nicht im Kino
        _bought = false; _outro = false;
        ShopCinema.Close(ref _cin);
        _cin = ShopCinema.Open(AvatarConfigs.Local, castPlayer: false);
        _cin?.PlayBackground("C_LIFE", true);
    }

    protected override string OpenInfo() => ", Kollektion " + _brand + (_seller != null ? " (" + AvatarLibrary.PnjName(_seller.ConfigName) + ")" : "");

    protected override void StartFilm(VmfPlayer ui)
    {
        ui.GotoLabel("/", "clothes_shop", true);
        _cinAt = _clock + 0.6f;   // Init-Skripte von clothes_shop/clothes_WM, dann ins Menü (cl_in)
    }

    protected override void OnFrame(VmfPlayer ui)
    {
        if (_cinAt > 0f && _clock >= _cinAt)
        {
            _cinAt = -1f;
            // Laufmodus überspringen: was clothes_WM per Taste setzen würde
            ui.SetVariable("/:SelItemType", "goodies");
            ui.GotoLabel("/", "cl_in", true);
            ui.SetValue("/:Input", true);
        }
    }

    protected override void CloseState()
    {
        if (_open && _seller != null) InteriorNpcs.ResetPose(_seller);
        _seller = null;
        _bought = false; _outro = false;
        ShopCinema.Close(ref _cin);
        base.CloseState();
    }

    public override void Run(IGameModeContext ctx, in GameTime time)
    {
        if (_open && !ctx.Paused) ShopCinema.Update(_cin, time.DeltaTime);
        if (_outro && _open && !ctx.Paused)
        {
            // Kauf-Kino C_BUY_ITEM: Kamera des Kinos, danach zurück in den Raum
            _ctx = ctx;
            // überspringbar (Bestätigen/Zurück)
            if (_cin == null || !_cin.MainPlaying || ShopCinema.SkipPressed()) { _cin?.StopMain(); _outro = false; Close(); return; }
            ShopCinema.Camera(_cin, false);
            return;
        }
        base.Run(ctx, time);
    }

    public override void Render(IGameModeContext ctx, in GameTime time)
    {
        base.Render(ctx, time);
        var st = _t?.Streamer;
        if (_open && st != null) _cin?.Render(st.RootMatrix);
    }

    /// <summary>
    /// "csou" (Menü verlassen, exe 0xAE95A0): nach einem Kauf C_BUY_ITEM als Haupt-Kino mit dem Spieler-Darsteller (Anprobe-Avatar aus),
    /// sonst C_LIFE – hier wird ohne Kauf wie bisher geschlossen.
    /// </summary>
    protected override bool BeforeClose() => BuyCinematic();

    bool BuyCinematic()
    {
        if (!_bought || _cin == null || _cin.Find("C_BUY_ITEM") == null) return false;
        _bought = false;
        _cin.StopMain();
        _cin.StopBackground();
        _cin.CastPlayer = true;
        _cin.SetPlayer(AvatarConfigs.Local.Clone());
        _cin.PlayMain("C_BUY_ITEM");
        _preview?.Dispose(); _preview = null;
        if (_ui2 != null) { _ui2.Visible = false; _ui2.MouseEnabled = false; }
        _outro = true;
        return true;
    }

    protected override bool Command(string lc)
    {
        if (_outro) return true;   // Kauf-Kino läuft: mcou/gofr … schließen erst danach
        switch (lc)
        {
            case "buit": Buy(); return true;
            case "csin":   // exe 0xAE8B50: Hintergrund C_LOOP statt C_LIFE
                if (_cin != null && _cin.Find("C_LOOP") != null) { _cin.StopBackground(); _cin.PlayBackground("C_LOOP", true); }
                return true;
            case "csou":
            case "mcou":
            case "wmon":
            case "gofr":
            case "shko":
            case "avou":
            case "liin":
                Close(); return true;
        }
        return false;
    }

    /// <summary>Kollektion aus der Verkäuferin (DBPNJ "Clothes seller Highlife" …).</summary>
    static int BrandFromSeller(InteriorNpcs.Npc? s)
    {
        string n = s != null ? (AvatarLibrary.PnjName(s.ConfigName) ?? "") : "";
        if (n.Contains("Highlife", StringComparison.OrdinalIgnoreCase)) return BrandHighlife;
        if (n.Contains("Streetwear", StringComparison.OrdinalIgnoreCase)) return BrandStreetwear;
        if (n.Contains("Sportswear", StringComparison.OrdinalIgnoreCase)) return BrandSportswear;
        if (n.Contains("Fashion", StringComparison.OrdinalIgnoreCase)) return BrandFashion;
        if (n.Contains("DLC", StringComparison.OrdinalIgnoreCase)) return BrandEden;
        return 0;
    }

    /// <summary>Ersatz ohne Verkäuferin: Spot-Kategorie 15 Highlife, 16 Streetwear, 17 Sportswear, 18 Fashion, 19 Eden.</summary>
    static int BrandFor(int cat) => cat switch
    {
        15 => BrandHighlife,
        16 => BrandStreetwear,
        17 => BrandSportswear,
        18 => BrandFashion,
        19 => BrandEden,
        _ => 0,
    };

    /// <summary>Sortiment wie das Original: DBClothe-Zeilen der Marke des Ladens, Geschlecht des Spielers, ohne die $999.999-Standardteile.</summary>
    bool Sells(ClotheInfo c, int sex)
    {
        if (!c.Valid || c.IsNothing || _brand == 0) return false;
        if (sex == 1 ? !c.ForWoman : !c.ForMan) return false;
        if (!AvatarLibrary.HasBank(c) || c.Price >= 999999) return false;
        return c.Brand == _brand;
    }

    protected override List<Entry> BuildItems(string cam)
    {
        var list = new List<Entry>();
        int sex = _base.Sex;
        var parts = PartsFor(cam, sex);
        if (parts == null) return list;
        var found = new List<ClotheInfo>();
        foreach (var c in AvatarLibrary.AllClothes)
            if (Array.IndexOf(parts, c.Part) >= 0 && Sells(c, sex)) found.Add(c);
        if (found.Count == 0) return list;   // die Marke führt nichts davon: Kategorie gesperrt (SetCounts)
        found.Sort(Order);
        if (Removable(parts[0])) list.Add(new Entry { Item = null, Part = parts[0], Owned = true });
        foreach (var c in found)
        {
            int n = System.Math.Max(1, c.NbColors);
            for (int col = 0; col < n; col++)
                list.Add(new Entry { Item = c, Part = c.Part, Color = col, Owned = OwnsColor(c, col) });
        }
        return list;
    }

    void Buy()
    {
        var e = Current;
        // Der Film hat die Eingabe schon gesperrt (ValidateItemBuy: Input = false, Hilfe zu) -> in jedem Fall wieder herausführen
        _ui2?.SetValue("/:EnableItemBuyCin", false);
        _reopenAt = _clock + 0.9f;
        if (e == null) return;
        var sv = TDU2Session.Save;
        bool changed = false;
        if (!e.Owned && sv != null)
        {
            if (sv.money < e.Price) { Log.Warn("Kleidungsladen: nicht genug Geld"); return; }
            sv.money = System.Math.Max(0, sv.money - e.Price);
            changed = true;
            _bought = true;
        }
        // exe 0x66F7E7 / 0x687AC0: nur die gekaufte Farbe gilt als gekauft (ClothesSold |= 1 << Farbe)
        if (e.Item != null && sv != null && Paradise.Game.Utils.PlayerUtils.AddOwnedClotheColor(e.Item.Hash, e.Color, sv)) changed = true;
        Wear(e);
        // Kauf (Geld/Besitz) explizit speichern – Wear/SetLocal speichert bereits bei eingeloggter Session, aber das stellt Persistenz sicher.
        if (changed && sv != null && TDU2Session.LoggedIn) TDU2Session.SaveAsync();
        Log.Info("Kleidungsladen: gekauft " + (e.Item != null ? e.Item.File + " Farbe " + e.Color : "(ablegen)") + " für $" + (e.Owned ? 0 : e.Price));
        foreach (var x in _items) if (x.Item != null && e.Item != null && x.Item.Hash == e.Item.Hash && x.Color == e.Color) x.Owned = true;
        // Kauf-Kinematik (M_BUY_ITEM) gibt es nicht: nach der Bestätigungs-Animation das Stückmenü sauber schließen (Run)
    }
}
