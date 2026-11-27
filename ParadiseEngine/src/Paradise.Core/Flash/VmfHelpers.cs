#nullable disable
using Paradise.Core.Math;

namespace Paradise.Core.Flash
{
    /// <summary>
    /// Provides convenient helpers for interacting with Flash movies. Paths use the same
    /// format as in the original game (for example, "/menus/profile/profile2"); variables
    /// use formats such as "/:Name" or "/path:name".
    ///
    /// Basic movie control:
    ///   ui.Show(path) / ui.Hide(path)             Set visibility (like _visible; movie scripts may change it again).
    ///   ui.ForceVisible(path, false)              Enforce visibility even if the movie changes _visible; pass null to release it.
    ///   ui.SetFrame(path, 10)                     Call gotoAndStop(10), using Flash's 1-based frame numbering.
    ///   ui.PlayFrom(path, 10) / ui.PlayLabel(path, "open")
    ///   ui.SetLabel(path, "on")                   Call gotoAndStop("on").
    ///   ui.SetText("/:AvatarName", "Michael")     Set a text/value variable (a text field displays it once assigned).
    ///   ui.SetNumber("/:Nationality", 24)         Set a typed value (for comparisons or gotoAndStop(_root.Nationality) in scripts).
    ///   ui.SetBool("/:state_online", true)
    ///   ui.Frame(path) / ui.Label(path)           Get the current frame (1-based) / label.
    ///   ui.Exists(path), ui.LabelsOf(path)
    ///
    /// Pins prevent the movie timeline or its scripts from overwriting values:
    ///   ui.PinText("/car_datas/datas_base:cur_power", "300 bhp")   Keep a variable set (reapplied after every frame).
    ///   ui.PinValue("/:IsCardLoaded", true)                        Keep a typed value (number/bool).
    ///   ui.PinLabel("/car_datas/ca_name/group", "A2")              Call gotoAndStop(label), including when the clip is recreated.
    ///   ui.PinFrame(path, 3)                                       Same as above, using a 1-based frame number.
    ///   using (ui.PinScope()) { ui.SetVariable(...); ui.SetLabel(...); }
    ///                                                        Automatically pin everything set within the scope.
    ///   ui.Unpin("/car_datas") / ui.UnpinAll()                      Release pins by variable/path prefix, or release all pins.
    ///
    /// Mouse handling:
    ///   ui.ConsumeClick() discards the current click so the movie does not receive its release event.
    ///   HintClicked ("A"/"B"/"X"/"Y") is raised when a menu button hint (a_/b_/x_/y_) is clicked.
    ///   If there is no subscriber, the corresponding Input(...) call is used.
    ///   TextFieldClicked provides the variable of the clicked text field (for example, "/:email").
    /// </summary>
    public sealed partial class VmfPlayer
    {
        readonly Dictionary<string, bool> _forcedVisible = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, float> _forcedAlpha = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Makes the specified movie instance visible.</summary>
        /// <param name="path">The instance path.</param>
        /// <returns><see langword="true"/> if the instance was found; otherwise, <see langword="false"/>.</returns>
        public bool Show(string path) { var d = Find(path); if (d == null) return false; d.Visible = true; return true; }
        /// <summary>Hides the specified movie instance.</summary>
        /// <param name="path">The instance path.</param>
        /// <returns><see langword="true"/> if the instance was found; otherwise, <see langword="false"/>.</returns>
        public bool Hide(string path) { var d = Find(path); if (d == null) return false; d.Visible = false; return true; }

        /// <summary>
        /// Enforces an instance's visibility and reapplies the setting after every frame.
        /// Pass <see langword="null"/> to let the movie control visibility again.
        /// </summary>
        /// <param name="path">The instance path.</param>
        /// <param name="visible">The visibility to enforce, or <see langword="null"/> to release the override.</param>
        public void ForceVisible(string path, bool? visible)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (visible.HasValue) { _forcedVisible[path] = visible.Value; var d = Find(path); if (d != null) d.Visible = visible.Value; }
            else _forcedVisible.Remove(path);
        }

