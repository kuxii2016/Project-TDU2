using Silk.NET.Shaderc;
using System.Runtime.InteropServices;
using System.Text;

namespace Paradise.Core.Render.Vulkan;

/// <summary>
/// Übersetzt GLSL zur Laufzeit nach SPIR-V (via shaderc). So bleiben die Shader
/// lesbar im Projekt, und es wird kein Vulkan-SDK zum Bauen gebraucht. Das
/// native shaderc wird über Silk.NET.Shaderc.Native geliefert und in die
/// Single-File-exe eingebettet.
/// </summary>
public sealed unsafe class ShaderCompiler : IDisposable
{
    private readonly Shaderc _sc;
    private readonly Compiler* _compiler;

    public ShaderCompiler()
    {
        _sc = Shaderc.GetApi();
        _compiler = _sc.CompilerInitialize();
        if (_compiler == null) throw new Exception("shaderc: The compiler could not be initialized.");
    }

    public byte[] Compile(string source, ShaderKind kind, string name)
    {

        byte[] src = Encoding.UTF8.GetBytes(source);
        byte[] nm = Encoding.UTF8.GetBytes(name + "\0");
        byte[] entry = Encoding.UTF8.GetBytes("main\0");

        fixed (byte* pSrc = src)
        fixed (byte* pName = nm)
        fixed (byte* pEntry = entry)
        {
            CompilationResult* result = _sc.CompileIntoSpv(
                _compiler, pSrc, (nuint)src.Length, kind, pName, pEntry, null);
            try
            {
                if (_sc.ResultGetCompilationStatus(result) != CompilationStatus.Success)
                {
                    string msg = Marshal.PtrToStringAnsi((nint)_sc.ResultGetErrorMessage(result)) ?? "unbekannt";
                    throw new Exception($"Shader ‘{name}’ compilation error:\n{msg}");
                }
                int len = (int)_sc.ResultGetLength(result);
                var spirv = new byte[len];
                Marshal.Copy((nint)_sc.ResultGetBytes(result), spirv, 0, len);
                return spirv;
            }
            finally { _sc.ResultRelease(result); }
        }
    }

    public void Dispose()
    {
        if (_compiler != null) _sc.CompilerRelease(_compiler);
        _sc.Dispose();
    }
}