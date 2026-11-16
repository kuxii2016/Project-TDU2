using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;
using Paradise.Core.Vehicles;
using Paradise.Game.Screens;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Testfahrt beim Autohändler – Port von Unity TDU2CarDealer.cs (Testfahrt): tstk -> mit der gewählten Ausstattung draußen fahren
/// (wie Freifahrt: HUD, Pausemenü, Karte), Restzeit im HUD (/HUD/time_cd), danach zurück in den Händler (Original: 2 Minuten).
/// </summary>
public class GAME_MODE_TESTCAR : GAME_MODE_FREERIDE
{
    public override Gamemode Mode => Gamemode.GAME_MODE_TESTCAR;



    // ================================================================== Testfahrt
    /// <summary>Dauer der Testfahrt beim Autohändler (Sekunden Spielzeit, Original: 2 Minuten).</summary>
    internal static float TestDriveSeconds = 120f;
    /// <summary>true während einer Testfahrt (Leihwagen wird nicht gespeichert).</summary>
    internal static bool TestDriveActive { get; private set; }
    internal static float _testEnd; internal static Spot? _testSpot; internal static CarPhysics? _testCar, _testPrev; internal static bool _testHud; internal static int _testLastSec = -1;

    internal static void StartTestDrive()
    {
        var dc = InteriorUtils._dealerCar;
        if (dc?.Car == null || InteriorUtils._spot == null) return;
        var car = dc.Car; var spot = InteriorUtils._spot; var prev = InteriorUtils._lastCar; var choice = GAME_MODE_SHOP.Choice();
        InteriorUtils.EndDealerMenu();
        CarChoices.SetTemporary(car.FileName, choice);
        TestDriveActive = true;
        _testCar = car; _testSpot = spot; _testPrev = prev;
        _testEnd = InteriorUtils._time + MathF.Max(10f, TestDriveSeconds);
        _testHud = false; _testLastSec = -1;
        Log.Info($"Testfahrt: {car.FileName} ({choice}), {TestDriveSeconds:0} s");
        InteriorUtils.ExitInterior(car);
        InteriorUtils._lastCar = prev;   // ExitInterior merkt sich sonst den Leihwagen
    }

    /// <summary>Jedes Bild draußen: Restzeit im HUD, danach zurück zum Händler.</summary>
    internal static void UpdateTestDrive()
    {
        if (!TestDriveActive) return;
        float left = _testEnd - InteriorUtils._time;
        var h = InteriorUtils.Vm?.Minimap?.Hud;
        if (h != null)
        {
            if (!_testHud) { _testHud = true; h.SetVisible("/HUD/time_cd", true); h.GotoLabel("/HUD/time_cd", "open", true); }
            float s = MathF.Max(0f, left);
            int ms = (int)(s * 1000f);
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            h.SetVariable("/:MinCd", (ms / 60000).ToString(ci));
            h.SetVariable("/:SecCd", ((ms / 1000) % 60).ToString("00", ci));
            h.SetVariable("/:CentCd", ((ms / 10) % 100).ToString("00", ci));
            int sec = (int)MathF.Ceiling(s);
            if (sec <= 5 && sec >= 1 && sec != _testLastSec)
            {
                if (_testLastSec < 0) h.GotoLabel("/HUD/time_out", "on", true);
                _testLastSec = sec;
                h.SetVariable("/:time_out", sec.ToString(ci));
            }
        }
        if (left > 0f || InteriorUtils.Inside || (InteriorUtils.Vm?.IsLoadingCar ?? false)) return;
        var spot = _testSpot; var prev = _testPrev;
        StopTestDrive();
        if (spot != null && InteriorUtils.EnterSpot(spot))
        {
            InteriorUtils._lastCar = prev ?? InteriorUtils._lastCar;
            GameMode.Set(InteriorUtils.RoomMode);   // Händler-Modus: dort läuft UpdateInside (Szene laden, Ladebildschirm beenden)
        }
    }

    /// <summary>Testfahrt beenden (HUD aus, Leihwagen-Ausstattung verwerfen).</summary>
    internal static void StopTestDrive()
    {
        if (!TestDriveActive) return;
        TestDriveActive = false;
        if (_testCar != null) CarChoices.ClearTemporary(_testCar.FileName);
        var h = InteriorUtils.Vm?.Minimap?.Hud;
        if (h != null && _testHud)
        {
            h.GotoLabel("/HUD/time_cd", "default", false);
            h.GotoLabel("/HUD/time_out", "off", false);
        }
        _testHud = false; _testCar = null; _testSpot = null;
    }
}
