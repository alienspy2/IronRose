# Phase 9: Light Volume Rendering + Shadow Mapping

> **목표**: Deferred 라이팅 패스를 **라이트별 볼륨 렌더링**으로 전환하고, **Shadow Mapping + Shadow Atlas**를 추가하여 라이트 스케일링과 시각 품질을 동시에 개선한다.

**핵심 원칙:**
- Point Light → Sphere 메시, Spot Light → Cone 메시, Directional Light → 풀스크린 삼각형
- 라이트당 1 draw call, Additive Blending으로 누적
- Directional → Orthographic Shadow Map, Point → Cubemap Shadow Map, Spot → Perspective 2D Shadow Map
- **그림자 개별 퀄리티보다 다수 라이트의 그림자 동시 처리를 우선** — 저해상도·단순 PCF로 충분, 대신 shadow 캐스팅 라이트 수 제한 없이 스케일
- **Shadow Map Atlas**로 모든 shadow map을 단일 텍스처에 타일링 — 바인딩 비용 최소화, 다수 라이트 스케일링
- 기존 GBuffer·HDR·PostProcessing 파이프라인 유지

---

## 진행 상태

| 단계 | 내용 | 상태 |
|------|------|------|
| **9A. Light Volume Rendering** | Ambient/Directional/Point 파이프라인 분리, sphere 볼륨 렌더링 | ✅ 완료 |
| **9A-2. Spot Light Volume** | Spot Light cone 메시 볼륨 렌더링 + 셰이더 | ✅ 완료 |
| **9B. Shadow Mapping (개별)** | Directional 2D, Point cubemap, Spot perspective 2D shadow map, PCF | ✅ 완료 |
| **9C. Shadow Map Atlas** | 모든 shadow map을 단일 아틀라스 텍스처로 통합 | ✅ 완료 |

> **Phase 9 완료 조건**: 9A + 9A-2 + 9B + 9C 모두 구현 및 검증 완료

---

## 현재 상태 (Phase 8 완료 기준)

### Deferred Lighting 흐름

```
Geometry Pass → GBuffer (4 RT + Depth)
    ↓
Lighting Pass → HDR (풀스크린 삼각형 1회, 모든 라이트 루프)
    ↓
Skybox → HDR
    ↓
Forward (Sprite/Text/Wireframe) → HDR
    ↓
Post-Processing → Swapchain
```

**문제점**: `deferred_lighting.frag`에서 `for (i < LightCount && i < 64)` 루프를 **모든 픽셀**이 실행. 화면 구석의 픽셀도 반대편 Point Light를 계산함.

### 관련 파일

| 파일 | 역할 |
|------|------|
| `src/IronRose.Engine/RenderSystem.cs` | 전체 렌더 파이프라인 (L453-515: Render) |
| `Shaders/deferred_lighting.vert` | 풀스크린 삼각형 (하드코딩 3 vertices) |
| `Shaders/deferred_lighting.frag` | GBuffer 샘플링 + PBR 라이팅 + IBL |
| `src/IronRose.Rendering/GBuffer.cs` | Albedo/Normal/Material/WorldPos/Depth(D32_S8) |
| `src/IronRose.Engine/RoseEngine/Light.cs` | LightType (Directional, Point, Spot), color/intensity/range |
| `src/IronRose.Engine/RoseEngine/PrimitiveGenerator.cs` | `CreateSphere()` 재사용 가능 |

---

## 설계

### 새로운 렌더 흐름

```
1. Geometry Pass   → GBuffer
2. Shadow Pass     → Shadow Maps  (라이트별: Directional→2D ortho, Point→Cubemap, Spot→2D perspective)
3. Ambient Pass    → HDR  (풀스크린, IBL + ambient, Overwrite)
4. Directional     → HDR  (풀스크린 × N개, Additive, shadow 적용)
5. Point Lights    → HDR  (Sphere × N개, Additive, shadow 적용)
6. Spot Lights     → HDR  (Cone × N개, Additive, shadow 적용)
7. Skybox          → HDR
8. Forward         → HDR
9. Post-Processing → Swapchain
```

### 핵심 기법: Back-Face + GreaterEqual

Point Light 볼륨 렌더링에 **back-face rendering** 기법 사용:

```
Rasterizer:   FaceCullMode.Back   (far hemisphere 렌더, 엔진 winding=Clockwise 기준)
Depth Test:   GreaterEqual        (far hemisphere가 씬보다 뒤에 있으면 pass)
Depth Write:  OFF
Blend:        Additive (One + One)
```

> **주의**: 일반적인 문헌에서는 `CullMode.Front`로 설명하지만, IronRose 엔진의 `FrontFace.Clockwise` winding 컨벤션에서는 `CullMode.Back`이 동일한 효과를 냄. 디버깅으로 검증 완료 (Test 1~6, 2025-02).

**동작 원리:**
- Sphere의 far hemisphere가 씬 지오메트리보다 **뒤에** 있으면 → 해당 픽셀은 라이트 볼륨 **내부** → 셰이딩
- Sphere의 far hemisphere가 씬 지오메트리보다 **앞에** 있으면 → 해당 픽셀은 볼륨 **외부** → 스킵
- 카메라가 볼륨 내부에 있어도 정상 작동 (far hemisphere는 항상 카메라 앞에 있으므로)

