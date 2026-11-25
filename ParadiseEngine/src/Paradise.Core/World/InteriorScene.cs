using Paradise.Core.Formats;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Walkable interiors (Euro/Bnk/interior/*.bnk): Car dealership (icd_*), living area (lph_*/ich_*), garage (gph_*/gch_*)
///   ArrayOf_Entity__FirstPerson   “FirstPerson”: StartCamera = __Hc of the start camera (first-person perspective)
///   ArrayOf_Entity__Camera        cam_entree, cam_entree_garage, Cam_Hostess ... (orient_pos_scale, Fov)
///   ArrayOf_Entity__Volume__FpSelectable  Hotspots: Exit/ExitDoor (Type 0), Hostess_area (1), Garage (5), Living (6),
///                                 Diary (9), Dressing (10), Billboard (12/14), Bar (50), Office (51), Club (54), Media (57)
///                                 volume_mat = Game space → unit cube [-0.5, 0.5]³ (columns _0.._2, translation _3)
///   ArrayOf_Entity__PosCam        D_CAR_01.. Parking spaces for cars
///   ArrayOf_Entity__MyHouseObject Furniture (interior/myhouse/&lt;name without _NN&gt;.bnk)
/// Matrices as System.Numerics (row vectors): Unity column i = row i. “front” = local +Z-axis.
/// </summary>
public sealed class InteriorScene
{
    public class Ent
    {
        public string Name = "";
        public ulong Hc;
        public Matrix4x4 M = Matrix4x4.Identity;    // Spielraum der Szene
        public XmbNode? Node;
        public Vector3 Position => M.Translation;
        public Vector3 Forward { get { var f = Vector3.TransformNormal(Vector3.UnitZ, M); return f.LengthSquared() > 1e-12f ? Vector3.Normalize(f) : Vector3.UnitZ; } }
    }

    public sealed class Selectable : Ent
    {
        public int Type;
        public string Str = "";
        /// <summary>Play area -> Unit cube [-0.5, 0.5]³ (Original: Box test against ±0.5, unpacked.exe 0x9392C0 with 0x149484C/0x1482258;
        /// previously [-1,1]³ – all selection points were twice as large; the diary filled almost the entire space).</summary>
        public Matrix4x4 Volume = Matrix4x4.Identity;
        public ulong Camera, Cinematic;

        /// <summary>Beam (scene space) against the volume; distance to the point of entry.</summary>
        public bool Raycast(Vector3 o, Vector3 d, out float t)
        {
            var lo = Vector3.Transform(o, Volume);
            var ld = Vector3.TransformNormal(d, Volume);
            float t0 = 0f, t1 = float.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                float oi = i == 0 ? lo.X : i == 1 ? lo.Y : lo.Z, di = i == 0 ? ld.X : i == 1 ? ld.Y : ld.Z;
                if (MathF.Abs(di) < 1e-8f)
                {
                    if (oi < -0.5f || oi > 0.5f) { t = 0; return false; }
                    continue;
                }
                float a = (-0.5f - oi) / di, b = (0.5f - oi) / di;
                if (a > b) (a, b) = (b, a);
                t0 = MathF.Max(t0, a); t1 = MathF.Min(t1, b);
                if (t0 > t1) { t = 0; return false; }
            }
            t = t0;
            return true;
        }

