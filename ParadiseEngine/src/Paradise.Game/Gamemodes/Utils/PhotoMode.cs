using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Online;
using Paradise.Core.Photos;
using Paradise.Core.Render;
using Paradise.Game.Input;
using Paradise.Game.Screens;
using System.Numerics;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Fotomodus des Pausemenüs ("Foto machen", MAIN.vmf) – exe MngPhotoMode (Befehle 0xceb670 / 0xceb920 / 0xcec180, Ablauf 0xceae50 ...).
/// Der Film macht die Oberfläche selbst:
///   Pausemenü "photo" -> phmo:phin, mcls, Wurzel "photomode" (openPhotoMode: photomode_menu "open", Leiste upbar default/car);
///   Konsole (photomode_menu "stop"): A = phmo:clic + "take" (sndp:shot) -> Polaroid (photomode_Polaroid "open_choice", Bild in
///   lurephoto); B = schließen (mcou) -> Wurzel "close_photomode" -> phmo:phou -> zurück ins Pausemenü (pause_back);
///   X = Optionen (photomode_param, phop:*); Y = Notizblock (nur Fotografen-Modus).
///   Polaroid: A -> Namenseingabe ("open_name", WhatPlatform 1) bzw. virtuelle Tastatur, A = phsa:vali, B = phsa:canc;
///   Ende des Polaroids phsa:clos.
/// Hier: Kamera, Aufnahme, Polaroid-Bild, Vorgabename, Speichern ins Album (<see cref="PhotoAlbum"/>), Sperren wie die exe.
/// Läuft im angehaltenen Spiel (Pausemenü: Fahrzeuge eingefroren, die Welt steht still).
/// </summary>
internal static class PhotoMode
{
    /// <summary>Kamera: Höchstabstand zum Auto (m) [vermutet: im Original nicht gefunden].</summary>
    public const float MaxDistance = 20f;
    public const float MoveSpeed = 6f, LookSpeed = 1.6f, MinFov = 20f, MaxFov = 90f;

    public static bool Active { get; private set; }
    /// <summary>Konsole offen: Kamera bewegen statt Menü-Pfeile (photomode_menu steht auf "stop").</summary>
    public static bool CameraActive => Active && _ui != null && _shotState == 0 && _ui.Label("/photomode_menu") == "stop";

    static IGameModeContext? _ctx;
    static VmfPlayer? _ui;
    static Func<int, string, string>? _loc;
    static Func<string?>? _area;
    // Kamera vor dem Fotomodus (beim Verlassen zurück)
    static Vector3 _savedPos; static (Vector3 forward, Vector3 up)? _savedOverride; static float _savedFov, _savedNear;
    static bool _savedHud;
    // Fotokamera (Spielkoordinaten)
    static Vector3 _pos, _carPos; static float _yaw, _pitch, _fov;
    // Aufnahme
    static int _shotState;                 // 0 bereit, 1 angefordert, 2 Polaroid zeigt das Bild
    static SceneCapture? _shot;
    static byte[]? _jpg;
    static Texture2D? _polaroid;

    /// <summary>
    /// /:LockSnapshot wie exe 0x9eb142: frei nur in der Freifahrt (keine Rennen, kein Interieur, ohne laufende Sperren) und solange
    /// das Album nicht voll ist (0xcea5b0: Zähler 0..59).
    /// </summary>
    public static bool CanTake(IGameModeContext ctx, bool raceMode)
    {
        if (raceMode || InteriorUtils.Inside) return false;
        if (GAME_MODE_PAUSE.Return != Gamemode.GAME_MODE_FREERIDE) return false;
        if (ctx.Terrain?.Streamer == null || ctx.Vehicles?.Manager?.Current == null) return false;
        if (Story.BlocksPauseMenu) return false;
        return !PhotoAlbum.IsFull;
    }