> GBuffer의 Depth 포맷이 `D32_Float_S8_UInt`이고, HDR 프레임버퍼가 이 depth를 공유하므로 추가 depth copy 불필요.

---

## 구현 계획

### 9.1 리소스 레이아웃 분리

현재 단일 레이아웃(`_deferredLightingLayout`, set 0에 GBuffer + 라이트 + envmap 전부)을 **2-set 구조**로 분리:

**Set 0 — GBuffer (모든 라이팅 패스 공유)**
```
binding 0: gAlbedo     (TextureReadOnly)
binding 1: gNormal     (TextureReadOnly)
binding 2: gMaterial   (TextureReadOnly)
binding 3: gWorldPos   (TextureReadOnly)
binding 4: gSampler    (Sampler)
```

**Set 1 — Ambient Pass 전용**
```
binding 0: AmbientBuffer  (UBO: CameraPos + SkyAmbient)
binding 1: EnvMap          (TextureReadOnly, Cubemap)
binding 2: EnvMapParams    (UBO)
```

**Set 1 — Direct Light Pass 전용 (Directional + Point + Spot 공용)**
```
binding 0: LightVolumeBuffer  (UBO: 아래 구조체)
```

```csharp
[StructLayout(LayoutKind.Sequential)]
struct LightVolumeUniforms
{
    Matrix4x4 WorldViewProjection;   // 64B  Point: sphere MVP, Spot: cone MVP, Directional: 미사용
    Matrix4x4 LightViewProjection;   // 64B  Shadow Map 좌표 변환 (Directional/Spot용)
    Vector4   CameraPos;             // 16B
    Vector4   ScreenParams;          // 16B  x=width, y=height (gl_FragCoord → UV 변환)
    Vector4   ShadowParams;          // 16B  x=hasShadow(0/1), y=bias, z=normalBias, w=shadowStrength
    LightInfoGPU Light;              // 64B  단일 라이트 (Spot: direction + angles 포함)
}
// Total: 240 bytes
```

### 9.2 파이프라인 생성

기존 `_lightingPipeline` 1개를 **5개**로 교체:

| 파이프라인 | Vertex Shader | Fragment Shader | Cull | Depth | Blend | 비고 |
|-----------|--------------|----------------|------|-------|-------|------|
| `_shadowPipeline` | `shadow.vert` (신규) | (없음/빈 frag) | Back | LessEqual, WriteOn | — | Depth-only |
| `_ambientPipeline` | `deferred_lighting.vert` (기존) | `deferred_ambient.frag` (신규) | None | Disabled | Overwrite | |
| `_directionalLightPipeline` | `deferred_lighting.vert` (기존) | `deferred_directlight.frag` (신규) | None | Disabled | **Additive** | |
| `_pointLightPipeline` | `deferred_pointlight.vert` (신규) | `deferred_pointlight.frag` (신규) | **Back** (엔진 CW 기준) | **GreaterEqual, WriteOff** | **Additive** | |
| `_spotLightPipeline` | `deferred_spotlight.vert` (신규) | `deferred_spotlight.frag` (신규) | **Back** (엔진 CW 기준) | **GreaterEqual, WriteOff** | **Additive** | Cone 메시 |

> Point Light Cubemap Shadow에는 `_shadowPipeline`을 6회 (face당 1회) 실행. Geometry Shader 없이 멀티패스로 처리하여 호환성 유지.
> Spot Light Shadow는 단일 2D perspective shadow map — `_shadowPipeline` 1회 실행.

### 9.3 셰이더 작성

#### `deferred_ambient.frag` (신규)

기존 `deferred_lighting.frag`에서 IBL/ambient 부분만 추출:
- GBuffer 샘플링 + decode
- `sampleEnvMapRough()`, `sampleEnvMapDiffuse()`, `envBRDFApprox()` 유지
- `for (i < LightCount)` 루프 **제거**
- 출력: `ambient_diffuse + ambient_specular + emission`

#### `deferred_directlight.frag` (신규)

풀스크린 삼각형, 단일 Directional Light:
- GBuffer 샘플링 + decode
- 단일 라이트 Cook-Torrance BRDF 계산
- UV: `fsin_UV` (vertex shader에서 전달)
- Shadow Map 샘플링 (2D texture, LightViewProjection으로 UV 계산)

#### `deferred_pointlight.vert` (신규)

```glsl
layout(set = 1, binding = 0) uniform LightVolumeData {
    mat4 WorldViewProjection;
    vec4 CameraPos;
    vec4 ScreenParams;
    // ... LightInfo
};

layout(location = 0) in vec3 Position;

void main() {
    gl_Position = WorldViewProjection * vec4(Position, 1.0);
}
```

- 입력: sphere 메시 vertex (Position만 사용, Normal/UV 무시)
- 출력: 클립 좌표만 (fragment에서 `gl_FragCoord` 사용)

#### `deferred_pointlight.frag` (신규)

```glsl
void main() {
    vec2 uv = gl_FragCoord.xy / ScreenParams.xy;

    // GBuffer 샘플링 + decode
    // worldPosData.a < 0.5 → discard (배경 픽셀)

    // Shadow: cubemap shadow map 샘플링
    vec3 lightToFrag = worldPos - lightPos;
    float closestDepth = texture(shadowCubeMap, lightToFrag).r * farPlane;
    float currentDepth = length(lightToFrag);
    float shadow = currentDepth - bias > closestDepth ? 0.0 : 1.0;

    // 단일 Point Light Cook-Torrance BRDF × shadow
}
```

