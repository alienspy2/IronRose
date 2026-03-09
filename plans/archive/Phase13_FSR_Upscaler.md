# Phase 13: FSR3 Temporal Upscaler

> **목표**: Render/Display 해상도 분리 인프라 구축 + Motion Vector RT 추가 + FSR 스타일 Temporal Upscaler 컴퓨트 셰이더 구현

## Context

IronRose 엔진은 현재 모든 렌더 타겟(GBuffer, HDR, PostProcess)이 윈도우 해상도와 1:1로 동작한다. 고해상도 디스플레이에서 성능 확보를 위해 내부 렌더링을 낮은 해상도에서 수행하고, FSR 스타일 Temporal Upscaler로 디스플레이 해상도까지 업스케일하는 파이프라인을 구축한다.

FSR3 Upscaler의 핵심 알고리즘(Temporal Accumulation + Jittered Sampling + Motion Reprojection)을 GLSL 컴퓨트 셰이더로 구현한다. FidelityFX SDK 소스를 `./external/fsr3.1`에 클론하여 셰이더 알고리즘과 헤더를 참조 소스로 활용한다.

**단계적 접근:**
- **13A**: FidelityFX SDK 클론 (참조용) + Render/Display 해상도 분리 + Motion Vector RT
- **13B**: Temporal Upscaler 컴퓨트 셰이더 (Jitter + Reprojection + Accumulation)

---

## 현재 상태 (Phase 12 완료 기준)

| 항목 | 현재 |
|------|------|
| 해상도 관리 | Window = Render = Display (단일) |
| GBuffer | 4 RT + Depth (모두 윈도우 해상도) |
| HDR 버퍼 | R16G16B16A16_Float (윈도우 해상도) |
| PostProcess | Ping-pong HDR → Blit → Swapchain (동일 해상도) |
| Motion Vector | 없음 (SSIL temporal은 WorldPos로 reprojection) |
| Blit 셰이더 | 단순 샘플링 + 감마 보정 (스케일링 없음) |

---

## FidelityFX SDK 클론 (참조 소스)

네이티브 `.so`/`.dll` 빌드는 하지 않는다. SDK 소스를 클론하여 셰이더 알고리즘만 참조하고, IronRose용 GLSL 컴퓨트 셰이더를 직접 작성한다. Veldrid 파이프라인과의 충돌 위험이 없고, 외부 바이너리 의존성도 없다.

### 저장소 클론

```bash
# 프로젝트 루트에서 실행
mkdir -p external
git clone --depth 1 --branch v1.1.4 \
  https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK.git \
  external/fsr3.1
```

> **v1.1.4 선택 이유**: FSR 3.1.4 셰이더 소스 포함. v2.0+는 FSR4(ML 기반)로 전환되어 참조 가치 낮음.

### 핵심 참조 파일

| 경로 | 용도 |
|------|------|
| `sdk/include/FidelityFX/gpu/fsr3upscaler/ffx_fsr3upscaler_accumulate.h` | Temporal accumulation 알고리즘 |
| `sdk/include/FidelityFX/gpu/fsr3upscaler/ffx_fsr3upscaler_rcas.h` | RCAS (Robust Contrast Adaptive Sharpening) |
| `sdk/include/FidelityFX/gpu/fsr3upscaler/ffx_fsr3upscaler_reproject.h` | Motion reprojection 로직 |
| `sdk/include/FidelityFX/gpu/fsr3upscaler/ffx_fsr3upscaler_lock.h` | Lock/luminance stability |
| `sdk/include/FidelityFX/gpu/spd/ffx_spd.h` | Single Pass Downsampler (MIP 생성) |
| `sdk/include/FidelityFX/host/ffx_fsr3upscaler.h` | C API 구조체/enum 정의 (파라미터 참조) |

### .gitignore 설정

```gitignore
# external/fsr3.1 — 알고리즘 참조 전용, 빌드하지 않음
external/fsr3.1/
```

> SDK 라이선스는 MIT. 소스를 직접 복사하지 않고 알고리즘만 참고하여 GLSL로 재구현.

