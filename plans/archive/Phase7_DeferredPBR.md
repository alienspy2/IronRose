# Phase 7: Deferred Rendering & PBR ✅ (2026-02-15 완료)

> **커밋**: `1197355` (G-Buffer + PBR), `3049434` (IBL), `4cc5e4d` (PostProcessing 모듈화)

## 목표
Forward → Deferred 하이브리드 렌더링 파이프라인으로 전환하여 PBR 머티리얼과 다중 라이트를 지원합니다.

**핵심 원칙:**
- 기존 Forward 렌더러(Sprite, Text, 투명 오브젝트)와 공존하는 하이브리드 구조
- 현재 Resource Layout 구조(`set 0` = per-object, `set 1` = per-frame)를 최대한 유지
- HDR 파이프라인: Geometry → Lighting → Post-Processing → Swapchain

---

## 현재 렌더링 상태 (Phase 6 기준)

| 항목 | 현재 구현 |
|---|---|
| 렌더링 방식 | Forward (단일 패스) |
| 라이팅 | Lambert + Blinn-Phong, 최대 8개 |
| Material | `color`, `mainTexture`, `emission` 만 존재 |
| 셰이더 | vertex.glsl + fragment.glsl (GLSL 450) |
| Resource Layout | set 0: `{Transforms, MaterialData, MainTexture, MainSampler}`, set 1: `{LightData}` |
| 특수 렌더링 | SpriteRenderer, TextRenderer (unlit, alpha blend, 별도 파이프라인) |
| 출력 대상 | Swapchain Framebuffer 직접 출력 |

---

## 작업 항목

### 7.1 Material 확장

PBR에 필요한 속성을 `Material.cs`에 추가합니다.

**추가 속성:**
```csharp
// Material.cs (RoseEngine namespace)
public class Material
{
    // 기존
    public Color color { get; set; } = Color.white;
    public Texture2D? mainTexture { get; set; }
    public Color emission { get; set; } = Color.black;

    // PBR 추가
    public float metallic { get; set; } = 0.0f;       // 0 = 비금속, 1 = 금속
    public float roughness { get; set; } = 0.5f;       // 0 = 매끄러움, 1 = 거침
    public float occlusion { get; set; } = 1.0f;       // 0 = 완전 차폐, 1 = 노출
    public Texture2D? normalMap { get; set; }           // 탄젠트 공간 노멀맵
    public Texture2D? MROMap { get; set; }               // R: metallic, G: roughness, B: occlusion
}
```

**GPU Uniform 확장:**
```csharp
// RenderSystem.cs
[StructLayout(LayoutKind.Sequential)]
internal struct MaterialUniforms
{
    public Vector4 Color;
    public Vector4 Emission;
    public float HasTexture;
    public float Metallic;       // 추가
    public float Roughness;      // 추가
    public float Occlusion;      // 추가
}
```

**검증:** 빌드 성공, 기존 Forward 렌더링 정상 동작 (추가 속성은 아직 미사용)

---

### 7.2 G-Buffer 생성

**G-Buffer 레이아웃:**
| RT | 포맷 | 내용 |
|---|---|---|
| RT0 (Albedo) | R8G8B8A8_UNorm | RGB: Base Color, A: Alpha |
| RT1 (Normal) | R16G16B16A16_Float | RGB: World Normal, A: Roughness |
| RT2 (Material) | R8G8B8A8_UNorm | R: Metallic, G: Occlusion, B: Emission intensity, A: unused |
| RT3 (WorldPos) | R16G16B16A16_Float | RGB: World Position, A: 1.0 (geometry marker) |
| Depth | D32_Float_S8_UInt | Hardware depth (Geometry Pass 전용) |

> **설계 결정:** 초기에는 DepthCopy(R32_Float) + InverseViewProjection 행렬로 World Position을 복원하려 했으나,
> Veldrid Vulkan 백엔드의 MRT 호환성 문제와 depth 복사 복잡도를 고려하여 RT3에 World Position을 직접 기록하는 방식으로 변경.
> RGBA16F 대역폭 비용이 있지만, 정밀도와 안정성 면에서 우수합니다.

> **RT1 포맷 선택:** Normal은 [-1,1] 범위를 정밀하게 보존해야 하므로 R16G16B16A16_Float 사용.
> R8G8B8A8_UNorm에 `n*0.5+0.5` 인코딩은 8bit 정밀도 한계로 PBR 라이팅에서 banding이 발생합니다.