#### `deferred_spotlight.vert` (신규)

Cone 메시 MVP 변환 (Point Light와 동일 구조):
```glsl
layout(set = 1, binding = 0) uniform LightVolumeData {
    mat4 WorldViewProjection;
    vec4 CameraPos;
    vec4 ScreenParams;
    // ... LightInfo (direction, spotAngle 포함)
};

layout(location = 0) in vec3 Position;

void main() {
    gl_Position = WorldViewProjection * vec4(Position, 1.0);
}
```

#### `deferred_spotlight.frag` (신규)

Cone 볼륨 내부 픽셀에 단일 Spot Light 적용:
```glsl
void main() {
    vec2 uv = gl_FragCoord.xy / ScreenParams.xy;

    // GBuffer 샘플링 + decode
    // worldPosData.a < 0.5 → discard (배경 픽셀)

    // Spot cone falloff
    vec3 L = normalize(lightPos - worldPos);
    float theta = dot(L, normalize(-lightDir));
    float epsilon = cosInnerAngle - cosOuterAngle;
    float spotFactor = clamp((theta - cosOuterAngle) / epsilon, 0.0, 1.0);

    // Distance attenuation (same as point light)
    float dist = length(lightPos - worldPos);
    float attenuation = max(1.0 - (dist / lightRange), 0.0);
    attenuation *= attenuation;

    // Shadow: 2D perspective shadow map 샘플링 (Directional과 유사)
    float shadow = calcSpotShadow(worldPos);

    // 단일 Spot Light Cook-Torrance BRDF × spotFactor × attenuation × shadow
}
```

#### `shadow.vert` (신규)

Shadow Map 생성용 depth-only vertex shader:
```glsl
layout(set = 0, binding = 0) uniform ShadowTransforms {
    mat4 LightMVP;
};
layout(location = 0) in vec3 Position;

void main() {
    gl_Position = LightMVP * vec4(Position, 1.0);
}
```

#### `shadow_point.frag` (신규)

Point Light cubemap shadow용 — 선형 depth 기록:
```glsl
layout(location = 0) in vec3 fragWorldPos;
layout(location = 0) out float outDepth;

uniform vec4 LightPosAndFarPlane;  // xyz=lightPos, w=farPlane

void main() {
    float dist = length(fragWorldPos - LightPosAndFarPlane.xyz);
    outDepth = dist / LightPosAndFarPlane.w;  // [0,1] 정규화
}
```

### 9.4 Light Volume 메시 준비

#### Sphere 메시 (Point Light)

`PrimitiveGenerator.CreateSphere()` 재사용 (radius=0.5 기본):
- 라이트 볼륨용으로 저해상도 sphere 생성 (lon=12, lat=8 정도면 충분)
- `Initialize()`에서 1회 생성, GPU 업로드, 전체 Point Light 공유
- MVP에서 scale = `light.range * 2` (radius 0.5 → diameter 1.0이므로 range로 스케일)

#### Cone 메시 (Spot Light)

`PrimitiveGenerator.CreateCone()` 신규 작성:
- 꼭짓점이 원점(라이트 위치), 밑면이 +Z 방향 (라이트 방향)
- 높이 = 1.0, 밑면 반지름 = 1.0 (MVP에서 스케일)
- 세그먼트: 16~24개면 충분
- `Initialize()`에서 1회 생성, GPU 업로드, 전체 Spot Light 공유

```csharp
// Cone MVP 계산
float height = light.range;
float radius = light.range * MathF.Tan(light.spotOuterAngle * 0.5f * MathF.PI / 180f);
var scale = new Vector3(radius * 2, radius * 2, height);
// 라이트 방향으로 회전
var rotation = Quaternion.FromToRotation(Vector3.forward, light.transform.forward);
var world = Matrix4x4.TRS(lightPos, rotation, scale).ToNumerics();
mvp = world * viewProj;
```

### 9.5 Render() 수정

`RenderSystem.Render()` (L453-515) 변경:

```csharp
// === 2. Ambient/IBL Pass → HDR (Overwrite) ===
cl.SetPipeline(_ambientPipeline);
cl.SetGraphicsResourceSet(0, _gBufferResourceSet);
cl.SetGraphicsResourceSet(1, _ambientResourceSet);
cl.Draw(3, 1, 0, 0);

// === 3. Direct Lights → HDR (Additive) ===
foreach (var light in Light._allLights)
{
    if (!light.enabled || !light.gameObject.activeInHierarchy) continue;

    UploadSingleLightUniforms(cl, light, viewProj);

    if (light.type == LightType.Directional)
    {
        cl.SetPipeline(_directionalLightPipeline);
        cl.SetGraphicsResourceSet(0, _gBufferResourceSet);
        cl.SetGraphicsResourceSet(1, _lightVolumeResourceSet);
        cl.Draw(3, 1, 0, 0);
    }
    else if (light.type == LightType.Point)
    {
        cl.SetPipeline(_pointLightPipeline);
        cl.SetGraphicsResourceSet(0, _gBufferResourceSet);
        cl.SetGraphicsResourceSet(1, _lightVolumeResourceSet);
        cl.SetVertexBuffer(0, _lightSphereMesh.VertexBuffer);
        cl.SetIndexBuffer(_lightSphereMesh.IndexBuffer, IndexFormat.UInt32);
        cl.DrawIndexed((uint)_lightSphereMesh.indices.Length);
    }
    else if (light.type == LightType.Spot)
    {
        cl.SetPipeline(_spotLightPipeline);
        cl.SetGraphicsResourceSet(0, _gBufferResourceSet);
        cl.SetGraphicsResourceSet(1, _lightVolumeResourceSet);
        cl.SetVertexBuffer(0, _lightConeMesh.VertexBuffer);
        cl.SetIndexBuffer(_lightConeMesh.IndexBuffer, IndexFormat.UInt32);
        cl.DrawIndexed((uint)_lightConeMesh.indices.Length);
    }
}
```

