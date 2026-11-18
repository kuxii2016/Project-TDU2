#nullable disable
using Paradise.Core.Math;
using System.Globalization;

namespace Paradise.Core.Flash
{
    /// <summary>
    /// Corrections to the original Flash interfaces (incorrect positions, sizes, text), without modifying the movies.
    /// File: /Resources/FlashPatches.txt (in the build via Resources), one line per correction:
    ///   movie | path | command | values
    ///   shopsavatar | /menu_item/item_cart/#29 | offset | 4 -2         Offset in stage pixels (in addition to the timeline)
    ///   shopsavatar | /menu_item/item_cart/#29 | pos    | 15 4         fixed position (replaces _x/_y on the timeline)
    ///   shopsavatar | /menu_item/item_cart     | scale  | 1.1 1.1      scaling (factor)
    ///   shopsavatar | /menu_item/item_cart     | alpha  | 0.5          Opacity 0..1
    ///   shopsavatar | /menu_item/item_cart     | visible| 0            Hide (1 = always show)
    ///   shopsavatar | /menu_item/#3            | text   | Hello        custom text in a text field
    ///   *           | ...                                              applies to all movies
    /// movie = Name of the bank without the extension (shopsavatar, home, hud …). Paths as in the Flash Debugger (“#n” = unnamed, depth n).
    /// The corrections are reapplied after every frame (even if the timeline recreates the object).
    /// Lines beginning with # are comments.
    /// </summary>
    public sealed partial class VmfPlayer
    {
        public sealed class Patch
        {
            public string Film, Path, Command, Value;
            public override string ToString() => $"{Film} | {Path} | {Command} | {Value}";
        }

        public const string PatchResource = "FlashPatches";
        static List<Patch> _allPatches;
        static int _patchVersion;
        List<Patch> _myPatches; int _myPatchVersion = -1;

        /// <summary>All corrections (from Resources/FlashPatches.txt, imported directly from the file into the editor).</summary>
        public static List<Patch> AllPatches
        {
            get
            {
                if (_allPatches == null) ReloadPatches();
                return _allPatches;
            }
        }

        /// <summary>Reload file (Flash Debugger after saving).</summary>
        public static void ReloadPatches()
        {
            string text = null;
            try { if (File.Exists(PatchFileEditor)) text = File.ReadAllText(PatchFileEditor); } catch (Exception) { }
            _allPatches = ParsePatches(text);
            _patchVersion++;
        }

        /// <summary>Path to the patch file (next to the exe).</summary>
        public static string PatchFileEditor => System.IO.Path.Combine(Paradise.Core.Config.AppPaths.ExeDir, PatchResource + ".txt");

        public static List<Patch> ParsePatches(string text)
        {
            var list = new List<Patch>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                var p = line.Split(new[] { '|' }, 4);
                if (p.Length < 3) continue;
                list.Add(new Patch { Film = p[0].Trim(), Path = p[1].Trim(), Command = p[2].Trim().ToLowerInvariant(), Value = p.Length > 3 ? p[3].Trim() : "" });
            }
            return list;
        }

        /// <summary>Name of the movie for the corrections (file name without extension, e.g., “shopsavatar”).</summary>
        public string FilmName => string.IsNullOrEmpty(BankPath) ? "" : System.IO.Path.GetFileNameWithoutExtension(BankPath);

        List<Patch> MyPatches()
        {
            var all = AllPatches;
            if (_myPatches != null && _myPatchVersion == _patchVersion) return _myPatches;
            _myPatches = new List<Patch>();
            string film = FilmName;
            foreach (var p in all)
                if (p.Film == "*" || string.Equals(p.Film, film, StringComparison.OrdinalIgnoreCase)) _myPatches.Add(p);
            _myPatchVersion = _patchVersion;
            return _myPatches;
        }

        void ApplyPatches()
        {
            var list = MyPatches();
            if (list.Count == 0) return;
            foreach (var p in list)
            {
                var d = Find(p.Path);
                if (d != null) ApplyPatch(d, p.Command, p.Value);
            }
        }

        static float F(string s, float fallback = 0f) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        static Vector2 V2(string s, float fallback = 0f)
        {
            var p = (s ?? "").Split(new[] { ' ', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            float x = p.Length > 0 ? F(p[0], fallback) : fallback;
            float y = p.Length > 1 ? F(p[1], x) : x;
            return new Vector2(x, y);
        }

        /// <summary>Apply a correction to an object (even directly from the debugger).</summary>
        public static void ApplyPatch(DisplayObject d, string command, string value)
        {
            if (d == null) return;
            switch ((command ?? "").ToLowerInvariant())
            {
                case "offset": d.Offset = V2(value); break;
                case "pos": d.Position = V2(value); break;
                case "scale": d.ScaleMul = V2(value, 1f); break;
                case "alpha": d.Cx.Mul.w = Mathf.Clamp01(F(value, 1f)); d.ScriptCx = true; break;
                case "visible": d.Visible = value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase); break;
                case "text": d.TextOverride = (value ?? "").Replace("\\n", "\n"); break;
            }
        }

        /// <summary>Offset in addition to the timeline (stage pixels in the parent clip).</summary>
        public void SetOffset(string path, Vector2 offset) { var d = Find(path); if (d != null) d.Offset = offset; }
        /// <summary>Scaling (factor per axis) in addition to the timeline.</summary>
        public void SetScale(string path, Vector2 scale) { var d = Find(path); if (d != null) d.ScaleMul = scale; }
        /// <summary>Opacity 0..1 (such as _alpha / 100).</summary>
        public void SetAlpha(string path, float alpha) { var d = Find(path); if (d != null) { d.Cx.Mul.w = Mathf.Clamp01(alpha); d.ScriptCx = true; } }
        /// <summary>Set the text of a text field directly (even without a variable); null = variable/initial text.</summary>
        public void SetObjectText(string path, string text) { var d = Find(path); if (d != null) d.TextOverride = text; }
    }
}