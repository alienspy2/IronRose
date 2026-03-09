#version 450

layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Normal;
layout(location = 2) in vec2 UV;

layout(location = 0) out vec3 frag_Normal;
layout(location = 1) out vec2 frag_UV;
layout(location = 2) out vec3 frag_WorldPos;

layout(set = 0, binding = 0) uniform Transforms
{
    mat4 World;
    mat4 ViewProjection;
};

void main()
{
    vec4 worldPos = World * vec4(Position, 1.0);
    gl_Position = ViewProjection * worldPos;

    // Transform normal to world space (ignoring non-uniform scale for simplicity)
    frag_Normal = mat3(World) * Normal;
    frag_UV = UV;
    frag_WorldPos = worldPos.xyz;
}
