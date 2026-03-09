#version 450

layout(location = 0) in vec3 fsin_Normal;
layout(location = 1) in vec2 fsin_UV;
layout(location = 2) in vec3 fsin_WorldPos;
layout(location = 3) in vec4 fsin_CurrClip;
layout(location = 4) in vec4 fsin_PrevClip;

// MRT outputs (5)
layout(location = 0) out vec4 gAlbedo;     // RT0: R8G8B8A8_UNorm
layout(location = 1) out vec4 gNormal;     // RT1: R16G16B16A16_Float
layout(location = 2) out vec4 gMaterial;   // RT2: R8G8B8A8_UNorm
layout(location = 3) out vec4 gWorldPos;   // RT3: R16G16B16A16_Float
layout(location = 4) out vec2 gVelocity;   // RT4: R16G16_Float

layout(set = 0, binding = 1) uniform MaterialData
{
    vec4 Color;
    vec4 Emission;
    float HasTexture;
    float Metallic;
    float Roughness;
    float Occlusion;
    float NormalMapStrength;
    float HasNormalMap;
    float HasMROMap;
    float _pad1;
    vec2 TextureOffset;
    vec2 TextureScale;
};

layout(set = 0, binding = 2) uniform texture2D MainTexture;
layout(set = 0, binding = 3) uniform sampler MainSampler;
layout(set = 0, binding = 4) uniform texture2D NormalMap;
layout(set = 0, binding = 5) uniform texture2D MROMap;

void main()
{
    // Apply texture tiling & offset
    vec2 uv = fsin_UV * TextureScale + TextureOffset;

    // Albedo
    vec4 texColor = vec4(1.0);
    if (HasTexture > 0.5)
    {
        texColor = texture(sampler2D(MainTexture, MainSampler), uv);
    }
    gAlbedo = Color * texColor;

    // Normal
    vec3 N = normalize(fsin_Normal);

    if (HasNormalMap > 0.5)
    {
        // Cotangent frame TBN (screen-space derivatives, no vertex tangent needed)
        vec3 dp1 = dFdx(fsin_WorldPos);
        vec3 dp2 = dFdy(fsin_WorldPos);
        vec2 duv1 = dFdx(uv);
        vec2 duv2 = dFdy(uv);

        vec3 dp2perp = cross(dp2, N);
        vec3 dp1perp = cross(N, dp1);
        vec3 T = dp2perp * duv1.x + dp1perp * duv2.x;
        vec3 B = dp2perp * duv1.y + dp1perp * duv2.y;
        float invmax = inversesqrt(max(dot(T, T), dot(B, B)));
        mat3 TBN = mat3(T * invmax, B * invmax, N);

        // BC5 stores only RG — reconstruct Z from XY
        vec2 nXY = texture(sampler2D(NormalMap, MainSampler), uv).rg * 2.0 - 1.0;
        nXY *= NormalMapStrength;
        vec3 nTS = vec3(nXY, sqrt(max(1.0 - dot(nXY, nXY), 0.0)));
        N = normalize(TBN * nTS);
    }

    // PBR: MRO map overrides scalar uniforms when present
    float metallic  = Metallic;
    float roughness = Roughness;
    float occlusion = Occlusion;
    if (HasMROMap > 0.5)
    {
        vec3 mro = texture(sampler2D(MROMap, MainSampler), uv).rgb;
        metallic  = mro.r;
        roughness = mro.g;
        occlusion = mro.b;
    }

    gNormal = vec4(N, roughness);

    // Material: Metallic, Occlusion, Emission intensity
    float emissionIntensity = max(Emission.r, max(Emission.g, Emission.b));
    gMaterial = vec4(metallic, occlusion, emissionIntensity, 1.0);

    // World position (direct storage — avoids depth reconstruction issues)
    gWorldPos = vec4(fsin_WorldPos, 1.0);

    // Screen-space velocity (NDC difference * 0.5 = UV-space displacement)
    vec2 currNDC = fsin_CurrClip.xy / fsin_CurrClip.w;
    vec2 prevNDC = fsin_PrevClip.xy / fsin_PrevClip.w;
    gVelocity = (currNDC - prevNDC) * 0.5;
}
