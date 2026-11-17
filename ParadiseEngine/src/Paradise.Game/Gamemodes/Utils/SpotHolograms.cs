using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using Paradise.Game.Config;
using System.Numerics;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Schwebende Spot-Hologramme der Freifahrt (docs spot_holograms_spec): scenarics/packs/pack_spots.bnk, entities.xmb
///   ArrayOf_Entity__Actor__SpotMesh (56 Symbol-Tafeln, z.B. CD_MERC, Playerhouse, Car_Wash) und
///   ArrayOf_Entity__Actor__SpotMeshAddOn (8 Rahmen mit Pulsringen und Zeiger, z.B. Circle_Orange_Frame),
///   mesh[0].rsc -> Szene spots_holograms_&lt;hash&gt;.3DD/.3DG/.2DM (Material-Shader TD_Gouraud, Pulsringe mit .uva-Lauf).
/// Platzierung: Anker = Einfahrt des Spots (spots.bin +0x28 = Position2), Maßstab 1, Tafel und Rahmen drehen sich um Y zur
/// Kamera; im Umkreis <see cref="HologramConfig.Radius"/> die <see cref="HologramConfig.MaxCount"/> nächsten.
/// Zuordnung Spot -> Tafel/Rahmen nach dem Code wie die Spot-Symbole (Minimap.SpotLabel); die Type-Bedingung der entities.xmb, mit
/// der das Original wählt, ist nicht entschlüsselt (Spec). Noch offen: Zeiger-Neigung zur Tür (Spec 2, ±0,61 rad), SignificanceLevel.
/// </summary>
internal static class SpotHolograms
{
    sealed class Instance
    {
        public Spot Spot = null!;
        public readonly List<(MapStreamer.SceneHandle H, List<(RenderObject O, Matrix4x4 Base, Vector3 Min, Vector3 Max, Vector3 Center)> Parts)> Scenes = new();
        /// <summary>Seitlicher Zeiger des Rahmens (zeigt zur Tür), gespiegelt wenn die Tür auf der anderen Seite liegt.</summary>
        public readonly HashSet<RenderObject> SideArrows = new();
        public bool Mirror;
        public bool Shown;
    }

    static MapStreamer? _st;
    static Task<(BankModel model, Dictionary<string, ObjectSet> sets)?>? _load;
    static BankModel? _model;
    static Dictionary<string, ObjectSet>? _sets;   // Entitätsname (CD_MERC, Square_Red_Frame …) -> Szene
    static readonly Dictionary<Spot, Instance?> _inst = new();
    static bool _failed;

    static string? PackPath => Paradise.Core.Config.GameData.BnkDir is { } d ? CarDatabase.FindFile(Path.Combine(d, "scenarics", "packs"), "pack_spots.bnk") : null;

    /// <summary>Jedes Bild der Freifahrt.</summary>
    public static void Update(IGameModeContext ctx)
    {
        var st = ctx.Terrain?.Streamer; var cam = ctx.Terrain?.Camera;
        if (!HologramConfig.Enabled || st == null || cam == null || st.Spots == null) { Hide(); return; }
        if (!ReferenceEquals(st, _st)) Reset(st);   // Inselwechsel: neuer MapStreamer
        if (_failed) return;
        if (_sets == null)
        {
            if (_load == null)
            {
                string? path = PackPath;
                if (path == null) { _failed = true; Log.Warn("Hologramme: scenarics/packs/pack_spots.bnk fehlt"); return; }
                var s0 = st;
                _load = Task.Run(() => Load(s0, path));
                return;
            }
            if (!_load.IsCompleted) return;
            var r = _load.Status == TaskStatus.RanToCompletion ? _load.Result : null;
            _load = null;
            if (r == null) { _failed = true; return; }
            _model = r.Value.model; _sets = r.Value.sets;
        }

        var eye = st.RenderToGame(cam.Position);
        // die nächsten Spots im Umkreis
        var near = new List<(Spot s, float d)>();
        float r2 = HologramConfig.Radius * HologramConfig.Radius;
        foreach (var s in st.Spots.Spots)
        {
            var a = Anchor(s);
            float dx = a.X - eye.X, dz = a.Z - eye.Z, d2 = dx * dx + dz * dz;
            if (d2 <= r2 && Plate(s, out _) != null) near.Add((s, d2));
        }
        near.Sort((x, y) => x.d.CompareTo(y.d));
        if (near.Count > HologramConfig.MaxCount) near.RemoveRange(HologramConfig.MaxCount, near.Count - HologramConfig.MaxCount);

        foreach (var kv in _inst) if (kv.Value != null) kv.Value.Shown = false;
        foreach (var (s, _) in near)
        {
            if (!_inst.TryGetValue(s, out var inst)) _inst[s] = inst = Build(st, s);
            if (inst == null) continue;
            inst.Shown = true;
            var a = Anchor(s);
            var to = eye - a;
            float yaw = to.X * to.X + to.Z * to.Z > 1e-4f ? MathF.Atan2(to.X, to.Z) : s.Heading2;
            if (HologramConfig.FlipFacing) yaw += MathF.PI;
            var m = Matrix4x4.CreateScale(HologramConfig.Scale) * Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(a);
            // Tür-Seite (Spec 2): Gebäudepunkt minus Einfahrt auf die rechte Achse der Tafel (lokal +X = (cos h, 0, -sin h));
            // der Zeiger ist auf +X modelliert -> liegt die Tür auf -X, gespiegelt
            var door = s.Position - a;
            float side = door.X * MathF.Cos(yaw) - door.Z * MathF.Sin(yaw);
            inst.Mirror = side < 0f;
            var mm = Matrix4x4.CreateScale(-1f, 1f, 1f) * m;
            foreach (var (_, parts) in inst.Scenes)
                foreach (var (o, b, mn, mx, c) in parts)
                {
                    var w = inst.Mirror && inst.SideArrows.Contains(o) ? mm : m;
                    o.Model = b * w;
                    ObjectBuilder.TransformBounds(mn, mx, w, out o.BoundsMin, out o.BoundsMax);
                    o.Center = Vector3.Transform(c, w);
                }
        }
        foreach (var kv in _inst)
            if (kv.Value != null) foreach (var (h, _) in kv.Value.Scenes) h.Active = kv.Value.Shown;
    }