> **⚠️ MRT BlendState 주의:** Veldrid Vulkan 백엔드에서 `BlendStateDescription.SingleOverrideBlend`는
> blend attachment를 1개만 제공하므로, MRT(4개 color target) 사용 시 RT1~RT3에 데이터가 기록되지 않습니다.
> 반드시 color target 수만큼 명시적으로 `BlendAttachmentDescription`을 제공해야 합니다.

**GBuffer.cs (IronRose.Rendering):**
```csharp
using Veldrid;

namespace IronRose.Rendering
{
    public class GBuffer : IDisposable
    {
        public Texture AlbedoTexture { get; private set; } = null!;
        public Texture NormalTexture { get; private set; } = null!;
        public Texture MaterialTexture { get; private set; } = null!;
        public Texture DepthTexture { get; private set; } = null!;
        public Texture WorldPosTexture { get; private set; } = null!;

        public TextureView AlbedoView { get; private set; } = null!;
        public TextureView NormalView { get; private set; } = null!;
        public TextureView MaterialView { get; private set; } = null!;
        public TextureView WorldPosView { get; private set; } = null!;

        public Framebuffer Framebuffer { get; private set; } = null!;

        public uint Width { get; private set; }
        public uint Height { get; private set; }

        public void Initialize(GraphicsDevice device, uint width, uint height)
        {
            Dispose();

            Width = width;
            Height = height;
            var factory = device.ResourceFactory;

            // RT0: Albedo (RGBA8)
            AlbedoTexture = factory.CreateTexture(TextureDescription.Texture2D(
                width, height, 1, 1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.RenderTarget | TextureUsage.Sampled));

            // RT1: Normal + Roughness (RGBA16F — 정밀도 보존)
            NormalTexture = factory.CreateTexture(TextureDescription.Texture2D(
                width, height, 1, 1,
                PixelFormat.R16_G16_B16_A16_Float,
                TextureUsage.RenderTarget | TextureUsage.Sampled));

            // RT2: Material (RGBA8)
            MaterialTexture = factory.CreateTexture(TextureDescription.Texture2D(
                width, height, 1, 1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.RenderTarget | TextureUsage.Sampled));

            // Depth-Stencil (렌더링 전용)
            DepthTexture = factory.CreateTexture(TextureDescription.Texture2D(
                width, height, 1, 1,
                PixelFormat.D32_Float_S8_UInt,
                TextureUsage.DepthStencil));

            // RT3: World Position (RGBA16F — written directly by geometry shader)
            WorldPosTexture = factory.CreateTexture(TextureDescription.Texture2D(
                width, height, 1, 1,
                PixelFormat.R16_G16_B16_A16_Float,
                TextureUsage.RenderTarget | TextureUsage.Sampled));

            // TextureViews
            AlbedoView = factory.CreateTextureView(AlbedoTexture);
            NormalView = factory.CreateTextureView(NormalTexture);
            MaterialView = factory.CreateTextureView(MaterialTexture);
            WorldPosView = factory.CreateTextureView(WorldPosTexture);

            // Framebuffer (4 color + 1 depth)
            Framebuffer = factory.CreateFramebuffer(new FramebufferDescription(
                DepthTexture,
                AlbedoTexture,
                NormalTexture,
                MaterialTexture,
                WorldPosTexture));
        }

        public void Dispose()
        {
            AlbedoView?.Dispose();
            NormalView?.Dispose();
            MaterialView?.Dispose();
            WorldPosView?.Dispose();
            Framebuffer?.Dispose();
            AlbedoTexture?.Dispose();
            NormalTexture?.Dispose();
            MaterialTexture?.Dispose();
            DepthTexture?.Dispose();
            WorldPosTexture?.Dispose();
        }
    }
}
```

**윈도우 리사이즈 대응:**
- `GraphicsManager`의 `_window.Resize` 이벤트에서 `GBuffer.Initialize()` 재호출
- `Dispose()` → `Initialize()` 순서로 안전하게 재생성

**검증:** G-Buffer 텍스처 4개 + depth가 올바른 해상도로 생성됨, 리사이즈 시 재생성 확인

---

### 7.3 Geometry Pass