    /// <summary>FSCommands des Fotomodus; true = behandelt.</summary>
    public static bool Command(string c, string? args, IGameModeContext ctx, VmfPlayer ui, Func<int, string, string> loc, Func<string?> area)
    {
        _area = area;
        switch (c)
        {
            case "phmo:phin": Begin(ctx, ui, loc); return true;
            case "mcls": return Active;                         // Menü schließt (Film blendet das Pausemenü aus)
            case "mcou": return Active;
            case "phmo:clic": Shoot(); return true;
            case "phmo:phou": End(); return true;
            case "phmo:recl": return true;                      // Belohnungsfenster des Fotografen geschlossen (Fotografen-Modus)
            case "phmo:npin": case "phmo:npva": case "phph": return true;   // Notizblock: nur Fotografen-Aufträge (GAME_MODE_PHOTOGRAPH)
            case "phsa:vali": SaveShot(); return true;
            case "phsa:canc": DiscardShot(); return true;
            case "phsa:clos": CloseShot(); return true;
        }
        if (c.StartsWith("phop:", StringComparison.Ordinal)) { Option(c); return true; }
        return false;
    }

    static void Begin(IGameModeContext ctx, VmfPlayer ui, Func<int, string, string> loc)
    {
        var t = ctx.Terrain; var vm = ctx.Vehicles?.Manager; var cam = t?.Camera; var st = t?.Streamer;
        if (t == null || cam == null || st == null || vm == null) { Log.Warn("Fotomodus: keine Welt"); return; }
        if (Active) return;   // photomode_stop meldet phin erneut: Kamera nicht neu merken
        _ctx = ctx; _ui = ui; _loc = loc;
        Active = true; _shotState = 0; _shot = null; _jpg = null;
        _savedPos = cam.Position; _savedOverride = cam.Override; _savedFov = t.FieldOfView; _savedNear = t.NearClip;
        var hud = vm.Minimap?.Hud;
        _savedHud = hud?.Visible ?? false;

        _carPos = vm.PositionGame;
        _pos = st.RenderToGame(cam.Position);
        var f = cam.Override?.forward ?? cam.Forward;
        f = Vector3.Normalize(f);
        _yaw = MathF.Atan2(f.X, f.Z);
        _pitch = MathF.Asin(Math.Clamp(f.Y, -1f, 1f));
        _fov = Math.Clamp(t.FieldOfView, MinFov, MaxFov);

        // Werte, die die exe beim Öffnen setzt (Konsole: A/B erlaubt, Fotografen-Modus aus, Leiste ohne Fahrzeugkamera)
        ui.SetValue("/:PhotographerMode", false);
        ui.SetValue("/:PhotoModeCar", false);
        ui.SetValue("/:BackEnable", true);
        ui.SetValue("/:ClicEnable", true);
        ui.SetValue("/:isLimitPhoto", PhotoAlbum.IsFull);
        ui.SetValue("/:nb_photo_taken", PhotoAlbum.Count);
        ui.SetValue("/:WhatPlatform", 1);                     // PC: Namensfeld statt virtueller Tastatur (photomode_Polaroid A)
        PublishParams(ui);
        ApplyCamera();
        Log.Info($"Fotomodus an (Album {PhotoAlbum.Count}/{PhotoAlbum.MaxPhotos})");
    }

    /// <summary>Fotomodus verlassen (phmo:phou): Kamera und HUD wie vorher.</summary>
    public static void End()
    {
        if (!Active) return;
        Active = false;
        var t = _ctx?.Terrain; var cam = t?.Camera;
        if (t != null && cam != null)
        {
            cam.Position = _savedPos; cam.Override = _savedOverride; t.FieldOfView = _savedFov; t.NearClip = _savedNear;
        }
        var hud = _ctx?.Vehicles?.Manager?.Minimap?.Hud;
        if (hud != null) hud.Visible = _savedHud;
        _ui?.SetClipTexture("/photomode_Polaroid/lurephoto", null!);
        _polaroid = null; _jpg = null; _shot = null; _shotState = 0;
        RestoreFx();
        Log.Info("Fotomodus aus");
    }

