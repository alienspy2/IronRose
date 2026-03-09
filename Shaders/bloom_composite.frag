#version 450

layout(location = 0) in vec2 fsin_UV;
layout(location = 0) out vec4 fsout_Color;

layout(set = 0, binding = 0) uniform texture2D SceneTexture;
layout(set = 0, binding = 1) uniform texture2D BloomTexture;
layout(set = 0, binding = 2) uniform sampler TexSampler;

layout(set = 0, binding = 3) uniform BloomCompositeParams
{
    float BloomIntensity;
    float _pad1;
    float _pad2;
    float _pad3;
};

void main()
{
    vec4 sceneSample = texture(sampler2D(SceneTexture, TexSampler), fsin_UV);
    vec3 bloom = texture(sampler2D(BloomTexture, TexSampler), fsin_UV).rgb;

    vec3 color = sceneSample.rgb + bloom * BloomIntensity;
    fsout_Color = vec4(color, sceneSample.a);
}
