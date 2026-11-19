using Paradise.Core.Avatars;
using Paradise.Core.Diagnostics;
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
        _brand = BrandFromSeller(_seller);
        if (_brand == 0) _brand = BrandFor(((_spot?.TypeA ?? 0) >> 8) & 0xFF);
        // Verkäuferin hinter die Theke (M_LOOP)
        if (_seller != null) { var loop = InteriorNpcs.FindActor(_seller.Hc, "LOOP"); if (loop != null) InteriorNpcs.SetPose(_seller.ConfigName, loop); }
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
        base.CloseState();
    }

    protected override bool Command(string lc)
    {
        switch (lc)
        {
            case "buit": Buy(); return true;
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

    bool Sells(ClotheInfo c, int sex, bool anyBrand)
    {
        if (!c.Valid || c.IsNothing) return false;
        if (sex == 1 ? !c.ForWoman : !c.ForMan) return false;
        if (!AvatarLibrary.HasBank(c)) return false;
        if (c.Brand == BrandPnj || c.Brand == BrandDefault || c.Brand == BrandHair || c.Brand == BrandCasino) return false;
        return anyBrand || c.Brand == _brand;
    }

    protected override List<Entry> BuildItems(string cam)
    {
        var list = new List<Entry>();
        int sex = _base.Sex;
        var parts = PartsFor(cam, sex);
        if (parts == null) return list;
        var owned = OwnedSet();
        foreach (bool anyBrand in new[] { false, true })
        {
            if (_brand == 0 && !anyBrand) continue;
            var found = new List<ClotheInfo>();
            foreach (var c in AvatarLibrary.AllClothes)
                if (Array.IndexOf(parts, c.Part) >= 0 && Sells(c, sex, anyBrand)) found.Add(c);
            if (found.Count == 0) continue;   // Kollektion führt nichts davon -> ganzes Sortiment
            found.Sort(Order);
            if (Removable(parts[0])) list.Add(new Entry { Item = null, Part = parts[0], Owned = true });
            foreach (var c in found)
            {
                int n = System.Math.Max(1, c.NbColors);
                for (int col = 0; col < n; col++)
                    list.Add(new Entry { Item = c, Part = c.Part, Color = col, Owned = owned.Contains(c.Hash) || Wears(_base, c) });
            }
            break;
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
        }
        if (e.Item != null && sv != null)
        {
            sv.ownedClothes ??= new List<int>();
            if (!sv.ownedClothes.Contains(e.Item.Hash)) { sv.ownedClothes.Add(e.Item.Hash); changed = true; }
        }
        Wear(e);
        // Kauf (Geld/Besitz) explizit speichern – Wear/SetLocal speichert bereits bei eingeloggter Session, aber das stellt Persistenz sicher.
        if (changed && sv != null && TDU2Session.LoggedIn) TDU2Session.SaveAsync();
        Log.Info("Kleidungsladen: gekauft " + (e.Item != null ? e.Item.File + " Farbe " + e.Color : "(ablegen)") + " für $" + (e.Owned ? 0 : e.Price));
        foreach (var x in _items) if (x.Item != null && e.Item != null && x.Item.Hash == e.Item.Hash) x.Owned = true;
        // Kauf-Kinematik (M_BUY_ITEM) gibt es nicht: nach der Bestätigungs-Animation das Stückmenü sauber schließen (Run)
    }
}
