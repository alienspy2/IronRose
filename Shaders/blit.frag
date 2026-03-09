#version 450

layout(location = 0) in vec2 fsin_UV;
layout(location = 0) out vec4 fsout_Color;

layout(set = 0, binding = 0) uniform texture2D SourceTexture;
layout(set = 0, binding = 1) uniform sampler SourceSampler;

void main()
{
    vec4 color = texture(sampler2D(SourceTexture, SourceSampler), fsin_UV);
    // Clamp HDR → LDR, gamma correct
    color.rgb = pow(clamp(color.rgb, 0.0, 1.0), vec3(1.0 / 2.2));
    fsout_Color = color;
}
