# Phase 12: Screen Space Indirect Lighting (SSIL)

> **목표**: GTAO 기반 Depth MIP 인프라 구축 후, Visibility Bitmask SSIL로 AO + 1-bounce 간접광 색상 블리딩 구현

## Context

IronRose 엔진의 Deferred PBR 파이프라인에는 현재 Screen Space AO/GI가 없다. `deferred_ambient.frag`의 `occlusion` 값은 머티리얼에 베이크된 고정값(materialData.g)뿐이다. Cornell Box 데모에서 빨간/초록 벽의 색이 인접 표면에 번지는 효과(color bleeding)를 위해, 단순 GTAO가 아닌 **Visibility Bitmask 기반 SSIL**을 구현한다.

**단계적 접근:**
- **12A**: Depth MIP 인프라 + GTAO (AO만) — 컴퓨트 파이프라인 기반 구축
- **12B**: Visibility Bitmask SSIL — GTAO MainPass를 교체하여 진짜 1-bounce 간접광 추가

---

## 현재 상태 (Phase 11 완료 기준)

| 항목 | 현재 |
|------|------|
| G-Buffer | 4 RT + Depth (WorldPos 직접 저장) |
| 컴퓨트 셰이더 | BC5/BC7 압축용만 존재 |
| AO | 머티리얼 베이크 occlusion만 (materialData.g) |
| 간접광 | IBL 환경맵 기반 (screen space 없음) |
| DepthTexture | `DepthStencil` 전용 — TextureView 없음, 샘플링 불가 |

---

## 설계

### 렌더 파이프라인 변경

```
G-Buffer Pass
    ↓
Shadow Pass + Blur
    ↓
[신규] Depth Copy (cl.CopyTexture → R32F 샘플링 가능 텍스처)
    ↓
[신규] PrefilterDepths (compute) → viewspace depth R16F + 5 MIP
    ↓
[12A] GTAO MainPass (compute) → AO (R8)
[12B] SSIL MainPass (compute) → AO (R8) + IndirectLight (RGBA16F)
    ↓
[신규] Denoise (compute) → filtered AO (+ filtered indirect)
    ↓
Ambient Pass (수정: AO 텍스처 + indirect 텍스처 바인딩)
    ↓
Direct Light Passes (기존)
```

### 핵심 문제: DepthTexture 샘플링

현재 `GBuffer.DepthTexture`는 `TextureUsage.DepthStencil` 전용이라 컴퓨트 셰이더에서 샘플링 불가능하다.

> **해결**: `cl.CopyTexture()`로 D32_Float → R32_Float 복사 텍스처 생성 후 샘플링. Veldrid는 depth→color 복사를 지원한다.

---

## 수정 대상 파일

| 파일 | 변경 내용 | 상태 |
|------|----------|------|
| `Shaders/ssil_prefilter_depth.comp` | **신규** — depth → viewspace linear + MIP 생성 |
| `Shaders/ssil_main.comp` | **신규** — 12A: GTAO / 12B: Visibility Bitmask SSIL |
| `Shaders/ssil_denoise.comp` | **신규** — 5x5 에지 보존 공간 필터 |
| `Shaders/deferred_ambient.frag` | **수정** — AO + indirect light 텍스처 바인딩 추가 |
| `src/IronRose.Engine/RenderSystem.cs` | **수정** — 컴퓨트 패스 추가, 리소스 관리 |
| `src/IronRose.Rendering/GBuffer.cs` | **수정** — DepthCopy 텍스처 + View 추가 |

---

## 12A. Depth MIP 인프라 + GTAO

### 12A.1 GBuffer.cs 수정 — Depth 샘플링 텍스처

```csharp
// 신규 필드
public Texture DepthCopyTexture { get; private set; } = null!;
public TextureView DepthCopyView { get; private set; } = null!;

// Initialize() 내부 추가
DepthCopyTexture = factory.CreateTexture(TextureDescription.Texture2D(
    width, height, 1, 1,
    PixelFormat.R32_Float,
    TextureUsage.Sampled | TextureUsage.RenderTarget));
DepthCopyView = factory.CreateTextureView(DepthCopyTexture);

// Dispose() 추가
DepthCopyView?.Dispose();
DepthCopyTexture?.Dispose();
```

### 12A.2 RenderSystem.cs — 신규 필드

