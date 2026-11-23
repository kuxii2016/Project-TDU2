#nullable disable
using Paradise.Core.Math;

namespace Paradise.Core.Flash
{
    /// <summary>
    /// Smooth initial display of Flash movies:
    ///  - Shapes are rasterized in a background thread (VmfRaster is purely computational, without the Unity API). Immediately after
    ///    loading, all shapes in a movie are prepared at stage resolution (“Prewarm”); the textures are then generated
    ///    piece by piece in the main thread (time budget per frame).
    ///  - If a shape is needed that isn’t ready yet: small ones immediately, large ones (or if the frame budget is exhausted
    ///    ) in the background—until then, it remains invisible for a few frames or at the existing lower resolution.
    ///    Previously, everything was rasterized synchronously in the first frame -> the game froze for up to several seconds.
    ///  - Fonts (glyph textures) are also generated one after another after loading.
    ///  - Clip textures set via path (SetClipTexture) are preserved, even if the timeline recreates the clip.
    /// </summary>
    public sealed partial class VmfPlayer
    {
        /// <summary>Render shapes in the background (no delay when first displayed).</summary>
        public static bool AsyncRaster = true;
        /// <summary>Number of parallel raster threads.</summary>
        public static int RasterThreadCount = System.Math.Max(4, (Environment.ProcessorCount / 4));
        /// <summary>Rasterize all shapes before loading.</summary>
        public static bool PrewarmShapes = true;
        /// <summary>Rasterize no more than this many MB of pixels per movie in advance.</summary>
        public static int PrewarmBudgetMB = 12;
        /// <summary>Shapes up to this number of pixels are rasterized immediately (synchronously) as needed; larger ones are rasterized in the background.</summary>
        public static int SyncRasterMaxPixels = 12000;
        /// <summary>Time budget (ms) per frame for generating final textures: visible films / all invisible ones combined.</summary>
        public static float IntegrateBudgetVisibleMs = 0.1f, IntegrateBudgetHiddenMs = 0.05f;
        const long MaxPendingBytes = 64L * 1024 * 1024;
        /// <summary>When fading in, wait at most until all visible shapes are ready, rather than showing the construction process.</summary>
        public static float FirstShowHoldSeconds = 0f;
        /// <summary>
        /// Atomic Fade-In: A clip that appears for the first time (window, page, popup) is not displayed until all of its shapes
        /// are fully rendered—it is paused for no longer than that. Its timeline remains active until the opening animation
        /// has completed. 0 = off (old style: Shapes appear individually as soon as they are ready).
        /// </summary>
        public static float RevealHoldSeconds = 0.2f;
        /// <summary>GPU uploads of finished shapes per image (number/time budget in ms): visible and invisible movies.</summary>
        public static int UploadsPerFrameVisible = 16, UploadsPerFrameHidden = 4;
        public static double UploadBudgetVisibleMs = 2.0, UploadBudgetHiddenMs = 0.5;
        bool _probe, _missing, _wasVisible;
        float _holdUntil;
        int _drawSerial;
        bool _revealHolding;

        readonly Queue<(int id, int target, int gen, VmfRaster.Result r)> _uploadQueue = new Queue<(int id, int target, int gen, VmfRaster.Result r)>();

        // Profiler-Graph "Flash" (FlashHost): je Bild hochgeladene Shapes, zurückgehaltene Clips, längste Wartezeit bis zum Einblenden
        internal int ProfUploads, ProfHeld;
        internal float ProfRevealWaitMs;

        /// <summary>Profiler: wartende Raster-Aufträge und fertig gerasterte, noch nicht hochgeladene Shapes.</summary>
        internal (int jobs, int ready) RasterBacklog()
        {
            lock (_rasterLock) return (_rasterJobs.Count, _rasterDone.Count + _uploadQueue.Count);
        }

