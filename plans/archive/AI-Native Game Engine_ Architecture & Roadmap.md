# **IronRose: AI-Native .NET 10 게임 엔진 아키텍처 설계 보고서**

> **"Iron for Strength, Rose for Beauty"**
> 현재 상태: Phase 40 완료 (2026-03-02) — 풀 에디터 + Deferred PBR + Animation + Physics + Hot Reload

## **1\. 프로젝트 비전: The "Prompt-to-Play" Engine**

본 프로젝트는 기존의 게임 엔진(Unity, Unreal)이 가진 무거운 에디터 중심의 워크플로우를 탈피하고, **AI(LLM)가 코드를 생성하고 엔진이 이를 즉시 컴파일하여 실행하는** 새로운 패러다임을 제시합니다. .NET 10의 최신 기술을 활용하여 유니티의 방대한 API 생태계를 흡수하되, 내부적으로는 가볍고 빠른 최신 렌더링/메모리 아키텍처를 지향합니다.

## **2\. 엔진 이름: IronRose**

**IronRose** — "Iron for Strength, Rose for Beauty"

금속(Iron)의 강건한 성능과 장미(Rose)의 아름다운 렌더링을 결합한 이름.
`RoseEngine` 네임스페이스로 Unity API 호환성을 제공합니다.

---

## **3\. 핵심 아키텍처: 기술 스택 및 구조**

### **3.1 기반 기술 (Foundation)**

| 레이어 | 기술 | 용도 |
|--------|------|------|
| **Runtime** | .NET 10.0 | JIT + AOT 가능 런타임 |
| **Windowing** | Silk.NET.Windowing (GLFW) | 크로스 플랫폼 윈도우 + Multi-Viewport |
| **Input** | Silk.NET.Input | 키보드/마우스/게임패드 |
| **Graphics** | Veldrid (Vulkan 백엔드) | 저수준 GPU 추상화 |
| **Shader** | Veldrid.SPIRV | GLSL 450 → Vulkan SPIR-V |
| **Editor GUI** | ImGui.NET (cimgui) | 즉시 모드 에디터 UI + Multi-Viewport |
| **Scripting** | Roslyn (Microsoft.CodeAnalysis) | 런타임 C# 컴파일 + 핫 리로드 |
| **Asset Import** | AssimpNet + SharpGLTF.Core | FBX/GLB/OBJ 3D 모델 로드 |
| **Image** | SixLabors.ImageSharp 3.1.12 | PNG/JPG 텍스처 로딩 + 드로잉 |
| **Image → GPU** | Veldrid.ImageSharp | ImageSharp 텍스처 → Veldrid GPU 리소스 변환 |
| **Font** | SixLabors.Fonts | 글리프 아틀라스 래스터라이징 + 한글 지원 |
| **Mesh Optimize** | Meshoptimizer.NET | LOD 생성, 메시 최적화 |
| **Texture Compress** | BCnEncoder.Net | BC5/BC7 GPU 텍스처 압축 |
| **YAML** | YamlDotNet | Unity Scene/Prefab 파싱 |
| **Physics 3D** | BepuPhysics v2.4.0 | 3D 리지드바디 물리 |
| **Physics 2D** | Aether.Physics2D v2.2.0 | 2D 리지드바디 물리 |
| **Serialization** | Tomlyn | TOML 씬/에셋/설정 직렬화 |

---

## **4\. 핵심 기능 구현 (Deep Dive)**

### **4.1 AI 친화적 런타임 코딩 & 핫 리로딩 (The "Heart")**

AI가 생성한 코드를 게임을 끄지 않고 즉시 적용하려면 **AssemblyLoadContext (ALC)**를 활용한 핫 스왑 구조가 필수적입니다.

**구현 메커니즘: FrozenCode / LiveCode 이원화 구조**

1. **IronRose.Engine (EXE, 안정적 기반):** 진입점 + 엔진 코어
   * Silk.NET/Veldrid 초기화, 메인 루프
   * GameObject, Component, Transform, SceneManager
   * 렌더링/물리/애니메이션 시스템
   * ImGui 에디터 오케스트레이션

