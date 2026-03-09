#version 450

layout(location = 0) in vec2 fsin_UV;
layout(location = 0) out vec4 fsout_Color;

// Set 0 — GBuffer
layout(set = 0, binding = 0) uniform texture2D gAlbedo;
layout(set = 0, binding = 1) uniform texture2D gNormal;
layout(set = 0, binding = 2) uniform texture2D gMaterial;
layout(set = 0, binding = 3) uniform texture2D gWorldPos;
layout(set = 0, binding = 4) uniform sampler gSampler;

// Set 1 — Ambient data
layout(set = 1, binding = 0) uniform AmbientData
{
    vec4 CameraPos;
    vec4 SkyAmbient;
};

layout(set = 1, binding = 1) uniform textureCube envMap;

layout(set = 1, binding = 2) uniform EnvMapParams
{
    vec4 EnvTextureParams;   // x=hasTexture, y=exposure, z=rotation(rad), w=unused
    vec4 EnvSunDirection;    // xyz = direction toward sun
    vec4 EnvSkyParams;       // x=zenithIntensity, y=horizonIntensity, z=sunAngularRadius, w=sunIntensity
    vec4 EnvZenithColor;     // rgb = zenith color
    vec4 EnvHorizonColor;    // rgb = horizon color
};

// Set 2 — SSIL output (AO + Indirect)
layout(set = 2, binding = 0) uniform texture2D gAO;
layout(set = 2, binding = 1) uniform texture2D gIndirect;

layout(set = 2, binding = 2) uniform SSILOutputParams
{
    float IndirectBoost;
    float SaturationBoost;
    float AoIntensity;
    float _ssilPad0;
};

const float PI = 3.14159265359;

// === Environment Map Sampling ===

vec3 rotateY(vec3 dir, float rad)
{
    float cosR = cos(rad);
    float sinR = sin(rad);
    return vec3(
        dir.x * cosR - dir.z * sinR,
        dir.y,
        dir.x * sinR + dir.z * cosR
    );
}

vec3 proceduralSky(vec3 dir)
{
    float upFactor = max(dir.y, 0.0);

    vec3 zenith = EnvZenithColor.rgb * EnvSkyParams.x;
    vec3 horizon = EnvHorizonColor.rgb * EnvSkyParams.y;

    float blend = pow(upFactor, 0.7);
    vec3 skyColor = mix(horizon, zenith, blend);

    if (dir.y < 0.0)
    {
        float downFactor = clamp(-dir.y, 0.0, 1.0);
        vec3 groundColor = horizon * 0.3;
        skyColor = mix(horizon, groundColor, pow(downFactor, 0.5));
    }

    vec3 sunDir = normalize(EnvSunDirection.xyz);
    float sunAngularRadius = EnvSkyParams.z;
    float sunIntensity = EnvSkyParams.w;

    float cosAngle = dot(dir, sunDir);
    float cosRadius = cos(sunAngularRadius);

    float sunMask = smoothstep(cosRadius - 0.002, cosRadius + 0.001, cosAngle);
    vec3 sunColor = vec3(1.0, 0.95, 0.85) * sunIntensity * sunMask;

    float glowFactor = pow(max(cosAngle, 0.0), 256.0) * sunIntensity * 0.3;
    vec3 glowColor = vec3(1.0, 0.9, 0.7) * glowFactor;

    return skyColor + sunColor + glowColor;
}

// === PBR Helper (needed before IBL) ===

float distributionGGX(vec3 N, vec3 H, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float NdotH2 = NdotH * NdotH;

    float denom = (NdotH2 * (a2 - 1.0) + 1.0);
    denom = PI * denom * denom;

    return a2 / denom;
}

// === Importance Sampling ===

float radicalInverse_VdC(uint bits)
{
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
    bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
    bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
    return float(bits) * 2.3283064365386963e-10;
}

vec2 hammersley(uint i, uint N)
{
    return vec2(float(i) / float(N), radicalInverse_VdC(i));
}

vec3 importanceSampleGGX(vec2 Xi, vec3 N, float roughness)
{
    float a = roughness * roughness;
    float phi = 2.0 * PI * Xi.x;
    float cosTheta = sqrt((1.0 - Xi.y) / (1.0 + (a * a - 1.0) * Xi.y));
    float sinTheta = sqrt(1.0 - cosTheta * cosTheta);

    vec3 H = vec3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);

    vec3 up = abs(N.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
    vec3 tangent = normalize(cross(up, N));
    vec3 bitangent = cross(N, tangent);

    return normalize(tangent * H.x + bitangent * H.y + N * H.z);
}

vec3 importanceSampleCosine(vec2 Xi, vec3 N)
{
    float phi = 2.0 * PI * Xi.x;
    float cosTheta = sqrt(1.0 - Xi.y);
    float sinTheta = sqrt(Xi.y);

    vec3 H = vec3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);

    vec3 up = abs(N.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
    vec3 tangent = normalize(cross(up, N));
    vec3 bitangent = cross(N, tangent);

    return normalize(tangent * H.x + bitangent * H.y + N * H.z);
}

const uint ENV_SAMPLE_COUNT = 32u;
const uint DIFFUSE_SAMPLE_COUNT = 16u;

