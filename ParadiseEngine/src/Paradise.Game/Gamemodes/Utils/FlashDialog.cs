using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;

namespace Paradise.Game.Gamemodes;

/// <summary>
/// Bestätigungsdialog des Spiels (Original: GSFlash_Impl "dialog_box"-Manager, OnFlashMessage): Ein Film ruft seine
/// _root.DialogBox(Case, Txt1, Txt2, Type1, Type2, YesNo …) auf, setzt /:DgbxCase, /:Msg1Txt, /:Msg2Txt, /:Msg1Type, /:Msg2Type,
/// /:YesNo und meldet FSCommand "DBXO". Das Spiel zeigt dann die Box aus general.bnk (/dialog_box, SetMsg: open_simple bzw.
/// open_double mit Ja/Nein), der Spieler entscheidet, die Box spielt "close" und meldet "dbxc"; danach setzt das Spiel
/// /:PlayerAnswer im Quell-Film (/:DialogBoxSource) und ruft dort DialogBox() erneut auf.
/// Unity hat die Box übersprungen und immer "Ja" geantwortet – hier wird sie wie im Original angezeigt.
/// Aufbau (general.vmf): /dialog_box setzt beim Öffnen _root.Clip = this und definiert A()/B(); die Knöpfe in /dialog_box/help
/// (Labels yes_no, ok …) rufen bei Klick _root.Clip.A() bzw. B() und spielen a_/b_.pc_btn "over"/"out" (Markierung).
/// Läuft unabhängig vom Modus (ScreenModule.Update), damit die Box auch nach dem Schließen eines Menüs bedienbar bleibt.
/// Gemeinsam für alle Interieur-/Shop-Filme (Aufruf bei "dbxo", <see cref="Update"/> jedes Bild solange <see cref="Active"/>).
/// </summary>
public static class FlashDialog
{
    static VmfPlayer? _src, _gen;
    static bool _hooked, _closed, _prevMouse, _prevSrcMouse, _yesNo;
    static string _case = "";
    static string? _fnMove, _fnA, _fnB;
    static Action<string>? _onAnswer;

    /// <summary>Box offen: Tasten/Maus gehören der Box (der Quell-Film wartet).</summary>
    public static bool Active { get; private set; }

    /// <summary>"dbxo" des Films <paramref name="source"/>: Box anzeigen. false = keine Box möglich (dann sofort Ja, wie bisher).</summary>
    /// <param name="onAnswer">Antwort ("a"/"b") an den Modus: ruft dort die DialogBox des Films (eigener Besitzer/Nacharbeit je Modus);
    /// null = /:PlayerAnswer setzen und den letzten DialogBox-Besitzer des Quell-Films aufrufen.</param>
    public static bool Open(VmfPlayer source, Action<string>? onAnswer = null)
    {
        _onAnswer = onAnswer;
        var gen = LoadingScreen.General;
        _case = source.GetVariable("/:DgbxCase") ?? "";
        // /dialog_box ist im Overlay ausgeblendet (GeneralOverlay); erst sichtbar machen – FunctionOwners kennt nur gezeigte Clips
        bool wasShown = gen != null && gen.Find("/dialog_box")?.Visible == true;
        gen?.Show("/dialog_box");
        if (gen == null || gen.FunctionOwners("SetMsg").Count == 0)
        {
            if (gen != null && !wasShown) gen.Hide("/dialog_box");
            Log.Warn("Dialog '" + _case + "': general.bnk/dialog_box SetMsg fehlt -> Ja");
            Deliver(source, "a");
            return false;
        }
        _src = source; _gen = gen; _closed = false;
        if (!_hooked) { gen.FSCommand += OnGeneralCommand; _hooked = true; }
        // Texte und Art der Box aus dem Quell-Film übernehmen (_root-Variablen wie im Original)
        foreach (var v in new[] { "Msg1Txt", "Msg2Txt", "Msg3Txt", "Msg1Type", "Msg2Type", "DgbxCase" })
        {
            string? val = source.GetVariable("/:" + v);
            gen.SetVariable("/:" + v, string.IsNullOrEmpty(val) ? (v == "DgbxCase" ? "" : "none") : val);
        }
        // YesNo als Boolean: B() der Box prüft "_root.YesNo == true" – ein Text "true" ist in AS1 dabei NaN != 1 (Nein/Zurück tat nichts)
        _yesNo = IsTrue(source.GetVariable("/:YesNo"));
        gen.SetValue("/:YesNo", _yesNo);
        gen.SetVariable("/:PlayerAnswer", "");
        gen.SetVariable("/:DialogBoxSource", "shop");
        gen.SetVariable("/:Choice", "yes");
        // Maus nur an die Box (der Quell-Film liegt sonst über/unter ihr und schluckt die Klicks)
        _prevMouse = gen.MouseEnabled; _prevSrcMouse = source.MouseEnabled;
        gen.MouseEnabled = true; source.MouseEnabled = false;
        gen.SetValue("/:Input", true);
        gen.SetValue("/:_buttons", true);
        // Knöpfe der Box (Ja/Nein: Labels default/over/loop/out) in den Grundzustand – die Box war seit dem Start ausgeblendet
        ResetButtons(gen, gen.Find("/dialog_box")?.Sprite);
        // wie das Spiel über das Label "open": Bild 6 setzt _root.Clip = this (Ziel der Knopf-Klicks _root.Clip.A()/B()),
        // Bild 7 ruft SetMsg (-> open_simple/open_double). Der direkte SetMsg-Aufruf ließ Clip bei einfachen Boxen leer.
        if (!gen.GotoLabel("/dialog_box", "open", true))
        {
            var owners = gen.FunctionOwners("SetMsg");
            gen.CallClip(owners[0], "SetMsg");
        }
        _fnMove = Owner(gen, "Move"); _fnA = Owner(gen, "A"); _fnB = Owner(gen, "B");
        Active = true;
        Log.Info("Dialog '" + _case + "': Funktionen Move " + (_fnMove ?? "-") + ", A " + (_fnA ?? "-") + ", B " + (_fnB ?? "-") + " | " + Tree(gen, gen.Find("/dialog_box")?.Sprite, 0));
        Log.Info("Dialog '" + _case + "': " + (gen.GetVariable("/:Msg1Txt") ?? "") + " (Ja/Nein " + (gen.GetVariable("/:YesNo") ?? "-") + ")");
        return true;
    }

