#version 450

layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Normal;    // unused but must match vertex layout stride
layout(location = 2) in vec2 UV;        // unused but must match vertex layout stride

struct LightInfo
{
    vec4 PositionOrDirection;
    vec4 ColorIntensity;
    vec4 Params;
    vec4 SpotDirection;
};

layout(set = 1, binding = 0) uniform LightVolumeData
{
    mat4 WorldViewProjection;
    mat4 LightViewProjection;
    vec4 CameraPos;
    vec4 ScreenParams;
    vec4 ShadowParams;
    vec4 ShadowAtlasParams;
    LightInfo Light;
};

void main()
{
    gl_Position = WorldViewProjection * vec4(Position, 1.0);
}