    /// <summary>Außerhalb der Freifahrt (Interieur, Karte, Kinos …) nicht zeichnen.</summary>
    public static void Hide()
    {
        foreach (var kv in _inst)
            if (kv.Value != null) { kv.Value.Shown = false; foreach (var (h, _) in kv.Value.Scenes) h.Active = false; }
    }

    static void Reset(MapStreamer st)
    {
        // Szenen des alten Streamers gehören zu dessen Welt (beim Inselwechsel mit abgebaut)
        if (_st != null)
            foreach (var kv in _inst)
                if (kv.Value != null) foreach (var (h, _) in kv.Value.Scenes) { _st.Scenes.Remove(h); h.Res.Destroy(); }
        _inst.Clear();
        _st = st; _model = null; _sets = null; _load = null; _failed = false;
    }

    /// <summary>Hintergrund: Bank als Szene laden (Texturen und .uva registrieren), Entitätsnamen -> Szene.</summary>
    static (BankModel, Dictionary<string, ObjectSet>)? Load(MapStreamer st, string path)
    {
        try
        {
            var model = st.LoadSceneModel(path);
            var bank = model.Bank;
            var byRsc = new Dictionary<ulong, BnkEntry>();
            var hc = bank.Find("hashcodes.xmb");
            var groups = hc != null ? XmbFile.Parse(bank.Read(hc)).Get("___CmdSaveFileInfoArray") : null;
            if (groups != null)
                foreach (var g in groups.Items)
                {
                    var idx = g.Get("FileIndices"); var refs = g.Get("FileReferences");
                    if (idx == null || refs == null) continue;
                    int n = System.Math.Min(idx.Items.Count, refs.Items.Count);
                    for (int i = 0; i < n; i++)
                    {
                        int fi = idx.Items[i].AsInt(-1);
                        BnkEntry? be = null;
                        foreach (var e in bank.Entries) if (e.Index == fi) { be = e; break; }
                        if (be == null && fi >= 0 && fi < bank.Entries.Count) be = bank.Entries[fi];
                        if (be != null) byRsc[refs.Items[i].AsULong()] = be;
                    }
                }
            var setByFile = new Dictionary<string, ObjectSet>(StringComparer.OrdinalIgnoreCase);
            foreach (var set in model.AllObjectSets()) if (set != null) setByFile[set.Name] = set;
            var sets = new Dictionary<string, ObjectSet>(StringComparer.OrdinalIgnoreCase);
            var ent = bank.Find("entities.xmb");
            var root = ent != null ? XmbFile.Parse(bank.Read(ent)) : null;
            int plates = 0, frames = 0;
            if (root != null)
                foreach (var section in root.Children)
                    foreach (var arr in section.Children)
                    {
                        if (!arr.IsArray || !arr.Name.StartsWith("ArrayOf_Entity__Actor__SpotMesh", StringComparison.Ordinal)) continue;
                        bool addOn = arr.Name.EndsWith("AddOn", StringComparison.Ordinal);
                        foreach (var it in arr.Items)
                        {
                            string name = it.S("__Name", "") ?? "";
                            var mesh = it.Get("mesh");
                            if (name.Length == 0 || mesh == null) continue;
                            foreach (var rr in mesh.Items)
                                if (rr.I("rscSubType", -1) == 0 && byRsc.TryGetValue(rr.U("rsc"), out var dd) && setByFile.TryGetValue(dd.Name, out var set))
                                { sets[name] = set; if (addOn) frames++; else plates++; break; }
                        }
                    }
            Log.Info($"Hologramme: {plates} Tafeln, {frames} Rahmen aus {Path.GetFileName(path)}");
            return (model, sets);
        }
        catch (Exception ex) { Log.Warn("Hologramme: " + ex.Message); return null; }
    }

