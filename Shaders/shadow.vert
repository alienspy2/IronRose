#version 450

layout(set = 0, binding = 0) uniform ShadowTransforms
{
    mat4 LightMVP;
    vec4 DepthParams;   // x=useLinearDepth(1/0), y=near, z=far
};

layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Normal;    // unused but must match vertex layout stride
layout(location = 2) in vec2 UV;        // unused but must match vertex layout stride

layout(location = 0) out float vViewZ;  // clip-space W = view-space Z for perspective

void main()
{
    gl_Position = LightMVP * vec4(Position, 1.0);
    vViewZ = gl_Position.w;
}