```csharp
// --- SSIL / GTAO ---
private Veldrid.Shader? _ssilPrefilterShader;
private Veldrid.Shader? _ssilMainShader;
private Veldrid.Shader? _ssilDenoiseShader;

private Pipeline? _ssilPrefilterPipeline;
private Pipeline? _ssilMainPipeline;
private Pipeline? _ssilDenoisePipeline;

private ResourceLayout? _ssilPrefilterLayout;
private ResourceLayout? _ssilMainLayout;
private ResourceLayout? _ssilDenoiseLayout;

private ResourceSet? _ssilPrefilterSet;
private ResourceSet? _ssilMainSet;
private ResourceSet? _ssilDenoiseSet;

private Texture? _depthMipTexture;      // R16F, 5 MIP levels
private TextureView? _depthMipView;
private TextureView[]? _depthMipLevelViews;  // 개별 MIP 레벨 뷰

private Texture? _aoRawTexture;         // R8, 필터 전 AO
private TextureView? _aoRawView;
private Texture? _aoTexture;            // R8, 필터 후 AO
private TextureView? _aoView;

private Texture? _indirectRawTexture;   // RGBA16F, 필터 전 (12B)
private TextureView? _indirectRawView;
private Texture? _indirectTexture;      // RGBA16F, 필터 후 (12B)
private TextureView? _indirectView;

private DeviceBuffer? _ssilParamsBuffer;
```

### 12A.3 ssil_prefilter_depth.comp

```glsl
#version 450
layout(local_size_x = 16, local_size_y = 16) in;

layout(set = 0, binding = 0) uniform texture2D depthInput;
layout(set = 0, binding = 1) uniform sampler depthSampler;
layout(set = 0, binding = 2, r16f) uniform writeonly image2D depthMip0;

layout(set = 0, binding = 3) uniform PrefilterParams {
    mat4 InvProjection;
    vec2 TexelSize;     // 1.0 / resolution
    float NearPlane;
    float FarPlane;
};

float linearizeDepth(float d) {
    return NearPlane * FarPlane / (FarPlane - d * (FarPlane - NearPlane));
}

void main() {
    ivec2 pos = ivec2(gl_GlobalInvocationID.xy);
    vec2 uv = (vec2(pos) + 0.5) * TexelSize;
    float depth = texture(sampler2D(depthInput, depthSampler), uv).r;
    float viewZ = linearizeDepth(depth);
    imageStore(depthMip0, pos, vec4(viewZ));
}
```

> **Note**: MIP 1~4는 추가 디스패치 또는 같은 셰이더의 후속 패스로 생성. 각 레벨에서 2x2 min 다운샘플.

### 12A.4 ssil_main.comp (12A: GTAO 모드)

```glsl
#version 450
layout(local_size_x = 8, local_size_y = 8) in;

layout(set = 0, binding = 0) uniform texture2D depthMips;
layout(set = 0, binding = 1) uniform texture2D normalTex;
layout(set = 0, binding = 2) uniform sampler linearSampler;
layout(set = 0, binding = 3, r8) uniform writeonly image2D aoOutput;

layout(set = 0, binding = 4) uniform SSILParams {
    mat4 ViewMatrix;
    mat4 InvProjection;
    vec2 Resolution;
    float Radius;        // 월드 단위 AO 반경
    float FalloffScale;
    int SliceCount;      // 3 (High)
    int StepsPerSlice;   // 3 (High, 양쪽 합산 6)
    int FrameIndex;
    float _pad;
};

// GTAO IntegrateArc
float integrateArc(float h1, float h2, float n) {
    float cosN = cos(n);
    float sinN = sin(n);
    return 0.25 * (-cos(2.0*h1 - n) + cosN + 2.0*h1*sinN)
         + 0.25 * (-cos(2.0*h2 - n) + cosN + 2.0*h2*sinN);
}

void main() {
    // ... GTAO 호라이즌 트레이싱 + IntegrateArc ...
    imageStore(aoOutput, pos, vec4(visibility));
}
```

### 12A.5 ssil_denoise.comp

```glsl
#version 450
layout(local_size_x = 8, local_size_y = 8) in;

layout(set = 0, binding = 0) uniform texture2D aoInput;
layout(set = 0, binding = 1) uniform texture2D depthMips;
layout(set = 0, binding = 2) uniform sampler linearSampler;
layout(set = 0, binding = 3, r8) uniform writeonly image2D aoOutput;

layout(set = 0, binding = 4) uniform DenoiseParams {
    vec2 TexelSize;
    float DepthThreshold;
    float _pad;
};

void main() {
    // 5x5 에지 보존 가우시안 (뎁스 차이 가중)
    // ...
    imageStore(aoOutput, pos, vec4(filtered));
}
```

### 12A.6 deferred_ambient.frag 수정

```glsl
// Set 0에 AO 텍스처 추가 (기존 5개 바인딩 뒤에)
layout(set = 0, binding = 5) uniform texture2D gAO;

void main() {
    // ... 기존 코드 ...
    float ssao = texture(sampler2D(gAO, gSampler), fsin_UV).r;

    vec3 ambient_diffuse = kD_ambient * albedo * envDiffuse * occlusion * ssao;
    vec3 ambient_specular = specularScale * envSpecular * occlusion * ssao;
    // ...
}
```

