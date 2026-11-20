#if WINDOWS
using Paradise.Editor;
using Paradise.Game.Online;
using System.Globalization;

namespace Paradise.Game.Screens;

/// <summary>
/// F1 &gt; Tools &gt; Open Editor: Szenen-Editor (Projekt Paradise.Editor) mit Hierarchy- und Inspector-Fenster wie in Unity.
/// Hier meldet das Spiel die Wurzeln der Hierarchie an: Welt (MapStreamer), eigenes Auto, Verkehr, Mitspieler-Autos.
/// Das ausgewählte Objekt wird im 3D-Bild über die DebugLines markiert (SceneEditor.Pump in DevMenu.Update).
/// </summary>
public sealed partial class DevMenu
{
    bool _editorRoots;

    void OpenEditor()
    {
        if (!_editorRoots)
        {
            _editorRoots = true;
            SceneEditor.RenderToGame = r => Streamer is { } s ? s.RenderToGame(r) : r;
            SceneEditor.CameraGame = () => Streamer?.ViewerGame ?? System.Numerics.Vector3.Zero;
            SceneEditor.SetRoot("world", () => Streamer is { } s ? SceneAdapters.World(s) : null);
            SceneEditor.SetRoot("player", EditorPlayerCar);
            SceneEditor.SetRoot("traffic", EditorTraffic);
            SceneEditor.SetRoot("remote", EditorRemoteCars);
        }
        SceneEditor.Open();
        Toast("Editor geöffnet (Hierarchy + Inspector)");
    }

    SceneNode? EditorPlayerCar()
    {
        var m = Mgr;
        var model = m?.Model;
        if (m == null || model == null) return null;
        string car = m.CurrentCar?.ModelName ?? "?";
        return SceneAdapters.Car("g:player", "Player Car (" + car + ")", "Eigenes Fahrzeug (VehicleManager)", model,
            () => ReferenceEquals(Mgr?.Model, model),
            b =>
            {
                b.Section("Eigenes Fahrzeug");
                b.Info("Auto", car);
                b.Info("Position (Spielraum)", m.PositionGame);
                b.Info("Fahrtrichtung", m.ForwardGame);
                b.Info("Farbe / Felge / Innenraum", m.ColorIndex + " / " + m.RimId + " / " + m.InteriorId);
                b.Info("Motor läuft", m.EngineRunning ? "ja" : "nein");
            });
    }

    SceneNode? EditorTraffic()
    {
        var t = _vehicles.Traffic;
        if (t == null) return null;
        return SceneAdapters.Group("g:traffic", "Traffic", () => t.EditorCars().Count(c => c.Model != null),
            () => t.EditorCars().Where(c => c.Model != null).Select(c =>
            {
                var model = c.Model!;
                uint id = c.Id;
                return SceneAdapters.Car("g:traffic/" + id.ToString(CultureInfo.InvariantCulture), c.Type + "  #" + id.ToString(CultureInfo.InvariantCulture),
                    "Verkehrsauto", model,
                    () => _vehicles.Traffic is { } tt && tt.EditorCars().Exists(x => x.Id == id && ReferenceEquals(x.Model, model)),
                    b =>
                    {
                        // aktuelle Werte bei jedem Snapshot neu holen
                        var cur = _vehicles.Traffic?.EditorCars().Find(x => x.Id == id);
                        b.Section("Verkehrsauto");
                        b.Info("Id", id.ToString(CultureInfo.InvariantCulture));
                        b.Info("Typ", c.Type);
                        if (cur is { } x && x.Id == id)
                        {
                            b.Info("Position (Spielraum)", x.Position);
                            b.Info("Tempo", (x.Speed * 3.6f).ToString("0", CultureInfo.InvariantCulture) + " km/h");
                            b.Info("Modus", x.Mode switch { 0 => "fährt", 1 => "Unfall (Physik)", 2 => "steht", _ => x.Mode.ToString(CultureInfo.InvariantCulture) });
                            b.Info("Gezeichnet", x.Visible ? "ja" : "nein");
                            b.Info("Eigenes (nicht vom Mitspieler)", x.Mine ? "ja" : "nein");
                        }
                    });
            }),
            b =>
            {
                var cars = t.EditorCars();
                b.Section("Verkehr");
                b.Info("Autos", cars.Count.ToString(CultureInfo.InvariantCulture));
                b.Info("Davon mit Modell", cars.Count(c => c.Model != null).ToString(CultureInfo.InvariantCulture));
                b.Info("Gezeichnet", cars.Count(c => c.Visible).ToString(CultureInfo.InvariantCulture));
            }, null, "Verkehr (Gameplay/Traffic)");
    }

    SceneNode? EditorRemoteCars()
    {
        if (!RemoteCars.All.Any()) return null;
        return SceneAdapters.Group("g:remote", "Remote Players", () => RemoteCars.All.Count(r => r.Model != null),
            () => RemoteCars.All.Where(r => r.Model != null).ToList().Select(r =>
            {
                var model = r.Model!;
                int id = r.Id;
                string car = r.Assets?.Car?.ModelName ?? "?";
                return SceneAdapters.Car("g:remote/" + id.ToString(CultureInfo.InvariantCulture), "Player " + id.ToString(CultureInfo.InvariantCulture) + " (" + car + ")",
                    "Mitspieler-Auto (RemoteCars)", model,
                    () => ReferenceEquals(RemoteCars.Get(id)?.Model, model),
                    b =>
                    {
                        b.Section("Mitspieler");
                        b.Info("Id", id.ToString(CultureInfo.InvariantCulture));
                        b.Info("Auto", car);
                        b.Info("Position (Spielraum)", r.VisPos);
                        b.Info("Gezeichnet", r.Visible ? "ja" : "nein");
                        b.Info("Kamera-Abstand", r.CamDistance.ToString("0.0", CultureInfo.InvariantCulture) + " m");
                    });
            }),
            null, null, "Mitspieler (Online/RemoteCars)");
    }
}
#endif
