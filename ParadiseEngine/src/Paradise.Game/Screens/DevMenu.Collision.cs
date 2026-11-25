using BepuPhysics.Collidables;
using Paradise.Core.Physics;
using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using Paradise.Game.Gameplay;
using Paradise.Game.Online;
using Paradise.Game.Racing;
using System.Numerics;
using DL = Paradise.Core.Render.DebugLines;

namespace Paradise.Game.Screens;

/// <summary>
/// Entwicklermenü (F1), Debug-Ansicht der Kollisionen: Drahtkästen in der Nähe der Kamera aus den echten Physikdaten
/// der Engine. Vier Typen (Straßen/Gelände, feste Objekte, umfallbare Actors, Autos) lassen sich einzeln abschalten
/// und führen eine vordefinierte Farbe. Die Linien laufen über <see cref="DebugLines"/> (VehicleModule.Render).
/// Große Sektor-Netze werden auf einen kleinen Bereich um ihren nächsten Punkt begrenzt.
/// </summary>
public sealed partial class DevMenu
{
    bool _collisionBoxes;
    float _collisionRange = 100f, _detailDistance = 45f;
    bool _boxRoad = true, _boxObjects = true, _boxProps = true, _boxFixed = true, _boxLoose = true, _boxCars = true;
    bool _boxLabels = true, _aiPaths;
    // Budgets sind einstellbar: bei 1000 m Reichweite reicht das Standard-Detailbudget nicht für alles.
    int _maxCollisionLabels = 48, _detailTriBudget = 60000, _pathLineBudget = 1500;
    int _labelsThisFrame;
    // Eine Farbe pro schaltbarer Gruppe – damit Farbe und An/Aus-Schalter immer zusammenpassen.
    int _colPreset, _colRoad = 0, _colObjects = 8, _colProps = 5, _colFixed = 1, _colLoose = 2, _colCar = 3, _colPath = 10;
    int _cntRoad, _cntObjects, _cntProps, _cntFixed, _cntLoose, _cntCar, _cntPath;

    /// <summary>Ein Zeichen-Kandidat: entweder ein komplettes Netz oder ein einzelner Sektor-Objektbereich.</summary>
    struct StaticDraw
    {
        public StaticCollider Collider;
        public Vector3 Center, Half;
        public float DistSq;
        public int From, Count;
        public int ColorIndex;
        public bool Flat;
        public string Label;
    }

    // Vordefinierte Farben (Reihenfolge muss zu CollisionColorNames passen).
    static readonly string[] CollisionColorNames = { "Orange", "Blue", "Green", "Cyan", "Yellow", "Red", "Magenta", "White", "Lime", "Purple", "Pink", "Steel" };
    static readonly Vector4[] CollisionColors =
    {
        new(1f, 0.55f, 0.12f, 1f),   // Orange
        new(0.30f, 0.60f, 1f, 1f),   // Blue
        new(0.25f, 1f, 0.45f, 1f),   // Green
        new(0.20f, 1f, 1f, 1f),      // Cyan
        new(1f, 0.92f, 0.20f, 1f),   // Yellow
        new(1f, 0.25f, 0.20f, 1f),   // Red
        new(1f, 0.35f, 0.90f, 1f),   // Magenta
        new(0.95f, 0.95f, 0.95f, 1f),// White
        new(0.60f, 1f, 0.20f, 1f),   // Lime
        new(0.65f, 0.40f, 1f, 1f),   // Purple
        new(1f, 0.50f, 0.70f, 1f),   // Pink
        new(0.50f, 0.70f, 0.80f, 1f) // Steel
    };
    static readonly string[] CollisionPresetNames = { "Default", "Neon", "Colorblind", "Warm", "Cool" };
    // Reihenfolge: Road, Sector objects, Props, Fixed actors, Breakable actors, Cars, AI path
    static readonly int[][] CollisionPresets =
    {
        new[] { 0, 8, 5, 1, 2, 3, 10 },  // Orange / Lime / Red / Blue / Green / Cyan / Pink
        new[] { 4, 7, 8, 6, 9, 3, 10 },  // Yellow / White / Lime / Magenta / Purple / Cyan / Pink
        new[] { 0, 4, 5, 1, 7, 6, 10 },  // Orange / Yellow / Red / Blue / White / Magenta / Pink
        new[] { 5, 0, 8, 10, 1, 2, 3 },  // Red / Orange / Lime / Pink / Blue / Green / Cyan
        new[] { 9, 1, 3, 7, 4, 8, 10 }   // Purple / Blue / Cyan / White / Yellow / Lime / Pink
    };

