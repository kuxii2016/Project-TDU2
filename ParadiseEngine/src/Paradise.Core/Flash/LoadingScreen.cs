#nullable disable
using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.Render;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Paradise.Core.Flash
{
    /// <summary>
    /// Loading screen as seen in the game during scene transitions (entering/exiting interiors, changing rooms, changing islands, teleporting):
    ///   * Image from frontend/tips_&lt;language&gt;.bnk (slideshow.xml: Tips_Progress, Tips_titles, car_category) – one per loading process
    ///     chosen at random; the image does not advance.
    ///   * Data is saved when the image appears (TDU2Session.SaveAsync); the save icon (general.bnk saving_logo) is superimposed over the image.
    ///   * It is not hidden until the scene is complete (caller’s condition), saving is complete, and a minimum amount of time
    ///     has elapsed—the setup is not visible.
    ///
    /// Legal slideshow at game start (ShowLegalScreen): the three &lt;IMAGE&gt; entries of frontend/legal_&lt;language&gt;.bnk/slideshow.xml
    /// are shown in that exact order, each for its "duration" seconds; afterwards onDone starts the intro videos.
    /// </summary>
    public sealed class LoadingScreen : FlashHost.IGuiLayer
    {
        /// <summary>Maximum duration if a condition is never met (seconds).</summary>
        public static float MaxSeconds = 20f;
        /// <summary>general.bnk (save icon above the loading screen); set by the game.</summary>
        public static VmfPlayer General;
        /// <summary>World around the camera (Radius) – set by the game (Streamer + Camera).</summary>
        public static Func<float, bool> WorldReadyCheck;

        sealed class LegalSlide
        {
            public string Name;
            public float Duration;
            public float MinDuration;
            public byte[] Rgba;
            public int W, H;
            public IGpuTexture Texture;
        }

        static LoadingScreen _inst;
        IGpuTexture _tex, _black;
        Task<(byte[] rgba, int w, int h, string name)?> _decode;
        Task<List<LegalSlide>> _legalDecode;
        readonly List<Func<bool>> _ready = new List<Func<bool>>();
        float _shownAt, _minUntil, _alpha;
        bool _closing;
        bool _legal;
        List<LegalSlide> _slides;
        int _slideIndex;
        float _slideUntil;
        Action _legalDone;

        public int GuiDepth => -2000;

        /// <summary>The loading screen is (still) visible.</summary>
        public static bool Visible => _inst != null && !_inst._closing;
        /// <summary>An original image has been loaded (otherwise, only black).</summary>
        public static bool HasImage => _inst != null && _inst._tex != null;

        /// <summary>
        /// Display the loading screen (if the game is already running, this condition applies). <paramref name="ready"/> = Scene complete;
        /// <paramref name="save"/> = Save game now (with the save icon).
        /// </summary>
        public static void Show(string reason, Func<bool> ready, float minSeconds = 1.2f, bool save = true)
        {
            float now = Time.unscaledTime;
            if (_inst == null || _inst._closing)
            {
                _inst?.Destroy();
                _inst = new LoadingScreen { _shownAt = now, _alpha = 1f };   // sofort deckend
                _inst._decode = StartDecode();
                FlashHost.AddLayer(_inst);
                Log.Info("Loadingscreen why?: " + reason);
            }
            var s = _inst;
            if (ready != null) s._ready.Add(ready);
            s._minUntil = MathF.Max(s._minUntil, now + minSeconds);
            if (save && TDU2Session.LoggedIn && !TDU2Session.Saving)
            {
                try
                {
                    TDU2Session.SaveAsync();
                }
                catch (Exception ex)
                {
                    Log.Error("Loading screen: Save:" + ex.Message);
                }
            }
        }

        /// <summary>
        /// Startup legal slideshow: shows the three images from legal_&lt;lang&gt;.bnk/slideshow.xml in XML order,
        /// each for its "duration" seconds. <paramref name="onDone"/> is called once the last image faded out.
        /// </summary>
        public static void ShowLegalScreen(string reason, Action onDone, float minSeconds = 1f)
        {
            float now = Time.unscaledTime;
            if (_inst != null && !_inst._closing) return;   // schon aktiv: keine zweite Slideshow
            _inst?.Destroy();
            _inst = new LoadingScreen { _shownAt = now, _alpha = 1f, _legal = true, _legalDone = onDone };
            _inst._legalDecode = StartDecodeLegalSlides();
            _inst._minUntil = now + minSeconds;
            FlashHost.AddLayer(_inst);
            Log.Info("Legal screen: " + reason);
        }

        /// <summary>
        /// Liest slideshow.xml aus legal_&lt;lang&gt;.bnk und dekodiert alle &lt;IMAGE&gt; in der dortigen Reihenfolge
        /// auf einem MTA-Hintergrundthread (der Spielthread wartet nie darauf). Fallback: alle JPG/PNG der Bank.
        /// </summary>
        static Task<List<LegalSlide>> StartDecodeLegalSlides()
        {
            var tcs = new TaskCompletionSource<List<LegalSlide>>(TaskCreationOptions.RunContinuationsAsynchronously);
            string dir = FlashHost.BnkDir, lang = FlashHost.Language;
            if (FlashHost.Renderer == null || string.IsNullOrEmpty(dir) || !OperatingSystem.IsWindows()) { tcs.SetResult(null); return tcs.Task; }
            var th = new Thread(() =>
            {
                try
                {
                    string path = null;
                    foreach (var l in new[] { lang, "us", "ge" })
                    {
                        if (string.IsNullOrEmpty(l)) continue;
                        var p = FlashHost.FindFile(Path.Combine(dir, "frontend"), "legal_" + l + ".bnk");
                        if (p != null) { path = p; break; }
                    }
                    if (path == null) { Log.Warn("Legal screen: keine legal_<lang>.bnk gefunden."); tcs.SetResult(null); return; }
                    var bank = BnkArchive.Open(path);
                    var slides = new List<LegalSlide>();
                    var xml = bank.Find("slideshow.xml");
                    if (xml != null)
                    {
                        string text = System.Text.Encoding.Latin1.GetString(bank.Read(xml));
                        foreach (Match m in Regex.Matches(text, @"<IMAGE\b[^>]*>", RegexOptions.IgnoreCase))
                        {
                            string tag = m.Value;
                            string name = Attr(tag, "name");
                            float dur = AttrFloat(tag, "duration", 5f);
                            float minDur = AttrFloat(tag, "minDuration", dur);
                            if (string.IsNullOrEmpty(name)) continue;
                            var e = bank.Find(name);
                            if (e == null) { Log.Warn("Legal screen: Bild fehlt in " + path + ": " + name); continue; }
                            var rgba = WicImage.Decode(bank.Read(e), out int w, out int h);
                            if (rgba == null) { Log.Warn("Legal screen: Bild nicht lesbar: " + name); continue; }
                            slides.Add(new LegalSlide { Name = name, Duration = dur, MinDuration = minDur, Rgba = rgba, W = w, H = h });
                        }
                    }
                    if (slides.Count == 0)
                    {
                        foreach (var e in bank.Entries)
                        {
                            if (e.Extension != ".jpg" && e.Extension != ".png") continue;
                            var rgba = WicImage.Decode(bank.Read(e), out int w, out int h);
                            if (rgba == null) continue;
                            slides.Add(new LegalSlide { Name = e.Name, Duration = 5f, MinDuration = 5f, Rgba = rgba, W = w, H = h });
                        }
                    }
                    tcs.SetResult(slides.Count > 0 ? slides : null);
                }
                catch (Exception ex)
                {
                    Log.Error("Legal screen: " + ex.Message);
                    tcs.TrySetResult(null);
                }
            })
            {
                IsBackground = true,
                Name = "LegalSlides"
            };
            th.SetApartmentState(ApartmentState.MTA);
            th.Start();
            return tcs.Task;
        }

        static string Attr(string tag, string name)
        {
            var m = Regex.Match(tag, @"\b" + Regex.Escape(name) + @"\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(tag, @"\b" + Regex.Escape(name) + @"\s*=\s*'([^']*)'", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : "";
        }

        static float AttrFloat(string tag, string name, float fallback)
        {
            string v = Attr(tag, name);
            return float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : fallback;
        }

        /// <summary>The world around the camera is ready—a prerequisite for fading out after leaving an interior or switching islands.</summary>
        public static bool WorldReady(float radius = 350f) => WorldReadyCheck == null || WorldReadyCheck(radius);

        /// <summary>Sofort ausblenden (z.B. Abbruch).</summary>
        public static void Hide() { if (_inst != null) _inst._closing = true; }

        /// <summary>
        /// Read a random image from frontend/tips_&lt;language&gt;.bnk (fallback: tips_us, tips_ge) on a separate MTA thread and
        /// decode it using WIC—the game never waits for this (s96: Decoding on the game thread caused a hang upon entering). Until the
        /// image is ready, the loading screen is black; the texture is created by <see cref="Update"/> on the game thread.
        /// </summary>
        static Task<(byte[] rgba, int w, int h, string name)?> StartDecode()
        {
            var tcs = new TaskCompletionSource<(byte[] rgba, int w, int h, string name)?>(TaskCreationOptions.RunContinuationsAsynchronously);
            string dir = FlashHost.BnkDir, lang = FlashHost.Language;
            if (FlashHost.Renderer == null || string.IsNullOrEmpty(dir) || !OperatingSystem.IsWindows()) { tcs.SetResult(null); return tcs.Task; }
            var th = new Thread(() =>
            {
                try
                {
                    string path = null;
                    foreach (var l in new[] { lang, "us", "ge" })
                    {
                        if (string.IsNullOrEmpty(l)) continue;
                        var p = FlashHost.FindFile(Path.Combine(dir, "frontend"), "tips_" + l + ".bnk");
                        if (p != null) { path = p; break; }
                    }
                    if (path == null) { tcs.SetResult(null); return; }
                    var bank = BnkArchive.Open(path);
                    var imgs = new List<BnkEntry>();
                    foreach (var e in bank.Entries) if (e.Extension == ".jpg" || e.Extension == ".png") imgs.Add(e);
                    if (imgs.Count == 0) { tcs.SetResult(null); return; }
                    var pick = imgs[Random.Shared.Next(imgs.Count)];
                    var bytes = bank.Read(pick);
                    var rgba = WicImage.Decode(bytes, out int w, out int h);
                    tcs.SetResult(rgba != null ? (rgba, w, h, pick.Name) : null);
                }
                catch (Exception ex)
                {
                    Log.Error("Loading screen: " + ex.Message);
                    tcs.TrySetResult(null);
                }
            })
            {
                IsBackground = true,
                Name = "Loadingscreen"
            };
            th.SetApartmentState(ApartmentState.MTA);
            th.Start();
            return tcs.Task;
        }

        /// <summary>Create a decoded image as a texture (game thread).</summary>
        void TakeDecoded()
        {
            var d = _decode;
            if (d == null || !d.IsCompleted) return;
            _decode = null;
            var r = FlashHost.Renderer;
            if (d.Result is not { } img || r == null) return;
            try
            {
                // UnityGui=true zeichnet die Werte unverändert in die UNORM-Sicht: die Textur muss deshalb UNORM (linear) sein,
                // sonst dekodiert die GPU sRGB->linear und der Flash-Shader kodiert nicht nach (Bild wird zu dunkel).
                _tex = r.CreateTexture(new TextureUpload { Width = img.w, Height = img.h, Format = GpuTextureFormat.Rgba8, Blocks = img.rgba, WrapU = TextureWrap.Clamp, WrapV = TextureWrap.Clamp, Linear = true, Name = "Loadingscreen " + img.name });
            }
            catch (Exception ex)
            {
                Log.Error("Loading screen: Texture:" + ex.Message);
            }
        }

        bool Ready()
        {
            foreach (var r in _ready)
            {
                bool ok;
                try { ok = r(); } catch (Exception) { ok = true; }
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>Every image (from the game): Hide them as soon as everything is ready.</summary>
        public static void Update()
        {
            var s = _inst;
            if (s == null) return;
            float t = Time.unscaledTime;

            if (s._legal)
            {
                if (!s._closing)
                {
                    if (s._slides == null && s._legalDecode != null && s._legalDecode.IsCompleted)
                    {
                        s._slides = s._legalDecode.Result;
                        s._legalDecode = null;
                        var r = FlashHost.Renderer;
                        if (s._slides != null && r != null)
                            foreach (var sl in s._slides)
                            {
                                try
                                {
                                    sl.Texture = r.CreateTexture(new TextureUpload { Width = sl.W, Height = sl.H, Format = GpuTextureFormat.Rgba8, Blocks = sl.Rgba, WrapU = TextureWrap.Clamp, WrapV = TextureWrap.Clamp, Linear = true, Name = "Legal " + sl.Name });
                                }
                                catch (Exception ex) { Log.Error("Legal screen: Texture " + sl.Name + ": " + ex.Message); }
                            }
                        if (s._slides != null && s._slides.Count > 0) s._slideUntil = t + MathF.Max(1f, s._slides[0].Duration);
                    }
                    if (s._slides != null && s._slides.Count > 0)
                    {
                        while (t >= s._slideUntil && s._slideIndex < s._slides.Count)
                        {
                            s._slideIndex++;
                            if (s._slideIndex < s._slides.Count) s._slideUntil = t + MathF.Max(1f, s._slides[s._slideIndex].Duration);
                        }
                        if (s._slideIndex >= s._slides.Count)
                            s._closing = true;
                        else
                            s._tex = s._slides[s._slideIndex].Texture;
                    }
                    else if (t - s._shownAt > MaxSeconds)
                    {
                        s._closing = true;
                        Log.Warn("Legal screen: Timeout beim Laden der Bilder");
                    }
                }
            }
            else
            {
                if (!s._closing) s.TakeDecoded();
                if (!s._closing && t >= s._minUntil && !TDU2Session.Saving && s.Ready()) s._closing = true;
                if (!s._closing && t - s._shownAt > MaxSeconds) { s._closing = true; Log.Warn("Loading Screen: Timeout"); }
            }

            float step = Time.unscaledDeltaTime / (s._closing ? 0.35f : 0.12f);
            s._alpha = s._closing ? MathF.Max(0f, s._alpha - step) : MathF.Min(1f, s._alpha + step);
            if (General != null) General.GuiDepth = s._alpha > 0.01f ? -2001 : -30;
            if (s._closing && s._alpha <= 0f)
            {
                var done = s._legalDone;
                s._legalDone = null;
                s.Destroy();
                _inst = null;
                if (done != null)
                {
                    try { done(); } catch (Exception ex) { Log.Error("Legal screen: onDone: " + ex.Message); }
                }
            }
        }

        void Destroy()
        {
            FlashHost.RemoveLayer(this);
            if (General != null) General.GuiDepth = -30;
            _tex?.Dispose(); _tex = null;
            _black?.Dispose(); _black = null;
            if (_slides != null)
            {
                foreach (var sl in _slides) sl.Texture?.Dispose();
                _slides = null;
            }
        }

        public void Render(IRenderer r)
        {
            if (_alpha <= 0f) return;
            float sw = Screen.width, sh = Screen.height;
            _black ??= r.CreateTexture(new TextureUpload { Width = 1, Height = 1, Format = GpuTextureFormat.Rgba8, Blocks = new byte[] { 0, 0, 0, 255 }, Name = "Loadingscreen-Black", Linear = true });
            Quad(r, _black, 0, 0, sw, sh, _alpha);
            if (_tex != null)
            {
                // ScaleToFit
                float sc = MathF.Min(sw / _tex.Width, sh / _tex.Height);
                float w = _tex.Width * sc, h = _tex.Height * sc;
                Quad(r, _tex, (sw - w) * 0.5f, (sh - h) * 0.5f, w, h, _alpha);
            }
        }

        static readonly ObjectVertex[] _q = new ObjectVertex[4];
        static void Quad(IRenderer r, IGpuTexture t, float x, float y, float w, float h, float alpha)
        {
            _q[0] = new ObjectVertex { Position = new Vector3(x, y, 0), Uv0 = new System.Numerics.Vector2(0, 0) };
            _q[1] = new ObjectVertex { Position = new Vector3(x + w, y, 0), Uv0 = new System.Numerics.Vector2(1, 0) };
            _q[2] = new ObjectVertex { Position = new Vector3(x + w, y + h, 0), Uv0 = new System.Numerics.Vector2(1, 1) };
            _q[3] = new ObjectVertex { Position = new Vector3(x, y + h, 0), Uv0 = new System.Numerics.Vector2(0, 1) };
            // wie Unity GUI.DrawTexture im linearen Farbraum (wie die Flash-Filme)
            var p = new FlashQuadParams { Mul = new System.Numerics.Vector4(1, 1, 1, alpha), Add = System.Numerics.Vector4.Zero, Clip = new System.Numerics.Vector4(-1e7f, -1e7f, 1e7f, 1e7f), MaskRect = System.Numerics.Vector4.Zero, UnityGui = true };
            r.DrawFlash(_q, t, null, p);
        }
    }
}