    /// <summary>Jedes Bild (vom Modus, solange <see cref="Active"/>): Tasten an die Box; true = Box offen.</summary>
    public static bool Update()
    {
        if (!Active || _gen == null) return false;
        var gen = _gen;
        // Tasten wie im Spiel an die Box: links/rechts = Ja/Nein wählen (Move), A = gewählte Antwort, B = Nein
        foreach (var (key, dir) in new[] { (FlashKey.Left, "left"), (FlashKey.Right, "right"), (FlashKey.Up, "up"), (FlashKey.Down, "down") })
            if (FlashNav.Pressed(key)) { if (_fnMove != null) gen.CallClip(_fnMove, "Move", dir); else FlashNav.Send(gen, key); }
        if (FlashNav.Pressed(FlashKey.Confirm)) { if (_fnA != null) gen.CallClip(_fnA, "A"); else FlashNav.Send(gen, FlashKey.Confirm); }
        if (FlashNav.Pressed(FlashKey.Back))
        {
            if (_fnB != null) gen.CallClip(_fnB, "B");
            else if (_yesNo) { gen.SetVariable("/:Choice", "no"); if (_fnA != null) gen.CallClip(_fnA, "A"); else FlashNav.Send(gen, FlashKey.Confirm); }
            else FlashNav.Send(gen, FlashKey.Back);
        }
        if (_closed) Finish();
        return Active;
    }

    static void OnGeneralCommand(string cmd, string args)
    {
        if (!Active) return;
        if (string.Equals((cmd ?? "").Trim(), "dbxc", StringComparison.OrdinalIgnoreCase)) _closed = true;
    }

    static void Finish()
    {
        var gen = _gen; var src = _src;
        Active = false; _closed = false; _src = null;
        if (gen == null || src == null) return;
        string ans = gen.GetVariable("/:PlayerAnswer") ?? "";
        if (string.IsNullOrEmpty(ans)) ans = gen.GetVariable("/:TmpAnswer") ?? "a";
        gen.Hide("/dialog_box");
        gen.MouseEnabled = _prevMouse; src.MouseEnabled = _prevSrcMouse;
        Log.Info("Dialog '" + _case + "' -> " + ans);
        Deliver(src, ans);
    }

    static void Deliver(VmfPlayer src, string ans)
    {
        var cb = _onAnswer; _onAnswer = null;
        if (cb != null) cb(ans); else Answer(src, ans);
    }

    /// <summary>Antwort an den Quell-Film (/:PlayerAnswer + DialogBox()).</summary>
    static void Answer(VmfPlayer src, string ans)
    {
        src.SetVariable("/:PlayerAnswer", ans);
        var owners = src.FunctionOwners("DialogBox");
        src.CallClip(owners.Count > 0 ? owners[^1] : "/", "DialogBox");
    }

    /// <summary>Box ohne Antwort schließen (Modus verlassen).</summary>
    public static void Cancel()
    {
        if (!Active) return;
        var src = _src;
        Active = false; _closed = false; _src = null; _onAnswer = null;
        if (_gen != null) { _gen.Hide("/dialog_box"); _gen.MouseEnabled = _prevMouse; }
        if (src != null) src.MouseEnabled = _prevSrcMouse;
    }

    static bool IsTrue(string? v) => v != null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>Kürzester Besitzer einer Funktion innerhalb von /dialog_box (sonst null).</summary>
    static string? Owner(VmfPlayer gen, string fn)
    {
        string? best = null;
        foreach (var o in gen.FunctionOwners(fn))
            if (o.IndexOf("dialog_box", StringComparison.OrdinalIgnoreCase) >= 0 && (best == null || o.Length < best.Length)) best = o;
        return best;
    }

    /// <summary>Clips mit den Knopf-Labels default/over unter der Box auf "default" (Grundzustand ohne Markierung).</summary>
    static void ResetButtons(VmfPlayer gen, VmfPlayer.Instance? inst)
    {
        if (inst == null) return;
        foreach (var d in inst.List.Values)
        {
            if (d.Sprite == null) continue;
            string path = VmfPlayer.PathOf(d.Sprite);
            if (gen.HasLabel(path, "default") && gen.HasLabel(path, "over")) gen.GotoLabel(path, "default", false);
            ResetButtons(gen, d.Sprite);
        }
    }

    /// <summary>Diagnose: Clips der Box mit Label und Sichtbarkeit.</summary>
    static string Tree(VmfPlayer gen, VmfPlayer.Instance? inst, int depth)
    {
        if (inst == null || depth > 4) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var d in inst.List.Values)
        {
            if (d.Sprite == null) continue;
            string path = VmfPlayer.PathOf(d.Sprite);
            sb.Append(path).Append('[').Append(gen.Label(path) ?? "-").Append(d.Visible ? "" : ",aus").Append("] ");
            sb.Append(Tree(gen, d.Sprite, depth + 1));
        }
        return sb.ToString();
    }
}