        public void UploadTick()
        {
            if (_uploadQueue.Count == 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int done = 0;
            // Engine: mehrere Uploads je Bild (früher 1 -> hud.bnk/Minimap brauchte Sekunden, bis alle Shapes da waren)
            double ms = Visible ? UploadBudgetVisibleMs : UploadBudgetHiddenMs;
            int max = Visible ? UploadsPerFrameVisible : UploadsPerFrameHidden;
            while (sw.Elapsed.TotalMilliseconds < ms && done < max && _uploadQueue.Count > 0)
            {
                var e = _uploadQueue.Dequeue();
                if (e.gen != _rasterGen) continue;
                if (_shapeBest.TryGetValue(e.id, out int best) && best >= e.target && _shapeCache.ContainsKey((e.id, best))) continue;
                try { StoreShape(e.id, e.target, e.r); } catch { }
                done++;
            }
            ProfUploads += done;
        }

        /// <summary>
        /// Should the clip be drawn? When it (re)appears, check once to see if all shapes beneath it are present in full resolution
        /// (missing shapes are requested as a priority); if not, skip the clip and pause its timeline.
        /// </summary>
        bool RevealReady(DisplayObject d, Matrix2D mw, ColorTransform cw, string dyn, Texture clipTex)
        {
            if (RevealHoldSeconds <= 0f || d.Sprite == null) return true;
            float now = Time.unscaledTime;
            bool appearing = d.SeenDraw != _drawSerial && d.SeenDraw != _drawSerial - 1;   // im letzten Zeichnen nicht dabei
            d.SeenDraw = _drawSerial;
            if (appearing) { d.Revealed = false; d.RevealSince = now; d.HeldOnce = false; }
            if (d.Revealed) return true;
            // Prüfdurchgang ohne Zeichnen (rastert kleine Shapes sofort, große im Hintergrund)
            var clip = _clip; var mask = _maskTex; var maskRect = _maskRect;
            bool wasMissing = _missing;
            _probe = true; _missing = false;
            try { Draw(d.Sprite, mw, cw, dyn, clipTex); }
            finally { _probe = false; }
            bool missing = _missing;
            _missing = wasMissing; _clip = clip; _maskTex = mask; _maskRect = maskRect;
            if (!missing || now - d.RevealSince >= RevealHoldSeconds)
            {
                if (d.HeldOnce) { ProfRevealWaitMs = System.Math.Max(ProfRevealWaitMs, (now - d.RevealSince) * 1000f); d.HeldOnce = false; }
                d.Revealed = true; d.FrozenUntil = 0f;
                return true;
            }
            d.FrozenUntil = now + 0.15f;   // verlängert sich, solange der Clip gezeichnet werden will
            _revealHolding = true;
            d.HeldOnce = true;
            ProfHeld++;
            return false;
        }

        readonly object _rasterLock = new object();
        readonly LinkedList<(int id, int target, int gen)> _rasterJobs = new LinkedList<(int, int, int)>();
        readonly HashSet<long> _rasterQueued = new HashSet<long>();
        readonly Queue<(int id, int target, int gen, VmfRaster.Result r)> _rasterDone = new Queue<(int, int, int, VmfRaster.Result)>();
        readonly AutoResetEvent _rasterSignal = new AutoResetEvent(false);
        Thread[] _rasterThreads;
        volatile bool _rasterStop;
        long _pendingBytes;
        int _rasterGen;
        VmfMovie _rasterMovie;
        List<VmfMovie.Font> _fontQueue;

        static int _hiddenFrame = -1; static float _hiddenMs;

        volatile float _lastBitmapUse;
        /// <summary>Discard decoded atlas pages (RGBA, 4 bytes per pixel) after this many seconds without rasterization—redecode if necessary.</summary>
        public static float DecodedBitmapKeepSeconds = 10f;

        /// <summary>Release decoded bitmaps (used only for rasterizing bitmap fills) when nothing else is being rasterized.</summary>
        void TrimDecodedBitmaps()
        {
            if (Time.realtimeSinceStartup - _lastBitmapUse < DecodedBitmapKeepSeconds) return;
            lock (_rasterLock) if (_rasterJobs.Count > 0) return;
            lock (_pixels) { if (_pixels.Count > 0) _pixels.Clear(); }
        }

        /// <summary>Memory used by this movie (diagnostics): Raw texture data, decoded bitmaps, rasterized shapes (GPU), pending rasterizations.</summary>
        public (long raw, long decoded, int shapes, long shapeGpu, long pending) MemoryStats()
        {
            long raw = 0, dec = 0, sg = 0;
            foreach (var d in _texDefs.Values) raw += d.Data?.Length ?? 0;
            lock (_pixels) foreach (var p in _pixels.Values) dec += p?.Pixels != null ? p.Pixels.LongLength * 4 : 0;
            foreach (var e in _shapeCache.Values) if (e.tex != null) sg += (long)e.tex.width * e.tex.height * 4;
            long pend; lock (_rasterLock) pend = _pendingBytes;
            return (raw, dec, _shapeCache.Count, sg, pend);
        }

        static long Key(int id, int target) => ((long)id << 16) | (long)(target + 1000);

        /// <summary>Estimated number of pixels in a shape at resolution level target.</summary>
        static long EstimatePixels(VmfMovie.Shape s, int target)
        {
            float sc = Mathf.Pow(2f, target * 0.5f);
            long px = (long)(Mathf.Max(1f, s.Bounds.width * sc + 2f) * Mathf.Max(1f, s.Bounds.height * sc + 2f));
            return System.Math.Min(px, 2_500_000L);
        }

        /// <summary>Shape with a bitmap fill whose atlas page has not yet been decoded (decoding a 2048-page atlas takes a noticeable amount of time).</summary>
        bool NeedsUndecodedBitmap(VmfMovie.Shape s)
        {
            foreach (var fs in s.Fills)
            {
                if (fs.Type < 0x40) continue;
                if (!Movie.Chars.TryGetValue(fs.BitmapId, out var c) || !(c is VmfMovie.Bitmap b) || b.TextureName == null) continue;
                lock (_pixels) if (!_pixels.ContainsKey(b.TextureName)) return true;
            }
            return false;
        }

        void StopRasterThread()
        {
            _rasterStop = true;
            _rasterSignal.Set();
            lock (_rasterLock)
            {
                _rasterJobs.Clear(); _rasterQueued.Clear(); _rasterDone.Clear(); _pendingBytes = 0;
                _rasterGen++;
            }
            if (_rasterThreads != null)
            {
                foreach (var t in _rasterThreads) { try { t.Join(50); } catch { } }
            }
            _rasterThreads = null;
            _fontQueue = null;
        }

        void StartRasterThread()
        {
            if (!AsyncRaster || (_rasterThreads != null && _rasterThreads.Length > 0)) return;
            _rasterStop = false;
            _rasterMovie = Movie;
            int gen; lock (_rasterLock) gen = _rasterGen;
            int count = RasterThreadCount;
            if (count < 1) count = 1;
            _rasterThreads = new Thread[count];
            for (int i = 0; i < count; i++)
            {
                var t = new Thread(() => RasterLoop(gen)) { IsBackground = true, Priority = System.Threading.ThreadPriority.Normal, Name = "VMF Raster " + System.IO.Path.GetFileNameWithoutExtension(BankPath ?? "") + " " + i };
                _rasterThreads[i] = t;
                t.Start();
            }
        }

        /// <summary>After loading: prepare all shapes (up to the budget) and fonts.</summary>
        void StartPrewarm()
        {
            if (!AsyncRaster || Movie == null) return;
            StartRasterThread();
            if (PrewarmShapes)
            {
                int target = StageBucket() + 1;
                long budget = PrewarmBudgetMB * 1024L * 1024L / 4L, sum = 0;
                // Small shapes first (frames, icons, buttons—there are hundreds of them): with the same budget, so many
                // more windows are immediately complete; the few large areas are quickly added when the window is displayed
                var list = new List<(VmfMovie.Shape s, long px)>();
                foreach (var c in Movie.Chars.Values)
                    if (c is VmfMovie.Shape s && s.Paths.Count > 0) list.Add((s, EstimatePixels(s, target)));
                list.Sort((a, b) => a.px.CompareTo(b.px));
                foreach (var (s, px) in list)
                {
                    if (sum + px > budget) break;
                    sum += px;
                    RequestRaster(s.Id, target, false);
                }
            }
            _fontQueue = new List<VmfMovie.Font>();
            foreach (var f in Movie.Fonts.Values) if (f.Glyphs.Count > 0) _fontQueue.Add(f);
        }

        void RequestRaster(int id, int target, bool urgent)
        {
            if (_rasterThreads == null || _rasterThreads.Length == 0) StartRasterThread();
            if (_rasterThreads == null || _rasterThreads.Length == 0) return;
            lock (_rasterLock)
            {
                long k = Key(id, target);
                if (_rasterQueued.Contains(k))
                {
                    if (!urgent) return;
                    // nach vorne holen
                    for (var node = _rasterJobs.First; node != null; node = node.Next)
                        if (node.Value.id == id && node.Value.target == target) { _rasterJobs.Remove(node); _rasterJobs.AddFirst(node); break; }
                    return;
                }
                _rasterQueued.Add(k);
                var job = (id, target, _rasterGen);
                if (urgent) _rasterJobs.AddFirst(job); else _rasterJobs.AddLast(job);
            }
            _rasterSignal.Set();
        }

        void RasterLoop(int gen)
        {
            var movie = _rasterMovie;
            while (!_rasterStop)
            {
                (int id, int target, int gen) job = default; bool have = false;
                lock (_rasterLock)
                {
                    if (gen != _rasterGen) return;
                    if (_rasterJobs.Count > 0 && _pendingBytes < MaxPendingBytes) { job = _rasterJobs.First.Value; _rasterJobs.RemoveFirst(); have = true; }
                }
                if (!have)
                {
                    VmfRaster.ReleaseThreadBuffers();   // ohne Arbeit keine Arbeitspuffer festhalten
                    _rasterSignal.WaitOne(250);
                    continue;
                }
                VmfRaster.Result r = null;
                try
                {
                    if (movie != null && movie.Chars.TryGetValue(job.id, out var ch) && ch is VmfMovie.Shape s)
                        r = VmfRaster.Render(s, Mathf.Pow(2f, job.target * 0.5f), BitmapFor);
                }
                catch (Exception) { r = null; }
                lock (_rasterLock)
                {
                    if (job.gen != _rasterGen) continue;
                    _rasterDone.Enqueue((job.id, job.target, job.gen, r));
                    if (r != null && r.Pixels != null) _pendingBytes += r.Pixels.Length * 4L;
                }
            }
        }

        /// <summary>Apply finished rasterizations as textures (main thread, with time budget).</summary>
        void IntegrateRasters()
        {
            if ((_rasterThreads == null || _rasterThreads.Length == 0) && _fontQueue == null) return;
            bool visible = Visible;
            float budget;
            if (visible) budget = _holdUntil > 0f || _revealHolding ? 2.0f : 1.0f;
            else
            {
                if (_hiddenFrame != Time.frameCount) { _hiddenFrame = Time.frameCount; _hiddenMs = 0f; }
                budget = System.Math.Max(IntegrateBudgetHiddenMs, 0.5f) - _hiddenMs;
                if (budget <= 0f) return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int integrated = 0;
            int maxIntegrate = visible ? UploadsPerFrameVisible : UploadsPerFrameHidden;
            while (sw.Elapsed.TotalMilliseconds < budget && integrated < maxIntegrate)
            {
                (int id, int target, int gen, VmfRaster.Result r) e;
                lock (_rasterLock)
                {
                    if (_rasterDone.Count == 0) break;
                    e = _rasterDone.Dequeue();
                    _rasterQueued.Remove(Key(e.id, e.target));
                    if (e.r != null && e.r.Pixels != null) _pendingBytes -= e.r.Pixels.Length * 4L;
                }
                if (e.gen != _rasterGen) continue;
                if (_shapeBest.TryGetValue(e.id, out int best) && best >= e.target && _shapeCache.ContainsKey((e.id, best))) continue;
                _uploadQueue.Enqueue(e);
                integrated++;
            }
            if (_fontQueue != null && _fontQueue.Count > 0 && sw.Elapsed.TotalMilliseconds < budget && integrated == 0)
            {
                var f = _fontQueue[_fontQueue.Count - 1];
                _fontQueue.RemoveAt(_fontQueue.Count - 1);
                try { FontTexture(f); } catch (Exception) { }
                if (_fontQueue.Count == 0) _fontQueue = null;
            }
            if (!visible) _hiddenMs += (float)sw.Elapsed.TotalMilliseconds;
            try
            {
                lock (_rasterLock)
                {
                    LastRasterPendingBytes = _pendingBytes;
                    LastRasterQueueCount = _rasterJobs.Count;
                    LastRasterDoneCount = _rasterDone.Count;
                }
            }
            catch { }
        }

        /// <summary>Save the rasterization as a texture (replaces a smaller step).</summary>
        (Texture2D tex, float ox, float oy, float sc) StoreShape(int id, int target, VmfRaster.Result r)
        {
            Texture2D tex = null;
            if (r != null && r.Pixels != null)
            {
                tex = new Texture2D(r.Width, r.Height) { wrapMode = TextureWrapMode.Clamp };
                tex.SetPixels32(r.Pixels);
                tex.Apply();
            }
            var e = (tex, r?.OriginX ?? 0, r?.OriginY ?? 0, r?.Scale ?? 1);
            if (_shapeBest.TryGetValue(id, out int old) && _shapeCache.TryGetValue((id, old), out var oe))
            {
                oe.tex?.Dispose();
                _shapeCache.Remove((id, old));
            }
            _shapeCache[(id, target)] = e;
            _shapeBest[id] = target;
            return e;
        }

        readonly Dictionary<string, Texture> _clipTex = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Reattach textures set via path to the clips (which may have been recreated by the timeline).</summary>
        void ApplyClipTextures()
        {
            if (_clipTex.Count == 0 || Root == null) return;
            foreach (var kv in _clipTex)
            {
                var d = Find(kv.Key);
                if (d != null && !ReferenceEquals(d.DynTex, kv.Value)) d.DynTex = kv.Value;
            }
        }

        void ForgetClipTextures(string prefix)
        {
            if (_clipTex.Count == 0 || string.IsNullOrEmpty(prefix)) return;
            List<string> rm = null;
            foreach (var k in _clipTex.Keys)
                if (k.Equals(prefix, StringComparison.OrdinalIgnoreCase) || k.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) (rm ?? (rm = new List<string>())).Add(k);
            if (rm != null) foreach (var k in rm) _clipTex.Remove(k);
        }

        public bool HidePlaceholders = true;
        readonly Dictionary<int, bool> _placeholder = new Dictionary<int, bool>();

        bool IsPlaceholder(VmfMovie.EditText t)
        {
            if (_placeholder.TryGetValue(t.Id, out bool p)) return p;
            p = t.KeyText || IsPlaceholderText(t.Text, t.Variable);
            _placeholder[t.Id] = p;
            return p;
        }

        /// <summary>“Starttext” is a placeholder created by the developers (it is always overwritten in the game).</summary>
        public static bool IsPlaceholderText(string text, string variable)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string s = text.Trim();
            if (s.Length == 0) return false;
            if (s[0] == '_') return true;                                     // "_CAR NAME", "_root.SpotName", "_Rival : …"
            if (s.Equals("xx", StringComparison.OrdinalIgnoreCase) || s == "TITLE" || s == "Txt" || s == "title_param") return true;
            // Schlüssel-artig ("Rules_", "/:m_VehiclesBought_") oder gleich dem Variablennamen ("playerName", "item_name")
            if (s.EndsWith("_") && s.IndexOf(' ') < 0) return true;
            if (!string.IsNullOrEmpty(variable))
            {
                string v = variable;
                int c = v.LastIndexOfAny(new[] { ':', '.', '/' });
                string leaf = c >= 0 ? v.Substring(c + 1) : v;
                if (s.Equals(v, StringComparison.OrdinalIgnoreCase) || (leaf.Length > 2 && s.Equals(leaf, StringComparison.OrdinalIgnoreCase))) return true;
            }
            // Ziffern-/Breiten-Platzhalter: "99", "$ 9 999 999", "999.99 miles", "9999 Pkte.", "99/99", "WWWWWW"
            int nines = 0, other = 0;
            string rest = s;
            foreach (var unit in new[] { "miles", "Pkte.", "Pkte", "pts", "km/h", "Player(s)", "Session(s)", "$", "%" })
                rest = rest.Replace(unit, " ");
            foreach (char ch in rest)
            {
                if (ch == '9' || ch == 'W') nines++;
                else if (ch == ' ' || ch == '.' || ch == ',' || ch == '/' || ch == ':' || ch == '-' || ch == '+' || ch == '\r' || ch == '\n') { }
                else other++;
            }
            return other == 0 && nines >= 2;
        }
    }
}