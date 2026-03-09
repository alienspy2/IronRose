# Phase 20 — Scene View

## Context

IronRose Editor에 Unity의 Scene View에 해당하는 편집 뷰포트를 추가한다.
현재는 Game View(Camera.main 시점)만 존재하여, 씬을 자유롭게 탐색하거나 오브젝트를 시각적으로 선택/조작할 수 없다.

**목표**: Scene View 패널 + 에디터 카메라(Fly/Orbit/Pan/Zoom) + 렌더링 모드 선택 + 무한 XZ 그리드 + GPU 피킹 오브젝트 선택 + 선택 아웃라인 + Transform Gizmo(이동/회전/스케일)

---

## 핵심 설계 — 렌더링 모드

Scene View는 **4가지 렌더링 모드**를 툴바에서 전환할 수 있다.

```csharp
public enum SceneViewRenderMode
{
    Wireframe,    // 와이어프레임만
    MatCap,       // MatCap 텍스처 기반 셰이딩 (뷰 공간 법선 → UV). Solid 프리셋 선택 시 그레이스케일 효과
    DiffuseOnly,  // Albedo + 기본 디렉셔널 라이팅 (PBR 없음)
    WYSIWYG,      // Game View와 완전히 동일한 Deferred PBR
}
```

| 모드 | 렌더러 | 설명 |
|------|--------|------|
| **Wireframe** | `SceneViewRenderer` | 와이어프레임 폴리곤. 엣지 구조 확인용 |
| **MatCap** | `SceneViewRenderer` | MatCap 텍스처 기반 셰이딩. 뷰 공간 법선을 UV로 사용하여 구체 환경맵 샘플링. 프리셋 콤보로 전환 (Solid/Clay/Metal/Glossy/Skin/Red Wax). Solid 프리셋 선택 시 기존 그레이스케일 Solid 모드와 동일 효과 |
| **DiffuseOnly** | `SceneViewRenderer` | Albedo 텍스처 + 기본 디렉셔널 라이트 1개. PBR/IBL/SSIL 없음. 색상·질감 확인용 |
| **WYSIWYG** | `RenderSystem` | 기존 Deferred PBR 파이프라인 그대로 사용. Game View와 동일한 최종 화면 |

**아키텍처 분기**:
- **Wireframe / MatCap / DiffuseOnly** → `SceneViewRenderer` (간소화된 Forward 렌더러, 자체 리소스)
- **WYSIWYG** → 기존 `RenderSystem.Render()` 재호출 (EditorCamera를 Camera 프록시로 래핑)

**WYSIWYG 해상도 전략**: `RenderSystem`은 해상도별 내부 리소스(G-Buffer, HDR, SSIL 등)를 보유하므로,
Scene View와 Game View의 해상도가 다르면 `Resize()`가 필요하다.
이를 회피하기 위해 WYSIWYG 모드에서는 **Game View의 내부 렌더 해상도를 그대로 공유**하고,
Scene View 패널에서는 결과를 스케일링하여 표시한다.
→ `RenderSystem.Resize()` 호출 없이 동일 프레임 내 두 번 `Render()` 가능.

---

## 구현 단계

### Step 1. EditorSelection — 선택 상태 중앙화

현재 `ImGuiHierarchyPanel`에 로컬로 존재하는 선택 상태를 공유 싱글턴으로 추출.
Scene View 피킹, Inspector, Gizmo 모두 이 상태를 참조한다.

**새 파일**: `src/IronRose.Engine/Editor/EditorSelection.cs`
- `static int? SelectedGameObjectId`
- `static long SelectionVersion`
- `static GameObject? SelectedGameObject` (SceneManager.AllGameObjects에서 조회)
- `static void Select(int? id)` / `static void SelectGameObject(GameObject? go)`

