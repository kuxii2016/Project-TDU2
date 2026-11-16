using Paradise.Core.Diagnostics;
using Paradise.Core.Files;
using Paradise.Core.Formats;
using Paradise.Core.Math;
using Paradise.Core.Render;
using Paradise.Core.World;
using System.Numerics;

namespace Paradise.Core.Vehicles
{
    /// <summary>A parsed scene (3dd + 3dg + 2dm) with pre-processed mesh data (LOD 0 only). Portiert aus dem Unity-Projekt (CarModel.cs).</summary>
    public sealed class CarScene
    {
        public Scene3DD Scene = null!;
        public Geometry3DG Geo = null!;
        public Material2DM? Materials;
        public readonly Dictionary<ulong, MeshData?> Meshes = new Dictionary<ulong, MeshData?>();

        public static CarScene? Load(BnkArchive? bank, string? stemHint = null)
        {
            if (bank == null) return null;
            BnkEntry? dd = null, dg = null, dm = null;
            foreach (var e in bank.Entries)
            {
                string stem = e.Name.Substring(0, System.Math.Max(0, e.Name.Length - e.Extension.Length));
                if (stemHint != null && !stem.Equals(stemHint, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Extension == ".3dd" && dd == null) dd = e;
                else if (e.Extension == ".3dg" && dg == null) dg = e;
                else if (e.Extension == ".2dm" && dm == null && !stem.EndsWith("_color", StringComparison.OrdinalIgnoreCase)) dm = e;
            }
            if (dd == null || dg == null) return stemHint != null ? Load(bank, null) : null;
            var s = new CarScene { Scene = Scene3DD.Parse(bank.Read(dd)), Geo = Geometry3DG.Parse(bank.Read(dg)) };
            if (dm != null) s.Materials = Material2DM.Parse(bank.Read(dm));
            foreach (var off in s.Scene.InsOffsets)
            {
                var ins = s.Scene.GetIns(off);
                if (ins.Lods.Length == 0) continue;
                var k = ins.Lods[0].ObjKey;
                if (s.Meshes.ContainsKey(k)) continue;
                try { s.Meshes[k] = s.Geo.BuildMesh(k); } catch (Exception) { s.Meshes[k] = null; }
            }
            return s;
        }
    }

    /// <summary>
    /// Gemeinsame Fahrzeug-Ressourcen aus vehicules/common_car.bnk, einmal geladen und von allen Autos geteilt:
    /// Material-Bibliothek common_mesh.2dm (z.B. Kennzeichen PLATE_LR/PLATE_LR_01, Federn SPRG_xx) und die Texturen
    /// (plate, plate_lr, ws_plate, ws_sprg_alpha …).
    /// </summary>
    public static class CommonCarAssets
    {
        static readonly object _lock = new object();
        static string? _dir;
        static Material2DM? _lib;
        static LocalTextureSet? _tex;

        public static Material2DM? Materials => _lib;

        /// <summary>Laden (thread-sicher, auch im Hintergrund). bnkDir = Euro/Bnk.</summary>
        public static void Ensure(string? bnkDir)
        {
            if (string.IsNullOrEmpty(bnkDir)) return;
            lock (_lock)
            {
                if (_dir == bnkDir) return;
                _dir = bnkDir;
                _lib = null; _tex = null;
                try
                {
                    var path = CarDatabase.CommonCarBank(bnkDir);
                    if (path == null) return;
                    var b = BnkArchive.Open(path);
                    var lib = new Material2DM();
                    foreach (var e in b.All(".2dm"))
                    {
                        try { foreach (var kv in Material2DM.Parse(b.Read(e)).Materials) if (!lib.Materials.ContainsKey(kv.Key)) lib.Materials[kv.Key] = kv.Value; }
                        catch (Exception) { }
                    }
                    var set = new LocalTextureSet();
                    foreach (var e in b.All(".2db"))
                    {
                        try { var def = TextureDef.Parse(b.Read(e), System.IO.Path.GetFileNameWithoutExtension(e.Name)); if (def != null) set.Add(def); }
                        catch (Exception) { }
                    }
                    _lib = lib; _tex = set;
                }
                catch (Exception ex) { Log.Warn("common_car.bnk: " + ex.Message); }
            }
        }

        /// <summary>Euro/Bnk aus einem Pfad darunter (z.B. Verkehrsbank islands/…/traffic/…).</summary>
        public static string? BnkDirFrom(string anyPath)
        {
            try
            {
                var d = System.IO.Path.GetDirectoryName(anyPath);
                while (!string.IsNullOrEmpty(d))
                {
                    if (GameFiles.DirectoryExists(System.IO.Path.Combine(d, "vehicules"))) return d;
                    d = System.IO.Path.GetDirectoryName(d);
                }
            }
            catch (Exception) { }
            return null;
        }

        public static MaterialDef? Material(ulong key) => key != 0 && _lib != null && _lib.Materials.TryGetValue(key, out var m) ? m : null;

        /// <summary>Hauptthread.</summary>
        public static IGpuTexture? GetTexture(ulong key, TextureWrap u, TextureWrap v, IRenderer r)
            => key != 0 && _tex != null && _tex.TryGet(key, u, v, r, out var t) ? t : null;
    }

    /// <summary>All vehicle data (loaded in the background): model, interior, rims, tires, textures, torque, sound.</summary>
    public sealed class CarAssets
    {
        public CarPhysics Car = null!;
        public RimInfo? Rim;
        public CarColor? Color;
        public InteriorInfo? Interior;
        public CarScene? Body, Cockpit, RimFront, RimRear, Tire, TireRear;
        public Material2DM? ColorLib;
        /// <summary>
        /// Factory decals (DBCarPhysicsData._sticker, e.g., “rsa_CCXR” for racing school cars, police): 1024-pixel decal map from
        /// scenarics/stickers/&lt;name&gt;.2db (EXE: “%sscenarics/Stickers/%s.2db”). Applied to the paint materials.
        /// </summary>
        public TextureDef? Sticker;
        public string? StickerName;
        /// <summary>common_car.bnk/dirt_scratch.2db: R = Dirt (outside/bottom first), G = Scratches.</summary>
        public TextureDef? DirtMap;
        public Curve? TorqueMax, TorqueMin;
        public CarSoundConfig? Sound;
        public GaugeConfig? Gauges;
        public readonly Dictionary<string, (float[] data, int channels, int rate)> Waves = new Dictionary<string, (float[], int, int)>(StringComparer.OrdinalIgnoreCase);
        public readonly List<LocalTextureSet> TextureSets = new List<LocalTextureSet>();
        public readonly List<string> Log = new List<string>();
        /// <summary>Traffic car (islands/island/vehicules/traffic): a model with wheels (DISC_xx) in the scene, no rims or interior seats.</summary>
        public bool Traffic;
        /// <summary>Renderer für die GPU-Texturen (in Unity implizit). Wird von <see cref="CarModel.Build"/> gesetzt.</summary>
        public IRenderer? Renderer;

        /// <summary>
        /// Load traffic car: Model from islands/island/vehicles/traffic/_car_model/_car_version/.bnk,
        /// Sound from sound/vehicles/name&gt;_high.bnk, otherwise vehicles/med/generic_traffic_low.bnk.
        /// </summary>
        public static CarAssets LoadTraffic(string bankPath, string? soundBank, CarPhysics car)
        {
            var a = new CarAssets { Car = car, Traffic = true };
            CommonCarAssets.Ensure(CommonCarAssets.BnkDirFrom(bankPath));   // Kennzeichen (PLATE_LR …) und andere gemeinsame Materialien
            var bank = BnkArchive.Open(bankPath);
            string stem = System.IO.Path.GetFileNameWithoutExtension(bankPath);
            a.Body = CarScene.Load(bank, stem);
            a.AddTextures(bank);
            if (soundBank != null)
            {
                try
                {
                    var sb = BnkArchive.Open(soundBank);
                    var cfgEntry = sb.Find("CarVSTConfig.xmb");
                    if (cfgEntry != null) a.Sound = CarSoundConfig.Parse(sb.Read(cfgEntry));
                    if (a.Sound != null)
                    {
                        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var s in a.Sound.Samples) if (s.Wave != null) needed.Add(s.Wave);
                        foreach (var e in sb.Entries)
                        {
                            if (!needed.Contains(e.Name)) continue;
                            if (Audio.WavDecoder.TryDecode(sb.Read(e), out var data, out int ch, out int rate) && data != null) a.Waves[e.Name] = (data, ch, rate);
                        }
                    }
                }
                catch (Exception ex) { a.Log.Add("Sound: " + ex.Message); }
            }
            return a;
        }

        /// <param name="rimId">Wheel (DBRims._hashcode, 0 = car's default wheel)</param>
        /// <param name="interiorId">Interior (DBInterior._hashcode, 0 = first interior for the paint color)</param>
        public static CarAssets Load(string bnkDir, CarDatabase db, CarPhysics car, int colorIndex, bool loadSound, int rimId = 0, int interiorId = 0)
        {
            var a = new CarAssets { Car = car };
            CommonCarAssets.Ensure(bnkDir);
            // Rims: selected rim (car dealer) or the car's standard rim (DBRims)
            if (rimId != 0) db.Rims.TryGetValue(rimId, out a.Rim);
            if (a.Rim == null && !db.Rims.TryGetValue(car.DefaultRims, out a.Rim) && db.CarRims.TryGetValue(car.Id, out var rl) && rl.Count > 0)
                db.Rims.TryGetValue(rl[0], out a.Rim);
            // Special models (racing school, police, etc.) have their own IDs without a color table: _duplicate = base car (colors/rims)
            int dup = car.Raw != null ? car.Raw.I("_duplicate", 0) : 0;
            if (a.Rim == null && dup != 0 && db.CarRims.TryGetValue(dup, out var drl) && drl.Count > 0) db.Rims.TryGetValue(drl[0], out a.Rim);
            if ((!db.Colors.TryGetValue(car.Id, out var cols) || cols.Count == 0) && dup != 0) db.Colors.TryGetValue(dup, out cols);
            if (cols != null && cols.Count > 0)
            {
                a.Color = cols[((colorIndex % cols.Count) + cols.Count) % cols.Count];
                if (interiorId == 0 || Array.IndexOf(a.Color.Interiors, interiorId) < 0 || !db.Interiors.TryGetValue(interiorId, out a.Interior))
                    db.Interiors.TryGetValue(a.Color.Interiors[0], out a.Interior);
            }

            string sticker = car.Raw != null ? car.Raw.S("_sticker", "") ?? "" : "";
            if (!string.IsNullOrEmpty(sticker) && sticker != "##")
            {
                try
                {
                    var sf = CarDatabase.FindFile(System.IO.Path.Combine(bnkDir, "scenarics", "stickers"), sticker + ".2db");
                    if (sf != null) { a.Sticker = TextureDef.Parse(GameFiles.ReadAllBytes(sf), sticker); a.StickerName = sticker; }
                    else a.Log.Add("Beklebung " + sticker + ".2db nicht gefunden (scenarics/stickers)");
                }
                catch (Exception ex) { a.Log.Add("Beklebung " + sticker + ": " + ex.Message); }
            }

            var carBankPath = CarDatabase.CarBank(bnkDir, car) ?? throw new System.IO.FileNotFoundException("Fahrzeugbank fehlt: vehicules/high/" + car.FileName + ".bnk");
            var carBank = BnkArchive.Open(carBankPath);
            a.Body = CarScene.Load(carBank, car.FileName);
            a.AddTextures(carBank);
            foreach (var e in carBank.Entries)
            {
                var n = e.Name.ToLowerInvariant();
                if (e.Extension == ".2dm" && n.EndsWith("_color.2dm")) a.ColorLib = Material2DM.Parse(carBank.Read(e));
                else if (n.EndsWith("_max.xmb")) { try { a.TorqueMax = TorqueCurves.Parse(carBank.Read(e)); } catch (Exception ex) { a.Log.Add(ex.Message); } }
                else if (n.EndsWith("_min.xmb")) { try { a.TorqueMin = TorqueCurves.Parse(carBank.Read(e)); } catch (Exception ex) { a.Log.Add(ex.Message); } }
            }
            if (a.TorqueMax == null) a.TorqueMax = TorqueCurves.Synthetic(car);
            if (a.TorqueMin == null) a.TorqueMin = TorqueCurves.SyntheticBrake(car);

            TryLoad(a, CarDatabase.InteriorBank(bnkDir, car), b => a.Cockpit = CarScene.Load(b));
            TryLoad(a, CarDatabase.RimBank(bnkDir, a.Rim, true), b => a.RimFront = CarScene.Load(b));
            if (a.Rim != null && !string.Equals(a.Rim.FileFront, a.Rim.FileRear, StringComparison.OrdinalIgnoreCase))
                TryLoad(a, CarDatabase.RimBank(bnkDir, a.Rim, false), b => a.RimRear = CarScene.Load(b));
            TryLoad(a, CarDatabase.TireBank(bnkDir, a.Rim != null ? a.Rim.DiameterFront : 18f), b => a.Tire = CarScene.Load(b));
            if (a.Rim != null && Mathf.RoundToInt(a.Rim.DiameterRear) != Mathf.RoundToInt(a.Rim.DiameterFront))
                TryLoad(a, CarDatabase.TireBank(bnkDir, a.Rim.DiameterRear), b => a.TireRear = CarScene.Load(b));
            TryLoad(a, CarDatabase.CommonCarBank(bnkDir), b =>
            {
                // Dirt/scratch mask for the car body (R = dirt, G = scratches), same process as the sticker map
                var de = b.Find("dirt_scratch.2db");
                if (de != null) a.DirtMap = TextureDef.Parse(b.Read(de), "dirt_scratch");
            });
            try
            {
                var gb = CarDatabase.GaugeBank(bnkDir, car);
                if (gb != null)
                {
                    var bank = BnkArchive.Open(gb);
                    var ini = bank.Find("hud.ini");
                    if (ini != null) a.Gauges = GaugeConfig.Parse(System.Text.Encoding.ASCII.GetString(bank.Read(ini)));
                }
            }
            catch (Exception ex) { a.Log.Add("hud.ini: " + ex.Message); }

            if (loadSound) LoadSound(a, bnkDir, car);
            return a;
        }

