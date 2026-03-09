#version 450

layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Normal;
layout(location = 2) in vec2 UV;

layout(set = 0, binding = 0) uniform Transforms
{
    mat4 World;
    mat4 ViewProjection;
    mat4 PrevViewProjection;
};

layout(location = 0) out vec3 fsin_Normal;
layout(location = 1) out vec2 fsin_UV;
layout(location = 2) out vec3 fsin_WorldPos;
layout(location = 3) out vec4 fsin_CurrClip;
layout(location = 4) out vec4 fsin_PrevClip;

void main()
{
    vec4 worldPos = World * vec4(Position, 1.0);
    vec4 currClip = ViewProjection * worldPos;
    gl_Position = currClip;

    fsin_Normal = normalize(mat3(World) * Normal);
    fsin_UV = UV;
    fsin_WorldPos = worldPos.xyz;
    fsin_CurrClip = currClip;
    fsin_PrevClip = PrevViewProjection * worldPos;
}
