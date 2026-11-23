using Paradise.Core.Avatars;
using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Game.Screens;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Garderobe (Port Unity TDU2ClothesShop ShopMode.Dressing + TDU2Interiors.OpenDressing): Auswahlpunkt "Dressing" Typ 10 im Haus
/// lädt das Ankleidezimmer interior/IDR_&lt;x&gt;.bnk (Hausstufe L0..L5 -> 0, A, B, C, D, F) und öffnet danach home.vmf ab
/// "dressing_in" (SelItemType "dressing", root_menu_stop Kleidung/Motorrad -> square_in/_stop -> item_selection_in/_stop, menu_item
/// WhatPage "dressing_ic": A = "seit" anziehen). Sortiment: gekaufte + getragene Stücke + Standard-Aussehen. Verlassen (avou/liin …)
/// führt zurück ins Wohnzimmer. Gemeinsames mit dem Kleidungsladen: Gamemodes/Utils/AvatarShop.cs.
/// </summary>
public class GAME_MODE_DRESSING : AvatarShop
{
    public override Gamemode Mode => Gamemode.GAME_MODE_DRESSING;
    static readonly ScreenDef[] _screens = { ScreenDefs.Home };
    public override IReadOnlyList<ScreenDef> Screens => _screens;
    protected override ScreenDef Screen => ScreenDefs.Home;
    protected override bool IsShop => false;
    protected override string DefaultPose => "dr_standdressing-m";

    /// <summary>Auswahlpunkt Typ 10 (InteriorUtils.Activate): Ankleidezimmer laden, danach den Modus öffnen.</summary>
    internal static void Open()
    {
        if (!AvatarLibrary.Ready) { Log.Info("Garderobe: Avatar-Daten werden noch geladen …"); return; }
        if (_t == null || _spot == null) return;
        string code = (_spot.Code ?? "").ToLowerInvariant();
        char lvl = code.Length > 5 && code.StartsWith("eph_", StringComparison.Ordinal) ? code[5] : '0';
        string letter = lvl switch { '1' => "a", '2' => "b", '3' => "c", '4' => "d", '5' => "f", _ => "0" };
        string? bank = Bank("idr_" + letter) ?? Bank("idr_0") ?? BankPrefix("idr_");
        if (bank == null) { Log.Warn("Garderobe: interior/idr_*.bnk fehlt"); return; }
        UpdateUiAim(null);
        _afterBuild = () => GameMode.Set(Gamemode.GAME_MODE_DRESSING);
        LoadScene(bank, Kind.Other, null);
        Log.Info("Garderobe: lade " + Path.GetFileNameWithoutExtension(bank));
    }

    protected override void StartFilm(VmfPlayer ui)
    {
        // Wie ein frisch geladener Film (Unity: eigene home.vmf-Instanz): der Wohnbereich blendet die Clips bis auf den Cursor aus
        foreach (var d in ui.Root.List.Values) d.Visible = true;
        // dressing_in setzt SelItemType "dressing", root_menu_stop zeigt Kleidung/Motorrad
        ui.GotoLabel("/", "dressing_in", true);
        ui.SetValue("/:Input", true);
    }

    protected override bool Command(string lc)
    {
        switch (lc)
        {
            case "mcou": return true;   // dressing_in meldet mcou beim Öffnen
            case "csou":
            case "wmon":
            case "gofr":
            case "shko":
            case "avou":
            case "liin":
                Close(); return true;
        }
        return false;
    }

    /// <summary>Unity BackFromDressing: zurück ins Wohnzimmer.</summary>
    protected override void AfterClose()
    {
        if (Inside && _spotBanks?.Living != null) LoadScene(_spotBanks.Living, Kind.Living, null);
    }

    /// <summary>Garderobe: gekaufte + getragene Stücke + das Standard-Aussehen, jeweils alle Farben.</summary>
    protected override List<Entry> BuildItems(string cam)
    {
        var list = new List<Entry>();
        int sex = _base.Sex;
        var parts = PartsFor(cam, sex);
        if (parts == null) return list;
        var have = OwnedSet();
        foreach (int h in _base.Items) have.Add(h);
        foreach (int h in AvatarConfig.Default(sex).Items) have.Add(h);
        var found = new List<ClotheInfo>();
        foreach (int h in have)
        {
            var c = AvatarLibrary.Clothe(h);
            if (c != null && !c.IsNothing && Array.IndexOf(parts, c.Part) >= 0 && (sex == 1 ? c.ForWoman : c.ForMan) && AvatarLibrary.HasBank(c)) found.Add(c);
        }
        found.Sort(Order);
        if (Removable(parts[0])) list.Add(new Entry { Item = null, Part = parts[0], Owned = true });
        foreach (var c in found)
        {
            // nur gekaufte Farben (ClothesSold) bzw. die getragene; Standardteile in allen Farben
            bool standard = AvatarConfig.Default(sex).Items.Contains(c.Hash);
            for (int col = 0; col < System.Math.Max(1, c.NbColors); col++)
                if (standard || OwnsColor(c, col)) list.Add(new Entry { Item = c, Part = c.Part, Color = col, Owned = true });
        }
        return list;
    }
}
