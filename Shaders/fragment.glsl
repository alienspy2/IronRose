#version 450

layout(location = 0) in vec3 frag_Normal;
layout(location = 1) in vec2 frag_UV;
layout(location = 2) in vec3 frag_WorldPos;

layout(location = 0) out vec4 out_Color;

// Per-object (set 0)
layout(set = 0, binding = 1) uniform MaterialData
{
    vec4 Color;
    vec4 Emission;
    float HasTexture;
    float _pad1;
    float _pad2;
    float _pad3;
};

layout(set = 0, binding = 2) uniform texture2D MainTexture;
layout(set = 0, binding = 3) uniform sampler MainSampler;

// Per-frame (set 1)
struct LightInfo
{
    vec4 PositionOrDirection; // xyz = position/direction, w = type (0=dir, 1=point)
    vec4 ColorIntensity;     // rgb = color, a = intensity
    vec4 Params;             // x = range, yzw = unused
    vec4 SpotDirection;
};

layout(set = 1, binding = 0) uniform LightData
{
    vec4 CameraPos;          // xyz = camera position, w = unused
    int LightCount;
    int _lpad1;
    int _lpad2;
    int _lpad3;
    LightInfo Lights[8];
};

void main()
{
    vec3 normal = normalize(frag_Normal);

    // Sample texture or use white
    vec4 texColor = vec4(1.0);
    if (HasTexture > 0.5)
    {
        texColor = texture(sampler2D(MainTexture, MainSampler), frag_UV);
    }

    vec4 baseColor = Color * texColor;

    // Unlit mode (sprites) — LightCount < 0
    if (LightCount < 0)
    {
        out_Color = vec4(baseColor.rgb + Emission.rgb, baseColor.a);
        return;
    }

    // No lights — emit only emission (ambient handled by deferred pass)
    if (LightCount <= 0)
    {
        out_Color = vec4(Emission.rgb, baseColor.a);
        return;
    }

    // Multi-light accumulation (ambient handled by deferred pass)
    vec3 ambient = vec3(0.0);
    vec3 diffuseAccum = vec3(0.0);
    vec3 specAccum = vec3(0.0);

    vec3 viewDir = normalize(CameraPos.xyz - frag_WorldPos);
    float shininess = 32.0;

    for (int i = 0; i < LightCount && i < 8; i++)
    {
        vec3 lightColor = Lights[i].ColorIntensity.rgb * Lights[i].ColorIntensity.a;
        float lightType = Lights[i].PositionOrDirection.w;
        float attenuation = 1.0;
        vec3 lightDir;

        if (lightType < 0.5)
        {
            // Directional light
            lightDir = normalize(-Lights[i].PositionOrDirection.xyz);
        }
        else
        {
            // Point light
            vec3 toLight = Lights[i].PositionOrDirection.xyz - frag_WorldPos;
            float dist = length(toLight);
            lightDir = toLight / max(dist, 0.001);
            float lightRange = Lights[i].Params.x;
            attenuation = max(1.0 - (dist / lightRange), 0.0);
            attenuation *= attenuation; // Quadratic falloff
        }

        // Diffuse (Lambert)
        float ndotl = max(dot(normal, lightDir), 0.0);
        diffuseAccum += baseColor.rgb * lightColor * ndotl * attenuation;

        // Specular (Blinn-Phong)
        vec3 halfDir = normalize(lightDir + viewDir);
        float spec = pow(max(dot(normal, halfDir), 0.0), shininess);
        specAccum += lightColor * spec * attenuation * 0.5;
    }

    vec3 finalColor = ambient + diffuseAccum + specAccum + Emission.rgb;
    out_Color = vec4(finalColor, baseColor.a);
}