2. **FrozenCode (컴파일 타임 참조):** 안정된 게임 스크립트
   * `dotnet build` 시 컴파일, RoseEditor/Standalone이 `ProjectReference`로 직접 참조
   * 검증 완료된 컴포넌트 및 데모 씬
   * `/digest` 커맨드로 LiveCode에서 승격

3. **LiveCode (Roslyn 핫 리로드):** 빠른 프로토타입
   * `LiveCodeManager`가 FileSystemWatcher로 *.cs 변경 감지
   * Roslyn으로 런타임 컴파일 → ALC 핫 스왑 (GameObject 보존, 컴포넌트 타입만 마이그레이션)
   * Inspector/SceneSerializer 타입 캐시 자동 무효화

4. **AI Digest (`/digest`):** 검증된 LiveCode를 FrozenCode로 편입
   * IDE에서 열려 있는 LiveCode 파일을 자동으로 FrozenCode 프로젝트로 이동
   * LiveCode는 항상 실험/개발 중인 스크립트만 유지

**장점:**
* 엔진 코어는 항상 안정적
* LiveCode 예외 시 해당 스크립트만 격리
* 에디터 모드에서 GameObject 보존 핫 리로드 (상태 유실 없음)

### **4.2 Unity 호환 아키텍처 (RoseEngine API, 99개 파일)**

AI(LLM)는 인터넷상의 방대한 유니티 코드로 학습되어 있습니다. 따라서 **"using RoseEngine;"** 스타일의 코드를 그대로 실행할 수 있게 하는 것이 핵심입니다.

**RoseEngine API:**

| 카테고리 | 구현 항목 |
|----------|-----------|
| **수학** | Vector2, Vector3, Vector4, Quaternion, Color, Matrix4x4, Mathf |
| **코어** | GameObject, Component, Transform, MonoBehaviour, SceneManager, Object |
| **렌더링** | Camera, Light, Mesh, MeshFilter, MeshRenderer, Material, Texture2D, Shader |
| **입력 (레거시)** | Input (키보드/마우스/게임패드 정적 API) |
| **입력 (액션 기반)** | InputSystem — InputAction, InputActionMap, InputBinding, 2DVector/1DAxis 컴포짓 |
| **물리** | Rigidbody, Rigidbody2D, Collider (Box/Sphere/Capsule 3D+2D), Collision, ForceMode |
| **2D** | Sprite, SpriteRenderer, Font, TextRenderer, Rect |
| **UI (Canvas)** | RectTransform, Canvas, CanvasRenderer, UIText, UIImage, UIButton, UIPanel, UIInputField |
| **애니메이션** | AnimationCurve, AnimationClip, Animator, SpriteAnimation, WrapMode |
| **IBL** | Cubemap, RenderSettings |
| **유틸** | Random, Debug, Time, Screen, Application, Resources, Coroutine, Attributes |

* **단순성 우선:** Shim(껍데기) 레이어나 ECS 변환 없이 Unity의 GameObject/Component 패턴을 직접 구현
* **라이프사이클:** Awake → OnEnable → Start → FixedUpdate(50Hz) → Update → Coroutines → LateUpdate → OnDisable → OnDestroy
* **코루틴:** WaitForSeconds, WaitForEndOfFrame, WaitUntil, WaitWhile, 중첩 코루틴, Invoke/InvokeRepeating

### **4.3 에셋 파이프라인 (Import Pipeline)**

TOML 기반 `.rose` 메타데이터 시스템으로 에셋을 관리합니다.

* **AssetDatabase:** `Assets/` 디렉토리 스캔, GUID→경로 매핑, 에셋 캐싱, 자동 reimport 이벤트
* **GUID 시스템:** `.rose` 메타데이터 파일 자동 생성 (TOML 기반, Unity .meta 대응), 영구 GUID
* **Mesh Import:** AssimpNet + SharpGLTF (GLB/FBX/OBJ), 머티리얼 자동 추출 (albedo, metallic, roughness, emission, 텍스처)
* **Texture Import:** ImageSharp → Veldrid GPU 텍스처, Normal Map / MRO Map PBR 지원
* **Prefab System:** Prefab Asset + Variant + Edit Mode + Instantiate
* **Animation Import:** .anim TOML 파싱/익스포트
* **Sprite Sub-Asset:** GLB 내 임베디드 텍스처 추출, Sprite GUID 보존

