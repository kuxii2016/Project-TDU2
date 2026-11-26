using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;

namespace Paradise.Core.Render.Original;

/// <summary>
/// Ein Original-Shader-Satz (.sob, XMBF „ShaderSet“): Profile (Vertex/Pixel) mit Bytecode-Einträgen („attribs“) und
/// Permutationen („shaders“: id = Hash der 48 Schalter, attrib = Index, spec = Schalter).
/// </summary>
public sealed class ShaderLibrary
{
    public string Name = "";
    public ulong Guid;
    public readonly List<Profile> Profiles = new();

    public sealed class Profile
    {
        public int Id, MaxLod;
        public readonly List<Attrib> Attribs = new();
        public readonly List<Item> Items = new();
        public readonly Dictionary<ulong, Item> ById = new();
        /// <summary>Vertex- oder Pixel-Shader (aus dem Versions-Token des ersten Eintrags).</summary>
        public bool IsPixel;
    }

    public sealed class Attrib
    {
        public byte[] Bytecode = Array.Empty<byte>();
        public byte[] MatIds = Array.Empty<byte>(), DynIds = Array.Empty<byte>(), SymIds = Array.Empty<byte>();
        public bool Frame, Scene, Skin;
        DxsoShader? _parsed;
        public DxsoShader? Parsed { get { if (_parsed == null && DxsoShader.TryParse(Bytecode, out var s, out _)) _parsed = s; return _parsed; } }
    }

    public sealed class Item
    {
        public ulong Id;
        public int Attrib;
        public byte[] Spec = Array.Empty<byte>();
    }

    public Profile? Vs => Profiles.Find(p => !p.IsPixel && p.Attribs.Count > 0);
    public Profile? Ps => Profiles.Find(p => p.IsPixel && p.Attribs.Count > 0);

    public static ShaderLibrary? Load(string path)
    {
        var root = XmbFile.Parse(File.ReadAllBytes(path), rawBytes: true);
        if (root == null) return null;
        var lib = new ShaderLibrary { Name = root.S("name") ?? Path.GetFileNameWithoutExtension(path), Guid = root.U("guid") };
        var profArr = root["profArr"];
        if (profArr == null) return null;
        foreach (var pv in profArr.Items)
        {
            var p = new Profile { Id = pv.I("id"), MaxLod = pv.I("maxlod") };
            var attribs = pv["attribs"];
            if (attribs != null)
                foreach (var av in attribs.Items)
                    p.Attribs.Add(new Attrib
                    {
                        Bytecode = av["bin"]?.Value as byte[] ?? Array.Empty<byte>(),
                        MatIds = av["matID"]?.Value as byte[] ?? Array.Empty<byte>(),
                        DynIds = av["dynID"]?.Value as byte[] ?? Array.Empty<byte>(),
                        SymIds = av["symID"]?.Value as byte[] ?? Array.Empty<byte>(),
                        Frame = av.I("frame") != 0, Scene = av.I("scene") != 0, Skin = av.I("skin") != 0,
                    });
            var shaders = pv["shaders"];
            if (shaders != null)
                foreach (var iv in shaders.Items)
                {
                    var it = new Item { Id = iv.U("id"), Attrib = iv.I("attrib"), Spec = iv["spec"]?.Value as byte[] ?? Array.Empty<byte>() };
                    p.Items.Add(it);
                    p.ById.TryAdd(it.Id, it);
                }
            if (p.Attribs.Count > 0 && p.Attribs[0].Bytecode.Length >= 4)
                p.IsPixel = BitConverter.ToUInt32(p.Attribs[0].Bytecode, 0) >> 16 == 0xFFFF;
            lib.Profiles.Add(p);
        }
        return lib;
    }
}

/// <summary>
/// Original-Shader eines Materials (Satz + Vertex-/Pixel-Eintrag). <see cref="Differences"/> = abweichende Schalter (0 = exakt).
/// </summary>
public sealed class OriginalProgram
{
    public ShaderLibrary Library = null!;
    public ShaderLibrary.Attrib Vs = null!, Ps = null!;
    public int VsIndex, PsIndex;
    public ulong ItemId;
    public int Differences;
    /// <summary>Schalter der gewählten Permutation (für Vergleich/Protokoll).</summary>
    public byte[]? Spec;
    /// <summary>Klassifizierte Konstanten einmal je Shader, nicht je Draw aus dem ConditionalWeakTable holen.</summary>
    public OriginalConstants.Entry[]? VsEntries, PsEntries;
    /// <summary>Bitmaske gelesener Konstanten – einmal je Programm statt je Draw neu aus beiden Entry-Arrays zusammensetzen.</summary>
    public ulong ConstantMask;
    public bool MaskReady;
    public string Name => Library.Name + " vs" + VsIndex + "/ps" + PsIndex;
}

