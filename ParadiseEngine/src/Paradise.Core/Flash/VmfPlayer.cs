#nullable disable
using Paradise.Core.Formats;
using Paradise.Core.Math;
using Paradise.Core.Render;
using System.Diagnostics;
using System.Text;

namespace Paradise.Core.Flash
{
    /// <summary>
    /// Plays a VMF movie (Eden Flash) as a screen overlay.
    ///  - Display lists/timelines (PlaceObject2/RemoveObject2, sprites, labels), movie frame rate
    ///  - ActionScript subset: Stop, Play, NextFrame, PrevFrame, GotoFrame, GoToLabel, SetTarget
    ///  - Shapes are rasterized on the CPU (VmfRaster) and cached as textures (per zoom level)
    ///  - Interface as in the game: clips by path (“/HUD/compteur_ext/aiguille”), text field variables (“/:Gear”)
    ///  - Dynamic textures: Shapes in a clip with a registered name are replaced by the texture
    ///    (e.g., “c_ext” → EXT_RPM, “aiguille_speed” → Needle from the car’s instrument panel)
    /// Stage 1280 x 960, scaled to screen width and centered vertically (16:9 displays y 120–840).
    /// </summary>
    public sealed partial class VmfPlayer : IDisposable
    {
        public sealed class Instance
        {
            public VmfMovie.Timeline Timeline;
            public int Frame = -1;
            public bool Playing = true;
            public readonly SortedDictionary<int, DisplayObject> List = new SortedDictionary<int, DisplayObject>();
            public DisplayObject Owner;
        }

        public sealed class DisplayObject
        {
            public int Depth, CharId, ClipDepth, Ratio;
            public Matrix2D Matrix = Matrix2D.Identity;
            public ColorTransform Cx = ColorTransform.Identity;
            public string Name;
            public Instance Sprite;
            /// <summary>Custom texture for all shapes in this clip (such as logos/images loaded by the game via “lure”); null = original.</summary>
            public Texture DynTex;
            public Instance Parent;
            public bool Visible = true;
            public float? Rotation;      // vom Spiel gesetzt (Grad, zusätzlich zur Zeitleisten-Matrix)
            public Vector2? Position;    // vom Spiel gesetzt (Bühnenkoordinaten relativ zum Eltern-Clip, ersetzt Tx/Ty)
            /// <summary>Just like in Flash: after a script has set _x, _y, _xscale, _rotation, or _alpha, the timeline ignores the matrix and color.</summary>
            public bool ScriptMatrix, ScriptCx;
            /// <summary>Corrections (VmfPatches / Flash Debugger): in addition to the timeline – offset (stage pixels in the parent clip),
            /// scaling (factor per axis), custom text for text fields (null = variable/default text).</summary>
            public Vector2 Offset;
            public Vector2 ScaleMul = Vector2.one;
            public string TextOverride;
            /// <summary>Atomic Fade-In (VmfPlayerAsync.cs): Number of the last character fully displayed; has been waiting since
            /// the timeline was paused until (while the shapes of the newly appearing clip are still being rasterized).</summary>
            internal int SeenDraw = -10;
            internal bool Revealed;
            internal float RevealSince, FrozenUntil;
        }

        [NonSerialized] public static float LastFrameTimeMs;
        [NonSerialized] public static float LastDrawTimeMs;
        [NonSerialized] public static float LastTickTimeMs;
        [NonSerialized] public static float LastRasterIntegrateTimeMs;
        [NonSerialized] public static float LastMouseTimeMs;
        [NonSerialized] public static float LastApplyTimeMs;
        [NonSerialized] public static int LastDrawCalls;
        [NonSerialized] public static int LastShapeDraws;
        [NonSerialized] public static int LastTextDraws;
        [NonSerialized] public static int LastQuadBatches;
        [NonSerialized] public static long LastRasterPendingBytes;
        [NonSerialized] public static int LastRasterQueueCount;
        [NonSerialized] public static int LastRasterDoneCount;
        static readonly Queue<float> _frameTimeHist = new Queue<float>(300);
        static readonly Queue<float> _drawTimeHist = new Queue<float>(300);
        static readonly Queue<float> _tickTimeHist = new Queue<float>(300);
        static readonly Queue<int> _drawCallHist = new Queue<int>(300);
        static readonly object _histLock = new object();
        internal static void PushFlashMetric(float frameMs, float drawMs, float tickMs, int drawCalls)
        {
            lock (_histLock)
            {
                _frameTimeHist.Enqueue(frameMs);
                while (_frameTimeHist.Count > 300) _frameTimeHist.Dequeue();
                _drawTimeHist.Enqueue(drawMs);
                while (_drawTimeHist.Count > 300) _drawTimeHist.Dequeue();
                _tickTimeHist.Enqueue(tickMs);
                while (_tickTimeHist.Count > 300) _tickTimeHist.Dequeue();
                _drawCallHist.Enqueue(drawCalls);
                while (_drawCallHist.Count > 300) _drawCallHist.Dequeue();
            }
        }
        public static float[] GetFrameHistory() { lock (_histLock) return _frameTimeHist.ToArray(); }
        public static float[] GetDrawHistory() { lock (_histLock) return _drawTimeHist.ToArray(); }
        public static float[] GetTickHistory() { lock (_histLock) return _tickTimeHist.ToArray(); }
        public static int[] GetDrawCallHistory() { lock (_histLock) return _drawCallHist.ToArray(); }
        public static void ClearFlashMetrics() { lock (_histLock) { _frameTimeHist.Clear(); _drawTimeHist.Clear(); _tickTimeHist.Clear(); _drawCallHist.Clear(); } }
        public static string GetFlashLogPath()
        {
            try { return Path.Combine(Paradise.Core.Config.UserPaths.Logs, "FlashPerfLog.csv"); }
            catch { return Path.Combine(Environment.CurrentDirectory, "FlashPerfLog.csv"); }
        }
        public static void AppendFlashLog()
        {
            try
            {
                string path = GetFlashLogPath();
                bool exists = File.Exists(path);
                using (var sw = new StreamWriter(path, true, Encoding.UTF8))
                {
                    if (!exists) sw.WriteLine("timestamp,frameMs,drawMs,tickMs,mouseMs,applyMs,rasterIntMs,drawCalls,shapeDraws,textDraws,quadBatches,pendingBytes,rasterQueue,rasterDone");
                    sw.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "," + LastFrameTimeMs.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "," + LastDrawTimeMs.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "," + LastTickTimeMs.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "," + LastMouseTimeMs.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "," + LastApplyTimeMs.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "," + LastRasterIntegrateTimeMs.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "," + LastDrawCalls + "," + LastShapeDraws + "," + LastTextDraws + "," + LastQuadBatches + "," + LastRasterPendingBytes + "," + LastRasterQueueCount + "," + LastRasterDoneCount);
                }
            }
            catch { }
        }

        /// <summary>Restrict a clip to the coordinate system of its parent clip (for _width/_height); null = empty.</summary>
        public Rect? LocalBounds(DisplayObject d, int depth = 0)
        {
            if (d == null || depth > 12) return null;
            var m = LocalMatrix(d);
            Rect? inner = null;
            if (d.Sprite != null)
            {
                foreach (var c in d.Sprite.List.Values)
                {
                    if (!c.Visible || c.ClipDepth > 0) continue;
                    var r = LocalBounds(c, depth + 1);
                    if (!r.HasValue) continue;
                    inner = inner.HasValue ? Rect.MinMaxRect(Mathf.Min(inner.Value.xMin, r.Value.xMin), Mathf.Min(inner.Value.yMin, r.Value.yMin),
                                                            Mathf.Max(inner.Value.xMax, r.Value.xMax), Mathf.Max(inner.Value.yMax, r.Value.yMax)) : r;
                }
            }
            else if (Movie.Chars.TryGetValue(d.CharId, out var ch))
            {
                if (ch is VmfMovie.Shape sh) inner = sh.Bounds;
                else if (ch is VmfMovie.EditText et) inner = et.Bounds;
            }
            if (!inner.HasValue) return null;
            var b = inner.Value; bool any = false; float x0 = 0, y0 = 0, x1 = 0, y1 = 0;
            foreach (var c in new[] { new Vector2(b.xMin, b.yMin), new Vector2(b.xMax, b.yMin), new Vector2(b.xMin, b.yMax), new Vector2(b.xMax, b.yMax) })
            {
                var q = m.Apply(c);
                if (!any) { x0 = x1 = q.x; y0 = y1 = q.y; any = true; }
                else { x0 = Mathf.Min(x0, q.x); x1 = Mathf.Max(x1, q.x); y0 = Mathf.Min(y0, q.y); y1 = Mathf.Max(y1, q.y); }
            }
            return Rect.MinMaxRect(x0, y0, x1, y1);
        }

        public VmfMovie Movie { get; private set; }
        public Instance Root { get; private set; }
        public bool Visible = true;
        [Range(0f, 1f)] public float Opacity = 1f;
        public int GuiDepth = 0;