### **4.4 씬 직렬화**

* **TOML 기반 씬 포맷:** `.scene` 파일에 GameObject 계층, 컴포넌트 프로퍼티, 에셋 참조 직렬화
* **GameObject 영구 GUID:** 씬 저장/로드 간 오브젝트 식별 보존
* **Custom Component 직렬화:** Vector4, Quaternion, long, double, byte, 배열/리스트, 씬 오브젝트 참조 (GUID 지연 해석), 중첩 `[Serializable]` struct/class
* **씬 환경 설정:** `[sceneEnvironment]` 섹션 (Skybox, Ambient, Sky 설정)

### **4.5 Unity와의 주요 차이점**

#### 스크립트 파일 위치 제한
* Unity에서는 `Assets/` 폴더 하위에 `.cs` 스크립트 파일을 자유롭게 배치하지만, **IronRose에서는 `Assets/` 폴더에 `.cs` 파일을 추가할 수 없음**
* 모든 C# 스크립트는 반드시 **LiveCode** 또는 **FrozenCode** 프로젝트에만 추가해야 함
* `Assets/` 폴더는 텍스처, 모델, 씬, 프리팹 등 **비코드 에셋 전용**

#### Prefab Override 미지원
* Unity에서는 Prefab을 씬에 배치하거나 다른 Prefab 안에 Sub Prefab으로 넣을 때 개별 속성값을 override할 수 있지만, **IronRose에서는 Prefab 인스턴스의 값 override를 지원하지 않음**
* Prefab을 씬에 배치하면 원본 Prefab의 값이 그대로 사용됨
* Sub Prefab (Nested Prefab)도 마찬가지로 원본 값 그대로 사용됨
* **값을 변경하려면 반드시 Prefab Variant를 생성**하여 Variant에서 원하는 값을 수정해야 함

---

## **5\. 렌더링 파이프라인: Forward/Deferred 하이브리드 + PBR**

Forward(Sprite, Text, UI, 투명)와 Deferred(불투명 3D 메시)를 결합한 하이브리드 렌더링 파이프라인.

### **5.1 G-Buffer 설계**

| Render Target | 포맷 | 채널 데이터 |
| :---- | :---- | :---- |
| **RT0 (Albedo)** | R8G8B8A8_UNorm | RGB: Base Color, A: Alpha |
| **RT1 (Normal)** | R16G16B16A16_Float | RGB: World Normal [-1,1], A: Roughness |
| **RT2 (Material)** | R8G8B8A8_UNorm | R: Metallic, G: Occlusion, B: Emission intensity |
| **RT3 (WorldPos)** | R16G16B16A16_Float | RGB: World Position, A: 1.0 (geometry marker) |
| **RT4 (Velocity)** | R16G16_Float | RG: Screen-space motion vectors (temporal upscaling/TAA용) |
| **Depth** | D32_Float_S8_UInt | Hardware Depth |
| **DepthCopy** | R32_Float | Depth R32F 복사본 (컴퓨트 셰이더 샘플링용, SSIL 등) |

### **5.2 렌더링 패스**

```
 1. Shadow Pass       → Shadow Atlas (Directional/Point/Spot 그림자 맵)
 2. Geometry Pass     → G-Buffer에 불투명 3D 메시 기록 (4 MRT + depth, Normal Map 지원)
 3. Lighting Pass     → G-Buffer → HDR 텍스처 (Cook-Torrance PBR + IBL + Shadows)
    - Directional Light Pass (전체 화면)
    - Point Light Pass (라이트 볼륨 구)
    - Spot Light Pass (라이트 볼륨 콘)
    - Ambient/IBL Pass
 4. Skybox Pass       → 큐브맵 기반 스카이박스 렌더링
 5. Forward Pass      → HDR 텍스처에 Sprite/Text/Wireframe 추가
 6. Canvas UI Pass    → 2D UI 오버레이 (RectTransform 기반)
 7. Post-Processing   → Bloom + ACES Tone Mapping + (선택) SSIL + FSR Upscale → Swapchain
 8. Editor Overlay    → ImGui 에디터 UI + Gizmo 렌더링
```