    // ------------------------------------------------------------------ Bildeinstellungen (photomode_param, exe 0xcee1a0)
    // Der Film (sprite photomode_param): initParam sendet phop:open und sperrt die Eingabe (_root.Input = false); die exe setzt dann
    // /:NbNotches und die Werte /:PM_* und gibt die Eingabe frei. Hoch/Runter wählt die Zeile (Move, phop:edit:chpa meldet die
    // Fokus-Variable /:VarToFocus), Links/Rechts gehen an den Regler der Zeile (Param&lt;n&gt;/slider: Move -> setzt die Variable,
    // FSCommand "&lt;cmd&gt;:slid:updt"); A = phop:vali, B = phop:canc.
    public const int Notches = 20;   // Vorgabe des Films (fake-Daten: NbNotches 20) [vermutet: exe-Wert aus einer Tabelle]
    static readonly string[] ParamVars = { "PM_BloomIntensity", "PM_DOFIntensity", "PM_DOFDistance", "PM_Contrast",
                                           "PM_ColorBalanceR", "PM_ColorBalanceG", "PM_ColorBalanceB" };
    static readonly float[] _param = { 10, 10, 10, 10, 10, 10, 10 }, _paramSaved = new float[7];
    static bool _fxSaved;
    static float _baseBloom, _baseOverlay; static Vector3 _baseMid;

    /// <summary>Einstellungen offen (photomode_param nicht auf "default").</summary>
    static bool OptionsOpen => Active && _ui != null && _ui.Label("/photomode_param") is string l && l != "default" && l != "close";

    static void Option(string c)
    {
        var ui = _ui;
        if (ui == null) return;
        switch (c)
        {
            case "phop:open":
                Array.Copy(_param, _paramSaved, _param.Length);
                PublishParams(ui);
                ui.SetValue("/:Input", true);
                return;
            case "phop:vali":
                ReadParams();
                ui.SetValue("/:Input", true);
                return;
            case "phop:canc":
                Array.Copy(_paramSaved, _param, _param.Length);
                ui.SetValue("/:Input", true);
                return;
        }
        if (c.EndsWith(":slid:updt", StringComparison.Ordinal) || c.StartsWith("phop:edit", StringComparison.Ordinal)) { ReadParams(); return; }
        Log.Debug("Fotomodus Option: " + c);
    }

    /// <summary>Werte für initParam: Rasterzahl, Reglerstände, Fokus auf die erste Zeile (setParam[0][3]).</summary>
    static void PublishParams(VmfPlayer ui)
    {
        ui.SetValue("/:NbNotches", Notches);
        for (int i = 0; i < ParamVars.Length; i++) ui.SetValue("/:" + ParamVars[i], _param[i]);
        ui.SetValue("/:CurrentParam", 0);
        ui.SetVariable("/:VarToFocus", "_root." + ParamVars[0]);
    }

    static void ReadParams()
    {
        for (int i = 0; i < ParamVars.Length; i++)
        {
            var v = _ui!.GetVariable("/:" + ParamVars[i]);
            if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
                _param[i] = Math.Clamp(f, 0f, Notches);
        }
    }

    /// <summary>
    /// Regler auf die Nachbearbeitung (PhotoshopFx) – solange der Fotomodus offen ist, danach die Werte von vorher.
    /// [vermutet: Abbildung; die exe-Umrechnung 0xcee1a0 ist nicht entschlüsselt] Mitte (10) = unverändert: Bloom/Kontrast skalieren
    /// BloomIntensity bzw. OverlayOpacity (0..2-fach), Farbbalance R/G/B verschiebt die Mitteltöne um ±0,3. Tiefenschärfe hat der
    /// Renderer noch nicht (PhotoshopFx.CrappyMode) -> ohne Wirkung.
    /// </summary>
    static void ApplyFx()
    {
        if (!_fxSaved) { _baseBloom = PhotoshopFx.BloomIntensity; _baseOverlay = PhotoshopFx.OverlayOpacity; _baseMid = PhotoshopFx.BalanceMidtones; _fxSaved = true; }
        float k(int i) => _param[i] / (Notches * 0.5f);
        PhotoshopFx.BloomIntensity = _baseBloom * k(0);
        PhotoshopFx.OverlayOpacity = Math.Clamp(_baseOverlay * k(3), 0f, 1f);
        PhotoshopFx.BalanceMidtones = _baseMid + new Vector3(k(4) - 1f, k(5) - 1f, k(6) - 1f) * 0.3f;
    }