        /// <summary>
        /// Load engine/driving sounds (CarVSTConfig.xmb + WAV files from the sound bank) into existing assets—e.g., for the display car
        /// at the dealership (getting in: start the engine, accelerate from a standstill, honk the horn). May run in the background.
        /// </summary>
        public static void LoadSound(CarAssets? a, string bnkDir, CarPhysics? car)
        {
            if (a == null || car == null) return;
            var sb = CarDatabase.SoundBank(bnkDir, car);
            if (sb == null) { a.Log.Add("Keine Soundbank für " + car.FileName); return; }
            try
            {
                var bank = BnkArchive.Open(sb);
                var cfgEntry = bank.Find("CarVSTConfig.xmb");
                var sound = cfgEntry != null ? CarSoundConfig.Parse(bank.Read(cfgEntry)) : null;
                if (sound == null) return;
                var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in sound.Samples) if (s.Wave != null) needed.Add(s.Wave);
                foreach (var e in bank.Entries)
                {
                    if (!needed.Contains(e.Name)) continue;
                    if (Audio.WavDecoder.TryDecode(bank.Read(e), out var data, out int ch, out int rate) && data != null) a.Waves[e.Name] = (data, ch, rate);
                    else a.Log.Add("WAV nicht lesbar: " + e.Name);
                }
                a.Sound = sound;
            }
            catch (Exception ex) { a.Log.Add("Sound: " + ex.Message); }
        }

        static void TryLoad(CarAssets a, string? path, Action<BnkArchive> use)
        {
            if (path == null) return;
            try
            {
                var b = BnkArchive.Open(path);
                use(b);
                a.AddTextures(b);
            }
            catch (Exception ex) { a.Log.Add(System.IO.Path.GetFileName(path) + ": " + ex.Message); }
        }

        void AddTextures(BnkArchive bank)
        {
            var set = new LocalTextureSet();
            foreach (var e in bank.All(".2db"))
            {
                var def = TextureDef.Parse(bank.Read(e), System.IO.Path.GetFileNameWithoutExtension(e.Name));
                if (def != null) set.Add(def);
            }
            TextureSets.Add(set);
        }