        readonly Dictionary<string, string> _vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Texture> _dynamic = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _dynamicUpright = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // RenderTextures: v = 0 unten
        readonly Dictionary<string, TextureDef> _texDefs = new Dictionary<string, TextureDef>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, PixelImage> _pixels = new Dictionary<string, PixelImage>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<(int, int), (Texture2D tex, float ox, float oy, float sc)> _shapeCache = new Dictionary<(int, int), (Texture2D, float, float, float)>();
        readonly Dictionary<int, int> _shapeBest = new Dictionary<int, int>();   // Shape -> größte gerasterte Stufe
        int _rasterFrame = -1; float _rasterMs;
        const float RasterBudgetMs = 6f;
        static int StageBucket() => Mathf.RoundToInt(Mathf.Log(Mathf.Max(0.05f, Screen.width / VmfMovie.StageWidth), 2f) * 2f);
        float _accum;

        /// <summary>Loads the movie from a front-end bank (e.g., Euro/Bnk/frontend/hires/gauges.bnk).</summary>
        /// <summary>Loaded bank (for display/debugger).</summary>
        public string BankPath { get; private set; }

        public void Load(string bankPath)
        {
            StopRasterThread();
            BankPath = bankPath;
            var bank = BnkArchive.Open(bankPath);
            BnkEntry vmfEntry = null;
            foreach (var e in bank.All(".vmf")) { vmfEntry = e; break; }
            if (vmfEntry == null) throw new FileNotFoundException("no VMF file in: " + bankPath);
            foreach (var e in bank.All(".2db"))
            {
                var def = TextureDef.Parse(bank.Read(e), Path.GetFileNameWithoutExtension(e.Name));
                if (def != null) _texDefs[def.Name] = def;
            }
            Movie = VmfMovie.Parse(bank.Read(vmfEntry), n => _texDefs.ContainsKey(n));
            AssignPagesBySize();
            Root = new Instance { Timeline = Movie.Root };
            RegisterFonts();
            GotoFrame(Root, 0, true);
            StartPrewarm();   // Shapes/Schriften im Hintergrund vorbereiten (VmfPlayerAsync.cs)
        }

        /// <summary>
        /// Banks with multiple Atlas pages (hud.vmf: page_001 2048×1024, page_002 128×128, page_003 512×512):
        /// The page is determined by the bitmap size divided by the UV section (w / (u1 - u0) = page width), not by the header table.
        /// </summary>
        void AssignPagesBySize()
        {
            var pages = new List<TextureDef>();
            foreach (var kv in _texDefs) if (kv.Key.StartsWith("page_", StringComparison.OrdinalIgnoreCase)) pages.Add(kv.Value);
            if (pages.Count == 0) return;
            foreach (var c in Movie.Chars.Values)
            {
                if (!(c is VmfMovie.Bitmap b) || b.TextureName == null || !b.TextureName.StartsWith("page_", StringComparison.OrdinalIgnoreCase)) continue;
                if (pages.Count == 1) { b.TextureName = pages[0].Name; continue; }
                float du = Mathf.Abs(b.U1 - b.U0), dv = Mathf.Abs(b.V1 - b.V0);
                if (du < 1e-6f || dv < 1e-6f) continue;
                float ew = b.Width / du, eh = b.Height / dv, best = float.MaxValue;
                foreach (var p in pages)
                {
                    float err = Mathf.Abs(Mathf.Log(ew / Mathf.Max(1, p.Width))) + Mathf.Abs(Mathf.Log(eh / Mathf.Max(1, p.Height)));
                    if (err < best) { best = err; b.TextureName = p.Name; }
                }
            }
        }

        public void SetDynamicTexture(string clipName, Texture2D tex) { SetDynamicTexture(clipName, tex, false); }
        /// <summary>Replaces the shapes in the clip clipName with tex. upright = true for render textures (v = 0 at the bottom).</summary>
        public void SetDynamicTexture(string clipName, Texture tex, bool upright)
        {
            if (tex != null) _dynamic[clipName] = tex; else _dynamic.Remove(clipName);
            if (upright && tex != null) _dynamicUpright.Add(clipName); else _dynamicUpright.Remove(clipName);
        }
        /// <summary>
        /// Set variable. Notation as in the game: “/:Title” (root), “/car_datas/datas_base:cur_power” (clip),
        /// “_root.MyMoneyStr” (= “/:MyMoneyStr”), “name” (= root). Text fields resolve their (including relative) variable names
        /// (“../../:cur_power”, “_parent.num_contest”, “val”) to the same absolute key.
        /// </summary>
        public void SetVariable(string name, string value)
        {
            if (AutoPin) _pinVars[name] = new PinVar { Value = value ?? "", Typed = false };
            var k = ResolveVar(null, name); _vars[k] = value; _vars[name] = value; _objVars.Remove(k);
        }
        public string GetVariable(string name) => _vars.TryGetValue(ResolveVar(null, name), out var v) ? v : (_vars.TryGetValue(name, out v) ? v : null);

        /// <summary>Absolute path of an instance (“/” = root, otherwise “/a/b”; unnamed clips as “#Depth”).</summary>
        public static string PathOf(Instance inst)
        {
            var parts = new List<string>();
            for (var i = inst; i != null && i.Owner != null; i = i.Owner.Parent) parts.Add(i.Owner.Name ?? ("#" + i.Owner.Depth));
            parts.Reverse();
            return "/" + string.Join("/", parts);
        }