**수정**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiHierarchyPanel.cs`
- `SelectedGameObjectId` → `EditorSelection.SelectedGameObjectId` 위임
- 클릭 시 `EditorSelection.Select(id)` 호출

---

### Step 2. EditorCamera — 에디터 전용 카메라

GameObject/Component가 아닌 독립 클래스. ImGui 입력을 직접 사용하여 게임 Input 시스템과 충돌 없음.

**새 파일**: `src/IronRose.Engine/Editor/SceneView/EditorCamera.cs`

속성:
- `Position` (Vector3), `Rotation` (Quaternion), `Pivot` (Vector3)
- `FieldOfView` (60f), `NearClip` (0.01f), `FarClip` (5000f)
- `Forward`, `Right`, `Up` (computed)

메서드:
- `GetViewMatrix()` → Matrix4x4.LookAt
- `GetProjectionMatrix(float aspect)` → Matrix4x4.Perspective
- `Update(float dt, SceneViewInputState input)` — 카메라 모드 처리

카메라 모드:

| 입력 | 모드 | 동작 |
|------|------|------|
| RMB + WASD/QE | Fly | FPS 비행. Shift 가속 |
| Alt + LMB | Orbit | Pivot 중심 공전 |
| MMB 드래그 | Pan | 뷰 평면 이동 |
| 스크롤 | Zoom | 시선 방향 전진/후퇴 |
| F | Focus | 선택 오브젝트 프레이밍 |

입력 소스: `ImGui.GetIO()` — 키보드/마우스 직접 읽기 (RoseEngine.Input 미사용)

---

### Step 3. SceneViewCameraProxy — WYSIWYG용 카메라 프록시

WYSIWYG 모드에서 `RenderSystem.Render(Camera camera, ...)` 호출 시, EditorCamera 값을 기존 `Camera` 컴포넌트로 전달하기 위한 프록시.

**새 파일**: `src/IronRose.Engine/Editor/SceneView/SceneViewCameraProxy.cs`
- 숨겨진 `GameObject` + `Camera` 컴포넌트 보유
- GameObject는 SceneManager에 등록하되, `_isEditorInternal = true` 태그로 Hierarchy 필터링
- 매 프레임 WYSIWYG 렌더 직전에 EditorCamera → Camera 동기화:
```csharp
public void Sync(EditorCamera editorCam)
{
    _go.transform.position = editorCam.Position;
    _go.transform.rotation = editorCam.Rotation;
    _camera.fieldOfView = editorCam.FieldOfView;
    _camera.nearClipPlane = editorCam.NearClip;
    _camera.farClipPlane = editorCam.FarClip;
    _camera.clearFlags = CameraClearFlags.Skybox;
}
```
- `Camera.main`은 게임 카메라가 이미 점유하므로 간섭 없음

---

### Step 4. SceneViewRenderer — 간소화된 Forward 렌더러 (Wireframe / MatCap / DiffuseOnly)

기존 `RenderSystem`과 완전히 독립. Wireframe, MatCap, DiffuseOnly 모드를 담당.

**새 파일**: `src/IronRose.Engine/Rendering/SceneViewRenderer.cs`

#### Wireframe 모드 렌더 순서:
```
1. Clear (다크 그레이 배경)
2. Wireframe: MeshRenderer._allRenderers를 FillMode.Wireframe 파이프라인으로 순회
3. Grid (알파 블렌딩)
4. Selection Outline (스텐실 기반)
5. Gizmo (depth off)
```

#### MatCap 모드 렌더 순서:
```
1. Clear (미디엄 그레이 배경)
2. MatCap: 뷰 공간 법선 → UV로 MatCap 텍스처 샘플링
3. Grid (알파 블렌딩)
4. Selection Outline (스텐실 기반)
5. Gizmo (depth off)
```

#### DiffuseOnly 모드 렌더 순서:
```
1. Clear (스카이 컬러)
2. Forward Diffuse: Albedo 텍스처 + 디렉셔널 라이트 1개 (Lambert N·L)
3. Skybox (depth ≤ LessEqual)
4. Grid (알파 블렌딩)
5. Selection Outline (스텐실 기반)
6. Gizmo (depth off)
```

자체 리소스:
- `_framebuffer` (Color RGBA8 + Depth D32_Float_S8_UInt)
- Wireframe 파이프라인 (`FillMode.Wireframe`, 기존 `vertex.glsl` + 단색 frag)
- MatCap 파이프라인 (`sceneview_matcap.vert` + `sceneview_matcap.frag` + MatCap 텍스처 바인딩)
- DiffuseOnly 파이프라인 (`sceneview_diffuse.vert` + `sceneview_diffuse.frag`)
- Grid 파이프라인
- Pick 파이프라인 + pick framebuffer (R32_UInt)
- Outline 파이프라인 (stencil write + expand)
- Gizmo 파이프라인

**새 파일**: `src/IronRose.Engine/Rendering/SceneViewUniforms.cs`
- `SceneViewTransformUniforms`, `GridUniforms`, `PickObjectId`, `OutlineUniforms`, `GizmoUniforms`

---

### Step 4b. EditorAssets — 에디터 내장 에셋 (MatCap 텍스처)

**새 파일**: `src/IronRose.Engine/Editor/EditorAssets.cs`

에디터 전용 내장 에셋(MatCap 텍스처 등)을 로드하고 관리하는 정적 클래스.

```csharp
public static class EditorAssets
{
    // MatCap 텍스처들 (프리셋별)
    public static Texture2D[] MatCapTextures { get; private set; }