/// <summary>
/// Zuordnung Material -> Original-Shader wie das Original (edRenderShaderSet, GSRender_ShaderMng): Hash der 48 Schalter
/// (<see cref="HashSpec"/>) im Satz der Material-GUID; sonst die Varianten, die der Szenen-Handler zur Laufzeit erzeugt
/// (Texturanzahl ±10, bis zu zwei optionale Schalter aus), zuletzt die nächstgelegene gespeicherte Permutation.
/// </summary>
public static class OriginalShaderSystem
{
    public const int NumSpecs = 48;
    const int S_NBTEXTURES = 0, S_USESKINNING = 9, S_FOG = 11, S_PASS_Z = 16, S_PASS_SHADOWCAST = 17, S_PASS_SHADOWRECEIVE = 18, S_MODE = 19, S_LOD = 20, S_USEALPHATEST = 21;

    static readonly object s_lock = new();
    static List<ShaderLibrary>? s_libs;
    static readonly Dictionary<ulong, ShaderLibrary> s_byGuid = new();
    static readonly Dictionary<string, OriginalProgram?> s_programs = new();

    // Statistik (DevMenu)
    public static int Resolved, Exact, Approx, Missing, NoSpec;
    public static volatile string ScanStatus = "";
    // DevMenu-Schrift hat keine Umlaute -> ASCII
    public static string Stats => (s_libs == null ? "Saetze nicht geladen" : s_libs.Count + " Saetze") +
        $" - Materialien {Resolved}: exakt {Exact}, angenaehert {Approx}, ohne {Missing}, ohne Schalter {NoSpec}" + (ScanStatus.Length > 0 ? " - " + ScanStatus : "") +
        $" - Renderer: {OriginalMaterial.Created} Mat., {OriginalMaterial.DrawnLastFrame} Draws/Bild, {OriginalMaterial.Pipelines} Pipelines" +
        (OriginalMaterial.LastError.Length > 0 ? " - Fehler: " + OriginalMaterial.LastError : "") +
        (OriginalConstants.Missing.Length > 0 ? " - ohne Wert: " + OriginalConstants.Missing : "");
    static readonly Dictionary<string, int[]> s_perSet = new();   // Satz -> {exakt, angenähert}
    static readonly System.Text.StringBuilder s_examples = new();
    static readonly Dictionary<string, int> s_exampleCount = new();
    static readonly string[] SpecNames =
    {
        "S_NBTEXTURES", "S_COORDTEXTURE", "S_USEGLOSSMAP", "S_USEREFLECTION", "S_USEAMBIENT", "S_USEDIFFUSE", "S_USESPECULAR", "S_USEEMISSIVE",
        "S_UVMATRIXMASK", "S_USESKINNING", "S_NBPRELIGHT", "S_FOG", "S_NORMAL", "S_TANGENT", "S_BINORMAL", "S_TANGENTSPACE", "S_PASS_Z",
        "S_PASS_SHADOWCAST", "S_PASS_SHADOWRECEIVE", "S_MODE", "S_LOD", "S_USEALPHATEST", "PAD22", "PAD23", "PAD24", "PAD25", "PAD26", "PAD27",
        "PAD28", "PAD29", "PAD30", "PAD31", "S_USERTYPE1", "S_USERTYPE2", "S_USERTYPE3", "S_USERTYPE4", "S_USERTYPE5", "S_USERTYPE6",
        "S_USERTYPE7", "S_USERTYPE8", "S_USERTYPE9", "S_USERTYPE10", "S_USERTYPE11", "S_USERTYPE12", "S_USERTYPE13", "S_USERTYPE14",
        "S_USERTYPE15", "S_USERTYPE16",
    };

