#version 450

layout(location = 0) in vec2 fsin_UV;
layout(location = 0) out vec4 fsout_Color;

layout(set = 0, binding = 0) uniform texture2D SourceTexture;
layout(set = 0, binding = 1) uniform sampler SourceSampler;

layout(set = 0, binding = 2) uniform BlurParams
{
    vec2 Direction;    // (1/width, 0) or (0, 1/height)
    float _pad1;
    float _pad2;
};

// 9-tap Gaussian kernel
const float weights[5] = float[](0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216);

void main()
{
    vec3 result = texture(sampler2D(SourceTexture, SourceSampler), fsin_UV).rgb * weights[0];

    for (int i = 1; i < 5; i++)
    {
        vec2 offset = Direction * float(i);
        result += texture(sampler2D(SourceTexture, SourceSampler), fsin_UV + offset).rgb * weights[i];
        result += texture(sampler2D(SourceTexture, SourceSampler), fsin_UV - offset).rgb * weights[i];
    }

    fsout_Color = vec4(result, 1.0);
}