    public static void Initialize(GraphicsDevice device)
    {
        // EditorAssets/MatCaps/ 디렉토리에서 PNG 로드
        // StbImageSharp로 디코딩 → Veldrid Texture2D 생성
    }

    public static Texture2D GetMatCap(MatCapPreset preset) => MatCapTextures[(int)preset];

    public static void Dispose() { /* 텍스처 해제 */ }
}

public enum MatCapPreset
{
    Solid,      // 무조명 그레이스케일 (N·V 밝기) — 순수 형태 확인
    Clay,       // 부드러운 점토 — 형태 확인에 최적
    Metal,      // 크롬 금속 — 반사/곡률 확인
    Glossy,     // 유광 플라스틱 — 하이라이트 확인
    Skin,       // 피부 톤 — 캐릭터 모델링
    RedWax,     // 빨간 왁스 — 서브서피스 느낌, ZBrush 스타일
}
```

**MatCap 텍스처 디렉토리**: `EditorAssets/MatCaps/`
- `solid.png` (256×256) — 무조명 그레이스케일, N·V 밝기만 (기존 Solid 모드와 동일 효과)
- `clay.png` (256×256) — 웜 그레이 점토, 부드러운 하이라이트
- `metal.png` (256×256) — 크롬/실버 메탈, 강한 반사
- `glossy.png` (256×256) — 백색 유광 플라스틱
- `skin.png` (256×256) — 살구색 피부 톤
- `red_wax.png` (256×256) — 빨간 왁스, ZBrush 기본 MatCap 느낌

MatCap 텍스처는 구체를 촬영/렌더링한 256×256 PNG 이미지.
프로그래밍적으로 생성 가능 (초기화 시 Phong/Blinn-Phong 셰이딩을 구체에 적용하여 CPU에서 베이크).

**대안**: 런타임 절차적 생성
```csharp
// 빌드 시 외부 PNG 없이 코드로 MatCap 텍스처 생성
public static Texture2D GenerateMatCap(GraphicsDevice device, MatCapPreset preset)
{
    var pixels = new byte[256 * 256 * 4];
    for (int y = 0; y < 256; y++)
    for (int x = 0; x < 256; x++)
    {
        float nx = (x / 127.5f) - 1f;
        float ny = (y / 127.5f) - 1f;
        float r2 = nx * nx + ny * ny;
        if (r2 > 1f) { /* 배경: 투명 */ continue; }
        float nz = MathF.Sqrt(1f - r2);
        // preset별 라이팅 계산 (diffuse + specular + rim)
        // → pixels[i] = color
    }
    // Veldrid Texture2D로 업로드
}
```

---

### Step 5. 셰이더 (10개 파일)

**새 파일**:

| 파일 | 용도 |
|------|------|
| `Shaders/sceneview_matcap.vert` | MatCap 모드 — MVP + 뷰 공간 법선 전달 |
| `Shaders/sceneview_matcap.frag` | MatCap 모드 — 뷰 공간 법선 → UV로 MatCap 텍스처 샘플링 |
| `Shaders/sceneview_diffuse.vert` | DiffuseOnly 모드 — MVP + 법선 + UV 전달 |
| `Shaders/sceneview_diffuse.frag` | DiffuseOnly 모드 — Albedo 텍스처 + Lambert N·L |
| `Shaders/scene_grid.vert` | 무한 XZ 그리드 — 카메라 주변 대형 쿼드 생성 |
| `Shaders/scene_grid.frag` | 그리드 라인 (fwidth AA) + 거리 페이드 + X축 빨강/Z축 파랑 |
| `Shaders/pick_object.vert` | GPU 피킹 — 표준 MVP 변환 |
| `Shaders/pick_object.frag` | GPU 피킹 — `out uint ObjectId` 출력 |
| `Shaders/outline.vert` | 선택 아웃라인 — 법선 방향 확장 (clip space) |
| `Shaders/outline.frag` | 선택 아웃라인 — 오렌지 솔리드 컬러 |

Wireframe 모드는 기존 `vertex.glsl` + 단색 fragment를 `FillMode.Wireframe` 래스터라이저로 재사용.
기즈모도 기존 셰이더에 유니폼 색상을 전달하여 렌더링 (별도 셰이더 불필요).

#### MatCap 셰이더 핵심 (`sceneview_matcap.frag`):
```glsl
// 뷰 공간 법선을 [0,1] 범위로 변환하여 MatCap 텍스처 UV로 사용.
vec3 viewNormal = normalize(frag_ViewNormal);
vec2 matcapUV = viewNormal.xy * 0.5 + 0.5;
vec3 matcapColor = texture(MatCapTex, matcapUV).rgb;
out_Color = vec4(matcapColor, 1.0);
```

#### DiffuseOnly 셰이더 핵심 (`sceneview_diffuse.frag`):
```glsl
// Albedo 텍스처 + 단일 디렉셔널 라이트 Lambert 셰이딩. PBR/IBL 없음.
vec3 albedo = hasTexture > 0.5 ? texture(Tex, frag_UV).rgb * baseColor.rgb : baseColor.rgb;
float nDotL = max(dot(normalize(frag_Normal), lightDir), 0.0);
vec3 diffuse = albedo * (nDotL * 0.8 + 0.2);  // 20% ambient
out_Color = vec4(diffuse, 1.0);
```

---

### Step 6. ImGuiSceneViewPanel — Scene View UI 패널

**새 파일**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiSceneViewPanel.cs`