### 9.6 Shadow Map 리소스

#### Light 컴포넌트 확장

```csharp
// Light.cs에 추가

// LightType enum 확장
public enum LightType { Directional, Point, Spot }

// Spot Light 프로퍼티
public float spotAngle { get; set; } = 30f;        // inner cone angle (degrees, 전체 각도)
public float spotOuterAngle { get; set; } = 45f;    // outer cone angle (degrees, 전체 각도)

// Shadow 프로퍼티 (모든 라이트 타입 공통)
public bool shadows { get; set; } = false;
public int shadowResolution { get; set; } = 1024;  // Atlas 타일 크기 (라이트별 조절 가능)
public float shadowBias { get; set; } = 0.005f;
public float shadowNormalBias { get; set; } = 0.02f;
public float shadowNearPlane { get; set; } = 0.1f;
```

> `shadowResolution`은 9B에서는 개별 텍스처 크기, 9C에서는 아틀라스 내 타일 크기로 사용됨.
> 라이트별로 다른 해상도를 설정하여 중요도에 따라 품질/성능 트레이드오프 조절 가능.
> 예: 주 Directional Light = 2048, 보조 Point Light = 256

> Spot Light의 `spotAngle`은 inner cone (full intensity), `spotOuterAngle`은 outer cone (falloff 끝).
> 두 각도 사이에서 `smoothstep` 감쇠 적용. `spotAngle ≤ spotOuterAngle` 보장.

#### Directional Light Shadow Map

- **2D Depth Texture** (`D32_Float`)
- Orthographic projection, 카메라 위치 기준 frustum 계산
- 해상도: `light.shadowResolution × light.shadowResolution` (기본 1024)

```csharp
// 카메라 중심으로 orthographic bounds 계산
float shadowRange = 20f;  // 씬 크기에 따라 조절
var lightView = Matrix4x4.CreateLookAt(camPos - lightDir * shadowRange, camPos, Vector3.UnitY);
var lightProj = Matrix4x4.CreateOrthographic(shadowRange * 2, shadowRange * 2, 0.1f, shadowRange * 2);
var lightVP = lightView * lightProj;
```

#### Point Light Shadow Map

- **Cubemap Depth Texture** (`R32_Float`, 6면)
- Perspective projection (FOV=90°, aspect=1.0) × 6 방향
- Geometry Shader 없이 **6-pass 렌더링** (호환성 우선)

```csharp
// 6 face view matrices
Matrix4x4[] faceViews = new Matrix4x4[6];
faceViews[0] = Matrix4x4.CreateLookAt(lightPos, lightPos + Vector3.UnitX,  -Vector3.UnitY); // +X
faceViews[1] = Matrix4x4.CreateLookAt(lightPos, lightPos - Vector3.UnitX,  -Vector3.UnitY); // -X
faceViews[2] = Matrix4x4.CreateLookAt(lightPos, lightPos + Vector3.UnitY,   Vector3.UnitZ); // +Y
faceViews[3] = Matrix4x4.CreateLookAt(lightPos, lightPos - Vector3.UnitY,  -Vector3.UnitZ); // -Y
faceViews[4] = Matrix4x4.CreateLookAt(lightPos, lightPos + Vector3.UnitZ,  -Vector3.UnitY); // +Z
faceViews[5] = Matrix4x4.CreateLookAt(lightPos, lightPos - Vector3.UnitZ,  -Vector3.UnitY); // -Z

var faceProj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 1f, near, light.range);
```

- 각 face는 별도 `Framebuffer`로 렌더 (cubemap face를 color attachment로 bind)
- Fragment shader에서 **선형 distance** 기록 (`length(fragWorldPos - lightPos) / farPlane`)

#### Spot Light Shadow Map

- **2D Depth Texture** (`D32_Float`) — Directional과 동일 포맷
- Perspective projection (FOV = `spotOuterAngle`, aspect = 1.0)
- 해상도: `light.shadowResolution × light.shadowResolution`
- **가장 단순한 shadow type** — 단일 2D 텍스처, 1-pass 렌더링

```csharp
// Spot Light shadow VP 계산
var lightView = Matrix4x4.CreateLookAt(lightPos, lightPos + lightDir, Vector3.UnitY);
var lightProj = Matrix4x4.CreatePerspectiveFieldOfView(
    light.spotOuterAngle * MathF.PI / 180f,  // outer angle as FOV
    1f,                                        // aspect = 1:1
    light.shadowNearPlane,
    light.range);
var lightVP = lightView * lightProj;
```

#### Shadow Map 관리