---

## 설계

### 렌더 파이프라인 변경

```
[Before]
Window(1920x1080) → GBuffer(1920x1080) → HDR(1920x1080) → PostProcess(1920x1080) → Blit → Swapchain(1920x1080)

[After — Performance 2.0x 예시]
Window(1920x1080) → GBuffer(960x540) → HDR(960x540) → PostProcess(960x540)
                                                              ↓
                                                    Temporal Upscale (compute)
                                                              ↓
                                                    Upscaled HDR(1920x1080) → Blit → Swapchain(1920x1080)
```

### 핵심 변경 포인트

1. **RenderSettings**: `fsrEnabled`, `fsrScaleMode` (Quality/Balanced/Performance/UltraPerformance/Custom), `fsrCustomScale` 추가
2. **RenderSystem**: `renderWidth`/`renderHeight` vs `displayWidth`/`displayHeight` 분리
3. **GBuffer**: `renderWidth x renderHeight`로 생성
4. **HDR/SSIL 텍스처**: `renderWidth x renderHeight`로 생성
5. **PostProcess ping-pong**: `renderWidth x renderHeight`
6. **Temporal Upscaler**: `renderWidth` → `displayWidth` 업스케일 (컴퓨트)
7. **Blit**: `displayWidth x displayHeight`로 최종 출력 (기존과 동일)

### FSR 품질 모드

| 모드 | 배율 | Render (1080p 기준) |
|------|------|---------------------|
| NativeAA | 1.0x | 1920x1080 |
| Quality | 1.5x | 1280x720 |
| Balanced | 1.7x | 1129x635 |
| Performance | 2.0x | 960x540 |
| Ultra Performance | 3.0x | 640x360 |

### Motion Vector 설계

```
deferred_geometry.vert:
  현재: gl_Position = ViewProjection * worldPos
  추가: prevClipPos = PrevViewProjection * worldPos  (per-vertex)

deferred_geometry.frag:
  추가: layout(location = 4) out vec2 gVelocity  →  (currNDC - prevNDC) * 0.5
```

- 포맷: `RG16_Float` (screen-space 2D velocity, [-1, 1] 범위)
- GBuffer RT4로 추가
- 카메라 이동 + 오브젝트 이동 모두 캡처

### Temporal Upscaler 컴퓨트 셰이더 설계

FSR3 Upscaler의 핵심 원리를 GLSL 컴퓨트로 구현:

```
[입력 — render resolution]
├── Color (HDR, R16G16B16A16_Float)
├── Depth (R32_Float)
├── Motion Vectors (RG16_Float)
└── (선택) Reactive Mask

[내부 상태 — display resolution]
├── History Color (이전 프레임 업스케일 결과)
└── History Lock (누적 신뢰도)

[출력 — display resolution]
└── Upscaled Color (R16G16B16A16_Float)
```

**알고리즘 핵심:**
1. **Jittered Rendering**: 매 프레임 서브픽셀 지터 오프셋 (Halton 2,3)
2. **Reprojection**: Motion Vector로 이전 프레임 히스토리 샘플
3. **Neighborhood Clamping**: 현재 프레임 3x3 이웃으로 히스토리 클램핑 (ghosting 방지)
4. **Accumulation**: 현재 샘플 + 클램핑된 히스토리 블렌딩
5. **Sharpening**: 선택적 CAS (Contrast Adaptive Sharpening)

### Jitter 적용 위치

```csharp
// RenderSystem.Render() — 카메라 Projection 수정
var jitter = GetHaltonJitter(frameIndex, renderWidth, renderHeight);
var jitteredProjection = AddJitterToProjection(projection, jitter);
// 이 jitteredProjection을 geometry pass에만 적용
```

---

## 수정 대상 파일