        public bool Contains(Vector3 p)
        {
            var l = Vector3.Transform(p, Volume);
            return MathF.Abs(l.X) <= 0.5f && MathF.Abs(l.Y) <= 0.5f && MathF.Abs(l.Z) <= 0.5f;
        }
    }

    /// <summary>
    /// Entity__Volume (ExportType_Volume, z.B. "Tess" in new_intro.bnk): volume_mat = Szenenraum -> Einheitsvolumen;
    /// OfType 1 Kugel, 2 Box, 3 Zylinder (Unity TDU2StoryScene.Volume.Contains, Original FUN_00b40e50).
    /// </summary>
    public sealed class Volume : Ent
    {
        public int OfType;
        public Matrix4x4 Mat = Matrix4x4.Identity;

        public bool Contains(Vector3 p)
        {
            var u = Vector3.Transform(p, Mat);
            return OfType switch
            {
                1 => Vector3.Dot(u, u) <= 1f,
                2 => MathF.Abs(u.X) <= 0.5f && MathF.Abs(u.Y) <= 0.5f && MathF.Abs(u.Z) <= 0.5f,
                3 => MathF.Abs(u.Y) <= 0.5f && u.X * u.X + u.Z * u.Z <= 1f,
                _ => false,
            };
        }
    }

    /// <summary>Display car on a turntable (Entity__Actor__ActorVehiclePosition, e.g., casino lobby).</summary>
    public sealed class ShowCar : Ent
    {
        public int VehicleId;
        public float TurnsPerMinute;
    }

    public string BankPath = "";
    public readonly List<ShowCar> ShowCars = new();
    public ulong StartCamera;
    public readonly List<Ent> Cameras = new();
    public readonly List<Ent> CarSlots = new();
    public readonly List<Ent> HouseObjects = new();
    public readonly List<Ent> Npcs = new();
    public readonly List<Selectable> Selectables = new();
    /// <summary>Entity__Volume (Story-Szenen: Auslöser wie "Tess").</summary>
    public readonly List<Volume> Volumes = new();
    /// <summary>Entity__CinCar (Story-Szenen: Auto-Darsteller wie CIN_CAR / PLAYER_CAR).</summary>
    public readonly List<Ent> CinCars = new();
    /// <summary>Entity__Light: Spotlichter der Szene für das Avatar-Licht (Avatars/AvatarLighting, Original-Container max. 64).</summary>
    public readonly List<Ent> Lights = new();

    public Ent? CarSlot(string name)
    {
        foreach (var c in CarSlots) if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    public Volume? FindVolume(string name)
    {
        foreach (var v in Volumes) if (v.Name == name) return v;
        return null;
    }

    public Ent? Camera(ulong hc)
    {
        foreach (var c in Cameras) if (c.Hc == hc) return c;
        return null;
    }

    public Ent? Camera(string name)
    {
        foreach (var c in Cameras) if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    /// <summary>Starting point for the first-person perspective: FirstPerson.StartCamera, otherwise cam_entree, otherwise the first camera.</summary>
    public Ent? StartCam => Camera(StartCamera) ?? Camera("cam_entree") ?? (Cameras.Count > 0 ? Cameras[0] : null);

    public static InteriorScene Parse(BnkArchive bank)
    {
        var sc = new InteriorScene { BankPath = bank.FilePath ?? "" };
        var e = bank.Find("entities.xmb");
        if (e == null) return sc;
        var root = XmbFile.Parse(bank.Read(e));
        var et = root?.Get("ExportType_Entity");
        if (et == null) return sc;
        foreach (var arr in et.Children)
        {
            if (!arr.IsArray) continue;
            foreach (var it in arr.Items)
            {
                switch (arr.Name)
                {
                    case "ArrayOf_Entity__FirstPerson": sc.StartCamera = it.U("StartCamera"); break;
                    case "ArrayOf_Entity__Camera": sc.Cameras.Add(Fill(new Ent(), it)); break;
                    case "ArrayOf_Entity__PosCam": sc.CarSlots.Add(Fill(new Ent(), it)); break;
                    case "ArrayOf_Entity__CinCar": sc.CinCars.Add(Fill(new Ent(), it)); break;
                    case "ArrayOf_Entity__MyHouseObject": sc.HouseObjects.Add(Fill(new Ent(), it)); break;
                    case "ArrayOf_Entity__PNJ": sc.Npcs.Add(Fill(new Ent(), it)); break;
                    case "ArrayOf_Entity__Light": sc.Lights.Add(Fill(new Ent(), it)); break;
                    case "ArrayOf_Entity__Actor__ActorVehiclePosition":
                        {
                            var v = Fill(new ShowCar(), it);
                            v.VehicleId = it.I("VehicleId"); v.TurnsPerMinute = it.F("NbTurnByMinute");
                            if (v.VehicleId > 0) sc.ShowCars.Add(v);
                            break;
                        }
                    case "ArrayOf_Entity__Volume__FpSelectable":
                        {
                            var s = Fill(new Selectable(), it);
                            s.Type = it.I("Type"); s.Str = it.S("SelectableString", "") ?? "";
                            s.Camera = it.U("Camera"); s.Cinematic = it.U("Cinematic");
                            Matrix4x4.Invert(s.M, out var inv);
                            var vm = it.Get("volume_mat");
                            if (vm != null)
                            {
                                // Unity: Spalte c = (X, Y, Z, W) -> Zeilenvektor-Matrix: Zeile c
                                var m = new Matrix4x4();
                                for (int c = 0; c < 4; c++)
                                {
                                    var col = vm.Get("_" + c);
                                    var v4 = col != null ? new Vector4(col.F("X"), col.F("Y"), col.F("Z"), col.F("W", c == 3 ? 1f : 0f)) : (c == 3 ? new Vector4(0, 0, 0, 1) : Vector4.Zero);
                                    SetRow(ref m, c, v4);
                                }
                                s.Volume = MathF.Abs(m.GetDeterminant()) > 1e-9f ? m : inv;
                            }
                            else s.Volume = inv;
                            sc.Selectables.Add(s);
                            break;
                        }
                }
            }
        }
        var vt = root.Get("ExportType_Volume")?.Get("ArrayOf_Entity__Volume");
        if (vt != null)
            foreach (var it in vt.Items)
            {
                var v = Fill(new Volume(), it);
                v.OfType = it.I("OfType");
                var vm = it.Get("volume_mat");
                var m = Matrix4x4.Identity;
                if (vm != null)
                    for (int c = 0; c < 4; c++)
                    {
                        var col = vm.Get("_" + c);
                        var v4 = col != null ? new Vector4(col.F("X"), col.F("Y"), col.F("Z"), col.F("W", c == 3 ? 1f : 0f)) : (c == 3 ? new Vector4(0, 0, 0, 1) : Vector4.Zero);
                        SetRow(ref m, c, v4);
                    }
                v.Mat = m;
                sc.Volumes.Add(v);
            }
        sc.CarSlots.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return sc;
    }

    /// <summary>
    /// Entity__Light -> Licht des Avatar-Sammelns wie das Original (Laden unpacked.exe 0x952cb3, Grundwerte 0 / isInterior 1;
    /// Adder 0xaf8c50: Lage = Übersetzung der Entity-Matrix, Richtung = −Y-Achse, Farben x Intensity). Szenenraum.
    /// </summary>
    public static Avatars.SceneLight LightOf(Ent e)
    {
        var n = e.Node;
        Vector3 V(string name)
        {
            var c = n?.Get(name);
            if (c == null) return Vector3.Zero;
            if (c.Get("X") != null) return new Vector3(c.F("X"), c.F("Y"), c.F("Z"));
            var f = c.Floats();
            return new Vector3(f.Length > 0 ? f[0] : 0f, f.Length > 1 ? f[1] : 0f, f.Length > 2 ? f[2] : 0f);
        }
        float F(string name, float def) => n?.Get(name) is XmbNode c ? c.AsFloat(def) : def;
        var y = new Vector3(e.M.M21, e.M.M22, e.M.M23);
        return Avatars.SceneLight.Create(e.Position, y, V("Ambient"), V("Diffuse"), V("Specular"), F("Intensity", 0f),
                                         F("DistanceDropOff", 0f), F("DistanceCutOff", 0f), F("AngularDropOff", 0f), F("AngularCutOff", 0f),
                                         F("isInterior", 1f) != 0f);
    }

    static void SetRow(ref Matrix4x4 m, int r, Vector4 v)
    {
        switch (r)
        {
            case 0: m.M11 = v.X; m.M12 = v.Y; m.M13 = v.Z; m.M14 = v.W; break;
            case 1: m.M21 = v.X; m.M22 = v.Y; m.M23 = v.Z; m.M24 = v.W; break;
            case 2: m.M31 = v.X; m.M32 = v.Y; m.M33 = v.Z; m.M34 = v.W; break;
            default: m.M41 = v.X; m.M42 = v.Y; m.M43 = v.Z; m.M44 = v.W; break;
        }
    }

    static T Fill<T>(T e, XmbNode it) where T : Ent
    {
        e.Name = it.S("__Name", "") ?? "";
        e.Hc = it.U("__Hc");
        e.Node = it;
        var ops = it.Get("orient_pos_scale");
        if (ops != null) e.M = BankModel.EntityMatrix(ops);
        return e;
    }
}