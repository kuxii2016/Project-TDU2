using EdenSavegame;
using Paradise.Core.Config;
using Paradise.Core.Diagnostics;
using Paradise.Core.Online;
using Paradise.Core.Savegame;
using System.Text.Json;

namespace Paradise.Game.Screens;

/// <summary>
/// Entwicklermenü (F1) > Savegame Debug: Game-Content-Werkzeuge, die es im Unity-Original so gab bzw. die zum
/// Investigieren des Spielstands fehlten – Save/Load, Live-Daten als Baum, JSON- und XMBF-Export/Import.
/// Dateien liegen unter Documentation/KuxiiSoft/Test Drive Unlimited 2/Saves/Debug und überschreiben nichts im Spiel.
/// Der Import ersetzt nie den Server-Spielstand, sondern nur die laufende Sitzung; gespeichert wird erst mit SaveAsync.
/// </summary>
public sealed partial class DevMenu
{
    const int SgPageSize = 25;
    const int SgMaxDepth = 6;

    /// <summary>Gecachte Baum-Ansichten (Rebuild() baut das Menü jedes Bild neu – keine Datei-Leseaktion pro Frame).</summary>
    JsonDocument? _sgLiveDoc, _sgFileDoc;
    XObject? _sgXmbfRoot;
    string _sgViewInfo = "no data loaded";