        /// <summary>Variable name (relative to ctx, null = root) -> canonical key “/path:name” (root “/:name”).</summary>
        public static string ResolveVar(Instance ctx, string v)
        {
            if (string.IsNullOrEmpty(v)) return v;
            string path, name;
            int colon = v.LastIndexOf(':');
            if (colon >= 0) { path = v.Substring(0, colon); name = v.Substring(colon + 1); }
            else
            {
                int dot = v.LastIndexOf('.');
                if (dot >= 0) { path = v.Substring(0, dot).Replace('.', '/'); name = v.Substring(dot + 1); }
                else { path = ""; name = v; }
            }
            var segs = new List<string>();
            bool absolute = path.StartsWith("/") || path.StartsWith("_root", StringComparison.OrdinalIgnoreCase);
            if (!absolute && ctx != null)
            {
                var basePath = PathOf(ctx);
                foreach (var p in basePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)) segs.Add(p);
            }
            foreach (var p in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (p == "." || p.Equals("_root", StringComparison.OrdinalIgnoreCase) || p.Equals("_level0", StringComparison.OrdinalIgnoreCase)) continue;
                if (p == ".." || p.Equals("_parent", StringComparison.OrdinalIgnoreCase)) { if (segs.Count > 0) segs.RemoveAt(segs.Count - 1); continue; }
                segs.Add(p);
            }
            return "/" + string.Join("/", segs) + ":" + name;
        }

        /// <summary>Set the clip to a frame number (0-based), e.g., bar graphs with 101 frames (0..100 %).</summary>
        public bool GotoFrameNumber(string path, int frame, bool play = false)
        {
            if (!play && AutoPin) return PinFrame(path, frame + 1);
            var inst = path == "/" || string.IsNullOrEmpty(path) ? Root : Find(path)?.Sprite;
            if (inst == null) return false;
            inst.Playing = play; GotoFrame(inst, frame, true);   // stop() im Ziel-Frame behält Vorrang
            return true;
        }

        public bool HasLabel(string path, string label)
        {
            var inst = path == "/" || string.IsNullOrEmpty(path) ? Root : Find(path)?.Sprite;
            return inst != null && inst.Timeline.Labels.ContainsKey(label);
        }

        /// <summary>
        /// Attach a sprite (character ID) as a named instance under parentPath (like attachMovie) – e.g., spot icons on the minimap.
        /// Returns the DisplayObject (already exists: the same one).
        /// </summary>
        public DisplayObject AttachSprite(string parentPath, int charId, string name, int depth)
        {
            var parent = parentPath == "/" || string.IsNullOrEmpty(parentPath) ? Root : Find(parentPath)?.Sprite;
            if (parent == null) return null;
            if (parent.List.TryGetValue(depth, out var existing) && existing.CharId == charId) return existing;
            return AttachTo(parent, charId, name, depth);
        }

        DisplayObject AttachTo(Instance parent, int charId, string name, int depth)
        {
            if (parent == null || !Movie.Chars.TryGetValue(charId, out var ch) || !(ch is VmfMovie.Timeline sp)) return null;
            var d = new DisplayObject { Depth = depth, CharId = charId, Parent = parent, Name = name };
            d.Sprite = new Instance { Timeline = sp, Owner = d };
            GotoFrame(d.Sprite, 0, true);
            parent.List[depth] = d;
            return d;
        }

        static readonly VmfMovie.Timeline EmptyTimeline = new VmfMovie.Timeline { Id = -2 };
        DisplayObject AttachEmpty(Instance parent, string name, int depth)
        {
            if (parent == null) return null;
            if (EmptyTimeline.Frames.Count == 0) EmptyTimeline.Frames.Add(new List<VmfMovie.FrameOp>());
            var d = new DisplayObject { Depth = depth, CharId = -2, Parent = parent, Name = name };
            d.Sprite = new Instance { Timeline = EmptyTimeline, Owner = d, Frame = 0 };
            parent.List[depth] = d;
            return d;
        }

        public void Detach(string parentPath, int depth)
        {
            var parent = parentPath == "/" || string.IsNullOrEmpty(parentPath) ? Root : Find(parentPath)?.Sprite;
            if (parent != null && parent.List.TryGetValue(depth, out var old) && old.Name != null)
                ForgetClipTextures((parentPath == "/" || string.IsNullOrEmpty(parentPath) ? "" : parentPath.TrimEnd('/')) + "/" + old.Name);
            parent?.List.Remove(depth);
        }

        /// <summary>Clip by path: “/HUD/compteur_ext/aiguille” (absolute) or “HUD.compteur_ext” (relative to the root).</summary>
        public DisplayObject Find(string path, Instance from = null)
        {
            if (Root == null || string.IsNullOrEmpty(path)) return null;
            var inst = path.StartsWith("/") ? Root : (from ?? Root);
            DisplayObject cur = null;
            foreach (var part in (path.IndexOf('/') >= 0 ? path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries) : path.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries)))
            {
                if (part == "_root") { inst = Root; continue; }
                if (part == "..") { inst = inst?.Owner?.Parent; continue; }
                if (inst == null) return null;
                cur = null;
                // "#12" = unbenanntes Objekt auf Tiefe 12 (so zeigt es der Flash Debugger)
                if (part.Length > 1 && part[0] == '#' && int.TryParse(part.Substring(1), out int dep)) inst.List.TryGetValue(dep, out cur);
                else foreach (var d in inst.List.Values) if (string.Equals(d.Name, part, StringComparison.OrdinalIgnoreCase)) { cur = d; break; }
                if (cur == null) return null;
                inst = cur.Sprite;
            }
            return cur;
        }

        public bool GotoLabel(string path, string label, bool play = false)
        {
            if (!play && AutoPin) return PinLabel(path, label);
            var inst = path == "/" || string.IsNullOrEmpty(path) ? Root : Find(path)?.Sprite;
            if (inst == null || !inst.Timeline.Labels.TryGetValue(label, out var f)) return false;
            inst.Playing = play; GotoFrame(inst, f, true);   // stop() im Ziel-Frame behält Vorrang
            return true;
        }

        public void SetVisible(string path, bool visible) { var d = Find(path); if (d != null) d.Visible = visible; }
        public void SetRotation(string path, float deg) { var d = Find(path); if (d != null) d.Rotation = deg; }
        public void SetPosition(string path, Vector2 pos) { var d = Find(path); if (d != null) d.Position = pos; }
        /// <summary>Set a texture only for this one clip (path) – e.g., brand logo in “/HUD/minimap/spot_0/ic/logo/lure”.</summary>
        /// <param name="onTop">Draw the image after the entire movie (on top of everything). Only for preview images that would be obscured in the movie by
        /// higher-level window elements (merchant purchase window); otherwise, follow the normal layer order (map tooltip).</param>
        public bool SetClipTexture(string path, Texture tex, bool onTop = false)
        {
            if (tex != null) { if (onTop) _topTextures.Add(tex); else _topTextures.Remove(tex); }
            // gemerkt: legt die Zeitleiste den Clip neu an (Animation "extend" …), bekommt er die Textur wieder (ApplyClipTextures)
            if (tex != null) _clipTex[path] = tex; else _clipTex.Remove(path);
            var d = Find(path); if (d == null) return false; d.DynTex = tex; return true;
        }

        /// <summary>Screen rectangle (GUI pixels, y-axis down) of a clip based on the bounds of its shapes; null if invisible or missing.</summary>
        public Rect? ScreenRect(string path)
        {
            if (Root == null) return null;
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            float sc = Screen.width / VmfMovie.StageWidth;
            float offY = (VmfMovie.StageHeight * sc - Screen.height) * 0.5f;
            var m = new Matrix2D { A = sc, D = sc, Ty = -offY };
            var inst = Root; DisplayObject cur = null;
            foreach (var part in parts)
            {
                cur = null;
                foreach (var d in inst.List.Values) if (string.Equals(d.Name, part, StringComparison.OrdinalIgnoreCase)) { cur = d; break; }
                if (cur == null || !cur.Visible) return null;
                m = m * LocalMatrix(cur);
                inst = cur.Sprite;
                if (inst == null) break;
            }
            if (cur == null || inst == null) return null;
            bool any = false; float x0 = 0, y0 = 0, x1 = 0, y1 = 0;
            foreach (var d in inst.List.Values)
            {
                if (!Movie.Chars.TryGetValue(d.CharId, out var ch) || !(ch is VmfMovie.Shape sh)) continue;
                var mw = m * LocalMatrix(d);
                foreach (var c in new[] { new Vector2(sh.Bounds.xMin, sh.Bounds.yMin), new Vector2(sh.Bounds.xMax, sh.Bounds.yMin), new Vector2(sh.Bounds.xMin, sh.Bounds.yMax), new Vector2(sh.Bounds.xMax, sh.Bounds.yMax) })
                {
                    var q = mw.Apply(c);
                    if (!any) { x0 = x1 = q.x; y0 = y1 = q.y; any = true; }
                    else { x0 = Mathf.Min(x0, q.x); x1 = Mathf.Max(x1, q.x); y0 = Mathf.Min(y0, q.y); y1 = Mathf.Max(y1, q.y); }
                }
            }
            return any ? Rect.MinMaxRect(x0, y0, x1, y1) : (Rect?)null;
        }

        static Matrix2D LocalMatrix(DisplayObject d)
        {
            var mm = d.Matrix;
            if (d.Position.HasValue) { mm.Tx = d.Position.Value.x; mm.Ty = d.Position.Value.y; }
            mm.Tx += d.Offset.x; mm.Ty += d.Offset.y;
            if (d.ScaleMul.x != 1f || d.ScaleMul.y != 1f) { mm.A *= d.ScaleMul.x; mm.B *= d.ScaleMul.x; mm.C *= d.ScaleMul.y; mm.D *= d.ScaleMul.y; }
            if (d.Rotation.HasValue) mm = mm * Matrix2D.Rotation(d.Rotation.Value);
            return mm;
        }

        public bool MouseEnabled;
        DisplayObject _hoverBtn, _pressBtn; bool _mouseDown;
        // startDrag
        DisplayObject _dragD; Vector2 _dragOff; bool _dragLock; Rect? _dragRect;

        static Matrix2D StageMatrix()
        {
            float sc = Screen.width / VmfMovie.StageWidth;
            float offY = (VmfMovie.StageHeight * sc - Screen.height) * 0.5f;
            return new Matrix2D { A = sc, D = sc, Ty = -offY };
        }

        static Vector2 MouseGui()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            var mp = FlashInput.mousePosition;
            return new Vector2(mp.x, Screen.height - mp.y);
#else
            return Vector2.zero;