**PBR BRDF**: Cook-Torrance (GGX Distribution + Schlick Fresnel + Smith Geometry)
**IBL**: 큐브맵 기반 Split-sum approximation + 디퓨즈 irradiance
**그림자**: Shadow Atlas 기반 Directional/Point/Spot 그림자 맵 + Blur

### **5.3 셰이더 구성 (48개 파일)**

| 카테고리 | 셰이더 |
|----------|--------|
| **Forward** | vertex.glsl, fragment.glsl |
| **Deferred** | deferred_geometry.\*, deferred_directlight.\*, deferred_pointlight.\*, deferred_spotlight.\*, deferred_ambient.\* |
| **Shadow** | shadow.\*, shadow_point.\*, shadow_atlas.\*, shadow_blur.\* |
| **Post-Processing** | bloom_threshold/composite, gaussian_blur, tonemap/tonemap_composite |
| **Compute** | fsr_upscale/cas, ssil_main/denoise/temporal/prefilter_depth, compress_bc5/bc7 |
| **Editor** | gizmo_line, outline, pick_object, imgui, debug_overlay, preview_material, sceneview_diffuse/matcap |
| **Skybox** | skybox.\* |
| **Utility** | fullscreen.vert, blit.frag |

### **5.4 Post-Processing Stack**

모듈식 이펙트 파이프라인:

* **BloomEffect**: Threshold → Gaussian Blur (9-tap, 2-pass separable) → Composite
* **TonemapEffect**: ACES Filmic Tone Mapping + Gamma 보정
* **PostProcess Volume 시스템**: BoxCollider + blendDistance 기반 카메라 진입/퇴장 감지, weighted average 블렌딩
* **.ppprofile TOML 에셋**: 여러 포스트프로세싱 프로파일 저장/전환
* **Renderer Settings**: .renderer TOML 프로파일 에셋 (FSR/SSIL 14개 프로퍼티)

---

## **6\. 에디터 (RoseEditor)**

ImGui.NET 기반 풀 에디터로, Unity와 유사한 워크플로우를 제공합니다.

### **6.1 에디터 패널 구성**

| 패널 | 기능 |
|------|------|
| **Hierarchy** | GameObject 트리 뷰, 드래그&드롭 재배치, Range Select, Copy/Cut/Paste |
| **Inspector** | 컴포넌트 프로퍼티 편집, Custom Component 자동 감지, Material 에디터, 에셋 참조 드래그&드롭 |
| **Scene View** | 3D/2D 뷰포트, Move/Rotate/Scale/Rect Gizmo, 그리드 스냅, 마우스 래핑, 뷰 모드 (Diffuse/Matcap/Debug) |
| **Project Panel** | 에셋 브라우저, 폴더 탐색, 파일 리네임, .rose 메타데이터 관리 |
| **Animation Editor** | 타임라인 + 커브 에디터, Dopesheet ↔ Curves 모드, Keyframe 편집, Undo/Redo, 이벤트 마커 |
| **Render Settings** | Renderer Profile, Scene Environment (Skybox/Ambient), PostProcess Volume 관리 |
| **Project Settings** | Build 탭 (시작 씬 선택), 일반 설정 |

### **6.2 에디터 주요 기능**

