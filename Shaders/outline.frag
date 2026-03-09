#version 450

layout(location = 0) out vec4 out_Color;

layout(set = 0, binding = 0) uniform OutlineData
{
    mat4 World;
    mat4 ViewProjection;
    vec4 OutlineColor;
    vec4 CameraPos;
    float OutlineWidth;
    float _pad1;
    float _pad2;
    float _pad3;
};

void main()
{
    out_Color = OutlineColor;
}
