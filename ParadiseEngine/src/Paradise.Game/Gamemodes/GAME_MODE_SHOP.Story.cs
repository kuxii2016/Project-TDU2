// Story-Schritt portiert aus dem Unity-Projekt (Runtime/Story/TDU2Story.cs: UsedCarDealerMode).
using Paradise.Core.Diagnostics;
using Paradise.Game.Screens;

namespace Paradise.Game.Gamemodes;

public partial class GAME_MODE_SHOP
{
    /// <summary>
    /// Schritt 7 "Used Car Dealer" (Original: Spielmodus SHOP am Gebrauchtwagenhändler ESTOL_B, Tipp 4033 "Ein Auto kaufen!"
    /// TUTO_BUY_A_CAR): Auto-Auswahl beim Händler (Startautos des Servers, wie bei neuen Konten), danach zurück in die freie Fahrt ->
    /// Schritt 8 "Tuto FROG".
    /// </summary>
    internal sealed class UsedCarDealerStep : StoryStep
    {
        public override bool BlocksInput => false;
        StoryScene? _ui;
        bool _chosen, _told, _pushed;
        float _t;

        public override void Enter()
        {
            Suspend();
            // Unity: TDU2GameMode.Push(SHOP). Hier erst, wenn das Händler-Interieur betreten ist – der Raum-Modus GAME_MODE_SHOP
            // (InteriorUtils) baut seine Oberfläche beim Betreten auf (EnterStarterDealer -> GameMode.Set(RoomMode)).
            _ui = CreateScene(new StoryScene.Setup { Bank = null, Spot = null, LoadSets = false, IntroUi = false });
            Story.StarterDealer(car => { _chosen = true; Log.Info("Geschichte: Auto gekauft – " + car?.FileName); });
        }

        public override void Tick(float dt)
        {
            _t += dt;
            _ui?.Update(dt);
            if (!_pushed && InteriorUtils.Inside && GameMode.Current == Gamemode.GAME_MODE_SHOP) { _pushed = true; GameMode.Push(Gamemode.GAME_MODE_SHOP); }
            if (!_told && _ui != null && _ui.Loaded && _t > 2f) { _told = true; _ui.ShowTutorial(StoryScene.Loc(4033)); }   // "Ein Auto kaufen!"
            if (_chosen && Manager.Current != null && !InteriorUtils.Inside) Story.Go(8);
        }

        public override void Exit()
        {
            _ui?.CloseTutorial();
            _ui?.Dispose(); _ui = null;
            if (_pushed) GameMode.Pop();
        }
    }
}
