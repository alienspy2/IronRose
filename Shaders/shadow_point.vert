#version 450

layout(set = 0, binding = 0) uniform ShadowPointTransforms
{
    mat4 LightMVP;
    mat4 World;
    vec4 LightPosAndFarPlane;   // xyz = light position, w = far plane (range)
};

layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Normal;    // unused but must match vertex layout stride
layout(location = 2) in vec2 UV;        // unused but must match vertex layout stride

layout(location = 0) out vec3 fragWorldPos;

void main()
{
    fragWorldPos = (World * vec4(Position, 1.0)).xyz;
    gl_Position = LightMVP * vec4(Position, 1.0);
}
