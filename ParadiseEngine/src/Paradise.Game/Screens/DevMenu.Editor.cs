#if WINDOWS
using Paradise.Core.Avatars;
using Paradise.Core.Platform;
using Paradise.Editor;
using Paradise.Game.Gamemodes;
using Paradise.Game.Online;
using System.Globalization;
using System.Numerics;

namespace Paradise.Game.Screens;

/// <summary>
/// F1 &gt; Tools &gt; Open Editor: Szenen-Editor (Projekt Paradise.Editor) mit Hierarchy- und Inspector-Fenster wie in Unity.
/// Hier meldet das Spiel die Wurzeln der Hierarchie an: Welt (MapStreamer), eigenes Auto, Verkehr, Mitspieler-Autos, Avatare.
/// Das ausgewählte Objekt wird im 3D-Bild über die DebugLines markiert (SceneEditor.Pump in DevMenu.Update).
/// Strg + Linksklick ins Spielfenster wählt das Objekt unter der Maus (TerrainModule.ScreenRay), "Fokus" fährt die
/// Free-fly-Kamera hin.
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
            SceneEditor.SetRoot("avatars", () => SceneAdapters.Avatars(DescribeAvatar));
            SceneEditor.FocusCamera = EditorFocusCamera;
        }
        SceneEditor.Open();
        Toast("Editor open (Hierarchy + Inspector)");
    }

    SceneNode? EditorPlayerCar()
    {
        var m = Mgr;
        var model = m?.Model;
        if (m == null || model == null) return null;
        string car = m.CurrentCar?.ModelName ?? "?";
        return SceneAdapters.Car("g:player", "Player Car (" + car + ")", "Own Vehicle (VehicleManager)", model,
            () => ReferenceEquals(Mgr?.Model, model),
            b =>
            {
                b.Section("Personal Vehicle");
                b.Info("Car", car);
                b.Info("Position (Range)", m.PositionGame);
                b.Info("Direction of travel", m.ForwardGame);
                b.Info("Color / Wheels / Interior", m.ColorIndex + " / " + m.RimId + " / " + m.InteriorId);
                b.Info("Engine Running", m.EngineRunning ? "yes" : "no");
            });
    }

    SceneNode? EditorTraffic()
    {
        var t = _vehicles.Traffic;
        if (t == null) return null;
        return SceneAdapters.Toggle(SceneAdapters.Group("g:traffic", "Traffic", () => t.EditorCars().Count(c => c.Model != null),
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
                            b.Info("Position (Range)", x.Position);
                            b.Info("Speed", (x.Speed * 3.6f).ToString("0", CultureInfo.InvariantCulture) + " km/h");
                            b.Info("Mode", x.Mode switch { 0 => "Driving", 1 => "Crash (Physik)", 2 => "Idle", _ => x.Mode.ToString(CultureInfo.InvariantCulture) });
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
            }, null, "Verkehr (Gameplay/Traffic)"),
            () => t.EditorCars().Exists(c => c.Model != null && c.Model.Visual.Active),
            v => { foreach (var c in t.EditorCars()) if (c.Model != null) c.Model.Visual.Active = v; });
    }

    SceneNode? EditorRemoteCars()
    {
        if (!RemoteCars.All.Any()) return null;
        return SceneAdapters.Toggle(SceneAdapters.Group("g:remote", "Remote Players", () => RemoteCars.All.Count(r => r.Model != null),
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
            null, null, "Mitspieler (Online/RemoteCars)"),
            () => RemoteCars.All.Any(r => r.Model != null && r.Model.Visual.Active),
            v => { foreach (var r in RemoteCars.All) if (r.Model != null) r.Model.Visual.Active = v; });
    }

    /// <summary>Namen der Avatare, die das Spiel kennt (Interieur-Figuren); sonst Standard (Fahrer/Beifahrer/Avatar).</summary>
    static (string Name, string Type)? DescribeAvatar(AvatarModel a)
    {
        foreach (var n in InteriorNpcs.Npcs)
            if (ReferenceEquals(n.Avatar, a))
                return (string.IsNullOrEmpty(n.Name) ? (string.IsNullOrEmpty(n.ConfigName) ? "Figur" : n.ConfigName) : n.Name, "Interieur-Figur (InteriorNpcs)");
        return null;
    }

    /// <summary>F im Editor: Free-fly an, Kamera aus der aktuellen Blickrichtung mit Abstand auf die Auswahl (Spielraum-Mitte).</summary>
    bool EditorFocusCamera(Vector3 centerGame, float radius)
    {
        var s = Streamer; var cam = _terrain.Camera;
        if (s == null || cam == null) return false;
        if (!_terrain.FreeFly) SetFreeFly(true);
        var target = s.GameToRender(centerGame);
        var dir = target - cam.Position;
        if (dir.LengthSquared() < 1e-4f) dir = cam.Forward;
        dir = Vector3.Normalize(dir);
        // nicht steiler als ~60° von oben/unten, damit der Blick waagrecht bleibt
        if (MathF.Abs(dir.Y) > 0.87f) { dir.Y = MathF.CopySign(0.87f, dir.Y); dir = Vector3.Normalize(dir); }
        float dist = MathF.Max(radius * 2.2f, 4f);
        cam.Position = target - dir * dist;
        cam.Yaw = MathF.Atan2(dir.X, dir.Z);
        cam.Pitch = Math.Clamp(MathF.Asin(Math.Clamp(dir.Y, -1f, 1f)), -1.5f, 1.5f);
        return true;
    }

    /// <summary>
    /// Jedes Bild (DevMenu.Update, vor SceneEditor.Pump): Strg + Linksklick ins Spielfenster → Objekt unter der Maus auswählen.
    /// Gefangene Maus (Fahrzeugkamera) → Bildmitte.
    /// </summary>
    void EditorUpdate()
    {
        if (!SceneEditor.ClickSelect) return;
        var input = _window.Input;
        if (!input.LeftMousePressed || !input.IsKeyDown(InputState.VK_CONTROL)) return;
        if (_r.Width <= 0 || _r.Height <= 0) return;
        float px = _r.Width * 0.5f, py = _r.Height * 0.5f;
        if (!input.CursorLocked)
        {
            // Mausposition aus FlashInput (Fensterpixel, y von unten), auf die Swapchain-Größe umgerechnet
            var mp = Paradise.Core.Flash.FlashInput.mousePosition;
            int sw = Paradise.Core.Flash.Screen.width, sh = Paradise.Core.Flash.Screen.height;
            if (sw > 0 && sh > 0)
            {
                px = mp.x * _r.Width / sw;
                py = (sh - mp.y) * _r.Height / sh;
            }
        }
        if (_terrain.ScreenRay(px, py, out var origin, out var dir)) SceneEditor.PickRay(origin, dir);
    }
}
#endif