| 파일 | 변경 내용 | 상태 |
|------|----------|------|
| `external/fsr3.1/` | **신규** — FidelityFX SDK v1.1.4 클론 (참조 전용, 빌드 안 함) | ⏳ |
| `.gitignore` | **수정** — `external/fsr3.1/` 제외 추가 | ⏳ |
| `src/IronRose.Engine/RoseEngine/RenderSettings.cs` | **수정** — FSR 배율 파라미터 추가 | ⏳ |
| `src/IronRose.Rendering/GBuffer.cs` | **수정** — Velocity RT4 추가 | ⏳ |
| `src/IronRose.Engine/RenderSystem.cs` | **수정** — render/display 분리, jitter, MV, upscaler 디스패치 | ⏳ |
| `Shaders/deferred_geometry.vert` | **수정** — PrevViewProjection 추가, prevClipPos 출력 | ⏳ |
| `Shaders/deferred_geometry.frag` | **수정** — Velocity 출력 (RT4) | ⏳ |
| `Shaders/fsr_upscale.comp` | **신규** — Temporal Upscaler 컴퓨트 셰이더 (FSR3 알고리즘 참조) | ⏳ |
| `src/IronRose.Rendering/PostProcessing/PostProcessStack.cs` | **수정** — Resize에 render/display 분리 반영 | ⏳ |

---

## 13A. Render/Display 분리 + Motion Vector

### 13A.1 RenderSettings.cs — FSR 파라미터 추가

```csharp
// --- FSR Upscaler ---
public static bool fsrEnabled { get; set; } = false;
public static FsrScaleMode fsrScaleMode { get; set; } = FsrScaleMode.Quality;
public static float fsrCustomScale { get; set; } = 1.5f;  // Custom 모드 시 사용

public enum FsrScaleMode
{
    NativeAA,        // 1.0x
    Quality,         // 1.5x
    Balanced,        // 1.7x
    Performance,     // 2.0x
    UltraPerformance // 3.0x
}
```

> `FsrScaleMode` enum은 `RenderSettings.cs` 내부 또는 동일 파일에 정의.

### 13A.2 RenderSystem.cs — 해상도 분리 로직

```csharp
// 신규 필드
private uint _displayWidth, _displayHeight;
private uint _renderWidth, _renderHeight;

// 배율 → 렌더 해상도 계산
private (uint rw, uint rh) CalcRenderResolution(uint dw, uint dh)
{
    if (!RenderSettings.fsrEnabled) return (dw, dh);

    float scale = RenderSettings.fsrScaleMode switch
    {
        FsrScaleMode.NativeAA        => 1.0f,
        FsrScaleMode.Quality         => 1.5f,
        FsrScaleMode.Balanced        => 1.7f,
        FsrScaleMode.Performance     => 2.0f,
        FsrScaleMode.UltraPerformance => 3.0f,
        _ => RenderSettings.fsrCustomScale
    };

    uint rw = Math.Max((uint)(dw / scale), 1);
    uint rh = Math.Max((uint)(dh / scale), 1);
    return (rw, rh);
}
```

**영향 범위 — render resolution으로 변경할 것들:**
- `GBuffer.Initialize(renderWidth, renderHeight)`
- `_hdrTexture` 생성
- `_hdrFramebuffer`
- SSIL 관련 모든 텍스처 (depth MIP, AO, indirect)
- PostProcessStack ping-pong 버퍼
- 컴퓨트 셰이더 디스패치 그룹 수

**display resolution으로 유지할 것들:**
- Swapchain (변경 없음)
- Upscaler 출력 텍스처
- 최종 Blit

### 13A.3 GBuffer.cs — Velocity RT 추가

```csharp
// 신규 필드
public Texture VelocityTexture { get; private set; } = null!;
public TextureView VelocityView { get; private set; } = null!;

// Initialize() 내부 추가
VelocityTexture = factory.CreateTexture(TextureDescription.Texture2D(
    width, height, 1, 1,
    PixelFormat.R16_G16_Float,
    TextureUsage.RenderTarget | TextureUsage.Sampled));
VelocityView = factory.CreateTextureView(VelocityTexture);

// Framebuffer 수정 — 5 color targets
Framebuffer = factory.CreateFramebuffer(new FramebufferDescription(
    DepthTexture,
    AlbedoTexture,      // RT0
    NormalTexture,       // RT1
    MaterialTexture,     // RT2
    WorldPosTexture,     // RT3
    VelocityTexture));   // RT4
```