    static Vector4 Col(int i) => CollisionColors[Math.Clamp(i, 0, CollisionColors.Length - 1)];
    static Vector4 Dim(Vector4 c) => new(c.X * 0.6f, c.Y * 0.6f, c.Z * 0.6f, 1f);
    static string ColName(int i) => CollisionColorNames[Math.Clamp(i, 0, CollisionColorNames.Length - 1)];

    void SetCollisionPreset(int i)
    {
        _colPreset = ((i % CollisionPresets.Length) + CollisionPresets.Length) % CollisionPresets.Length;
        var p = CollisionPresets[_colPreset];
        _colRoad = p[0]; _colObjects = p[1]; _colProps = p[2]; _colFixed = p[3];
        _colLoose = p[4]; _colCar = p[5]; _colPath = p[6];
    }

    /// <summary>Einstieg in die Debug-Ansicht (F1 &gt; Debug view): nur Hauptschalter und Reichweite. Alle Typ-, Farb-
    /// und Overlay-Optionen sitzen zusammen in einem Untermenü.</summary>
    void AddCollisionDebugToggles(List<Item> l)
    {
        l.Add(Bool("Show collision boxes", () => _collisionBoxes, v => _collisionBoxes = v, false));
        l.Add(Float("Collision box range", () => _collisionRange, v => _collisionRange = MathF.Max(20f, v), 25f, 20f, 1000f, 100f, "0", " m"));
        l.Add(Float("Detail range (real wireframe)", () => _detailDistance, v => _detailDistance = MathF.Max(10f, v), 5f, 10f, 500f, 45f, "0", " m"));
        // Zähler stehen bewusst hier (nicht nur im Untermenü): so sieht man beim Regeln sofort, was erscheint.
        l.Add(Info("Visible road / sector / props", () => _cntRoad + " / " + _cntObjects + " / " + _cntProps));
        l.Add(Info("Visible fixed / loose / cars", () => _cntFixed + " / " + _cntLoose + " / " + _cntCar));
        l.Add(Menu("View types, colors & AI path", CollisionViewMenu));
    }

    /// <summary>Alle An/Aus-Schalter und Farben der Debug-Ansicht an einem Ort.</summary>
    List<Item> CollisionViewMenu()
    {
        var l = new List<Item>
        {
            Head("QUICK GROUPS"),
            Action_("All collision groups ON", () => SetAllCollisionGroups(true)),
            Action_("All collision groups OFF", () => SetAllCollisionGroups(false)),
            Head("WORLD COLLIDERS (ON / OFF)"),
            Bool("Road / terrain (flat patches)", () => _boxRoad, v => _boxRoad = v, true),
            Bool("Sector objects (rocks, signs, trees)", () => _boxObjects, v => _boxObjects = v, true),
            Bool("Props & guardrails (real wireframe)", () => _boxProps, v => _boxProps = v, true),
            Head("ACTORS (ON / OFF)"),
            Bool("Fixed actors (heavy objects)", () => _boxFixed, v => _boxFixed = v, true),
            Bool("Breakable actors", () => _boxLoose, v => _boxLoose = v, true),
            Head("VEHICLES (ON / OFF)"),
            Bool("Cars (player/traffic/online/race)", () => _boxCars, v => _boxCars = v, true),
            Head("OVERLAYS (ON / OFF)"),
            Bool("Box labels (identify objects)", () => _boxLabels, v => _boxLabels = v, true),
            Bool("AI path system (also in Traffic tab)", () => _aiPaths, v => _aiPaths = v, false),
            Menu("AI path settings & color", AiPathMenu),
            Head("COLOR PRESET (sets all seven)"),
            Table("Preset", CollisionPresetNames, () => _colPreset, SetCollisionPreset, 0),
            Head("PER GROUP COLORS"),
            Table("Road / terrain", CollisionColorNames, () => _colRoad, v => _colRoad = v, 0),
            Table("Sector objects", CollisionColorNames, () => _colObjects, v => _colObjects = v, 8),
            Table("Props & guardrails", CollisionColorNames, () => _colProps, v => _colProps = v, 5),
            Table("Fixed actors", CollisionColorNames, () => _colFixed, v => _colFixed = v, 1),
            Table("Breakable actors", CollisionColorNames, () => _colLoose, v => _colLoose = v, 2),
            Table("Cars", CollisionColorNames, () => _colCar, v => _colCar = v, 3),
            Info("AI path (settings on AI path page)", () => ColName(_colPath)),
            Head("DETAIL BUDGET (performance)"),
            Float("Detail triangles", () => _detailTriBudget / 1000f, v => _detailTriBudget = (int)(v * 1000f), 5f, 5f, 250f, 60f, "0", " k"),
            Int("Max box labels", () => _maxCollisionLabels, v => _maxCollisionLabels = v, 0, 200, 48),
            Info("AI path lines (settings on AI path page)", () => _pathLineBudget.ToString()),
            Head("LEGEND"),
            Info("Road / terrain", () => ColName(_colRoad)),
            Info("Sector objects", () => ColName(_colObjects)),
            Info("Props & guardrails", () => ColName(_colProps)),
            Info("Fixed actors", () => ColName(_colFixed)),
            Info("Breakable actors", () => ColName(_colLoose)),
            Info("Cars", () => ColName(_colCar)),
            Info("AI path", () => ColName(_colPath)),
            Info("Far colliders", () => "dim group outline"),
            Info("AI path lines visible", () => _cntPath.ToString()),
        };
        return l;
    }

