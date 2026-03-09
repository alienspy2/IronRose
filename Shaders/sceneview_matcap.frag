#version 450

layout(location = 0) in vec3 frag_ViewNormal;
layout(location = 1) in vec2 frag_UV;

layout(location = 0) out vec4 out_Color;

layout(set = 0, binding = 1) uniform MaterialData
{
    vec4 Color;
    float HasTexture;
    float _pad1;
    float _pad2;
    float _pad3;
};

layout(set = 0, binding = 2) uniform texture2D MainTexture;
layout(set = 0, binding = 3) uniform sampler MainSampler;

void main()
{
    vec3 viewNormal = normalize(frag_ViewNormal);
    // Y 반전: 텍스처 UV (0,0)=좌상단이므로 viewNormal.y+ → UV.y=0(상단)
    vec2 matcapUV = vec2(viewNormal.x, -viewNormal.y) * 0.5 + 0.5;

    if (HasTexture > 0.5)
    {
        vec3 matcapColor = texture(sampler2D(MainTexture, MainSampler), matcapUV).rgb;
        out_Color = vec4(matcapColor * Color.rgb, 1.0);
    }
    else
    {
        // Fallback: simple N·V grayscale
        float nDotV = max(viewNormal.z, 0.0);
        float gray = mix(0.3, 0.9, nDotV);
        out_Color = vec4(gray * Color.rgb, 1.0);
    }
}
