#version 450

layout(location = 0) out vec4 out_Color;

layout(set = 0, binding = 1) uniform MaterialData
{
    vec4 Color;
    float HasTexture;
    float _pad1;
    float _pad2;
    float _pad3;
};

void main()
{
    out_Color = Color;
}