#endif
        }

        /// <summary>Overall matrix (stage -> GUI pixels) of a clip.</summary>
        Matrix2D WorldOf(Instance inst)
        {
            var chain = new List<DisplayObject>();
            for (var i = inst; i != null && i.Owner != null; i = i.Owner.Parent) chain.Add(i.Owner);
            var m = StageMatrix();
            for (int k = chain.Count - 1; k >= 0; k--) m = m * LocalMatrix(chain[k]);
            return m;
        }

        /// <summary>Mouse position in the parent clip's coordinate system (such as _parent._xmouse/_ymouse).</summary>
        Vector2 MouseInParent(DisplayObject d) => WorldOf(d.Parent).Inverse().Apply(MouseGui());

        /// <summary>The mouse is hovering over a Flash button (the click is part of the movie).</summary>
        public bool MouseOverButton => _hoverBtn != null;
        /// <summary>
        /// Clips whose buttons the mouse should not hit (paths as in Find), e.g., the map cursor /curseur, which is itself
        /// located under the mouse: otherwise, it reports “mouse over button” and flickers with the mouse pointer.
        /// </summary>
        public readonly HashSet<string> MouseIgnore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<DisplayObject> _mouseIgnoreObjs = new HashSet<DisplayObject>();

        /// <summary>
        /// Text width (stage pixels) of a text variable's value in the font of its text field—a replacement for the native
        /// game function getTextLength(“_root.player_car”), which, for example, allows scrolling text to determine whether to scroll.
        /// </summary>
        public float MeasureVarText(string variable) => MeasureVarText(variable, variable);

        /// <summary>
        /// Same as <see cref="MeasureVarText(string)"/>; value from <paramref name="variable"/> (absolute, e.g., “/av_name_09:av_name”); font
        /// of the text field containing the variable name <paramref name="fieldVariable"/> (“av_name”) – getTextLength in duplicated clips
        /// (name tags general.bnk, Game/Online/NameTags).
        /// </summary>
        public float MeasureVarText(string variable, string fieldVariable)
        {
            if (Movie == null || string.IsNullOrEmpty(variable)) return 0f;
            string s = null;
            if (!_vars.TryGetValue(ResolveVar(null, variable), out s)) _vars.TryGetValue(variable, out s);
            VmfMovie.EditText field = null;
            foreach (var ch in Movie.Chars.Values)
                if (ch is VmfMovie.EditText et && string.Equals(et.Variable, fieldVariable, StringComparison.OrdinalIgnoreCase)) { field = et; break; }
            if (s == null) s = field?.Text ?? "";
            float size = field != null && field.Height > 0 ? field.Height : 12f;
            s = PlainText(s, field != null && field.Html);
            if (field != null && Movie.Fonts.TryGetValue(field.FontId, out var font) && font.Glyphs.Count > 0)
            {
                // breiteste Zeile (Markup und Zeilenumbrüche berücksichtigt)
                float w = 0f, best = 0f;
                foreach (var c in s) { if (c == '\n') { best = Mathf.Max(best, w); w = 0f; continue; } w += Advance(font, c, size); }
                return Mathf.Max(best, w) + 4f;
            }
            return s.Length * size * 0.5f;
        }

        /// <summary>GUI pixel (y down) -> coordinates in the clip's parentPath (“/” = stage); for SetPosition of children.</summary>
        public Vector2? GuiToLocal(string parentPath, Vector2 gui)
        {
            var p = string.IsNullOrEmpty(parentPath) || parentPath == "/" ? Root : Find(parentPath)?.Sprite;
            if (p == null) return null;
            return WorldOf(p).Inverse().Apply(gui);
        }

        void BeginDrag(DisplayObject d, bool lockCenter, Rect? bounds)
        {
            if (d == null) return;
            _dragD = d; _dragLock = lockCenter; _dragRect = bounds;
            var mp = MouseInParent(d);
            _dragOff = new Vector2(d.Matrix.Tx, d.Matrix.Ty) - mp;
        }

        void EndDrag() { _dragD = null; }

        void UpdateDrag()
        {
            var d = _dragD;
            if (d == null || d.Parent == null) { _dragD = null; return; }
            var p = MouseInParent(d);
            if (!_dragLock) p += _dragOff;
            if (_dragRect.HasValue)
            {
                var r = _dragRect.Value;
                p.x = Mathf.Clamp(p.x, Mathf.Min(r.xMin, r.xMax), Mathf.Max(r.xMin, r.xMax));
                p.y = Mathf.Clamp(p.y, Mathf.Min(r.yMin, r.yMax), Mathf.Max(r.yMin, r.yMax));
            }
            d.Matrix.Tx = p.x; d.Matrix.Ty = p.y; d.Position = null; d.ScriptMatrix = true;
        }

        DisplayObject _rPressBtn;

        void UpdateMouse()
        {
            if (!MouseEnabled || !Visible || Root == null) { _hoverBtn = null; _pressBtn = null; _mouseDown = false; _dragD = null; return; }
#if ENABLE_LEGACY_INPUT_MANAGER
            var pt = MouseGui();
            var stage = StageMatrix();
            if (_dragD != null) UpdateDrag();
            _mouseIgnoreObjs.Clear();
            foreach (var ip in MouseIgnore) { var io = Find(ip); if (io != null) _mouseIgnoreObjs.Add(io); }
            var hit = HitButton(Root, stage, pt);
            if (hit != _hoverBtn)
            {
                if (_hoverBtn != null) RunButton(_hoverBtn, _mouseDown ? 0x10 : 0x02);    // dragOut / rollOut
                _hoverBtn = hit;
                if (hit != null) RunButton(hit, _mouseDown ? 0x20 : 0x01);              // dragOver / rollOver
            }
            if (FlashInput.GetMouseButtonDown(0))
            {
                _mouseDown = true; _pressBtn = _hoverBtn;
                StoreClipVar(Root, "WhatMouseButton", 0.0);
                if (_hoverBtn != null) RunButton(_hoverBtn, 0x04);
            }
            // Rechtsklick auf eine Zeile der nativen Liste: wie im Spiel _root.WhatMouseButton = 1 (z.B. Steuerung: Belegung löschen)
            if (FlashInput.GetMouseButtonDown(1)) _rPressBtn = _hoverBtn != null && InActiveList(_hoverBtn.Parent) ? _hoverBtn : null;
            if (FlashInput.GetMouseButtonUp(1))
            {
                if (_rPressBtn != null && _rPressBtn == _hoverBtn)
                {
                    StoreClipVar(Root, "WhatMouseButton", 1.0);
                    RunButton(_rPressBtn, 0x08);
                    StoreClipVar(Root, "WhatMouseButton", 0.0);
                }
                _rPressBtn = null;
            }
            if (FlashInput.GetMouseButtonUp(0))
            {
                // Login-Popup u.ä.: Felder/Tasten-Symbole unter einem Button trotzdem bedienbar
                if (_mouseDown && _hoverBtn != null && HandleSubscribedClick(pt)) { _mouseDown = false; _pressBtn = null; return; }
                if (_mouseDown && _pressBtn != null)
                {
                    if (_pressBtn == _hoverBtn) RunButton(_pressBtn, 0x08);              // release
                    else RunButton(_pressBtn, 0x40);                                     // releaseOutside
                }
                // wie Flash: ohne Druck auf denselben Button kein onRelease (sonst löste ein Klick, der ein Menü unter der Maus
                // öffnet, gleich dessen ersten Eintrag aus – z.B. Mini-Menü der Karte)
                else if (_mouseDown && _hoverBtn == null) HandleFreeClick(pt);   // Tasten-Symbol / Textfeld (VmfHelpers)
                _mouseDown = false; _pressBtn = null;
            }
#endif
        }

        /// <summary>Topmost visible button under the item (GUI pixels); hit based on the hit records (otherwise Up).</summary>
        DisplayObject HitButton(Instance inst, Matrix2D m, Vector2 pt)
        {
            var list = RentList(inst);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var d = list[i];
                if (!d.Visible || d.ClipDepth > 0 || (d.Cx.Mul.w <= 0.003f && d.Cx.Add.w <= 0.003f)) continue;
                if (_mouseIgnoreObjs.Count > 0 && _mouseIgnoreObjs.Contains(d)) continue;
                var mw = m * LocalMatrix(d);
                if (d.Sprite != null) { var h = HitButton(d.Sprite, mw, pt); if (h != null) { ReturnList(list); return h; } continue; }
                if (!Movie.Chars.TryGetValue(d.CharId, out var ch) || !(ch is VmfMovie.Button b)) continue;
                // reine Tasten-Buttons (on(keyPress "<Up>") …) sind für die Maus unsichtbar
                bool mouse = b.Actions.Count == 0;
                foreach (var ba in b.Actions) if ((ba.Conditions & 0x01FF) != 0) { mouse = true; break; }
                if (!mouse) continue;
                bool hasHit = false;
                foreach (var r in b.Records) if ((r.States & 0x08) != 0) { hasHit = true; break; }
                foreach (var r in b.Records)
                {
                    if ((r.States & (hasHit ? 0x08 : 0x01)) == 0 || !Movie.Chars.TryGetValue(r.CharId, out var rc) || !(rc is VmfMovie.Shape rs)) continue;
                    var local = (mw * r.Matrix).Inverse().Apply(pt);
                    if (rs.Bounds.Contains(local)) { ReturnList(list); return d; }
                }
            }
            ReturnList(list);
            return null;
        }

        void RunButton(DisplayObject d, int condition)
        {
            if (d == null || !Movie.Chars.TryGetValue(d.CharId, out var ch) || !(ch is VmfMovie.Button b)) return;
            foreach (var a in b.Actions)
                if ((a.Conditions & condition) != 0) ExecFrameActions(d.Parent, a.Code);   // Button-Aktionen laufen in der Zeitleiste, die den Button enthält
            // AS2-Ereignisse am benannten Button (btn.onPress = function() {...})
            string ev = condition == 0x01 ? "onRollOver" : condition == 0x02 ? "onRollOut" : condition == 0x04 ? "onPress" : condition == 0x08 ? "onRelease"
                      : condition == 0x10 ? "onDragOut" : condition == 0x20 ? "onDragOver" : condition == 0x40 ? "onReleaseOutside" : null;
            if (ev != null) CallButtonHandler(d, ev);
        }

        // Reusable lists for iterations (otherwise, garbage per frame -> GC stuttering)
        readonly Stack<List<DisplayObject>> _listPool = new Stack<List<DisplayObject>>();
        List<DisplayObject> RentList(Instance inst)
        {
            var l = _listPool.Count > 0 ? _listPool.Pop() : new List<DisplayObject>(16);
            foreach (var d in inst.List.Values) l.Add(d);
            return l;
        }
        void ReturnList(List<DisplayObject> l) { l.Clear(); _listPool.Push(l); }

        /// <summary>Called by the host for each frame (Unity: MonoBehaviour.Update).</summary>
        public void Update()
        {
            if (Root == null) return;
            var swU = Stopwatch.StartNew();
            if (Visible && !_wasVisible) _holdUntil = Time.unscaledTime + FirstShowHoldSeconds;   // wird gerade eingeblendet
            _wasVisible = Visible;
            var swR = Stopwatch.StartNew();
            IntegrateRasters();   // fertige Hintergrund-Rasterungen -> Texturen (VmfPlayerAsync.cs)
            TrimDecodedBitmaps();
            try { UploadTick(); } catch { }
            LastRasterIntegrateTimeMs = (float)swR.Elapsed.TotalMilliseconds;
            var swM = Stopwatch.StartNew();
            UpdateMouse();
            ListScrollTick();   // Scrollbalken der nativen Listen (VmfScript.cs)
            TextInputTick();    // Tastatur in das Feld von setInputFocus (VmfHelpers.cs)
            LastMouseTimeMs = (float)swM.Elapsed.TotalMilliseconds;
            _accum += Time.unscaledDeltaTime;   // Menüs laufen auch bei angehaltenem Spiel (Pause)
            float step = 1f / Mathf.Max(1f, Movie.FrameRate);
            int guard = 0;
            var swT = Stopwatch.StartNew();
            while (_accum >= step && guard++ < 4) { _accum -= step; Tick(Root); RunEnterFrames(); }
            if (guard >= 4) _accum = 0f;
            LastTickTimeMs = (float)swT.Elapsed.TotalMilliseconds;
            var swA = Stopwatch.StartNew();
            ApplyForcedVisibility();   // ForceVisible (VmfHelpers) gilt auch nach _visible-Änderungen des Films
            ApplyPins();               // festgehaltene Werte/Labels (VmfHelpers: PinText/PinValue/PinLabel/PinScope)
            ApplyPatches();            // Korrekturen aus TDU2FlashPatches (VmfPatches.cs)
            LastApplyTimeMs = (float)swA.Elapsed.TotalMilliseconds;
            LastFrameTimeMs = (float)swU.Elapsed.TotalMilliseconds;
        }

        void Tick(Instance inst)
        {
            // neu erscheinender Clip wartet auf seine Shapes: Zeitleiste (samt Kindern) kurz anhalten, damit seine
            // Öffnen-Animation danach vollständig zu sehen ist (VmfPlayerAsync.cs, atomares Einblenden)
            if (inst.Owner != null && inst.Owner.FrozenUntil > Time.unscaledTime) return;
            if (inst.Playing && inst.Timeline.FrameCount > 1)
            {
                int next = inst.Frame + 1;
                if (next >= inst.Timeline.FrameCount) next = 0;
                GotoFrame(inst, next, true);
            }
            // Kinder (Kopie, da Aktionen die Liste ändern können)
            var kids = RentList(inst);
            for (int i = 0; i < kids.Count; i++) if (kids[i].Sprite != null) Tick(kids[i].Sprite);
            ReturnList(kids);
        }

        public void GotoFrame(Instance inst, int frame, bool runActions)
        {
            var tl = inst.Timeline;
            frame = Mathf.Clamp(frame, 0, Mathf.Max(0, tl.FrameCount - 1));
            if (frame == inst.Frame) return;
            int start;
            if (frame < inst.Frame || inst.Frame < 0)
            {
                // zurück: neu aufbauen (bestehende Objekte mit gleicher Tiefe/Charakter bleiben erhalten -> Sprites behalten Zustand)
                var keep = new Dictionary<int, DisplayObject>(inst.List);
                inst.List.Clear();
                start = 0;
                for (int f = 0; f <= frame; f++) ApplyFrame(inst, f, keep);
            }
            else
            {
                start = inst.Frame + 1;
                for (int f = start; f <= frame; f++) ApplyFrame(inst, f, null);
            }
            inst.Frame = frame;
            if (runActions)
                foreach (var op in tl.Frames[frame])
                    if (op.Kind == VmfMovie.OpKind.Action) QueueOrRun(inst, op.Actions);
        }

        void ApplyFrame(Instance inst, int f, Dictionary<int, DisplayObject> reuse)
        {
            foreach (var op in inst.Timeline.Frames[f])
            {
                if (op.Kind == VmfMovie.OpKind.Remove) { inst.List.Remove(op.Depth); continue; }
                if (op.Kind != VmfMovie.OpKind.Place) continue;
                inst.List.TryGetValue(op.Depth, out var d);
                DisplayObject pendingInit = null;
                if (op.HasChar && (d == null || !op.Move || d.CharId != op.CharId))
                {
                    var old = d;
                    DisplayObject nd = null; bool reused = false;
                    if (reuse != null && reuse.TryGetValue(op.Depth, out var prev) && prev.CharId == op.CharId) { nd = prev; reused = true; }
                    if (nd == null)
                    {
                        nd = new DisplayObject { Depth = op.Depth, CharId = op.CharId, Parent = inst };
                        if (Movie.Chars.TryGetValue(op.CharId, out var ch) && ch is VmfMovie.Timeline sp)
                        {
                            nd.Sprite = new Instance { Timeline = sp, Owner = nd };
                            pendingInit = nd;   // erstes Bild erst ausführen, wenn Name/Matrix gesetzt sind (Funktionen landen sonst unter "#Tiefe")
                        }
                    }
                    if (old != null && op.Move) { nd.Matrix = old.Matrix; nd.Cx = old.Cx; nd.Name = old.Name; nd.ClipDepth = old.ClipDepth; }
                    else if (!reused) { nd.Matrix = Matrix2D.Identity; nd.Cx = ColorTransform.Identity; nd.ClipDepth = 0; }
                    else
                    {
                        // wiederverwendet (Zeitleiste zurückgesprungen): nur per Skript gesetzte Lage/Farbe bleibt wie in Flash erhalten,
                        // sonst gilt wieder der Stand der Zeitleiste (sonst bleibt z.B. die Spielerliste auf 21 % einer Einblendung stehen)
                        if (!nd.ScriptMatrix) nd.Matrix = Matrix2D.Identity;
                        if (!nd.ScriptCx) nd.Cx = ColorTransform.Identity;
                        nd.ClipDepth = 0;
                    }
                    d = nd;
                    inst.List[op.Depth] = d;
                }
                if (d == null) continue;
                if (op.HasMatrix && !d.ScriptMatrix) d.Matrix = op.Matrix;
                if (op.HasCx && !d.ScriptCx) d.Cx = op.Cx;
                if (op.HasRatio) d.Ratio = op.Ratio;
                if (op.HasName) d.Name = op.Name;
                if (op.HasClip) d.ClipDepth = op.ClipDepth;
                if (pendingInit != null) GotoFrame(pendingInit.Sprite, 0, true);
            }
        }

        void RunActions(Instance self, byte[] a)
        {
            var target = self;
            int p = 0, guard = 0;
            while (p < a.Length && guard++ < 10000)
            {
                int op = a[p++];
                if (op == 0) break;
                int len = 0, q = p;
                if (op >= 0x80) { if (p + 2 > a.Length) break; len = a[p] | (a[p + 1] << 8); q = p + 2; p = q + len; }
                switch (op)
                {
                    case 0x06: if (target != null) target.Playing = true; break;                      // Play
                    case 0x07: if (target != null) target.Playing = false; break;                     // Stop
                    case 0x04: if (target != null) GotoFrame(target, target.Frame + 1, false); break;  // NextFrame
                    case 0x05: if (target != null) GotoFrame(target, target.Frame - 1, false); break;  // PrevFrame
                    case 0x81: if (target != null && len >= 2) { GotoFrame(target, a[q] | (a[q + 1] << 8), false); target.Playing = false; } break;
                    case 0x8C:                                                                          // GoToLabel
                        if (target != null)
                        {
                            string lbl = AsString(a, q, q + len);
                            if (target.Timeline.Labels.TryGetValue(lbl, out var fr)) { GotoFrame(target, fr, false); target.Playing = false; }
                        }
                        break;
                    case 0x8B:                                                                          // SetTarget
                        {
                            string t = AsString(a, q, q + len);
                            target = string.IsNullOrEmpty(t) ? self : (Find(t, self)?.Sprite ?? (t.StartsWith("/") ? null : FindRelative(self, t)));
                            break;
                        }
                }
            }
        }

        Instance FindRelative(Instance from, string path)
        {
            var inst = from;
            foreach (var part in (path.IndexOf('/') >= 0 ? path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries) : path.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries)))
            {
                if (inst == null) return null;
                if (part == "..") { inst = inst.Owner?.Parent; continue; }
                DisplayObject hit = null;
                foreach (var d in inst.List.Values) if (string.Equals(d.Name, part, StringComparison.OrdinalIgnoreCase)) { hit = d; break; }
                inst = hit?.Sprite;
            }
            return inst;
        }

        static string AsString(byte[] a, int q, int end)
        {
            int s = q; while (q < end && a[q] != 0) q++;
            var c = new char[q - s]; for (int i = 0; i < c.Length; i++) c[i] = (char)a[s + i];
            return new string(c);
        }

        readonly List<(DisplayObject d, Matrix2D m, ColorTransform c)> _dynTop = new List<(DisplayObject, Matrix2D, ColorTransform)>();
        readonly HashSet<Texture> _topTextures = new HashSet<Texture>();

        /// <summary>Called by the host for each image. Draws over <see cref="FlashHost.Renderer"/>.</summary>
        public void Render()
        {
            if (!Visible || Root == null || FlashHost.Renderer == null) return;
            var swD = Stopwatch.StartNew();
            LastDrawCalls = 0; LastShapeDraws = 0; LastTextDraws = 0; LastQuadBatches = 0;
            ApplyClipTextures();
            float sc = Screen.width / VmfMovie.StageWidth;
            float offY = (VmfMovie.StageHeight * sc - Screen.height) * 0.5f;
            var stage = new Matrix2D { A = sc, D = sc, Ty = -offY };
            var cx = ColorTransform.Identity; cx.Mul.w = Opacity;
            // Erstes Zeigen: erst zeichnen, wenn alle sichtbaren Shapes fertig gerastert sind (sonst sieht man den Aufbau)
            if (_holdUntil > 0f)
            {
                if (Time.unscaledTime < _holdUntil)
                {
                    _probe = true; _missing = false;
                    try { Draw(Root, stage, cx, null); } finally { _probe = false; }
                    if (_missing) { LastDrawTimeMs = (float)swD.Elapsed.TotalMilliseconds; return; }
                }
                _holdUntil = 0f;
            }
            _drawSerial++; _revealHolding = false;   // atomares Einblenden neuer Clips (VmfPlayerAsync.cs)
            _batchOpen = false;
            _dynTop.Clear();
            Draw(Root, stage, cx, null);
            foreach (var (dd, dm, dc) in _dynTop) if (dd.Sprite != null && dd.DynTex != null) Draw(dd.Sprite, dm, dc, null, dd.DynTex);
            _dynTop.Clear();
            FlushBatch();
            if (Highlight != null) DrawHighlight(Highlight);
            LastDrawTimeMs = (float)swD.Elapsed.TotalMilliseconds;
            try
            {
                lock (_histLock)
                {
                    LastRasterPendingBytes = _pendingBytes;
                    LastRasterQueueCount = _rasterJobs.Count;
                    LastRasterDoneCount = _rasterDone.Count;
                }
            }
            catch { }
            PushFlashMetric(LastFrameTimeMs, LastDrawTimeMs, LastTickTimeMs, LastDrawCalls);
        }

        /// <summary>Debug: Outline this object in red on the game screen (Flash Debugger).</summary>
        [NonSerialized] public DisplayObject Highlight;

        /// <summary>Screen rectangle (GUI pixels) of an object, including its contents; null if empty.</summary>
        public Rect? ScreenBounds(DisplayObject d)
        {
            if (d == null || d.Parent == null) return null;
            var r = LocalBounds(d);
            if (!r.HasValue) return null;
            var m = WorldOf(d.Parent);
            var b = r.Value; bool any = false; float x0 = 0, y0 = 0, x1 = 0, y1 = 0;
            foreach (var c in new[] { new Vector2(b.xMin, b.yMin), new Vector2(b.xMax, b.yMin), new Vector2(b.xMin, b.yMax), new Vector2(b.xMax, b.yMax) })
            {
                var q = m.Apply(c);
                if (!any) { x0 = x1 = q.x; y0 = y1 = q.y; any = true; }
                else { x0 = Mathf.Min(x0, q.x); x1 = Mathf.Max(x1, q.x); y0 = Mathf.Min(y0, q.y); y1 = Mathf.Max(y1, q.y); }
            }
            return Rect.MinMaxRect(x0, y0, x1, y1);
        }

        void DrawHighlight(DisplayObject d)
        {
            var r = ScreenBounds(d);
            var white = Texture2D.whiteTexture;
            var mul = new Vector4(1f, 0.15f, 0.1f, 0.95f);
            var keepClip = _clip; var keepMask = _maskTex;
            _clip = NoClip; _maskTex = null;
            void Box(Rect q)
            {
                BeginQuad(white, mul, Vector4.zero, 0f);
                Q(0, 1, q.xMin, q.yMin); Q(1, 1, q.xMax, q.yMin); Q(1, 0, q.xMax, q.yMax); Q(0, 0, q.xMin, q.yMax);
            }
            if (r.HasValue)
            {
                var v = r.Value; const float w = 2f;
                Box(new Rect(v.xMin, v.yMin, v.width, w));
                Box(new Rect(v.xMin, v.yMax - w, v.width, w));
                Box(new Rect(v.xMin, v.yMin, w, v.height));
                Box(new Rect(v.xMax - w, v.yMin, w, v.height));
            }
            // Ursprung (Registrierpunkt) als Kreuz
            var o = WorldOf(d.Parent).Apply(new Vector2(d.Matrix.Tx, d.Matrix.Ty));
            Box(new Rect(o.x - 6f, o.y - 1f, 12f, 2f));
            Box(new Rect(o.x - 1f, o.y - 6f, 2f, 12f));
            FlushBatch();
            _clip = keepClip; _maskTex = keepMask;
        }

        // Maske (SWF ClipDepth) als Bildschirmrechteck in GUI-Pixeln (xMin, yMin, xMax, yMax); im Shader per discard ausgewertet
        static readonly Vector4 NoClip = new Vector4(-1e7f, -1e7f, 1e7f, 1e7f);
        Vector4 _clip = NoClip;
        // Maskenform: gerasterte Shape der Maskenebene, im Shader per Alpha-Test (Bildschirmrechteck _maskRect)
        Texture _maskTex; Vector4 _maskRect;

        void Draw(Instance inst, Matrix2D m, ColorTransform cx, string dynamicClip, Texture clipTex = null)
        {
            int maskUntil = -1;
            var outerClip = _clip; var outerMask = _maskTex; var outerMaskRect = _maskRect;
            foreach (var d in inst.List.Values)
            {
                if (maskUntil >= 0 && d.Depth > maskUntil) { _clip = outerClip; _maskTex = outerMask; _maskRect = outerMaskRect; maskUntil = -1; }
                if (d.ClipDepth > 0)
                {
                    // Mask layer: Draw objects up to ClipDepth only within their shape
                    // (Rectangle as a prefilter; shape determined by an alpha test of the rasterized mask shape)
                    var mr = MaskRect(d, m);
                    if (mr.HasValue)
                    {
                        var r = mr.Value;
                        _clip = new Vector4(Mathf.Max(outerClip.x, r.x), Mathf.Max(outerClip.y, r.y), Mathf.Min(outerClip.z, r.z), Mathf.Min(outerClip.w, r.w));
                        maskUntil = d.ClipDepth;
                        SetMaskShape(d, m);
                    }
                    continue;
                }
                if (!d.Visible) continue;
                var mw = m * LocalMatrix(d);
                // Clip with in-game texture (e.g., shopscar window_buy/lure, preview camera): In the movie, the placeholder has an alpha value of 0 –
                // the game inserts its own image there. Therefore, do not apply your own color/alpha transformations to the clip (only those of the parent).
                var cw = d.DynTex != null ? cx : cx * d.Cx;
                if (cw.Mul.w <= 0.003f && cw.Add.w <= 0.003f) continue;
                if (d.CharId == -2 && d.Sprite != null) { Draw(d.Sprite, mw, cw, dynamicClip, d.DynTex ?? clipTex); continue; }   // createEmptyMovieClip
                if (!Movie.Chars.TryGetValue(d.CharId, out var ch)) continue;
                switch (ch)
                {
                    case VmfMovie.Timeline _:
                        string dyn = d.Name != null && _dynamic.ContainsKey(d.Name) ? d.Name : dynamicClip;
                        if (!_probe && !RevealReady(d, mw, cw, dyn, d.DynTex ?? clipTex)) break;   // Not yet fully rasterized
                        // Clip with in-game image (e.g., preview camera window_buy/lure): Draw over the film last—in the game,
                        // the engine inserts the image itself; otherwise, background areas at greater depths would cover it up
                        if (d.DynTex != null && !_probe && _dynTop != null && _topTextures.Contains(d.DynTex)) { _dynTop.Add((d, mw, cw)); break; }
                        Draw(d.Sprite, mw, cw, dyn, d.DynTex ?? clipTex);
                        break;
                    case VmfMovie.Shape s:
                        if (clipTex != null) DrawTexture(clipTex, s.Bounds, mw, cw, true);   // .2db-Rohdaten (oberste Zeile bei v = 0)
                        else if (dynamicClip != null && _dynamic.TryGetValue(dynamicClip, out var dt)) DrawTexture(dt, s.Bounds, mw, cw, !_dynamicUpright.Contains(dynamicClip));   // .2db-Rohdaten: oberste Zeile liegt bei v = 0
                        else DrawShape(s, mw, cw);
                        break;
                    case VmfMovie.EditText t:
                        DrawText(t, mw, cw, inst, d.TextOverride ?? CaretText(t));
                        break;
                    case VmfMovie.Button:
                        // edFlash does not draw buttons (DefineButton2)—they are merely click targets for the mouse or keys. Note: In
                        // shopscar/main/shopsavatar, the buttons have no visible states; in general.bnk, 104 out of 159 only have
                        // the placeholder colors from the Flash editor (Up 0,204,255 @57, Over 153,0,0 @80)—not visible in the game.
                        break;
                }
            }
            _clip = outerClip; _maskTex = outerMask; _maskRect = outerMaskRect;
        }

        /// <summary>Set the mask shape if the mask consists of exactly one (unrotated) shape; otherwise, use only the rectangle.</summary>
        void SetMaskShape(DisplayObject d, Matrix2D m)
        {
            var mw = m * LocalMatrix(d);
            VmfMovie.Shape sh = null;
            if (Movie.Chars.TryGetValue(d.CharId, out var ch))
            {
                if (ch is VmfMovie.Shape s0) sh = s0;
                else if (ch is VmfMovie.Timeline && d.Sprite != null && d.Sprite.List.Count == 1)
                    foreach (var c in d.Sprite.List.Values)
                        if (Movie.Chars.TryGetValue(c.CharId, out var cc) && cc is VmfMovie.Shape s1) { sh = s1; mw = mw * LocalMatrix(c); }
            }
            if (sh == null || Mathf.Abs(mw.B) > 1e-3f || Mathf.Abs(mw.C) > 1e-3f) return;
            if (sh.Morph) return;   // Morph-Maske (z.B. Füllbalken der Fahrzeugdaten): nur als Rechteck nach Ratio (MaskRect)
            var (tex, rect) = ShapeTexture(sh, mw);
            if (tex == null) return;
            var a = mw.Apply(new Vector2(rect.xMin, rect.yMin)); var b = mw.Apply(new Vector2(rect.xMax, rect.yMax));
            _maskTex = tex;
            _maskRect = new Vector4(a.x, a.y, b.x, b.y);   // Texturzeile oben (v = 1) liegt bei rect.yMin
        }

        /// <summary>Screen boundary of all shapes in a mask object (GUI pixels).
        /// Boundary of a shape; for morph shapes, between the start and end forms according to the ratio (masks for bar animations).</summary>
        static Rect MorphBounds(VmfMovie.Shape s, DisplayObject d)
        {
            if (!s.Morph || d == null) return s.Bounds;
            float t = Mathf.Clamp01(d.Ratio / 65535f);
            var a = s.Bounds; var b = s.EndBounds;
            return Rect.MinMaxRect(Mathf.Lerp(a.xMin, b.xMin, t), Mathf.Lerp(a.yMin, b.yMin, t), Mathf.Lerp(a.xMax, b.xMax, t), Mathf.Lerp(a.yMax, b.yMax, t));
        }

        Vector4? MaskRect(DisplayObject d, Matrix2D m)
        {
            var mw = m * LocalMatrix(d);
            if (!Movie.Chars.TryGetValue(d.CharId, out var ch)) return null;
            if (ch is VmfMovie.Shape sh)
            {
                var b = MorphBounds(sh, d);
                var a0 = mw.Apply(new Vector2(b.xMin, b.yMin)); var a1 = mw.Apply(new Vector2(b.xMax, b.yMin));
                var a2 = mw.Apply(new Vector2(b.xMax, b.yMax)); var a3 = mw.Apply(new Vector2(b.xMin, b.yMax));
                return new Vector4(Mathf.Min(Mathf.Min(a0.x, a1.x), Mathf.Min(a2.x, a3.x)), Mathf.Min(Mathf.Min(a0.y, a1.y), Mathf.Min(a2.y, a3.y)),
                                   Mathf.Max(Mathf.Max(a0.x, a1.x), Mathf.Max(a2.x, a3.x)), Mathf.Max(Mathf.Max(a0.y, a1.y), Mathf.Max(a2.y, a3.y)));
            }
            if (ch is VmfMovie.Timeline && d.Sprite != null)
            {
                Vector4? acc = null;
                foreach (var c in d.Sprite.List.Values)
                {
                    var r = MaskRect(c, mw);
                    if (!r.HasValue) continue;
                    acc = acc.HasValue ? new Vector4(Mathf.Min(acc.Value.x, r.Value.x), Mathf.Min(acc.Value.y, r.Value.y), Mathf.Max(acc.Value.z, r.Value.z), Mathf.Max(acc.Value.w, r.Value.w)) : r;
                }
                return acc;
            }
            return null;
        }

        void DrawShape(VmfMovie.Shape s, Matrix2D m, ColorTransform cx)
        {
            var (tex, rect) = ShapeTexture(s, m);
            if (tex == null) return;
            DrawTexture(tex, rect, m, cx, false);
        }

        /// <summary>Rasterized shape (cache by size level) and its rectangle in shape coordinates.</summary>
        (Texture2D tex, Rect rect) ShapeTexture(VmfMovie.Shape s, Matrix2D m)
        {
            float scale = Mathf.Max(0.05f, m.ScaleEstimate);
            int bucket = Mathf.RoundToInt(Mathf.Log(scale, 2f) * 2f);
            (Texture2D tex, float ox, float oy, float sc) e = default; bool found = false;
            if (_shapeBest.TryGetValue(s.Id, out int best) && best >= bucket && _shapeCache.TryGetValue((s.Id, best), out e)) found = true;
            if (!found)
            {
                if (Time.frameCount != _rasterFrame) { _rasterFrame = Time.frameCount; _rasterMs = 0; }
                bool haveSmaller = _shapeBest.TryGetValue(s.Id, out best) && _shapeCache.TryGetValue((s.Id, best), out e);
                int target = Mathf.Max(bucket + 1, StageBucket() + 1);
                bool background = AsyncRaster && (_rasterMs > RasterBudgetMs || EstimatePixels(s, target) > SyncRasterMaxPixels || NeedsUndecodedBitmap(s));
                if (background)
                {
                    RequestRaster(s.Id, target, true);
                    if (haveSmaller) { found = true; }
                    else { _missing = true; return (null, default); }
                }
                else if (haveSmaller && Time.frameCount == _rasterFrame && _rasterMs > RasterBudgetMs)
                    found = true;
                else
                {
                    FlushBatch();   // keine Texturerzeugung mitten in einem GL-Stapel
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var r = VmfRaster.Render(s, Mathf.Pow(2f, target * 0.5f), BitmapFor);
                    e = StoreShape(s.Id, target, r);
                    _rasterMs += (float)sw.Elapsed.TotalMilliseconds;
                }
            }
            if (e.tex == null) return (null, default);
            return (e.tex, new Rect(e.ox, e.oy, e.tex.width / e.sc, e.tex.height / e.sc));
        }

        bool _batchOpen; Texture _bTex, _bMaskTex; Vector4 _bMul, _bAdd, _bClip, _bMaskRect; float _bLum;
        readonly List<ObjectVertex> _bVerts = new List<ObjectVertex>(1024);

        void BeginQuad(Texture tex, Vector4 mul, Vector4 add, float lum)
        {
            if (_batchOpen && ReferenceEquals(tex, _bTex) && mul == _bMul && add == _bAdd && lum == _bLum && _clip == _bClip
                && ReferenceEquals(_maskTex, _bMaskTex) && (_maskTex == null || _maskRect == _bMaskRect)) return;
            FlushBatch();
            _bTex = tex; _bMul = mul; _bAdd = add; _bLum = lum; _bClip = _clip; _bMaskTex = _maskTex; _bMaskRect = _maskRect;
            _batchOpen = true;
        }

        /// <summary>GL.TexCoord2 + GL.Vertex3 (pixel, 0,0 top-left).</summary>
        void Q(float u, float v, float x, float y) => _bVerts.Add(new ObjectVertex { Position = new System.Numerics.Vector3(x, y, 0f), Uv0 = new System.Numerics.Vector2(u, v) });

        void FlushBatch()
        {
            if (!_batchOpen) return;
            _batchOpen = false;
            var r = FlashHost.Renderer;
            var gpu = (_bTex ?? Texture2D.whiteTexture)?.Gpu ?? Texture2D.whiteTexture.Gpu;
            if (r != null && gpu != null && _bVerts.Count >= 4)
            {
                LastQuadBatches++;
                var prm = new FlashQuadParams
                {
                    Mul = _bMul.ToNumerics(),
                    Add = _bAdd.ToNumerics(),
                    Clip = _bClip.ToNumerics(),
                    MaskRect = _bMaskRect.ToNumerics(),
                    LumAlpha = _bLum > 0.5f,
                    UnityGui = true,
                };
                r.DrawFlash(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_bVerts), gpu, _bMaskTex?.Gpu, prm);
            }
            _bVerts.Clear();
        }

        void DrawTexture(Texture tex, Rect r, Matrix2D m, ColorTransform cx, bool flipV)
        {
            if (_probe) return;
            BeginQuad(tex, cx.Mul, cx.Add, 0f);
            var p0 = m.Apply(new Vector2(r.xMin, r.yMin)); var p1 = m.Apply(new Vector2(r.xMax, r.yMin));
            var p2 = m.Apply(new Vector2(r.xMax, r.yMax)); var p3 = m.Apply(new Vector2(r.xMin, r.yMax));
            float vt = flipV ? 0f : 1f, vb = flipV ? 1f : 0f;
            Q(0, vt, p0.x, p0.y);
            Q(1, vt, p1.x, p1.y);
            Q(1, vb, p2.x, p2.y);
            Q(0, vb, p3.x, p3.y);
        }

        void DrawText(VmfMovie.EditText t, Matrix2D m, ColorTransform cx, Instance ctx, string overrideText = null)
        {
            if (_probe) return;
            LastDrawCalls++; LastTextDraws++;
            string s = overrideText;
            if (s == null && !string.IsNullOrEmpty(t.Variable))
            {
                if (!_vars.TryGetValue(ResolveVar(ctx, t.Variable), out s)) _vars.TryGetValue(t.Variable, out s);
            }
            // wie Flash: der Starttext gilt nur, solange die Variable nie gesetzt wurde ("" bleibt leer);
            // Entwickler-Platzhalter ("99", "_CAR NAME", "playerName" …) werden im Spiel immer überschrieben -> nicht zeigen
            if (s == null) { if (HidePlaceholders && IsPlaceholder(t)) return; s = t.Text; }
            if (string.IsNullOrEmpty(s)) return;
            var col = new Vector4(t.Color.r / 255f, t.Color.g / 255f, t.Color.b / 255f, t.Color.a / 255f);
            var cMul = Vector4.Scale(col, cx.Mul);
            float size = t.Height > 0 ? t.Height : 12f;

            // Original-Font (Glyphen-Textur aus der Bank)
            if (Movie.Fonts.TryGetValue(t.FontId, out var font) && font.Glyphs.Count > 0)
            {
                var tex = FontTexture(font);
                if (tex != null)
                {
                    // Zeilen, Umbruch, Farben (#color:..#), Ausrichtung je Zeile – siehe VmfText.cs
                    DrawTextLaidOut(t, font, tex, s, size, m, cMul, cx);
                    return;
                }
            }

            // Font ohne Glyphen ("font name flushed", z.B. log_in2.bnk "Berlin Sans FB"): wie im Spiel die gleichnamige Schrift
            // eines anderen geladenen Films (main.bnk enthält Berlin Sans FB mit allen Zeichen).
            var named = Movie.Fonts.TryGetValue(t.FontId, out var empty) ? empty?.Name : null;
            if (SharedFont(named, out var owner, out var shared))
            {
                var stex = owner.FontTexture(shared, false);
                if (stex != null)
                {
                    DrawTextLaidOut(t, shared, stex, s, size, m, cMul, cx);
                    return;
                }
            }
        }
        readonly HashSet<int> _sharedFontLogged = new HashSet<int>();

        static readonly Dictionary<string, (VmfPlayer owner, VmfMovie.Font font)> _fontsByName = new Dictionary<string, (VmfPlayer, VmfMovie.Font)>(StringComparer.OrdinalIgnoreCase);

        void RegisterFonts()
        {
            var list = new List<string>();
            foreach (var f in Movie.Fonts.Values)
            {
                list.Add($"{f.Id} '{f.Name}' {f.Glyphs.Count} Zeichen");
                if (string.IsNullOrEmpty(f.Name) || f.Glyphs.Count == 0) continue;
                lock (_fontsByName)
                    if (!_fontsByName.TryGetValue(f.Name, out var have) || have.font.Glyphs.Count < f.Glyphs.Count) _fontsByName[f.Name] = (this, f);
            }
        }

        void UnregisterFonts()
        {
            lock (_fontsByName)
            {
                var gone = new List<string>();
                foreach (var kv in _fontsByName) if (ReferenceEquals(kv.Value.owner, this)) gone.Add(kv.Key);
                foreach (var k in gone) _fontsByName.Remove(k);
            }
        }

        static IEnumerable<string> RegisteredFonts()
        {
            lock (_fontsByName) { var r = new List<string>(); foreach (var kv in _fontsByName) r.Add(kv.Key + "@" + Path.GetFileName(kv.Value.owner.BankPath)); return r; }
        }

        /// <summary>Font with the same name and glyphs from a loaded movie (otherwise, the first available font with glyphs).</summary>
        static bool SharedFont(string name, out VmfPlayer owner, out VmfMovie.Font font)
        {
            owner = null; font = null;
            lock (_fontsByName)
            {
                if (!string.IsNullOrEmpty(name) && _fontsByName.TryGetValue(name, out var hit)) { owner = hit.owner; font = hit.font; return true; }
            }
            return false;
        }
        readonly HashSet<int> _noFontWarned = new HashSet<int>();

        /// <summary>
        /// Ascender height (1/1024 em). For some Eden fonts, the header value is unusable (e.g., 15810)—in such cases, it is estimated based on the height of the
        /// uppercase letters (Ascent ≈ 1.27 × cap height, measured using Berlin Sans 914/720 and Lucida Sans 950/747).
        /// </summary>
        static float FontAscent(VmfMovie.Font f)
        {
            if (f.Ascent >= 500 && f.Ascent <= 1300) return f.Ascent;
            foreach (char c in "AHEIT")
                if (f.Glyphs.TryGetValue(c, out var g) && g.Top > 200 && g.Top < 1200) return g.Top * 1.27f;
            return 920f;
        }

        static float Advance(VmfMovie.Font f, char c, float size)
        {
            if (f.Advances.TryGetValue(c, out var adv) && adv > 0 && adv < 4096) return adv * size / 1024f;   // Vorschubtabelle der Schrift
            if (f.Glyphs.TryGetValue(c, out var g) && g.W > 0 && g.W < 4096) return (g.W - 2 * g.XOff) * size / 1024f;
            return size * 0.35f;   // Leerzeichen/fehlende Glyphe
        }

        readonly Dictionary<string, Texture2D> _fontTex = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        Texture2D FontTexture(VmfMovie.Font f, bool flush = true)
        {
            if (_fontTex.TryGetValue(f.TextureName, out var t)) return t;
            // Die Glyphen liegen scharf in fla_<Font-ID>; die Textur aus dem Font-Kopf (fla_<texId>) ist bei vielen Fonts nur die
            // weichgezeichnete Variante (Schein/Distanzfeld) mit gleicher Anordnung – als Schrift sieht sie wie Klötzchen aus.
            if (flush) FlushBatch();
            string own = $"fla_{f.Id:0000}";
            if (!_texDefs.TryGetValue(own, out var def) && !_texDefs.TryGetValue(f.TextureName, out def)) def = null;
            t = def != null ? Texture2D.FromUpload(def.ToUpload(TextureWrap.Clamp, TextureWrap.Clamp)) : null;
            _fontTex[f.TextureName] = t;
            return t;
        }

        void DrawGlyph(Texture tex, Rect r, VmfMovie.Glyph g, Matrix2D m, Vector4 mul, Vector4 add)
        {
            if (_probe) return;
            BeginQuad(tex, mul, add, 1f);
            var p0 = m.Apply(new Vector2(r.xMin, r.yMin)); var p1 = m.Apply(new Vector2(r.xMax, r.yMin));
            var p2 = m.Apply(new Vector2(r.xMax, r.yMax)); var p3 = m.Apply(new Vector2(r.xMin, r.yMax));
            // .2db-Rohdaten: v = 0 ist die oberste Zeile -> Glyphen-UVs direkt verwenden
            Q(g.U0, g.V0, p0.x, p0.y);
            Q(g.U1, g.V0, p1.x, p1.y);
            Q(g.U1, g.V1, p2.x, p2.y);
            Q(g.U0, g.V1, p3.x, p3.y);
        }

        (PixelImage, VmfMovie.Bitmap) BitmapFor(int id)
        {
            if (!Movie.Chars.TryGetValue(id, out var c) || !(c is VmfMovie.Bitmap b) || b.TextureName == null) return (null, null);
            PixelImage img;
            _lastBitmapUse = Time.realtimeSinceStartup;
            lock (_pixels)   // auch aus dem Raster-Thread (VmfPlayerAsync.cs)
            {
                if (!_pixels.TryGetValue(b.TextureName, out img))
                {
                    img = _texDefs.TryGetValue(b.TextureName, out var def) ? PixelImage.FromTextureDef(def) : null;
                    _pixels[b.TextureName] = img;
                }
            }
            return (img, b);
        }

        public void Dispose()
        {
            UnregisterFonts();
            StopRasterThread();
            foreach (var e in _shapeCache.Values) e.tex?.Dispose();
            foreach (var t in _fontTex.Values) t?.Dispose();
            _shapeCache.Clear();
            _shapeBest.Clear();
            _fontTex.Clear();
        }
    }
}