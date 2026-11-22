using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;
using Paradise.Core.Render;
using System.Numerics;

namespace Paradise.Core.World;

/// <summary>
/// C#-Port der rotierenden Windrad-Rotoren aus dem C++-Referenzcode (EdenWorld::loadSector / ::draw).
/// Die Sektorbank enthält eine verschachtelte "&lt;Sector&gt;_sc.bnk" mit entities.xmb
/// (ArrayOf_Entity__Actor__RotatingActor) und dem Rotormodell (fu_eolea_act); das Sektormodell
/// selbst besteht nur aus Turm/Gondel.
///
/// Alle Matrizen bleiben im Spielkoordinatensystem. Der Renderpfad von MapStreamer multipliziert
/// anschließend mit RootMatrix – deshalb wird hier bewusst KEIN RootOffset / keine World-Shift-
/// Translation eingebaut. Beim Verschieben des Render-Ursprungs drehen die Rotoren einfach weiter
/// und werden vom bestehenden Root-Matrix-Pfad korrekt verschoben.
/// </summary>
public static class WindTurbines
{
    const string ActorArray = "ArrayOf_Entity__Actor__RotatingActor";

    /// <summary>Ein Rotor aus entities.xmb (sowie die vom C++-Code gelesenen, aber nur fürs Logging genutzten Felder).</summary>
    public sealed class ActorDef
    {
        public string Name = "";
        /// <summary>Rotation + Position des Actors (ohne Scale) in Sektorkoordinaten, Zeilenvektor-Konvention.</summary>
        public Matrix4x4 RotationTranslation;
        public Vector3 Scale = Vector3.One;
        /// <summary>RotationSpeed aus entities.xmb (rad/s).</summary>
        public float Speed;
        /// <summary>Wie im C++-Code: Phase = Index * 1.7.</summary>
        public float Phase;
        public float WindCoeff = 1f;
        public float MinAngle = float.NaN;
        public float MaxAngle = float.NaN;
        public float Pause;
    }

    /// <summary>CPU-Daten einer verschachtelten _sc-Bank.</summary>
    public sealed class BankData
    {
        public BankModel Model = null!;
        public readonly List<ActorDef> Actors = new();
    }

    /// <summary>Alles, was ein Sektor an Rotor-Daten aus dem Hintergrund-Thread liefert.</summary>
    public sealed class SectorData
    {
        public readonly List<BankData> Banks = new();
        public int Count
        {
            get
            {
                int n = 0;
                foreach (var b in Banks) n += b.Actors.Count;
                return n;
            }
        }
    }

    internal sealed class RotorPart
    {
        public RenderObject Object = null!;
        public Matrix4x4 BaseModel;      // nodeLocal * Scale (ohne Drehung/Platzierung)
        public Vector3 BaseMin, BaseMax, BaseCenter;
    }

    internal sealed class RotorInstance
    {
        readonly ActorDef _def;
        readonly Matrix4x4 _origin;
        readonly RotorPart[] _parts;

        public RotorInstance(ActorDef def, Vector3 origin, List<RotorPart> parts)
        {
            _def = def;
            _origin = Matrix4x4.CreateTranslation(origin);
            _parts = parts.ToArray();
        }

        /// <summary>clock = Zeit seit Start der Welt ( Sekunden ); identisch zur C++-steady_clock.</summary>
        public void Apply(float clock)
        {
            float angle = _def.Phase + _def.Speed * clock;
            // C++: RT(rot,pos) * spin(Y) * Scale. In der Zeilenvektor-Konvention der Engine
            // (base = nodeLocal * Scale) ist die Reihenfolge base * spin * RT * origin.
            var toWorld = Matrix4x4.CreateRotationY(angle) * _def.RotationTranslation * _origin;
            foreach (var p in _parts)
            {
                p.Object.Model = p.BaseModel * toWorld;
                ObjectBuilder.TransformBounds(p.BaseMin, p.BaseMax, toWorld, out var mn, out var mx);
                p.Object.BoundsMin = mn;
                p.Object.BoundsMax = mx;
                p.Object.Center = Vector3.Transform(p.BaseCenter, toWorld);
            }
        }
    }

    /// <summary>Live-Zustand eines Sektors: Node wird wie gewohnt von CollectSector gezeichnet.</summary>
    public sealed class SectorRuntime
    {
        public readonly MapStreamer.Node Node = new() { Name = "Rotors" };
        readonly List<RotorInstance> _rotors = new();
        readonly List<BankModel> _models = new();

        public int Count => _rotors.Count;

        public void AddModel(BankModel m) => _models.Add(m);
        internal void Add(RotorInstance r) => _rotors.Add(r);
        public void Tick(float clock)
        {
            for (int i = 0; i < _rotors.Count; i++) _rotors[i].Apply(clock);
        }
        public void Release()
        {
            _rotors.Clear();
            Node.Objects.Clear();
            foreach (var m in _models) m.Release();
            _models.Clear();
        }
    }

