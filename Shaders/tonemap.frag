#version 450

layout(location = 0) in vec2 fsin_UV;
layout(location = 0) out vec4 fsout_Color;

layout(set = 0, binding = 0) uniform texture2D SourceTexture;
layout(set = 0, binding = 1) uniform sampler SourceSampler;

layout(set = 0, binding = 2) uniform TonemapParams
{
    float Exposure;
    float Saturation;
    float Contrast;
    float WhitePoint;
    float Gamma;
    float _pad1;
    float _pad2;
    float _pad3;
};

// ACES Filmic Tone Mapping (with configurable white point)
vec3 ACESFilm(vec3 x, float w)
{
    float a = 2.51;
    float b = 0.03;
    float c = 2.43;
    float d = 0.59;
    float e = 0.14;
    vec3 mapped = (x * (a * x + b)) / (x * (c * x + d) + e);
    float wMapped = (w * (a * w + b)) / (w * (c * w + d) + e);
    return clamp(mapped / wMapped, 0.0, 1.0);
}

void main()
{
    vec4 hdrSample = texture(sampler2D(SourceTexture, SourceSampler), fsin_UV);
    vec3 color = hdrSample.rgb;
    float alpha = hdrSample.a;

    // Exposure
    color *= Exposure;

    // Contrast (applied in log space around mid-gray 0.18)
    color = max(color, vec3(0.0));
    color = pow(color / 0.18, vec3(Contrast)) * 0.18;

    // Saturation
    float luma = dot(color, vec3(0.2126, 0.7152, 0.0722));
    color = mix(vec3(luma), color, Saturation);

    // ACES Tone Mapping with white point
    color = ACESFilm(max(color, vec3(0.0)), WhitePoint);

    // Gamma correction (linear → sRGB)
    color = pow(color, vec3(1.0 / Gamma));

    // Preserve alpha so background (alpha=0) shows the clear color
    fsout_Color = vec4(color, alpha);
}