```csharp
class ShadowData
{
    public Texture? ShadowMap;          // Directional/Spot: Texture2D, Point: TextureCube
    public TextureView? ShadowView;
    public Framebuffer? ShadowFB;       // Directional/Spot: 1개, Point: 6개 (face별)
    public Framebuffer[]? CubeFBs;      // Point Light용 6-face framebuffers
    public Matrix4x4 LightVP;          // Directional/Spot용
    public Matrix4x4[] FaceVPs;        // Point용 (6개)
}
```

- Shadow를 캐스팅하는 라이트만 ShadowData 생성 (`light.shadows == true`)
- `Initialize()`에서 shadow pipeline + layout 생성
- 매 프레임 shadow 캐스팅 라이트만 shadow pass 실행

### 9.7 Shadow Pass 렌더링

Render() 흐름에서 Geometry Pass 직후, Ambient Pass 직전:

```csharp
// === 2. Shadow Pass ===
foreach (var light in Light._allLights)
{
    if (!light.enabled || !light.shadows) continue;

    if (light.type == LightType.Directional)
    {
        // Orthographic shadow map: 전체 씬을 라이트 시점에서 depth 렌더
        cl.SetFramebuffer(shadowData.ShadowFB);
        cl.ClearDepthStencil(1f);
        cl.SetPipeline(_shadowPipeline);
        foreach (var renderer in MeshRenderer._allRenderers)
            DrawShadowCaster(cl, renderer, shadowData.LightVP);
    }
    else if (light.type == LightType.Spot)
    {
        // Perspective shadow map: Directional과 동일 파이프라인, perspective VP
        cl.SetFramebuffer(shadowData.ShadowFB);
        cl.ClearDepthStencil(1f);
        cl.SetPipeline(_shadowPipeline);
        foreach (var renderer in MeshRenderer._allRenderers)
            DrawShadowCaster(cl, renderer, shadowData.LightVP);
    }
    else if (light.type == LightType.Point)
    {
        // Cubemap shadow: 6면 각각 depth 렌더
        for (int face = 0; face < 6; face++)
        {
            cl.SetFramebuffer(shadowData.CubeFBs[face]);
            cl.ClearColorTarget(0, RgbaFloat.White);  // depth=1 (최대)
            cl.SetPipeline(_shadowPointPipeline);
            foreach (var renderer in MeshRenderer._allRenderers)
                DrawShadowCaster(cl, renderer, shadowData.FaceVPs[face]);
        }
    }
}
```

### 9.8 셰이더 Shadow 샘플링

#### Directional Light — 2D Shadow Map

```glsl
// deferred_directlight.frag
uniform sampler2DShadow shadowMap;  // 또는 sampler2D + 수동 비교

float calcShadow(vec3 worldPos) {
    if (ShadowParams.x < 0.5) return 1.0;  // shadow 비활성

    vec4 lightSpacePos = LightViewProjection * vec4(worldPos, 1.0);
    vec3 projCoords = lightSpacePos.xyz / lightSpacePos.w;
    projCoords = projCoords * 0.5 + 0.5;  // [-1,1] → [0,1]

    if (projCoords.z > 1.0) return 1.0;  // frustum 밖

    // PCF 3×3 (soft shadow)
    float shadow = 0.0;
    vec2 texelSize = 1.0 / textureSize(shadowMap, 0);
    for (int x = -1; x <= 1; x++) {
        for (int y = -1; y <= 1; y++) {
            float closestDepth = texture(shadowMap, projCoords.xy + vec2(x,y) * texelSize).r;
            shadow += projCoords.z - ShadowParams.y > closestDepth ? 0.0 : 1.0;
        }
    }
    return shadow / 9.0;
}
```

#### Point Light — Cubemap Shadow Map

```glsl
// deferred_pointlight.frag
uniform samplerCube shadowCubeMap;

float calcPointShadow(vec3 worldPos, vec3 lightPos, float farPlane) {
    if (ShadowParams.x < 0.5) return 1.0;

    vec3 fragToLight = worldPos - lightPos;
    float closestDepth = texture(shadowCubeMap, fragToLight).r;  // [0,1] 정규화
    float currentDepth = length(fragToLight) / farPlane;

    // PCF: 방향 오프셋 (20-sample disc)
    float shadow = 0.0;
    float diskRadius = (1.0 + length(CameraPos.xyz - worldPos) / farPlane) / 50.0;
    vec3 sampleOffsetDirections[20] = vec3[]( /* 20개 방향 벡터 */ );
    for (int i = 0; i < 20; i++) {
        float depth = texture(shadowCubeMap, fragToLight + sampleOffsetDirections[i] * diskRadius).r;
        shadow += currentDepth - ShadowParams.y > depth ? 0.0 : 1.0;
    }
    return shadow / 20.0;
}
```

#### Spot Light — 2D Perspective Shadow Map

```glsl
// deferred_spotlight.frag
// Directional과 거의 동일한 shadow sampling (perspective projection이므로 projCoords.z 범위만 다름)

float calcSpotShadow(vec3 worldPos) {
    if (ShadowParams.x < 0.5) return 1.0;  // shadow 비활성

    vec4 lightSpacePos = LightViewProjection * vec4(worldPos, 1.0);
    vec3 projCoords = lightSpacePos.xyz / lightSpacePos.w;
    projCoords = projCoords * 0.5 + 0.5;  // [-1,1] → [0,1]

    if (projCoords.z > 1.0) return 1.0;  // frustum 밖

    // PCF 3×3 (soft shadow)
    float shadow = 0.0;
    vec2 texelSize = 1.0 / textureSize(shadowMap, 0);
    for (int x = -1; x <= 1; x++) {
        for (int y = -1; y <= 1; y++) {
            float closestDepth = texture(shadowMap, projCoords.xy + vec2(x,y) * texelSize).r;
            shadow += projCoords.z - ShadowParams.y > closestDepth ? 0.0 : 1.0;
        }
    }
    return shadow / 9.0;
}
```