    static void RestoreFx()
    {
        if (!_fxSaved) return;
        PhotoshopFx.BloomIntensity = _baseBloom; PhotoshopFx.OverlayOpacity = _baseOverlay; PhotoshopFx.BalanceMidtones = _baseMid;
        _fxSaved = false;
    }

    /// <summary>Pfeile in den Einstellungen: Hoch/Runter an photomode_param, Links/Rechts an den Regler der Zeile.</summary>
    static void OptionKeys()
    {
        var ui = _ui!;
        if (FlashNav.Pressed(FlashKey.Up)) ui.Call("/photomode_param", "Move", "up");
        if (FlashNav.Pressed(FlashKey.Down)) ui.Call("/photomode_param", "Move", "down");
        bool l = FlashNav.Pressed(FlashKey.Left), r = FlashNav.Pressed(FlashKey.Right);
        if (!l && !r) return;
        string n = ui.GetVariable("/:CurrentParam") ?? "0";
        if (!int.TryParse(n, out int idx)) idx = 0;
        string box = idx == 7 ? "choicebox" : "slider";   // setParam[7] = Avatar (choicebox), sonst Regler
        string path = "/photomode_param/Param" + idx + "/" + box;
        if (!ui.Call(path, "Move", l ? "left" : "right")) Log.Debug("Fotomodus: kein Move an " + path);
    }

    // ------------------------------------------------------------------ Jedes Bild (aus PauseMenu.Update)
    /// <summary>Kamera und Aufnahme; true = die Kamera hat die Pfeiltasten (Konsole offen).</summary>
    public static bool Update(float dt)
    {
        if (!Active || _ctx == null) return false;
        var hud = _ctx.Vehicles?.Manager?.Minimap?.Hud;
        if (hud != null) hud.Visible = false;                 // HUD aus, solange fotografiert wird
        var shot = Interlocked.Exchange(ref _shot, null);
        if (shot != null) OnShot(shot);
        bool cam = CameraActive, opt = !cam && OptionsOpen;
        if (cam) MoveCamera(dt);
        else if (opt) OptionKeys();
        ApplyCamera();
        ApplyFx();
        return cam || opt;
    }

    static void MoveCamera(float dt)
    {
        var t = _ctx!.Terrain!;
        var input = t.Input;
        // Blick: Maus (Taste gedrückt), rechter Stick
        if (FlashInput.GetMouseButton(0) || FlashInput.GetMouseButton(1))
        {
            // wie FlyCamera: Maus nach rechts = nach rechts drehen (Yaw nimmt ab, Blick +Z -> Bildschirm-rechts = -X)
            _yaw -= input.MouseDeltaX * 0.0025f;
            _pitch -= input.MouseDeltaY * 0.0025f;
        }
        var look = TDU2InputDevices.Look;
        _yaw -= look.X * LookSpeed * dt;
        _pitch += look.Y * LookSpeed * dt;
        _pitch = Math.Clamp(_pitch, -1.45f, 1.45f);

        // Bewegen: WASD/Pfeile (Steuerkreuz), linker Stick; hoch/runter E/Q bzw. Gas/Bremse
        float fw = (TDU2Controls.RawKeyDown(0x57) || TDU2Nav.UpHeld() ? 1f : 0f) - (TDU2Controls.RawKeyDown(0x53) || TDU2Nav.DownHeld() ? 1f : 0f);
        float rt = (TDU2Controls.RawKeyDown(0x44) || TDU2Nav.RightHeld() ? 1f : 0f) - (TDU2Controls.RawKeyDown(0x41) || TDU2Nav.LeftHeld() ? 1f : 0f);
        float up = (TDU2Controls.RawKeyDown(0x45) ? 1f : 0f) - (TDU2Controls.RawKeyDown(0x51) ? 1f : 0f);
        if (TDU2InputDevices.HasDevice)
        {
            if (MathF.Abs(rt) < 0.01f) rt = TDU2InputDevices.Steer;
            up += TDU2InputDevices.Throttle - TDU2InputDevices.Brake;
        }
        var fwd = new Vector3(MathF.Sin(_yaw), 0f, MathF.Cos(_yaw));
        var right = new Vector3(-fwd.Z, 0f, fwd.X);   // Bildschirm-rechts (rechtshändig, wie FlyCamera: -Cross(Y, vorne))
        float fast = TDU2Controls.RawKeyDown(0x10) ? 3f : 1f;   // Umschalt
        var p = _pos + (fwd * fw + right * rt + Vector3.UnitY * up) * MoveSpeed * fast * dt;
        // im Umkreis des Autos bleiben, nicht unter den Boden
        var off = p - (_carPos + Vector3.UnitY);
        if (off.Length() > MaxDistance) p = _carPos + Vector3.UnitY + Vector3.Normalize(off) * MaxDistance;
        var st = t.Streamer;
        if (st != null && st.TryGetGroundHeight(p, out var gy) && p.Y < gy + 0.3f) p.Y = gy + 0.3f;
        _pos = p;

        // Brennweite: Mausrad, Bild auf/ab
        float zoom = -FlashInput.mouseScrollDelta * 2f
                     + ((TDU2Controls.RawKeyDown(0x22) ? 1f : 0f) - (TDU2Controls.RawKeyDown(0x21) ? 1f : 0f)) * 25f * dt;
        _fov = Math.Clamp(_fov + zoom, MinFov, MaxFov);
    }

