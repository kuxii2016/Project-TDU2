using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Flash;
using Paradise.Core.Online;
using Paradise.Game.World;

namespace Paradise.Game.Online;

/// <summary>
/// general.bnk als globales Overlay wie im Spiel (Unity TDU2GameData.UpdateGeneral):
///   saving_logo = Speichern (ab TDU2Session.SaveStarted, mindestens <see cref="SavingLogoMinSeconds"/>),
///   online_evt = Spieler online/offline, deco_pop = Verbindung verloren. Nur diese Clips (und pop_chat) bleiben sichtbar.
/// </summary>
public static class GeneralOverlay
{
    /// <summary>Speicher-Symbol mindestens so lange zeigen (Unity TDU2GameData.SavingLogoMinSeconds).</summary>
    public static float SavingLogoMinSeconds = 2f;

    static VmfPlayer? _general;
    static float _clock, _saveCloseAt = -1f, _evtCloseAt = -1f, _evtFreeAt, _districtCloseAt = -1f, _districtOpenAt = -1f;
    static volatile bool _saveRequested;
    static readonly Queue<(string nick, bool on)> _events = new();
    static TerrainModule? _terrain;
    static VehicleModule? _vehicles;
    /// <summary>general.bnk übernehmen (einmal nach dem Laden).</summary>
    public static void Attach(VmfPlayer? general, TerrainModule? ctx, VehicleModule? vehicles)
    {
        if (ctx != null)
        {
            _terrain = ctx;
        }
        if (vehicles != null)
        {
            _vehicles = vehicles;
        }
        if (general == null || ReferenceEquals(general, _general)) return;
        _general = general;
        general.SetValue("/:WhatPlatform", 1);
        general.GotoLabel("/", "default", false);
        // nur die benutzten Overlays sichtbar lassen
        if (general.Root != null)
            foreach (var d in general.Root.List.Values) d.Visible = d.Name == "deco_pop" || d.Name == "online_evt" || d.Name == "saving_logo" || d.Name == "pop_chat";
        general.GotoLabel("/pop_chat", "default", false);
        for (int i = 1; i <= 4; i++) general.GotoLabel("/pop_chat/log/line" + i, "default", false);
        TDU2Session.SaveStarted -= OnSaveStarted;
        TDU2Session.SaveStarted += OnSaveStarted;
    }

    static void OnSaveStarted() => _saveRequested = true;

    static bool _decoOpen;

    /// <summary>Verbindung verloren (deco_pop) – schließt mit Enter/Esc oder wenn die Verbindung zurück ist.</summary>
    public static void ShowDisconnected()
    {
        if (_general == null) return;
        _decoOpen = true;
        _general.GotoLabel("/deco_pop", "open", true);
    }

    /// <summary>deco_pop schließen (Unity: Enter/Esc bzw. wieder online).</summary>
    public static void CloseDisconnected()
    {
        if (!_decoOpen || _general == null) return;
        _decoOpen = false;
        if (!_general.CallClip("/deco_pop", "validDeco")) _general.GotoLabel("/deco_pop", "close", true);
    }

    public static bool DisconnectedOpen => _decoOpen;

    /// <summary>Meldung "Spieler online/offline" (online_evt) einreihen.</summary>
    public static void PlayerEvent(string nick, bool online) { if (string.IsNullOrEmpty(nick)) return; lock (_events) if (_events.Count < 8) _events.Enqueue((nick, online)); }

    public static string LastArea = "";