    static Instance? Build(MapStreamer st, Spot s)
    {
        string? plate = Plate(s, out string frame);
        if (plate == null || _sets == null || _model == null) return null;
        var inst = new Instance { Spot = s };
        foreach (var name in new[] { plate, frame })
        {
            if (!_sets.TryGetValue(name, out var set)) { Log.Warn($"Hologramme: {name} fehlt in pack_spots ({s.Code})"); continue; }
            var saved = set.Placements;
            var wrap = new BankModel { Path = _model.Path, Kind = BankKind.Exterior, Bank = _model.Bank, Objects = set };
            set.Placements = new List<Matrix4x4> { Matrix4x4.Identity };
            MapStreamer.SceneHandle h;
            MaterialFactory.Hologram = true;
            try { h = st.BuildScene(wrap, Matrix4x4.Identity, false); }
            finally { set.Placements = saved; MaterialFactory.Hologram = false; }
            h.Model = null;   // gemeinsame Szene der Bank – beim Abbau nicht freigeben
            h.Active = false;
            st.Scenes.Add(h);
            var parts = new List<(RenderObject, Matrix4x4, Vector3, Vector3, Vector3)>();
            foreach (var o in h.Objects) parts.Add((o, o.Model, o.BoundsMin, o.BoundsMax, o.Center));
            if (name == frame) ClassifyFrame(h.Objects, inst);
            inst.Scenes.Add((h, parts));
        }
        return inst.Scenes.Count > 0 ? inst : null;
    }

    /// <summary>
    /// Rahmen (SpotMeshAddOn): größtes Teil = Ring/Pulse; von den Zeigern ist der mittige (x ≈ 0, Spitze nach unten) die im Original
    /// ausgeblendete Autoren-Richtung (Spec 2: ForceHiddenNodes) -> aus; die übrigen sind seitliche Zeiger zur Tür.
    /// </summary>
    static void ClassifyFrame(List<RenderObject> objs, Instance inst)
    {
        if (objs.Count < 2) return;
        RenderObject? ring = null; float best = -1f;
        foreach (var o in objs) { float sz = Vector3.Distance(o.BoundsMin, o.BoundsMax); if (sz > best) { best = sz; ring = o; } }
        RenderObject? down = null; float cx = float.MaxValue;
        foreach (var o in objs) if (o != ring && MathF.Abs(o.Center.X) < cx) { cx = MathF.Abs(o.Center.X); down = o; }
        foreach (var o in objs)
        {
            if (o == ring) continue;
            if (o == down) { o.Active = false; continue; }
            inst.SideArrows.Add(o);
        }
    }

    /// <summary>Anker: Einfahrt an der Straße (spots.bin +0x28), sonst der Gebäudepunkt.</summary>
    static Vector3 Anchor(Spot s) => s.Position2.LengthSquared() > 1f ? s.Position2 : s.Position;

    /// <summary>
    /// Spot -> Symbol-Tafel und Rahmen (Spec: Händler nach Bank, ECD_MERC -> CD_MERC, ECDC_WLD -> CDC_WLD; eigenes Haus Playerhouse
    /// mit Square_Red, Haus zu kaufen House_To_Buy mit Rhombus_Blue, Händler Circle_Orange, Club/Casino Circle_Green, Schule
    /// Square_Blue, Rest Square_DarkBlue). null = kein Hologramm.
    /// </summary>
    static string? Plate(Spot s, out string frame)
    {
        frame = "Square_DarkBlue_Frame";
        string code = (s.Code ?? "").ToUpperInvariant();
        string? label = Minimap.SpotLabel(code);
        switch (label)
        {
            case "carDealer":
            case "bikeDealer":
                frame = "Circle_Orange_Frame";
                return code.Length > 1 ? code[1..] : null;   // ECD_MERC -> CD_MERC, ECDC_WLD -> CDC_WLD, EBD_CLA -> BD_CLA
            case "home":
                if (HouseOwned(s)) { frame = "Square_Red_Frame"; return "Playerhouse"; }
                frame = "Rhombus_Blue_Frame"; return "House_To_Buy";
            case "club": frame = "Circle_Green_Frame"; return "Club";
            case "casino": frame = "Circle_Green_Frame"; return "Casino";
            case "rschool": frame = "Square_Blue_Frame"; return "Racing_School";
            case "gamblingHall": return "GamblinHall";
            case "tuning": return "Tuner";
            case "carWash": return "Car_Wash";
            case "carPaint": return "Stickers";
            case "estates": return "Realtor";
            case "clothes": return "Clothes_Shop";
            case "hair": return "Hair_Stylist";
            case "surgery": return "Surgery";
            case "airport": return "Airport";
            case "auctionHall": return "Used_Car_Dealer";
            case "photograph": return "Photograph";
            default: return null;
        }
    }

    /// <summary>Haus gehört dem Spieler? Wie die DATA (TDU2SaveGame.Build): derzeit gehören alle Häuser (Kaufen folgt).</summary>
    static bool HouseOwned(Spot s) => true;
}
