// Portiert aus dem Unity-Projekt (Runtime/Racing/TDU2RaceDebugView.cs).
using Paradise.Core.Diagnostics;
using Paradise.Core.Racing;
using Paradise.Core.Render;
using Paradise.Game.Gamemodes;
using Paradise.Game.Racing;
using Paradise.Game.World;
using System.Numerics;

namespace Paradise.Game.Screens;

public enum RaceGroup { RacingSchool, Championship, LicensedEvent, FreeRace }

/// <summary>
/// Race debug view for the developer menu: catalog of all .ig2 races, live race details, 3D route/gate/decor/
/// opponent visualisation in the running game, plus start and teleport helpers.
/// The overlay uses <see cref="DebugLines"/> (lines + labels, game coordinates).
/// </summary>
public sealed class RaceDebugView
{
    public static RaceDebugView Instance { get; } = new();

    public bool ShowInWorld = true;
    /// <summary>Also show the 3D labels while playing.</summary>
    public bool ShowOverlay = true;
    public bool ShowRoute = true;
    /// <summary>Use the road-following route (RaceRoadPath) instead of the straight checkpoint line.</summary>
    public bool ShowRoadRoute = true;
    public bool ShowGates = true;
    public bool ShowDecor = true;
    public bool ShowOpponents = true;
    public bool ShowLabels = true;
    /// <summary>Objects farther away than this from the camera are not drawn (m).</summary>
    public float ViewDistance = 900f;
    /// <summary>World labels are only drawn up to this distance (m).</summary>
    public float LabelDistance = 250f;

    RaceRepository? _repo;
    string? _selectedName;
    RaceDefinition? _selected;
    string _status = "not loaded";
    readonly List<string> _files = new();
    readonly Dictionary<string, LicenseInfo> _license = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, RaceGroup> _group = new(StringComparer.OrdinalIgnoreCase);

    public bool Loaded => _repo != null && _files.Count > 0;
    public string Status => _status;
    public string? SelectedName => _selectedName;
    public RaceDefinition? Selected => _selected;
    public IReadOnlyList<string> Files => _files;
    public RaceGroup SelectedGroup => _selectedName != null && _group.TryGetValue(_selectedName, out var g) ? g : RaceGroup.FreeRace;
    public string SelectedGroupName => GroupName(SelectedGroup);

    public static string GroupName(RaceGroup g) => g switch
    {
        RaceGroup.RacingSchool => "RACING SCHOOL",
        RaceGroup.Championship => "CHAMPIONSHIP",
        RaceGroup.LicensedEvent => "LICENSED EVENT",
        _ => "FREE RACE / SPECIAL",
    };

    // ------------------------------------------------------------------ catalog
    public void Refresh()
    {
        _selected = null;
        _selectedName = null;
        _files.Clear();
        _license.Clear();
        _group.Clear();
        try { _repo = GAME_MODE_SCHOOL.Instance.EnsureRepository(); }
        catch (Exception ex) { _repo = null; _status = "loading failed: " + ex.Message; return; }
        if (_repo == null) { _status = "no bnk folder yet"; return; }
        _files.AddRange(_repo.AllNames());
        if (_files.Count == 0) { _status = "ig2 folder not found: " + _repo.Ige2Dir; return; }
        foreach (var lic in _repo.Database.Licenses)
            foreach (var cf in lic.ChallengeFiles)
            {
                string key = Path.GetFileNameWithoutExtension(cf ?? "");
                if (key.Length > 0 && !_license.ContainsKey(key)) _license[key] = lic;
            }
        foreach (var name in _files)
        {
            _license.TryGetValue(name, out var lic);
            _group[name] = lic == null ? RaceGroup.FreeRace
                : lic.IsRacingSchool ? RaceGroup.RacingSchool
                : lic.IsChampionship ? RaceGroup.Championship
                : RaceGroup.LicensedEvent;
        }
        _status = _files.Count + " .ig2 files · " + _repo.Database.Licenses.Count + " licenses";
    }

    public List<string> FilesInGroup(RaceGroup g)
    {
        var list = new List<string>();
        foreach (var n in _files) if (_group.TryGetValue(n, out var f) && f == g) list.Add(n);
        return list;
    }

    public string HintFor(string file) => _license.TryGetValue(file, out var lic) ? lic.DisplayName : "";

    public bool Select(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (_repo == null) Refresh();
        if (_repo == null) { _status = "race data not available"; return false; }
        _license.TryGetValue(name, out var lic);
        try { _selected = _repo.Build(name, lic); }
        catch (Exception ex) { _selected = null; _status = "error in " + name + ": " + ex.Message; return false; }
        if (_selected == null) { _status = name + ".ig2 could not be parsed"; return false; }
        _selectedName = name;
        ShowInWorld = true;
        _status = "selected " + name;
        return true;
    }