        /// <summary>Main thread. Textures by filename (without .2db) from all loaded banks (e.g., “shadow” from common_car.bnk).</summary>
        public IGpuTexture? FindTexture(string name, TextureWrap u = TextureWrap.Clamp, TextureWrap v = TextureWrap.Clamp)
        {
            if (Renderer == null) return null;
            foreach (var s in TextureSets)
                foreach (var kv in s.Defs)
                    if (string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase) && s.TryGet(kv.Key, u, v, Renderer, out var t)) return t;
            return null;
        }

        public IGpuTexture? GetTexture(ulong key, TextureWrap u, TextureWrap v)
        {
            if (key == 0 || Renderer == null) return null;
            foreach (var s in TextureSets) if (s.TryGet(key, u, v, Renderer, out var t)) return t;
            return CommonCarAssets.GetTexture(key, u, v, Renderer);   // gemeinsame Texturen (Kennzeichen, Federn …), nicht pro Auto zerstört
        }

        public void DestroyTextures()
        {
            foreach (var s in TextureSets) s.Destroy();
        }
    }

    /// <summary>
    /// Material eines Fahrzeugteils (in Unity: Material mit Shader TDU2/Car bzw. TDU2/CarGlass). Die Properties stehen in
    /// <see cref="Desc"/>; nach Änderungen <see cref="Apply"/> aufrufen.
    /// </summary>
    public sealed class CarMaterial : IDisposable
    {
        public string Name = "Car";
        public readonly MaterialDesc Desc;
        public IGpuMaterial? Gpu;
        public CarMaterial(MaterialKind kind) { Desc = new MaterialDesc { Kind = kind }; }
        /// <summary>Kopie (Unity: new Material(m)).</summary>
        public CarMaterial(CarMaterial src) { Desc = src.Desc.Clone(); Name = src.Name; }
        /// <summary>Unity: shader == TDU2/Car (alle Car-Properties vorhanden).</summary>
        public bool IsCarShader => Desc.Kind == MaterialKind.Car;
        public IGpuTexture? MainTexture { get => Desc.Main; set => Desc.Main = value; }

        /// <summary>GPU-Material erzeugen bzw. aktualisieren (Main-Thread).</summary>
        public void Apply(IRenderer r)
        {
            Desc.Name = Name;
            if (Gpu == null) Gpu = r.CreateMaterial(Desc);
            else r.UpdateMaterial(Gpu, Desc);
        }

        public void Dispose() { Gpu?.Dispose(); Gpu = null; }
    }

    /// <summary>Materials for vehicles (paint, chrome, glass, interior) based on the .2dm parameters.</summary>
    public static class CarMaterials
    {
        /// <summary>Lackverlauf RAMPCLR1 -> RAMPCLR2 (RAMPPOW.x) im Shader TDU2/Car benutzen.</summary>
        public static bool PaintRamp = true;
        /// <summary>Wird während des Aufbaus von Felgen gesetzt (Rad-Materialien wie im Original: Farbe = RAMPCLR1, auch schwarz;
        /// keine Chrom-Spiegelung bei ENVCOEFF 0).</summary>
        public static bool WheelPass;
        static readonly ulong SemFlake = Bin.KeyFromString("FLAKE");
        static readonly ulong SemTexLight = Bin.KeyFromString("TEXLIGHT");
        /// <summary>lgt_g: Function mask for the light lenses – R = Brake light, G = Reverse light, B = Light (headlight/taillight), A = 0 → Turn signal.</summary>
        static readonly ulong SemGlossLight = MaterialDef.KeyFromName("GLOSSLIGHT");
        static readonly ulong SemGloss = Bin.KeyFromString("GLOSS");

        static IGpuTexture? _white;
        static IRenderer? _whiteOwner;

        /// <summary>Unity Texture2D.whiteTexture (1x1 weiß), einmal je Renderer.</summary>
        public static IGpuTexture WhiteTexture(IRenderer r)
        {
            if (_white == null || _whiteOwner != r)
            {
                _white = r.CreateTexture(new TextureUpload { Width = 1, Height = 1, Format = GpuTextureFormat.Rgba8, Blocks = new byte[] { 255, 255, 255, 255 }, Name = "white" });
                _whiteOwner = r;
            }
            return _white;
        }

        /// <summary>Original-Lackformel (TD_CarPaint) statt Unity-Standard verwenden.</summary>
        public static bool TdPaint = true;
        /// <summary>Flocken-Variante von TD_CarPaint2 (Game/Config GraphicsConfig.OriginalCarFlakes). Gilt für neu geladene Autos.</summary>
        public static bool OriginalFlakes;
        /// <summary>Außenglas nach dem Original-Shader der Familie TD_CarPaint (GraphicsConfig.OriginalCarGlass). Neu geladene Autos.</summary>
        public static bool OriginalGlass;
        /// <summary>Shader-Familie TD_CarPaint (Material +0x310), z.B. Glass/Glass_SK/Glass_Opaque der Autos.</summary>
        const ulong ShaderTdCarPaint = 0x183157b81c6c7357UL;

        static bool TryParam(MaterialDef a, MaterialDef? b, string name, out Vector4 v)
        {
            ulong k = ParamKey(name);
            if (a.Params.TryGetValue(k, out v)) return true;
            if (b != null && b.Params.TryGetValue(k, out v)) return true;
            v = default; return false;
        }

        /// <summary>
        /// Parameterschlüssel wie im Spiel: 8 Bytes; längere Namen werden aufaddiert (Zeichen i landet auf Byte i % 8),
        /// z.B. SPECBLINN -> 'S'+'N' = 0xA1 "PECBLIN", RAMPFLAKE, FLAKEABNS.
        /// </summary>
        static ulong ParamKey(string name)
        {
            var b = new byte[8];
            for (int i = 0; i < name.Length; i++) b[i % 8] = (byte)(b[i % 8] + (byte)name[i]);
            return BitConverter.ToUInt64(b, 0);
        }

        static bool Dxt1(IGpuTexture? t) => t != null && t.Format == GpuTextureFormat.Bc1;

        public static CarMaterial Create(MaterialDef? def, Func<ulong, TextureWrap, TextureWrap, IGpuTexture?> getTex, MaterialDef? paint, MaterialDef? tint = null, bool interior = false)
        {
            if (def == null) return new CarMaterial(MaterialKind.Car) { Name = "Car_Default" };

            var src = paint ?? def;   // The paint color from <car>_color.2dm replaces the “Paint” material
            // Interior: Apply only the color; retain the texture of the original cockpit material (seat/door stitching,
            // “Dots” perforations, etc., come from the cockpit texture, not from the color material).
            var colorSrc = tint ?? src;
            if (tint != null) src = def;
            MaterialFactory.GetBlend(def, out float alphaRef, out bool twoSided);
            var f = def.Flags;
            bool additive = f[8] == 5 && f[9] == 2;
            bool alphaBlend = f[8] == 5 && f[9] == 6;
            string name = (def.Name ?? "").ToLowerInvariant();

            IGpuTexture? tex = null, overlay = null;
            MaterialLayer layer;
            // Road cars (TD_CarDeform_TS): SHADOW = body shading (white, UV0) x paint color, DETAIL = highlights/detail atlas
            // with alpha on the second UV set on top. (Brake discs Mat_TS_Disk_xx have SHADOW = DETAIL = the same disc texture:
            // in that case, just the texture, no overlay)
            if (src.TryGetLayer(MaterialDef.SemShadow, out var shl) && shl.TextureKey != 0 && src.TryGetLayer(MaterialDef.SemDetail, out var dtl) && dtl.TextureKey != 0
                && shl.TextureKey != dtl.TextureKey)
            {
                tex = getTex(shl.TextureKey, TextureDef.WrapFromEden(shl.AddressU), TextureDef.WrapFromEden(shl.AddressV));
                overlay = getTex(dtl.TextureKey, TextureDef.WrapFromEden(dtl.AddressU), TextureDef.WrapFromEden(dtl.AddressV));
            }
            else if (src.TryGetLayer(MaterialDef.SemDetail, out layer) || src.TryGetLayer(MaterialDef.SemColor, out layer))
                tex = getTex(layer.TextureKey, TextureDef.WrapFromEden(layer.AddressU), TextureDef.WrapFromEden(layer.AddressV));

            // Base color: RAMPCLR1 (ramp color) > material's diffuse color (MAT.+0xC0) > texture/white.
            // Interior materials carry their color ONLY in the diffuse block, not as RAMPCLR1 (which is (0,0,0) there).
            // Outside the interior: no RAMPCLR1 + no color texture = black (tire/rim materials).
            Vector4 col = Vector4.One;
            // Tire profile (rim/tire/t_<inches>p_oh, material T_SpCa_O): DETAIL is lr_ambocc_* = packed shading (R sharp,
            // G/B softer) and no color texture. The texture is the tread pattern: bright rings (up to 1.0) in R, which as
            // an albedo looked like white stripes over the tread. Rubber is black, so the texture now only modulates a very
            // dark base color (0.05) -> black tire with subtile shading, no white tread.
            bool aoMask = tex != null && tex.Name != null && tex.Name.IndexOf("ambocc", StringComparison.OrdinalIgnoreCase) >= 0;
            if (aoMask) col = new Vector4(0.05f, 0.05f, 0.05f, 1f);
            else if (colorSrc.TryGetParam("RAMPCLR1", out var rc) && (rc.X + rc.Y + rc.Z) > 0.001f) col = new Vector4(rc.X, rc.Y, rc.Z, 1f);
            else if (interior && (colorSrc.Diffuse.X + colorSrc.Diffuse.Y + colorSrc.Diffuse.Z) > 0.001f)
                col = new Vector4(colorSrc.Diffuse.X, colorSrc.Diffuse.Y, colorSrc.Diffuse.Z, 1f);
            else if (tex == null && colorSrc.TryGetParam("RAMPCLR1", out var rc0)) col = new Vector4(rc0.X, rc0.Y, rc0.Z, 1f);

            // Felgen: RAMPCLR1 gilt immer, auch (0,0,0) mit Textur.
            bool wheel = WheelPass && !aoMask && tint == null && paint == null;
            if (wheel && colorSrc.TryGetParam("RAMPCLR1", out var wc)) col = new Vector4(wc.X, wc.Y, wc.Z, 1f);

            float smooth = 0.35f, metal = 0f;
            if (src.TryGetParam("SPECPARM", out var sp) && sp.X > 0f) smooth = Mathf.Clamp01(Mathf.Log10(Mathf.Max(1f, sp.X)) / 3.1f);
            bool flake = src.TryGetLayer(SemFlake, out _);
            bool isPaint = (paint != null && tint == null) || CarModel.IsPaintName(name) || flake;
            if (isPaint) { smooth = 0.88f; metal = flake ? 0.35f : 0.1f; }
            if (name.Contains("chrome") || name.Contains("metal") || name.Contains("alu")) { metal = 0.9f; smooth = Mathf.Max(smooth, 0.8f); }
            if (name.Contains("mirror")) { metal = 1f; smooth = 0.97f; col = new Vector4(0.8f, 0.8f, 0.8f, 1f); }
            // Felge ohne Umgebungsspiegelung (ENVCOEFF 0): kein Chrom
            bool wheelNoEnv = wheel && (!src.TryGetParam("ENVCOEFF", out var env) || (env.X + env.Y + env.Z + env.W) < 0.001f);
            if (wheelNoEnv) { metal = Mathf.Min(metal, 0.3f); smooth = Mathf.Min(smooth, 0.6f); }

            CarMaterial m;
            // Außenspiegel: Form im Alpha der Detail-Textur (DXT5) bzw. bei leerer DETAIL-Ebene in GLOSS (DXT1 -> Rotkanal).
            IGpuTexture? mirrorMask = null;
            if (name.Contains("mirror"))
            {
                if (tex != null && !Dxt1(tex)) mirrorMask = tex;   // DETAIL mit Alpha-Umriss
                if (mirrorMask == null && src.TryGetLayer(SemGloss, out var gll) && gll.TextureKey != 0)
                    mirrorMask = getTex(gll.TextureKey, TextureDef.WrapFromEden(gll.AddressU), TextureDef.WrapFromEden(gll.AddressV));
            }
            bool mirrorCut = mirrorMask != null;
            bool mirrorRed = mirrorCut && mirrorMask != tex && Dxt1(mirrorMask);
            if (mirrorCut) tex = mirrorMask;
            // Glass with an opaque render status (blending off, e.g., traffic cars without interiors): opaque and reflective
            bool opaqueGlass = name.Contains("glass") && !alphaBlend && !additive && f[10] == 6 && f[8] == 2 && f[9] == 1
                               && (tex == null || Dxt1(tex));   // Glastextur ohne Alpha
            if (mirrorCut)
            {
                m = new CarMaterial(MaterialKind.Car);
                m.Desc.Glossiness = smooth;
                m.Desc.Metallic = metal;
                m.Desc.Cutoff = f[7] > 2 ? Mathf.Clamp(alphaRef, 0.05f, 0.9f) : 0.5f;
                if (mirrorRed) m.Desc.AlphaFromRed = 1f;
            }
            else if (opaqueGlass)
            {
                m = new CarMaterial(MaterialKind.Car);
                if (tex == null) col = new Vector4(0.08f, 0.09f, 0.1f, 1f);
                m.Desc.Glossiness = 0.95f;
                m.Desc.Metallic = 0.2f;
                m.Desc.ClearCoat = 0.8f;
            }
            else if (alphaBlend || additive || name.Contains("glass"))
            {
                m = new CarMaterial(MaterialKind.CarGlass);
                if (tex == null && col == Vector4.One) col = new Vector4(0.12f, 0.13f, 0.14f, 1f);
                float alpha = tex != null && !Dxt1(tex) ? 1f : 0.35f;
                col.W = alpha;
                if (additive) { col.W = 0.6f; }
                m.Desc.Glossiness = 0.95f;
            }
            else
            {
                m = new CarMaterial(MaterialKind.Car);
                m.Desc.Glossiness = smooth;
                m.Desc.Metallic = metal;
                bool cutout = f[5] == 0 && f[7] > 2;
                if (cutout) m.Desc.Cutoff = Mathf.Clamp(alphaRef, 0.05f, 0.9f);
            }
            m.Desc.Color = col;
            // Lackverlauf wie im Original: RAMPCLR1 frontal, RAMPCLR2 streifend, Exponent RAMPPOW.x
            if ((isPaint || wheel) && PaintRamp && m.IsCarShader && colorSrc.TryGetParam("RAMPPOW", out var rpw) && rpw.X >= 1f
                && colorSrc.TryGetParam("RAMPCLR2", out var rc2) && (rc2.X + rc2.Y + rc2.Z) > 0.001f)
            {
                m.Desc.Color2 = new Vector4(rc2.X, rc2.Y, rc2.Z, 1f);
                m.Desc.RampPow = Mathf.Min(rpw.X, 16f);
            }
            if (tex != null) m.MainTexture = tex;
            // Original-Glas TD_CarGlassDirt: Farbe = Spiegelung (MATPARAM wie beim Lack) + Glanz (SPECPARM), Deckkraft = Textur-Alpha
            // Nicht für Leuchten-Gläser (glass_Light): deren Farbe (rot/orange Rück-/Blinkerglas) steckt in der Glastextur, die diese
            // Formel nicht nutzt – die Gläser wurden sonst weiß/fleckig. Sie laufen wie im Unity-Projekt über TDU2/CarGlass.
            // Original-Glas der Außenhaut (Familie TD_CarPaint, TD_CarPaint.sob ps @201040, Docs\re\car_glass_spec.md §2):
            //  Basis = lerp(lerp(Rampe, DETAIL·LAYRCLR1, DETAIL.a·LAYRCLR1.w), DETAIL2, DETAIL2.a)  (DETAIL2 = G_I, schwarz deckend)
            //  Farbe = Basis × lerp(Ambient, Diffus, N·L·Schatten) + Glanz (DirLight specular) + Spiegelung × Fresnel·ENVCOEFF·MATPARAM
            //  Deckkraft = DETAIL.a (SrcAlpha/InvSrcAlpha)
            if (m.Desc.Kind == MaterialKind.CarGlass && tex != null && TdPaint && OriginalGlass && def.ShaderSet == ShaderTdCarPaint
                && !name.Contains("light") && def.TryGetParam("MATPARAM", out var omp) && def.TryGetParam("SPECPARM", out var osp))
            {
                m.Desc.TdPaint = 3f;
                m.Desc.TdMatParam = omp; m.Desc.TdSpecParm = osp;
                m.Desc.TdEnvCoeff = Vector4.One;   // zur Laufzeit: x = car_envmap_factor (TDU2CarLighting), y wie Material
                m.Desc.TdLayrClr = def.TryGetParam("LAYRCLR1", out var oly) ? oly : Vector4.One;
                if (def.TryGetLayer(MaterialDef.SemDetail2, out var d2) && d2.TextureKey != 0)
                    m.Desc.Detail2Tex = getTex(d2.TextureKey, TextureDef.WrapFromEden(d2.AddressU), TextureDef.WrapFromEden(d2.AddressV));
                if (def.TryGetParam("RAMPCLR1", out var gr1)) m.Desc.Color = new Vector4(gr1.X, gr1.Y, gr1.Z, m.Desc.Color.W);
                if (def.TryGetParam("RAMPCLR2", out var gr2)) m.Desc.Color2 = new Vector4(gr2.X, gr2.Y, gr2.Z, 1f);
                if (def.TryGetParam("RAMPPOW", out var grp)) m.Desc.RampPow = grp.X;
            }
            else if (m.Desc.Kind == MaterialKind.CarGlass && tex != null && !Dxt1(tex) && TdPaint && !name.Contains("light")
                && def.TryGetParam("MATPARAM", out var gmp) && def.TryGetParam("SPECPARM", out var gsp))
            {
                m.Desc.TdPaint = 1f;
                m.Desc.TdMatParam = gmp; m.Desc.TdSpecParm = gsp;
            }
            // Original-Lack: TD_CarPaint2 (Lack mit RAMPCLR3, SPECBLINN) bzw. TD_CarPaint (SPECPARM). ENVCOEFF steht in allen
            // Materialien (Auto-Bank, colors.bnk) auf 0 – das Spiel setzt ihn zur Laufzeit global; neutral = (1,1,1,1).
            if (isPaint && TdPaint && m.Desc.Kind == MaterialKind.Car && overlay == null && TryParam(colorSrc, src, "MATPARAM", out var tmp))
            {
                bool paint2 = TryParam(colorSrc, src, "RAMPCLR3", out _);
                Vector4 tsp;
                bool hasSpec = paint2 ? TryParam(colorSrc, src, "SPECBLINN", out tsp) : TryParam(colorSrc, src, "SPECPARM", out tsp);
                TryParam(colorSrc, src, "ENVCOEFF", out var tec);
                if (tec.X == 0f && tec.Y == 0f && tec.Z == 0f && tec.W == 0f) tec = Vector4.One;
                if (hasSpec || paint2)
                {
                    m.Desc.TdPaint = paint2 ? 2f : 1f;
                    m.Desc.TdMatParam = tmp; m.Desc.TdEnvCoeff = tec; m.Desc.TdSpecParm = tsp;
                    m.Desc.TdLayrClr = TryParam(colorSrc, src, "LAYRCLR1", out var tly) ? tly : Vector4.One;
                    m.Desc.TdLayer = tex != null ? 1f : 0f;
                    if (TryParam(colorSrc, src, "RAMPCLR1", out var t1)) m.Desc.Color = new Vector4(t1.X, t1.Y, t1.Z, 1f);
                    m.Desc.Color2 = TryParam(colorSrc, src, "RAMPCLR2", out var t2) ? new Vector4(t2.X, t2.Y, t2.Z, 1f) : m.Desc.Color;
                    m.Desc.RampPow = TryParam(colorSrc, src, "RAMPPOW", out var tpw) ? tpw.X : 0f;
                    // Flocken-Variante des Originals: Material mit FLAKE-Ebene (gMicroFlakesTexSampler) + FLAKEABNS/RAMPFLAKE
                    if (paint2 && OriginalFlakes && TryParam(colorSrc, src, "FLAKEABNS", out var fab) && TryParam(colorSrc, src, "RAMPFLAKE", out var rfl)
                        && (src.TryGetLayer(SemFlake, out var fl) || def.TryGetLayer(SemFlake, out fl)) && fl.TextureKey != 0)
                    {
                        m.Desc.FlakeTex = getTex(fl.TextureKey, TextureWrap.Repeat, TextureWrap.Repeat);
                        m.Desc.TdFlake = 1f;
                        m.Desc.TdFlakeABNS = fab;
                        m.Desc.TdRampFlake = rfl;
                        m.Desc.TdRampPowY = tpw.Y;
                    }
                }
            }
            if (aoMask && m.IsCarShader) { m.Desc.MainGray = 1f; m.Desc.Glossiness = 0.18f; m.Desc.Metallic = 0f; }
            if (overlay != null && m.IsCarShader)
            {
                m.Desc.StickerTex = overlay;
                m.Desc.UseSticker = 1f;
                m.Desc.StickerAlphaOnly = 1f;
                m.Desc.StickerUV = 1f;
            }
            // Light glasses: TEXLIGHT = “on” version of the light texture (lgt_on) -> emission, controlled by script
            if (m.IsCarShader && def.TryGetLayer(SemTexLight, out var tl) && tl.TextureKey != 0)
            {
                var on = getTex(tl.TextureKey, TextureWrap.Clamp, TextureWrap.Clamp);
                if (on != null) { m.Desc.EmissionTex = on; m.Desc.Emission = 0f; m.Desc.EmissionDiff = 1f; }
                if (on != null && def.TryGetLayer(SemGlossLight, out var gl) && gl.TextureKey != 0)
                {
                    // Function mask (Unity: FilterMode.Point; der Shader schneidet hart mit step(0.5))
                    var mask = getTex(gl.TextureKey, TextureWrap.Clamp, TextureWrap.Clamp);
                    if (mask != null)
                    {
                        m.Desc.LightMask = mask;
                        m.Desc.UseMask = 1f;
                    }
                }
            }
            // _Cull: die Engine zeichnet Fahrzeugteile beidseitig (Normalen im Shader gespiegelt)
            // Paint: additional clear coat (reflection + highlight), chrome with a slight sheen
            if (m.IsCarShader)
            {
                if (isPaint) m.Desc.ClearCoat = 1f;
                if (isPaint && flake) m.Desc.Flake = 0.6f;   // Metallic-Lack (FLAKE-Schicht)
                else if (metal > 0.8f) m.Desc.ClearCoat = 0.35f;
            }
            m.Name = string.IsNullOrEmpty(def.Name) ? "Car" : def.Name;
            if (isPaint)
            {
                var ps = new System.Text.StringBuilder();
                foreach (var kv in src.Params) ps.Append($" {Bin.KeyToString(kv.Key)}=({kv.Value.X:0.###};{kv.Value.Y:0.###};{kv.Value.Z:0.###};{kv.Value.W:0.###})");
                Log.Info($"LACK {def.Name} (Original-Formel {(m.Desc.TdPaint > 1.5f ? "TD_CarPaint2" : m.Desc.TdPaint > 0.5f ? "TD_CarPaint" : "aus")}, Kind {m.Desc.Kind}, Sticker {(overlay != null ? "ja" : "nein")}):{ps}");
            }
            if (wheel)
                Log.Debug($"FELGE {m.Name}: Shader {m.Desc.Kind}, Farbe ({col.X:0.00},{col.Y:0.00},{col.Z:0.00},{col.W:0.00}), Textur {(tex != null ? tex.Name + " " + tex.Format : "keine")}, "
                    + $"metallisch {metal:0.00}, glatt {smooth:0.00}, Flags {f[5]}/{f[7]}/{f[8]}/{f[9]}/{f[10]}, Verlauf {m.Desc.RampPow:0.#}");
            return m;
        }

        /// <summary>
        /// Illuminated headlight lens of a passenger car. The original materials (FRL_*) are untextured
        /// reflective/glass surfaces without emission parameters; dark as visible glass, lighting provided solely via
        /// “_Emission” (white glow texture * _EmissionTint * _Emission, see TDU2/Car).
        /// </summary>
        public static CarMaterial CreateLamp(Vector4 tint)
        {
            var m = new CarMaterial(MaterialKind.Car) { Name = "TrafficLamp" };
            m.Desc.Color = new Vector4(0.06f, 0.06f, 0.06f, 1f);
            m.Desc.Glossiness = 0.9f;
            m.Desc.Metallic = 0f;
            m.Desc.EmissionTex = null;            // weiß wird beim Zeichnen gesetzt (WhiteTexture)
            m.Desc.EmissionTint = tint;
            m.Desc.Emission = 0f;
            m.Desc.EmissionDiff = 0f;
            m.Desc.UseMask = 0f;
            m.Desc.SelfIllum = 0f;
            return m;
        }
    }

    /// <summary>
    /// Knoten des Fahrzeugmodells (in Unity: GameObject + Transform + MeshRenderer). Lage relativ zum Elternknoten
    /// (Position/Rotation/Skalierung, Spielkoordinaten +X = links), Teile = Submeshes mit Material.
    /// </summary>
    public sealed class CarNode
    {
        public string Name = "";
        public CarNode? Parent;
        public readonly List<CarNode> Children = new List<CarNode>();
        public Vector3 LocalPosition;
        public Quaternion LocalRotation = Quaternion.Identity;
        public Vector3 LocalScale = Vector3.One;
        /// <summary>Matrix aus der Datei, wenn sie sich nicht zerlegen lässt (Scherung); null = aus Position/Rotation/Skalierung.</summary>
        public Matrix4x4? RawLocal;
        public bool Active = true;
        /// <summary>Sichtbarkeit der Teile (Unity: MeshRenderer.enabled).</summary>
        public bool RendererEnabled = true;
        public readonly List<CarPart> Parts = new List<CarPart>();
        public Vector3 MeshMin, MeshMax;
        public bool HasMesh;
        /// <summary>Welt-Matrix (Render-Raum), aktualisiert von <see cref="CarModel.UpdateTransforms"/>.</summary>
        public Matrix4x4 World = Matrix4x4.Identity;

        public Matrix4x4 Local => RawLocal ?? Matrix4x4.CreateScale(LocalScale) * Matrix4x4.CreateFromQuaternion(LocalRotation) * Matrix4x4.CreateTranslation(LocalPosition);

        public void SetParent(CarNode? p)
        {
            Parent?.Children.Remove(this);
            Parent = p;
            p?.Children.Add(this);
        }

        /// <summary>Unity ObjectBuilder.ApplyMatrix: Matrix in Position/Rotation/Skalierung zerlegen.</summary>
        public void ApplyMatrix(Matrix4x4 m)
        {
            // 1:1 wie Unity ObjectBuilder.ApplyMatrix: Rotation = LookRotation(Z-Achse, Y-Achse), Spiegelung (negative Determinante)
            // immer als negative X-Skalierung. (Matrix4x4.Decompose legt die Spiegelung anders ab -> die gespiegelten rechten Räder
            // drehten beim Lenken mit StirrupBaseRot * AngleAxis falsch herum.)
            LocalPosition = m.Translation; RawLocal = null;
            float sx = new Vector3(m.M11, m.M12, m.M13).Length();
            float sy = new Vector3(m.M21, m.M22, m.M23).Length();
            float sz = new Vector3(m.M31, m.M32, m.M33).Length();
            if (sx < 1e-6f || sy < 1e-6f || sz < 1e-6f) { LocalRotation = Quaternion.Identity; LocalScale = Vector3.One; RawLocal = m; return; }
            var fwd = new Vector3(m.M31, m.M32, m.M33) / sz;
            var up = new Vector3(m.M21, m.M22, m.M23) / sy;
            float det = m.GetDeterminant();
            LocalRotation = U.LookRotation(fwd, up);
            LocalScale = new Vector3(det < 0f ? -sx : sx, sy, sz);
            // Scherung (lässt sich so nicht darstellen): Datei-Matrix zum Zeichnen behalten
            var re = Matrix4x4.CreateScale(LocalScale) * Matrix4x4.CreateFromQuaternion(LocalRotation) * Matrix4x4.CreateTranslation(LocalPosition);
            if (!Near(re, m)) RawLocal = m;
        }

        static bool Near(Matrix4x4 a, Matrix4x4 b)
        {
            var d = a - b;
            return System.MathF.Abs(d.M11) + System.MathF.Abs(d.M12) + System.MathF.Abs(d.M13) + System.MathF.Abs(d.M21) + System.MathF.Abs(d.M22) + System.MathF.Abs(d.M23)
                 + System.MathF.Abs(d.M31) + System.MathF.Abs(d.M32) + System.MathF.Abs(d.M33) + System.MathF.Abs(d.M41) + System.MathF.Abs(d.M42) + System.MathF.Abs(d.M43) < 1e-2f;
        }

        public bool ActiveInHierarchy { get { for (var n = this; n != null; n = n.Parent) if (!n.Active) return false; return true; } }

        /// <summary>Alle Knoten des Teilbaums (inkl. dieses).</summary>
        public IEnumerable<CarNode> SelfAndDescendants()
        {
            yield return this;
            foreach (var c in Children) foreach (var d in c.SelfAndDescendants()) yield return d;
        }
    }

    /// <summary>Ein Submesh eines Knotens (Unity: Submesh + sharedMaterials[i]).</summary>
    public sealed class CarPart
    {
        public IGpuMesh Mesh = null!;
        public CarMaterial Material = null!;
        /// <summary>Quelle (CPU) für die UV-Satz-Erkennung der Karosserie.</summary>
        public MeshData? Source;
        public int SubMesh;
    }

    /// <summary>
    /// Creates the visible vehicle from the banks. In der Engine liegt das Modell direkt in Spielkoordinaten (+X = links)
    /// unter dem Knoten <see cref="Visual"/> (in Unity zusätzlich an X gespiegelt).
    /// Nodes (missing names): BODY_HR/MR/LR (LODs), FORK_xx (suspension), STIRRUP_xx (steering), DISC_xx (wheel rotation),
    /// D_RIM_xx / D_TIRE_xx (rim/tire), STEER_HR (steering wheel in the interior), DOOR_L/R, D_SBOX (shadow box) ...
    /// </summary>
    public sealed class CarModel
    {
        public CarNode Visual = new CarNode();
        public readonly Dictionary<string, CarNode> Nodes = new Dictionary<string, CarNode>(StringComparer.OrdinalIgnoreCase);
        public readonly List<IDisposable> Owned = new List<IDisposable>();
        /// <summary>Karosserie-Bounds im Fahrzeugraum (Min/Max).</summary>
        public Vector3 BodyMin, BodyMax;
        public Vector3 BodyCenter => (BodyMin + BodyMax) * 0.5f;
        public Vector3 BodySize => BodyMax - BodyMin;
        /// <summary>
        /// Wirksamer Reifen-Außenradius (m) nach dem Radkasten-Abgleich in <see cref="FitTiresToArches"/> – derselbe Wert
        /// für Grafik und Physik (sonst schwebt der Wagen auf zu großen Physik-Rädern).
        /// </summary>
        public float TireRadiusFront = 0.33f, TireRadiusRear = 0.33f;
        public float TireRadiusFx(bool front) => front ? TireRadiusFront : TireRadiusRear;
        public CarNode? SteeringWheel;
        /// <summary>Light Glasses (LGT_FL/FR/RL/RR/LGT) with a luminous texture (TEXLIGHT) – brightness controlled via “_Emission”.</summary>
        public readonly List<CarMaterial> LightMaterials = new List<CarMaterial>();
        /// <summary>Alle Materialien des Modells (für Aktualisierungen nach Änderungen).</summary>
        public readonly List<CarMaterial> AllMaterials = new List<CarMaterial>();

        /// <summary>Lighting function of a traffic car light panel (derived from the node name FA_xx).</summary>
        public enum LampKind { Head, Tail, Brake, Reverse, Indicator, Beacon }

        /// <summary>Lighting material for a single lamp (traffic cars: separate material instance for each node).</summary>
        public struct TrafficLamp { public CarMaterial Mat; public LampKind Kind; public int Side; }

        /// <summary>
        /// Lights on traffic cars. The traffic models do not have a TEXLIGHT mask, but instead have their own
        /// light quads (FRL_L/FRL_S/FRL_F/FRL_W/FRL_G + headlight) attached to the FA_ nodes for each function.
        /// </summary>
        public readonly List<TrafficLamp> TrafficLamps = new List<TrafficLamp>();

        /// <summary>Set during assembly: Body of a road car (then include FA_-lamp quads).</summary>
        bool _traffic;
        /// <summary>Paint materials on the car body (decals, dirt).</summary>
        public readonly List<CarMaterial> PaintMaterials = new List<CarMaterial>();
        /// <summary>Instruments (HUD* materials) – Lighting via script.</summary>
        public readonly List<CarMaterial> DashMaterials = new List<CarMaterial>();
        public CarNode? CockpitRoot;
        public static float InteriorSelfIllum = 0.04f;
        public static float InteriorOcclusion = 0.4f;
        /// <summary>Ultra-smooth matte interior materials (fabric, leather, plastic—as shown, without ambient reflections).</summary>
        public static float InteriorGloss = 0.1f;

        IRenderer _r = null!;

        static readonly string[] KnownNames =
        {
            "D_SHADOW","D_SBOX","EXHAUST_L","EXHAUST_R","EXHAUST_HL","EXHAUST_HR","BUMP_F_LR","BUMP_R_LR","BUMP_F_MR","BUMP_R_MR","BUMP_F_HR","BUMP_R_HR",
            "STIRRUP_FL","STIRRUP_FR","STIRRUP_RL","STIRRUP_RR","D_REVERSING","D_BREAKING","D_HEADLIGHT","D_WHEEL_FL","D_WHEEL_FR","D_WHEEL_RL","D_WHEEL_RR",
            "D_TIRE_FL","D_TIRE_FR","D_TIRE_RL","D_TIRE_RR","REVERSING","BREAKING","HEADLIGHT","SCENE01","D_INT_HR_L","D_INT_HR_R","DVRSEAT_LR","DVRSEAT_HR",
            "PSGSEAT_LR","PSGSEAT_HR","SEAT_L_HR","SEAT_R_HR","SEAT_L_LR","SEAT_R_LR","HAND_LR_L","HAND_LR_R","HAND_HR_L","HAND_HR_R","FOOT_LR_L","FOOT_LR_R",
            "FOOT_HR_L","FOOT_HR_R","BACK_LR_L","BACK_LR_R","BACK_HR_L","BACK_HR_R","DOOR_L_COL","DOOR_R_COL","FA_B_RL_C","FA_B_RR_C","FA_L_FL_C","FA_L_FR_C",
            "FA_L_FL1_C","FA_L_FR1_C","FA_R_RL_C","FA_R_RR_C","FA_S_RL_C","FA_S_RR_C","FA_S_RL1_C","FA_S_RR1_C","FA_W_FL_C","FA_W_FR_C","FA_W_RL_C","FA_W_RR_C",
            "PSGFOOT_LR_L","PSGFOOT_LR_R","PSGFOOT_HR_L","PSGFOOT_HR_R","STEER_HR","STEER_LR","SEAT_HR_L","SEAT_HR_R","SEAT_LR_L","SEAT_LR_R","HARDTOP_MR","HARDTOP_LR",
            "GLASS_TOP","GLASS_LEFT","GLASS_RIGHT","HANDLE_DOOR_L","HANDLE_DOOR_R","LGT_FL_MR","LGT_FR_MR","LGT_RL_MR","LGT_RR_MR",
            "BIKE_STAND","BRAKE_LEVER",
        };
        static Dictionary<ulong, string>? _names;

        public static string NodeName(ulong key)
        {
            if (_names == null)
            {
                var d = new Dictionary<ulong, string>();
                foreach (var n in KnownNames) d[MaterialDef.KeyFromName(n)] = n;
                _names = d;
            }
            if (_names.TryGetValue(key, out var s)) return s;
            return Bin.KeyToString(key);
        }

        static bool SkipNode(string n, bool hasCockpit)
        {
            string u = n.ToUpperInvariant();
            if (u == "COCK_LR" || u.StartsWith("INT_LR")) return hasCockpit;   // Low-Poly-Cockpit nur ohne Interieur-Bank
            if (u == "BODY_MR" || u == "BODY_LR" || u.EndsWith("_MR") || u.EndsWith("_LR") || u.Contains("_LR_") || u.Contains("_MR_")) return true;
            if (u == "D_SBOX" || u == "D_SHADOW" || u == "D_HEADLIGHT" || u == "D_REVERSING" || u == "D_BREAKING") return true;
            if (u.StartsWith("D_WHEEL")) return true;                            // Hilfs-Zylinder (Radvolumen, verzerrt skaliert)
            if (u == "BIKE_STAND") return true;                                  // Motorrad-Ständer: im Spiel eingeklappt
            if (u == "D_SPLANE") return true;
            return false;
        }

        /// <summary>Traffic cars: WLR_xx is the replacement cylinder for the rotating wheel (DISC_xx is drawn), shadow boxes/dummies removed.</summary>
        static bool SkipTrafficNode(string n)
        {
            string u = n.ToUpperInvariant();
            if (u.StartsWith("WLR_")) return true;
            if (u.Contains("SBOX") || u == "D_SHADOW" || u == "D_HEADLIGHT" || u == "D_REVERSING" || u == "D_BREAKING") return true;
            return false;
        }

        /// <summary>
        /// Wheel placeholder for the body bench (D_TIRE_xx / WHEEL_xx): rough tire geometry, which is replaced in the game by rim/tire/t_&lt;inches&gt;p_oh
        /// Use only as an anchor—otherwise, a second, jagged tire will appear on top of the real one.
        /// </summary>
        static bool WheelPlaceholder(string n)
        {
            string u = n.ToUpperInvariant();
            return u.Contains("TIRE") || u.Contains("WHEEL") || u.Contains("PNEU");
        }

        /// <summary>Light flare points (FA_L = headlight, FA_B = brake light, FA_R = reverse light, FA_S = taillight, FA_W = turn signal): used only as anchors, without a mesh.</summary>
        static bool AnchorOnly(string n) => n.StartsWith("FA_", StringComparison.OrdinalIgnoreCase);

        /// <summary>Auspuff-Effektpunkte (exe 0x73d747: EXH_FX0..3, sonst EXO_FX0..3) – nur Lage, kein Mesh.</summary>
        static bool ExhaustFxNode(string n) => n.StartsWith("EXH_FX", StringComparison.OrdinalIgnoreCase) || n.StartsWith("EXO_FX", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Headlight projection surface (“headlight”, headlightcaustics.2db): a quad approximately 9x11 m in size in front of the car,
        /// which, in the original, projects the beam of light onto the ground. Never rendered.
        /// </summary>
        public static bool IsLightPlane(MaterialDef? def)
        {
            if (def == null || def.Name == null) return false;
            string n = def.Name.ToLowerInvariant();
            return n == "headlight" || n.StartsWith("headlight") || n.Contains("caustics");
        }

        /// <summary>
        /// Light function + side based on the FA_-node names in the traffic models:
        ///   FA_L_* = headlights, FA_B_* = brake lights, FA_R_* = reverse lights, FA_S_* = taillights/brake lights,
        ///   FA_W_* = turn signal, FA_G_* = police rotating beacon; ...HEADLI... = headlights.
        /// Side: L (left) = 1, R (right) = 2, otherwise 0.
        /// </summary>
        public static bool TryTrafficLamp(string name, out LampKind kind, out int side)
        {
            kind = LampKind.Head; side = 0;
            if (string.IsNullOrEmpty(name)) return false;
            string u = name.ToUpperInvariant();
            if (u.StartsWith("FA_") && u.Length >= 4)
            {
                switch (u[3])
                {
                    case 'L': kind = LampKind.Head; break;
                    case 'B': kind = LampKind.Brake; break;
                    case 'R': kind = LampKind.Reverse; break;
                    case 'S': kind = LampKind.Tail; break;
                    case 'W': kind = LampKind.Indicator; break;
                    case 'G': kind = LampKind.Beacon; break;
                    default: return false;
                }
                side = GetSide(u);
                return true;
            }
            if (u.Contains("HEADLI")) { kind = LampKind.Head; return true; }
            return false;
        }

        static int GetSide(string u)
        {
            if (u.Contains("_FL") || u.Contains("_RL") || u.EndsWith("_L") || u.Contains("_L_")) return 1;
            if (u.Contains("_FR") || u.Contains("_RR") || u.EndsWith("_R") || u.Contains("_R_")) return 2;
            return 0;
        }

        /// <summary>Base color of a traffic light (emission color).</summary>
        public static Vector4 LampColor(LampKind kind)
        {
            switch (kind)
            {
                case LampKind.Head: return new Vector4(1f, 0.97f, 0.9f, 1f);
                case LampKind.Tail: return new Vector4(1f, 0.06f, 0.03f, 1f);
                case LampKind.Brake: return new Vector4(1f, 0.04f, 0.02f, 1f);
                case LampKind.Reverse: return new Vector4(1f, 1f, 1f, 1f);
                case LampKind.Indicator: return new Vector4(1f, 0.45f, 0.02f, 1f);
                default: return new Vector4(0.2f, 0.4f, 1f, 1f);
            }
        }

        /// <summary>Modell aufbauen (Main-Thread). <paramref name="parent"/> = Fahrzeugknoten (Unity: Transform des Autos), darf null sein.</summary>
        public static CarModel Build(CarAssets a, IRenderer r, CarNode? parent = null)
        {
            var cm = new CarModel();
            cm._r = r;
            a.Renderer = r;
            cm._traffic = a.Traffic;
            cm.Visual = new CarNode { Name = "Visual (" + a.Car.FileName + ")" };
            cm.Visual.SetParent(parent);

            Func<ulong, TextureWrap, TextureWrap, IGpuTexture?> getTex = a.GetTexture;
            MaterialDef? paintDef = null, paintDef2 = null;
            if (a.ColorLib != null && a.Color != null && !string.IsNullOrEmpty(a.Color.ColorId))
                a.ColorLib.Materials.TryGetValue(MaterialDef.KeyFromName(a.Color.ColorId), out paintDef);
            // zweite Lackfarbe (DBCarsAndColors._color_id_2, z.B. "White on Mark IV Red") -> Material "Paint2"
            if (a.ColorLib != null && a.Color != null && !string.IsNullOrEmpty(a.Color.ColorId2) && a.Color.ColorId2 != "##")
                a.ColorLib.Materials.TryGetValue(MaterialDef.KeyFromName(a.Color.ColorId2), out paintDef2);
            if (paintDef2 == null) paintDef2 = paintDef;

            // Interieurfarben: A_C1*/C1* -> Farbe 1, A_C2*/C2* -> Farbe 2, A_C3*/C3* -> Farbe 3, MAT* -> Materialvariante
            var interiorRepl = new Dictionary<int, MaterialDef>();
            if (a.ColorLib != null && a.Interior != null)
            {
                for (int i = 0; i < 3; i++)
                    if (!string.IsNullOrEmpty(a.Interior.Colors[i]) && a.ColorLib.Materials.TryGetValue(MaterialDef.KeyFromName(a.Interior.Colors[i]), out var d)) interiorRepl[i + 1] = d;
                if (!string.IsNullOrEmpty(a.Interior.Material) && a.ColorLib.Materials.TryGetValue(MaterialDef.KeyFromName(a.Interior.Material), out var md)) interiorRepl[4] = md;
            }
            MaterialDef? Replace(MaterialDef? def, bool interior)
            {
                if (def == null || def.Name == null) return null;
                string n = def.Name.ToUpperInvariant();
                if (n == "PAINT2") return paintDef2;
                if (n == "PAINT_LR" || (n.StartsWith("PAINT_0") && n.Length <= 8)) return paintDef;   // LOD-/Zusatz-Lack wie Paint
                if (!interior && n == "PAINT") return paintDef;
                if (!interior) return null;
                if (n == "PAINT") return paintDef;
                for (int i = 1; i <= 3; i++)
                    if ((n.StartsWith("A_C" + i) || n.StartsWith("C" + i + "_")) && interiorRepl.TryGetValue(i, out var rr)) return rr;
                if (n.StartsWith("MAT") && interiorRepl.TryGetValue(4, out var mr)) return mr;
                return null;
            }
            // Interieurfarbe: Farbmaterial, dessen Grundfarbe auf das Cockpitmaterial getönt wird (Textur bleibt erhalten).
            MaterialDef? InteriorTint(MaterialDef? def)
            {
                if (def == null || def.Name == null) return null;
                string n = def.Name.ToUpperInvariant();
                for (int i = 1; i <= 3; i++)
                    if ((n.StartsWith("A_C" + i) || n.StartsWith("C" + i + "_")) && interiorRepl.TryGetValue(i, out var rr)) return rr;
                if (n.StartsWith("MAT") && interiorRepl.TryGetValue(4, out var mr)) return mr;
                return null;
            }

            var matCache = new Dictionary<(object, ulong), CarMaterial>();
            bool bikeModel = a.Car != null && a.Car.IsBike;
            if (a.Traffic) cm.BuildScene(a.Body, cm.Visual, getTex, matCache, def => null, SkipTrafficNode);
            else cm.BuildScene(a.Body, cm.Visual, getTex, matCache, def => Replace(def, false),
                n => SkipNode(n, a.Cockpit != null));
            if (!a.Traffic) cm.SetupPaint(a, matCache);
            if (a.Cockpit != null)
            {
                var cockRoot = new CarNode { Name = "Cockpit" };
                cockRoot.SetParent(cm.Visual);
                cm.BuildScene(a.Cockpit, cockRoot, getTex, matCache, def => Replace(def, true),
                    n => n.StartsWith("D_", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("D_ND_", StringComparison.OrdinalIgnoreCase),
                    InteriorTint, true);
                cm.CockpitRoot = cockRoot;
                // Interieur liegt im Schatten der Karosserie: Grundhelligkeit wie im Spiel (vorberechnetes Licht in den Texturen)
                var seen = new HashSet<CarMaterial>();
                foreach (var node in cockRoot.SelfAndDescendants())
                    foreach (var part in node.Parts)
                    {
                        var m = part.Material;
                        if (m == null || !seen.Add(m) || !m.IsCarShader) continue;
                        string mn = m.Name.ToUpperInvariant();
                        bool hud = mn.StartsWith("HUD");
                        m.Desc.SelfIllum = hud ? 0.12f : InteriorSelfIllum;
                        m.Desc.Occlusion = InteriorOcclusion;
                        // Original (TD_CarMat_NM / TD_CarVelour / TD_CarPaint_NM): Stoffe, Leder, Kunststoff haben ENVCOEFF = 0 – keine
                        // Umgebungsspiegelung, nur ein schwacher, breiter Glanz (SPECPARM) -> matt; Chrom/Stahl/Spiegel/Glas/Instrumente bleiben.
                        bool shiny = hud || mn.Contains("CHROME") || mn.Contains("STEEL") || mn.Contains("MIRROR") || mn.Contains("GLASS") || mn.Contains("ALU") || mn.Contains("METAL");
                        if (!shiny)
                        {
                            m.Desc.Glossiness = Mathf.Min(m.Desc.Glossiness, InteriorGloss);
                            m.Desc.Metallic = 0f;
                            m.Desc.ClearCoat = 0f;
                            m.Desc.Flake = 0f;
                        }

                        if (mn.Contains("MIRROR"))
                        {
                            //TODO: wir brauchen einen Shader um das ausen Herrunm zu Verdecken
                        }

                        if (hud) cm.DashMaterials.Add(m);
                    }
            }

            // Felgen und Reifen an D_RIM_xx / D_TIRE_xx
            cm.TireRadiusFront = a.Rim != null ? a.Rim.TireRadius(true) : 0.33f;
            cm.TireRadiusRear = a.Rim != null ? a.Rim.TireRadius(false) : 0.33f;
            if (!a.Traffic && a.Rim != null) cm.FitTiresToArches();
            bool isBikeModel = bikeModel;
            if (isBikeModel)
            {
                foreach (var w in new[] { "F", "R" })
                {
                    bool front = w == "F";
                    var rimScene = front ? a.RimFront : (a.RimRear ?? a.RimFront);
                    float rimMeshR = 0f;
                    CarNode? rimNode = cm.Find("D_RIM_" + w);
                    if (rimNode == null) rimNode = cm.Find("D_RIM_" + (front ? "FL" : "RL"));
                    if (rimNode == null) rimNode = cm.Find(front ? "WHR_F" : "WHR_R");
                    if (rimScene != null && rimNode != null)
                    {
                        CarMaterials.WheelPass = true;
                        try { cm.BuildScene(rimScene, rimNode, getTex, matCache, d => null, n => false); } finally { CarMaterials.WheelPass = false; }
                        rimMeshR = RimRadius(rimScene) * Mathf.Abs(rimNode.LocalScale.Y);
                    }
                    var tireScene = front ? a.Tire : (a.TireRear ?? a.Tire);
                    CarNode? tireNode = cm.Find("TIRE_" + w);
                    if (tireNode == null) tireNode = cm.Find("D_TIRE_" + w);
                    if (tireNode == null) tireNode = cm.Find("D_TIRE_" + (front ? "FL" : "RL"));
                    if (tireNode == null) tireNode = cm.Find(front ? "WHR_F" : "WHR_R");
                    if (tireNode == null && rimNode != null) tireNode = rimNode;
                    if (tireScene != null && tireNode != null)
                        cm.BuildTire(a, tireScene, front, tireNode, rimMeshR, getTex, matCache);
                }
            }
            else
            {
                foreach (var w in new[] { "FL", "FR", "RL", "RR" })
                {
                    bool front = w[0] == 'F';
                    var rimScene = front ? a.RimFront : (a.RimRear ?? a.RimFront);
                    float rimMeshR = 0f;
                    if (rimScene != null && cm.Nodes.TryGetValue("D_RIM_" + w, out var rimNode))
                    {
                        CarMaterials.WheelPass = true;
                        try { cm.BuildScene(rimScene, rimNode, getTex, matCache, d => null, n => false); } finally { CarMaterials.WheelPass = false; }
                        rimMeshR = RimRadius(rimScene) * Mathf.Abs(rimNode.LocalScale.Y);
                    }
                    var tireScene = front ? a.Tire : (a.TireRear ?? a.Tire);
                    if (tireScene != null && cm.Nodes.TryGetValue("D_TIRE_" + w, out var tireNode))
                        cm.BuildTire(a, tireScene, front, tireNode, rimMeshR, getTex, matCache);
                }
            }

            cm.SteeringWheel = cm.Find("STEER_HR") ?? cm.Find("STEER_LR");

            // GPU-Materialien erzeugen (in Unity sofort bei new Material)
            foreach (var m in cm.AllMaterials) m.Apply(r);

            // Karosserie-Bounds im Fahrzeugraum (parent)
            cm._fallbackSize = new Vector3(a.Car!.Width, a.Car.Height, a.Car.Length) * 0.001f;
            cm.RecomputeBodyBounds();
            return cm;
        }

        Vector3 _fallbackSize;

        /// <summary>
        /// Karosserie-Bounds im Fahrzeugraum neu berechnen (nur sichtbare Teile). Aus den 8 Ecken der Mesh-Bounds jedes Teils,
        /// unabhängig von der Drehung des Autos in der Welt.
        /// </summary>
        public (Vector3 min, Vector3 max) RecomputeBodyBounds()
        {
            var body = Find("BODY_HR") ?? Find("BODY") ?? Visual;
            bool any = false;
            Vector3 mn = Vector3.Zero, mx = Vector3.Zero;
            foreach (var n in body.SelfAndDescendants())
            {
                if (!n.RendererEnabled || !n.HasMesh || !n.ActiveInHierarchy) continue;
                var m = LocalToVisualParent(n);
                for (int i = 0; i < 8; i++)
                {
                    var c = new Vector3((i & 1) != 0 ? n.MeshMax.X : n.MeshMin.X, (i & 2) != 0 ? n.MeshMax.Y : n.MeshMin.Y, (i & 4) != 0 ? n.MeshMax.Z : n.MeshMin.Z);
                    var p = Vector3.Transform(c, m);
                    if (!any) { mn = mx = p; any = true; } else { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
                }
            }
            if (any) { BodyMin = mn; BodyMax = mx; }
            else { var c0 = new Vector3(0, 0.6f, 0); BodyMin = c0 - _fallbackSize * 0.5f; BodyMax = c0 + _fallbackSize * 0.5f; }
            return (BodyMin, BodyMax);
        }

        /// <summary>Matrix eines Knotens relativ zum Fahrzeug (Elternknoten von <see cref="Visual"/>).</summary>
        public Matrix4x4 LocalToVisualParent(CarNode n)
        {
            var m = Matrix4x4.Identity;
            for (var c = n; c != null && c != Visual.Parent; c = c.Parent) m *= c.Local;
            return m;
        }

        /// <summary>UV-Satz der Stickermap/Schmutzmaske auf der Karosserie: -1 = automatisch, 0 = erster, 1 = zweiter UV-Satz.</summary>
        public static int StickerUvSet = 1;

        /// <summary>
        /// UV-Satz der Karosserie-Abwicklung bestimmen: der Satz, dessen Koordinaten auf den Lackflächen im Bereich 0..1 liegen
        /// und die größte Fläche abdecken (der andere Satz ist gekachelt, z.B. für Flakes).
        /// </summary>
        int DetectBodyUvSet()
        {
            var score = new float[2]; var inRange = new int[2]; int total = 0;
            var mn = new[] { new Vector2(float.MaxValue, float.MaxValue), new Vector2(float.MaxValue, float.MaxValue) };
            var mx = new[] { new Vector2(float.MinValue, float.MinValue), new Vector2(float.MinValue, float.MinValue) };
            bool differ = false;
            foreach (var n in Visual.SelfAndDescendants())
                foreach (var part in n.Parts)
                {
                    var md = part.Source;
                    if (md == null || md.UV0.Count == 0 || !PaintMaterials.Contains(part.Material)) continue;
                    var uv0 = md.UV0; var uv1 = md.UV1;
                    foreach (int vi in md.SubMeshes[part.SubMesh])
                    {
                        total++;
                        for (int set = 0; set < 2; set++)
                        {
                            var list = set == 0 ? uv0 : uv1;
                            if (vi >= list.Count) continue;
                            var uv = list[vi];
                            if (uv.X >= -0.01f && uv.X <= 1.01f && uv.Y >= -0.01f && uv.Y <= 1.01f) inRange[set]++;
                            mn[set] = Vector2.Min(mn[set], uv); mx[set] = Vector2.Max(mx[set], uv);
                        }
                        if (vi < uv1.Count && (uv0[vi] - uv1[vi]).LengthSquared() > 1e-6f) differ = true;
                    }
                }
            if (total == 0 || !differ) return 0;
            for (int set = 0; set < 2; set++)
            {
                var size = mx[set] - mn[set];
                float area = Mathf.Clamp01(size.X) * Mathf.Clamp01(size.Y);
                score[set] = (float)inRange[set] / total * 2f + area;
            }
            int pick = score[1] > score[0] ? 1 : 0;
            Log.Info($"Karosserie-UV: Satz {pick} (UV0 {inRange[0] * 100 / total}% in 0..1, UV1 {inRange[1] * 100 / total}%)");
            return pick;
        }

        /// <summary>
        /// Lack-Materialien sammeln und Werks-Beklebung (Stickermap) sowie Schmutzmaske (dirt_scratch) auflegen.
        /// Schmutzstufe setzt TDU2CarDirt (_DirtLevel 0..1).
        /// </summary>
        void SetupPaint(CarAssets a, Dictionary<(object, ulong), CarMaterial> matCache)
        {
            foreach (var kv in matCache)
            {
                var m = kv.Value;
                if (m != null && m.IsCarShader && IsPaintName(m.Name) && !PaintMaterials.Contains(m))
                    PaintMaterials.Add(m);
            }
            if (PaintMaterials.Count == 0) { if (a.Sticker != null) Log.Warn("Beklebung " + a.StickerName + ": kein Lack-Material (\"Paint\") gefunden"); return; }

            IGpuTexture? sticker = null, dirt = null;
            if (a.Sticker != null)
            {
                sticker = TextureInfo.Create(_r, a.Sticker, TextureWrap.Clamp, TextureWrap.Clamp);
                if (sticker == null) Log.Warn("Beklebung " + a.StickerName + ": Texturformat nicht unterstützt");
                else Owned.Add(sticker);
            }
            if (a.DirtMap != null)
            {
                dirt = TextureInfo.Create(_r, a.DirtMap, TextureWrap.Clamp, TextureWrap.Clamp, linear: true);   // Unity: CreateTexture(..., linear true)
                if (dirt != null) Owned.Add(dirt);
            }
            int uvSet = StickerUvSet >= 0 ? StickerUvSet : DetectBodyUvSet();
            foreach (var m in PaintMaterials)
            {
                m.Desc.StickerUV = uvSet;
                if (sticker != null) { m.Desc.StickerTex = sticker; m.Desc.UseSticker = 1f; }
                if (dirt != null) { m.Desc.DirtTex = dirt; m.Desc.DirtLevel = 0f; }
            }
            if (sticker != null) Log.Info("Beklebung " + a.StickerName + " auf " + PaintMaterials.Count + " Lack-Material(ien)");
        }

        /// <summary>Lack-Materialien der Karosserie: Paint, Paint2 (zweite Farbe), Paint_LR (LOD), Paint_01 …</summary>
        public static bool IsPaintName(string? n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            n = n.ToLowerInvariant();
            return n == "paint" || n == "paint2" || n == "paint_lr" || (n.StartsWith("paint_0") && n.Length <= 8);
        }

        /// <summary>Lackfarbe 2 (Material Paint2)?</summary>
        public static bool IsPaint2(CarMaterial? m) => m != null && string.Equals(m.Name, "paint2", StringComparison.OrdinalIgnoreCase);

        public CarNode? Find(string name) => Nodes.TryGetValue(name, out var t) ? t : null;

        void BuildScene(CarScene? cs, CarNode root, Func<ulong, TextureWrap, TextureWrap, IGpuTexture?> getTex,
            Dictionary<(object, ulong), CarMaterial> matCache, Func<MaterialDef?, MaterialDef?> replace, Func<string, bool> skip,
            Func<MaterialDef?, MaterialDef?>? tint = null, bool interior = false)
        {
            if (cs?.Scene == null) return;
            var scene = cs.Scene;
            var goByHie = new Dictionary<int, CarNode>();
            var skipped = new HashSet<int>();
            var hier = scene.Hierarchy;
            if (hier.Count == 0)
            {
                foreach (var off in scene.InsOffsets) AddNode(cs, scene.GetIns(off), root, getTex, matCache, replace, tint, true, interior);
                return;
            }
            // Traffic cars: Build only the “D_CAR” car tree. At the scene level (“SCENE01”), there are also
            // the headlight projection surface (“headlight”), shadow boxes, and open doors/attachments.
            int carRoot = 0;
            if (_traffic)
            {
                foreach (var h in hier)
                    if (NodeName(scene.GetIns(h.InsOffset).Key).Equals("D_CAR", StringComparison.OrdinalIgnoreCase)) { carRoot = h.Offset; break; }
            }
            if (carRoot != 0)
            {
                var byOff = new Dictionary<int, HieRecord>();
                foreach (var h in hier) byOff[h.Offset] = h;
                // Ancestors of D_CAR (e.g., SCENE01) are only containers and must not be skipped.
                var keep = new HashSet<int> { carRoot };
                for (var a = byOff[carRoot]; a.ParentOffset != 0 && byOff.TryGetValue(a.ParentOffset, out var pa); a = pa)
                { if (!keep.Add(a.ParentOffset)) break; }
                foreach (var h in hier)
                {
                    if (keep.Contains(h.Offset)) continue;
                    bool under = false; var cur = h; int guard = 0;
                    while (cur.ParentOffset != 0 && guard++ < 64)
                    {
                        if (cur.ParentOffset == carRoot) { under = true; break; }
                        if (!byOff.TryGetValue(cur.ParentOffset, out var p)) break;
                        cur = p;
                    }
                    if (!under) skipped.Add(h.Offset);
                }
            }
            foreach (var h in hier)
            {
                if (h.ParentOffset != 0 && skipped.Contains(h.ParentOffset)) { skipped.Add(h.Offset); continue; }
                if (skipped.Contains(h.Offset)) continue;
                var ins = scene.GetIns(h.InsOffset);
                string name = NodeName(ins.Key);
                if (skip(name)) { skipped.Add(h.Offset); continue; }
                var parent = h.ParentOffset != 0 && goByHie.TryGetValue(h.ParentOffset, out var pt) ? pt : root;
                bool lampMesh = _traffic && AnchorOnly(name);
                goByHie[h.Offset] = AddNode(cs, ins, parent, getTex, matCache, replace, tint, lampMesh || (!AnchorOnly(name) && !WheelPlaceholder(name) && !ExhaustFxNode(name)), interior);
            }
        }

        CarNode AddNode(CarScene cs, InsRecord ins, CarNode parent, Func<ulong, TextureWrap, TextureWrap, IGpuTexture?> getTex,
            Dictionary<(object, ulong), CarMaterial> matCache, Func<MaterialDef?, MaterialDef?> replace, Func<MaterialDef?, MaterialDef?>? tint = null, bool withMesh = true, bool interior = false)
        {
            string name = NodeName(ins.Key);
            var go = new CarNode { Name = name };
            go.SetParent(parent);
            go.ApplyMatrix(ins.Matrix);
            if (!Nodes.ContainsKey(name)) Nodes[name] = go;
            if (withMesh && ins.Lods.Length > 0 && cs.Meshes.TryGetValue(ins.Lods[0].ObjKey, out var md) && md != null)
            {
                // Headlight projection surface (“headlight”): Do not render (otherwise it will hang under the car).
                if (md.SubMeshMaterialIndex.Count > 0 && cs.Materials != null)
                {
                    var bankKeys0 = cs.Scene.ReadMaterialBank(ins.MaterialBankOffset);
                    int mi0 = md.SubMeshMaterialIndex[0];
                    if (mi0 >= 0 && mi0 < bankKeys0.Length && cs.Materials.Materials.TryGetValue(bankKeys0[mi0], out var def0) && IsLightPlane(def0))
                        return go;
                }
                bool isLampNode = _traffic && AnchorOnly(name) && TryTrafficLamp(name, out _, out _);
                int lampSide = 0; LampKind lampKind = LampKind.Head;
                if (isLampNode) TryTrafficLamp(name, out lampKind, out lampSide);
                var bankKeys = cs.Scene.ReadMaterialBank(ins.MaterialBankOffset);
                var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
                for (int i = 0; i < md.SubMeshes.Count; i++)
                {
                    int mi = md.SubMeshMaterialIndex[i];
                    ulong mk = mi >= 0 && mi < bankKeys.Length ? bankKeys[mi] : 0;
                    MaterialDef? def = null;
                    if (mk != 0 && cs.Materials != null) cs.Materials.Materials.TryGetValue(mk, out def);
                    if (def == null && mk != 0) def = CommonCarAssets.Material(mk);   // z.B. PLATE_LR / SPRG_xx aus common_mesh.2dm
                    var key = ((object)cs, mk);
                    CarMaterial? mat;
                    if (isLampNode)
                    {
                        // Separate material instance for each lamp so that the left and right turn signals light up independently.
                        mat = CarMaterials.CreateLamp(LampColor(lampKind));
                        mat.Desc.EmissionTex = CarMaterials.WhiteTexture(_r);   // Texture2D.whiteTexture
                        Owned.Add(mat);
                        AllMaterials.Add(mat);
                        TrafficLamps.Add(new TrafficLamp { Mat = mat, Kind = lampKind, Side = lampSide });
                    }
                    else if (!matCache.TryGetValue(key, out mat))
                    {
                        mat = CarMaterials.Create(def, getTex, replace(def), tint != null ? tint(def) : null, interior);
                        matCache[key] = mat;
                        Owned.Add(mat);
                        AllMaterials.Add(mat);
                        if (mat.IsCarShader && mat.Desc.EmissionTex != null) LightMaterials.Add(mat);
                    }
                    var (v, idx) = ObjectBuilder.ToObjectVertices(md, i, ref min, ref max);
                    if (idx.Length == 0) continue;
                    var mesh = _r.CreateMesh(v, idx);
                    Owned.Add(mesh);
                    go.Parts.Add(new CarPart { Mesh = mesh, Material = mat, Source = md, SubMesh = i });
                }
                if (go.Parts.Count > 0) { go.HasMesh = true; go.MeshMin = min; go.MeshMax = max; }
            }
            return go;
        }

        /// <summary>Outer radius (around the X-axis) of the rim meshes in model units.</summary>
        static float RimRadius(CarScene cs)
        {
            float r = 0f;
            foreach (var md in cs.Meshes.Values)
                if (md != null) foreach (var p in md.Positions) r = Mathf.Max(r, Mathf.Sqrt(p.Y * p.Y + p.Z * p.Z));
            return r;
        }

        /// <summary>
        /// Reifen an den Radkasten des Modells anpassen. Die Fahrzeugdatenbank beschreibt Reifen, die nicht immer zum
        /// Karosseriemodell passen: Beim Bugatti Veyron steht hinten 365/40 auf 22 Zoll (Radius 0,425 m), der Radkasten des
        /// Modells ist aber nur rund 0,40 m groß – der Reifen schneidet in die Karosserie und füllt den Radkasten komplett
        /// aus. Gemessen wird der kleinste Abstand Radmitte -> Außenhaut (nur die äußere Haut, 25. Perzentil über 24
        /// Winkelsektoren, damit einzelne Streu-Vertices nicht zählen). Angepasst wird nur, wenn eine Achse sauber passt und
        /// die andere nicht – das ist ein Widerspruch zwischen Datenbank und Modell (kein Modellierfehler, deshalb bleiben
        /// Autos mit engen Kotflügeln wie 2CV/E-Type unverändert).
        /// </summary>
        void FitTiresToArches()
        {
            float archF = ArchRadius("D_TIRE_FL", TireRadiusFront);
            float archR = ArchRadius("D_TIRE_RL", TireRadiusRear);
            if (archF <= 0f || archR <= 0f) return;
            float relF = archF / TireRadiusFront, relR = archR / TireRadiusRear;
            const float TooBig = 0.97f, Fine = 1.05f, Margin = 1.02f, MinScale = 0.88f;
            if (relR < TooBig && relF > Fine)
                TireRadiusRear *= Mathf.Clamp(relR / Margin, MinScale, 1f);
            else if (relF < TooBig && relR > Fine)
                TireRadiusFront *= Mathf.Clamp(relF / Margin, MinScale, 1f);
        }

        /// <summary>Kleinster Abstand Radmitte -> Außenhaut der Karosserie (0 = nicht messbar).</summary>
        float ArchRadius(string tireNodeName, float tireR)
        {
            var body = Find("BODY_HR") ?? Find("BODY");
            var tn = Find(tireNodeName);
            if (body == null || tn == null || tireR <= 0.01f) return 0f;
            var c = LocalToVisualParent(tn).Translation;
            float sx = Mathf.Sign(c.X), cxAbs = Mathf.Abs(c.X);
            float ringIn = 0.5f * tireR, ringOut = 1.6f * tireR;
            var mins = new float[24];
            for (int i = 0; i < mins.Length; i++) mins[i] = float.MaxValue;
            foreach (var n in body.SelfAndDescendants())
            {
                if (!n.HasMesh || !n.RendererEnabled || !n.ActiveInHierarchy) continue;
                var wm = LocalToVisualParent(n);
                // Grobe Vorauswahl über die Modell-Bounds des Teils (Karosserie hat >100k Vertices)
                var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3((i & 1) != 0 ? n.MeshMax.X : n.MeshMin.X, (i & 2) != 0 ? n.MeshMax.Y : n.MeshMin.Y, (i & 4) != 0 ? n.MeshMax.Z : n.MeshMin.Z);
                    var wc = Vector3.Transform(corner, wm);
                    mn = Vector3.Min(mn, wc); mx = Vector3.Max(mx, wc);
                }
                if (mx.Z < c.Z - ringOut || mn.Z > c.Z + ringOut || mx.Y < c.Y - ringOut || mn.Y > c.Y + ringOut) continue;
                if (Mathf.Sign(mn.X) == Mathf.Sign(mx.X) && Mathf.Sign(mn.X) != sx) continue;
                foreach (var part in n.Parts)
                {
                    var md = part.Source;
                    if (md == null) continue;
                    foreach (var p in md.Positions)
                    {
                        var w = Vector3.Transform(p, wm);
                        if (Mathf.Sign(w.X) != sx) continue;
                        if (Mathf.Abs(w.X) < cxAbs + 0.02f) continue;   // nur Außenhaut, nicht die Radhausschale
                        float dy = w.Y - c.Y, dz = w.Z - c.Z;
                        float r = MathF.Sqrt(dy * dy + dz * dz);
                        if (r < ringIn || r > ringOut) continue;
                        int b = (int)((MathF.Atan2(dy, dz) + MathF.PI) * (24f / (2f * MathF.PI))) % 24;
                        if (r < mins[b]) mins[b] = r;
                    }
                }
            }
            var bins = new List<float>();
            foreach (var v in mins) if (v < float.MaxValue) bins.Add(v);
            if (bins.Count == 0) return 0f;
            bins.Sort();
            return bins[bins.Count / 4];
        }

        /// <summary>
        /// Tire: The model from rim/tire/t_&lt;inches&gt;p_oh.bnk is normalized (radius 0.48 m). Radially, it is mapped to
        /// [rim radius, tire radius], and axially to the tire width (DBRims: diameter, width, sidewall height %).
        /// </summary>
        void BuildTire(CarAssets a, CarScene cs, bool front, CarNode node, float rimMeshR, Func<ulong, TextureWrap, TextureWrap, IGpuTexture?> getTex,
            Dictionary<(object, ulong), CarMaterial> matCache)
        {
            var rim = a.Rim;
            float rimR = rim != null ? (front ? rim.DiameterFront : rim.DiameterRear) * 0.0254f * 0.5f : 0.23f;
            float tireR = TireRadiusFx(front);
            float targetWidth = 0f;
            if (a.Car != null && a.Car.IsBike && rim != null)
                targetWidth = Mathf.Max(0.06f, (front ? rim.WidthFront : rim.WidthRear) * 0.001f);
            if (rimMeshR > rimR * 0.8f && rimMeshR < rimR * 1.25f) rimR = rimMeshR;
            foreach (var off in cs.Scene.InsOffsets)
            {
                var ins = cs.Scene.GetIns(off);
                if (ins.Lods.Length == 0 || !cs.Meshes.TryGetValue(ins.Lods[0].ObjKey, out var md) || md == null) continue;
                var copy = RevolveTire(md, rimR, tireR, targetWidth);
                if (copy == null) continue;
                // Der Wurzelknoten der Reifenbank skaliert das Mesh auf die Zollgröße (20" = 1,0, 22" = 1,123, 24" = 1,238).
                // RevolveTire baut aber schon in absoluten Metern (Radius/Breite aus der Fahrzeugdatenbank) – ohne diese
                // Korrektur erben die Reifen die Skalierung ein zweites Mal und werden zu groß (Bugatti Veyron hinten 1,123x,
                // Spyker D8 vorn 1,238x). Die Skalierung der Karosserieknoten (z. B. X = 1,2 für die Felgenbreite) bleibt erhalten.
                ScaleMesh(copy, ins.Matrix);
                var tmp = new CarScene { Scene = cs.Scene, Geo = cs.Geo, Materials = cs.Materials };
                tmp.Meshes[ins.Lods[0].ObjKey] = copy;
                var go = AddNode(tmp, ins, node, getTex, matCache, d => null);
                go.Name = "Tire";
            }
        }

        /// <summary>Skalierung eines Knotens aus der Dateimatrix (wie <see cref="CarNode.ApplyMatrix"/>: Spaltenlängen).</summary>
        static Vector3 MatrixScale(Matrix4x4 m) => new Vector3(
            new Vector3(m.M11, m.M12, m.M13).Length(),
            new Vector3(m.M21, m.M22, m.M23).Length(),
            new Vector3(m.M31, m.M32, m.M33).Length());

        /// <summary>Rechnet die Skalierung eines Knotens aus einem Mesh heraus (Positionen geteilt, Normalen renormalisiert).</summary>
        static void ScaleMesh(MeshData md, Matrix4x4 node)
        {
            var s = MatrixScale(node);
            if (s.X < 1e-4f || s.Y < 1e-4f || s.Z < 1e-4f) return;
            if (Mathf.Abs(s.X - 1f) < 1e-5f && Mathf.Abs(s.Y - 1f) < 1e-5f && Mathf.Abs(s.Z - 1f) < 1e-5f) return;
            float ix = 1f / s.X, iy = 1f / s.Y, iz = 1f / s.Z;
            for (int i = 0; i < md.Positions.Count; i++)
            {
                var p = md.Positions[i];
                md.Positions[i] = new Vector3(p.X * ix, p.Y * iy, p.Z * iz);
            }
            for (int i = 0; i < md.Normals.Count; i++)
            {
                var n = md.Normals[i];
                n = new Vector3(n.X * s.X, n.Y * s.Y, n.Z * s.Z);   // Inverse Transponierte
                md.Normals[i] = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
            }
        }

        /// <summary>
        /// Tire as a smooth solid of revolution: The original (t_&lt;inches&gt;p_oh) has only 20 segments with modeled
        /// shoulder blocks and is not to scale (outer radius 0.43–0.51). The envelope curve (block top edges) is derived from its profile stations (UV0.x =
        /// arc length bead→tread→bead), mapped radially to [rim radius, tire radius], and rotated with 72 segments; V = angle/18°, as in the original,
        /// so that the tread and sidewall textures fit unchanged.
        /// </summary>
        static MeshData? RevolveTire(MeshData md, float rimR, float tireR, float targetWidth = 0f)
        {
            if (md.Positions.Count == 0 || md.UV0.Count != md.Positions.Count || md.SubMeshes.Count == 0) return null;
            // Hauptsubmesh = das mit den meisten Indizes (das zweite ist die flache Abdeckscheibe)
            int main = 0;
            for (int i = 1; i < md.SubMeshes.Count; i++) if (md.SubMeshes[i].Count > md.SubMeshes[main].Count) main = i;
            var used = new HashSet<int>(md.SubMeshes[main]);
            float rMin = float.MaxValue, rMax = 0f, xMin = float.MaxValue, xMax = float.MinValue;
            foreach (int vi in used)
            {
                if (vi < 0 || vi >= md.Positions.Count) continue;
                var p = md.Positions[vi];
                float r = Mathf.Sqrt(p.Y * p.Y + p.Z * p.Z);
                rMin = Mathf.Min(rMin, r); rMax = Mathf.Max(rMax, r);
                xMin = Mathf.Min(xMin, p.X); xMax = Mathf.Max(xMax, p.X);
            }
            if (rMax - rMin < 1e-3f) return null;
            float xc = (xMin + xMax) * 0.5f;
            float xScale = targetWidth > 0.01f && (xMax - xMin) > 0.01f ? targetWidth / (xMax - xMin) : 1f;
            var st = new SortedDictionary<int, Vector2>();   // key = round(u*1000) -> (x, r)
            foreach (int vi in used)
            {
                if (vi < 0 || vi >= md.Positions.Count) continue;
                var p = md.Positions[vi];
                float r = Mathf.Sqrt(p.Y * p.Y + p.Z * p.Z), x = (p.X - xc) * xScale;
                int k = Mathf.RoundToInt(md.UV0[vi].X * 1000f);
                if (!st.TryGetValue(k, out var cur) || r > cur.Y + 1e-4f || (Mathf.Abs(r - cur.Y) <= 1e-4f && Mathf.Abs(x) > Mathf.Abs(cur.X)))
                    st[k] = new Vector2(x, r);
            }
            var keys = new List<int>(st.Keys);
            var prof = new List<Vector3>();   // (u, x, r)
            for (int i = 0; i < keys.Count; i++)
            {
                var v = st[keys[i]];
                if (i > 0 && i < keys.Count - 1 && v.Y < Mathf.Min(st[keys[i - 1]].Y, st[keys[i + 1]].Y) - 0.005f * rMax) continue;
                prof.Add(new Vector3(keys[i] / 1000f, v.X, v.Y));
            }
            if (prof.Count < 3) return null;
            const float treadOut = 0.03f;
            float rS = rMin + 0.7f * (rMax - rMin);
            float kT = treadOut / Mathf.Max(1e-4f, rMax - rS);
            float rSo = tireR - treadOut, rB = Mathf.Min(rimR * 0.97f, rSo - 0.01f);
            for (int i = 0; i < prof.Count; i++)
            {
                var q = prof[i];
                float nr = q.Z >= rS ? tireR - (rMax - q.Z) * kT : Mathf.Lerp(rB, rSo, (q.Z - rMin) / Mathf.Max(1e-4f, rS - rMin));
                prof[i] = new Vector3(q.X, q.Y, nr);
            }
            // Profilnormalen (x, r) aus den Nachbarn
            int M = prof.Count;
            var pn = new Vector2[M];
            for (int i = 0; i < M; i++)
            {
                var a = prof[Mathf.Max(0, i - 1)]; var b = prof[Mathf.Min(M - 1, i + 1)];
                var t = new Vector2(b.Y - a.Y, b.Z - a.Z);
                var n = new Vector2(t.Y, -t.X);
                pn[i] = n.LengthSquared() > 1e-12f ? Vector2.Normalize(n) : Vector2.UnitY;
            }
            // Wicklung des Originals bestimmen (Kreuzprodukt vs. gespeicherte Normale)
            float sign = 0f;
            if (md.Normals.Count == md.Positions.Count)
            {
                var tl = md.SubMeshes[main];
                for (int i = 0; i + 2 < tl.Count && i < 3000; i += 3)
                {
                    var A = md.Positions[tl[i]]; var B = md.Positions[tl[i + 1]]; var C = md.Positions[tl[i + 2]];
                    sign += Vector3.Dot(Vector3.Cross(B - A, C - A), md.Normals[tl[i]] + md.Normals[tl[i + 1]] + md.Normals[tl[i + 2]]) > 0f ? 1f : -1f;
                }
            }
            bool wantPositive = sign >= 0f;

            const int N = 72;   // 4 Segmente je Texturwiederholung (180°)
            var o = new MeshData { Name = md.Name + "_rev", HasColors = md.HasColors, HasUV1 = md.HasUV1, HasUV2 = md.HasUV2 };
            for (int j = 0; j <= N; j++)
            {
                float th = 2f * Mathf.PI * j / N, c = Mathf.Cos(th), s = Mathf.Sin(th);
                float vv = j * (20f / N);   // V = Winkel / 180°
                for (int i = 0; i < M; i++)
                {
                    var q = prof[i];
                    o.Positions.Add(new Vector3(q.Y, q.Z * c, q.Z * s));
                    o.Normals.Add(new Vector3(pn[i].X, pn[i].Y * c, pn[i].Y * s));
                    var uv = new Vector2(q.X, vv);
                    o.UV0.Add(uv);
                    if (md.UV1.Count == md.Positions.Count) o.UV1.Add(uv);
                    if (md.UV2.Count == md.Positions.Count) o.UV2.Add(uv);
                    if (md.Colors.Count == md.Positions.Count) o.Colors.Add(new Rgba32(255, 255, 255, 255));
                }
            }
            var tris = new List<int>(N * (M - 1) * 6);
            bool flip = false;
            {
                var A = o.Positions[0]; var B = o.Positions[M]; var C = o.Positions[1];
                var nn = Vector3.Cross(B - A, C - A);
                bool pos = Vector3.Dot(nn, o.Normals[0] + o.Normals[1]) > 0f;
                flip = pos != wantPositive;
            }
            for (int j = 0; j < N; j++)
                for (int i = 0; i < M - 1; i++)
                {
                    int a = j * M + i, b = a + 1, c = a + M, d = c + 1;
                    if (!flip) { tris.Add(a); tris.Add(c); tris.Add(b); tris.Add(b); tris.Add(c); tris.Add(d); }
                    else { tris.Add(a); tris.Add(b); tris.Add(c); tris.Add(b); tris.Add(d); tris.Add(c); }
                }
            o.SubMeshes.Add(tris);
            o.SubMeshMaterialIndex.Add(main < md.SubMeshMaterialIndex.Count ? md.SubMeshMaterialIndex[main] : 0);
            if (main < md.SubMeshIsSprite.Count) o.SubMeshIsSprite.Add(false);
            if (main < md.SubMeshUvSets.Count) o.SubMeshUvSets.Add(md.SubMeshUvSets[main]);
            return o;
        }

        // ------------------------------------------------------------------ Engine (ersetzt Unity-Transform/MeshRenderer)

        /// <summary>Welt-Matrizen aller Knoten berechnen. <paramref name="root"/> = Lage des Elternknotens von <see cref="Visual"/> (Render-Raum).</summary>
        public void UpdateTransforms(Matrix4x4 root) => UpdateNode(Visual, root);

        static void UpdateNode(CarNode n, Matrix4x4 parentWorld)
        {
            n.World = n.Local * parentWorld;
            foreach (var c in n.Children) UpdateNode(c, n.World);
        }

        /// <summary>Alle sichtbaren Teile zeichnen (nach <see cref="UpdateTransforms"/>).</summary>
        public void Draw(IRenderer r) => DrawNode(r, Visual);

        static void DrawNode(IRenderer r, CarNode n)
        {
            if (!n.Active) return;
            if (n.RendererEnabled)
                foreach (var p in n.Parts)
                    if (p.Material.Gpu != null) r.DrawMesh(p.Mesh, p.Material.Gpu, n.World);
            foreach (var c in n.Children) DrawNode(r, c);
        }

        /// <summary>Geänderte Materialien (Lichter, Schmutz …) neu hochladen.</summary>
        public void ApplyMaterial(CarMaterial m) => m.Apply(_r);

        public void Destroy()
        {
            foreach (var o in Owned) o?.Dispose();
            Owned.Clear();
            Visual.SetParent(null);
            Nodes.Clear();
        }
    }
}