    static void ApplyCamera()
    {
        var t = _ctx?.Terrain; var cam = t?.Camera; var st = t?.Streamer;
        if (t == null || cam == null || st == null) return;
        var dir = new Vector3(MathF.Sin(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), MathF.Cos(_yaw) * MathF.Cos(_pitch));
        t.FlyCameraEnabled = false;
        cam.Position = st.GameToRender(_pos);
        cam.Override = (Vector3.Normalize(dir), Vector3.UnitY);
        t.FieldOfView = _fov;
        t.NearClip = 0.1f;
    }

    // ------------------------------------------------------------------ Aufnahme (phmo:clic, exe 0xceb6d7)
    static void Shoot()
    {
        if (!Active || _shotState != 0) return;
        if (PhotoAlbum.IsFull) { _ui?.SetValue("/:isLimitPhoto", true); return; }
        _shotState = 1;
        var r = _ctx?.Vehicles?.Renderer;
        if (r == null) { _shotState = 0; return; }
        r.RequestSceneCapture(c => Volatile.Write(ref _shot, c ?? new SceneCapture()), false);
        // Vorgabename (exe 0xceb75a: Name des Gebiets am Spieler, z.B. "Ibiza - Bereich 2", sonst Text 956 "Foto";
        // ' wird zum Leerzeichen, höchstens 31 Zeichen)
        string def = PhotoAlbum.CleanName(AreaName() ?? _loc?.Invoke(956, "Foto") ?? "Foto").Replace('\'', ' ');
        if (def.Length > 31) def = def[..31];
        var ui = _ui!;
        ui.SetVariable("/:TxtInput", def);
        ui.SetVariable("/:PhotoNameDefault", def);
        ui.SetVariable("/:PhotoName", def);
        ui.SetValue("/:VK_Value", def);
        ui.SetVariable("/:m_name_", _loc?.Invoke(1934, "Name") ?? "Name");
    }

    /// <summary>Gebietsname wie die Karte: h1..h4 -> Text 3332..3335 ("Hawaii-Bereich n"), i1/i2 -> 3336/3337, d1/d2 -> 3338/3339.</summary>
    static string? AreaName()
    {
        var a = _area?.Invoke();
        if (string.IsNullOrEmpty(a) || a.Length < 2 || !int.TryParse(a[1..], out int n) || n < 1) return null;
        int id = char.ToLowerInvariant(a[0]) switch { 'h' => 3331 + n, 'i' => 3335 + n, 'd' => 3337 + n, _ => 0 };
        return id > 0 ? _loc?.Invoke(id, a) : null;
    }