    /// <summary>Jedes Bild (ScreenModule).</summary>
    public static void Update(float dt)
    {
        _clock += dt;
        var g = _general;
        if (g == null) return;

        // Spieler online/offline
        if (_evtCloseAt > 0f && _clock >= _evtCloseAt)
        {
            _evtCloseAt = -1f;
            g.GotoLabel("/online_evt", "close", true);
            _evtFreeAt = _clock + 0.6f;
        }
        if (_evtCloseAt < 0f && _clock >= _evtFreeAt)
        {
            (string nick, bool on) e = default;
            bool has;
            lock (_events) { has = _events.Count > 0; if (has) e = _events.Dequeue(); }
            if (has)
            {
                g.SetVariable("/:OEType", e.on ? "player_on" : "player_off");
                g.SetVariable("/:OEPlayerName", e.nick);
                g.GotoLabel("/online_evt", "open", true);
                _evtCloseAt = _clock + 3.5f;
            }
        }

        if (_districtOpenAt > 0f && _clock >= _districtCloseAt)
        {
            _districtOpenAt = -1f;
            g.GotoLabel("/district_name", "close", true);
        }

        if (_saveRequested)
        {
            _saveRequested = false;
            if (_saveCloseAt < 0f) g.GotoLabel("/saving_logo", "open", true);
            _saveCloseAt = _clock + MathF.Max(0.5f, SavingLogoMinSeconds);
        }
        if (_saveCloseAt > 0f && _clock >= _saveCloseAt && !TDU2Session.Saving) { _saveCloseAt = -1f; g.GotoLabel("/saving_logo", "close", true); }

        var currentArea = CurrentArea() ?? (IsHawaii() ? "h1" : "i1");
        if (LastArea != currentArea)
        {
            if (_vehicles?.Manager.Current?.SpeedKmh >= 2)
            {
                _districtOpenAt = _clock;
                _districtCloseAt = _clock + 5f;
                g.GotoLabel("/district_name", "open", true);
                g.ForceVisible("/district_name", true);
                switch (currentArea)
                {
                    case "h1":
                        {
                            g.SetFrame("/district_name/icon", 10);
                            g.SetFrame("/district_name/bkgd", 6);
                            g.SetVariable("/:districtName", "Hawai Area 1");
                        }
                        break;
                    case "h2":
                        {
                            g.SetFrame("/district_name/icon", 15);
                            g.SetFrame("/district_name/bkgd", 6);
                            g.SetVariable("/:districtName", "Hawai Area 2");
                        }
                        break;
                    case "h3":
                        {
                            g.SetFrame("/district_name/icon", 20);
                            g.SetFrame("/district_name/bkgd", 6);
                            g.SetVariable("/:districtName", "Hawai Area 3");
                        }
                        break;
                    case "h4":
                        {
                            g.SetFrame("/district_name/icon", 25);
                            g.SetFrame("/district_name/bkgd", 6);
                            g.SetVariable("/:districtName", "Hawai Area 4");
                        }
                        break;
                    case "i1":
                        {
                            g.SetFrame("/district_name/icon", 34);
                            g.SetFrame("/district_name/bkgd", 1);
                            g.SetVariable("/:districtName", "Ibiza Area 1");
                        }
                        break;
                    case "i2":
                        {
                            g.SetFrame("/district_name/icon", 39);
                            g.SetFrame("/district_name/bkgd", 1);
                            g.SetVariable("/:districtName", "Ibiza Area 2");
                        }
                        break;
                }
                LastArea = currentArea;
            }
        }
    }

    static List<(string label, (float x, float z)[] poly)>? _zones;
    static string? _zonesFor;

    static bool IsHawaii() => (_terrain?.Streamer?.Index?.IslandName ?? "").StartsWith("h", StringComparison.OrdinalIgnoreCase);

    static void EnsureZones()
    {
        var idx = _terrain?.Streamer?.Index;
        if (idx == null || _zonesFor == idx.LevelDir) return;
        _zonesFor = idx.LevelDir;
        _zones = new();
        try
        {
            string? f = string.IsNullOrEmpty(idx.LevelDir) ? null : Path.Combine(idx.LevelDir, "cruisingzones.bin");
            if (f == null || !GameFiles.Exists(f)) return;
            var b = GameFiles.ReadAllBytes(f);
            var polys = new List<List<(float, float)>>();
            var cur = new List<(float, float)>();
            var vals = new List<float>();
            void Flush()
            {
                for (int i = 0; i + 1 < vals.Count; i += 2) cur.Add((vals[i], vals[i + 1]));
                vals.Clear();
                if (cur.Count >= 3) polys.Add(cur);
                cur = new();
            }

            for (int o = 36; o + 4 <= b.Length; o += 4)
            {
                uint u = BitConverter.ToUInt32(b, o);
                if (u == 0xEFBEADDEu) { if (vals.Count > 0) Flush(); continue; }
                vals.Add(BitConverter.ToSingle(b, o));
            }
            if (vals.Count > 0) Flush();
            string[] labels = IsHawaii() ? new[] { "h4", "h1", "h3", "h2" } : new[] { "i1", "i2" };
            for (int i = 0; i < polys.Count && i < labels.Length; i++) _zones.Add((labels[i], polys[i].ToArray()));
        }
        catch (Exception ex) { Log.Warn("cruisingzones.bin: " + ex.Message); }
    }


    static string? CurrentArea()
    {
        var gp = PlayerGamePos();
        return gp == null ? null : AreaAt(gp.Value.X, gp.Value.Z);
    }

    static System.Numerics.Vector3? PlayerGamePos()
    {
        var vm = _vehicles?.Manager;
        if (vm?.Current != null) return vm.PositionGame;
        var s = _terrain?.Streamer; var cam = _terrain?.Camera;
        return s != null && cam != null ? s.RenderToGame(cam.Position) : null;
    }

    static string? AreaAt(float px, float pz)
    {
        EnsureZones();
        if (_zones == null || _zones.Count == 0) return null;
        foreach (var z in _zones) if (InPolygon(z.poly, px, pz)) return z.label;
        // knapp außerhalb (Küste/Meer): nächstes Gebiet
        string? best = null; float bd = float.MaxValue;
        foreach (var z in _zones)
        {
            float cx = 0, cz = 0; foreach (var q in z.poly) { cx += q.x; cz += q.z; }
            cx /= z.poly.Length; cz /= z.poly.Length;
            float d = (cx - px) * (cx - px) + (cz - pz) * (cz - pz);
            if (d < bd) { bd = d; best = z.label; }
        }
        return best;
    }

    static bool InPolygon((float x, float z)[] poly, float px, float pz)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var a = poly[i]; var c = poly[j];
            if ((a.z > pz) != (c.z > pz) && px < (c.x - a.x) * (pz - a.z) / (c.z - a.z) + a.x) inside = !inside;
        }
        return inside;
    }
}