불투명 3D 오브젝트의 머티리얼 정보를 G-Buffer에 기록합니다.

**Resource Layout (기존 구조 유지):**
- set 0 (per-object): `Transforms(UBO)` + `MaterialData(UBO)` + `MainTexture` + `MainSampler`
- set 1 (per-frame): 사용 안 함 (Geometry Pass에서는 라이트 불필요)

> Transform UBO는 기존 `{World, ViewProjection}` 구조를 그대로 사용합니다.
> MaterialUniforms에 추가된 Metallic/Roughness/Occlusion 필드를 셰이더에서 읽습니다.

**Shaders/deferred_geometry.vert:**
```glsl
#version 450

layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Normal;
layout(location = 2) in vec2 UV;

layout(set = 0, binding = 0) uniform Transforms
{
    mat4 World;
    mat4 ViewProjection;
};

layout(location = 0) out vec3 fsin_Normal;
layout(location = 1) out vec2 fsin_UV;
layout(location = 2) out vec3 fsin_WorldPos;

void main()
{
    vec4 worldPos = World * vec4(Position, 1.0);
    gl_Position = ViewProjection * worldPos;

    fsin_Normal = normalize(mat3(World) * Normal);
    fsin_UV = UV;
    fsin_WorldPos = worldPos.xyz;
}
```

**Shaders/deferred_geometry.frag:**
```glsl
#version 450

layout(location = 0) in vec3 fsin_Normal;
layout(location = 1) in vec2 fsin_UV;
layout(location = 2) in vec3 fsin_WorldPos;

// MRT 출력 (4개)
layout(location = 0) out vec4 gAlbedo;     // RT0: R8G8B8A8_UNorm
layout(location = 1) out vec4 gNormal;     // RT1: R16G16B16A16_Float
layout(location = 2) out vec4 gMaterial;   // RT2: R8G8B8A8_UNorm
layout(location = 3) out vec4 gWorldPos;   // RT3: R16G16B16A16_Float

layout(set = 0, binding = 1) uniform MaterialData
{
    vec4 Color;
    vec4 Emission;
    float HasTexture;
    float Metallic;
    float Roughness;
    float Occlusion;
};

layout(set = 0, binding = 2) uniform texture2D MainTexture;
layout(set = 0, binding = 3) uniform sampler MainSampler;

void main()
{
    // Albedo
    vec4 texColor = vec4(1.0);
    if (HasTexture > 0.5)
    {
        texColor = texture(sampler2D(MainTexture, MainSampler), fsin_UV);
    }
    gAlbedo = Color * texColor;

    // Normal (RGBA16F — store [-1,1] directly) + Roughness
    vec3 normal = normalize(fsin_Normal);
    gNormal = vec4(normal, Roughness);

    // Material: Metallic, Occlusion, Emission intensity
    float emissionIntensity = max(Emission.r, max(Emission.g, Emission.b));
    gMaterial = vec4(Metallic, Occlusion, emissionIntensity, 1.0);

    // World position (direct storage — avoids depth reconstruction issues)
    gWorldPos = vec4(fsin_WorldPos, 1.0);
}
```

**Geometry Pipeline 설정:**
- `Outputs`: G-Buffer Framebuffer의 OutputDescription (4 color + 1 depth)
- `DepthStencilState`: 기존과 동일 (LessEqual, write enabled)
- `RasterizerState`: 기존과 동일 (Back cull, Solid)
- `ResourceLayouts`: 기존 per-object layout 재사용 (set 1은 dummy 또는 제거)
- `BlendState`: 4개 MRT 모두에 명시적 BlendAttachmentDescription 필요 (아래 참고)

```csharp
// ⚠️ 중요: SingleOverrideBlend 대신 4개 attachment를 명시적으로 제공
var gBufferBlend = new BlendStateDescription
{
    AttachmentStates = new[]
    {
        BlendAttachmentDescription.OverrideBlend, // RT0: Albedo
        BlendAttachmentDescription.OverrideBlend, // RT1: Normal
        BlendAttachmentDescription.OverrideBlend, // RT2: Material
        BlendAttachmentDescription.OverrideBlend, // RT3: WorldPos
    }
};
```

**검증:** 불투명 메시가 G-Buffer 4개 RT에 올바르게 기록됨

---

### 7.4 Lighting Pass (PBR)