    /// <summary>Ein Tastendruck für alle sechs Kollisionsgruppen – die Overlays bleiben unangetastet.</summary>
    void SetAllCollisionGroups(bool on)
    {
        _boxRoad = _boxObjects = _boxProps = _boxFixed = _boxLoose = _boxCars = on;
        Toast(on ? "Collision groups: all ON" : "Collision groups: all OFF");
    }

    /// <summary>Kurzer KI-Pfad-Schalter für Traffic-, Debug-Views- und Kollisionsseiten (gleiche Variable überall).</summary>
    void AddAiPathToggles(List<Item> l)
    {
        l.Add(Bool("AI path system (traffic lanes)", () => _aiPaths, v => _aiPaths = v, false));
        l.Add(Info("AI path lines drawn", () => _cntPath.ToString()));
    }

    /// <summary>Einzige Seite für die KI-Pfad-Einstellungen – von Traffic, Collision und Debug Views erreichbar.</summary>
    List<Item> AiPathMenu()
    {
        return new List<Item>
        {
            Head("AI PATH SYSTEM (SHARED BY TRAFFIC + RACE AUTOPILOT)"),
            Bool("Draw AI path lines", () => _aiPaths, v => _aiPaths = v, false),
            Table("Path color", CollisionColorNames, () => _colPath, v => _colPath = v, 10),
            Int("Line budget", () => _pathLineBudget, v => _pathLineBudget = v, 100, 10000, 1500),
            Float("Range (uses collision range)", () => _collisionRange, v => _collisionRange = MathF.Max(20f, v), 25f, 20f, 1000f, 100f, "0", " m"),
            Info("Lines visible", () => _cntPath.ToString()),
            Info("Lane network", () => Traffic?.Lanes != null ? "loaded" : "-"),
            Head(""),
            Info("Same lane polylines that traffic and race opponents follow."),
            Info("Also reachable from Collision Debug View and Debug Views hub."),
        };
    }

