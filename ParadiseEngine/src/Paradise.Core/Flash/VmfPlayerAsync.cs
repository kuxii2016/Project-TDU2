#nullable disable
// Portiert aus dem Unity-Projekt (Runtime/Flash/VmfPlayerAsync.cs). Unity-Typen: siehe Flash/FlashCompat.cs.
using Paradise.Core.Math;

namespace Paradise.Core.Flash
{
    /// <summary>
    /// Ruckelfreies erstes Anzeigen der Flash-Filme:
    ///  - Shapes werden in einem Hintergrund-Thread gerastert (VmfRaster ist reine Rechnerei, ohne Unity-API). Gleich nach dem
    ///    Laden werden alle Shapes eines Films in der Bühnen-Auflösung vorbereitet ("Prewarm"), die Texturen entstehen danach
    ///    stückweise im Hauptthread (Zeitbudget je Bild).
    ///  - Wird eine Shape gebraucht, die noch nicht fertig ist: kleine sofort, große (oder wenn das Bild-Budget aufgebraucht
    ///    ist) im Hintergrund – bis dahin bleibt sie für wenige Bilder unsichtbar bzw. in der vorhandenen kleineren Stufe.
    ///    Vorher wurde alles im ersten Bild synchron gerastert -> das Spiel stand bis zu mehrere Sekunden.
    ///  - Schriften (Glyphen-Texturen) werden nach dem Laden ebenfalls nacheinander erzeugt.
    ///  - Per Pfad gesetzte Clip-Texturen (SetClipTexture) bleiben erhalten, auch wenn die Zeitleiste den Clip neu anlegt.
    /// </summary>
    public sealed partial class VmfPlayer
    {
        /// <summary>Shapes im Hintergrund rastern (kein Stillstand beim ersten Anzeigen).</summary>
        public static bool AsyncRaster = true;
        /// <summary>Anzahl paralleler Raster-Threads.</summary>
        public static int RasterThreadCount = System.Math.Max(4, Environment.ProcessorCount);
        /// <summary>Nach dem Laden alle Shapes vorab rastern.</summary>
        public static bool PrewarmShapes = true;
        /// <summary>Höchstens so viele MB Pixel je Film vorab rastern.</summary>
        public static int PrewarmBudgetMB = 12;
        /// <summary>Shapes bis zu dieser Pixelzahl werden bei Bedarf sofort (synchron) gerastert, größere im Hintergrund.</summary>
        public static int SyncRasterMaxPixels = 12000;
        /// <summary>Zeitbudget (ms) je Bild für das Erzeugen fertiger Texturen: sichtbare Filme / alle unsichtbaren zusammen.</summary>
        public static float IntegrateBudgetVisibleMs = 0.1f, IntegrateBudgetHiddenMs = 0.05f;
        const long MaxPendingBytes = 64L * 1024 * 1024;
        /// <summary>Beim Einblenden höchstens so lange (s) warten, bis alle sichtbaren Shapes fertig sind, statt den Aufbau zu zeigen.</summary>
        public static float FirstShowHoldSeconds = 0f;
        /// <summary>
        /// Atomares Einblenden: ein Clip, der neu erscheint (Fenster, Seite, Popup), wird erst gezeigt, wenn alle seine Shapes
        /// scharf gerastert sind – höchstens so lange (s) angehalten. Seine Zeitleiste steht so lange, die Öffnen-Animation
        /// läuft danach vollständig ab. 0 = aus (alter Stil: Shapes erscheinen einzeln, sobald sie fertig sind).
        /// </summary>
        public static float RevealHoldSeconds = 0.2f;
        /// <summary>GPU-Uploads fertiger Shapes je Bild (Anzahl/Zeitbudget ms): sichtbare bzw. unsichtbare Filme.</summary>
        public static int UploadsPerFrameVisible = 16, UploadsPerFrameHidden = 4;
        public static double UploadBudgetVisibleMs = 2.0, UploadBudgetHiddenMs = 0.5;
        bool _probe, _missing, _wasVisible;
        float _holdUntil;
        int _drawSerial;
        bool _revealHolding;

        readonly Queue<(int id, int target, int gen, VmfRaster.Result r)> _uploadQueue = new Queue<(int id, int target, int gen, VmfRaster.Result r)>();
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
        }