### 9.9 Lighting Resource Set 확장

Direct Light set (set 1)에 shadow map 바인딩 추가:

**Set 1 — Direct Light Pass (업데이트)**
```
binding 0: LightVolumeBuffer    (UBO)
binding 1: ShadowMap            (TextureReadOnly, 2D 또는 Cube)
binding 2: ShadowSampler        (Sampler, ComparisonLessEqual)
```

> Directional/Spot과 Point가 다른 텍스처 타입을 사용하므로, 별도 resource layout + resource set 필요:
> - `_directionalLightLayout`: set 1에 UBO + Texture2D + Sampler (Directional + Spot 공유)
> - `_pointLightLayout`: set 1에 UBO + TextureCube + Sampler

### 9.10 정리

- `MaxDeferredLights` 상수 제거 (더 이상 배열 제한 없음)
- `DeferredLightHeader`, `_deferredLights[]` 제거
- `UploadDeferredLightData()` → `UploadSingleLightUniforms()` 교체
- 기존 `_lightingPipeline`, `_lightingShaders` 제거
- `deferred_lighting.frag`의 PBR 함수들은 새 셰이더에 복사 (GLSL include 없음)
- Shadow 미활성 라이트는 shadow pass 스킵 + 셰이더에서 `ShadowParams.x < 0.5` early-out

---

## 9A-2. Spot Light Volume — 미구현

> Point Light sphere 볼륨과 동일한 back-face + GreaterEqual 기법을 **cone 메시**로 적용.

### 구현 순서

1. **LightType enum 확장** — `Spot` 추가
2. **Light.cs 확장** — `spotAngle`, `spotOuterAngle` 프로퍼티 추가
3. **PrimitiveGenerator.CreateCone()** — Cone 메시 생성
4. **Spot Light 셰이더** — `deferred_spotlight.vert`, `deferred_spotlight.frag`
5. **`_spotLightPipeline` 생성** — Back + GreaterEqual (Point Light와 동일 depth/blend)
6. **LightInfoGPU 확장** — direction, cosInnerAngle, cosOuterAngle 필드
7. **Render() Spot Light 패스 추가** — cone 메시 드로우

### 검증 (9A-2)

1. Spot Light cone 볼륨이 정확한 범위에만 라이팅 적용되는지 확인
2. Inner/outer angle 경계에서 smooth falloff 확인
3. 카메라가 cone 내부에 있을 때 정상 렌더링 확인

---

## 9B. Shadow Mapping (개별) — 미구현

> 9.6~9.9의 설계를 기반으로 **라이트당 개별 shadow map**을 먼저 구현하여 기본 동작을 검증한다.

### 구현 순서

1. **Light.cs 확장** — shadow 프로퍼티 추가
2. **Shadow 셰이더 생성** — `shadow.vert`, `shadow_point.frag`
3. **Shadow Pipeline + Layout** — depth-only 파이프라인, resource layout
4. **Directional Shadow Map** — 2D depth 텍스처, orthographic VP 계산
5. **Spot Shadow Map** — 2D depth 텍스처, perspective VP 계산 (가장 단순)
6. **Point Shadow Map** — Cubemap depth 텍스처, 6-pass 렌더링
7. **Lighting 셰이더에 shadow sampling 추가** — PCF 3×3 (directional/spot), 20-sample (point)
8. **LightVolumeUniforms 확장** — `LightViewProjection`, `ShadowParams` 필드
9. **Render() Shadow Pass 추가** — Geometry Pass 후, Ambient Pass 전

### 검증 (9B)

1. Directional Light `shadows = true` → 오브젝트 그림자 확인
2. Spot Light `shadows = true` → cone 범위 내 그림자 확인
3. Point Light `shadows = true` → 전방향 그림자 확인 (6면)
4. Shadow bias 조절 → shadow acne / peter-panning 없는 적절한 값 확인
5. PCF soft shadow 시각 품질 확인
6. `shadows = false` 라이트 → shadow pass 스킵, 성능 영향 없음

---

## 9C. Shadow Map Atlas — 미구현

> 개별 shadow map 검증 완료 후, 모든 shadow map을 **단일 아틀라스 텍스처**로 통합하여 다수 라이트의 그림자를 효율적으로 처리한다.

### 동기

개별 shadow map 방식의 문제:
- 라이트 N개 → N개의 개별 텍스처 할당 + N번의 resource set 변경
- Point Light는 cubemap이므로 라이트당 6개 face framebuffer
- GPU 메모리 파편화, 바인딩 오버헤드 증가

### 설계

#### Shadow Atlas 텍스처

```
단일 2D Depth Texture (D32_Float 또는 R32_Float)
크기: 4096 × 4096 (기본, 설정 가능)
```

각 라이트가 아틀라스의 **사각형 타일**을 할당받음. 타일 크기는 `light.shadowResolution`으로 라이트별 개별 설정:
- Directional Light: 1개 타일 (`shadowResolution × shadowResolution`, 기본 1024×1024)
- Spot Light: 1개 타일 (`shadowResolution × shadowResolution`, 기본 512×512)
- Point Light: 6개 타일 (face당 `shadowResolution × shadowResolution`, 기본 512×512 × 6)