`ImGuiGameViewPanel`과 동일한 패턴 (텍스처 표시, hover/focus 추적, 이미지 영역 크기 추적).

툴바:
```
[Wireframe ▼]  [Clay ▼]  [W] Translate  [E] Rotate  [R] Scale  [Local/World]  [☑ Grid]
```
- **렌더 모드 콤보**: `Wireframe` / `MatCap` / `Diffuse Only` / `WYSIWYG`
- **MatCap 프리셋 콤보**: MatCap 모드일 때만 표시. `Solid` / `Clay` / `Metal` / `Glossy` / `Skin` / `Red Wax`
- `[W] Translate  [E] Rotate  [R] Scale` — Gizmo 모드 전환
- `[Local / World]` — Transform 공간 전환
- `[Grid]` 체크박스

상태:
- `SceneViewRenderMode` enum (Wireframe, MatCap, DiffuseOnly, WYSIWYG)
- `MatCapPreset` enum + `SelectedMatCapPreset` — MatCap 프리셋 선택
- `TransformTool` enum (Translate, Rotate, Scale)
- `TransformSpace` enum (World, Local)
- `ShowGrid` bool
- `GetRenderTargetSize()` — 항상 Native 모드 (패널 크기)

---

### Step 7. SceneViewRenderTargetManager — RT 관리

**새 파일**: `src/IronRose.Engine/Editor/ImGui/SceneViewRenderTargetManager.cs`

기존 `ImGuiRenderTargetManager`와 동일한 패턴:
- 오프스크린 Color + Depth 텍스처 생성
- ImGui 텍스처 바인딩 (VeldridImGuiRenderer.GetOrCreateImGuiBinding)
- 크기 변경 디바운스 (8% 임계값, 150ms 대기)
- Wireframe/MatCap/DiffuseOnly: `SceneViewRenderer.Resize()` 호출
- WYSIWYG: RT는 Game View와 동일 해상도. 패널에서 스케일링 표시

---

### Step 8. GPU 피킹 — 오브젝트 선택

`SceneViewRenderer` 내부에 구현. 모든 렌더 모드에서 동작.

흐름:
1. Scene View에서 LMB 클릭 (카메라 조작 중이 아닐 때)
2. 마우스 스크린 좌표 → Scene View RT 좌표 변환
3. Pick framebuffer (R32_UInt)에 모든 메시를 `GetInstanceID()` 컬러로 렌더링
4. 클릭 위치 1픽셀을 Staging 텍스처로 복사
5. `device.Map()` → uint 값 읽기
6. `EditorSelection.Select(id)` 호출