    /// <summary>Select the next (+1) or previous (-1) .ig2 file in the catalog.</summary>
    public bool Cycle(int direction)
    {
        if (_files.Count == 0) { Refresh(); if (_files.Count == 0) return false; }
        int i = _selectedName != null ? _files.IndexOf(_selectedName) : -1;
        i = ((i + direction) % _files.Count + _files.Count) % _files.Count;
        return Select(_files[i]);
    }

    /// <summary>Select the next/previous file inside the current group.</summary>
    public bool CycleGroup(int direction)
    {
        var list = FilesInGroup(SelectedGroup);
        if (list.Count == 0) return false;
        int i = _selectedName != null ? list.IndexOf(_selectedName) : -1;
        i = ((i + direction) % list.Count + list.Count) % list.Count;
        return Select(list[i]);
    }

    // ------------------------------------------------------------------ start pose / actions
    public bool TryGetStartPose(out Vector3 pos, out Vector3 forward)
    {
        pos = Vector3.Zero; forward = Vector3.UnitZ;
        return _selected != null && GAME_MODE_SCHOOL.Instance.TryGetStartPose(_selected, out pos, out forward);
    }

    public string StartWorldText() => TryGetStartPose(out var p, out _) ? p.X.ToString("0") + " / " + p.Y.ToString("0") + " / " + p.Z.ToString("0") : "no start position";

    public string StartRace()
    {
        if (_selected == null) return "no race selected";
        if (_selected.Checkpoints.Count == 0) return "race has no checkpoints";
        if (!GAME_MODE_SCHOOL.Instance.StartTestDirect(_selected)) return "race could not be started (world / vehicles not ready yet?)";
        return "starting " + _selected.Title;
    }

    public string TeleportCarToStart(TerrainModule t, VehicleModule v)
    {
        var m = v.Manager;
        if (m == null) return "no vehicle manager";
        if (_selected == null) return "no race selected";
        if (!TryGetStartPose(out var pos, out var fwd)) return "race has no start position";
        if (m.Current != null) m.TeleportSafe(pos, fwd);
        else
        {
            if (m.FindCar(m.StartCar) == null) return "no car available";
            m.SpawnCar(m.StartCar, pos, fwd);
        }
        MoveCamera(t, pos, fwd, 8f, 3f);
        return "car teleported to " + _selected.Title;
    }

    public string MoveCameraToStart(TerrainModule t, bool freeCamera)
    {
        if (_selected == null) return "no race selected";
        if (!TryGetStartPose(out var pos, out var fwd)) return "race has no start position";
        if (freeCamera) t.FreeFly = true;
        MoveCamera(t, pos, fwd, 10f, 4f);
        return freeCamera ? "free camera at " + _selected.Title : "camera moved to " + _selected.Title;
    }

    static void MoveCamera(TerrainModule t, Vector3 pos, Vector3 fwd, float back, float up)
    {
        var cam = t.Camera; var st = t.Streamer;
        if (cam == null || st == null) return;
        var eye = pos - fwd * back + Vector3.UnitY * up;
        cam.Position = st.GameToRender(eye);
        var look = pos + Vector3.UnitY * 1.5f - eye;
        if (look.LengthSquared() > 1e-6f)
        {
            look = Vector3.Normalize(look);
            cam.Yaw = MathF.Atan2(look.X, look.Z);
            cam.Pitch = MathF.Asin(System.Math.Clamp(look.Y, -1f, 1f));
        }
    }

    public string ComputeRoute(VehicleModule v)
    {
        if (_selected == null) return "no race selected";
        try
        {
            RaceRoadPath.Lanes = v.Traffic?.Lanes;
            RaceRoadPath.Compute(_selected);
            int n = _selected.RoadPath?.Count ?? 0;
            return n > 1 ? "road route: " + n + " points" : "no road route found (using checkpoint line)";
        }
        catch (Exception ex) { return "route failed: " + ex.Message; }
    }

    // ------------------------------------------------------------------ runtime overlay (DebugLines)
    static Vector4 C(float r, float g, float b, float a = 1f) => new(r, g, b, a);
    static Vector4 Lerp(Vector4 a, Vector4 b, float t) => a + (b - a) * System.Math.Clamp(t, 0f, 1f);