    static readonly JsonSerializerOptions SgPrettyJson = new()
    {
        IncludeFields = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString |
                         System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    static string SgNick => string.IsNullOrEmpty(TDU2Session.Nick) ? "_offline" : TDU2Session.Nick;
    static string SgDebugDir => Path.Combine(UserPaths.Saves, "Debug");
    static string SgFile(string ext, string suffix = "") => Path.Combine(SgDebugDir, SafeFile(SgNick) + suffix + "." + ext);
    static string SgJsonPath => SgFile("json");
    static string SgXmbfPath => SgFile("xmbf");
    static string SgBuiltXmbfPath => SgFile("xmbf", ".live");
    static string SgDataBackupPath => SgFile("DATA", ".backup");

    static string SafeFile(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    static string SgFileInfo(string path)
    {
        try
        {
            return File.Exists(path)
                ? new FileInfo(path).Length.ToString("N0", Inv) + " bytes · " + File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss", Inv)
                : "none";
        }
        catch (Exception e) { return e.Message; }
    }

    // ------------------------------------------------------------------ Hauptseite

    List<Item> SavegameDebugMenu()
    {
        var l = new List<Item>
        {
            Head("PROFILE / FILES"),
            Info("Profile", () => TDU2Session.Nick ?? "-"),
            Info("Session", () => TDU2Session.LoggedIn ? (TDU2Session.Offline ? "offline profile" : "server · session " + ShortId(TDU2Session.SessionId)) : "not logged in"),
            Info("Token", () => string.IsNullOrEmpty(TDU2Session.Token) ? "-" : "present (" + TDU2Session.Token.Length + " chars)"),
            Info("Local DATA", () => SgFileInfo(TDU2SaveGame.PathFor(SgNick))),
            Info("JSON export", () => SgFileInfo(SgJsonPath)),
            Info("XMBF export", () => SgFileInfo(SgXmbfPath)),
            Info("DATA backup", () => SgFileInfo(SgDataBackupPath)),
            Info("Debug folder", () => SgDebugDir),

            Head("SAVE / LOAD"),
            Action_("Save to server + local DATA", () =>
            {
                if (!TDU2Session.LoggedIn) { Toast("Not logged in"); return; }
                TDU2Session.SaveAsync();
                Toast("Saving game (server + local DATA)");
            }),
            Action_("Write local DATA now (from live SaveData)", () =>
            {
                if (TDU2Session.Save == null) { Toast("No savegame loaded"); return; }
                if (Mgr?.Database == null) { Toast("Car database not loaded"); return; }
                TDU2SaveGame.WriteAsync(SgNick, TDU2Session.Save, Mgr.Database, "savegame debug");
                Toast("Writing " + TDU2SaveGame.PathFor(SgNick));
            }),
            Action_("Import local DATA -> session (keeps server-only fields)", () =>
            {
                if (Mgr?.Database == null) { Toast("Car database not loaded"); return; }
                if (!TDU2SaveGame.TryRead(SgNick, Mgr.Database, out var s, out var why)) { Toast("DATA not readable: " + why); return; }
                if (TDU2Session.Save != null) TDU2SaveGame.KeepServerOnly(s, TDU2Session.Save);
                TDU2Session.ApplySave(s);
                RefreshSgViews();
                Toast("DATA imported: $" + s.money + ", " + s.ownedCars.Count + " cars, island " + s.island + " (not saved to server)");
            }),
            Action_("Read local DATA (log only, no import)", () =>
            {
                if (Mgr?.Database == null) { Toast("Car database not loaded"); return; }
                if (TDU2SaveGame.TryRead(SgNick, Mgr.Database, out var s, out var why))
                {
                    string info = "DATA: $" + s.money + ", " + s.ownedCars.Count + " cars, " + s.island + ", story " + s.story + ", interior '" + s.interior + "'";
                    Log.Info(info); Toast(info);
                }
                else Toast("DATA not readable: " + why);
            }),

            Head("EXPORT"),
            Action_("Export live SaveData -> JSON", () =>
            {
                if (TDU2Session.Save == null) { Toast("No savegame loaded"); return; }
                try
                {
                    Directory.CreateDirectory(SgDebugDir);
                    File.WriteAllText(SgJsonPath, JsonSerializer.Serialize(TDU2Session.Save, SgPrettyJson));
                    RefreshSgViews();
                    Toast("JSON exported: " + SgJsonPath);
                }
                catch (Exception e) { Toast("JSON export failed: " + e.Message); }
            }),
            Action_("Export local DATA -> decrypted XMBF", () =>
            {
                if (!TDU2SaveGame.Exists(SgNick)) { Toast("No local DATA"); return; }
                try
                {
                    Directory.CreateDirectory(SgDebugDir);
                    var dec = SaveCrypto.Decrypt(File.ReadAllBytes(TDU2SaveGame.PathFor(SgNick)), SgNick, TDU2SaveGame.FileName, "PLAYERSAVE");
                    File.WriteAllBytes(SgXmbfPath, dec.Xmbf);
                    _sgXmbfRoot = Xmbf.Decode(dec.Xmbf).Root;
                    Toast("XMBF exported: " + SgXmbfPath + " (" + dec.Xmbf.Length + " bytes)");
                }
                catch (Exception e) { Toast("XMBF export failed: " + e.Message); }
            }),
            Action_("Build XMBF from live SaveData -> file", () =>
            {
                if (TDU2Session.Save == null || Mgr?.Database == null) { Toast("Savegame or car database missing"); return; }
                try
                {
                    Directory.CreateDirectory(SgDebugDir);
                    byte[] xmbf = TDU2SaveGame.Build(SgNick, TDU2Session.Save, Mgr.Database, out string info);
                    File.WriteAllBytes(SgBuiltXmbfPath, xmbf);
                    Toast("XMBF built (" + xmbf.Length + " bytes): " + info);
                }
                catch (Exception e) { Toast("XMBF build failed: " + e.Message); }
            }),
            Action_("Copy local DATA -> .DATA backup", () =>
            {
                if (!TDU2SaveGame.Exists(SgNick)) { Toast("No local DATA"); return; }
                try
                {
                    Directory.CreateDirectory(SgDebugDir);
                    File.Copy(TDU2SaveGame.PathFor(SgNick), SgDataBackupPath, true);
                    Toast("DATA backup: " + SgDataBackupPath);
                }
                catch (Exception e) { Toast("DATA backup failed: " + e.Message); }
            }),

            Head("IMPORT"),
            Action_("Import exported JSON -> session", () =>
            {
                if (!File.Exists(SgJsonPath)) { Toast("No JSON export: " + SgJsonPath); return; }
                try
                {
                    var s = NetJson.FromJson<SaveData>(File.ReadAllText(SgJsonPath));
                    if (s == null) { Toast("JSON is empty"); return; }
                    TDU2Session.ApplySave(s);
                    RefreshSgViews();
                    Toast("JSON imported: $" + s.money + ", " + s.ownedCars.Count + " cars (not saved to server)");
                }
                catch (Exception e) { Toast("JSON import failed: " + e.Message); }
            }),
            Action_("Import exported XMBF -> DATA + session", () =>
            {
                if (!File.Exists(SgXmbfPath)) { Toast("No XMBF export: " + SgXmbfPath); return; }
                try
                {
                    byte[] xmbf = File.ReadAllBytes(SgXmbfPath);
                    var doc = Xmbf.Decode(xmbf);   // validity check before overwriting the DATA
                    byte[] enc = SaveCrypto.Encrypt(xmbf, SgNick, TDU2SaveGame.FileName, null, "PLAYERSAVE");
                    Directory.CreateDirectory(Path.GetDirectoryName(TDU2SaveGame.PathFor(SgNick)));
                    File.WriteAllBytes(TDU2SaveGame.PathFor(SgNick), enc);
                    _sgXmbfRoot = doc.Root;
                    string why = "car database not loaded";
                    if (Mgr?.Database != null && TDU2SaveGame.TryRead(SgNick, Mgr.Database, out var s, out why))
                    {
                        if (TDU2Session.Save != null) TDU2SaveGame.KeepServerOnly(s, TDU2Session.Save);
                        TDU2Session.ApplySave(s);
                        Toast("XMBF imported: " + s.ownedCars.Count + " cars → DATA + session");
                    }
                    else Toast("XMBF imported to DATA (session not reloaded: " + why + ")");
                    RefreshSgViews();
                }
                catch (Exception e) { Toast("XMBF import failed: " + e.Message); }
            }),

            Head("VIEW DATA"),
            Menu("View live SaveData (JSON tree)", ViewLiveSaveDataMenu),
            Menu("View JSON export (JSON tree)", ViewJsonExportMenu),
            Menu("View local DATA (XMBF tree)", ViewXmbfMenu),
            Action_("Refresh data views", () => { RefreshSgViews(); Toast("Data views refreshed"); }),
            Info("Last view status", () => _sgViewInfo),
            Head(""),
            Info("JSON = server/NetJson format · XMBF = original unencrypted PLAYERSAVE/DATA"),
            Info("Export/import only changes the live session and local files – use Save to server to persist."),
        };
        return l;
    }

    static string ShortId(string id) => string.IsNullOrEmpty(id) ? "-" : id.Length <= 8 ? id : id.Substring(0, 8) + "…";

    // ------------------------------------------------------------------ Snapshots

    void RefreshSgViews()
    {
        _sgLiveDoc?.Dispose(); _sgLiveDoc = null;
        _sgFileDoc?.Dispose(); _sgFileDoc = null;
        _sgXmbfRoot = null;
        _sgViewInfo = "no data loaded";
        try
        {
            if (TDU2Session.Save != null)
            {
                _sgLiveDoc = JsonDocument.Parse(NetJson.ToJson(TDU2Session.Save));
                _sgViewInfo = "live SaveData loaded";
            }
        }
        catch (Exception e) { _sgViewInfo = "live JSON: " + e.Message; }
        try
        {
            if (File.Exists(SgJsonPath)) _sgFileDoc = JsonDocument.Parse(File.ReadAllText(SgJsonPath));
        }
        catch (Exception e) { _sgViewInfo = "export JSON: " + e.Message; }
        try
        {
            if (TDU2SaveGame.Exists(SgNick) && Mgr?.Database != null)
            {
                byte[] file = File.ReadAllBytes(TDU2SaveGame.PathFor(SgNick));
                var dec = SaveCrypto.Decrypt(file, SgNick, TDU2SaveGame.FileName, "PLAYERSAVE");
                _sgXmbfRoot = Xmbf.Decode(dec.Xmbf).Root;
            }
        }
        catch (Exception e) { _sgViewInfo = "local DATA: " + e.Message; }
    }

    // ------------------------------------------------------------------ JSON-Baum

    List<Item> ViewLiveSaveDataMenu()
    {
        if (_sgLiveDoc == null) RefreshSgViews();
        var l = new List<Item> { Head("LIVE SAVEDATA · " + SgNick) };
        if (_sgLiveDoc == null) { l.Add(Info(_sgViewInfo)); return l; }
        AddJsonItems(l, _sgLiveDoc.RootElement, 0);
        return l;
    }

    List<Item> ViewJsonExportMenu()
    {
        if (_sgFileDoc == null) RefreshSgViews();
        var l = new List<Item> { Head("JSON EXPORT · " + SgJsonPath) };
        if (_sgFileDoc == null) { l.Add(Info(_sgViewInfo)); return l; }
        AddJsonItems(l, _sgFileDoc.RootElement, 0);
        return l;
    }

    List<Item> JsonNodeMenu(JsonElement e, string title, int depth)
    {
        var l = new List<Item> { Head(title.ToUpperInvariant()) };
        AddJsonItems(l, e, depth);
        return l;
    }

    void AddJsonItems(List<Item> l, JsonElement e, int depth)
    {
        if (depth > SgMaxDepth) { l.Add(Info("[max depth reached]")); return; }
        if (e.ValueKind == JsonValueKind.Object)
        {
            var props = new List<JsonProperty>();
            foreach (var p in e.EnumerateObject()) props.Add(p);
            if (props.Count <= SgPageSize)
            {
                foreach (var p in props) AddJsonEntry(l, p.Name, p.Value, depth);
                return;
            }
            for (int from = 0; from < props.Count; from += SgPageSize)
            {
                int f = from, t = Math.Min(from + SgPageSize, props.Count);
                l.Add(Menu("Fields " + (f + 1) + "–" + t + " / " + props.Count, () => JsonFieldsMenu(props, f, t, depth)));
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            var items = new List<JsonElement>();
            foreach (var x in e.EnumerateArray()) items.Add(x);
            if (items.Count <= SgPageSize)
            {
                for (int i = 0; i < items.Count; i++) AddJsonEntry(l, "[" + i + "]", items[i], depth);
                return;
            }
            for (int from = 0; from < items.Count; from += SgPageSize)
            {
                int f = from, t = Math.Min(from + SgPageSize, items.Count);
                l.Add(Menu("Items " + (f + 1) + "–" + t + " / " + items.Count, () => JsonItemsMenu(items, f, t, depth)));
            }
        }
        else l.Add(Info("Value", () => JsonScalar(e)));
    }

    List<Item> JsonFieldsMenu(List<JsonProperty> props, int from, int to, int depth)
    {
        var l = new List<Item> { Head("FIELDS " + (from + 1) + "–" + to) };
        for (int i = from; i < to; i++) AddJsonEntry(l, props[i].Name, props[i].Value, depth);
        return l;
    }

    List<Item> JsonItemsMenu(List<JsonElement> items, int from, int to, int depth)
    {
        var l = new List<Item> { Head("ITEMS " + (from + 1) + "–" + to) };
        for (int i = from; i < to; i++) AddJsonEntry(l, "[" + i + "]", items[i], depth);
        return l;
    }

    void AddJsonEntry(List<Item> l, string name, JsonElement e, int depth)
    {
        if (e.ValueKind == JsonValueKind.Object || e.ValueKind == JsonValueKind.Array)
        {
            int n = e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : CountJsonFields(e);
            string kind = e.ValueKind == JsonValueKind.Array ? "array" : "object";
            l.Add(Menu(name + "  (" + kind + ", " + n + ")", () => JsonNodeMenu(e, name, depth + 1)));
        }
        else l.Add(Info(name, () => JsonScalar(e)));
    }

    static int CountJsonFields(JsonElement e)
    {
        int n = 0;
        foreach (var _ in e.EnumerateObject()) n++;
        return n;
    }

    static string JsonScalar(JsonElement e)
    {
        string s = e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            JsonValueKind.Undefined => "undefined",
            _ => e.GetRawText(),
        };
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= 140 ? s : s.Substring(0, 140) + "…";
    }

    // ------------------------------------------------------------------ XMBF-Baum

    List<Item> ViewXmbfMenu()
    {
        if (_sgXmbfRoot == null) RefreshSgViews();
        var l = new List<Item> { Head("LOCAL DATA · XMBF TREE") };
        if (_sgXmbfRoot == null) { l.Add(Info(_sgViewInfo)); return l; }
        AddXItems(l, _sgXmbfRoot, 0);
        return l;
    }

    List<Item> XNodeMenu(XValue v, string title, int depth)
    {
        var l = new List<Item> { Head(title.ToUpperInvariant()) };
        AddXItems(l, v, depth);
        return l;
    }

    void AddXItems(List<Item> l, XValue v, int depth)
    {
        if (depth > SgMaxDepth) { l.Add(Info("[max depth reached]")); return; }
        if (v is XObject o)
        {
            var keys = new List<string>(o.Keys);
            if (keys.Count <= SgPageSize)
            {
                foreach (string key in keys) AddXEntry(l, key, o[key]!, depth);
                return;
            }
            for (int from = 0; from < keys.Count; from += SgPageSize)
            {
                int f = from, t = Math.Min(from + SgPageSize, keys.Count);
                l.Add(Menu("Fields " + (f + 1) + "–" + t + " / " + keys.Count, () => XFieldsMenu(keys, o, f, t, depth)));
            }
        }
        else if (v is XArray a)
        {
            if (a.Items.Count <= SgPageSize)
            {
                for (int i = 0; i < a.Items.Count; i++) AddXEntry(l, "[" + i + "]", a.Items[i], depth);
                return;
            }
            for (int from = 0; from < a.Items.Count; from += SgPageSize)
            {
                int f = from, t = Math.Min(from + SgPageSize, a.Items.Count);
                l.Add(Menu("Items " + (f + 1) + "–" + t + " / " + a.Items.Count, () => XItemsMenu(a, f, t, depth)));
            }
        }
        else if (v is XPrim p) l.Add(Info("Value (" + p.TypeName + ")", () => p.Display));
    }

    List<Item> XFieldsMenu(List<string> keys, XObject o, int from, int to, int depth)
    {
        var l = new List<Item> { Head("FIELDS " + (from + 1) + "–" + to) };
        for (int i = from; i < to; i++) AddXEntry(l, keys[i], o[keys[i]]!, depth);
        return l;
    }

    List<Item> XItemsMenu(XArray a, int from, int to, int depth)
    {
        var l = new List<Item> { Head("ITEMS " + (from + 1) + "–" + to) };
        for (int i = from; i < to; i++) AddXEntry(l, "[" + i + "]", a.Items[i], depth);
        return l;
    }

    void AddXEntry(List<Item> l, string name, XValue v, int depth)
    {
        if (v is XObject || v is XArray)
            l.Add(Menu(name + "  " + v.Display, () => XNodeMenu(v, name, depth + 1)));
        else
            l.Add(Info(name + " : " + v.TypeName, () => v.Display));
    }
}
