#version 450

layout(location = 0) out vec4 fsout_Color;

// Set 0 — GBuffer
layout(set = 0, binding = 0) uniform texture2D gAlbedo;
layout(set = 0, binding = 1) uniform texture2D gNormal;
layout(set = 0, binding = 2) uniform texture2D gMaterial;
layout(set = 0, binding = 3) uniform texture2D gWorldPos;
layout(set = 0, binding = 4) uniform sampler gSampler;

// Set 1 — Light volume data
struct LightInfo
{
    vec4 PositionOrDirection;   // xyz = position, w = type (1=point)
    vec4 ColorIntensity;        // rgb = color, a = intensity
    vec4 Params;                // x = range, y = shadowNearPlane, w = rangeNear
    vec4 SpotDirection;
};

layout(set = 1, binding = 0) uniform LightVolumeData
{
    mat4 WorldViewProjection;
    mat4 LightViewProjection;
    vec4 CameraPos;
    vec4 ScreenParams;          // x=width, y=height
    vec4 ShadowParams;          // x=hasShadow, y=bias, z=normalBias, w=softness
    vec4 ShadowAtlasParams;    // xy=tileOffset, zw=tileScale (unused for point, use FaceAtlasParams)
    LightInfo Light;
    mat4 FaceVPs[6];            // 6 cubemap face view-projection matrices
    vec4 FaceAtlasParams[6];   // xy=tileOffset, zw=tileScale per face
};

// Shadow atlas (2D texture — all shadow maps tiled)
layout(set = 1, binding = 1) uniform texture2D ShadowMap;
layout(set = 1, binding = 2) uniform sampler ShadowSampler;

const float PI = 3.14159265359;

// === PBR Functions ===

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