### 12A.7 RenderSystem.cs — _gBufferLayout 수정

```csharp
_gBufferLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
    new ResourceLayoutElementDescription("gAlbedo", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
    new ResourceLayoutElementDescription("gNormal", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
    new ResourceLayoutElementDescription("gMaterial", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
    new ResourceLayoutElementDescription("gWorldPos", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
    new ResourceLayoutElementDescription("gSampler", ResourceKind.Sampler, ShaderStages.Fragment),
    new ResourceLayoutElementDescription("gAO", ResourceKind.TextureReadOnly, ShaderStages.Fragment))); // 신규

// _gBufferResourceSet 업데이트
_gBufferResourceSet = factory.CreateResourceSet(new ResourceSetDescription(
    _gBufferLayout!,
    _gBuffer.AlbedoView,
    _gBuffer.NormalView,
    _gBuffer.MaterialView,
    _gBuffer.WorldPosView,
    _defaultSampler!,
    _aoView!));  // 신규
```

> **중요**: `_gBufferLayout`은 ambient, directional, point, spot 라이팅 셰이더 모두가 공유. AO 바인딩을 Set 0에 추가하면 **모든 라이팅 셰이더에도 `gAO` 바인딩 선언이 필요**하거나, 별도 Set으로 분리해야 한다.
>
> **설계 결정**: AO 텍스처는 ambient 패스에서만 적용하므로, `_gBufferLayout` 수정 대신 **ambient 전용 Set 2로 분리**한다.

### 12A.7 수정 — AO를 Set 2로 분리

```csharp
// _gBufferLayout 유지 (기존 5개 바인딩, 변경 없음)
// 신규 AO 레이아웃
_ssilOutputLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
    new ResourceLayoutElementDescription("gAO", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
    new ResourceLayoutElementDescription("gIndirect", ResourceKind.TextureReadOnly, ShaderStages.Fragment)));

// ambient 파이프라인 ResourceLayouts 수정
ResourceLayouts = new[] { _gBufferLayout!, _ambientLayout!, _ssilOutputLayout! }  // Set 2 추가
```

```glsl
// deferred_ambient.frag: Set 2
layout(set = 2, binding = 0) uniform texture2D gAO;
layout(set = 2, binding = 1) uniform texture2D gIndirect;  // 12B에서 사용
```

### 12A.8 Render() 삽입 — 컴퓨트 디스패치

```csharp
// Line 854 (BlurShadowAtlas 이후) 에 삽입:

// === 1.75 SSIL Compute Pass ===
// 1) Depth copy (D32S8 → R32F)
cl.CopyTexture(
    _gBuffer.DepthTexture, 0, 0, 0, 0, 0,
    _gBuffer.DepthCopyTexture, 0, 0, 0, 0, 0,
    _gBuffer.Width, _gBuffer.Height, 1, 1);

// 2) PrefilterDepths
cl.SetPipeline(_ssilPrefilterPipeline);
cl.SetComputeResourceSet(0, _ssilPrefilterSet);
cl.Dispatch((_gBuffer.Width + 15) / 16, (_gBuffer.Height + 15) / 16, 1);

// 3) MIP downsample (추가 디스패치 4회, MIP 1~4)
for (int mip = 1; mip <= 4; mip++) { /* dispatch downsample */ }

// 4) GTAO / SSIL MainPass
cl.SetPipeline(_ssilMainPipeline);
cl.SetComputeResourceSet(0, _ssilMainSet);
cl.Dispatch((_gBuffer.Width + 7) / 8, (_gBuffer.Height + 7) / 8, 1);

// 5) Denoise
cl.SetPipeline(_ssilDenoisePipeline);
cl.SetComputeResourceSet(0, _ssilDenoiseSet);
cl.Dispatch((_gBuffer.Width + 7) / 8, (_gBuffer.Height + 7) / 8, 1);
```

---

## 12B. Visibility Bitmask SSIL (MainPass 교체)

### 12B.1 ssil_main.comp 확장

GTAO 호라이즌 트레이싱 구조를 유지하되:
- 호라이즌 각도 대신 **32-bit 비트마스크** 사용
- 각 샘플에서 이웃 픽셀의 albedo를 읽어 간접광 누적
- 출력: AO (R8) + IndirectLight (RGBA16F)