    /// <summary>Bild ist da: JPG vorbereiten und ins Polaroid (/photomode_Polaroid/lurephoto, exe 0xceae7c).</summary>
    static void OnShot(SceneCapture c)
    {
        if (c.Width <= 0 || c.Height <= 0) { Log.Warn("Fotomodus: Bildaufnahme vom Renderer nicht möglich"); _shotState = 0; return; }
        // SceneCapture: Zeile 0 = oben (Render/SceneCapture.cs) – JPG und Polaroid-Textur unverändert. Mit flipVertical
        // standen die gespeicherten Fotos auf dem Kopf (B93D1BFX.JPG geprüft, Test 24.11. 08:46).
        _jpg = Paradise.Core.Formats.JpegEncoder.Encode(c.Rgba, c.Width, c.Height, 90);
        var px = new Color32[c.Width * c.Height];
        for (int i = 0, src = 0; i < px.Length; i++, src += 4) px[i] = new Color32(c.Rgba[src], c.Rgba[src + 1], c.Rgba[src + 2], 255);
        var tex = new Texture2D(c.Width, c.Height, linear: true) { wrapMode = TextureWrapMode.Clamp };   // Bildwerte wie aufgenommen
        tex.SetPixels32(px);
        tex.Apply();
        _polaroid = tex;
        _ui?.SetClipTexture("/photomode_Polaroid/lurephoto", tex);
        _shotState = 2;
        Log.Debug($"Fotomodus: Aufnahme {c.Width}x{c.Height}, JPG {_jpg.Length / 1024} KB");
    }

    /// <summary>phsa:vali – Foto unter dem eingegebenen Namen ins Album (exe 0xceb948 -> 0xcead80).</summary>
    static void SaveShot()
    {
        if (_jpg == null) { Log.Warn("Fotomodus: kein Bild zum Speichern"); return; }
        var ui = _ui!;
        string name = ui.GetVariable("/:TxtInput") ?? "";
        if (string.IsNullOrWhiteSpace(name)) name = ui.GetVariable("/:PhotoName") ?? "";
        if (string.IsNullOrWhiteSpace(name)) name = ui.GetVariable("/:PhotoNameDefault") ?? "Foto";
        int id = TDU2Session.LoggedIn ? TDU2Session.PlayerId : 0;
        // Buchstabe: exe wählt 'X' oder 'V' nach dem Wert 0x16f9074 (Bedeutung nicht geklärt) [vermutet: 'X']
        var e = PhotoAlbum.Save(_jpg, name.Trim(), id, 0, 'X');
        _jpg = null;
        ui.SetValue("/:nb_photo_taken", PhotoAlbum.Count);
        ui.SetValue("/:isLimitPhoto", PhotoAlbum.IsFull);
        if (e != null) PhotoSaved?.Invoke(e);
    }

    /// <summary>phsa:canc – Bild verwerfen (exe: Zustand 7, /:infophoto = 1).</summary>
    static void DiscardShot()
    {
        _jpg = null;
        _ui?.SetValue("/:infophoto", true);
    }

    /// <summary>phsa:clos – Polaroid geschlossen, zurück in die Konsole.</summary>
    static void CloseShot()
    {
        _jpg = null; _shotState = 0;
        _ui?.SetClipTexture("/photomode_Polaroid/lurephoto", null!);
        _polaroid = null;
        // exe 0xcec40e: außerhalb des Fotografen-Modus ruft sie danach _root.openPhotoMode() – Konsole wieder offen
        // (ohne das blieb der Film nach dem Speichern ohne offenes Menü stehen)
        if (Active && _ui != null && !_ui.Call("/", "openPhotoMode")) Log.Warn("Fotomodus: openPhotoMode nicht gefunden");
    }

    /// <summary>Neues Foto im Album (Upload/Abgleich hängt sich hier an).</summary>
    public static event Action<PhotoEntry>? PhotoSaved;
}
