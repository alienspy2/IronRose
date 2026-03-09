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
    float Metallic;
    float Roughness;
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

// ── Simplified PBR ──

const float PI = 3.14159265359;

float DistributionGGX(float NdotH, float rough)
{
    float a  = rough * rough;
    float a2 = a * a;
    float d  = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d + 0.0001);
}

float GeometrySchlickGGX(float NdotX, float rough)
{
    float k = (rough + 1.0) * (rough + 1.0) / 8.0;
    return NdotX / (NdotX * (1.0 - k) + k);
}

vec3 FresnelSchlick(float cosTheta, vec3 F0)
{
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

vec3 CalcLight(vec3 N, vec3 V, vec3 L, vec3 lightCol, vec3 albedo, float metal, float rough, vec3 F0)
{
    vec3 H = normalize(V + L);

    float NdotL = max(dot(N, L), 0.0);
    float NdotV = max(dot(N, V), 0.001);
    float NdotH = max(dot(N, H), 0.0);
    float HdotV = max(dot(H, V), 0.0);

    float D = DistributionGGX(NdotH, rough);
    float G = GeometrySchlickGGX(NdotL, rough) * GeometrySchlickGGX(NdotV, rough);
    vec3  F = FresnelSchlick(HdotV, F0);

    vec3 specular = (D * G * F) / (4.0 * NdotV * NdotL + 0.0001);
    vec3 kD = (1.0 - F) * (1.0 - metal);
    vec3 diffuse = kD * albedo / PI;

    return (diffuse + specular) * lightCol * NdotL;
}

void main()
{
    vec3 N = normalize(frag_Normal);
    vec3 V = normalize(CameraPos.xyz - frag_WorldPos);

    // Albedo
    vec3 albedo = Color.rgb;
    if (HasTexture > 0.5)
    {
        vec4 texColor = texture(sampler2D(MainTexture, MainSampler), frag_UV);
        albedo *= texColor.rgb;
    }

    float metal = clamp(Metallic, 0.0, 1.0);
    float rough = clamp(Roughness, 0.04, 1.0);
    vec3 F0 = mix(vec3(0.04), albedo, metal);

    vec3 color = vec3(0.0);

    // Key light (from LightDir — 고정 방향, 우상단)
    vec3 keyDir = normalize(-LightDir.xyz);
    color += CalcLight(N, V, keyDir, LightColor.rgb * 3.0, albedo, metal, rough, F0);

    // Fill light (좌하단, 약하고 따뜻한 톤)
    vec3 fillDir = normalize(vec3(-0.5, -0.3, -0.6));
    color += CalcLight(N, V, fillDir, vec3(0.45, 0.42, 0.38) * 1.2, albedo, metal, rough, F0);

    // Rim light (뒤에서 비추는 역광)
    vec3 rimDir = normalize(vec3(0.0, 0.3, -1.0));
    color += CalcLight(N, V, rimDir, vec3(0.6, 0.65, 0.7) * 0.8, albedo, metal, rough, F0);

    // IBL 근사 — 금속 재질의 환경 반사 + 비금속 ambient
    vec3 R = reflect(-V, N);
    // 반구 스카이: 위(zenith)=밝은 파랑, 아래(ground)=어두운 갈색
    vec3 skyUp    = vec3(0.4, 0.45, 0.55);
    vec3 skyDown  = vec3(0.15, 0.12, 0.1);
    vec3 envColor = mix(skyDown, skyUp, R.y * 0.5 + 0.5);
    // roughness에 따라 환경색 블러 (rough할수록 노멀 방향의 diffuse irradiance에 수렴)
    vec3 envDiffuse = mix(skyDown, skyUp, N.y * 0.5 + 0.5);
    vec3 envSpec = mix(envColor, envDiffuse, rough * rough);
    // Fresnel (환경 반사용)
    vec3 F_env = FresnelSchlick(max(dot(N, V), 0.0), F0);
    vec3 kD_env = (1.0 - F_env) * (1.0 - metal);
    vec3 ambient = kD_env * albedo * envDiffuse * 0.5 + F_env * envSpec * 0.6;
    color += ambient;

    // Tonemap (Reinhard)
    color = color / (color + 1.0);

    // Gamma
    color = pow(color, vec3(1.0 / 2.2));

    out_Color = vec4(color, 1.0);
}