* **Multi-Viewport:** ImGui Multi-Viewport 지원 — 패널을 OS 윈도우로 분리 가능 (독립 Swapchain + CommandList)
* **오브젝트 피킹:** GPU 피킹 (pick_object 셰이더) + Rectangle Selection
* **Gizmo 시스템:** 3D Move/Rotate/Scale + 2D UI RectTransform Gizmo (Rect Tool)
* **Undo/Redo:** 스냅샷 기반 Undo 시스템 (Transform, Component, AnimationClip 등)
* **Prefab 편집:** Prefab Edit Mode 진입/퇴출, Variant 오버라이드
* **3D Collider 편집:** Edit Collider 모드 (Box/Sphere/Capsule 핸들)
* **Debug Overlay:** 렌더링 통계, G-Buffer 시각화
* **한글 UI:** NotoSans 폰트 + 글리프 자동 감지 fallback 체인

### **6.3 Standalone 빌드**

* **IronRose.Standalone 프로젝트:** 에디터 없이 시작 씬을 직접 로드하는 HeadlessEditor 모드
* `rose_projectSettings.toml`에서 `[build] start_scene` 설정으로 시작 씬 지정

---

## **7\. 애니메이션 시스템**

### **7.1 런타임**

* **AnimationCurve:** Keyframe struct + Hermite cubic spline 보간 + Factory methods
* **AnimationClip:** propertyPath별 커브 + AnimationEvent 관리
* **Animator:** 멀티스레드 curve 평가 (Parallel.For, 16타겟 이상 시) + 메인스레드 이벤트 큐
* **SpriteAnimation:** 스프라이트 프레임 애니메이션 (RequireComponent)
* **WrapMode:** Once, Loop, PingPong, ClampForever

### **7.2 에디터**

* **타임라인 + 커브 에디터:** Dopesheet ↔ Curves 모드 전환 (Unity Animation Window 스타일)
* **Keyframe 편집:** 싱글/멀티 셀렉트, Box Select, 드래그 (프레임 그리드 스냅), Copy/Paste
* **Tangent 모드:** Auto/Linear/Constant/Free 프리셋
* **Record Mode:** 씬에서 프로퍼티 변경 시 자동 키프레임 기록
* **Undo/Redo:** 클립 전체 스냅샷 방식
* **.anim TOML 에셋:** AnimationClip 직렬화/역직렬화

---

## **8\. Canvas UI 시스템**

Unity 스타일의 2D UI 시스템:

* **RectTransform:** 앵커, 피벗, 오프셋 기반 2D 레이아웃, KeepVisual 앵커 적용
* **CanvasRenderer:** 회전/스케일 버텍스 트랜스폼, HitTest, 이벤트 시스템
* **UI 위젯:** UIText, UIImage, UIButton, UIPanel (9-슬라이스), UIInputField
* **Rect Tool (T키):** ImGui 2D 오버레이로 UI 요소 크기/이동 핸들 직접 편집
* **2D Gizmo:** RectTransform 선택 시 Move/Rotate/Scale 2D 축 기즈모

---

## **9\. 물리 엔진**

* **3D 물리 (BepuPhysics v2.4.0):** Rigidbody, Box/Sphere/Capsule Collider, 충돌 콜백
* **2D 물리 (Aether.Physics2D v2.2.0):** Rigidbody2D, Box/Circle Collider2D
* **FixedUpdate 50Hz:** 물리 시뮬레이션 고정 타임스텝, 렌더링과 독립
* **Transform↔Physics 동기화:** Dynamic=Physics→Transform, Kinematic=Transform→Physics
* **충돌 콜백:** OnCollisionEnter/Stay/Exit, OnTriggerEnter/Stay/Exit (3D + 2D)
* **Edit Collider 모드:** 에디터에서 콜라이더 핸들 직접 편집

---

## **10\. 리소스 관리**

C#의 GC에만 의존하면 GPU 메모리 해제 시점이 불명확하므로, 명시적인 관리 전략을 사용합니다.

* **AssetDatabase 캐싱:** GUID 기반 에셋 캐싱, 중복 로드 방지
* **Object.Destroy 패턴:** Deferred Destroy 큐 → 프레임 끝 처리, 자식 재귀 파괴 + OnDisable/OnDestroy 호출
* **OnComponentDestroy 다형성:** 컴포넌트별 GPU 리소스 정리 (MeshRenderer, Light, Camera 등)