### 13A.4 deferred_geometry.vert — PrevViewProjection 추가

```glsl
layout(set = 0, binding = 0) uniform Transforms
{
    mat4 World;
    mat4 ViewProjection;
    mat4 PrevViewProjection;  // 신규
};

layout(location = 0) out vec3 fsin_Normal;
layout(location = 1) out vec2 fsin_UV;
layout(location = 2) out vec3 fsin_WorldPos;
layout(location = 3) out vec4 fsin_CurrClip;   // 신규
layout(location = 4) out vec4 fsin_PrevClip;   // 신규

void main()
{
    vec4 worldPos = World * vec4(Position, 1.0);
    vec4 currClip = ViewProjection * worldPos;
    gl_Position = currClip;

    fsin_Normal = normalize(mat3(World) * Normal);
    fsin_UV = UV;
    fsin_WorldPos = worldPos.xyz;
    fsin_CurrClip = currClip;
    fsin_PrevClip = PrevViewProjection * worldPos;
}
```

### 13A.5 deferred_geometry.frag — Velocity 출력

```glsl
layout(location = 3) in vec4 fsin_CurrClip;
layout(location = 4) in vec4 fsin_PrevClip;

layout(location = 4) out vec2 gVelocity;  // RT4

void main()
{
    // ... 기존 gAlbedo, gNormal, gMaterial, gWorldPos 출력 ...

    // Screen-space velocity (NDC 차이의 절반 = UV 공간 이동량)
    vec2 currNDC = fsin_CurrClip.xy / fsin_CurrClip.w;
    vec2 prevNDC = fsin_PrevClip.xy / fsin_PrevClip.w;
    gVelocity = (currNDC - prevNDC) * 0.5;
}
```

### 13A.6 RenderSystem.cs — Transforms 유니폼 확장

```csharp
[StructLayout(LayoutKind.Sequential)]
internal struct TransformData
{
    public Matrix4x4 World;
    public Matrix4x4 ViewProjection;
    public Matrix4x4 PrevViewProjection;  // 신규 (192 bytes 총)
}

// Render() 루프에서:
// _prevViewProj는 이미 존재 (SSIL temporal에서 사용 중)
var transformData = new TransformData
{
    World = worldMatrix,
    ViewProjection = jitteredViewProj,   // 지터 적용된 VP
    PrevViewProjection = _prevViewProj
};
```

### 13A.7 Jitter 적용

```csharp
// Halton(2,3) 시퀀스 — 16프레임 주기
private static readonly Vector2[] HaltonSequence = GenerateHalton(16);

private Matrix4x4 ApplyJitter(Matrix4x4 projection, int frameIndex, uint renderW, uint renderH)
{
    if (!RenderSettings.fsrEnabled) return projection;

    var h = HaltonSequence[frameIndex % HaltonSequence.Length];
    // 서브픽셀 오프셋을 projection에 적용
    var jittered = projection;
    jittered.M31 += (h.X * 2.0f - 1.0f) / renderW;
    jittered.M32 += (h.Y * 2.0f - 1.0f) / renderH;
    return jittered;
}
```

---

## 13B. Temporal Upscaler 컴퓨트 셰이더

### 13B.1 RenderSystem.cs — Upscaler 리소스

```csharp
// --- FSR Upscaler ---
private Shader? _fsrUpscaleShader;
private Pipeline? _fsrUpscalePipeline;
private ResourceLayout? _fsrUpscaleLayout;
private ResourceSet? _fsrUpscaleSet;

private Texture? _upscaledTexture;        // display resolution, R16G16B16A16_Float
private TextureView? _upscaledView;

private Texture? _historyTexture;         // display resolution, 이전 프레임 결과
private TextureView? _historyView;

private DeviceBuffer? _fsrParamsBuffer;
```

### 13B.2 fsr_upscale.comp — 핵심 셰이더