풀스크린 삼각형에서 G-Buffer를 샘플링하여 PBR 라이팅을 계산합니다.

**World Position:**
- RT3(WorldPos)에서 직접 읽음 — depth 복원 불필요
- `worldPosData.a < 0.5`로 배경 픽셀 감지 (geometry가 없는 픽셀은 alpha=0)

**다중 라이트 지원:**
- 기존 `LightInfo[8]` 구조를 확장하여 최대 64개 라이트 지원
- Directional + Point 라이트 모두 지원

**Fullscreen Triangle:**
- Vertex buffer 불필요 — `gl_VertexIndex`로 오버사이즈 삼각형 생성
- `cl.Draw(3, 1, 0, 0)`으로 렌더링

**Lighting Uniform:**
```csharp
[StructLayout(LayoutKind.Sequential)]
internal struct DeferredLightHeader
{
    public Vector4 CameraPos;                    // 16 bytes
    public int LightCount;                       // 4 bytes
    public int _pad1, _pad2, _pad3;              // 12 bytes
    // Total: 32 bytes — followed by LightInfoGPU[64] in the buffer
}
```

**Shaders/deferred_lighting.frag (핵심 부분):**
```glsl
// G-Buffer textures
layout(set = 0, binding = 0) uniform texture2D gAlbedo;
layout(set = 0, binding = 1) uniform texture2D gNormal;
layout(set = 0, binding = 2) uniform texture2D gMaterial;
layout(set = 0, binding = 3) uniform texture2D gWorldPos;
layout(set = 0, binding = 4) uniform sampler gSampler;

layout(set = 0, binding = 5) uniform LightingBuffer
{
    vec4 CameraPos;
    int LightCount;
    int _lpad1, _lpad2, _lpad3;
    LightInfo Lights[64];
};

void main()
{
    // G-Buffer sampling
    vec4 worldPosData = texture(sampler2D(gWorldPos, gSampler), fsin_UV);

    // Background pixel (no geometry written) -> skip
    if (worldPosData.a < 0.5)
    {
        fsout_Color = vec4(0.0, 0.0, 0.0, 0.0);   // alpha=0 → 배경
        return;
    }

    vec3 worldPos = worldPosData.xyz;
    // ... Cook-Torrance BRDF 계산 ...

    // Ambient (IBL 미구현 — 0.1 상수 사용)
    vec3 ambient = vec3(0.1) * albedo * occlusion;
    vec3 color = ambient + Lo + emissionIntensity * albedo;

    // HDR linear 출력 (alpha=1 → geometry 존재)
    fsout_Color = vec4(color, 1.0);
}
```

**PBR BRDF:** Cook-Torrance (GGX Distribution + Schlick Fresnel + Smith Geometry)

**Lighting Pipeline 설정:**
- 입력: Fullscreen triangle (vertex buffer 불필요, gl_VertexIndex 사용)
- 출력: HDR 중간 텍스처 (R16G16B16A16_Float)
- Depth test: 비활성
- Blend: 비활성 (덮어쓰기)

**검증:** G-Buffer 데이터로부터 PBR 라이팅 결과가 HDR 텍스처에 정확히 기록됨

---

### 7.5 Forward/Deferred 하이브리드 파이프라인

Deferred 렌더링과 기존 Forward 렌더러를 결합합니다.

**렌더링 순서:**
```
1. Geometry Pass     → G-Buffer에 불투명 3D 메시 기록 (4 MRT + depth)
2. Lighting Pass     → G-Buffer → HDR 텍스처 (PBR 라이팅, fullscreen triangle)
3. Forward Pass      → HDR 텍스처에 추가 렌더링:
   3a. Sprite 렌더러  (unlit, alpha blend — 기존 코드 유지)
   3b. Text 렌더러   (unlit, alpha blend — 기존 코드 유지)
   3c. Wireframe 오버레이 (Debug.wireframe)
   3d. 투명 3D 오브젝트 (추후 확장)
4. Post-Processing   → HDR → Swapchain (Bloom + ACES Tone Mapping + Gamma)
```

> **Note:** Depth Copy 단계가 제거됨 — World Position을 RT3에 직접 기록하므로 depth 복사 불필요.