최적화: 클릭 시에만 pick 렌더링 (`_pickRequested` 플래그)

---

### Step 9. Selection Outline — 선택 하이라이트

`SceneViewRenderer` 렌더 루프에 통합. 모든 렌더 모드에서 동작.

스텐실 2-패스:
1. **Stencil Write**: 선택 오브젝트를 컬러 쓰기 off, 스텐실 Replace=1로 렌더
2. **Outline Expand**: 선택 오브젝트를 법선 방향으로 확장하여 렌더, 스텐실 NotEqual(1) → 아웃라인만 보임

색상: 오렌지 `(1.0, 0.6, 0.0, 1.0)`

WYSIWYG 모드에서는 `RenderSystem.Render()` 이후 Scene View RT에 아웃라인 패스만 추가 실행.

---

### Step 10. Transform Gizmo — 이동/회전/스케일

**새 파일**:
- `src/IronRose.Engine/Editor/SceneView/TransformGizmo.cs` — 상태/상호작용/렌더링
- `src/IronRose.Engine/Editor/SceneView/GizmoMeshBuilder.cs` — 메시 생성

#### 기즈모 메시:
- **Translate**: 원기둥 + 원뿔 화살표 (X=빨강, Y=초록, Z=파랑)
- **Rotate**: 토러스 링 3개 (각 축)
- **Scale**: 원기둥 + 큐브 말단

#### 화면 상수 크기:
```csharp
float scale = (camera.Position - target.position).magnitude * 0.1f;
```

#### 마우스 상호작용:
1. 마우스 스크린 좌표 → 월드 레이 변환 (unproject)
2. 레이와 각 축 라인 최근접 거리 계산
3. 가장 가까운 축이 임계값 이내 → 활성 축
4. 드래그:
   - **Translate**: 레이를 활성 축에 투영, 이동 delta → `transform.position += delta`
   - **Rotate**: 활성 축 평면의 각도 변화 → `transform.rotation *= deltaRot`
   - **Scale**: 축 투영 스케일 factor → `transform.localScale.x *= factor`

#### 렌더링:
- Depth test OFF (항상 위에)
- 호버된 축은 밝은 노란색으로 하이라이트
- 모든 렌더 모드에서 동작 (Wireframe/MatCap/DiffuseOnly/WYSIWYG 공통)

---

### Step 11. 통합 — ImGuiOverlay + EngineCore + Layout

**수정**: `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs`
- 필드 추가: `_sceneView`, `_editorCamera`, `_sceneRenderer`, `_sceneRtManager`, `_camProxy`
- `Initialize()`: 생성 + 초기화 + `EditorAssets.Initialize()` (MatCap 텍스처 로드)
- `Update()`: Scene View Draw + EditorCamera Update + Gizmo 상호작용
- `Render()`: Scene View 렌더링 추가 (렌더 모드에 따라 분기, MatCap 모드 시 선택된 프리셋 텍스처 전달)
- Windows 메뉴에 "Scene View" 토글 추가
- 새 프로퍼티: `SceneViewFramebuffer`, `SceneViewRenderMode`
- `RenderSceneView(CommandList cl)` 메서드 추가
- `Dispose()`: 새 리소스 정리 + `EditorAssets.Dispose()`

**수정**: `src/IronRose.Engine/EngineCore.cs` (Render 메서드)
```
렌더 모드별 분기:

■ Wireframe / MatCap / DiffuseOnly:
  BeginFrame
    → [GameView RT] → RenderSystem.Render(Camera.main)
    → [SceneView RT] → SceneViewRenderer.Render(EditorCamera, mode, matcapTex?)  ← 독립 렌더러
    → ImGui.Render → EndFrame

■ WYSIWYG:
  BeginFrame
    → [GameView RT] → RenderSystem.Render(Camera.main)
    → [SceneView RT] → CamProxy.Sync(EditorCamera)
                      → OverrideOutputFramebuffer = sceneViewFB
                      → RenderSystem.Render(CamProxy.Camera)  ← 동일 렌더러 재호출
                      → OverrideOutputFramebuffer = null
    → SceneViewRenderer.RenderOverlays(cl, ...)  ← 그리드+아웃라인+기즈모만
    → ImGui.Render → EndFrame
```

