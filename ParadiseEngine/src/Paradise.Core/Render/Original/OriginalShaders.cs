using Paradise.Core.Config;
using Paradise.Core.Diagnostics;
using Paradise.Core.Flash;
using Paradise.Core.Render.Vulkan;
using Silk.NET.Shaderc;
using System.Text;

namespace Paradise.Core.Render.Original;

/// <summary>
/// Original-Shader-Sätze (Euro/Shaders/*.sob) laden und übersetzen – Grundlage des Original-Renderers
/// (<see cref="GraphicsSettings.RenderUseOriginal"/>). Stufe 1: alle Shader finden, nach GLSL übersetzen, mit shaderc kompilieren
/// und das Ergebnis protokollieren. Die Zuordnung Material -> Shader (Spec-Hash, tdu2-specs §6.2/6.3) und das Füllen der
/// Konstanten (OpenTDU2 eden_renderer fillConstants) folgen in den nächsten Stufen.
/// </summary>
public static class OriginalShaders
{
    public static string ShaderDir => Path.Combine(Path.GetDirectoryName(FlashHost.BnkDir.TrimEnd('\\', '/')) ?? "", "Shaders");

    /// <summary>Letztes Ergebnis von <see cref="SelfTest"/> (DevMenu).</summary>
    public static volatile string Status = "nicht gestartet";

    /// <summary>Alle Shader eines Satzes (Suche nach Versions-Token mit folgender CTAB, doppelte Bytefolgen einmal).</summary>
    public static List<(int Offset, byte[] Bytes)> Enumerate(byte[] sob)
    {
        var list = new List<(int, byte[])>();
        var seen = new HashSet<ulong>();
        for (int o = 0; o + 16 <= sob.Length; o += 4)
        {
            uint v = BitConverter.ToUInt32(sob, o);
            if (v != 0xFFFF0300 && v != 0xFFFE0300) continue;
            uint c = BitConverter.ToUInt32(sob, o + 4);
            if ((c & 0xFFFF) != 0xFFFE || BitConverter.ToUInt32(sob, o + 8) != 0x42415443) continue;
            int size = DxsoShader.Size(sob.AsSpan(o));
            if (size <= 0) continue;
            var b = sob.AsSpan(o, size).ToArray();
            if (seen.Add(Hash(b))) list.Add((o, b));
            o += size - 4;
        }
        return list;
    }

    static ulong Hash(byte[] b) { ulong h = 0xcbf29ce484222325UL; foreach (byte x in b) { h ^= x; h *= 0x100000001b3UL; } return h; }

    /// <summary>
    /// Alle Shader aller Sätze übersetzen und kompilieren. Bericht: Logs/original_shaders_*.txt (Fehler mit GLSL-Ausschnitt).
    /// Läuft im Hintergrund (eigener shaderc-Compiler), dauert je nach Rechner einige Minuten.
    /// </summary>
    public static void SelfTest()
    {
        string dir = ShaderDir;
        if (!Directory.Exists(dir)) { Status = "folder missing: " + dir; Log.Warn("Original-Shader: " + Status); return; }
        var report = new StringBuilder();
        int total = 0, ok = 0, failT = 0, failC = 0;
        var opMiss = new Dictionary<string, int>();
        using var compiler = new ShaderCompiler();
        var files = Directory.GetFiles(dir, "*.sob").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        int fi = 0;
        foreach (var file in files)
        {
            fi++;
            string set = Path.GetFileNameWithoutExtension(file);
            int setOk = 0, setN = 0;
            foreach (var (off, bytes) in Enumerate(File.ReadAllBytes(file)))
            {
                total++; setN++;
                Status = $"{fi}/{files.Length} {set}: {setN} …";
                string name = set + "@0x" + off.ToString("x");
                if (!DxsoShader.TryParse(bytes, out var sh, out var perr) || sh == null) { failT++; report.Append("PARSE ").Append(name).Append(": ").Append(perr).Append('\n'); continue; }
                DxsoToGlsl.Result tr;
                try { tr = DxsoToGlsl.Translate(sh); }
                catch (Exception ex)
                {
                    failT++;
                    opMiss[ex.Message] = opMiss.TryGetValue(ex.Message, out int c) ? c + 1 : 1;
                    if (opMiss[ex.Message] <= 3) report.Append("TRANSLATE ").Append(name).Append(": ").Append(ex.Message).Append('\n');
                    continue;
                }
                try
                {
                    compiler.Compile(tr.Glsl, sh.IsPixel ? ShaderKind.FragmentShader : ShaderKind.VertexShader, name + (sh.IsPixel ? ".frag" : ".vert"));
                    ok++; setOk++;
                }
                catch (Exception ex)
                {
                    failC++;
                    if (failC <= 40) report.Append("COMPILE ").Append(name).Append(":\n").Append(ex.Message).Append("\n--- GLSL ---\n").Append(tr.Glsl).Append("\n------------\n");
                }
            }
            report.Insert(0, $"{set}: {setOk}/{setN}\n");
        }
        string sum = $"{ok}/{total} shaders translated and compiled, {failT} not translatable, {failC} compiler errors";
        foreach (var kv in opMiss.OrderByDescending(k => k.Value)) sum += $"\n  {kv.Value}x {kv.Key}";
        try
        {
            string logDir = UserPaths.Logs;
            string path = Path.Combine(logDir, "original_shaders_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            File.WriteAllText(path, sum + "\n\n" + report);
            Status = sum.Split('\n')[0] + " – " + Path.GetFileName(path);
        }
        catch (Exception ex) { Status = sum.Split('\n')[0] + " - report not written: " + ex.Message; }
    }
}