vec3 sampleEnvMapRough(vec3 N, vec3 V, float roughness)
{
    vec3 R = reflect(-V, N);

    if (EnvTextureParams.x > 0.5)
    {
        float perceptualRoughness = roughness * (1.7 - 0.7 * roughness);
        int envSize = textureSize(samplerCube(envMap, gSampler), 0).x;
        float maxLod = log2(float(envSize));
        float lod = perceptualRoughness * maxLod;

        vec3 rotDir = rotateY(R, EnvTextureParams.z);
        return textureLod(samplerCube(envMap, gSampler), rotDir, lod).rgb * EnvTextureParams.y;
    }
    else
    {
        if (roughness < 0.05)
            return proceduralSky(R);

        vec3 result = vec3(0.0);
        float totalWeight = 0.0;

        for (uint i = 0u; i < ENV_SAMPLE_COUNT; i++)
        {
            vec2 Xi = hammersley(i, ENV_SAMPLE_COUNT);
            vec3 H = importanceSampleGGX(Xi, R, roughness);
            vec3 L = normalize(2.0 * dot(R, H) * H - R);

            float NdotL = max(dot(N, L), 0.0);
            if (NdotL > 0.0)
            {
                result += proceduralSky(L) * NdotL;
                totalWeight += NdotL;
            }
        }

        return totalWeight > 0.0 ? result / totalWeight : proceduralSky(R);
    }
}

vec3 sampleEnvMapDiffuse(vec3 N)
{
    if (EnvTextureParams.x > 0.5)
    {
        int envSize = textureSize(samplerCube(envMap, gSampler), 0).x;
        float maxLod = log2(float(envSize));
        vec3 rotDir = rotateY(N, EnvTextureParams.z);
        return textureLod(samplerCube(envMap, gSampler), rotDir, maxLod).rgb * EnvTextureParams.y;
    }
    else
    {
        vec3 result = vec3(0.0);

        for (uint i = 0u; i < DIFFUSE_SAMPLE_COUNT; i++)
        {
            vec2 Xi = hammersley(i, DIFFUSE_SAMPLE_COUNT);
            vec3 sampleDir = importanceSampleCosine(Xi, N);
            result += proceduralSky(sampleDir);
        }

        return result / float(DIFFUSE_SAMPLE_COUNT);
    }
}

vec2 envBRDFApprox(float NdotV, float roughness)
{
    vec4 c0 = vec4(-1.0, -0.0275, -0.572, 0.022);
    vec4 c1 = vec4(1.0, 0.0425, 1.04, -0.04);
    vec4 r = roughness * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * NdotV)) * r.x + r.y;
    return vec2(-1.04, 1.04) * a004 + r.zw;
}

void main()
{
    vec4 albedoData = texture(sampler2D(gAlbedo, gSampler), fsin_UV);
    vec4 normalData = texture(sampler2D(gNormal, gSampler), fsin_UV);
    vec4 materialData = texture(sampler2D(gMaterial, gSampler), fsin_UV);
    vec4 worldPosData = texture(sampler2D(gWorldPos, gSampler), fsin_UV);

    if (worldPosData.a < 0.5)
    {
        fsout_Color = vec4(0.0, 0.0, 0.0, 0.0);
        return;
    }

    vec3 albedo = albedoData.rgb;
    vec3 N = normalize(normalData.rgb);
    float roughness = normalData.a;
    float metallic = materialData.r;
    float occlusion = materialData.g;
    float emissionIntensity = materialData.b;

    vec3 worldPos = worldPosData.xyz;
    vec3 V = normalize(CameraPos.xyz - worldPos);
    float NdotV = max(dot(N, V), 0.0);

    vec3 F0 = mix(vec3(0.04), albedo, metallic);

    // IBL Ambient — split-sum approximation
    vec3 envSpecular = sampleEnvMapRough(N, V, roughness);
    vec3 envDiffuse = sampleEnvMapDiffuse(N);

    vec2 brdf = envBRDFApprox(NdotV, roughness);
    vec3 specularScale = F0 * brdf.x + brdf.y;
    vec3 kD_ambient = (vec3(1.0) - specularScale) * (1.0 - metallic);

    // SSIL: Screen Space AO + Indirect Lighting
    float ssao = texture(sampler2D(gAO, gSampler), fsin_UV).r;
    vec3 indirect = texture(sampler2D(gIndirect, gSampler), fsin_UV).rgb;

    // Multi-bounce AO approximation (Jimenez 2016)
    vec3 a = 2.0404 * albedo - 0.3324;
    vec3 b = -4.7951 * albedo + 0.6417;
    vec3 c = 2.7552 * albedo + 0.6903;
    vec3 multiBounce = max(vec3(ssao), ((vec3(ssao) * a + b) * vec3(ssao) + c) * vec3(ssao));

    float ambientScale = SkyAmbient.a; // ambientIntensity from RenderSettings

    // AO strength: auto-soften at high ambient, modulated by AoIntensity
    float aoStrength = AoIntensity / sqrt(max(ambientScale, 1.0));
    vec3 softMultiBounce = mix(vec3(1.0), multiBounce, clamp(aoStrength, 0.0, 1.0));
    float softSsao = mix(1.0, ssao, clamp(aoStrength, 0.0, 1.0));

    vec3 ambient_diffuse = kD_ambient * albedo * envDiffuse * occlusion * softMultiBounce;
    vec3 ambient_specular = specularScale * envSpecular * occlusion * softSsao;
    vec3 ambient = (ambient_diffuse + ambient_specular) * ambientScale;

    // 1-bounce SSIL color bleeding
    float indirectLuma = dot(indirect, vec3(0.2126, 0.7152, 0.0722));
    vec3 saturatedIndirect = mix(vec3(indirectLuma), indirect, SaturationBoost);
    saturatedIndirect = max(saturatedIndirect, vec3(0.0));
    ambient += saturatedIndirect * albedo * IndirectBoost;

    vec3 color = ambient + emissionIntensity * albedo;
    fsout_Color = vec4(color, 1.0);
}