    /// <summary>Hintergrund-Thread: verschachtelte _sc-Bänke und ihre Rotor-Actoren lesen.</summary>
    public static SectorData? Load(BankModel sector, string logicalPath, GlobalTextureCache textures)
    {
        if (sector.Bank == null) return null;
        var result = new SectorData();

        foreach (var nested in sector.Bank.Entries)
        {
            if (!string.Equals(nested.Extension, ".bnk", StringComparison.OrdinalIgnoreCase)) continue;
            BankModel? rotor = null;
            try
            {
                var bytes = sector.Bank.Read(nested);
                string path = logicalPath + "::" + nested.Name;
                var bank = BnkArchive.Open(bytes, path);
                var actors = ReadActors(bank);
                if (actors.Count == 0) continue;   // wie C++: andere verschachtelte Bänke ignorieren

                // Der C++-Code nimmt die erste 3dd der _sc-Bank und die gleichnamige 3dg/2dm.
                var dd = bank.All(".3dd").FirstOrDefault();
                if (dd == null) continue;
                string stem = Stem(dd);
                var dg = bank.All(".3dg").FirstOrDefault(e => string.Equals(Stem(e), stem, StringComparison.OrdinalIgnoreCase))
                         ?? bank.All(".3dg").FirstOrDefault();
                if (dg == null) continue;
                var dm = bank.All(".2dm").FirstOrDefault(e => string.Equals(Stem(e), stem, StringComparison.OrdinalIgnoreCase));

                rotor = new BankModel { Path = path, Kind = BankKind.Generic, Bank = bank };
                foreach (var e in bank.All(".2db"))
                {
                    var def = TextureDef.Parse(bank.Read(e), e.Name);
                    if (def != null) rotor.Textures.Add(def);
                }
                rotor.Objects = ObjectSet.Load(bank, dd, dg, dm, rotor.Log);

                textures.IndexBank(bank);
                try { TextureAnimations.PrepareUva(bank); } catch (Exception) { }

                var bankData = new BankData { Model = rotor };
                bankData.Actors.AddRange(actors);
                result.Banks.Add(bankData);
                Log.Info($"Windräder: {actors.Count} Rotoren in {nested.Name} ({dd.Name})");
            }
            catch (Exception ex)
            {
                Log.Warn($"Windräder {nested.Name}: {ex.Message}");
                rotor?.Release();
            }
        }
        return result.Banks.Count == 0 ? null : result;
    }

    static string Stem(BnkEntry e) => e.Name.Length > e.Extension.Length ? e.Name[..^e.Extension.Length] : e.Name;

    static List<ActorDef> ReadActors(BnkArchive bank)
    {
        var empty = new List<ActorDef>();
        var entEntry = bank.Find("entities.xmb");
        if (entEntry == null) return empty;
        var root = XmbFile.Parse(bank.Read(entEntry));

        XmbNode? arr = null;
        foreach (var section in root.Children)
        {
            if (!string.Equals(section.Name, "ExportType_Entity", StringComparison.Ordinal)) continue;
            arr = section.Get(ActorArray);
            if (arr != null) break;
        }
        if (arr == null || !arr.IsArray) return empty;

        var actors = new List<ActorDef>(arr.Items.Count);
        for (int i = 0; i < arr.Items.Count; i++)
        {
            var it = arr.Items[i];
            var ops = it.Get("orient_pos_scale");
            if (ops == null) continue;
            var p = ops.Get("pos");
            var r = ops.Get("rot");
            var sc = ops.Get("scale");
            var pos = p != null ? new Vector3(p.F("X"), p.F("Y"), p.F("Z")) : Vector3.Zero;
            var q = r != null ? new Quaternion(-r.F("X"), -r.F("Y"), -r.F("Z"), r.F("W", 1f)) : Quaternion.Identity;
            float qm = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
            q = qm < 1e-6f ? Quaternion.Identity : new Quaternion(q.X / qm, q.Y / qm, q.Z / qm, q.W / qm);
            var scale = sc != null ? new Vector3(sc.F("X", 1f), sc.F("Y", 1f), sc.F("Z", 1f)) : Vector3.One;

            actors.Add(new ActorDef
            {
                Name = it.S("__Name", "") ?? "",
                RotationTranslation = Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(pos),
                Scale = scale,
                Speed = it.F("RotationSpeed"),
                Phase = i * 1.7f,
                WindCoeff = it.F("WindCoeff", 1f),
                MinAngle = it.F("MinAngle", float.NaN),
                MaxAngle = it.F("MaxAngle", float.NaN),
                Pause = it.F("Pause"),
            });
        }
        return actors;
    }

    /// <summary>Haupt-Thread: GPU-Objekte bauen und pro Actor eine eigene dynamische Matrix verwalten.</summary>
    public static SectorRuntime? Build(SectorData data, Vector3 origin, GlobalTextureCache textures,
                                       BuildResources res, ObjectBuildOptions opt, IRenderer renderer)
    {
        if (data.Count == 0) return null;
        var runtime = new SectorRuntime();
        foreach (var bank in data.Banks)
        {
            runtime.AddModel(bank.Model);
            foreach (var actor in bank.Actors)
            {
                var parts = new List<RotorPart>();
                // Scale ist Teil der Basis: anschließend wird nur noch spin * RT * origin angehängt.
                ObjectBuilder.BuildObjects(bank.Model, Matrix4x4.CreateScale(actor.Scale), textures, res, opt, renderer, o =>
                {
                    parts.Add(new RotorPart
                    {
                        Object = o,
                        BaseModel = o.Model,
                        BaseMin = o.BoundsMin,
                        BaseMax = o.BoundsMax,
                        BaseCenter = o.Center,
                    });
                });
                if (parts.Count == 0) continue;
                foreach (var p in parts) runtime.Node.Objects.Add(p.Object);
                runtime.Add(new RotorInstance(actor, origin, parts));
            }
        }
        if (runtime.Count == 0)
        {
            runtime.Release();
            return null;
        }
        runtime.Tick(0f);
        return runtime;
    }
}
