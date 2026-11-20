using Paradise.Core.Online;
using CarUpgrades = Paradise.Core.Vehicles.CarUpgrades;
using VmfPlayer = Paradise.Core.Flash.VmfPlayer;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Infos zum Fahrzeug des Spielers für die Original-Filme (große Karte map.vmf, Challenge-Editor gmeditor.vmf /infos):
/// _root.player_car (Name), _root.player_group (Label von infos/group: A1–A7, B1–B5, C1–C4, mA1/2, mB1–3, mC1/2, sonst "empty"),
/// _root.cur_upgrade (Label von infos/upg: level0–level5, Tuningstufe), _root.money.
/// </summary>
public static class PlayerCarInfo
{
    /// <summary>Fahrzeuggruppe (Unity GroupLabel): DB _vehiclecategory -> Label.</summary>
    public static string GroupLabel(int cat)
    {
        if (cat >= 1 && cat <= 7) return "A" + cat;
        if (cat >= 9 && cat <= 13) return "B" + (cat - 8);
        if (cat >= 14 && cat <= 17) return "C" + (cat - 13);
        if (cat >= 18 && cat <= 19) return "mA" + (cat - 17);
        if (cat >= 20 && cat <= 22) return "mB" + (cat - 19);
        if (cat >= 23 && cat <= 24) return "mC" + (cat - 22);
        return "empty";
    }

    /// <summary>Gruppe des aktuellen Fahrzeugs ("empty" ohne Fahrzeug).</summary>
    public static string CurrentGroup(IGameModeContext ctx)
    {
        var car = ctx.Vehicles?.Manager?.Current?.Car;
        return GroupLabel(car?.Raw != null ? car.Raw.I("_vehiclecategory") : 0);
    }

    /// <summary>Geld, Tuningstufe, Gruppe und Name des aktuellen Fahrzeugs setzen; Rückgabe: Name (null ohne Fahrzeug).</summary>
    public static string? Apply(VmfPlayer ui, IGameModeContext ctx)
    {
        ui.SetVariable("/:money", TDU2Session.LoggedIn
            ? TDU2Session.Save.money.ToString("N0", Paradise.Core.Util.GermanFormat.Number) + " $" : "999 999 999 $");
        var car = ctx.Vehicles?.Manager?.Current?.Car;
        ui.SetVariable("/:cur_upgrade", CarUpgrades.LevelLabel(CarUpgrades.Get(car?.FileName).Global));   // Tuningstufe (Tuner)
        ui.SetVariable("/:player_group", GroupLabel(car?.Raw != null ? car.Raw.I("_vehiclecategory") : 0));
        if (car == null) return null;
        string name = ((car.Brand ?? "") + " " + car.ModelName).Trim();
        ui.SetVariable("/:player_car", name);
        return name;
    }
}
