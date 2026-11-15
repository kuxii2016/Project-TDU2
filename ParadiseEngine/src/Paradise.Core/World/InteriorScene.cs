using Paradise.Core.Formats;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// Begehbare Interieurs (Euro/Bnk/interior/*.bnk): Autohändler (icd_*), Wohnbereich (lph_*/ich_*), Garage (gph_*/gch_*) ...
/// Port von Unity InteriorScene. entities.xmb → ExportType_Entity:
///   ArrayOf_Entity__FirstPerson   "FirstPerson": StartCamera = __Hc der Startkamera (Ich-Perspektive)
///   ArrayOf_Entity__Camera        cam_entree, cam_entree_garage, Cam_Hostess ... (orient_pos_scale, Fov)
///   ArrayOf_Entity__Volume__FpSelectable  Hotspots: Exit/ExitDoor (Type 0), Hostess_area (1), Garage (5), Living (6),
///                                 Diary (9), Dressing (10), Billboard (12/14), Bar (50), Office (51), Club (54), Media (57)
///                                 volume_mat = Spielraum → Einheitswürfel [-0,5, 0,5]³ (Spalten _0.._2, Translation _3)
///   ArrayOf_Entity__PosCam        D_CAR_01.. Stellplätze für Autos
///   ArrayOf_Entity__MyHouseObject Möbel (interior/myhouse/&lt;name ohne _NN&gt;.bnk)
/// Matrizen als System.Numerics (Zeilenvektoren): Unity-Spalte i = Zeile i. "vorne" = lokale +Z-Achse.
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
        /// <summary>Spielraum -> Einheitswürfel [-0,5, 0,5]³ (Original: Kastentest gegen ±0,5, unpacked.exe 0x9392C0 mit 0x149484C/0x1482258;
        /// vorher [-1,1]³ – alle Auswahlpunkte waren doppelt so groß, das Tagebuch füllte fast den ganzen Raum).</summary>
        public Matrix4x4 Volume = Matrix4x4.Identity;
        public ulong Camera, Cinematic;

        /// <summary>Strahl (Spielraum der Szene) gegen das Volumen; Abstand bis zum Eintritt.</summary>
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

    /// <summary>Ausstellungsauto auf Drehteller (Entity__Actor__ActorVehiclePosition, z.B. Casino-Lobby).</summary>
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

    /// <summary>Startpunkt der Ich-Perspektive: FirstPerson.StartCamera, sonst cam_entree, sonst erste Kamera.</summary>
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
                    case "ArrayOf_Entity__MyHouseObject": sc.HouseObjects.Add(Fill(new Ent(), it)); break;
                    case "ArrayOf_Entity__PNJ": sc.Npcs.Add(Fill(new Ent(), it)); break;
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
        sc.CarSlots.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return sc;
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