        /// <summary>
        /// Darf der Clip gezeichnet werden? Beim (Wieder-)Erscheinen einmal prüfen, ob alle Shapes darunter in voller Schärfe
        /// vorliegen (fehlende werden dabei vorrangig angefordert); solange nicht, Clip auslassen und seine Zeitleiste anhalten.
        /// </summary>
        bool RevealReady(DisplayObject d, Matrix2D mw, ColorTransform cw, string dyn, Texture clipTex)
        {
            if (RevealHoldSeconds <= 0f || d.Sprite == null) return true;
            float now = Time.unscaledTime;
            bool appearing = d.SeenDraw != _drawSerial && d.SeenDraw != _drawSerial - 1;   // im letzten Zeichnen nicht dabei
            d.SeenDraw = _drawSerial;
            if (appearing) { d.Revealed = false; d.RevealSince = now; }
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
                d.Revealed = true; d.FrozenUntil = 0f;
                return true;
            }
            d.FrozenUntil = now + 0.15f;   // verlängert sich, solange der Clip gezeichnet werden will
            _revealHolding = true;
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
        /// <summary>Dekodierte Atlas-Seiten (RGBA, 4 Byte je Pixel) nach so vielen Sekunden ohne Rastern verwerfen – bei Bedarf neu dekodiert.</summary>
        public static float DecodedBitmapKeepSeconds = 10f;

        /// <summary>Dekodierte Bitmaps (nur fürs Rastern von Bitmapfüllungen gebraucht) freigeben, wenn nichts mehr gerastert wird.</summary>
        void TrimDecodedBitmaps()
        {
            if (Time.realtimeSinceStartup - _lastBitmapUse < DecodedBitmapKeepSeconds) return;
            lock (_rasterLock) if (_rasterJobs.Count > 0) return;
            lock (_pixels) { if (_pixels.Count > 0) _pixels.Clear(); }
        }

        /// <summary>Speicher dieses Films (Diagnose): Rohdaten der Texturen, dekodierte Bitmaps, gerasterte Shapes (GPU), wartende Rasterungen.</summary>
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

        /// <summary>Geschätzte Pixelzahl einer Shape in Rasterstufe target.</summary>
        static long EstimatePixels(VmfMovie.Shape s, int target)
        {
            float sc = Mathf.Pow(2f, target * 0.5f);
            long px = (long)(Mathf.Max(1f, s.Bounds.width * sc + 2f) * Mathf.Max(1f, s.Bounds.height * sc + 2f));
            return System.Math.Min(px, 2_500_000L);
        }

        /// <summary>Shape mit Bitmapfüllung, deren Atlas-Seite noch nicht dekodiert ist (Dekodieren einer 2048er Seite dauert spürbar).</summary>
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

        /// <summary>Nach dem Laden: alle Shapes (bis zum Budget) und Schriften vorbereiten.</summary>
        void StartPrewarm()
        {
            if (!AsyncRaster || Movie == null) return;
            StartRasterThread();
            if (PrewarmShapes)
            {
                int target = StageBucket() + 1;
                long budget = PrewarmBudgetMB * 1024L * 1024L / 4L, sum = 0;
                // kleine Shapes zuerst (Rahmen, Symbole, Knöpfe – davon gibt es Hunderte): mit demselben Budget sind so viel
                // mehr Fenster sofort vollständig; die wenigen großen Flächen kommen beim Einblenden schnell nach
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

        /// <summary>Fertige Rasterungen als Texturen übernehmen (Hauptthread, mit Zeitbudget).</summary>
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

        /// <summary>Rasterung als Textur ablegen (ersetzt eine kleinere Stufe).</summary>
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

        // ---------------------------------------------------------------- Clip-Texturen per Pfad
        readonly Dictionary<string, Texture> _clipTex = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per Pfad gesetzte Texturen wieder an die (evtl. von der Zeitleiste neu angelegten) Clips hängen.</summary>
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

        // ---------------------------------------------------------------- Platzhaltertexte
        public bool HidePlaceholders = true;
        readonly Dictionary<int, bool> _placeholder = new Dictionary<int, bool>();

        bool IsPlaceholder(VmfMovie.EditText t)
        {
            if (_placeholder.TryGetValue(t.Id, out bool p)) return p;
            p = t.KeyText || IsPlaceholderText(t.Text, t.Variable);
            _placeholder[t.Id] = p;
            return p;
        }

        /// <summary>Starttext ist ein Platzhalter der Entwickler (wird im Spiel immer überschrieben).</summary>
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