**RenderSystem.Render() 분기:**
```csharp
public void Render(CommandList cl, Camera? camera, float aspectRatio)
{
    if (camera == null) return;

    var viewProj = camera.GetViewMatrix().ToNumerics()
                 * camera.GetProjectionMatrix(aspectRatio).ToNumerics();

    // 1. Geometry Pass (G-Buffer: 4 MRT)
    cl.SetFramebuffer(_gBuffer.Framebuffer);
    cl.ClearColorTarget(0, RgbaFloat.Clear);   // Albedo
    cl.ClearColorTarget(1, RgbaFloat.Clear);   // Normal
    cl.ClearColorTarget(2, RgbaFloat.Clear);   // Material
    cl.ClearColorTarget(3, RgbaFloat.Clear);   // WorldPos (alpha=0 → background)
    cl.ClearDepthStencil(1f);
    cl.SetPipeline(_geometryPipeline);
    DrawOpaqueRenderers(cl, viewProj);

    // 2. Lighting Pass → HDR 텍스처
    cl.SetFramebuffer(_hdrFramebuffer);
    cl.ClearColorTarget(0, RgbaFloat.Clear);
    cl.SetPipeline(_lightingPipeline);
    UploadDeferredLightData(cl, camera);
    cl.Draw(3, 1, 0, 0);  // Fullscreen triangle

    // 3. Forward Pass → HDR 텍스처에 추가
    DrawSprites(cl, viewProj, camera);
    DrawTexts(cl, viewProj, camera);
    if (Debug.wireframe) DrawWireframe(cl, viewProj);

    // 4. Post-Processing → Swapchain
    _postProcessing.Execute(cl, _device.SwapchainFramebuffer);
}
```

**HDR 중간 텍스처:**
- 포맷: R16G16B16A16_Float
- 용도: RenderTarget | Sampled
- Forward Pass에서도 이 텍스처에 렌더링 (공통 HDR 파이프라인)

**검증:** 불투명 3D → PBR 라이팅 + Sprite/Text → 정상 합성, 기존 데모 씬 정상 동작

---

### 7.6 Post-Processing

HDR 텍스처를 LDR로 변환하여 Swapchain에 출력합니다.

**파이프라인:**
```
HDR 텍스처 → [Bloom Threshold] → 1/4 해상도 텍스처
           → [Gaussian Blur H] → [Gaussian Blur V] (2-pass separable)
           → [Composite + Tone Mapping + Gamma]
           → Swapchain
```

**Shaders/bloom_threshold.frag:**
```glsl
#version 450

layout(location = 0) in vec2 fsin_UV;
layout(location = 0) out vec4 fsout_Color;

layout(set = 0, binding = 0) uniform texture2D SourceTexture;
layout(set = 0, binding = 1) uniform sampler SourceSampler;

layout(set = 0, binding = 2) uniform BloomParams
{
    float Threshold;
    float SoftKnee;
    float _pad1;
    float _pad2;
};

void main()
{
    vec3 color = texture(sampler2D(SourceTexture, SourceSampler), fsin_UV).rgb;
    float brightness = dot(color, vec3(0.2126, 0.7152, 0.0722));

    float knee = Threshold * SoftKnee;
    float soft = brightness - Threshold + knee;
    soft = clamp(soft, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee + 0.00001);

    float contribution = max(soft, brightness - Threshold) / max(brightness, 0.00001);
    fsout_Color = vec4(color * max(contribution, 0.0), 1.0);
}
```

**Shaders/gaussian_blur.frag:**
```glsl
#version 450

layout(location = 0) in vec2 fsin_UV;
layout(location = 0) out vec4 fsout_Color;

layout(set = 0, binding = 0) uniform texture2D SourceTexture;
layout(set = 0, binding = 1) uniform sampler SourceSampler;

layout(set = 0, binding = 2) uniform BlurParams
{
    vec2 Direction;    // (1/width, 0) 또는 (0, 1/height)
    float _pad1;
    float _pad2;
};

// 9-tap Gaussian kernel
const float weights[5] = float[](0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216);

void main()
{
    vec3 result = texture(sampler2D(SourceTexture, SourceSampler), fsin_UV).rgb * weights[0];

    for (int i = 1; i < 5; i++)
    {
        vec2 offset = Direction * float(i);
        result += texture(sampler2D(SourceTexture, SourceSampler), fsin_UV + offset).rgb * weights[i];
        result += texture(sampler2D(SourceTexture, SourceSampler), fsin_UV - offset).rgb * weights[i];
    }

    fsout_Color = vec4(result, 1.0);
}
```

