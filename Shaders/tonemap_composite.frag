#version 450

layout(location = 0) in vec2 fsin_UV;
layout(location = 0) out vec4 fsout_Color;

layout(set = 0, binding = 0) uniform texture2D HDRTexture;
layout(set = 0, binding = 1) uniform texture2D BloomTexture;
layout(set = 0, binding = 2) uniform sampler TexSampler;

layout(set = 0, binding = 3) uniform TonemapParams
{
    float BloomIntensity;
    float Exposure;
    float _pad1;
    float _pad2;
};

// ACES Filmic Tone Mapping
vec3 ACESFilm(vec3 x)
{
    float a = 2.51;
    float b = 0.03;
    float c = 2.43;
    float d = 0.59;
    float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

void main()
{
    vec4 hdrSample = texture(sampler2D(HDRTexture, TexSampler), fsin_UV);
    vec3 hdr = hdrSample.rgb;
    float alpha = hdrSample.a;
    vec3 bloom = texture(sampler2D(BloomTexture, TexSampler), fsin_UV).rgb;

    // Bloom composite
    vec3 color = hdr + bloom * BloomIntensity;

    // Exposure
    color *= Exposure;

    // ACES Tone Mapping
    color = ACESFilm(color);

    // Gamma correction (linear -> sRGB)
    color = pow(color, vec3(1.0 / 2.2));

    // Preserve HDR alpha so background (alpha=0) shows the clear color
    fsout_Color = vec4(color, alpha);
}
