#version 450

// Shadow atlas fragment shader — writes depth moments for Variance Shadow Maps.
// R = depth, G = depth².  Used for all shadow types (Directional, Spot, Point).
// Supports linear depth for perspective projections (better precision at distance).

layout(location = 0) in float vViewZ;   // from vertex shader: gl_Position.w

layout(set = 0, binding = 0) uniform ShadowTransforms
{
    mat4 LightMVP;
    vec4 DepthParams;   // x=useLinearDepth(1/0), y=near, z=far
};

layout(location = 0) out vec2 outMoments;

void main()
{
    float depth;
    if (DepthParams.x > 0.5)
    {
        // Linear depth: normalize view-space Z to [0,1]
        float near = DepthParams.y;
        float far  = DepthParams.z;
        depth = (vViewZ - near) / (far - near);
    }
    else
    {
        // Orthographic (directional): gl_FragCoord.z is already linear
        depth = gl_FragCoord.z;
    }
    outMoments = vec2(depth, depth * depth);
}
