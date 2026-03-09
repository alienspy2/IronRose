#version 450

layout(location = 0) in vec3 frag_Normal;
layout(location = 1) in vec2 frag_UV;
layout(location = 2) in vec3 frag_WorldPos;

layout(location = 0) out vec4 out_Color;

// Per-object
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

// Per-frame
layout(set = 1, binding = 0) uniform LightData
{
    vec4 CameraPos;
    vec4 LightDir;
    vec4 LightColor;
};

void main()
{
    vec3 normal = normalize(frag_Normal);

    // Albedo
    vec3 albedo = Color.rgb;
    if (HasTexture > 0.5)
    {
        vec4 texColor = texture(sampler2D(MainTexture, MainSampler), frag_UV);
        albedo *= texColor.rgb;
    }

    // Lambert diffuse + ambient
    vec3 lightDir = normalize(-LightDir.xyz);
    float nDotL = max(dot(normal, lightDir), 0.0);
    vec3 diffuse = albedo * (nDotL * LightColor.rgb * 0.8 + 0.2);

    out_Color = vec4(diffuse, 1.0);
}