    /// <summary>Jedes Bild (DevMenu.Update nach VehicleModule): Linien/Beschriftungen der gewählten Strecke sammeln.</summary>
    public void Collect(TerrainModule t)
    {
        var def = _selected;
        if (!ShowInWorld || def == null) return;
        var cam = t.Camera; var st = t.Streamer;
        if (cam == null || st == null) return;
        var camPos = st.RenderToGame(cam.Position);
        bool Near(Vector3 p) => Vector3.Distance(camPos, p) <= ViewDistance;

        if (ShowRoute && def.Checkpoints.Count > 1)
        {
            if (ShowRoadRoute && def.RoadPath != null && def.RoadPath.Count > 1)
            {
                int stride = System.Math.Max(1, def.RoadPath.Count / 300);
                for (int i = 0; i + stride < def.RoadPath.Count; i += stride)
                {
                    var a = def.RoadPath[i];
                    var b = def.RoadPath[System.Math.Min(i + stride, def.RoadPath.Count - 1)];
                    if (!Near(a) && !Near(b)) continue;
                    float k = i / (float)System.Math.Max(1, def.RoadPath.Count - 1);
                    DebugLines.Line(a, b, Lerp(C(0.2f, 1f, 1f, 0.8f), C(1f, 0.85f, 0.2f, 0.8f), k));
                }
            }
            else
            {
                for (int i = 1; i < def.Checkpoints.Count; i++)
                {
                    var a = def.Checkpoints[i - 1].Position;
                    var b = def.Checkpoints[i].Position;
                    if (Near(a) || Near(b)) DebugLines.Line(a, b, C(0.2f, 1f, 1f, 0.8f));
                }
            }
        }

        if (ShowGates)
        {
            foreach (var cp in def.Checkpoints)
            {
                if (!Near(cp.Position)) continue;
                float half = MathF.Max(2.5f, cp.Width * 0.5f);
                float height = cp.IsBrakingZone ? 2f : 5f;
                var col = cp.IsStart ? C(1f, 0.92f, 0.016f) : cp.IsFinish ? C(0.3f, 1f, 0.3f) : cp.IsBrakingZone ? C(1f, 0.35f, 0.2f) : C(1f, 1f, 1f);
                for (int s = 0; s <= 4; s++)
                {
                    float a0 = (s - 2) / 2f;   // -1, -0.5, 0, 0.5, 1
                    var p0 = cp.Position + cp.Right * half * a0;
                    DebugLines.Line(p0, p0 + Vector3.UnitY * height, col);
                    if (s < 4)
                    {
                        float a1 = (s - 1) / 2f;
                        var p1 = cp.Position + cp.Right * half * a1;
                        DebugLines.Line(p0 + Vector3.UnitY * height, p1 + Vector3.UnitY * height, col);
                        DebugLines.Line(p0, p1, col);
                    }
                }
                DebugLines.Ray(cp.Position, cp.Forward, 6f, C(1f, 1f, 1f, 0.45f));
            }
        }

        if (ShowDecor)
        {
            int n = 0;
            foreach (var d in def.Decors)
            {
                if (n++ > 600) break;
                if (!Near(d.From)) continue;
                var col = d.IsSeparator ? C(1f, 0.5f, 0.1f) : C(1f, 0.85f, 0.2f);
                if (d.IsLine)
                {
                    DebugLines.Line(d.From, d.To, col);
                    DebugLines.Line(d.From + Vector3.UnitY * 0.8f, d.To + Vector3.UnitY * 0.8f, col);
                }
                else DebugLines.Circle(d.From + Vector3.UnitY * 0.4f, 0.4f, col, 10);
            }
        }

        if (ShowOpponents)
            foreach (var o in def.Opponents)
            {
                if (!Near(o.Position)) continue;
                var col = C(1f, 0.3f, 0.3f);
                DebugLines.Circle(o.Position + Vector3.UnitY * 0.9f, 1.1f, col, 16);
                DebugLines.Ray(o.Position + Vector3.UnitY * 0.9f, o.Forward, 4f, col);
            }

        // Start arrow (thick enough to spot from the air).
        if (TryGetStartPose(out var start, out var fwd))
        {
            var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, fwd));
            var y = C(1f, 0.92f, 0.016f);
            DebugLines.Line(start, start + fwd * 12f, y);
            DebugLines.Line(start + fwd * 12f, start + fwd * 9f + right * 2.5f, y);
            DebugLines.Line(start + fwd * 12f, start + fwd * 9f - right * 2.5f, y);
            if (ShowOverlay && ShowLabels) DebugLines.Label(start + Vector3.UnitY * 6f, def.Title + "  (" + SelectedGroupName + ")", C(1f, 1f, 1f));
        }

        if (ShowOverlay && ShowLabels)
        {
            int shown = 0;
            foreach (var cp in def.Checkpoints)
            {
                if (shown++ > 40) break;
                if (Vector3.Distance(camPos, cp.Position) > LabelDistance) continue;
                DebugLines.Label(cp.Position + Vector3.UnitY * 5.5f, (cp.IsStart ? "START" : cp.IsFinish ? "FINISH" : "CP " + cp.Index) + " · " + cp.Width.ToString("0") + " m", C(1f, 1f, 1f));
            }
        }
    }
}