```glsl
#version 450
layout(local_size_x = 8, local_size_y = 8) in;

// 입력 (render resolution)
layout(set = 0, binding = 0) uniform texture2D colorInput;      // HDR color
layout(set = 0, binding = 1) uniform texture2D depthInput;      // linear depth
layout(set = 0, binding = 2) uniform texture2D velocityInput;   // motion vectors
layout(set = 0, binding = 3) uniform sampler linearSampler;

// 히스토리 (display resolution)
layout(set = 0, binding = 4) uniform texture2D historyInput;    // 이전 프레임 결과

// 출력 (display resolution)
layout(set = 0, binding = 5, rgba16f) uniform writeonly image2D upscaledOutput;

layout(set = 0, binding = 6) uniform FsrParams {
    vec2 RenderSize;       // render resolution
    vec2 DisplaySize;      // display resolution
    vec2 RenderSizeRcp;    // 1.0 / renderSize
    vec2 DisplaySizeRcp;   // 1.0 / displaySize
    vec2 JitterOffset;     // 현재 프레임 지터
    int FrameIndex;
    float _pad;
};

void main()
{
    ivec2 dispPos = ivec2(gl_GlobalInvocationID.xy);
    if (dispPos.x >= int(DisplaySize.x) || dispPos.y >= int(DisplaySize.y)) return;

    vec2 dispUV = (vec2(dispPos) + 0.5) * DisplaySizeRcp;

    // 1) 현재 프레임 샘플 (render resolution → display UV 매핑)
    vec2 renderUV = dispUV;  // UV 공간은 동일
    vec4 currentColor = texture(sampler2D(colorInput, linearSampler), renderUV);
    vec2 velocity = texture(sampler2D(velocityInput, linearSampler), renderUV).rg;

    // 2) 히스토리 reprojection
    vec2 historyUV = dispUV - velocity;
    vec4 historyColor = texture(sampler2D(historyInput, linearSampler), historyUV);

    // 3) Neighborhood clamping (3x3, render resolution)
    vec3 minColor = vec3(1e10);
    vec3 maxColor = vec3(-1e10);
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            vec2 offset = vec2(x, y) * RenderSizeRcp;
            vec3 s = texture(sampler2D(colorInput, linearSampler), renderUV + offset).rgb;
            minColor = min(minColor, s);
            maxColor = max(maxColor, s);
        }
    }
    historyColor.rgb = clamp(historyColor.rgb, minColor, maxColor);

    // 4) Blend factor (히스토리 신뢰도)
    float blendFactor = 0.05;  // 현재 프레임 5%, 히스토리 95%
    // 히스토리 UV가 화면 밖이면 현재 프레임 100%
    if (historyUV.x < 0.0 || historyUV.x > 1.0 ||
        historyUV.y < 0.0 || historyUV.y > 1.0)
        blendFactor = 1.0;

    vec3 result = mix(historyColor.rgb, currentColor.rgb, blendFactor);

    imageStore(upscaledOutput, dispPos, vec4(result, 1.0));
}
```

### 13B.3 렌더 파이프라인 삽입 위치

```
G-Buffer Pass (render res)
    ↓
Shadow Pass
    ↓
SSIL Compute (render res)
    ↓
Ambient + Light Passes → HDR (render res)
    ↓
Forward Pass → HDR (render res)
    ↓
PostProcess (Bloom, Tonemap) → HDR (render res)
    ↓
[신규] Temporal Upscale (compute) → upscaled HDR (display res)
    ↓
[신규] History Copy (upscaled → history)
    ↓
Blit → Swapchain (display res)
```

**PostProcessStack 수정:**
- `Execute()`의 최종 결과를 `upscaledTexture`에 쓰는 대신, 기존 HDR 결과를 upscaler에 전달
- `BlitToSwapchain()`은 `upscaledView`를 소스로 사용