```glsl
layout(set = 0, binding = 3, r8) uniform writeonly image2D aoOutput;
layout(set = 0, binding = 5, rgba16f) uniform writeonly image2D indirectOutput;  // 12B 추가
layout(set = 0, binding = 6) uniform texture2D albedoTex;  // 12B 추가: 이웃 색상 읽기

uint updateSectors(float minH, float maxH, uint bitfield) {
    uint startBit = uint(minH * 32.0);
    uint angle = uint(ceil((maxH - minH) * 32.0));
    uint mask = angle > 0u ? (0xFFFFFFFFu >> (32u - angle)) : 0u;
    return bitfield | (mask << startBit);
}

void main() {
    uint occlusionBits = 0u;
    vec3 indirectSum = vec3(0.0);

    for (int slice = 0; slice < SliceCount; slice++) {
        uint sliceBits = 0u;
        // 슬라이스 방향 호라이즌 트레이싱...
        for (int step = 0; step < StepsPerSlice; step++) {
            // 샘플 위치에서 호라이즌 각도 계산
            sliceBits = updateSectors(minH, maxH, sliceBits);

            // 간접광: 새로 차단된 섹터 × 샘플 색상
            uint newBits = sliceBits & ~occlusionBits;
            float weight = float(bitCount(newBits)) / 32.0;
            vec3 sampleColor = textureLod(sampler2D(albedoTex, linearSampler), sampleUV, 0).rgb;
            indirectSum += sampleColor * weight * NdotL;
        }
        occlusionBits |= sliceBits;
    }

    float ao = 1.0 - float(bitCount(occlusionBits)) / (32.0 * float(SliceCount));
    imageStore(aoOutput, pos, vec4(ao));
    imageStore(indirectOutput, pos, vec4(indirectSum / float(SliceCount), 1.0));
}
```

### 12B.2 deferred_ambient.frag 최종

```glsl
layout(set = 2, binding = 0) uniform texture2D gAO;
layout(set = 2, binding = 1) uniform texture2D gIndirect;

void main() {
    // ... 기존 코드 ...
    float ssao = texture(sampler2D(gAO, gSampler), fsin_UV).r;
    vec3 indirect = texture(sampler2D(gIndirect, gSampler), fsin_UV).rgb;

    // Multi-bounce 근사 (Jimenez)
    vec3 a = 2.0404 * albedo - 0.3324;
    vec3 b = -4.7951 * albedo + 0.6417;
    vec3 c = 2.7552 * albedo + 0.6903;
    vec3 multiBounce = max(vec3(ssao), ((vec3(ssao) * a + b) * vec3(ssao) + c) * vec3(ssao));

    vec3 ambient_diffuse = kD_ambient * albedo * envDiffuse * occlusion * multiBounce;
    vec3 ambient_specular = specularScale * envSpecular * occlusion * ssao;

    // SSIL 간접광 추가
    vec3 ambient = (ambient_diffuse + ambient_specular) * ambientScale;
    ambient += indirect * albedo;  // 1-bounce 색상 블리딩

    vec3 color = ambient + emissionIntensity * albedo;
    fsout_Color = vec4(color, 1.0);
}
```

---

## 구현 순서

| 단계 | 내용 | 상태 |
|------|------|------|
| 1 | GBuffer.cs — DepthCopy 텍스처/뷰 추가 | ⏳ |
| 2 | `ssil_prefilter_depth.comp` 작성 + MIP 다운샘플 | ⏳ |
| 3 | `ssil_main.comp` 작성 (12A: GTAO) | ⏳ |
| 4 | `ssil_denoise.comp` 작성 | ⏳ |
| 5 | RenderSystem.cs — 컴퓨트 파이프라인/리소스 초기화 | ⏳ |
| 6 | RenderSystem.cs — Render()에 디스패치 삽입 | ⏳ |
| 7 | `deferred_ambient.frag` — Set 2 AO 바인딩 + 적용 | ⏳ |
| 8 | 빌드 + Cornell Box 데모에서 AO 확인 | ⏳ |
| 9 | `ssil_main.comp` → Visibility Bitmask 교체 (12B) | ⏳ |
| 10 | indirect 텍스처 + ambient 셰이더 간접광 합산 | ⏳ |
| 11 | Cornell Box에서 color bleeding 확인 | ⏳ |

---

## 검증

- [ ] `dotnet build` 성공
- [ ] CornellBoxDemo 실행 시 AO가 모서리/접촉면에 적용됨
- [ ] 디버그 오버레이에 AO 텍스처 시각화 가능
- [ ] 12B 적용 후 빨간/초록 벽의 색상이 인접 흰 표면에 번짐
- [ ] 윈도우 리사이즈 시 SSIL 텍스처 정상 재생성
- [ ] SSIL 비활성화 시 기존 렌더링과 동일 (성능 회귀 없음)

---

## 향후 확장

- **Bent Normal**: GTAO MainPass에서 bent normal 출력 → IBL 샘플링 방향 개선
- **Half-Res**: MainPass를 1/2 해상도에서 실행 + 바이래터럴 업스케일
- **Temporal Filter**: 프레임 간 누적으로 노이즈 감소 (IGN 노이즈 + 히스토리 블렌드)
- **Hi-Z SSR**: PrefilterDepths MIP 체인을 SSR에 재사용