**Shaders/tonemap_composite.frag:**
```glsl
#version 450

layout(location = 0) in vec2 fsin_UV;
layout(location = 0) out vec4 fsout_Color;

layout(set = 0, binding = 0) uniform texture2D HDRTexture;
layout(set = 0, binding = 1) uniform texture2D BloomTexture;
layout(set = 0, binding = 2) uniform sampler TexSampler;

layout(set = 0, binding = 3) uniform TonemapParams
{
    float BloomIntensity;
    float Exposure;
    float _pad1;
    float _pad2;
};

// ACES Filmic Tone Mapping
vec3 ACESFilm(vec3 x)
{
    float a = 2.51;
    float b = 0.03;
    float c = 2.43;
    float d = 0.59;
    float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

void main()
{
    vec4 hdrSample = texture(sampler2D(HDRTexture, TexSampler), fsin_UV);
    vec3 hdr = hdrSample.rgb;
    float alpha = hdrSample.a;   // 배경 픽셀은 alpha=0
    vec3 bloom = texture(sampler2D(BloomTexture, TexSampler), fsin_UV).rgb;

    // Bloom 합성
    vec3 color = hdr + bloom * BloomIntensity;

    // Exposure
    color *= Exposure;

    // ACES Tone Mapping
    color = ACESFilm(color);

    // Gamma correction (linear → sRGB)
    color = pow(color, vec3(1.0 / 2.2));

    // Preserve HDR alpha so background (alpha=0) shows the clear color
    fsout_Color = vec4(color, alpha);
}
```

> **Clear Color 보존:** Composite pipeline에서 alpha blending (SrcAlpha, InvSrcAlpha) 사용.
> 배경 픽셀(alpha=0)은 swapchain clear color가 그대로 보이고, geometry 픽셀(alpha=1)은 tone-mapped 색상으로 덮어씁니다.

**PostProcessing.cs (IronRose.Rendering):**
```csharp
public class PostProcessing : IDisposable
{
    private Pipeline _bloomThresholdPipeline;
    private Pipeline _blurPipeline;          // H/V 공유
    private Pipeline _compositePipeline;     // alpha blending 사용

    private Texture _bloomHalf;              // 1/2 해상도
    private Texture _blurPingPong;           // blur 중간 결과

    public float BloomThreshold { get; set; } = 1.0f;
    public float BloomSoftKnee { get; set; } = 0.5f;
    public float BloomIntensity { get; set; } = 0.5f;
    public float Exposure { get; set; } = 1.0f;

    public void Execute(CommandList cl, Framebuffer swapchainFB)
    {
        // 1. Bloom threshold → _bloomHalf (1/2 해상도)
        // 2. Gaussian blur H → _blurPingPong
        // 3. Gaussian blur V → _bloomHalf
        // 4. Composite (HDR + Bloom) + Tone Mapping → Swapchain (alpha blending)
    }
}
```

**Composite Pipeline BlendState:**
```csharp
// Alpha blending — 배경 픽셀(alpha=0)에서 swapchain clear color 보존
var compositeBlend = new BlendStateDescription(
    RgbaFloat.Black,
    new BlendAttachmentDescription(
        blendEnabled: true,
        sourceColorFactor: BlendFactor.SourceAlpha,
        destinationColorFactor: BlendFactor.InverseSourceAlpha,
        colorFunction: BlendFunction.Add,
        sourceAlphaFactor: BlendFactor.One,
        destinationAlphaFactor: BlendFactor.InverseSourceAlpha,
        alphaFunction: BlendFunction.Add));
```

**검증:** Bloom 효과 가시, ACES tone mapping 정상, 배경 clear color 보존

---

### 7.7 PBR 데모

Phase 7 기능을 검증하는 데모 씬을 작성합니다.

**PBRDemo.cs (IronRose.Demo):**
```
씬 구성:
- 5x5 구체 그리드: metallic(행 0→1, 아래→위) × roughness(열 0→1, 왼→오)
- Directional light 1개 (주광, LookAt(0,0,0) from upper-left)
- Point light 4개 (Red, Green, Blue, White — HDR intensity, front-facing)
- 바닥 평면 (metallic=0, roughness=0.7)
- TextRenderer 라벨: 타이틀, 축 이름, 행/열 값
- 카메라: z=-14 (와이드 뷰)
```