---

#### **참고 자료**

1. Vulkan Backend \- Veldrid, 2월 13, 2026에 액세스, [https://veldrid.dev/articles/implementation/vulkan.html](https://veldrid.dev/articles/implementation/vulkan.html)
2. Veldrid (3D Graphics Library) Implementation Overview : r/csharp \- Reddit, 2월 13, 2026에 액세스, [https://www.reddit.com/r/csharp/comments/7tb1i2/veldrid\_3d\_graphics\_library\_implementation/](https://www.reddit.com/r/csharp/comments/7tb1i2/veldrid_3d_graphics_library_implementation/)
3. C\# Scripting Engine Part 7 – Hot Reloading • Kah Wei, Tng, 2월 13, 2026에 액세스, [https://kahwei.dev/2023/08/07/c-scripting-engine-part-7-hot-reloading/](https://kahwei.dev/2023/08/07/c-scripting-engine-part-7-hot-reloading/)
4. API proposal: ReferenceCountedDisposable
5. How Rider Hot Reload Works Under the Hood | The .NET Tools Blog, 2월 13, 2026에 액세스, [https://blog.jetbrains.com/dotnet/2021/12/02/how-rider-hot-reload-works-under-the-hood/](https://blog.jetbrains.com/dotnet/2021/12/02/how-rider-hot-reload-works-under-the-hood/)
6. Self-compiled Roslyn build performance, 2월 13, 2026에 액세스, [https://stackoverflow.com/questions/34853273/self-compiled-roslyn-build-performance-not-as-fast-as-originally-shipped-roslyn](https://stackoverflow.com/questions/34853273/self-compiled-roslyn-build-performance-not-as-fast-as-originally-shipped-roslyn)
7. Scripting API: MonoBehaviour \- Unity \- Manual, 2월 13, 2026에 액세스, [https://docs.unity3d.com/6000.3/Documentation/ScriptReference/MonoBehaviour.html](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/MonoBehaviour.html)
8. MonoBehaviour \- Unity \- Manual, 2월 13, 2026에 액세스, [https://docs.unity3d.com/6000.3/Documentation/Manual/class-MonoBehaviour.html](https://docs.unity3d.com/6000.3/Documentation/Manual/class-MonoBehaviour.html)
9. hadashiA/VYaml \- GitHub, 2월 13, 2026에 액세스, [https://github.com/hadashiA/VYaml](https://github.com/hadashiA/VYaml)
10. socialpoint-labs/unity-yaml-parser \- GitHub, 2월 13, 2026에 액세스, [https://github.com/socialpoint-labs/unity-yaml-parser](https://github.com/socialpoint-labs/unity-yaml-parser)
11. UnityYAML \- Unity \- Manual, 2월 13, 2026에 액세스, [https://docs.unity3d.com/6000.3/Documentation/Manual/UnityYAML.html](https://docs.unity3d.com/6000.3/Documentation/Manual/UnityYAML.html)
12. Shaders and Resources \- Veldrid, 2월 13, 2026에 액세스, [https://veldrid.dev/articles/shaders.html](https://veldrid.dev/articles/shaders.html)
13. CanTalat-Yakan/3DEngine \- GitHub, 2월 13, 2026에 액세스, [https://github.com/CanTalat-Yakan/3DEngine](https://github.com/CanTalat-Yakan/3DEngine)
14. What is Unity GUID \- Makaka Games, 2월 13, 2026에 액세스, [https://makaka.org/unity-tutorials/guid](https://makaka.org/unity-tutorials/guid)
15. Part 2 \- Veldrid, 2월 13, 2026에 액세스, [https://veldrid.dev/articles/getting-started/getting-started-part2.html](https://veldrid.dev/articles/getting-started/getting-started-part2.html)
16. Messing with Unity's GUIDs \- BorisTheBrave.Com, 2월 13, 2026에 액세스, [https://www.boristhebrave.com/2020/02/05/messing-with-unitys-guids/](https://www.boristhebrave.com/2020/02/05/messing-with-unitys-guids/)
