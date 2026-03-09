#version 450

layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Normal;
layout(location = 2) in vec2 UV;

layout(location = 0) out vec3 frag_ViewNormal;
layout(location = 1) out vec2 frag_UV;

layout(set = 0, binding = 0) uniform Transforms
{
    mat4 World;
    mat4 ViewProjection;
    mat4 ViewMatrix;
};

void main()
{
    vec4 worldPos = World * vec4(Position, 1.0);
    gl_Position = ViewProjection * worldPos;

    // Normal matrix = transpose(inverse(mat3(World)))
    mat3 normalMatrix = transpose(inverse(mat3(World)));
    vec3 worldNormal = normalize(normalMatrix * Normal);
    frag_ViewNormal = normalize(mat3(ViewMatrix) * worldNormal);
    frag_UV = UV;
}