    /// <summary>Quadrischer Abstand Punkt → Dreieck (Ericson). Wichtig: nicht den Schwerpunkt nehmen – bei einem langen
    /// Planken-/Straßendreieck kann der Schwerpunkt jenseits der Reichweite liegen, während eine Ecke direkt vor der
    /// Kamera ist. Dann verschwand das Dreieck genau beim Herangehen.</summary>
    static float DistSqToTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return ap.LengthSquared();
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return bp.LengthSquared();
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
        {
            float v = d1 / (d1 - d3);
            return (ap - ab * v).LengthSquared();
        }
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return cp.LengthSquared();
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
        {
            float w = d2 / (d2 - d6);
            return (ap - ac * w).LengthSquared();
        }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
        {
            float den = (d4 - d3) + (d5 - d6);
            float w = den > 1e-12f ? (d4 - d3) / den : 0f;
            return (p - (b + (c - b) * w)).LengthSquared();
        }
        float sum = va + vb + vc;
        if (MathF.Abs(sum) < 1e-12f) return ap.LengthSquared();
        float iv = 1f / sum;
        return (p - (a + ab * (vb * iv) + ac * (vc * iv))).LengthSquared();
    }

    /// <summary>
    /// Gebogene Leitplanken und einzelne Props sind eigene Dreiecksnetze. Deren AABB ist oft viel größer als das
    /// sichtbare Objekt – eine AABB-Attrappe wäre irreführend. Hier werden die echten Kollisionsdreiecke gezeichnet.
    /// Rückgabe: Anzahl gezeichneter Dreiecke (für das Budget pro Bild).
    /// </summary>
    static int DrawMeshTriangles(PhysicsWorld w, StaticCollider c, Vector3 eye, float r2, Vector4 color, int budget, int from = 0, int count = -1)
    {
        if (budget <= 0) return 0;
        if (w.Simulation.Shapes[c.Shape.Type] is not ShapeBatch<Mesh> batch) return 0;
        var st = w.Simulation.Statics[c.Handle];
        var basePos = w.ToGame(st.Pose.Position);
        var rot = st.Pose.Orientation;
        var tris = batch[c.Shape.Index].Triangles;
        int end = count < 0 ? tris.Length : Math.Min(tris.Length, from + count);
        int drawn = 0;
        for (int i = from; i < end && drawn < budget; i++)
        {
            var t = tris[i];
            var a = basePos + Vector3.Transform(t.A, rot);
            var b = basePos + Vector3.Transform(t.B, rot);
            var d = basePos + Vector3.Transform(t.C, rot);
            if (DistSqToTriangle(eye, a, b, d) > r2) continue;
            DL.Line(a, b, color); DL.Line(b, d, color); DL.Line(d, a, color);
            drawn++;
        }
        return drawn;
    }

    /// <summary>Beschriftung nur bei eingeschaltetem Schalter und solange das Budget reicht – sonst überfüllt die
    /// Anzeige bei vielen Sektor-Objekten.</summary>
    void Label(Vector3 at, string text, int colorIndex)
    {
        if (!_boxLabels || _labelsThisFrame >= _maxCollisionLabels) return;
        _labelsThisFrame++;
        DL.Label(at, text, Col(colorIndex));
    }

    /// <summary>Große Sektornetze als flacher Patch am 20-m-Raster – kein Quader, der in die Straße ragt.</summary>
    void DrawFlatPatch(StaticCollider c, Vector3 eye, float cap, int colorIndex, string text)
    {
        var cmin = c.BoundsCenter - c.BoundsHalf;
        var cmax = c.BoundsCenter + c.BoundsHalf;
        var anchor = new Vector3(MathF.Floor(eye.X / cap) * cap, 0f, MathF.Floor(eye.Z / cap) * cap);
        var nearest = Vector3.Max(cmin, Vector3.Min(anchor, cmax));
        var patch = new Vector3(cap);
        var mn = Vector3.Max(cmin, nearest - patch);
        var mx = Vector3.Min(cmax, nearest + patch);
        var center = (mn + mx) * 0.5f;
        var size = mx - mn;
        if (size.X < 0.02f || size.Z < 0.02f) return;
        size.X += 0.001f; size.Z += 0.001f;
        center.Y = nearest.Y + 0.02f;
        size.Y = 0.15f;
        DL.Box(center, Quaternion.Identity, size, Col(colorIndex));
        Label(center, text, colorIndex);
    }

    /// <summary>
    /// KI-Pfade: die tatsächlich befahrbaren Spuren des Straßennetzes (<see cref="TrafficLanes"/>) in der Nähe der
    /// Kamera – dasselbe Netz, dem Verkehr und Renn-Autopilot folgen. Läuft unabhängig von den Kollisionskästen.
    /// </summary>
    void CollectAiPathDebug()
    {
        _cntPath = 0;
        if (!_aiPaths) return;
        var tr = Traffic; var st = Streamer;
        if (tr?.Lanes == null || st == null) return;
        var lanes = tr.Lanes;
        var eye = st.ViewerGame;
        float r = MathF.Max(20f, _collisionRange), r2 = r * r;
        var col = Col(_colPath);
        int lineBudget = _pathLineBudget;
        foreach (int roadId in lanes.RoadsNear(eye, r))
        {
            if (!lanes.Net.Roads.TryGetValue(roadId, out var road)) continue;
            for (int f = 0; f < 2 && lineBudget > 0; f++)
            {
                bool forward = f == 0;
                int count = lanes.LaneCount(road, forward);
                for (int i = 0; i < count && lineBudget > 0; i++)
                {
                    var lane = lanes.Lane(roadId, forward, i);
                    if (lane == null || lane.P.Length < 2) continue;
                    for (int k = 0; k + 1 < lane.P.Length && lineBudget > 0; k++)
                    {
                        var a = lane.P[k]; var b = lane.P[k + 1];
                        if ((a - eye).LengthSquared() > r2 && (b - eye).LengthSquared() > r2) continue;
                        DL.Line(a + Vector3.UnitY * 0.25f, b + Vector3.UnitY * 0.25f, col);
                        lineBudget--; _cntPath++;
                    }
                }
            }
            if (lineBudget <= 0) break;
        }
    }

    /// <summary>Jedes Bild sammeln (auch bei geschlossenem Menü) – wie CollectCarDebug.</summary>
    void CollectCollisionDebug()
    {
        _labelsThisFrame = 0;
        CollectAiPathDebug();   // KI-Spuren laufen unabhängig davon, ob Kollisionskästen an sind
        if (!_collisionBoxes || _terrain.MapView ||
            !(_boxRoad || _boxObjects || _boxProps || _boxFixed || _boxLoose || _boxCars))
        {
            _cntRoad = _cntObjects = _cntProps = _cntFixed = _cntLoose = _cntCar = 0;
            return;
        }
        var m = Mgr; var st = Streamer;
        if (m == null || st == null) return;

        var eye = st.ViewerGame;
        float r = MathF.Max(20f, _collisionRange), r2 = r * r;
        _cntRoad = _cntObjects = _cntProps = _cntFixed = _cntLoose = _cntCar = 0;

        // Statische Netze: ALLES zeichnen, was Kollision hat. Nahe Objekte als echtes Dreiecksgitter, weiter entfernte
        // als kompakte AABB-Umrisslinie. So kann kein Kollider (Lampe, Baum, Haus, Leitplanke) mehr durch ein Budget
        // verschwinden. Sortierung nach Entfernung sorgt dafür, dass die Details dort landen, wo man hinschaut.
        var w = st.Physics;
        if (w != null && (_boxRoad || _boxObjects || _boxProps))
        {
            float cap = MathF.Min(r * 0.5f, 20f);
            float detail = MathF.Min(_detailDistance, r); float detailSq = detail * detail;
            var items = new List<StaticDraw>(128);
            foreach (var c in w.Statics)
            {
                if (c.Removed || c.Name.Contains(" Actor ", StringComparison.OrdinalIgnoreCase)) continue;
                bool road = c.Name.EndsWith(" Gelände", StringComparison.Ordinal);
                bool objects = c.Name.EndsWith(" Objekte", StringComparison.Ordinal);
                if (road ? !_boxRoad : !(_boxObjects && objects || _boxProps && !objects)) continue;
                var cmin = c.BoundsCenter - c.BoundsHalf;
                var cmax = c.BoundsCenter + c.BoundsHalf;
                float dx = MathF.Max(0f, MathF.Max(cmin.X - eye.X, eye.X - cmax.X));
                float dy = MathF.Max(0f, MathF.Max(cmin.Y - eye.Y, eye.Y - cmax.Y));
                float dz = MathF.Max(0f, MathF.Max(cmin.Z - eye.Z, eye.Z - cmax.Z));
                float dist2 = dx * dx + dy * dy + dz * dz;
                if (dist2 > r2) continue;

                if (road)
                {
                    items.Add(new StaticDraw { Collider = c, Center = c.BoundsCenter, Half = c.BoundsHalf, DistSq = dist2, ColorIndex = _colRoad, Flat = true, Label = "Sektor: Gelände/Straße" });
                    continue;
                }
                if (objects)
                {
                    if (c.DebugParts is { Length: > 0 })
                    {
                        foreach (var p in c.DebugParts)
                        {
                            var pmin = p.Center - p.Half; var pmax = p.Center + p.Half;
                            float px = MathF.Max(0f, MathF.Max(pmin.X - eye.X, eye.X - pmax.X));
                            float py = MathF.Max(0f, MathF.Max(pmin.Y - eye.Y, eye.Y - pmax.Y));
                            float pz = MathF.Max(0f, MathF.Max(pmin.Z - eye.Z, eye.Z - pmax.Z));
                            float pd2 = px * px + py * py + pz * pz;
                            if (pd2 > r2) continue;
                            items.Add(new StaticDraw { Collider = c, Center = p.Center, Half = p.Half, DistSq = pd2, From = p.Start * 2, Count = p.Count * 2, ColorIndex = _colObjects, Label = "Sektor-Objekt" });
                        }
                    }
                    else
                        items.Add(new StaticDraw { Collider = c, Center = c.BoundsCenter, Half = c.BoundsHalf, DistSq = dist2, ColorIndex = _colObjects, Flat = true, Label = "Sektor: Objekte (Patch)" });
                    continue;
                }
                items.Add(new StaticDraw { Collider = c, Center = c.BoundsCenter, Half = c.BoundsHalf, DistSq = dist2, From = 0, Count = -1, ColorIndex = _colProps, Label = c.Name });
            }
            items.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));

            int triBudget = _detailTriBudget;
            foreach (var it in items)
            {
                int ci = it.ColorIndex;
                if (it.Flat)
                {
                    bool isRoad = it.Label.StartsWith("Sektor: Gelände", StringComparison.Ordinal);
                    DrawFlatPatch(it.Collider, eye, cap, ci, it.Label);
                    if (isRoad) _cntRoad++; else _cntObjects++;
                    continue;
                }
                int total = it.Count < 0 ? it.Collider.Triangles : it.Count;
                if (it.DistSq <= detailSq && total > 0 && total <= triBudget)
                {
                    int used = DrawMeshTriangles(w, it.Collider, eye, r2, Col(ci), total, it.From, it.Count);
                    triBudget -= used;
                    if (used > 0)
                    {
                        Label(it.Center, it.Label, ci);
                        if (it.Count < 0) _cntProps++; else _cntObjects++;
                        continue;
                    }
                }
                // Entfernte Netze bleiben sichtbar: gedimmter Umriss in der Farbe der jeweiligen Gruppe.
                // So passt die Farbe immer zum An/Aus-Schalter, auch wenn das Dreiecksbudget aufgebraucht ist.
                var h = Vector3.Max(it.Half, new Vector3(0.02f));
                DL.Box(it.Center, Quaternion.Identity, h * 2f, Dim(Col(ci)));
                if (it.Count < 0) _cntProps++; else _cntObjects++;
            }
        }

        // Alle geladenen Actors: umfallbar (grün) und schwere Stehende (blau).
        var actors = WorldActorBodies.Current;
        if (actors != null && (_boxLoose || _boxFixed))
            foreach (var b in actors.DebugBoxes())
            {
                if ((b.center - eye).LengthSquared() > r2) continue;
                if (b.breakable && !_boxLoose || !b.breakable && !_boxFixed) continue;
                DL.Box(b.center, b.rot, b.size, Col(b.breakable ? _colLoose : _colFixed));
                Label(b.center, b.breakable ? "Actor: umfallbar" : "Actor: fest", b.breakable ? _colLoose : _colFixed);
                if (b.breakable) _cntLoose++; else _cntFixed++;
            }

        // Eigenes Auto: VisualPose (interpoliert wie das gerenderte Auto), nicht die rohe Physics-Pose – sonst zappelt
        // die Box alle FixedUpdate-Schritte vor/zurück, während das Auto selbst smooth läuft.
        var v = m.Current;
        var rb = v?.Rigidbody;
        if (_boxCars && v != null && rb != null && rb.Created)
        {
            var pose = v.VisualPose;
            // Extended-Modus: mehrere Kästen zeichnen (alle mit derselben interpolierten Pose).
            bool first = true;
            foreach (var b in rb.DebugBoxes)
            {
                var center = m.ToGame(pose.position + U.Rot(pose.rotation, b.center));
                if ((center - eye).LengthSquared() <= r2)
                {
                    DL.Box(center, U.Mirror(pose.rotation), b.size, Col(_colCar));
                    if (first) { Label(center, "Spieler-Auto", _colCar); first = false; }
                    _cntCar++;
                }
            }
        }

        void Cars(IEnumerable<(Vector3 center, Quaternion rot, Vector3 size)> boxes, string name)
        {
            foreach (var b in boxes)
                if ((b.center - eye).LengthSquared() <= r2)
                {
                    DL.Box(b.center, b.rot, b.size, Col(_colCar));
                    Label(b.center, name, _colCar);
                    _cntCar++;
                }
        }
        if (_boxCars)
        {
            var tr = Traffic;
            if (tr != null) Cars(tr.DebugBoxes(), "Verkehr");
            Cars(RemoteCars.DebugBoxes(), "Online");
            var race = RaceOpponents.Active;
            if (race != null) Cars(race.DebugBoxes(), "Rennen");
        }
    }
}