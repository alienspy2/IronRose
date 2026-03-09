#version 450

layout(location = 0) in vec2 fsin_UV;
layout(location = 0) out vec4 fsout_Color;

layout(set = 0, binding = 0) uniform texture2D SourceTexture;
layout(set = 0, binding = 1) uniform sampler SourceSampler;

layout(set = 0, binding = 2) uniform DebugParams
{
    float Mode;     // 0=Albedo, 1=Normal, 2=Material, 3=WorldPos, 4=ShadowAtlas
    float _pad1;
    float _pad2;
    float _pad3;
};

void main()
{
    vec4 raw = texture(sampler2D(SourceTexture, SourceSampler), fsin_UV);
    int mode = int(Mode + 0.5);

    if (mode == 0)
    {
        // Albedo: RGBA as-is
        fsout_Color = vec4(raw.rgb, 1.0);
    }
    else if (mode == 1)
    {
        // Normal: remap [-1,1] → [0,1]
        fsout_Color = vec4(raw.xyz * 0.5 + 0.5, 1.0);
    }
    else if (mode == 2)
    {
        // Material: R=Metallic, G=Occlusion, B=EmissionIntensity
        fsout_Color = vec4(raw.rgb, 1.0);
    }
    else if (mode == 3)
    {
        // WorldPos: fract of absolute value for repeating color pattern
        fsout_Color = vec4(fract(abs(raw.xyz) * 0.1), 1.0);
    }
    else
    {
        // Shadow Atlas (R32_Float): R channel as grayscale
        fsout_Color = vec4(vec3(raw.r), 1.0);
    }
}