```
예시: 4096×4096 아틀라스에 라이트별 해상도 배치

+------------------+--------+--------+
| Directional      | Point0 | Point0 |
| (2048×2048)      | face0  | face1  |
|                  | 512²   | 512²   |
|                  +--------+--------+
|                  | Point0 | Point0 |
|                  | face2  | face3  |
+--------+--------+--------+--------+
| Point0 | Point0 | Point1 face0~5  |
| face4  | face5  | (256² × 6)      |
+--------+--------+                 |
| Point2 (128² × 6)                |
| ...                               |
+-----------------------------------+
```

#### 타일 할당

```csharp
class ShadowAtlas
{
    private Texture _atlasTexture;          // 단일 4096×4096 depth 텍스처
    private Framebuffer _atlasFramebuffer;  // 전체 아틀라스 대상 FB
    private List<AtlasTile> _tiles;         // 할당된 타일 목록

    struct AtlasTile
    {
        public int X, Y, Width, Height;    // 아틀라스 내 픽셀 좌표 (light.shadowResolution 기반)
        public Light Light;
        public int CubeFace;               // Point: 0~5, Directional: -1
        public Matrix4x4 LightVP;
    }

    // 라이트 등록 시 타일 할당 — light.shadowResolution으로 타일 크기 결정
    // Directional: 1 tile (res × res), Spot: 1 tile (res × res), Point: 6 tiles (res × res each)
    public AtlasTile[] AllocateTiles(Light light);

    // 아틀라스 공간 부족 시 false 반환 → 해당 라이트 shadow 비활성
    public bool TryAllocate(Light light, out AtlasTile[] tiles);

    // 매 프레임 시작 시 전체 클리어
    public void Clear(CommandList cl);
}
```

#### 렌더 흐름 변경

```
개별 shadow map 방식:
  foreach light → SetFramebuffer(light.shadowFB) → Draw casters

Atlas 방식:
  SetFramebuffer(atlas.framebuffer)  // 한 번만
  foreach light → SetViewport(tile) → Draw casters
```

- `cl.SetViewport()`로 타일 영역 지정, 전체 씬을 해당 영역에 렌더
- 하나의 framebuffer에 모든 shadow map이 렌더되므로 framebuffer 전환 없음

#### 셰이더 변경

Lighting 셰이더에서 shadow 샘플링 시 타일 좌표로 UV 변환:

```glsl
// 개별 방식: UV = projCoords.xy (0~1 전체 텍스처)
// Atlas 방식: UV = projCoords.xy * tileScale + tileOffset

uniform vec4 ShadowAtlasParams;  // xy=tileOffset, zw=tileScale

vec2 atlasUV = projCoords.xy * ShadowAtlasParams.zw + ShadowAtlasParams.xy;
float closestDepth = texture(shadowAtlas, atlasUV).r;
```

Point Light cubemap → atlas 변환:
- `fragToLight` 방향으로 6면 중 dominant face 결정
- 해당 face의 LightVP로 좌표 변환 후 atlas 타일 UV 계산

```glsl
// Point light: cubemap 방향 → face index → atlas tile UV
int face = getDominantFace(fragToLight);
vec4 lightSpacePos = FaceViewProjections[face] * vec4(worldPos, 1.0);
vec2 projUV = lightSpacePos.xy / lightSpacePos.w * 0.5 + 0.5;
vec2 atlasUV = projUV * FaceTileScales[face] + FaceTileOffsets[face];
```

#### LightVolumeUniforms 확장 (Atlas용)

```csharp
[StructLayout(LayoutKind.Sequential)]
struct LightVolumeUniforms
{
    Matrix4x4 WorldViewProjection;   // 64B  Point: sphere MVP
    Matrix4x4 LightViewProjection;   // 64B  Directional shadow VP
    Vector4   CameraPos;             // 16B
    Vector4   ScreenParams;          // 16B
    Vector4   ShadowParams;          // 16B  x=hasShadow, y=bias, z=normalBias, w=strength
    Vector4   ShadowAtlasParams;     // 16B  xy=tileOffset, zw=tileScale (Directional / Point face 0)
    LightInfoGPU Light;              // 64B
    // Point Light 6-face용 추가 데이터:
    Matrix4x4 FaceVP1;              // 64B  face 1 VP (face 0은 LightViewProjection 사용)
    Matrix4x4 FaceVP2;              // 64B
    Matrix4x4 FaceVP3;              // 64B
    Matrix4x4 FaceVP4;              // 64B
    Matrix4x4 FaceVP5;              // 64B
    Vector4   FaceAtlasParams1;     // 16B  face 1 atlas offset/scale
    Vector4   FaceAtlasParams2;     // 16B
    Vector4   FaceAtlasParams3;     // 16B
    Vector4   FaceAtlasParams4;     // 16B
    Vector4   FaceAtlasParams5;     // 16B
}
```

> Directional Light는 `LightViewProjection` + `ShadowAtlasParams` 만 사용.
> Point Light는 face 0~5 각각의 VP + atlas tile 좌표가 필요.
> UBO 크기가 커지지만, 바인딩 1회로 모든 shadow 데이터 전달.

### 검증 (9C)

