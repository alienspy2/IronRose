#version 450

layout(location = 0) in vec3 fragWorldPos;
layout(location = 0) out float outDepth;

layout(set = 0, binding = 0) uniform ShadowPointTransforms
{
    mat4 LightMVP;
    mat4 World;
    vec4 LightPosAndFarPlane;   // xyz = light position, w = far plane (range)
};

void main()
{
    float dist = length(fragWorldPos - LightPosAndFarPlane.xyz);
    outDepth = dist / LightPosAndFarPlane.w;
}
