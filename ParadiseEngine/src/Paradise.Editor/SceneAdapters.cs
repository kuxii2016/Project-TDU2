// Szenen-Editor: Hierarchie-Einträge für die Engine-Typen aus Paradise.Core (Spiel-Thread).
//   World (MapStreamer) → Island LOD / Areas / Sectors (Nodes → RenderObjects → LODs, Placements) / Scenes / Map tiles / Water
//   Fahrzeuge (CarModel) → CarNode-Baum (Unity: GameObject-Hierarchie des Autos)
// Fahrzeuge aus Paradise.Game (eigenes Auto, Verkehr, Mitspieler) meldet das Spiel über SceneEditor.SetRoot an.

using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using System.Globalization;
using System.Numerics;
using System.Reflection;

namespace Paradise.Editor;

public static class SceneAdapters
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string N(float v, string fmt = "0.##") => v.ToString(fmt, Inv);
    static string Nm(string? s, string fallback) => string.IsNullOrEmpty(s) ? fallback : s;
    static Vector3 Cam => SceneEditor.CameraGame?.Invoke() ?? Vector3.Zero;
    static Vector3 ToGame(Vector3 render) => SceneEditor.RenderToGame?.Invoke(render) ?? render;

    // ------------------------------------------------------------------ Gruppe (Ordner ohne eigenes Engine-Objekt)
    public static SceneNode Group(string id, string name, Func<int> count, Func<IEnumerable<SceneNode>> children,
                                  Action<InspectorBuilder>? inspect = null, Func<bool>? alive = null, string type = "Gruppe")
        => new SceneNode
        {
            Id = id, Name = name, Type = type, CountChildren = count, Children = children, Alive = alive,
            Inspect = inspect ?? (b => { b.Section(type); b.Info("Einträge", count().ToString(Inv)); }),
        };

    // ------------------------------------------------------------------ Welt (MapStreamer)
    public static SceneNode World(MapStreamer s)
    {
        var node = new SceneNode
        {
            Id = "g:world",
            Name = "World (" + (s.Index?.IslandName ?? s.Island) + ")",
            Type = "MapStreamer",
            Target = s,
            CountChildren = () => 5 + (s.MapTiles.Count > 0 ? 1 : 0),
            Children = () => WorldChildren(s),
            Inspect = b =>
            {
                b.Section("MapStreamer");
                b.Info("Insel", s.Index?.IslandName ?? s.Island);
                b.Info("Status", s.Status);
                b.Info("Areas geladen", s.LoadedAreas.ToString(Inv));
                b.Info("Sektoren geladen", s.LoadedSectors.ToString(Inv));
                b.Info("Gezeichnete Objekte", s.DrawnObjects.ToString(Inv));
                b.Info("Kamera (Spielraum)", s.ViewerGame);
                b.Info("Render-Ursprung", s.RootOffset);
                b.Info("Wasserhöhe", s.WaterLevel);
                b.Bool("Welt ausgeblendet (HideWorld)", s.HideWorld, "hideworld");
            },
            Edit = (k, v) => { if (k != "hideworld") return false; s.HideWorld = v == "1"; return true; },
        };
        return node;
    }

    static IEnumerable<SceneNode> WorldChildren(MapStreamer s)
    {
        yield return Group("g:world/island", "Island LOD", () => s.EditorIslandPieces().Count,
            () => s.EditorIslandPieces().Select(p => Node(p.Node, "Island " + p.Key, () => true)));
        yield return Group("g:world/areas", "Areas", () => s.EditorAreas().Count,
            () => s.EditorAreas().Select(c => Cell(s, c)));
        yield return Group("g:world/sectors", "Sectors", () => s.EditorSectors().Count,
            () => s.EditorSectors().Select(c => Cell(s, c)));
        yield return Group("g:world/scenes", "Scenes (Interieur, Möbel, Tore)", () => s.Scenes.Count,
            () => s.Scenes.ToArray().Select((h, i) => Scene(s, h, i)));
        if (s.MapTiles.Count > 0)
            yield return Group("g:world/maptiles", "Map tiles", () => s.MapTiles.Count,
                () => s.MapTiles.ToArray().Select(t => MapTile(s, t)));
        yield return Water(s);
    }

    static SceneNode Cell(MapStreamer s, MapStreamer.EditorCell c)
    {
        string id = "g:" + c.Kind + "/" + c.Key;
        Func<bool> alive = () => s.EditorIsLoaded(c);
        var nodes = c.Nodes;
        var plc = c.Placements;
        int plcCount = plc != null && plc.EditorBatchCount > 0 ? 1 : 0;
        return new SceneNode
        {
            Id = id,
            Name = c.Kind + " " + c.Key + (c.Failed ? "  (Fehler)" : c.Built ? "" : "  (lädt)"),
            Type = c.Kind,
            Alive = alive,
            CountChildren = () => nodes.Count + plcCount,
            Children = () => CellChildren(id, nodes, plc, alive),
            Inspect = b =>
            {
                b.Section(c.Kind);
                b.Info("Schlüssel", c.Key);
                b.Info("Bank", c.Bank);
                b.Info("Gebaut", c.Built ? "ja" : "nein");
                if (c.Failed) b.Info("Fehler", "Laden fehlgeschlagen");
                b.Info("Nodes", nodes.Count.ToString(Inv));
                b.Info("Objekte", nodes.Sum(n => n.Objects.Count).ToString(Inv));
                b.Info("Zusatz-Bänke (Exteriors)", c.ExtraModels.ToString(Inv));
                b.Info("Kollisionskörper", c.Colliders.ToString(Inv));
                if (plc != null) b.Info("Platzierungen", plc.EditorBatchCount + " Typen, " + plc.InstanceCount + " Instanzen");
                b.Info("Gras", c.HasGrass ? "ja" : "nein");
                b.Info("Stromleitungen", c.HasWires ? "ja" : "nein");
            },
            Gizmo = g =>
            {
                Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
                foreach (var n in nodes) if (n.HasBounds) { mn = Vector3.Min(mn, n.BoundsMin); mx = Vector3.Max(mx, n.BoundsMax); }
                g.Aabb(mn, mx);
            },
        };
    }

    static IEnumerable<SceneNode> CellChildren(string id, IReadOnlyList<MapStreamer.Node> nodes, PlacementRenderer? plc, Func<bool> alive)
    {
        for (int i = 0; i < nodes.Count; i++) yield return Node(nodes[i], "Node " + i, alive);
        if (plc != null && plc.EditorBatchCount > 0) yield return Placements(id + "/plc", plc, alive);
    }

    // ------------------------------------------------------------------ MapStreamer.Node (Unity: GameObject einer Bank)
    public static SceneNode Node(MapStreamer.Node n, string fallbackName, Func<bool> cellAlive)
    {
        Func<bool> alive = () => !n.Disposed && cellAlive();
        return new SceneNode
        {
            Id = EditorIds.Of(n),
            Name = Nm(n.Name, fallbackName),
            Type = "Node (" + n.Objects.Count + " Objekte" + (n.Meshes.Count > 0 ? ", " + n.Meshes.Count + " Meshes" : "") + ")",
            Target = n,
            GetActive = () => n.Active,
            SetActive = v => n.Active = v,
            Alive = alive,
            CountChildren = () => n.Objects.Count,
            Children = () => n.Objects.ToArray().Select(o => RenderObject(o, alive)),
            Inspect = b =>
            {
                b.Section("Node");
                b.Text("Name", n.Name, "name");
                b.Info("Objekte", n.Objects.Count.ToString(Inv));
                b.Info("Eigene Meshes", n.Meshes.Count.ToString(Inv));
                if (n.HasBounds) Bounds(b, n.BoundsMin, n.BoundsMax);
                if (n.Meshes.Count > 0)
                {
                    b.Section("Mesh Renderer (eigene Meshes)");
                    for (int i = 0; i < n.Meshes.Count && i < 64; i++)
                    {
                        var (mesh, mat, model) = n.Meshes[i];
                        b.Info("Mesh " + i, Tris(mesh) + ", " + MatName(mat) + ", Pos " + InspectorBuilder.V3(model.Translation, "0.#"));
                    }
                    Materials(b, n.Meshes.Select(m => m.Material));
                }
            },
            Edit = (k, v) =>
            {
                switch (k)
                {
                    case "name": n.Name = v; return true;
                    case "active": n.Active = v == "1"; return true;
                }
                return false;
            },
            Gizmo = g => { if (n.HasBounds) g.Aabb(n.BoundsMin, n.BoundsMax); },
        };
    }

    // ------------------------------------------------------------------ RenderObject (Unity: GameObject mit MeshRenderer/LODGroup)
    public static SceneNode RenderObject(RenderObject ro, Func<bool> alive)
    {
        string id = EditorIds.Of(ro);
        return new SceneNode
        {
            Id = id,
            Name = Nm(ro.Name, "(Objekt)"),
            Type = "RenderObject (" + ro.Lods.Length + " LOD)",
            Target = ro,
            GetActive = () => ro.Active,
            SetActive = v => ro.Active = v,
            Alive = alive,
            CountChildren = () => ro.Lods.Length > 1 ? ro.Lods.Length : 0,
            Children = () => Enumerable.Range(0, ro.Lods.Length).Select(i => Lod(id, ro, i, alive)),
            Inspect = b => InspectRenderObject(b, ro),
            Edit = (k, v) => EditRenderObject(ro, k, v),
            Gizmo = g => g.Aabb(ro.BoundsMin, ro.BoundsMax),
        };
    }

    static void InspectRenderObject(InspectorBuilder b, RenderObject ro)
    {
        b.Section("RenderObject");
        b.Text("Name", ro.Name, "name");

        b.Section("Transform (Spielraum)");
        if (TransformMath.Decompose(ro.Model, out var pos, out var rot, out var scale))
        {
            b.Vec3("Position", pos, "pos");
            b.Vec3("Rotation", TransformMath.ToEuler(rot), "rot");
            b.Vec3("Scale", scale, "scale");
            if (ro.Model.GetDeterminant() < 0f) b.Info("Gespiegelt", "ja (negative X-Skalierung)");
        }
        else
        {
            b.Info("Matrix", "nicht zerlegbar");
            b.Info("Position", ro.Model.Translation);
        }
        b.Info("Hinweis", "Kollision bleibt beim Verschieben am alten Ort");

        Bounds(b, ro.BoundsMin, ro.BoundsMax);
        b.Info("Center (LOD)", ro.Center);

        b.Section("LOD Group");
        var cam = Cam;
        float d = Vector3.Distance(cam, ro.Center);
        var cur = ro.SelectLod(cam);
        b.Info("Kamera-Abstand", N(d, "0.0") + " m");
        b.Info("Aktuelle LOD", cur == null ? "keine (ausgeblendet)" : "LOD " + Array.IndexOf(ro.Lods, cur));
        b.Info("Letzte LOD ausblenden", ro.CullLastLod ? "ja" : "nein");
        b.Info("LOD-Bias", N(Paradise.Core.World.RenderObject.LodBias));
        for (int i = 0; i < ro.Lods.Length; i++)
        {
            var l = ro.Lods[i];
            b.Info("LOD " + i, (float.IsPositiveInfinity(l.MaxDistance) ? "unbegrenzt" : "bis " + N(l.MaxDistance * Paradise.Core.World.RenderObject.LodBias, "0") + " m")
                               + ", " + l.Parts.Length + " Teile, " + TrisSum(l.Parts));
        }

        if (ro.Lods.Length > 0)
        {
            b.Section("Mesh Renderer (LOD 0)");
            Parts(b, ro.Lods[0].Parts);
        }
        Materials(b, ro.Lods.SelectMany(l => l.Parts).Select(p => p.Material));
    }

    static bool EditRenderObject(RenderObject ro, string k, string v)
    {
        switch (k)
        {
            case "name": ro.Name = v; return true;
            case "active": ro.Active = v == "1"; return true;
            case "pos":
            case "rot":
            case "scale":
            {
                if (!InspectorBuilder.TryParseV3(v, out var val)) return false;
                var old = ro.Model;
                if (!TransformMath.Decompose(old, out var pos, out var rot, out var scale)) return false;
                if (k == "pos") pos = val; else if (k == "rot") rot = TransformMath.FromEuler(val); else scale = val;
                var m = TransformMath.Compose(pos, rot, scale);
                if (!Matrix4x4.Invert(old, out var inv)) return false;
                // Bounds/Center (Spielraum, Frustum-Test + LOD) mit der Änderung mitziehen
                var delta = inv * m;
                TransformMath.TransformAabb(ro.BoundsMin, ro.BoundsMax, delta, out var mn, out var mx);
                ro.BoundsMin = mn; ro.BoundsMax = mx;
                ro.Center = Vector3.Transform(ro.Center, delta);
                ro.Model = m;
                return true;
            }
        }
        return false;
    }

    static SceneNode Lod(string ownerId, RenderObject ro, int i, Func<bool> alive)
    {
        var l = ro.Lods[i];
        return new SceneNode
        {
            Id = ownerId + "/lod" + i,
            Name = "LOD " + i,
            Type = "LOD-Stufe",
            Alive = alive,
            Inspect = b =>
            {
                b.Section("LOD " + i + " von " + Nm(ro.Name, "(Objekt)"));
                b.Info("Sichtweite", float.IsPositiveInfinity(l.MaxDistance) ? "unbegrenzt" : "bis " + N(l.MaxDistance * Paradise.Core.World.RenderObject.LodBias, "0") + " m");
                b.Info("Aktiv (Kamera)", ReferenceEquals(ro.SelectLod(Cam), l) ? "ja" : "nein");
                b.Section("Mesh Renderer");
                Parts(b, l.Parts);
                Materials(b, l.Parts.Select(p => p.Material));
            },
            Gizmo = g => g.Aabb(ro.BoundsMin, ro.BoundsMax),
        };
    }

    // ------------------------------------------------------------------ Platzierungen (.pmi: Bäume, Büsche, Leitplanken …)
    static SceneNode Placements(string id, PlacementRenderer p, Func<bool> alive)
        => Group(id, "Placements (" + p.EditorBatchCount + " Typen, " + p.InstanceCount + " Instanzen)", () => p.EditorBatchCount,
            () => Enumerable.Range(0, p.EditorBatchCount).Select(i => PlacementBatch(id + "/" + i, p, i, alive)),
            b =>
            {
                b.Section("PlacementRenderer");
                b.Info("Typen", p.EditorBatchCount.ToString(Inv));
                b.Info("Instanzen", p.InstanceCount.ToString(Inv));
                b.Info("Sichtweite", N(p.MaxDistance * PlacementRenderer.DistanceMultiplier, "0") + " m");
            }, alive, "Placements");

    static SceneNode PlacementBatch(string id, PlacementRenderer p, int batch, Func<bool> alive)
    {
        var obj = p.EditorBatchObject(batch);
        var mats = p.EditorBatchMatrices(batch);
        return new SceneNode
        {
            Id = id,
            Name = Nm(obj.Name, "(Library-Objekt)") + "  ×" + mats.Count,
            Type = "Library-Objekt (instanziert)",
            Target = obj,
            Alive = alive,
            CountChildren = () => mats.Count,
            Children = () => Enumerable.Range(0, mats.Count).Select(j => PlacementInstance(id + "#" + j, obj, mats, j, alive)),
            Inspect = b => { InspectLibrary(b, obj); b.Section("Instanzen"); b.Info("Anzahl", mats.Count.ToString(Inv)); },
            Gizmo = g =>
            {
                for (int j = 0; j < mats.Count && j < 256; j++) g.Box(obj.BoundsMin, obj.BoundsMax, mats[j], j > 0);
            },
        };
    }

    static SceneNode PlacementInstance(string id, LibraryObject obj, IReadOnlyList<Matrix4x4> mats, int j, Func<bool> alive)
        => new SceneNode
        {
            Id = id,
            Name = Nm(obj.Name, "(Library-Objekt)") + " #" + j,
            Type = "Platzierte Instanz",
            Alive = alive,
            Inspect = b =>
            {
                var m = mats[j];
                b.Section("Transform (Spielraum, nur lesen)");
                if (TransformMath.Decompose(m, out var pos, out var rot, out var scale))
                {
                    b.Info("Position", pos);
                    b.Info("Rotation", TransformMath.ToEuler(rot), "0.##");
                    b.Info("Scale", scale);
                }
                else b.Info("Position", m.Translation);
                b.Info("Hinweis", "Instanzen sind vorsortiert (LOD-Eimer) – Ändern hier nicht möglich");
                TransformMath.TransformAabb(obj.BoundsMin, obj.BoundsMax, m, out var mn, out var mx);
                Bounds(b, mn, mx);
                InspectLibrary(b, obj);
            },
            Gizmo = g => g.Box(obj.BoundsMin, obj.BoundsMax, mats[j]),
        };

    static void InspectLibrary(InspectorBuilder b, LibraryObject obj)
    {
        b.Section("Library-Objekt (commonworld.bnk)");
        b.Info("Name", obj.Name);
        b.Info("Key", "0x" + obj.Key.ToString("x16", Inv));
        b.Info("Radius", obj.Radius);
        b.Info("Bounds lokal (LOD0)", InspectorBuilder.V3(obj.BoundsMin) + "  …  " + InspectorBuilder.V3(obj.BoundsMax));
        b.Section("LOD Group");
        for (int i = 0; i < obj.Lods.Length; i++)
            b.Info("LOD " + i, "bis " + N(obj.Lods[i].MaxDistance, "0") + " m, " + obj.Lods[i].Parts.Length + " Teile, " + TrisSum(obj.Lods[i].Parts)
                               + ", Geo 0x" + obj.Lods[i].GeoKey.ToString("x16", Inv));
        if (obj.Lods.Length > 0)
        {
            b.Section("Mesh Renderer (LOD 0)");
            Parts(b, obj.Lods[0].Parts);
        }
        Materials(b, obj.Lods.SelectMany(l => l.Parts).Select(p => p.Material));
    }

    // ------------------------------------------------------------------ Szenen (Interieur, Möbel, Renn-Tore)
    static SceneNode Scene(MapStreamer s, MapStreamer.SceneHandle h, int index)
    {
        Func<bool> alive = () => s.Scenes.Contains(h);
        string bank = h.Model?.Path ?? "";
        return new SceneNode
        {
            Id = EditorIds.Of(h),
            Name = bank.Length > 0 ? Path.GetFileName(bank) : "Scene " + index,
            Type = "SceneHandle (" + h.Objects.Count + " Objekte)",
            Target = h,
            GetActive = () => h.Active,
            SetActive = v => h.Active = v,
            Alive = alive,
            CountChildren = () => h.Objects.Count,
            Children = () => h.Objects.ToArray().Select(o => RenderObject(o, alive)),
            Inspect = b =>
            {
                b.Section("Scene");
                b.Info("Bank", bank);
                b.Info("Objekte", h.Objects.Count.ToString(Inv));
                Bounds(b, h.BoundsMin, h.BoundsMax);
            },
            Edit = (k, v) => { if (k != "active") return false; h.Active = v == "1"; return true; },
            Gizmo = g => g.Aabb(h.BoundsMin, h.BoundsMax),
        };
    }

    static SceneNode MapTile(MapStreamer s, MapStreamer.MapTile t)
        => new SceneNode
        {
            Id = EditorIds.Of(t),
            Name = "Map tile " + t.Key,
            Type = "Kartenkachel",
            Alive = () => s.MapTiles.Contains(t),
            Inspect = b =>
            {
                b.Section("Map tile");
                b.Info("Schlüssel", t.Key.ToString());
                b.Info("Ursprung", t.Origin);
                b.Info("Mesh", t.Mesh == null ? "-" : Tris(t.Mesh));
                b.Info("Farbtextur", Tex(t.Color));
                if (t.Material != null) Materials(b, new[] { t.Material });
            },
        };

    static SceneNode Water(MapStreamer s)
        => new SceneNode
        {
            Id = "g:world/water",
            Name = "Water (Meer)",
            Type = "Wasserfläche",
            Inspect = b =>
            {
                var w = s.EditorWater;
                b.Section("Meer");
                b.Info("Position (Spielraum)", w.PositionGame);
                b.Info("Wasserhöhe", s.WaterLevel);
                b.Info("Inselwasserhöhe", s.IslandWaterHeight);
                b.Bool("Anzeigen", s.ShowWater);
                b.Info("Mesh", w.Mesh == null ? "-" : Tris(w.Mesh));
                if (w.Material != null) Materials(b, new[] { w.Material });
            },
        };

    // ------------------------------------------------------------------ Fahrzeuge (CarModel / CarNode)
    /// <summary>Ein Fahrzeug als Wurzel (Kinder = CarNode-Baum unter Visual). extra = eigener Inspector-Abschnitt des Aufrufers.</summary>
    public static SceneNode Car(string id, string name, string type, CarModel model, Func<bool> alive, Action<InspectorBuilder>? extra = null)
    {
        var v = model.Visual;
        return new SceneNode
        {
            Id = id,
            Name = name,
            Type = type,
            Target = model,
            GetActive = () => v.Active,
            SetActive = a => v.Active = a,
            Alive = alive,
            CountChildren = () => v.Children.Count,
            Children = () => v.Children.ToArray().Select(c => CarNode(c, alive)),
            Inspect = b =>
            {
                extra?.Invoke(b);
                b.Section("Fahrzeugmodell (CarModel)");
                b.Info("Position (Spielraum)", ToGame(v.World.Translation));
                b.Info("Karosserie Min", model.BodyMin);
                b.Info("Karosserie Max", model.BodyMax);
                b.Info("Karosserie Größe", model.BodySize);
                b.Info("Reifenradius vorne / hinten", N(model.TireRadiusFront, "0.###") + " / " + N(model.TireRadiusRear, "0.###") + " m");
                b.Info("Knoten", model.Nodes.Count.ToString(Inv));
                b.Info("Materialien", model.AllMaterials.Count.ToString(Inv));
                b.Section("Alle Materialien", true);
                foreach (var m in model.AllMaterials) b.Info(m.Name, m.Desc.Kind + (m.Gpu == null ? " (nicht hochgeladen)" : ""));
            },
            Edit = (k, val) => { if (k != "active") return false; v.Active = val == "1"; return true; },
            Gizmo = g => g.BoxRender(model.BodyMin, model.BodyMax, v.World),
        };
    }

    public static SceneNode CarNode(CarNode n, Func<bool> alive)
        => new SceneNode
        {
            Id = EditorIds.Of(n),
            Name = Nm(n.Name, "(ohne Name)"),
            Type = "CarNode" + (n.HasMesh ? " + Mesh (" + n.Parts.Count + " Teile)" : ""),
            Target = n,
            GetActive = () => n.Active,
            SetActive = v => n.Active = v,
            Alive = alive,
            CountChildren = () => n.Children.Count,
            Children = () => n.Children.ToArray().Select(c => CarNode(c, alive)),
            Inspect = b => InspectCarNode(b, n),
            Edit = (k, v) => EditCarNode(n, k, v),
            Gizmo = g =>
            {
                if (n.HasMesh) g.BoxRender(n.MeshMin, n.MeshMax, n.World);
                else g.AxesRender(n.World, 0.3f);
            },
        };

    static void InspectCarNode(InspectorBuilder b, CarNode n)
    {
        b.Section("CarNode");
        b.Text("Name", n.Name, "name");
        b.Info("Aktiv in Hierarchie", n.ActiveInHierarchy ? "ja" : "nein");
        b.Info("Eltern", n.Parent == null ? "-" : Nm(n.Parent.Name, "(ohne Name)"));

        b.Section("Transform (lokal)");
        b.Vec3("Position", n.LocalPosition, "pos");
        b.Vec3("Rotation", TransformMath.ToEuler(n.LocalRotation), "rot");
        b.Vec3("Scale", n.LocalScale, "scale");
        if (n.RawLocal != null) b.Info("Hinweis", "Matrix aus der Datei (Scherung) – Ändern ersetzt sie");
        b.Info("Welt-Position (Spielraum)", ToGame(n.World.Translation));
        b.Info("Hinweis Animation", "Räder/Lenkung/Türen setzt das Spiel jedes Bild neu");

        b.Section("Mesh Renderer");
        b.Bool("Renderer an", n.RendererEnabled, "renderer");
        b.Info("Hat Mesh", n.HasMesh ? "ja" : "nein");
        if (n.HasMesh) b.Info("Mesh-Bounds", InspectorBuilder.V3(n.MeshMin) + "  …  " + InspectorBuilder.V3(n.MeshMax));
        for (int i = 0; i < n.Parts.Count; i++)
        {
            var p = n.Parts[i];
            b.Info("Teil " + i, "Submesh " + p.SubMesh + ", " + Tris(p.Mesh) + ", " + Nm(p.Material.Name, "?"));
        }
        foreach (var m in n.Parts.Select(p => p.Material).Distinct()) CarMaterialSection(b, m);
    }

    static bool EditCarNode(CarNode n, string k, string v)
    {
        switch (k)
        {
            case "name": n.Name = v; return true;
            case "active": n.Active = v == "1"; return true;
            case "renderer": n.RendererEnabled = v == "1"; return true;
            case "pos":
            case "rot":
            case "scale":
            {
                if (!InspectorBuilder.TryParseV3(v, out var val)) return false;
                if (n.RawLocal != null)
                {
                    // Datei-Matrix (Scherung) in Position/Rotation/Skalierung überführen, dann ändern
                    if (TransformMath.Decompose(n.RawLocal.Value, out var p0, out var r0, out var s0)) { n.LocalPosition = p0; n.LocalRotation = r0; n.LocalScale = s0; }
                    n.RawLocal = null;
                }
                if (k == "pos") n.LocalPosition = val;
                else if (k == "rot") n.LocalRotation = TransformMath.FromEuler(val);
                else n.LocalScale = val;
                return true;
            }
        }
        return false;
    }

    static void CarMaterialSection(InspectorBuilder b, CarMaterial m)
    {
        b.Section("Material: " + Nm(m.Name, "Car"));
        b.Info("Shader (Engine)", m.Desc.Kind.ToString());
        b.Info("Auf der GPU", m.Gpu != null ? "ja" : "nein");
        TextureRows(b, m.Desc);
        DescSection(b, Nm(m.Name, "Car"), m.Desc);
    }

    // ------------------------------------------------------------------ Hilfen: Bounds, Teile, Materialien
    static void Bounds(InspectorBuilder b, Vector3 min, Vector3 max)
    {
        if (!(min.X <= max.X)) return;
        b.Section("Bounds (Spielraum)");
        b.Info("Center", (min + max) * 0.5f);
        b.Info("Size", max - min);
        b.Info("Min", min);
        b.Info("Max", max);
    }

    static void Parts(InspectorBuilder b, RenderPart[] parts)
    {
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            b.Info("Teil " + i, Tris(p.Mesh) + ", " + MatName(p.Material) + (p.Collision != null ? ", Kollision " + p.Collision.Length / 3 + " Dr." : ""));
        }
    }

    static string Tris(IGpuMesh? m) => m == null ? "-" : (m.IndexCount / 3).ToString("N0", Inv) + " Dreiecke";
    static string TrisSum(RenderPart[] parts) => (parts.Sum(p => (long)(p.Mesh?.IndexCount ?? 0)) / 3).ToString("N0", Inv) + " Dreiecke";

    static string MatName(IGpuMaterial? m)
    {
        if (m == null) return "(kein Material)";
        if (MaterialFactory.TryGetInfo(m, out var mi)) return Nm(mi.Def.Name, mi.Desc.Name ?? m.Kind.ToString());
        return m.Kind.ToString();
    }

    public static string Tex(IGpuTexture? t) => t == null ? "-" : Nm(t.Name, "(ohne Name)") + "  " + t.Width + "×" + t.Height + " " + t.Format;

    /// <summary>Material-Abschnitte (je Material einmal): Original-Shader/Blend/Texturen + alle Parameter (MaterialDesc).</summary>
    public static void Materials(InspectorBuilder b, IEnumerable<IGpuMaterial?> mats)
    {
        var seen = new HashSet<IGpuMaterial>(ReferenceEqualityComparer.Instance);
        int n = 0;
        foreach (var m in mats)
        {
            if (m == null || !seen.Add(m)) continue;
            if (++n > 32) { b.Section("Weitere Materialien"); b.Info("…", "mehr als 32 – ausgelassen"); break; }
            string name = MatName(m);
            b.Section("Material: " + name);
            b.Info("Shader (Engine)", m.Kind.ToString());
            if (MaterialFactory.TryGetInfo(m, out var mi))
            {
                b.Info("Original-Material", mi.Def.Name);
                b.Info("Original-Shader (ShaderSet)", "0x" + mi.Def.ShaderSet.ToString("x16", Inv));
                b.Info("Blend", mi.Blend.ToString());
                b.Info("Flags", Convert.ToHexString(mi.Def.Flags));
                b.Info("Diffuse", V4(mi.Def.Diffuse));
                b.Info("COLOR", (mi.MainTexture ?? "-") + (mi.Lightmap ? " (Lightmap)" : ""));
                b.Info("DETAIL", (mi.DetailTexture ?? "-") + (mi.Packed ? " (packed)" : ""));
                b.Info("SHADOW", mi.ShadowTexture ?? "-");
                b.Info("TINT", mi.TintTexture ?? "-");
                b.Info("Straße / Boden / Sprite", (mi.Def.IsRoad ? "ja" : "nein") + " / " + (mi.Def.IsGround ? "ja" : "nein") + " / " + (mi.Sprite ? "ja" : "nein"));
                if (mi.Def.IsPoolWater) b.Info("Pool-Wasser", "ja");
                TextureRows(b, mi.Desc);
                DescSection(b, name, mi.Desc);
            }
            else b.Info("Details", "keine Material-Infos (Gelände/Engine-Material)");
        }
    }

    // MaterialDesc per Reflection: alle öffentlichen Felder (nichts auslassen, neue Felder erscheinen automatisch)
    static readonly FieldInfo[] DescFields = typeof(MaterialDesc).GetFields(BindingFlags.Public | BindingFlags.Instance);

    static void TextureRows(InspectorBuilder b, MaterialDesc d)
    {
        foreach (var f in DescFields)
        {
            if (!typeof(IGpuTexture).IsAssignableFrom(f.FieldType)) continue;
            if (f.GetValue(d) is IGpuTexture t) b.Info("Textur " + f.Name, Tex(t));
        }
    }

    static void DescSection(InspectorBuilder b, string name, MaterialDesc d)
    {
        b.Section("Shader-Parameter: " + name, true);
        foreach (var f in DescFields)
        {
            object? v;
            try { v = f.GetValue(d); } catch (Exception) { continue; }
            b.Info(f.Name, Fmt(v));
        }
    }

    static string Fmt(object? v) => v switch
    {
        null => "-",
        IGpuTexture t => Tex(t),
        IGpuMaterial m => MatName(m),
        float f => f.ToString("0.####", Inv),
        double d => d.ToString("0.####", Inv),
        bool bo => bo ? "true" : "false",
        Vector2 v2 => v2.X.ToString("0.###", Inv) + "  " + v2.Y.ToString("0.###", Inv),
        Vector3 v3 => InspectorBuilder.V3(v3),
        Vector4 v4 => V4(v4),
        IFormattable fm => fm.ToString(null, Inv),
        _ => v.ToString() ?? "",
    };

    static string V4(Vector4 v) => v.X.ToString("0.###", Inv) + "  " + v.Y.ToString("0.###", Inv) + "  " + v.Z.ToString("0.###", Inv) + "  " + v.W.ToString("0.###", Inv);
}