1. Shadow Atlas 텍스처 디버그 시각화 (전체 아틀라스를 화면에 표시)
2. Directional + Point 혼합 씬에서 shadow 정상 동작
3. 다수 라이트 (10+개) shadow 동시 렌더링 → 아틀라스 타일 배치 확인
4. 아틀라스 크기 초과 시 graceful fallback (shadow 비활성)
5. 개별 shadow map 대비 프레임 타임 개선 측정

---

## 수정 대상 파일

### 9A (완료)

| 파일 | 변경 내용 | 상태 |
|------|----------|------|
| `src/IronRose.Engine/RenderSystem.cs` | 파이프라인 4개 분리, Render() 루프 변경, 리소스 레이아웃/셋 재구성 | ✅ |
| `Shaders/deferred_ambient.frag` | IBL + ambient 전용 | ✅ |
| `Shaders/deferred_directlight.frag` | 단일 Directional Light PBR | ✅ |
| `Shaders/deferred_pointlight.vert` | Sphere MVP 변환 | ✅ |
| `Shaders/deferred_pointlight.frag` | 단일 Point Light PBR | ✅ |
| `Shaders/deferred_lighting.vert` | 유지 (ambient + directional용) | ✅ |
| `Shaders/deferred_lighting.frag` | 삭제 (ambient + directlight으로 분리) | ✅ |

### 9A-2 (미구현)

| 파일 | 변경 내용 |
|------|----------|
| `src/IronRose.Engine/RoseEngine/Light.cs` | `LightType.Spot` 추가, `spotAngle`, `spotOuterAngle` 프로퍼티 |
| `src/IronRose.Engine/RoseEngine/PrimitiveGenerator.cs` | `CreateCone()` 메서드 추가 |
| `src/IronRose.Engine/RenderSystem.cs` | `_spotLightPipeline`, `_lightConeMesh`, Spot Light 렌더 분기 |
| `Shaders/deferred_spotlight.vert` | **신규** — Cone MVP 변환 |
| `Shaders/deferred_spotlight.frag` | **신규** — Spot Light PBR + cone falloff |

### 9B (미구현)

| 파일 | 변경 내용 |
|------|----------|
| `src/IronRose.Engine/RoseEngine/Light.cs` | `shadows`, `shadowResolution`, `shadowBias`, `shadowNormalBias` 프로퍼티 추가 |
| `src/IronRose.Engine/RenderSystem.cs` | Shadow Pass 추가, ShadowData 관리, LightVolumeUniforms 확장 |
| `Shaders/shadow.vert` | **신규** — Shadow depth-only vertex shader |
| `Shaders/shadow_point.frag` | **신규** — Point Light 선형 depth 기록 |
| `Shaders/deferred_directlight.frag` | shadow sampling 추가 (2D PCF 3×3) |
| `Shaders/deferred_spotlight.frag` | shadow sampling 추가 (2D PCF 3×3) |
| `Shaders/deferred_pointlight.frag` | shadow sampling 추가 (cubemap PCF 20-sample) |

### 9C (미구현)

| 파일 | 변경 내용 |
|------|----------|
| `src/IronRose.Engine/RenderSystem.cs` | ShadowAtlas 클래스, 타일 할당, atlas framebuffer 관리 |
| `Shaders/deferred_directlight.frag` | atlas UV 변환으로 교체 |
| `Shaders/deferred_spotlight.frag` | atlas UV 변환으로 교체 |
| `Shaders/deferred_pointlight.frag` | cubemap → atlas face 인덱싱으로 교체 |

---

## 검증 (전체)

### 9A — Light Volume Rendering ✅
1. ~~`dotnet build` 성공 확인~~ ✅
2. ~~**CornellBoxDemo**: Point Light 볼륨 렌더링 정상~~ ✅
3. ~~카메라를 Point Light 내부로 이동 → 정상 렌더링~~ ✅
4. **ManyLightsDemo**: 다수 라이트 성능 테스트 — 미검증

### 9A-2 — Spot Light Volume
5. Spot Light cone 볼륨 라이팅 정상 동작
6. Inner/outer angle smooth falloff 확인
7. 카메라가 cone 내부에 있을 때 정상 렌더링 확인

### 9B — Shadow Mapping (개별)
8. Directional Light `shadows = true` → 오브젝트 그림자 확인
9. Spot Light `shadows = true` → cone 범위 내 그림자 확인
10. Point Light `shadows = true` → 전방향 그림자 확인 (6면)
11. Shadow bias 조절 → shadow acne / peter-panning 없는 적절한 값 확인
12. PCF soft shadow 시각 품질 확인

### 9C — Shadow Map Atlas
13. Atlas 디버그 시각화 (전체 아틀라스를 화면에 표시)
14. 다수 라이트 (10+개, Directional+Spot+Point 혼합) shadow 동시 렌더링
15. 개별 shadow map 대비 바인딩/메모리 개선 확인
16. Atlas 크기 초과 시 graceful fallback

---

## 향후 확장 (이 Phase 범위 밖)

- **Cascaded Shadow Maps (CSM)**: Directional Light 거리별 해상도 분리 (근거리 고해상도)
- **Tiled/Clustered Shading**: 수천 개 라이트 대응 (compute shader 기반)
- **Contact Shadows**: Screen-space ray marching으로 근접 그림자 보강
- **Cookie Textures**: Spot Light에 프로젝션 텍스처 적용 (창문 패턴 등)
