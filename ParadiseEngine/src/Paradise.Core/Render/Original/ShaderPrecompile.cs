using Paradise.Core.Config;
using Paradise.Core.Diagnostics;
using Paradise.Core.Render.Vulkan;
using Silk.NET.Shaderc;

namespace Paradise.Core.Render.Original;

/// <summary>
/// Vorab-Übersetzung aller Original-Shader beim Start (wie moderne Spiele ihren Shader-Cache aufbauen):
/// .sob -> <see cref="DxsoToGlsl"/> -> shaderc -> SPIR-V, abgelegt in Dokumente/…/ShaderCache/v{Version}/&lt;Hash&gt;.spv.
/// Läuft nur, wenn der Cache fehlt oder die Übersetzer-Version sich geändert hat; danach startet das Spiel ohne Wartezeit.
/// Der Original-Renderer (<see cref="GraphicsSettings.RenderUseOriginal"/>) lädt die Shader später über <see cref="TryLoad"/>.
/// </summary>
public static class ShaderPrecompile
{
    /// <summary>Bei jeder Änderung an <see cref="DxsoToGlsl"/> erhöhen – dann wird neu übersetzt.</summary>
    public const int TranslatorVersion = 1;

    public static string CacheDir => Path.Combine(UserPaths.Root, "ShaderCache", "v" + TranslatorVersion);
    static string DoneMarker => Path.Combine(CacheDir, "complete.ok");

    static int _total, _done, _failed;
    static volatile bool _running, _finished;
    static Task? _task;

    public static int Total => _total;
    public static int Completed => _done;
    public static int Failed => _failed;
    public static bool Running => _running;
    /// <summary>Fertig (oder nichts zu tun) – das Spiel darf starten.</summary>
    public static bool Finished => _finished;
    public static float Progress => _total > 0 ? System.Math.Clamp(_done / (float)_total, 0f, 1f) : (_finished ? 1f : 0f);

    /// <summary>Muss beim Start übersetzt werden? (Cache fehlt oder ist von einer anderen Übersetzer-Version.)</summary>
    public static bool Needed()
    {
        try { return Directory.Exists(OriginalShaders.ShaderDir) && !File.Exists(DoneMarker); }
        catch { return false; }
    }

    /// <summary>Im Hintergrund starten (mehrere Threads, je ein shaderc-Compiler). Mehrfachaufruf ist harmlos.</summary>
    public static void Start()
    {
        Log.Init("shader-Precompiler");
        if (_task != null) return;
        if (!Needed()) { _finished = true; return; }
        _running = true;
        _task = Task.Run(Run);
    }

    static void Run()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(CacheDir);
            // alle Shader aller Sätze einsammeln (gleiche Bytefolgen nur einmal)
            var jobs = new List<(string Name, ulong Hash, byte[] Bytes)>();
            var seen = new HashSet<ulong>();
            foreach (var file in Directory.GetFiles(OriginalShaders.ShaderDir, "*.sob"))
            {
                string set = Path.GetFileNameWithoutExtension(file);
                foreach (var (off, bytes) in OriginalShaders.Enumerate(File.ReadAllBytes(file)))
                {
                    ulong h = Hash(bytes);
                    if (seen.Add(h)) jobs.Add((set + "@0x" + off.ToString("x"), h, bytes));
                }
            }
            _total = jobs.Count;
            Task.Delay(250000);
            var compilers = new System.Collections.Concurrent.ConcurrentBag<ShaderCompiler>();
            using var local = new ThreadLocal<ShaderCompiler>(() => { var c = new ShaderCompiler(); compilers.Add(c); return c; });
            var opts = new ParallelOptions { MaxDegreeOfParallelism = System.Math.Max(1, Environment.ProcessorCount - 6) };
            Task.Delay(500);
            Parallel.ForEach(jobs, opts, job =>
            {
                try
                {
                    string path = Path.Combine(CacheDir, job.Hash.ToString("x16") + ".spv");
                    if (!File.Exists(path))
                    {
                        if (!DxsoShader.TryParse(job.Bytes, out var sh, out var err) || sh == null) throw new Exception(err);
                        var tr = DxsoToGlsl.Translate(sh);
                        var spv = local.Value!.Compile(tr.Glsl, sh.IsPixel ? ShaderKind.FragmentShader : ShaderKind.VertexShader, job.Name);
                        File.WriteAllBytes(path, spv);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failed);
                    Log.Warn("Shader-Cache: " + job.Name + ": " + ex.Message.Split('\n')[0]);
                }
                Interlocked.Increment(ref _done);
            });
            foreach (var c in compilers) c.Dispose();
            File.WriteAllText(DoneMarker, $"{_total} Shader, {_failed} Fehler, {sw.Elapsed.TotalSeconds:0.0} s\n");
            Log.Info($"Shader-Cache: {_total} Original-Shader übersetzt ({_failed} Fehler) in {sw.Elapsed.TotalSeconds:0.0} s -> {CacheDir}");
        }
        catch (Exception ex) { Log.Error("Shader-Cache: " + ex.Message); }
        finally
        { Task.Delay(2500); _running = false; _finished = true; }
    }

    /// <summary>SPIR-V eines Original-Shaders aus dem Cache (null = nicht vorhanden).</summary>
    public static byte[]? TryLoad(byte[] d3dBytecode)
    {
        string path = Path.Combine(CacheDir, Hash(d3dBytecode).ToString("x16") + ".spv");
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; } catch { return null; }
    }

    public static ulong Hash(byte[] b) { ulong h = 0xcbf29ce484222325UL; foreach (byte x in b) { h ^= x; h *= 0x100000001b3UL; } return h; }
}
