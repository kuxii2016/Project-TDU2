namespace Paradise.Core.Render.Vulkan;

/// <summary>GLSL für den FSR3-Velocity-Durchgang (UV-Bewegung: previous uv - current uv).</summary>
internal static class FsrShaders
{
    /// <summary>Push Constants: inverse ViewProj, vorheriges ViewProj, depthParams=(M33,M43), 1/Rendergröße (GLSL-mat4-Spaltenlayout).</summary>
    internal struct VelocityPush
    {
        public System.Numerics.Matrix4x4 InvViewProj;
        public System.Numerics.Matrix4x4 PrevViewProj;
        public System.Numerics.Vector2 DepthParams;
        public System.Numerics.Vector2 InvSize;
    }

    internal const string VelocityFrag = @"#version 450
layout(set = 0, binding = 0) uniform sampler2D sDepth;
layout(push_constant) uniform PC {
    mat4 invViewProj;
    mat4 prevViewProj;
    vec2 depthParams;
    vec2 invSize;
} pc;
layout(location = 0) out vec2 outVelocity;

void main() {
    vec2 uv = gl_FragCoord.xy * pc.invSize;
    float deviceDepth = texture(sDepth, clamp(uv, vec2(0.0), vec2(1.0))).r;
    // Reversed-Z wie in der PostFx: z = M43 / (d + M33); d == 0 ist der Hintergrund.
    if (deviceDepth <= 0.0 || deviceDepth >= 1.0) { outVelocity = vec2(0.0); return; }
    // d = (-M33 * z_eye - M43) / (-z_eye) -> z_eye = M43 / (d + M33); NDC = (far+near + 2*far*near/z_eye) / (far-near).
    float eyeZ = pc.depthParams.y / (deviceDepth + pc.depthParams.x);
    float A = pc.depthParams.x;
    float ndcZ = (A + 1.0) / (A - 1.0) + (2.0 / (A - 1.0)) / eyeZ;
    // UV-Y läuft nach unten (gl_FragCoord), NDC-Y nach oben: Vorzeichen kippen.
    vec2 ndc = vec2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    vec4 world = pc.invViewProj * vec4(ndc, ndcZ, 1.0);
    if (abs(world.w) < 0.000001) { outVelocity = vec2(0.0); return; }
    world /= world.w;
    vec4 prev = pc.prevViewProj * world;
    if (prev.w <= 0.000001) { outVelocity = vec2(0.0); return; }
    vec2 prevNdc = prev.xy / prev.w;
    vec2 prevUv = vec2(prevNdc.x * 0.5 + 0.5, 0.5 - prevNdc.y * 0.5);
    outVelocity = prevUv - uv;
}";
}