```csharp
// RenderSystem.Render() 내부:
if (RenderSettings.fsrEnabled)
{
    // PostProcess → render res HDR 결과
    _postProcessStack?.ExecuteWithoutBlit(cl, _hdrView!, ...);

    // Temporal Upscale
    UpdateFsrParams(cl, jitterOffset, frameIndex);
    cl.SetPipeline(_fsrUpscalePipeline);
    cl.SetComputeResourceSet(0, _fsrUpscaleSet);
    cl.Dispatch(
        (_displayWidth + 7) / 8,
        (_displayHeight + 7) / 8, 1);

    // History 복사
    cl.CopyTexture(_upscaledTexture, _historyTexture);

    // Blit upscaled → swapchain
    _postProcessStack?.BlitToSwapchain(cl, _upscaledView!, swapchainFB);
}
else
{
    // 기존 경로 (1:1)
    _postProcessStack?.Execute(cl, _hdrView!, swapchainFB);
}
```

### 13B.4 PostProcessStack 수정

`PostProcessStack`에 두 메서드 분리:

```csharp
// 기존 Execute → 내부 이펙트만 실행 (Blit 없이)
public TextureView ExecuteEffectsOnly(CommandList cl, TextureView hdrSourceView)
{
    // ping-pong 체인 실행 후 최종 TextureView 반환
}

// Blit만 별도 호출 가능하도록 public 노출
public void BlitToSwapchain(CommandList cl, TextureView source, Framebuffer swapchainFB)
{
    // 기존 private → public
}
```

---

## 구현 순서

| 단계 | 내용 | 상태 |
|------|------|------|
| 1 | RenderSettings.cs — FSR 배율 파라미터 + enum 추가 | ⏳ |
| 2 | GBuffer.cs — Velocity RT4 (RG16_Float) 추가 | ⏳ |
| 3 | deferred_geometry.vert — PrevViewProjection + prevClipPos 출력 | ⏳ |
| 4 | deferred_geometry.frag — Velocity 출력 (RT4) | ⏳ |
| 5 | RenderSystem.cs — TransformData 구조체 확장 + PrevViewProj 전달 | ⏳ |
| 6 | RenderSystem.cs — render/display 해상도 분리 (CalcRenderResolution) | ⏳ |
| 7 | RenderSystem.cs — CreateSizeDependentResources 분리 적용 | ⏳ |
| 8 | RenderSystem.cs — Jitter 적용 (Halton 시퀀스) | ⏳ |
| 9 | PostProcessStack.cs — ExecuteEffectsOnly / BlitToSwapchain 분리 | ⏳ |
| 10 | `fsr_upscale.comp` 작성 | ⏳ |
| 11 | RenderSystem.cs — Upscaler 파이프라인/리소스 초기화 | ⏳ |
| 12 | RenderSystem.cs — Render()에 upscaler 분기 삽입 | ⏳ |
| 13 | 빌드 + Performance 모드에서 시각 품질 확인 | ⏳ |
| 14 | Resize 시 render/display 텍스처 정상 재생성 확인 | ⏳ |

---

## 검증

- [ ] `dotnet build` 성공
- [ ] `fsrEnabled = false` 시 기존 렌더링과 완전 동일 (회귀 없음)
- [ ] `fsrEnabled = true, NativeAA` 시 1:1 업스케일 — 기존과 시각적 동일
- [ ] `fsrEnabled = true, Performance` 시 렌더 해상도 절반, 디스플레이는 원본
- [ ] 카메라 이동 시 ghosting 없이 깨끗한 업스케일
- [ ] 오브젝트 이동 시 motion vector 정상 동작 (잔상 없음)
- [ ] 윈도우 리사이즈 시 render/display 텍스처 모두 정상 재생성
- [ ] SSIL이 render resolution에서 정상 동작
- [ ] Bloom/Tonemap이 render resolution에서 정상 동작
- [ ] 에디터에서 FSR 배율 모드 변경 가능

---

## 향후 확장

- **Reactive Mask**: 파티클/알파 오브젝트에 대한 반응형 마스크 지원
- **Sharpening Pass**: CAS(Contrast Adaptive Sharpening) 후처리 추가
- **Depth-aware Upscale**: 깊이 기반 엣지 보존 업스케일링 개선
- **Dynamic Resolution**: 프레임 타임 기반 자동 배율 조정