**검증 기준:**
- metallic=0 행: 비금속(플라스틱) 느낌, 확산 반사 우세
- metallic=1 행: 금속 느낌, 반사광이 albedo 색상 반영
- roughness 0→1: 날카로운 하이라이트 → 거칠고 흐릿한 반사
- Point light 색상이 주변 구체에 정확히 반영
- Bloom이 밝은 하이라이트에서 발생
- TextRenderer 라벨로 축 의미 확인 가능
- 기존 SpriteDemo/TextDemo 위에 겹쳐도 정상 동작

---

## 새로 생성/수정되는 파일 목록

| 파일 | 작업 | 위치 |
|---|---|---|
| `Material.cs` | **수정** | src/IronRose.Engine/RoseEngine/ |
| `RenderSystem.cs` | **대폭 수정** | src/IronRose.Engine/ |
| `GraphicsManager.cs` | **수정** (리사이즈 이벤트에 GBuffer 재생성 연결) | src/IronRose.Rendering/ |
| `GBuffer.cs` | **신규** | src/IronRose.Rendering/ |
| `PostProcessing.cs` | **신규** | src/IronRose.Rendering/ |
| `deferred_geometry.vert` | **신규** | Shaders/ |
| `deferred_geometry.frag` | **신규** | Shaders/ |
| `deferred_lighting.vert` | **신규** | Shaders/ |
| `deferred_lighting.frag` | **신규** | Shaders/ |
| `bloom_threshold.frag` | **신규** | Shaders/ |
| `gaussian_blur.frag` | **신규** | Shaders/ |
| `tonemap_composite.frag` | **신규** | Shaders/ |
| `fullscreen.vert` | **신규** (bloom/blur/composite 공용) | Shaders/ |
| `PBRDemo.cs` | **신규** | src/IronRose.Demo/ |

---

## 구현 과정에서 발견된 이슈 및 해결

### MRT BlendState 문제 (Black Screen 원인)
`BlendStateDescription.SingleOverrideBlend`는 blend attachment를 1개만 제공합니다.
Veldrid Vulkan 백엔드에서 MRT(4 color target) 파이프라인에 사용하면 RT0만 기록되고 RT1~RT3은 데이터가 기록되지 않아 화면이 검게 표시됩니다.

**해결:** 4개 `BlendAttachmentDescription.OverrideBlend`를 명시적으로 제공.

### Clear Color 미표시
PostProcessing composite 셰이더가 배경 픽셀에 opaque black(alpha=1)을 출력하여 swapchain clear color를 덮어씀.

**해결:** HDR alpha를 composite 셰이더에서 보존 + composite pipeline에 alpha blending 적용.

### Depth Copy 방식 → World Position 직접 기록
초기 설계에서는 depth를 R32_Float로 복사 후 InverseViewProjection으로 world position을 복원하려 했으나,
MRT blend 문제와 depth 복사 호환성 이슈로 RT3에 world position을 직접 기록하는 방식으로 변경.

---

## 검증 기준

- [x] 금속/플라스틱 재질이 물리적으로 정확하게 렌더링됨 (5x5 구체 그리드) ✅
- [x] Directional + Point 라이트가 PBR로 정확히 계산됨 ✅
- [x] Bloom 효과가 밝은 영역에서 가시적 ✅
- [x] ACES Tone Mapping + Gamma 보정 적용 ✅
- [x] Sprite/Text 렌더링이 Deferred 전환 후에도 정상 동작 ✅
- [x] 배경 clear color가 정상 표시됨 ✅
- [x] TextRenderer 라벨로 PBR 파라미터 축 의미 확인 가능 ✅
- [x] 기존 Forward 셰이더(vertex.glsl, fragment.glsl)는 fallback으로 보존 ✅
- [x] 큐브맵 기반 IBL (Split-sum PBR + 디퓨즈 irradiance) ✅
- [x] 스카이박스 렌더링 + CameraClearFlags ✅
- [x] PostProcessing 모듈화 (BloomEffect + TonemapEffect 분리) ✅
- [ ] 윈도우 리사이즈 시 G-Buffer + Post-Processing 텍스처 정상 재생성 (미확인)

---

## 다음 단계
Phase 8: AI 통합