WYSIWYG 모드에서 `RenderSystem`을 두 번 호출하되:
- Resize 없음 — Game View 내부 해상도 공유
- Scene View RT 크기 ≠ 내부 렌더 해상도일 경우, blit 시 스케일링

**수정**: `src/IronRose.Engine/Editor/ImGui/ImGuiLayoutManager.cs`
- `ApplyDefaultIfNeeded`에 SceneViewPanel 파라미터 추가
- 기본 레이아웃: 센터를 좌/우 분할 (Scene View | Game View)
```csharp
ImGuiDockBuilder.SplitNode(centerId, DirLeft, 0.5f, out uint sceneViewId, out uint gameViewId);
ImGuiDockBuilder.DockWindow("Scene View", sceneViewId);
ImGuiDockBuilder.DockWindow("Game View", gameViewId);
```

**수정**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiHierarchyPanel.cs`
- `_isEditorInternal` 태그 필터링 추가 (CamProxy GO 숨김)

---

## 파일 요약

### 새 파일 (24개 — 코드 13 + 셰이더 5 + 에셋 6)

| 파일 | 용도 |
|------|------|
| `src/IronRose.Engine/Editor/EditorSelection.cs` | 선택 상태 싱글턴 |
| `src/IronRose.Engine/Editor/SceneView/EditorCamera.cs` | 에디터 카메라 |
| `src/IronRose.Engine/Editor/SceneView/SceneViewCameraProxy.cs` | WYSIWYG용 Camera 프록시 |
| `src/IronRose.Engine/Editor/SceneView/TransformGizmo.cs` | 기즈모 상호작용 |
| `src/IronRose.Engine/Editor/SceneView/GizmoMeshBuilder.cs` | 기즈모 메시 생성 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiSceneViewPanel.cs` | Scene View 패널 |
| `src/IronRose.Engine/Editor/ImGui/SceneViewRenderTargetManager.cs` | Scene View RT 관리 |
| `src/IronRose.Engine/Editor/EditorAssets.cs` | 에디터 내장 에셋 로더 (MatCap 텍스처 등) |
| `src/IronRose.Engine/Rendering/SceneViewRenderer.cs` | Forward 렌더러 (Wireframe + MatCap + DiffuseOnly) |
| `src/IronRose.Engine/Rendering/SceneViewUniforms.cs` | GPU 유니폼 구조체 |
| `Shaders/sceneview_matcap.vert` | MatCap 정점 셰이더 (뷰 공간 법선) |
| `Shaders/sceneview_matcap.frag` | MatCap 프래그먼트 (텍스처 샘플링) |
| `Shaders/sceneview_diffuse.vert` | DiffuseOnly 정점 셰이더 |
| `Shaders/sceneview_diffuse.frag` | DiffuseOnly Albedo + Lambert |
| `Shaders/scene_grid.vert` | 그리드 정점 셰이더 |
| `Shaders/scene_grid.frag` | 그리드 프래그먼트 셰이더 |
| `Shaders/pick_object.vert` | 피킹 정점 셰이더 |
| `Shaders/pick_object.frag` | 피킹 프래그먼트 셰이더 |
| `Shaders/outline.vert` | 아웃라인 정점 셰이더 |
| `Shaders/outline.frag` | 아웃라인 프래그먼트 셰이더 |
| `EditorAssets/MatCaps/solid.png` | MatCap 프리셋: Solid (무조명 그레이스케일 N·V) |
| `EditorAssets/MatCaps/clay.png` | MatCap 프리셋: Clay (부드러운 점토 질감) |
| `EditorAssets/MatCaps/metal.png` | MatCap 프리셋: Metal (광택 금속) |
| `EditorAssets/MatCaps/glossy.png` | MatCap 프리셋: Glossy (유광 플라스틱) |
| `EditorAssets/MatCaps/skin.png` | MatCap 프리셋: Skin (피부 질감) |
| `EditorAssets/MatCaps/red_wax.png` | MatCap 프리셋: Red Wax (빨간 왁스) |

### 수정 파일 (4개)