vec3 fresnelSchlick(float cosTheta, vec3 F0)
{
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

float geometrySchlickGGX(float NdotV, float roughness)
{
    float r = (roughness + 1.0);
    float k = (r * r) / 8.0;
    return NdotV / (NdotV * (1.0 - k) + k);
}

float geometrySmith(vec3 N, vec3 V, vec3 L, float roughness)
{
    float NdotV = max(dot(N, V), 0.0);
    float NdotL = max(dot(N, L), 0.0);
    return geometrySchlickGGX(NdotV, roughness) * geometrySchlickGGX(NdotL, roughness);
}

// === Cubemap face selection with edge blending ===

// edgeBlend = secondaryAxis/primaryAxis: 0 at face center, 1 at face edge.
// Matches _cubeFaceTargets order: +X=0, -X=1, +Y=2, -Y=3, +Z=4, -Z=5
void getFaceBlend(vec3 dir, out int face0, out int face1, out float edgeBlend)
{
    vec3 a = abs(dir);
    if (a.x >= a.y && a.x >= a.z) {
        face0 = dir.x > 0.0 ? 0 : 1;
        if (a.y >= a.z) { face1 = dir.y > 0.0 ? 2 : 3; edgeBlend = a.y / a.x; }
        else             { face1 = dir.z > 0.0 ? 4 : 5; edgeBlend = a.z / a.x; }
    } else if (a.y >= a.x && a.y >= a.z) {
        face0 = dir.y > 0.0 ? 2 : 3;
        if (a.x >= a.z) { face1 = dir.x > 0.0 ? 0 : 1; edgeBlend = a.x / a.y; }
        else             { face1 = dir.z > 0.0 ? 4 : 5; edgeBlend = a.z / a.y; }
    } else {
        face0 = dir.z > 0.0 ? 4 : 5;
        if (a.x >= a.y) { face1 = dir.x > 0.0 ? 0 : 1; edgeBlend = a.x / a.z; }
        else             { face1 = dir.y > 0.0 ? 2 : 3; edgeBlend = a.y / a.z; }
    }
}

// === VSM Shadow Sampling ===

// Chebyshev upper bound with contact hardening
float chebyshevShadow(vec2 moments, float receiver, float softness)
{
    float variance = max(moments.y - moments.x * moments.x, 0.00002);
    float d = receiver - moments.x;
    float pMax = variance / (variance + d * d);

    // Contact hardening: d ≈ 0 near caster → sharp, d large → soft
    float contactRange = max(softness * 0.02, 0.001);
    float softFactor = smoothstep(0.0, contactRange, d);
    float bleedThreshold = mix(0.7, 0.2, softFactor);
    pMax = smoothstep(bleedThreshold, 1.0, pMax);

    return (receiver <= moments.x) ? 1.0 : pMax;
}

// VSM shadow for one cubemap face (linear depth)
float sampleFaceShadow(int face, vec3 samplePos, float bias, float softness)
{
    vec4 lightSpacePos = FaceVPs[face] * vec4(samplePos, 1.0);
    vec3 projCoords = lightSpacePos.xyz / lightSpacePos.w;
    projCoords.xy = projCoords.xy * 0.5 + 0.5;
    projCoords.y = 1.0 - projCoords.y;

    if (projCoords.z > 1.0)
        return 1.0;

    // Linear depth: matches shadow_atlas.frag encoding
    float near = Light.Params.y;
    float far  = Light.Params.x;
    float linearDepth = (lightSpacePos.w - near) / (far - near);

    vec2 atlasUV = projCoords.xy * FaceAtlasParams[face].zw + FaceAtlasParams[face].xy;

    // Clamp to tile bounds (half-texel inset prevents cross-tile bleeding at cubemap edges)
    vec2 halfTexel = 0.5 / textureSize(sampler2D(ShadowMap, ShadowSampler), 0);
    vec2 tileMin = FaceAtlasParams[face].xy + halfTexel;
    vec2 tileMax = FaceAtlasParams[face].xy + FaceAtlasParams[face].zw - halfTexel;
    atlasUV = clamp(atlasUV, tileMin, tileMax);

    vec2 moments = texture(sampler2D(ShadowMap, ShadowSampler), atlasUV).rg;
    return chebyshevShadow(moments, linearDepth - bias, softness);
}

void main()
{
    // Reconstruct UV from gl_FragCoord
    vec2 uv = gl_FragCoord.xy / ScreenParams.xy;

    vec4 albedoData = texture(sampler2D(gAlbedo, gSampler), uv);
    vec4 normalData = texture(sampler2D(gNormal, gSampler), uv);
    vec4 materialData = texture(sampler2D(gMaterial, gSampler), uv);
    vec4 worldPosData = texture(sampler2D(gWorldPos, gSampler), uv);

    if (worldPosData.a < 0.5)
        discard;

    vec3 albedo = albedoData.rgb;
    vec3 N = normalize(normalData.rgb);
    float roughness = normalData.a;
    float metallic = materialData.r;
    vec3 worldPos = worldPosData.xyz;
    vec3 V = normalize(CameraPos.xyz - worldPos);
    vec3 F0 = mix(vec3(0.04), albedo, metallic);

    // Point light attenuation
    vec3 lightPos = Light.PositionOrDirection.xyz;
    vec3 toLight = lightPos - worldPos;
    float dist = length(toLight);
    vec3 L = toLight / max(dist, 0.001);
    float lightRange = Light.Params.x;
    float rangeNear = Light.Params.w;
    float attenuation = 1.0 - clamp((dist - rangeNear) / max(lightRange - rangeNear, 0.001), 0.0, 1.0);
    attenuation *= attenuation;

    if (attenuation <= 0.0)
        discard;

    vec3 lightColor = Light.ColorIntensity.rgb * Light.ColorIntensity.a;
    vec3 H = normalize(V + L);
    float NdotL = max(dot(N, L), 0.0);

    // Cook-Torrance BRDF
    float NDF = distributionGGX(N, H, roughness);
    float G = geometrySmith(N, V, L, roughness);
    vec3 F = fresnelSchlick(max(dot(H, V), 0.0), F0);

    vec3 specular = (NDF * G * F) /
        (4.0 * max(dot(N, V), 0.0) * NdotL + 0.0001);

    vec3 kD = (vec3(1.0) - F) * (1.0 - metallic);

    // VSM Shadow (face-based atlas with cubemap edge blending)
    float shadow = 1.0;
    if (ShadowParams.x > 0.5)
    {
        vec3 lightToFrag = worldPos - lightPos;

        // Primary and secondary face with edge blend factor
        int face0, face1;
        float edgeBlend;
        getFaceBlend(lightToFrag, face0, face1, edgeBlend);

        float normalOffset = ShadowParams.z * (1.0 - NdotL);
        vec3 offsetPos = worldPos + N * normalOffset;
        float bias = ShadowParams.y + ShadowParams.y * 5.0 * (1.0 - NdotL);
        float softness = ShadowParams.w;

        float shadow0 = sampleFaceShadow(face0, offsetPos, bias, softness);

        // Blend with secondary face near cubemap edges to eliminate seam artifacts
        float t = smoothstep(0.9, 1.0, edgeBlend) * 0.5;
        if (t > 0.0)
        {
            float shadow1 = sampleFaceShadow(face1, offsetPos, bias, softness);
            shadow = mix(shadow0, shadow1, t);
        }
        else
        {
            shadow = shadow0;
        }
    }

    vec3 Lo = (kD * albedo / PI + specular) * lightColor * NdotL * attenuation * shadow;

    fsout_Color = vec4(Lo, 0.0);
}