    /// <summary>
    /// Test ohne Spielen: alle Materialbibliotheken (.2dm) in allen Bänken unter Bnk/islands und Bnk/vehicles… durchgehen und jedes
    /// Material einmal zuordnen (Hintergrund, Fortschritt in <see cref="ScanStatus"/>). Ergebnis + Beispiele ohne Treffer im Log.
    /// </summary>
    public static void ScanAllMaterials()
    {
        try
        {
            EnsureLoaded();
            string bnk = Paradise.Core.Flash.FlashHost.BnkDir;
            var files = Directory.GetFiles(bnk, "*.bnk", SearchOption.AllDirectories);
            var seen = new HashSet<ulong>();
            int fi = 0, noMatch = 0;
            foreach (var f in files)
            {
                fi++;
                ScanStatus = $"Baenke {fi}/{files.Length}";
                BnkArchive bank;
                try { bank = BnkArchive.Open(f); } catch { continue; }
                foreach (var e in bank.Entries)
                {
                    if (!string.Equals(e.Extension, ".2dm", StringComparison.OrdinalIgnoreCase)) continue;
                    Material2DM lib;
                    try { lib = Material2DM.Parse(bank.Read(e)); } catch { continue; }
                    foreach (var def in lib.Materials.Values)
                    {
                        if (!seen.Add(def.Key ^ def.ShaderSet)) continue;
                        if (Resolve(def) == null && def.Spec != null && ++noMatch <= 20)
                            Log.Debug($"Original-Shader: ohne Treffer: {def.Name} Satz {def.ShaderSet:x16} Schalter {Convert.ToHexString(def.Spec)}");
                    }
                }
            }
            ScanStatus = $"Scan fertig ({files.Length} Baenke)";
            var rep = new System.Text.StringBuilder(Stats).Append("\n\nSatz: exakt / angenaehert\n");
            lock (s_lock)
                foreach (var kv in s_perSet.OrderByDescending(k => k.Value[1]))
                    rep.Append(kv.Key).Append(": ").Append(kv.Value[0]).Append(" / ").Append(kv.Value[1]).Append('\n');
            rep.Append("\nBeispiele angenaehert (Material, Satz, abweichende Schalter Shader/Material):\n").Append(s_examples);
            try
            {
                string path = Path.Combine(Paradise.Core.Config.UserPaths.Logs, "original_scan_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                File.WriteAllText(path, rep.ToString());
                ScanStatus += " - " + Path.GetFileName(path);
            }
            catch (Exception ex) { Log.Warn("Original-Shader-Scan: Bericht: " + ex.Message); }
        }
        catch (Exception ex) { ScanStatus = "Scan-Fehler: " + ex.Message; Log.Error("Original-Shader-Scan: " + ex); }
    }

    public static IReadOnlyList<ShaderLibrary> Libraries { get { EnsureLoaded(); return s_libs!; } }

    static void EnsureLoaded()
    {
        if (s_libs != null) return;
        lock (s_lock)
        {
            if (s_libs != null) return;
            var list = new List<ShaderLibrary>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string dir = OriginalShaders.ShaderDir;
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "*.sob"))
                {
                    try
                    {
                        var lib = ShaderLibrary.Load(f);
                        if (lib == null) continue;
                        list.Add(lib);
                        s_byGuid[lib.Guid] = lib;
                    }
                    catch (Exception ex) { Log.Error("Original-Shader: " + Path.GetFileName(f) + ": " + ex.Message); }
                }
            s_libs = list;
        }
    }

    public static ShaderLibrary? FindLibrary(string name) => Libraries.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    public static ulong HashSpec(ReadOnlySpan<byte> spec)
    {
        const ulong feedback = 0x00f0d50a00600340UL, offset = 0x6422fb175d04f76bUL;
        ulong r = 0;
        for (int i = 0; i < NumSpecs; i++)
        {
            for (int s = 0; s < 8; s++)
            {
                bool fb = ((r >> 31) & 1) != 0;
                r = (r << 1) | (r >> 63);
                if (fb) r ^= feedback;
            }
            r ^= (ulong)(i < spec.Length ? spec[i] : 0) << 32;
        }
        return r ^ offset;
    }

    /// <summary>
    /// Material-GUIDs ohne eigenen Satz, die die exe umleitet (tdu2-specs §6.2, Handler-Tabelle): Auto-Innenräume/Scheiben ->
    /// TD_CarMat_NM, Lichter/Details -> TD_CarPaint_NM, Baumstämme -> TD_Vegetation.
    /// </summary>
    static ulong Remap(ulong guid)
    {
        string? name = guid switch
        {
            0x69f07f72719bf862UL or 0xb39c35015789e65cUL => "TD_CarMat_NM",
            0x1b38348331d3942aUL => "TD_CarPaint_NM",
            0x37078865c0ca336eUL => "TD_Vegetation",
            _ => null,
        };
        if (name == null || s_byGuid.ContainsKey(guid)) return guid;
        var lib = s_libs!.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        return lib?.Guid ?? guid;
    }

    /// <summary>
    /// Laufzeit-Schalter des Szenen-Handlers (in der exe verschlüsselt; aus den Permutationen rückgewonnen
    /// EdenRenderer::updateProgram): S_NBTEXTURES = belegte Textur-Slots; TD_HeightMap/TD_Ground_HM: S_USERTYPE4 = Raster
    /// (0 Sektor, 1 Area, 2 Insel), S_USERTYPE7 = Detail-Ebenen (Slots 2/4/6), S_NBTEXTURES = 2 + 2·D1 + 2·D2 + D3 bzw. 1.
    /// </summary>
    static readonly ulong s_kDirtColor = MaterialDef.KeyFromName("DIRTCOLOR"), s_kDirtScratch = MaterialDef.KeyFromName("DIRTSCRATCH"),
                                  s_kStickers = MaterialDef.KeyFromName("STIKERS"), s_kDamage = MaterialDef.KeyFromName("DAMAGE");

    static byte[] RuntimeSpec(MaterialDef def, ulong guid)
    {
        var spec = (byte[])def.Spec!.Clone();
        bool Slot(int n) { foreach (var l in def.Layers) if (l.Slot == n && l.TextureKey != 0) return true; return false; }
        string fam = s_byGuid.TryGetValue(guid, out var lib) ? lib.Name.ToUpperInvariant() : "";
        if (fam == "TD_HEIGHTMAP" || fam == "TD_GROUND_HM")
        {
            string nm = def.Name;
            bool island = nm.Equals("Ibiza", StringComparison.OrdinalIgnoreCase) || nm.Equals("Hawai", StringComparison.OrdinalIgnoreCase) || nm.Equals("Casino", StringComparison.OrdinalIgnoreCase);
            int grid = island ? 2 : nm.StartsWith("Area", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            spec[35] = (byte)grid;
            if (grid == 0)
            {
                bool d1 = Slot(2), d2 = Slot(4), d3 = Slot(6);
                spec[38] = (byte)((d1 ? 1 : 0) | (d2 ? 2 : 0) | (d3 ? 4 : 0));
                spec[0] = (byte)(2 + (d1 ? 2 : 0) + (d2 ? 2 : 0) + (d3 ? 1 : 0));
            }
            else { spec[38] = 0; spec[0] = 1; }
        }
        else
        {
            int n = 0;
            // Dreck-Ebene der Autos (DIRTCOLOR bzw. TD_CarPaint2 DIRTSCRATCH) steht in der .2DM ohne Textur; das Spiel belegt sie zur
            // Laufzeit immer mit dirt_scratch.2db -> zählt als belegter Slot, sonst fällt die Wahl auf eine Permutation ohne
            // Dreck-Sampler (Test 24.11. 21:01: Dreckstufe 0,5 laut Log, am Lack nichts zu sehen) [s318]
            // ebenso die anderen Laufzeit-Ebenen der Lacke (TD_CarPaint2: STIKERS = Beklebung, DAMAGE = Schadenskarte)
            foreach (var l in def.Layers) if (l.TextureKey != 0 || l.Semantic == s_kDirtColor || l.Semantic == s_kDirtScratch || l.Semantic == s_kStickers || l.Semantic == s_kDamage) n++;
            if (n > 0) spec[S_NBTEXTURES] = (byte)n;
        }
        // Normalmap-Sätze (TD_CarMat_NM, TD_CarPaint_NM): alle gespeicherten Permutationen haben S_TANGENTSPACE = 1, die Materialien 0
        // -> setzt der Handler zur Laufzeit [vermutet aus Scan 24.11.]
        if (fam.EndsWith("_NM")) spec[15] = 1;
        // S_USEALPHATEST (Schalter 21) steht nur in wenigen .2DM (Kennzeichen, V_TBush_SprB), die Permutationen brauchen ihn (td_mirror: NB 2 + Alpha-Test = PS mit
        // DETAIL-Maske und texkill gegen __Alpha_Ref) -> aus dem Render-State des Materials wie OriginalMaterial.AlphaTest
        // ([7] Referenz != 0, [5] == 0). Ohne ihn wurde u. a. der Rückspiegel ohne Umriss-Maske gezeichnet (Test 24.11.).
        if (def.Flags[7] != 0 && def.Flags[5] == 0) spec[S_USEALPHATEST] = 1;
        return spec;
    }

    static int Weight(int i) => i switch
    {
        S_USESKINNING or S_PASS_Z or S_PASS_SHADOWCAST or S_PASS_SHADOWRECEIVE => 1000,
        S_LOD => 1,
        S_NBTEXTURES => 4,
        S_FOG or S_MODE => 6,
        _ => 10,
    };

    static ShaderLibrary.Item? BestItem(ShaderLibrary.Profile p, byte[] spec, out int diffs)
    {
        ShaderLibrary.Item? best = null; int bestCost = int.MaxValue; diffs = 0;
        foreach (var it in p.Items)
        {
            if (it.Spec.Length == 0) continue;
            int cost = 0, n = 0;
            for (int i = 0; i < NumSpecs; i++)
            {
                byte v = i < it.Spec.Length ? it.Spec[i] : (byte)0;
                if (v != spec[i]) { cost += Weight(i); n++; }
            }
            if (cost < bestCost) { bestCost = cost; best = it; diffs = n; if (cost == 0) break; }
        }
        return best;
    }

    /// <summary>Original-Shader für ein Material (null = kein Satz/keine Permutation).</summary>
    public static OriginalProgram? Resolve(MaterialDef def) => Resolve(def, false);

    /// <summary>TD_Road (Satz-GUID c8ad93c95455291b) – Regen-Permutation wie WFX_WetGrd (exe 0xa630f0).</summary>
    public const ulong SetRoad = 0xc8ad93c95455291bUL;

    /// <summary>
    /// <paramref name="wet"/> = Regenvariante der Straße wie exe 0xa630f0 (WFX_WetGrd, Rückruf je Material): nur Satz TD_Road und nur
    /// wenn Schalter S_USERTYPE7 (Satz +0x36 = Schalter 38) 0 ist; setzt S_USERTYPE9 (Satz +0x38 = Schalter 40) = nass und belegt drei
    /// weitere Textur-Slots (s6 RIPPLE, s7 Umgebungswürfel, s8 PUDDLES) -> S_NBTEXTURES + 3. Geprüft an TD_Road.sob: alle 72
    /// Regen-Pixelshader (RainSpecs, gTexSamplerPuddlesMap) unterscheiden sich von ihrem trockenen Gegenstück genau um
    /// S_USERTYPE9 = 1 und S_NBTEXTURES + 3. Ohne Regenvariante null.
    /// </summary>
    public static OriginalProgram? Resolve(MaterialDef def, bool wet)
    {
        EnsureLoaded();
        if (def.Spec == null || def.Spec.Length < NumSpecs) { lock (s_lock) { Resolved++; NoSpec++; } return null; }
        lock (s_lock)
        {
            ulong guid = Remap(def.ShaderSet);   // im Lock: Find trägt neue Zuordnungen in s_byGuid ein
            var spec = RuntimeSpec(def, guid);
            if (wet)
            {
                if (guid != SetRoad || spec[38] != 0) return null;
                spec[40] = 1;
                spec[S_NBTEXTURES] = (byte)(spec[S_NBTEXTURES] + 3);
            }
            string key = guid.ToString("x16") + Convert.ToHexString(spec, 0, NumSpecs);
            Resolved++;
            if (!s_programs.TryGetValue(key, out var prog))
            {
                prog = Find(guid, spec);
                s_programs[key] = prog;
            }
            if (prog == null) Missing++; else if (prog.Differences == 0) Exact++; else Approx++;
            if (prog != null)
            {
                if (!s_perSet.TryGetValue(prog.Library.Name, out var c)) s_perSet[prog.Library.Name] = c = new int[2];
                c[prog.Differences == 0 ? 0 : 1]++;
                if (prog.Differences != 0 && prog.Spec != null)
                {
                    s_exampleCount.TryGetValue(prog.Library.Name, out int ne);
                    if (ne < 8)
                    {
                        s_exampleCount[prog.Library.Name] = ne + 1;
                        var dsb = new System.Text.StringBuilder();
                        for (int i = 0; i < NumSpecs; i++)
                            if (prog.Spec[i] != spec[i]) dsb.Append(SpecNames[i]).Append('=').Append(prog.Spec[i]).Append('/').Append(spec[i]).Append(' ');
                        s_examples.Append(prog.Library.Name).Append(' ').Append(def.Name).Append(": ").Append(dsb).Append('\n');
                    }
                }
            }
            return prog;
        }
    }

    static readonly HashSet<ulong> s_missingSets = new();

    static OriginalProgram? Find(ulong guid, byte[] spec)
    {
        var candidates = new List<ShaderLibrary>();
        if (s_byGuid.TryGetValue(guid, out var exactLib)) candidates.Add(exactLib);
        else
        {
            // Satz ohne .sob im Shader-Ordner: kein „ähnlichster“ Satz nach GUID-Ziffern (falsche Sampler-Belegung) – das Material
            // bleibt beim eigenen Shader, Warnung je Satz im Log.
            if (s_missingSets.Add(guid)) Log.Warn($"Original Shader: The {guid:x16} set is missing from the shader folder – the material remains with its own shader");
            return null;
        }
        var cand = new byte[NumSpecs];
        foreach (var lib in candidates)
        {
            var vsP = lib.Vs; var psP = lib.Ps;
            if (vsP == null || psP == null) continue;
            ShaderLibrary.Item? psI = null, vsI = null;
            int diffs = 0;
            bool Try()
            {
                ulong id = HashSpec(cand);
                if (psP.ById.TryGetValue(id, out var p) && vsP.ById.TryGetValue(id, out var v)) { psI = p; vsI = v; return true; }
                return false;
            }
            var optional = new List<int>();
            for (int i = 1; i < NumSpecs; i++) if (spec[i] != 0 && i != S_LOD) optional.Add(i);
            var texCounts = new List<int> { spec[S_NBTEXTURES] };
            for (int d = 1; d <= 10; d++)
            {
                if (spec[S_NBTEXTURES] + d <= 16) texCounts.Add(spec[S_NBTEXTURES] + d);
                if (spec[S_NBTEXTURES] - d >= 0) texCounts.Add(spec[S_NBTEXTURES] - d);
            }
            for (int cleared = 0; cleared <= 2 && psI == null; cleared++)
            {
                foreach (int nt in texCounts)
                {
                    if (psI != null) break;
                    void Setup() { Array.Copy(spec, cand, NumSpecs); cand[S_NBTEXTURES] = (byte)nt; cand[S_LOD] = 0; }
                    if (cleared == 0) { Setup(); Try(); }
                    else if (cleared == 1)
                        for (int a = 0; a < optional.Count && psI == null; a++) { Setup(); cand[optional[a]] = 0; Try(); }
                    else
                        for (int a = 0; a < optional.Count && psI == null; a++)
                            for (int b = a + 1; b < optional.Count && psI == null; b++) { Setup(); cand[optional[a]] = 0; cand[optional[b]] = 0; Try(); }
                }
                diffs = cleared;
            }
            if (psI == null)
            {
                psI = BestItem(psP, spec, out diffs);
                if (psI != null) vsP.ById.TryGetValue(psI.Id, out vsI);
                if (vsI == null) psI = null;
            }
            if (psI != null && vsI != null && psI.Attrib < psP.Attribs.Count && vsI.Attrib < vsP.Attribs.Count)
            {
                if (candidates.Count > 1) { s_byGuid[guid] = lib; }
                return new OriginalProgram
                {
                    Library = lib, Vs = vsP.Attribs[vsI.Attrib], Ps = psP.Attribs[psI.Attrib], VsIndex = vsI.Attrib, PsIndex = psI.Attrib,
                    ItemId = psI.Id, Differences = diffs, Spec = psI.Spec.Length >= NumSpecs ? psI.Spec : (byte[])cand.Clone(),
                };
            }
        }
        return null;
    }
}