| 파일 | 변경 |
|------|------|
| `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs` | Scene View 생성/갱신/렌더링/메뉴 + 렌더 모드 분기 |
| `src/IronRose.Engine/Editor/ImGui/ImGuiLayoutManager.cs` | 기본 레이아웃에 Scene View 추가 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiHierarchyPanel.cs` | EditorSelection 위임 + 에디터 내부 GO 필터링 |
| `src/IronRose.Engine/EngineCore.cs` | Scene View 렌더 호출 + 렌더 모드별 분기 |

### 재사용하는 기존 코드

| 패턴 | 참조 파일 |
|------|-----------|
| `MeshRenderer._allRenderers` 순회 | `RenderSystem.Draw.cs:12-26` |
| `Mesh.UploadToGPU()` + VertexBuffer/IndexBuffer | `RenderSystem.Draw.cs:21` |
| `PrepareMaterial()` 패턴 | `RenderSystem.cs:1639-1660` |
| `Matrix4x4.TRS().ToNumerics()` | `RenderSystem.cs:1624` |
| `RenderSystem.Render(Camera, aspectRatio)` | `RenderSystem.cs:1346` (WYSIWYG 재호출) |
| `RenderSystem.OverrideOutputFramebuffer` | `RenderSystem.cs:386` (WYSIWYG RT 지정) |
| `ImGuiRenderTargetManager` 디바운스 패턴 | `ImGuiRenderTargetManager.cs` |
| `ImGuiGameViewPanel` 이미지 표시 패턴 | `ImGuiGameViewPanel.cs` |
| `ShaderCompiler` 셰이더 캐싱 | `RenderSystem.cs:Initialize` |
| `SceneManager.AllGameObjects` | `SceneManager.cs:25` |
| `GetInstanceID()` = `RuntimeHelpers.GetHashCode()` | `Object.cs:13` |

---

## 구현 순서 (의존성 기반)

```
Step 1 (EditorSelection)
    ↓
Step 2 (EditorCamera)
    ↓
Step 3 (SceneViewCameraProxy) ← WYSIWYG에 필요
    ↓
Step 4 (SceneViewRenderer — Wireframe + MatCap + DiffuseOnly)
    ↓
Step 4b (EditorAssets — MatCap 텍스처 로드)
    ↓
Step 5 (셰이더: matcap + diffuse + grid + pick + outline)
    ↓
Step 6 (ImGuiSceneViewPanel — 렌더 모드 + MatCap 프리셋 콤보)
    ↓
Step 7 (SceneViewRTManager)
    ↓
Step 11 (ImGuiOverlay + EngineCore 통합) — 여기서 빌드 확인
    ↓
Step 8 (GPU 피킹)
    ↓
Step 9 (Selection Outline)
    ↓
Step 10 (Transform Gizmo)
```

---

## 검증

1. **빌드**: `dotnet build IronRose.sln` — 0 errors
2. **Scene View 표시**: 에디터 실행 시 Scene View 패널이 Game View 옆에 독립 도킹
3. **카메라 조작**: RMB+WASD 비행, Alt+LMB 공전, MMB 팬, 스크롤 줌 동작 확인
4. **렌더 모드 전환**:
   - **Wireframe**: 와이어프레임만 보임, 배경 다크 그레이
   - **MatCap**: 프리셋 전환(Solid/Clay/Metal/Glossy/Skin/Red Wax) 정상 동작. Solid 프리셋 시 그레이스케일
   - **DiffuseOnly**: Albedo 텍스처 + 기본 Lambert 라이팅, PBR/IBL 없음
   - **WYSIWYG**: Game View와 동일한 PBR 품질 (스카이박스, 라이팅, 포스트프로세싱 포함)
5. **그리드**: XZ 평면 무한 그리드 표시, X축 빨강 / Z축 파랑 라인, 거리 페이드
6. **오브젝트 선택**: Scene View에서 메시 클릭 → Hierarchy에서 해당 오브젝트 선택 동기화 (모든 모드)
7. **아웃라인**: 선택된 오브젝트에 오렌지 아웃라인 표시 (모든 모드)
8. **기즈모**: W(이동)/E(회전)/R(스케일) 전환, 축 드래그로 Transform 변경, Inspector 실시간 반영
9. **양 뷰 동시**: Game View + Scene View 모두 동시에 렌더링 정상 (WYSIWYG에서도 프레임 유지)
