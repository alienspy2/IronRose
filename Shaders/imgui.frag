#version 450

layout(location = 0) in vec2 fsin_TexCoord;
layout(location = 1) in vec4 fsin_Color;

layout(location = 0) out vec4 fsout_Color;

layout(set = 1, binding = 0) uniform texture2D FontTexture;
layout(set = 1, binding = 1) uniform sampler FontSampler;

void main()
{
    fsout_Color = fsin_Color * texture(sampler2D(FontTexture, FontSampler), fsin_TexCoord);
}