        /// <summary>Releases all enforced visibility settings below the specified path prefix.</summary>
        /// <param name="pathPrefix">The path prefix to release, or <see langword="null"/> to release all visibility overrides.</param>
        public void ReleaseForced(string pathPrefix = null)
        {
            if (string.IsNullOrEmpty(pathPrefix)) { _forcedVisible.Clear(); return; }
            var keys = new List<string>(_forcedVisible.Keys);
            foreach (var k in keys) if (k.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase)) _forcedVisible.Remove(k);
        }

        void ApplyForcedVisibility()
        {
            if (_forcedVisible.Count == 0) return;
            foreach (var kv in _forcedVisible) { var d = Find(kv.Key); if (d != null) d.Visible = kv.Value; }
        }

        /// <summary>Enforces an instance's alpha after every frame, without pinning the timeline's color transform.</summary>
        /// <param name="path">The instance path.</param>
        /// <param name="alpha">Opacity 0..1.</param>
        public void ForceAlpha(string path, float alpha)
        {
            if (string.IsNullOrEmpty(path)) return;
            _forcedAlpha[path] = Mathf.Clamp01(alpha);
            var d = Find(path); if (d != null) d.Cx.Mul.w = _forcedAlpha[path];
        }

        /// <summary>Releases one enforced alpha, or all alphas below a path prefix (or all when prefix is null).</summary>
        public void ReleaseAlpha(string path = null)
        {
            if (string.IsNullOrEmpty(path)) { _forcedAlpha.Clear(); return; }
            foreach (var k in new List<string>(_forcedAlpha.Keys)) if (k.StartsWith(path, StringComparison.OrdinalIgnoreCase)) _forcedAlpha.Remove(k);
        }

        void ApplyForcedAlpha()
        {
            if (_forcedAlpha.Count == 0) return;
            foreach (var kv in _forcedAlpha) { var d = Find(kv.Key); if (d != null) d.Cx.Mul.w = kv.Value; }
        }

        sealed class PinVar { public object Value; public bool Typed; }
        sealed class PinPos { public string Label; public int Frame; public Instance Applied; public bool Always; }
        readonly Dictionary<string, PinVar> _pinVars = new Dictionary<string, PinVar>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, PinPos> _pinPos = new Dictionary<string, PinPos>(StringComparer.OrdinalIgnoreCase);
        int _pinScope;
        bool _applyingPins;

        /// <summary>
        /// Begins a scope in which calls to SetVariable, SetValue, SetLabel, SetFrame, and
        /// GotoLabel(..., false) are automatically pinned until the returned scope is disposed.
        /// </summary>
        /// <returns>An object that releases the pin scope when disposed.</returns>
        public IDisposable PinScope() { _pinScope++; return new PinScopeEnd(this); }
        sealed class PinScopeEnd : IDisposable
        {
            VmfPlayer _p; public PinScopeEnd(VmfPlayer p) { _p = p; }
            public void Dispose() { if (_p != null) { _p._pinScope = System.Math.Max(0, _p._pinScope - 1); _p = null; } }
        }
        bool AutoPin => _pinScope > 0 && !_applyingPins;

        /// <summary>Pins a variable as text so its value is restored after each frame.</summary>
        /// <param name="variable">The variable name.</param>
        /// <param name="text">The text value to preserve.</param>
        public void PinText(string variable, string text) { _pinVars[variable] = new PinVar { Value = text ?? "", Typed = false }; _applyingPins = true; SetVariable(variable, text ?? ""); _applyingPins = false; }
        /// <summary>Pins a typed variable value so it is restored after each frame.</summary>
        /// <param name="variable">The variable name.</param>
        /// <param name="value">The typed value to preserve.</param>
        public void PinValue(string variable, object value) { _pinVars[variable] = new PinVar { Value = value, Typed = true }; _applyingPins = true; SetValue(variable, value); _applyingPins = false; }
        /// <summary>Pins a frame label and reapplies it when the clip instance is recreated.</summary>
        /// <param name="path">The instance path.</param>
        /// <param name="label">The frame label to select.</param>
        /// <returns><see langword="true"/> if the label could be applied; otherwise, <see langword="false"/>.</returns>
        public bool PinLabel(string path, string label) => PinLabel(path, label, false);
        /// <summary>
        /// Pinnt ein Frame-Label und wendet es jedes Frame erneut an. Für Listenzeilen, deren Film bei Hover
        /// oder Auswahl den Frame wechselt und dabei den Zustand der Zelle überschreibt.
        /// </summary>
        /// <param name="path">Der Instanzpfad.</param>
        /// <param name="label">Das Frame-Label.</param>
        /// <param name="always">Auch innerhalb einer bestehenden Instanz ständig neu anwenden.</param>
        /// <returns><see langword="true"/>, wenn das Label gesetzt werden konnte.</returns>
        public bool PinLabel(string path, string label, bool always)
        {
            var pp = new PinPos { Label = label, Frame = -1, Always = always }; _pinPos[path] = pp;
            _applyingPins = true; bool ok = GotoLabel(path, label, false); _applyingPins = false;
            pp.Applied = InstanceOf(path); return ok;
        }
        /// <summary>Pins a frame number and reapplies it when the clip instance is recreated.</summary>
        /// <param name="path">The instance path.</param>
        /// <param name="flashFrame">The target frame using Flash's 1-based numbering.</param>
        /// <param name="always">Reapply every frame even while the instance still exists (stops running timelines).</param>
        /// <returns><see langword="true"/> if the frame could be applied; otherwise, <see langword="false"/>.</returns>
        public bool PinFrame(string path, int flashFrame, bool always = false)
        {
            var pp = new PinPos { Frame = flashFrame, Always = always }; _pinPos[path] = pp;
            _applyingPins = true; bool ok = SetFrame(path, flashFrame); _applyingPins = false;
            pp.Applied = InstanceOf(path); return ok;
        }

        /// <summary>Releases pinned variables and paths whose names begin with the specified prefix.</summary>
        /// <param name="prefix">The prefix to release, or <see langword="null"/> to release all pins.</param>
        public void Unpin(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) { UnpinAll(); return; }
            foreach (var k in new List<string>(_pinVars.Keys)) if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) _pinVars.Remove(k);
            foreach (var k in new List<string>(_pinPos.Keys)) if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) _pinPos.Remove(k);
        }
        /// <summary>Releases all pinned variables and clip positions.</summary>
        public void UnpinAll() { _pinVars.Clear(); _pinPos.Clear(); }
        /// <summary>Gets the total number of pinned variables and clip positions.</summary>
        public int PinCount => _pinVars.Count + _pinPos.Count;

        /// <summary>Reapplies pinned values after each frame, but only when the movie has changed them.</summary>
        void ApplyPins()
        {
            if (_pinVars.Count == 0 && _pinPos.Count == 0) return;
            _applyingPins = true;
            try
            {
                foreach (var kv in _pinVars)
                {
                    var pv = kv.Value;
                    string want = pv.Value is bool b ? (b ? "true" : "false") : pv.Value is double dd ? dd.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                : pv.Value is IFormattable f ? f.ToString(null, System.Globalization.CultureInfo.InvariantCulture) : pv.Value?.ToString() ?? "";
                    if (string.Equals(GetVariable(kv.Key), want, StringComparison.Ordinal)) continue;
                    if (pv.Typed) SetValue(kv.Key, pv.Value); else SetVariable(kv.Key, want);
                }
                foreach (var kv in _pinPos)
                {
                    var inst = InstanceOf(kv.Key);
                    if (inst == null || (!kv.Value.Always && ReferenceEquals(inst, kv.Value.Applied))) continue;   // nur neu angelegte Clips, außer bei Always-Pins
                    if (kv.Value.Label != null) GotoLabel(kv.Key, kv.Value.Label, false); else SetFrame(kv.Key, kv.Value.Frame);
                    kv.Value.Applied = inst;
                }
            }
            catch (Exception ex)
            {
                DebugManager.Error("Flashpin: " + ex.Message);
            }
            finally { _applyingPins = false; }
        }

        /// <summary>Consumes the current mouse click so the movie will not process its release event.</summary>
        public void ConsumeClick() { _mouseDown = false; _pressBtn = null; }

        Instance InstanceOf(string path) => string.IsNullOrEmpty(path) || path == "/" ? Root : Find(path)?.Sprite;

        /// <summary>Stops the clip at the specified frame using Flash's 1-based frame numbering.</summary>
        /// <param name="path">The instance path.</param>
        /// <param name="flashFrame">The target frame number (1 is the first frame).</param>
        /// <returns><see langword="true"/> if the instance was found; otherwise, <see langword="false"/>.</returns>
        public bool SetFrame(string path, int flashFrame)
        {
            if (AutoPin) return PinFrame(path, flashFrame);
            var inst = InstanceOf(path);
            if (inst == null) return false;
            inst.Playing = false;
            GotoFrame(inst, Mathf.Clamp(flashFrame - 1, 0, System.Math.Max(0, inst.Timeline.FrameCount - 1)), true);
            return true;
        }

        /// <summary>Starts playback at the specified frame using Flash's 1-based frame numbering.</summary>
        /// <param name="path">The instance path.</param>
        /// <param name="flashFrame">The target frame number (1 is the first frame).</param>
        /// <returns><see langword="true"/> if the instance was found; otherwise, <see langword="false"/>.</returns>
        public bool PlayFrom(string path, int flashFrame)
        {
            var inst = InstanceOf(path);
            if (inst == null) return false;
            inst.Playing = true;
            GotoFrame(inst, Mathf.Clamp(flashFrame - 1, 0, System.Math.Max(0, inst.Timeline.FrameCount - 1)), true);
            return true;
        }

        /// <summary>Stops the clip at the specified frame label.</summary>
        /// <param name="path">The instance path.</param>
        /// <param name="label">The frame label to select.</param>
        /// <returns><see langword="true"/> if the label could be applied; otherwise, <see langword="false"/>.</returns>
        public bool SetLabel(string path, string label) => AutoPin ? PinLabel(path, label) : GotoLabel(path, label, false);
        /// <summary>Starts playback at the specified frame label.</summary>
        /// <param name="path">The instance path.</param>
        /// <param name="label">The frame label to play from.</param>
        /// <returns><see langword="true"/> if the label could be applied; otherwise, <see langword="false"/>.</returns>
        public bool PlayLabel(string path, string label) => GotoLabel(path, label, true);

        /// <summary>Gets the current frame using Flash's 1-based numbering.</summary>
        /// <param name="path">The instance path.</param>
        /// <returns>The current frame number, or 0 if the clip does not exist.</returns>
        public int Frame(string path) { var i = InstanceOf(path); return i != null ? i.Frame + 1 : 0; }

        /// <summary>Gets the label of the current frame or the most recent preceding label, similar to Flash's _currentlabel.</summary>
        /// <param name="path">The instance path.</param>
        /// <returns>The current or preceding frame label, or <see langword="null"/> if no label applies.</returns>
        public string Label(string path)
        {
            var i = InstanceOf(path);
            if (i?.Timeline == null) return null;
            string best = null; int bf = -1;
            foreach (var kv in i.Timeline.Labels) if (kv.Value <= i.Frame && kv.Value > bf) { bf = kv.Value; best = kv.Key; }
            return best;
        }

        /// <summary>Determines whether an instance exists at the specified path.</summary>
        /// <param name="path">The instance path.</param>
        /// <returns><see langword="true"/> if the instance exists; otherwise, <see langword="false"/>.</returns>
        public bool Exists(string path) => InstanceOf(path) != null || Find(path) != null;

        /// <summary>Gets all frame labels defined for a clip, with frame numbers converted to Flash's 1-based numbering.</summary>
        /// <param name="path">The instance path.</param>
        /// <returns>A dictionary mapping each label to its 1-based frame number.</returns>
        public Dictionary<string, int> LabelsOf(string path)
        {
            var r = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var i = InstanceOf(path);
            if (i?.Timeline != null) foreach (var kv in i.Timeline.Labels) r[kv.Key] = kv.Value + 1;
            return r;
        }

        /// <summary>Sets a movie variable to a string value.</summary>
        /// <param name="variable">The variable name.</param>
        /// <param name="text">The text to assign. A null value is treated as an empty string.</param>
        public void SetText(string variable, string text) => SetVariable(variable, text ?? "");
        /// <summary>Gets the string representation of a movie variable.</summary>
        /// <param name="variable">The variable name.</param>
        /// <returns>The variable value, or null if it is not set.</returns>
        public string GetText(string variable) => GetVariable(variable);
        /// <summary>Sets a movie variable to a numeric value.</summary>
        /// <param name="variable">The variable name.</param>
        /// <param name="value">The numeric value to assign.</param>
        public void SetNumber(string variable, double value) => SetValue(variable, value);
        /// <summary>Sets a movie variable to a Boolean value.</summary>
        /// <param name="variable">The variable name.</param>
        /// <param name="value">The Boolean value to assign.</param>
        public void SetBool(string variable, bool value) => SetValue(variable, value);

        /// <summary>Raised when a menu button hint (a_, b_, x_, or y_) is clicked.</summary>
        /// <remarks>The event value is "A", "B", "X", or "Y". If there are no subscribers, the corresponding Input call is used.</remarks>
        public event Action<string> HintClicked;
        /// <summary>Raised when a visible text field is clicked; provides the field's variable name (for example, "/:email").</summary>
        public event Action<string> TextFieldClicked;

        static readonly string[] HintNames = { "a_", "b_", "x_", "y_" };

        /// <summary>Handles a left-click that did not hit a Flash button by checking for a button hint or text field.</summary>
        /// <param name="gui">The click position in GUI coordinates.</param>
        void HandleFreeClick(Vector2 gui)
        {
            FindFreeTargets(gui, out string hint, out string field);
            if (hint != null)
            {
                if (HintClicked != null) HintClicked(hint);
                else Input(hint);
                return;
            }
            if (field != null) { FocusClicked(field); TextFieldClicked?.Invoke(field); }
        }

        /// <summary>
        /// Handles clicks when subscribers are present (for example, in a login popup).
        /// Button hints and text fields are detected even when a Flash button overlaps them.
        /// This allows fields to receive focus and hint actions to work when popup background or
        /// focus buttons would otherwise intercept the click.
        /// </summary>
        /// <param name="gui">The click position in GUI coordinates.</param>
        /// <returns>
        /// <see langword="true"/> if a button hint was handled and no Flash button should be triggered;
        /// otherwise, <see langword="false"/>.
        /// </returns>
        bool HandleSubscribedClick(Vector2 gui)
        {
            if (HintClicked == null && TextFieldClicked == null && _focusRequested == null) return false;
            FindFreeTargets(gui, out string hint, out string field);
            if (hint != null && HintClicked != null) { HintClicked(hint); return true; }
            if (field != null) { FocusClicked(field); TextFieldClicked?.Invoke(field); }
            return false;
        }

        // In the original game, the movie calls setInputFocus("/:Variable") (for example, "Search for player"),
        // after which the engine writes keyboard input into that variable. Here, keyboard input is routed to
        // the variable while its text field is visible. A blinking underscore is displayed as the caret.
        // Clicking the field restores focus. Enter and Escape remain handled by the menu (the movie's A/B actions).
        /// <summary>Gets the variable currently receiving keyboard input, or <see langword="null"/> when text input is inactive.</summary>
        public string InputFocus { get; private set; }
        /// <summary>Gets or sets the maximum number of characters accepted by the text input handler.</summary>
        public int InputMaxLength = 24;
        string _focusRequested;   // zuletzt vom Film angeforderte Variable (Klick ins Feld fokussiert sie erneut)
        float _focusSince;

        /// <summary>Sets the movie variable that receives keyboard input.</summary>
        /// <param name="variable">The variable name, or null to clear the focus.</param>
        public void SetInputFocus(string variable)
        {
            InputFocus = string.IsNullOrEmpty(variable) ? null : variable;
            if (InputFocus != null) _focusRequested = InputFocus;
            _focusSince = Time.unscaledTime;
        }

        /// <summary>Clears the current keyboard input focus.</summary>
        public void ClearInputFocus() { InputFocus = null; }

        void FocusClicked(string field)
        {
            if (_focusRequested != null && SameVar(field, _focusRequested)) SetInputFocus(_focusRequested);
        }

        static string FocusKey(string v)
        {
            v = (v ?? "").Trim();
            if (v.StartsWith("_root.", StringComparison.OrdinalIgnoreCase)) v = v.Substring(6);
            else if (v.StartsWith("/:", StringComparison.Ordinal)) v = v.Substring(2);
            else if (v.StartsWith("_level0.", StringComparison.OrdinalIgnoreCase)) v = v.Substring(8);
            return v.ToLowerInvariant();
        }

        static bool SameVar(string a, string b) => FocusKey(a) == FocusKey(b);

        /// <summary>Determines whether the text field for the currently focused variable is visible.</summary>
        bool FocusFieldVisible()
        {
            bool found = false;
            void Walk(Instance inst)
            {
                foreach (var d in inst.List.Values)
                {
                    if (found) return;
                    if (!d.Visible || d.ClipDepth != 0 || (d.Cx.Mul.w <= 0.003f && d.Cx.Add.w <= 0.003f)) continue;
                    if (d.Sprite != null) { Walk(d.Sprite); continue; }
                    if (Movie.Chars.TryGetValue(d.CharId, out var ch) && ch is VmfMovie.EditText et && et.Variable != null && SameVar(et.Variable, InputFocus)) found = true;
                }
            }
            if (Root != null) Walk(Root);
            return found;
        }

        /// <summary>Processes typed characters and writes them to the focused variable once per frame.</summary>
        void TextInputTick()
        {
            if (InputFocus == null) return;
            if (!Visible || !FocusFieldVisible())
            {
                // The field may not exist yet because its opening animation often starts immediately after focus is requested.
                if (Time.unscaledTime - _focusSince > 1.5f) InputFocus = null;
                return;
            }
            _focusSince = Time.unscaledTime;
#if ENABLE_LEGACY_INPUT_MANAGER
            string s = FlashInput.inputString;
            if (string.IsNullOrEmpty(s)) return;
            string cur = GetVariable(InputFocus) ?? "";
            bool changed = false;
            foreach (char ch in s)
            {
                if (ch == '\b') { if (cur.Length > 0) { cur = cur.Substring(0, cur.Length - 1); changed = true; } }
                else if (ch == '\n' || ch == '\r' || ch == '\t') continue;
                else if (!char.IsControl(ch) && cur.Length < InputMaxLength) { cur += ch; changed = true; }
            }
            if (changed) SetVariable(InputFocus, cur);
#endif
        }

        /// <summary>Gets the focused text field's display text with a blinking caret; returns null for normal rendering.</summary>
        /// <param name="t">The text field to render.</param>
        /// <returns>The text including the caret, or <see langword="null"/> when no caret should be displayed.</returns>
        string CaretText(VmfMovie.EditText t)
        {
            if (InputFocus == null || t?.Variable == null || !SameVar(t.Variable, InputFocus)) return null;
            if ((Time.unscaledTime % 1f) >= 0.55f) return null;
            return (GetVariable(InputFocus) ?? "") + "_";
        }

        void FindFreeTargets(Vector2 gui, out string hintOut, out string fieldOut)
        {
            // Tasten-Symbole (oberstes zuerst)
            string hint = null; int hintOrder = -1, order = 0;
            string field = null; int fieldOrder = -1;
            void Walk(Instance inst, bool visible)
            {
                foreach (var d in inst.List.Values)
                {
                    order++;
                    bool vis = visible && d.Visible && d.ClipDepth == 0 && !(d.Cx.Mul.w <= 0.003f && d.Cx.Add.w <= 0.003f);
                    if (!vis) continue;
                    if (d.Sprite != null)
                    {
                        int hi = Array.IndexOf(HintNames, d.Name ?? "");
                        if (hi >= 0)
                        {
                            var r = ScreenBounds(d);
                            if (r.HasValue && Grow(r.Value, 6f).Contains(gui) && order > hintOrder) { hintOrder = order; hint = "ABXY".Substring(hi, 1); }
                        }
                        Walk(d.Sprite, true);
                    }
                    else if (Movie.Chars.TryGetValue(d.CharId, out var ch) && ch is VmfMovie.EditText et && !string.IsNullOrEmpty(et.Variable))
                    {
                        var m = WorldOf(d.Parent) * LocalMatrix(d);
                        var a = m.Apply(new Vector2(et.Bounds.xMin, et.Bounds.yMin));
                        var b = m.Apply(new Vector2(et.Bounds.xMax, et.Bounds.yMax));
                        var rect = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
                        if (Grow(rect, 4f).Contains(gui) && order > fieldOrder) { fieldOrder = order; field = et.Variable; }
                    }
                }
            }
            Walk(Root, true);
            hintOut = hint; fieldOut = field;
        }

        static Rect Grow(Rect r, float px) => new Rect(r.x - px, r.y - px, r.width + 2 * px, r.height + 2 * px);
    }
}