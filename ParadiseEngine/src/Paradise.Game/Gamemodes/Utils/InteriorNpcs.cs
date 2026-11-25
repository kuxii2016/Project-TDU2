using Paradise.Core.Avatars;
using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;
using Paradise.Core.Render;
using Paradise.Core.World;
using System.Diagnostics;
using System.Numerics;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Figuren eines Interieurs (Port Unity TDU2InteriorNpcs; Entity__PNJ: Verkäuferin, Friseur …) mit ihrem DBPNJ-Aussehen
/// (ConfigName = DBPNJ._configid). Platz und Pose aus der Cinematic-Szene, deren Darsteller die Figur bindet (__Hc) – die "LIFE"-Szene
/// (Laufmodus); Figuren nur in Cinematics (z.B. Maklerin) stehen nicht im Raum. Läden stellen Figuren um (<see cref="SetPose"/>).
/// Dazu die .sce der Bank (<see cref="Cines"/>) und ihre Clips (<see cref="Clip"/>). Gemeinsam für alle Interieur-Modi (InteriorUtils).
/// </summary>
public static class InteriorNpcs
{
    public sealed class Npc
    {
        public string ConfigName = "", Name = "";
        public ulong Hc;
        /// <summary>Lage im Szenenrahmen (Spielraum der Interieur-Szene).</summary>
        public Matrix4x4 Pose = Matrix4x4.Identity;
        public AvatarModel? Avatar;
        public AnmClip? Clip;
        public float T;
    }

    public static readonly List<Npc> Npcs = new();
    /// <summary>Figuren anzeigen (Entwicklermenü).</summary>
    public static bool Show = true;
    /// <summary>__Hc der Figuren, die gerade eine Cinematic spielt (SpotCinematics zeichnet sie dort) – hier nicht zusätzlich zeichnen.</summary>
    public static HashSet<ulong>? Suppressed;
    public static Dictionary<string, CineScene> Cines { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public static BnkArchive? Bank { get; private set; }
    static readonly Dictionary<string, AnmClip?> _clips = new(StringComparer.OrdinalIgnoreCase);
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _last;

    /// <summary>Clip der Interieur-Bank (sonst avatarsdriver.bnk).</summary>
    public static AnmClip? Clip(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_clips.TryGetValue(name, out var c)) return c;
        c = CineScene.LoadClip(Bank, name) ?? AvatarLibrary.Clip(name);
        _clips[name] = c;
        return c;
    }

    /// <summary>Darsteller, der die Figur spielt, in der ersten Szene, deren Name <paramref name="sceneContains"/> enthält.</summary>
    public static CineScene.Obj? FindActor(ulong hc, string? sceneContains)
    {
        foreach (var kv in Cines)
        {
            if (sceneContains != null && kv.Key.IndexOf(sceneContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
            foreach (var o in kv.Value.Objects) if (!o.IsCamera && o.Bind == hc && o.HasPosition) return o;
        }
        return null;
    }

    /// <summary>Kamera-/Darsteller-Objekt mit diesem Namen in irgendeiner Szene der Bank.</summary>
    public static CineScene.Obj? FindObject(string name, string? sceneContains = null)
    {
        foreach (var kv in Cines)
        {
            if (sceneContains != null && kv.Key.IndexOf(sceneContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var o = kv.Value.Find(name);
            if (o != null) return o;
        }
        return null;
    }

    /// <summary>Beim Aufbau des Interieurs (InteriorUtils.BuildInterior): Szenen der Bank lesen, Figuren aufstellen.</summary>
    public static void Build(IRenderer r, InteriorScene? data, BnkArchive? bank)
    {
        Clear();
        Bank = bank;
        Cines = CineScene.LoadAll(Bank);
        if (data == null || !AvatarLibrary.Ready) return;
        foreach (var e in data.Npcs)
        {
            string cfgName = e.Node?.S("ConfigName", "") ?? "";
            var cfg = AvatarLibrary.PnjConfig(cfgName);
            if (cfg == null) { Log.Debug("Interieur-Figur ohne DBPNJ: " + e.Name + " (" + cfgName + ")"); continue; }
            // Nur Figuren mit einer Laufmodus-Szene (M_LIFE) stehen ständig im Raum; die übrigen treten nur in Cinematics auf
            var life = FindActor(e.Hc, "LIFE");
            if (life == null) { Log.Warn("Interieur-Figur " + e.Name + " (" + cfgName + ") nur in Cinematics – nicht aufgestellt"); continue; }
            var npc = new Npc { ConfigName = cfgName, Name = e.Name, Hc = e.Hc };
            Place(npc, life, life.Clip);
            npc.Avatar = AvatarModel.Create(r, cfg);
            npc.Avatar.Indoor = true;
            Npcs.Add(npc);
        }
        if (Npcs.Count > 0) Log.Info("Interieur: " + Npcs.Count + " Figur(en) aufgestellt");
    }

    static void Place(Npc npc, CineScene.Obj act, string? clip)
    {
        npc.Pose = act.Matrix;
        var c = Clip(clip);
        if (c != null && c != npc.Clip) { npc.Clip = c; npc.T = 0f; }
    }

    /// <summary>Figur (ConfigName enthält <paramref name="configContains"/>, null = erste) an einen Darsteller-Platz stellen.</summary>
    public static Npc? SetPose(string? configContains, CineScene.Obj? act, string? clip = null)
    {
        foreach (var n in Npcs)
        {
            if (configContains != null && n.ConfigName.IndexOf(configContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (act != null) Place(n, act, clip ?? act.Clip);
            return n;
        }
        return null;
    }

    /// <summary>Figur zurück in ihre Laufmodus-Pose (LIFE-Szene).</summary>
    public static void ResetPose(Npc? n)
    {
        if (n == null) return;
        var act = FindActor(n.Hc, "LIFE") ?? FindActor(n.Hc, null);
        if (act != null) Place(n, act, act.Clip);
    }

    public static Npc? First(string? configContains)
    {
        foreach (var n in Npcs) if (configContains == null || n.ConfigName.IndexOf(configContains, StringComparison.OrdinalIgnoreCase) >= 0) return n;
        return null;
    }

    public static void Clear()
    {
        foreach (var n in Npcs) n.Avatar?.Dispose();
        Npcs.Clear();
        _clips.Clear();
        Cines = new Dictionary<string, CineScene>(StringComparer.OrdinalIgnoreCase);
        Bank = null;
    }

    /// <summary>Jedes Bild: Clip schleifen und zeichnen. <paramref name="sceneToRender"/> = Szenenrahmen -> Render-Raum.</summary>
    public static void Render(Matrix4x4 sceneToRender)
    {
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)System.Math.Clamp(now - _last, 0.0, 0.1);
        _last = now;
        foreach (var n in Npcs)
        {
            var a = n.Avatar;
            if (a == null) continue;
            if (Suppressed != null && Suppressed.Contains(n.Hc)) continue;
            a.Update();
            if (!a.Ready || a.Rig == null) continue;
            a.Visible = Show;
            n.T += dt;
            a.Rig.BeginPose();
            if (n.Clip != null)
            {
                a.Rig.ApplyClipLoop(n.Clip, n.T, 1f, true);   // Schleife mit Überblendung (kein Sprung Ende -> Anfang)
            }
            a.Draw(n.Pose * sceneToRender);
        }
    }
}
