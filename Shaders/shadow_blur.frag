#version 450

// Separable Gaussian blur for VSM shadow atlas tiles.
// Operates on RG channels (depth moments) with tile-boundary clamping.

layout(location = 0) in vec2 fsin_UV;
layout(location = 0) out vec2 fsout_Moments;

layout(set = 0, binding = 0) uniform texture2D SourceTexture;
layout(set = 0, binding = 1) uniform sampler SourceSampler;

layout(set = 0, binding = 2) uniform BlurParams
{
    vec4 Direction;     // xy = texel step direction (scaled by softness)
    vec4 TileParams;    // xy = tile offset in atlas UV, zw = tile scale
};

// 9-tap Gaussian kernel (sigma ≈ 1.4)
const float weights[5] = float[](0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216);

void main()
{
    // Map fullscreen UV to atlas tile UV
    vec2 atlasUV = fsin_UV * TileParams.zw + TileParams.xy;
    vec2 tileMin = TileParams.xy;
    vec2 tileMax = TileParams.xy + TileParams.zw;

    vec2 result = texture(sampler2D(SourceTexture, SourceSampler), atlasUV).rg * weights[0];

    for (int i = 1; i < 5; i++)
    {
        vec2 offset = Direction.xy * float(i);
        vec2 uv1 = clamp(atlasUV + offset, tileMin, tileMax);
        vec2 uv2 = clamp(atlasUV - offset, tileMin, tileMax);
        result += texture(sampler2D(SourceTexture, SourceSampler), uv1).rg * weights[i];
        result += texture(sampler2D(SourceTexture, SourceSampler), uv2).rg * weights[i];
    }

    fsout_Moments = result;
}
