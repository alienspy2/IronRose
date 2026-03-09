# IronRose 개발 진행 상황

**최종 업데이트**: 2026-03-02 (Phase 40 완료 - UI 2D Gizmo + SceneView 에디터)

---

## 전체 진행도

- [x] Phase 0: 프로젝트 구조 및 환경 설정 ✅
- [x] Phase 1: 최소 실행 가능 엔진 (Bootstrapper) ✅
- [x] Phase 2: Roslyn 핫 리로딩 시스템 (LiveCode) ✅
- [x] Phase 2A: Engine Core 핫 리로딩 ✅
- [x] Phase 2B-0: Bootstrapper/Engine 통합 ✅
- [x] Phase 3: Unity Architecture (GameObject, Component) ✅
- [x] Phase 3.5: 입력 시스템 (Silk.NET 마이그레이션) ✅
- [x] Phase 3.5+: Unity InputSystem (액션 기반 입력) ✅
- [x] Phase 3.5++: Unity 호환성 확장 (Mathf, Random, Object, Destroy, Coroutine, Transform 계층 등) ✅
- [x] Phase 4: 3D 렌더링 파이프라인 (Mesh, Shader, Camera, Primitive, Light, Material) ✅
- [x] Phase 5: Unity 에셋 임포터 (AssetDatabase, MeshImporter, TextureImporter, PrefabImporter) ✅
- [x] Phase 5A: SpriteRenderer (3D Space Sprite 렌더링, 알파 블렌딩) ✅
- [x] Phase 5B: TextRenderer (Font 아틀라스 기반 3D 텍스트 렌더링) ✅
- [x] Phase 6: 물리 엔진 (BepuPhysics 3D + Aether.Physics2D, FixedUpdate 50Hz) ✅
- [x] Phase 7: Deferred PBR 렌더링 (G-Buffer, Cook-Torrance BRDF, IBL, Post-Processing) ✅
- [x] Phase 8: 중간정리 (KISS 리팩토링, 코드 검증) ✅
- [x] Phase 35: Animation System (AnimationCurve, AnimationClip, Animator, SpriteAnimation, .anim 에셋) ✅
- [x] Phase 36: Animation Editor (타임라인 + 커브 에디터) ✅
- [x] Phase 38: Render Settings 분리 (Renderer Settings + Scene Environment + PostProcess Volume) ✅
- [x] Phase 39: ImGui Multi-Viewport + Custom Component 시스템 + Standalone 빌드 ✅
- [x] Phase 40: UI 2D Gizmo 시스템 + SceneView 에디터 개선 ✅

---

## Phase 40: UI 2D Gizmo 시스템 + SceneView 에디터 개선 ✅

**완료 날짜**: 2026-03-02
**커밋**: `6231f5f`, `f8968a1`

### 완료된 작업

#### 40-A: UI 2D Gizmo 시스템
- [x] `RectGizmoEditor.cs` (신규, 404줄) — Rect Tool (T키) UI 요소 크기/이동 핸들 편집 (ImGui 2D 오버레이)
- [x] `UITransformGizmo2D.cs` (신규, 643줄) — RectTransform 선택 시 Move/Rotate/Scale 2D 축 기즈모
- [x] `CanvasRenderer.cs` — 회전/스케일 버텍스 트랜스폼, HitTest, `CollectHitsInRect` 추가 (+227줄)
- [x] `RectSelectionTool.cs` — 사각형 선택에 UI 오브젝트 포함

#### 40-B: SceneView 에디터 개선
- [x] `RectTransform.cs` — `lastScreenRect` 캐시 필드 (기즈모 편집용)
- [x] `ImGuiSceneViewPanel.cs` — Rect Tool 버튼 (T키), Snap Settings 팝업 통합, 2D 그리드 스냅
- [x] `ImGuiOverlay.cs` — 카메라 활성 잠금 + Unity 스타일 마우스 화면 가장자리 래핑 (+164줄)

#### 40-C: 버그 수정
- [x] `GizmoRenderer.cs` — pick-only 배치 메시 분리 (`_pickBatchMesh`) 렌더/픽 충돌 수정
- [x] `RenderSystem.Lighting.cs` — 스카이박스 ray 재구성을 역행렬에서 카메라 기저 벡터로 교체
- [x] `PostProcessManager.cs` — PostProcessVolume `enabled` 플래그 미체크 수정
- [x] `UIInputField.cs` — 입력 포커스 진입을 Play Mode로 제한

### 주요 결정 사항
- Rect Tool: ImGui 2D 오버레이 방식으로 UI 요소 직접 편집
- 2D Gizmo: Move/Rotate/Scale을 RectTransform에 맞춰 2D 축 기반 구현
- 카메라 마우스 래핑: Unity 스타일로 화면 가장자리에서 반대편으로 이동

### 파일 변경 요약
- 신규 2개, 수정 ~16개 (+1,617 / -65)

### 중간 포스트모템 (`f8968a1`)
- 코드베이스 규모: ~44,475 C# SLOC (271파일), ~46,650 총 유효 코드 라인
- 맨먼스 추정: 18-20 (보수적: 24) — 평균 개발자 기준
- 실제 소요: ~197시간, 17일 (~1.1-1.5 맨먼스)
- AI 생산성 배수: **13-18x**

> 상세 포스트모템: [phase40_MidPostmortem.md](plans/phase40_MidPostmortem.md)

---

## Phase 39: ImGui Multi-Viewport + Custom Component 시스템 + Standalone 빌드 ✅

**완료 날짜**: 2026-03-02
**커밋**: `0538028`, `350b6cd`

### 완료된 작업

#### 39-A: ImGui Multi-Viewport 지원 (`0538028`)
- [x] `ViewportData.cs` (신규) — Per-viewport 윈도우/입력/GPU 리소스 추적 (GCHandle)
- [x] `ImGuiPlatformBackend.cs` (신규) — Platform IO 콜백 10개 구현
  - Silk.NET `Window.Create()` + `Initialize()` 보조 OS 윈도우 생명주기
  - cimgui 전용 `GetWindowPos`/`GetWindowSize` setter (C++ 리턴 컨벤션 호환)
  - GLFW API 모니터 열거 (ImGui PlatformMonitors)
  - 보조 윈도우 포커스 추적 (`FocusChanged` 이벤트)
- [x] `ImGuiRendererBackend.cs` (신규) — Renderer IO 콜백 5개 구현
  - 보조 뷰포트별 독립 Swapchain + CommandList
  - `VeldridImGuiRenderer.Render()` per-viewport DrawData 재사용
  - `WaitForIdle` 공유 버퍼 충돌 방지
- [x] `ImGuiInputHandler.cs` — 보조 윈도우 입력 컨텍스트 등록/해제, 마우스 좌표 로컬→절대 변환
- [x] `ImGuiOverlay.cs` — ViewportsEnable 활성화 + 백엔드 초기화/정리 통합, `RenderSecondaryViewports()` 추가
- [x] `EngineCore.cs` — 메인 EndFrame 후 `RenderSecondaryViewports()` 호출

#### 39-B: Custom Component 시스템 (`350b6cd`)
- [x] Inspector/Serializer 타입 확장:
  - 핵심 타입: Vector4, Quaternion (Euler), long, double, byte 추가
  - 배열/리스트: `T[]` 및 `List<T>` Inspector 편집 + TOML 직렬화
  - 씬 오브젝트 참조: GameObject/Component 참조 필드 (GUID 지연 해석)
  - 중첩 직렬화: `[Serializable]` struct/class TreeNode 폴드아웃
- [x] `DragFloat4Clickable` 위젯 (신규)

#### 39-C: LiveCode 핫 리로드 개선
- [x] Editor 모드에서 GameObject 보존, 컴포넌트 타입만 마이그레이션
- [x] Inspector/SceneSerializer 타입 캐시 핫 리로드 후 자동 무효화
- [x] Playing 상태 무관하게 핫 리로드 처리 실행

#### 39-D: Standalone 빌드
- [x] `IronRose.Standalone` 프로젝트 (신규) — HeadlessEditor 모드, ProjectSettings에서 시작 씬 로드
- [x] Project Settings > Build 탭: 시작 씬 선택기 (드래그 드롭 + 브라우저)
- [x] `rose_projectSettings.toml`에 `[build] start_scene` 설정 추가
- [x] LiveCode/FrozenCode 프로젝트 솔루션 등록

### 주요 결정 사항
- Multi-Viewport: cimgui C++ 리턴 컨벤션 호환을 위한 전용 setter 사용
- 보조 뷰포트: 독립 Swapchain + CommandList로 렌더링 격리
- Custom Component: GUID 기반 지연 참조 해석으로 씬 오브젝트 참조 직렬화
- Standalone: HeadlessEditor 모드로 에디터 없이 시작 씬 직접 로드
- 핫 리로드: GameObject 보존 전략으로 에디터 상태 유지

### 파일 변경 요약
- `0538028`: 신규 3개, 수정 6개 (+1,612 / -25)
- `350b6cd`: 신규 ~5개, 수정 ~17개 (+1,483 / -32)

> 상세 계획: [phase39_CustomComponent.md](plans/phase39_CustomComponent.md), [making_multiviewport.md](making_multiviewport.md)

---

## Phase 38: Render Settings 분리 ✅

**완료 날짜**: 2026-03-01

### 완료된 작업
- [x] Phase 38-A: Renderer Settings Panel — .renderer TOML 프로파일 에셋, FSR/SSIL 14개 프로퍼티
- [x] Phase 38-B: Scene Environment Panel — 씬별 Skybox/Ambient/Sky, [sceneEnvironment] 직렬화
- [x] Phase 38-C: Post Processing Volume System — Volume 기반 PP, .ppprofile 에셋, 카메라 위치 기반 블렌딩

### 주요 결정 사항
- 모놀리식 ImGuiRenderSettingsPanel.cs (406줄) → 3개 전문 시스템으로 완전 분해
- Renderer Settings: .renderer TOML 에셋으로 FSR/SSIL 설정 영속화, 여러 프로파일 전환 가능
- Scene Environment: 씬 파일에 [sceneEnvironment] 섹션, [renderSettings] 하위호환
- PostProcess Volume: BoxCollider + blendDistance 기반 카메라 진입/퇴장 감지, weighted average 블렌딩
- HideInInspectorAttribute에 AttributeTargets.Property 추가 (profileGuid 숨김)

### 파일 변경 요약
- 새 파일 8개, 수정 ~15개, 삭제 1개

---

## Phase 36: Animation Editor ✅

**시작 날짜**: 2026-02-28
**완료 날짜**: 2026-03-01

### 완료된 작업
- [x] `ImGuiAnimationEditorPanel.cs` — 신규 패널 생성 (Toolbar + 도프 시트 + 커브 뷰)
- [x] `ImGuiOverlay.cs` — 필드/초기화/Draw/Tools 메뉴/dispatch 등록
- [x] `ImGuiProjectPanel.cs` — .anim 더블클릭 dispatch 추가
- [x] `ImGuiInspectorPanel.cs` — Animator 컴포넌트 "Edit Animation" 버튼 추가
- [x] Target Animator 선택 콤보 + objectName.Component.attribute 경로 지원
- [x] 프리뷰 재생 (SampleAt) + 속도 조절
- [x] **Feature 1**: Keyframe value editing (time/value/tangent DragFloat Inspector)
- [x] **Feature 2**: Current value display (트랙별 playhead 위치 값 표시)
- [x] **Feature 3**: Undo/Redo (AnimationClipUndoAction 스냅샷, Ctrl+Z/Ctrl+Shift+Z)
- [x] **Feature 4**: Keyframe snap (드래그 시 프레임 그리드 스냅)
- [x] **Feature 5**: Dopesheet ↔ Curves 모드 전환 (Unity 스타일)
- [x] **Feature 6**: Tangent mode presets (Auto/Linear/Constant/Free)
- [x] **Feature 7**: Multi-select (Shift+click, box select, multi-drag)
- [x] **Feature 8**: Copy/Paste keyframes (Ctrl+C/V, 상대 시간 오프셋)
- [x] **Feature 9**: Animation Event editing (룰러 마커, 이벤트 인스펙터)
- [x] **Feature 10**: Keyboard shortcuts (Space, Ctrl+S, Ctrl+Z, Delete, A, K 등)
- [x] `AnimationClipUndoAction.cs` — 신규 undo action
- [x] `AnimationCurve.cs` — GetKeys()/SetKeys() 추가

### Phase 36 확장 — 버그 수정 7건 + 기능 개선 8건
- [x] Tangent 계산 division by zero guard (6곳)
- [x] Animator 이벤트 인덱스 PingPong 모드 지원
- [x] Multi-key 드래그 후 selection 인덱스 재구축 (time+value 매칭)
- [x] Events 클릭 순회 break 추가
- [x] DragFloat undo 이중 생성 수정 (7개 필드)
- [x] 첫/마지막 키프레임 tangent mode 감지 (edge key)
- [x] Preview snapshot 클립 교체 시 복원 (Revert에서 RestorePreview)
- [x] 키프레임 더블클릭 → Inspector 포커스
- [x] 'F' 키 Y축 자동 피팅 (Curves 모드)
- [x] Track Foldout (localPosition/Rotation/Scale 그룹 접기)
- [x] 드래그 시 tooltip (time + value)
- [x] Curves 모드 트랙 가시성 토글
- [x] Animator _targets Volatile.Write/Read 스레드 안전
- [x] Property path 검증 강화 (구체적 실패 이유 로그)
- [x] Curves 스플라인 LOD 렌더링 (적응형 세그먼트 + viewport culling)

### 주요 결정 사항
- SpriteEditorPanel과 동일한 tool-style 패널 (EditorState 비영속, 기본 닫힘)
- Dopesheet ↔ Curves: 동일 영역에서 뷰 모드 전환 (Unity Animation Window 스타일)
- Curves 뷰: Hermite spline 렌더링 + 탄젠트 핸들 드래그 + Y축 독립 줌/스크롤
- Multi-select: HashSet<(path, keyIndex)> 기반, Box Select + Shift/Ctrl 클릭
- Undo: 클립 전체 스냅샷 (curves + events + length) 방식
- 키프레임 드래그 시 프레임 그리드 자동 스냅
- DragFloat 패턴: `IsItemActivated() → BeginUndoAction()` → 값 변경 → `IsItemDeactivatedAfterEdit() → EndUndoAction()`

---

## Phase 35: Animation System ✅

**완료 날짜**: 2026-02-28

### 완료된 작업

#### Phase 1: 런타임 코어 (신규 5파일)
- [x] `WrapMode.cs` — Once, Loop, PingPong, ClampForever enum
- [x] `AnimationCurve.cs` — Keyframe struct + Hermite cubic spline 보간 + Factory methods
- [x] `AnimationClip.cs` — propertyPath별 커브 + AnimationEvent 관리
- [x] `Animator.cs` — 멀티스레드 curve 평가 + 메인스레드 이벤트 큐 + PropertyTarget 캐시
- [x] `SpriteAnimation.cs` — 스프라이트 프레임 애니메이션 (RequireComponent)

#### Phase 2: 에셋 파이프라인 (신규 1파일 + 기존 수정 4파일)
- [x] `AnimationClipImporter.cs` — .anim TOML 파싱/익스포트
- [x] `AssetDatabase.cs` — AnimationClipImporter 등록 + GUID 역검색
- [x] `RoseMetadata.cs` — .anim 확장자 임포터 추론
- [x] `SceneSerializer.cs` — AnimationClip 직렬화/역직렬화 + 에셋 참조 배열 (Sprite[]) 지원
- [x] `ImGuiInspectorPanel.cs` — AssetNameExtractors에 AnimationClip 추가

### 주요 결정 사항
- Animator.Update()는 MonoBehaviour.Update() 단계(3번)에서 실행
- Curve Evaluate + 프로퍼티 적용: 멀티스레드 (Parallel.For, 16타겟 이상 시)
- AnimationEvent: ConcurrentQueue로 수집 → 메인스레드에서 순차 invoke
- Timeline 에디터 패널은 별도 Phase로 분리

---

## Phase 0: 프로젝트 구조 및 환경 설정 ✅

**완료 날짜**: 2026-02-13
**소요 시간**: ~1시간

### 완료된 작업

#### 1. 환경 설정
- [x] .NET 10.0.101 SDK 설치 확인
- [x] 프로젝트 문서 검토

#### 2. 프로젝트 구조 생성
- [x] IronRose.sln 솔루션 생성
- [x] IronRose.Engine 프로젝트 생성
- [x] IronRose.Scripting 프로젝트 생성
- [x] IronRose.AssetPipeline 프로젝트 생성
- [x] IronRose.Rendering 프로젝트 생성
- [x] IronRose.Physics 프로젝트 생성
- [x] IronRose.Bootstrapper 프로젝트 생성

#### 3. 프로젝트 참조 설정
- [x] Bootstrapper → 모든 모듈 참조
- [x] 각 모듈 → Engine 참조

#### 4. NuGet 패키지 설치
- [x] **Rendering**: Veldrid, Veldrid.SPIRV, Veldrid.ImageSharp
- [x] **Windowing/Input**: Silk.NET.Windowing, Silk.NET.Input (GLFW 백엔드)
- [x] **Scripting**: Microsoft.CodeAnalysis.CSharp 5.0.0
- [x] **AssetPipeline**: YamlDotNet, AssimpNet, SixLabors.ImageSharp
- [x] **Engine**: Tomlyn
- [x] **Physics**: BepuPhysics, Aether.Physics2D

#### 5. 개발 환경 설정
- [x] .vscode/launch.json 생성
- [x] .vscode/tasks.json 생성
- [x] .editorconfig 생성 (UTF-8 BOM 강제)
- [x] .gitignore 생성

#### 6. 문서화
- [x] Claude.md 개선 (개발 가이드라인)
- [x] Progress.md 생성 (이 파일)

#### 7. 빌드 및 테스트
- [x] `dotnet build` 성공 (경고 14개, 오류 0개)
- [x] `dotnet run` 실행 확인 ("Hello, World!" 출력)

### 주요 결정 사항
- **아키텍처**: 모든 엔진 코드를 동적 로드 가능하도록 설계
- **부트스트래퍼**: 최소한의 고정 코드만 포함
- **인코딩**: UTF-8 BOM을 강제하여 인코딩 문제 방지
- **자동화**: AI 자동화를 위한 JSON 명령 인터페이스 설계
- **메인 테마 색상**: 금속의 백장미 (Metallic White Rose)
  - RGB: (230, 220, 210) - 은은한 베이지 톤
  - 정규화: (0.902f, 0.863f, 0.824f)
  - Hex: #E6DCD2
  - 용도: 배경, UI 기본 색상, 엔진 로고

### 알려진 이슈
- ~~⚠️ SixLabors.ImageSharp 1.0.4 보안 취약성 경고~~ → ✅ 3.1.12로 해결 (2026-02-14)

---

## Phase 1: 최소 실행 가능 엔진 (Bootstrapper) ✅

**시작 날짜**: 2026-02-13
**완료 날짜**: 2026-02-13
**소요 시간**: ~2시간

### 완료된 작업

#### 1. Bootstrapper 기본 구조
- [x] Program.cs 생성 (간소화된 메인 루프)
- [x] unsafe 코드 허용 설정

#### 2. GraphicsManager 구현
- [x] Veldrid.StartupUtilities를 사용한 윈도우 생성
- [x] Vulkan GraphicsDevice 초기화
- [x] CommandList 생성 및 렌더링 루프
- [x] IronRose 테마 색상 (금속의 백장미) 적용

#### 3. 빌드 및 실행 테스트
- [x] `dotnet build` 성공 (경고 14개, 오류 0개)
- [x] `dotnet run` 실행 확인
- [x] 1280x720 윈도우 생성 확인
- [x] Vulkan 초기화 성공
- [x] 메인 루프 진입 확인

### 주요 결정 사항
- **윈도우 생성**: Phase 3.5에서 Silk.NET.Windowing으로 전환 완료
  - 기존 Veldrid.Sdl2 → Silk.NET.Windowing (GLFW 백엔드) + 네이티브 핸들 Veldrid 연동
  - Bootstrapper를 더 간소화 (~50줄)
- **렌더링**: Vulkan 백엔드 사용
  - 성공적으로 초기화됨
  - IronRose 테마 색상으로 화면 클리어

#### 4. 타이밍 시스템
- [x] Stopwatch 기반 델타타임 계산
- [x] FPS 카운터 (매초 출력)
- [x] 프레임타임 로그
- [x] 60 FPS 타겟 프레임 제한

### 테스트 결과
- ✅ 윈도우 정상 생성 (1280x720)
- ✅ Vulkan 초기화 성공
- ✅ 금속 백장미 색상 렌더링
- ✅ **FPS: 59-60** (안정적)
- ✅ **프레임타임: 16-17ms** (목표: 16.67ms)

---

## Phase 2: Roslyn 핫 리로딩 시스템 ✅

**시작 날짜**: 2026-02-13
**완료 날짜**: 2026-02-13
**소요 시간**: ~1시간

### 완료된 작업

#### 1. Roslyn 컴파일러 래퍼
- [x] ScriptCompiler.cs 구현 (~125줄)
- [x] System.Runtime 참조 추가
- [x] 컴파일 성공/실패 로깅

#### 2. AssemblyLoadContext 핫 스왑
- [x] ScriptDomain.cs 구현 (~135줄)
- [x] 동적 어셈블리 로드/언로드
- [x] GC 기반 메모리 정리
- [x] 스크립트 인스턴스 자동 생성

#### 3. 상태 보존 시스템
- [x] IHotReloadable 인터페이스
- [x] StateManager 구현 (TOML 기반)

#### 4. 스크립팅 통합
- [x] Program.cs 업데이트 (스크립팅 시스템 통합)
- [x] FileSystemWatcher 설정
- [x] LiveCode 디렉토리 구조

#### 5. 테스트
- [x] TestScript.cs 컴파일 성공
- [x] 스크립트 로드 및 Update() 호출 확인
- [x] 코드 변경 시 새 버전 로드 (재시작 시)

### 주요 결정 사항
- **Roslyn 기반 컴파일**: Microsoft.CodeAnalysis.CSharp 사용
- **AssemblyLoadContext**: isCollectible: true로 메모리 관리
- **Update() 패턴**: MonoBehaviour 없이도 스크립트 실행 가능

### 테스트 결과
- ✅ TestScript.cs 컴파일 (3072 bytes)
- ✅ Update() 매 프레임 호출
- ✅ "Frame: 60, 120, 180..." 출력 확인
- ✅ 스크립트 수정 후 재시작 시 새 버전 로드

### 알려진 이슈
- ⚠️ FileSystemWatcher 실시간 감지 불안정
  - 엔진 실행 중 파일 수정 시 자동 리로드 안 됨
  - 엔진 재시작 시에는 정상 작동
  - NotifyFilters 설정 개선 필요

---

## Phase 2A: Engine Core 핫 리로딩 ✅

**시작 날짜**: 2026-02-13
**완료 날짜**: 2026-02-13
**소요 시간**: ~3시간

### 완료된 작업

#### 1. 아키텍처 재구성
- [x] IronRose.Contracts 프로젝트 생성 (인터페이스 분리)
- [x] IEngineCore 인터페이스 정의
- [x] Bootstrapper → Contracts만 참조 (정적 참조 제거)

#### 2. Engine 동적 로딩
- [x] EngineCore.cs 구현 (IEngineCore)
- [x] EngineLoader.cs (~170줄)
  - AssemblyLoadContext (isCollectible)
  - Shadow Copy (LoadFromStream - 파일 잠금 없음)
  - 26개 의존성 자동 로드

#### 3. 파일 감지 및 자동 빌드
- [x] EngineWatcher.cs (~120줄)
  - FileSystemWatcher (src/**/*.cs)
  - 디바운싱 타이머 (1초)
  - bin-hot 폴더 전략

#### 4. 핫 리로드 완성
- [x] bin-hot/{timestamp}/ 폴더로 빌드
- [x] 파일 잠금 없이 빌드 성공
- [x] 기존 엔진 언로드 (ALC + GC)
- [x] 새 엔진 로드 from bin-hot
- [x] **코드 변경 즉시 반영!**

### 주요 결정 사항
- **bin-hot 전략**: 파일 잠금 없이 빌드
  - 런타임: bin-hot/{timestamp}/*.dll 사용
  - 다음 실행: 변경된 소스가 bin/*.dll에 반영됨
- **Shadow Copy**: LoadFromStream으로 파일 잠금 방지
- **Contracts 분리**: 기본 ALC에만 로드 (타입 격리)
- **Window.Close() 생략**: SDL2 블록 문제 우회

### 테스트 결과
```
[EngineWatcher] Build SUCCESS ✅
[EngineLoader] Unloading engine... ✅
[EngineLoader] Engine ALC unloaded successfully ✅
[EngineLoader] HOT RELOAD: Loading from bin-hot/... ✅
╔════════════════════════════════════════════════════╗
║ ✨✨✨ FULL HOT RELOAD WORKING!!! ✨✨✨        ║
╚════════════════════════════════════════════════════╝
```

#### 추가 완료 사항 (2026-02-13)
- [x] **ALC 타입 격리 해결**
  - Veldrid, Silk.NET 라이브러리를 기본 ALC에서만 로드
  - Bootstrapper의 Sdl2Window와 Engine의 Sdl2Window 타입 일치
  - **윈도우 보존 성공!** (핫 리로드 시 재생성 없음)

- [x] **스크린샷 기능 구현**
  - logs/*.png로 자동 캡처 (프레임 1, 60, 매 300프레임)
  - AI가 화면을 읽고 분석 가능
  - SwapBuffers() 전 캡처로 실제 렌더링 결과 저장

- [x] **핫 리로드 검증**
  - 색상 변경 테스트 (베이지 → 파란 → 베이지)
  - 스크린샷으로 변경사항 확인
  - **완전히 작동하는 핫 리로드 확인!**

### 해결된 이슈
- ✅ ~~핫 리로드 후 윈도우 재생성~~ → Bootstrapper가 윈도우 관리
- ✅ ~~ALC 타입 격리~~ → Veldrid를 기본 ALC에서만 로드
- ✅ ~~ALC not fully unloaded~~ → 정상 언로드 확인

---

## Phase 2B-0: Bootstrapper/Engine 통합 ✅

**완료 날짜**: 2026-02-13

### 완료된 작업
- [x] Engine.csproj → EXE 전환 (OutputType, AllowUnsafeBlocks, Veldrid.StartupUtilities, Rendering 참조)
- [x] Program.cs를 Engine으로 이동 (EngineLoader/EngineWatcher/IEngineCore 제거)
- [x] EngineCore.cs 리플렉션 제거 (직접 참조로 전환)
- [x] IEngineCore.cs 삭제 (Contracts 프로젝트는 유지)
- [x] Bootstrapper 디렉토리 전체 삭제
- [x] IronRose.sln에서 Bootstrapper 제거
- [x] 부수 파일 업데이트 (launch.json, Claude.md, GraphicsManager 주석)

### 주요 결정 사항
- **전략 변경**: Phase 2A(엔진 전체 핫 리로드) → Phase 2B(플러그인 기반 핫 리로드)
- **Bootstrapper 제거**: 엔진을 동적 로드할 필요가 없으므로 통합
- **리플렉션 제거**: IronRose.Rendering을 직접 참조하여 타입 안전성 확보
- **Contracts 유지**: 향후 플러그인 API 컨테이너로 활용 예정

---

## Phase 3: Unity Architecture ✅

**시작 날짜**: 2026-02-14
**완료 날짜**: 2026-02-14
**소요 시간**: ~2시간

### 완료된 작업

#### Wave 1: 수학 타입 + 유틸리티 (신규 6파일)
- [x] `RoseEngine/Vector3.cs` - x,y,z struct, 연산자, Dot, Cross, Lerp, Distance
- [x] `RoseEngine/Vector2.cs` - x,y struct, 기본 연산자, Vector3 implicit 변환
- [x] `RoseEngine/Quaternion.cs` - x,y,z,w struct, Euler(), AngleAxis(), operator*
- [x] `RoseEngine/Color.cs` - r,g,b,a struct, 프리셋 색상 (white, red, blue 등)
- [x] `RoseEngine/Time.cs` - static deltaTime, time, frameCount (internal set)
- [x] `RoseEngine/Debug.cs` - static Log, LogWarning, LogError

#### Wave 2: Component 계층 (신규 5파일)
- [x] `RoseEngine/Component.cs` - 기본 클래스 (gameObject, transform, GetComponent<T>)
- [x] `RoseEngine/Transform.cs` - position, rotation, localScale, Translate(), Rotate()
- [x] `RoseEngine/MonoBehaviour.cs` - virtual Awake/Start/Update/LateUpdate/OnDestroy
- [x] `RoseEngine/SceneManager.cs` - RegisterBehaviour, Update(deltaTime), Clear()
- [x] `RoseEngine/GameObject.cs` - AddComponent<T>, AddComponent(Type), GetComponent<T>

#### Wave 3: 기존 파일 수정 (2파일)
- [x] **ScriptDomain.cs 수정**
  - `GetLoadedTypes()` 메서드 추가 (로드된 타입 노출)
  - `SetTypeFilter(Func<Type, bool>)` 추가 (MonoBehaviour 필터링)
  - ALC `Resolving` 이벤트 핸들러 추가 (default ALC fallback)
- [x] **EngineCore.cs 수정**
  - 엔진 어셈블리 참조 추가 (RoseEngine 타입 컴파일용)
  - TypeFilter 설정 (MonoBehaviour를 legacy 인스턴스화에서 제외)
  - `RegisterMonoBehaviours()` 신규 메서드 (GameObject 생성 + AddComponent + Awake)
  - `CompileAndLoadScripts()`: SceneManager.Clear() → Reload → RegisterMonoBehaviours 흐름
  - `Update()`: SceneManager.Update() 호출 추가
  - `Shutdown()`: SceneManager.Clear() 호출 추가

#### Wave 4: LiveCode 스크립트 전환
- [x] `LiveCode/TestScript.cs` → MonoBehaviour 패턴으로 전환
  - Screen.SetClearColor + transform.Rotate + Time/Debug 사용
- [x] `LiveCode/AnotherScript.cs` → MonoBehaviour 패턴으로 전환

### 주요 설계 결정

1. **RoseEngine 네임스페이스 위치**: `src/IronRose.Engine/RoseEngine/`
   - IronRose.Engine.dll (default ALC)에 위치
   - LiveCode ALC가 IronRose.Engine 참조 시 default ALC로 fallback → 타입 동일성 보장
   - `typeof(MonoBehaviour).IsAssignableFrom(liveCodeType)` 정상 동작

2. **Transform 자기참조 부트스트랩**
   - GameObject 생성자에서 Transform을 직접 생성하여 수동 와이어링
   - AddComponent를 거치지 않아 null 참조 문제 해결

3. **MonoBehaviour vs Legacy 이중 처리**
   - MonoBehaviour: EngineCore → GameObject 생성 + AddComponent → SceneManager 관리
   - Legacy (Update()만 있는 클래스): 기존 ScriptDomain 리플렉션 방식 유지
   - ScriptDomain에 TypeFilter로 MonoBehaviour 제외

4. **SceneManager 라이프사이클**
   - RegisterBehaviour: Awake() 즉시 호출, pendingStart 큐에 추가
   - Update: pending Start() 처리 → Update() 전체 → LateUpdate() 전체 → frameCount++
   - Clear: OnDestroy() 호출 후 전체 정리

5. **핫 리로드 흐름**
   ```
   파일 변경 감지 → SceneManager.Clear() (OnDestroy 호출)
   → ScriptDomain.Reload() (ALC 언로드/재로드)
   → EngineCore.RegisterMonoBehaviours() (GameObject 생성, Awake 호출)
   → 다음 프레임 SceneManager.Update()에서 Start() 호출
   ```

### 테스트 결과
- ✅ `dotnet build` 성공 (오류 0개)
- ✅ 총 11개 신규 파일 생성 + 4개 기존 파일 수정
- ✅ Unity 스타일 스크립팅 패턴 동작 확인

### 신규 파일 목록 (11개)
```
src/IronRose.Engine/RoseEngine/
├── Vector3.cs        (~65줄)
├── Vector2.cs        (~60줄)
├── Quaternion.cs     (~100줄)
├── Color.cs          (~55줄)
├── Time.cs           (~10줄)
├── Debug.cs          (~20줄)
├── Component.cs      (~12줄)
├── Transform.cs      (~40줄)
├── MonoBehaviour.cs   (~15줄)
├── SceneManager.cs    (~90줄)
└── GameObject.cs      (~60줄)
```

---

## Phase 3.5+: Unity InputSystem (액션 기반 입력) ✅

**완료 날짜**: 2026-02-14

### 완료된 작업

#### 신규 파일 (7개)
- [x] `RoseEngine/InputSystem/InputActionType.cs` - enum: Button, Value, PassThrough
- [x] `RoseEngine/InputSystem/InputActionPhase.cs` - enum: Disabled, Waiting, Started, Performed, Canceled
- [x] `RoseEngine/InputSystem/InputBinding.cs` - 바인딩 사양 + CompositeBinder (fluent `.With()` API)
- [x] `RoseEngine/InputSystem/InputControlPath.cs` - 경로 파싱 (`<Keyboard>/space` → `KeyCode.Space`) + 레거시 Input 재활용
- [x] `RoseEngine/InputSystem/InputAction.cs` - 핵심 액션 클래스 + CallbackContext + phase 전이 + `ReadValue<T>()`
- [x] `RoseEngine/InputSystem/InputActionMap.cs` - 액션 그룹 (AddAction, FindAction, Enable/Disable)
- [x] `RoseEngine/InputSystem/InputSystem.cs` - 정적 매니저 (활성 액션 추적, Update 루프)

#### 수정 파일 (2개)
- [x] `Program.cs` — `InputSystem.Update()` 호출 추가 (Input.Update() 직후)
- [x] `LiveCode/TestScript.cs` — InputSystem 사용 데모 (WASD 2DVector 컴포짓 + Space 버튼)

### 주요 설계 결정

1. **레거시 Input 재활용**: InputSystem이 기존 `RoseEngine.Input` 정적 상태를 읽음 (Silk.NET 이벤트 중복 등록 없음)
2. **Phase 전이 모델**: `Waiting → Started → Performed → Canceled`
   - Button: 누를 때 Started+Performed, 뗄 때 Canceled
   - Value: 매 프레임 값이 있으면 Performed 재호출
   - PassThrough: 입력이 있는 모든 프레임에 Performed
3. **컴포짓 바인딩**: `2DVector` (Up/Down/Left/Right → Vector2), `1DAxis` (Positive/Negative → float)
4. **경로 파싱**: `<Keyboard>/a-z`, `<Mouse>/leftButton` 등 Unity InputSystem 경로 형식 지원

### 프레임 업데이트 흐름
```
Program.OnUpdate()
  → Input.Update()           // 레거시 (기존)
  → InputSystem.Update()     // 신규: 모든 활성 InputAction 평가
  → EngineCore.Update()      // 게임 로직
```

### 사용 예시
```csharp
using RoseEngine;
using RoseEngine.InputSystem;

public class TestScript : MonoBehaviour
{
    private InputAction moveAction;
    private InputAction jumpAction;

    public override void Awake()
    {
        moveAction = new InputAction("Move", InputActionType.Value);
        moveAction.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/w")
            .With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");

        jumpAction = new InputAction("Jump", InputActionType.Button, "<Keyboard>/space");
        jumpAction.performed += ctx => Debug.Log("[InputSystem] Jump!");

        moveAction.Enable();
        jumpAction.Enable();
    }

    public override void Update()
    {
        Vector2 move = moveAction.ReadValue<Vector2>();
        if (move.x != 0 || move.y != 0)
            Debug.Log($"[InputSystem] Move: {move}");
    }
}
```

### 테스트 결과
- ✅ `dotnet build` 성공 (경고 0개, 오류 0개)
- ✅ 레거시 `RoseEngine.Input`과 `RoseEngine.InputSystem` 공존

### 신규 파일 목록 (7개)
```
src/IronRose.Engine/RoseEngine/InputSystem/
├── InputActionType.cs     (~10줄)
├── InputActionPhase.cs    (~10줄)
├── InputBinding.cs        (~30줄)
├── InputControlPath.cs    (~170줄)
├── InputAction.cs         (~210줄)
├── InputActionMap.cs      (~45줄)
└── InputSystem.cs         (~25줄)
```

---

## Phase 3.5++: Unity 호환성 확장 ✅

**완료 날짜**: 2026-02-14

### 완료된 작업

#### 신규 파일 (6개)
- [x] `RoseEngine/Mathf.cs` — Sin, Cos, Lerp, Clamp, Clamp01, SmoothDamp, PingPong, Repeat, Approximately 등 ~40개 메서드
- [x] `RoseEngine/Random.cs` — Range(float/int), insideUnitSphere, onUnitSphere, insideUnitCircle, rotation, ColorHSV
- [x] `RoseEngine/Object.cs` — 기반 클래스: Destroy(deferred), DestroyImmediate, Instantiate(deep clone), FindObjectOfType/s, implicit bool
- [x] `RoseEngine/Attributes.cs` — SerializeField, HideInInspector, Header, Range, Tooltip, Space, RequireComponent 등
- [x] `RoseEngine/YieldInstruction.cs` — WaitForSeconds, WaitForEndOfFrame, WaitUntil, WaitWhile, CustomYieldInstruction
- [x] `RoseEngine/Coroutine.cs` — Coroutine 핸들 (중첩 코루틴 지원)

#### 기존 수정 (8개)
- [x] `Component.cs` — Object 상속, name→gameObject 위임, tag, GetComponentInChildren/InParent, GetComponentsInChildren/InParent
- [x] `GameObject.cs` — Object 상속, SetActive/activeSelf/activeInHierarchy, tag/layer/CompareTag, Find/FindWithTag/FindGameObjectsWithTag, AddComponent 자동 MonoBehaviour 등록
- [x] `MonoBehaviour.cs` — OnEnable/OnDisable, enabled setter 연동, StartCoroutine(IEnumerator/string)/StopCoroutine/StopAllCoroutines, Invoke/InvokeRepeating/CancelInvoke/IsInvoking
- [x] `Transform.cs` — parent/children 계층, SetParent(worldPositionStays), localPosition/localRotation/localScale, 월드↔로컬 좌표 자동 변환, lossyScale, LookAt, RotateAround, TransformPoint/InverseTransformPoint, Space enum
- [x] `SceneManager.cs` — GameObject 레지스트리, 코루틴 스케줄러(WaitForSeconds/중첩/CustomYield), Invoke 타이머, Deferred Destroy 큐, activeInHierarchy 체크, 중복 등록 방지
- [x] `Quaternion.cs` — Inverse, Normalize, normalized, Lerp/Slerp/SlerpUnclamped, RotateTowards, Angle, LookRotation, FromToRotation
- [x] `Vector3.cs` — MoveTowards, SmoothDamp, Angle/SignedAngle, Scale, Project/ProjectOnPlane, Reflect, ClampMagnitude, Min/Max, RotateTowards, LerpUnclamped, Normalize(), Set(), indexer
- [x] `Color.cs` — HSVToRGB, RGBToHSV

### 주요 설계 결정

1. **RoseEngine.Object 베이스**: Component와 GameObject가 Object를 상속. `implicit operator bool`로 Destroy된 오브젝트 null 체크 패턴 지원
2. **Deferred Destroy**: `Object.Destroy(go, delay)` → SceneManager 큐 → 프레임 끝 처리. 자식 재귀 파괴 + OnDisable/OnDestroy 호출 + 레지스트리 정리
3. **Transform 로컬/월드 분리**: 내부에 localPosition/localRotation 저장, position/rotation은 부모 체인 계산. SetParent(worldPositionStays) 지원
4. **코루틴 스케줄러**: SceneManager에서 중앙 관리. WaitForSeconds 타이머, 중첩 Coroutine/IEnumerator 자동 감지, CustomYieldInstruction 지원
5. **AddComponent 자동 등록**: MonoBehaviour를 AddComponent하면 SceneManager.RegisterBehaviour 자동 호출 (중복 방지 guard 포함)
6. **라이프사이클 확장**: Awake → OnEnable → Start → Update → Coroutines → LateUpdate → Destroy 처리 → frameCount++

### 테스트 결과
- ✅ `dotnet build` 성공 (경고 0개, 오류 0개)
- ✅ Demo 프로젝트 정상 실행 (60 FPS 안정)
- ✅ 기존 모든 기능 정상 동작 (렌더링, Input, InputSystem, 핫 리로드)

---

## Phase 4: 3D 렌더링 파이프라인 ✅

**시작 날짜**: 2026-02-14
**완료 날짜**: 2026-02-14
**커밋**: `8a9d809`

### 완료된 작업

#### 4.1 메시 렌더링 시스템
- [x] `Mesh.cs` — GPU 업로드 (Vertex: Position+Normal+UV), dirty-flag 최적화
- [x] `MeshFilter.cs` — mesh 데이터 보유 컴포넌트
- [x] `MeshRenderer.cs` — 전역 렌더러 목록 자동 등록, Material 보유

#### 4.2 셰이더 (GLSL → SPIR-V)
- [x] `vertex.glsl` — Transforms uniform (World + ViewProjection), WorldPos/Normal/UV 출력
- [x] `fragment.glsl` — 멀티라이트 8개 지원 (Directional + Point), Lambert diffuse + Blinn-Phong specular, Material (Color/Emission/HasTexture)

#### 4.3 카메라 시스템
- [x] `Camera.cs` — Camera.main 자동 등록, LookAt/Perspective (왼손 좌표계, depth [0,1])

#### 4.4 프리미티브 생성
- [x] `PrimitiveGenerator.cs` — Cube(24v/36i), Sphere(UV 24×16), Capsule, Plane(10×10), Quad(1×1)
- [x] `GameObject.CreatePrimitive()` — MeshFilter + MeshRenderer 자동 구성

#### 4.5 렌더링 파이프라인 통합
- [x] `RenderSystem.cs` — Solid + Wireframe 듀얼 파이프라인, ResourceSet 캐싱
- [x] EngineCore 렌더 루프: BeginFrame → ClearColor+ClearDepth → Render → EndFrame → SwapBuffers
- [x] 윈도우 리사이즈 자동 종횡비 보정

#### 4.6 추가 Unity 호환성 (Phase 4 범위 확장)
- [x] `Light.cs` — Directional/Point 라이트, 전역 레지스트리 (`Light._allLights`), color/intensity/range
- [x] `Texture2D.cs` — ImageSharp 이미지 로드 → GPU 업로드, TextureView 관리
- [x] `Screen.cs` — Width/Height/DPI/currentResolution (윈도우에서 읽기)
- [x] `Material.cs` 확장 — color, emission, mainTexture 프로퍼티

#### 4.7 예제 에셋 (Assets/)
- [x] `Assets/houseInTheForest/` — 58개 3D 모델 에셋 (숲 속 집 테마)
  - 각 에셋: `model.glb` + `model.obj` + `preview.png` + `description.txt`
  - 벽, 바닥, 문, 계단, 가구, 소품 등 인테리어/건축 에셋

#### 4.8 Cornell Box 데모 씬
- [x] `TestScript.cs` — 5개 벽(빨강/초록/흰색) + 2개 블록 + 천장 포인트 라이트
- [x] WASD 이동 + Space 점프 + F1 와이어프레임 토글 + ESC 종료

### 검증 기준 — 전항목 통과
- ✅ 3D 프리미티브(Cube, Sphere, Capsule, Plane, Quad) 화면 렌더링
- ✅ Lambert + Specular 조명 음영 구분
- ✅ 카메라 위치/방향 변경 시 시점 반영
- ✅ Wireframe 디버그 오버레이
- ✅ 윈도우 리사이즈 종횡비 자동 보정
- ✅ LiveCode에서 프리미티브 생성/회전 스크립트 동작

---

## Phase 5: Unity 에셋 임포터 ✅

**완료 날짜**: 2026-02-14
**커밋**: `d43a185`

### 완료된 작업

#### 5.1 AssetDatabase (GUID 매핑)
- [x] `AssetDatabase.cs` — 프로젝트 `Assets/` 디렉토리 스캔, GUID→경로 매핑, 에셋 캐싱
- [x] `.rose` 메타데이터 파일 자동 생성 (TOML 기반, Unity .meta 대응)

#### 5.2 MeshImporter (AssimpNet)
- [x] `MeshImporter.cs` — GLB/FBX/OBJ 모델 로드 (Triangulate + GenerateNormals + FlipUVs)
- [x] 머티리얼 자동 추출 (albedo color, metallic, roughness, emission, 텍스처 참조)
- [x] `GlbTextureExtractor.cs` — GLB 파일 내 임베디드 텍스처 추출

#### 5.3 TextureImporter (ImageSharp)
- [x] `TextureImporter.cs` — PNG/JPG/BMP → Veldrid GPU 텍스처 업로드

#### 5.4 PrefabImporter
- [x] `PrefabImporter.cs` — Unity .prefab YAML 파싱 → GameObject 생성

#### 5.5 RoseMetadata
- [x] `RoseMetadata.cs` — TOML 기반 에셋 메타데이터 (guid, importer 설정)
- [x] `UnityYamlParser.cs` — Unity YAML `!u!` 태그 처리

### 테스트 결과
- ✅ `dotnet build` 성공
- ✅ GLB 모델 로드 + 텍스처 적용 정상
- ✅ `.rose` 파일 자동 생성 확인
- ✅ AssetDatabase GUID 매핑 정상 작동

### 신규 파일 목록 (7개)
```
src/IronRose.Engine/AssetPipeline/
├── AssetDatabase.cs
├── MeshImporter.cs
├── GlbTextureExtractor.cs
├── TextureImporter.cs
├── PrefabImporter.cs
├── RoseMetadata.cs
└── UnityYamlParser.cs
```

---

## Phase 5A: SpriteRenderer ✅

**완료 날짜**: 2026-02-14
**커밋**: `ba8c715`

### 완료된 작업
- [x] `Rect.cs` — 2D 사각형 구조체 (x, y, width, height)
- [x] `Sprite.cs` — Texture2D 래퍼 (rect, pivot, pixelsPerUnit)
- [x] `SpriteRenderer.cs` — 3D 공간 스프라이트 렌더링, 알파 블렌딩, Unlit 파이프라인
- [x] `TextAlignment.cs` — Left, Center, Right 정렬 enum

### 테스트 결과
- ✅ 3D 공간에서 스프라이트 정상 렌더링
- ✅ 알파 블렌딩 정상 작동
- ✅ SpriteDemo.cs 데모 씬 작동

---

## Phase 5B: TextRenderer ✅

**완료 날짜**: 2026-02-14
**커밋**: `ba8c715`

### 완료된 작업
- [x] `Font.cs` — SixLabors.Fonts 기반 글리프 아틀라스 래스터라이저
- [x] `TextRenderer.cs` — 아틀라스 기반 per-character 쿼드 메시 3D 텍스트 렌더링
- [x] 기존 Sprite 알파 블렌드 파이프라인 재사용

### 테스트 결과
- ✅ 3D 공간에서 텍스트 정상 렌더링
- ✅ TextDemo.cs 데모 씬 작동

---

## Phase 6: 물리 엔진 통합 ✅

**완료 날짜**: 2026-02-14
**커밋**: `066922a`

### 완료된 작업

#### 6.0 사전 작업: FixedUpdate 인프라
- [x] `MonoBehaviour.cs` — FixedUpdate() + 충돌 콜백 (OnCollisionEnter/Stay/Exit, OnTriggerEnter/Stay/Exit) + 2D 콜백
- [x] `EngineCore.cs` — Fixed timestep 누적기 (50Hz, `_fixedAccumulator`)
- [x] `SceneManager.cs` — FixedUpdate() 루프 추가
- [x] `Time.cs` — fixedDeltaTime, fixedTime

#### 6.1 3D 물리: BepuPhysics v2.4.0
- [x] `PhysicsWorld3D.cs` — BepuPhysics 순수 래퍼 (System.Numerics 타입만 사용)
  - Initialize, Step, AddDynamicBody/StaticBody
  - Box/Sphere/Capsule shape 생성
  - Body pose/velocity 읽기/쓰기, ApplyImpulse

#### 6.2 2D 물리: Aether.Physics2D v2.2.0
- [x] `PhysicsWorld2D.cs` — Aether.Physics2D 순수 래퍼
  - Initialize, Step, CreateDynamic/Static/KinematicBody
  - AttachRectangle/Circle fixture 생성

#### 6.3 PhysicsManager (통합 관리자)
- [x] `PhysicsManager.cs` — PhysicsWorld3D/2D 통합, Transform↔Physics 양방향 동기화, 충돌 콜백 디스패치

#### 6.4 Unity 3D 물리 API
- [x] `Collider.cs` — 3D 콜라이더 기본 클래스 (isTrigger, center)
- [x] `BoxCollider.cs`, `SphereCollider.cs`, `CapsuleCollider.cs` — 3D 콜라이더 구현
- [x] `Rigidbody.cs` — BepuPhysics↔Transform 동기화, velocity, AddForce, mass

#### 6.5 Unity 2D 물리 API
- [x] `Collider2D.cs` — 2D 콜라이더 기본 클래스 (isTrigger, offset)
- [x] `BoxCollider2D.cs`, `CircleCollider2D.cs` — 2D 콜라이더 구현
- [x] `Rigidbody2D.cs` — Aether↔Transform 동기화, velocity, AddForce, gravityScale

#### 6.6 Unity 물리 유틸리티
- [x] `ForceMode.cs` — Force, Acceleration, Impulse, VelocityChange enum
- [x] `Collision.cs` — 3D/2D 충돌 데이터, ContactPoint 구조체
- [x] `PhysicsStatic.cs` — 정적 물리 오브젝트 컴포넌트

### 주요 설계 결정
- **의존성 역전**: IronRose.Physics는 Engine 미참조 (순수 래퍼), Engine이 Physics 참조
- **FixedUpdate 50Hz**: 물리 시뮬레이션 고정 타임스텝, 렌더링과 독립
- **Transform↔Physics 동기화**: Dynamic=Physics→Transform, Kinematic=Transform→Physics

### 테스트 결과
- ✅ `dotnet build` 성공
- ✅ 큐브가 바닥으로 떨어지는 중력 시뮬레이션 정상
- ✅ MonoBehaviour.FixedUpdate() 50Hz 호출 확인
- ✅ PhysicsDemo3D.cs 데모 씬 작동

---

## Phase 7: Deferred PBR 렌더링 ✅

**완료 날짜**: 2026-02-15
**커밋**: `1197355`, `3049434`, `4cc5e4d`, `0fa6f56`

### 완료된 작업

#### 7.1 Material 확장 (PBR)
- [x] `Material.cs` — metallic(0-1), roughness(0-1), occlusion(0-1), normalMap, MROMap 추가
- [x] `MaterialUniforms` GPU 구조체 확장

#### 7.2 G-Buffer 생성
- [x] `GBuffer.cs` — 4개 Render Target + Depth 관리
  - RT0: Albedo (R8G8B8A8_UNorm) — RGB: Base Color, A: Alpha
  - RT1: Normal+Roughness (R16G16B16A16_Float) — RGB: World Normal, A: Roughness
  - RT2: Material (R8G8B8A8_UNorm) — R: Metallic, G: Occlusion, B: Emission intensity
  - RT3: WorldPos (R16G16B16A16_Float) — RGB: World Position, A: 1.0
  - Depth: D32_Float_S8_UInt

#### 7.3 Geometry Pass (MRT)
- [x] `deferred_geometry.vert` + `deferred_geometry.frag` — 불투명 3D 메시 → G-Buffer 기록
- [x] 4개 MRT BlendAttachmentDescription 명시적 제공 (Vulkan 호환)

#### 7.4 Lighting Pass (PBR)
- [x] `deferred_lighting.vert` + `deferred_lighting.frag` — Fullscreen triangle PBR 라이팅
- [x] Cook-Torrance BRDF (GGX Distribution + Schlick Fresnel + Smith Geometry)
- [x] Directional + Point 라이트 지원 (최대 64개)
- [x] HDR 중간 텍스처 출력 (R16G16B16A16_Float)

#### 7.5 Forward/Deferred 하이브리드
- [x] Geometry Pass → Lighting Pass → Forward Pass (Sprite/Text) → Post-Processing
- [x] 기존 Forward 렌더러(Sprite, Text, Wireframe)와 공존

#### 7.6 Post-Processing 모듈화
- [x] `PostProcessStack.cs` — 이펙트 파이프라인 관리자
- [x] `PostProcessEffect.cs` — 이펙트 베이스 클래스
- [x] `BloomEffect.cs` — Bloom threshold + Gaussian blur (9-tap, 2-pass separable) + composite
- [x] `TonemapEffect.cs` — ACES Filmic Tone Mapping + Gamma 보정
- [x] `EffectParameterInfo.cs` + `EffectParamAttribute.cs` — 이펙트 파라미터 메타데이터

#### 7.7 Skybox / IBL (Image-Based Lighting)
- [x] `Cubemap.cs` — 큐브맵 로드 및 관리 (단일 파노라마 → 6면 변환)
- [x] `RenderSettings.cs` — ambientLight, skybox, reflectionCubemap 설정
- [x] `skybox.vert` + `skybox.frag` — 스카이박스 렌더링
- [x] 큐브맵 기반 IBL: Split-sum PBR approximation + 디퓨즈 irradiance
- [x] `CameraClearFlags` 지원 (Skybox, SolidColor)

#### 7.8 Demo 구조 개편
- [x] `DemoLauncher.cs` — 데모 씬 선택기
- [x] FrozenCode/LiveCode 분리 (안정 데모 vs 실험 스크립트)
- [x] `PBRDemo.cs` — 5x5 구체 그리드 (metallic × roughness), 멀티라이트
- [x] `CornellBoxDemo.cs`, `PhysicsDemo3D.cs`, `AssetImportDemo.cs`, `SpriteDemo.cs`, `TextDemo.cs`

#### 7.9 네임스페이스 마이그레이션
- [x] `UnityEngine/` → `RoseEngine/` 디렉토리 이동 완료
- [x] 전체 코드베이스 `using RoseEngine;` 통일

### 주요 설계 결정

1. **World Position 직접 기록**: Depth Copy 대신 RT3에 world position 직접 저장 (정밀도 + 안정성)
2. **MRT BlendState**: Vulkan에서 4개 blend attachment 명시적 제공 필수
3. **Clear Color 보존**: Composite pipeline alpha blending으로 배경 clear color 유지
4. **IBL Split-Sum**: 큐브맵 기반 환경 반사, 프레넬 근사 + roughness mip 보간
5. **PostProcessing 모듈화**: 각 이펙트를 독립 클래스로 분리 (BloomEffect, TonemapEffect)
6. **Demo 구조**: FrozenCode(안정) + LiveCode(실험) 이원화

### 테스트 결과
- ✅ PBR 5x5 구체 그리드: metallic/roughness 그라데이션 정확
- ✅ Directional + Point 라이트 PBR 정상 계산
- ✅ Bloom 효과 + ACES Tone Mapping 가시적
- ✅ Sprite/Text 렌더링 Deferred 전환 후에도 정상
- ✅ 스카이박스 + IBL 환경 반사 정상
- ✅ 모든 데모 씬 정상 작동 (60 FPS 안정)

### 신규/수정 파일 요약
```
신규 셰이더 (8개):
  Shaders/deferred_geometry.vert, deferred_geometry.frag
  Shaders/deferred_lighting.vert, deferred_lighting.frag
  Shaders/skybox.vert, skybox.frag
  Shaders/bloom_threshold.frag, bloom_composite.frag
  Shaders/gaussian_blur.frag
  Shaders/tonemap.frag, tonemap_composite.frag
  Shaders/fullscreen.vert

신규 렌더링 (7개):
  src/IronRose.Rendering/GBuffer.cs
  src/IronRose.Rendering/PostProcessing/PostProcessStack.cs
  src/IronRose.Rendering/PostProcessing/PostProcessEffect.cs
  src/IronRose.Rendering/PostProcessing/BloomEffect.cs
  src/IronRose.Rendering/PostProcessing/TonemapEffect.cs
  src/IronRose.Rendering/PostProcessing/EffectParameterInfo.cs
  src/IronRose.Rendering/PostProcessing/EffectParamAttribute.cs

신규 RoseEngine (2개):
  src/IronRose.Engine/RoseEngine/Cubemap.cs
  src/IronRose.Engine/RoseEngine/RenderSettings.cs

대폭 수정:
  src/IronRose.Engine/RenderSystem.cs (~1163줄)
  src/IronRose.Engine/EngineCore.cs (~409줄)
```

---

## Phase 8: 중간정리 (KISS 리팩토링) ✅

**완료 날짜**: 2026-02-15

### 완료된 작업

#### 8.1 데모 보일러플레이트 제거
- [x] `DemoUtils.cs` 신규 — CreateCamera(), LoadFont() 공용 헬퍼 추출
- [x] 7개 데모 파일에서 카메라/폰트 중복 코드 DemoUtils로 교체
  - CornellBoxDemo, PBRDemo, AssetImportDemo, PhysicsDemo3D, SpriteDemo, TextDemo, ColorPulseDemo

#### 8.2 SceneManager.ExecuteDestroy() 단순화
- [x] `Component.cs` — `OnComponentDestroy()` 가상 메서드 추가
- [x] 8개 컴포넌트에 override 구현 (MeshRenderer, SpriteRenderer, TextRenderer, Light, Camera, Rigidbody, Rigidbody2D)
- [x] SceneManager에서 7-type if 분기 (~56줄) → `comp.OnComponentDestroy()` 단일 호출로 축소
- [x] `DestroyComponent()` 헬퍼 메서드 추출

#### 8.3 RenderSystem 라이트 데이터 중복 제거
- [x] `CollectLightInfo()` 정적 헬퍼 추출
- [x] `SetLightInfo` switch(0-7) → unsafe 포인터 인덱싱으로 교체
- [x] `MaxForwardLights = 8` 상수 추출
- [x] UploadDeferredLightData / UploadForwardLightData 공통화

#### 8.4 RenderSystem Draw 메서드 중복 축소
- [x] `DrawMesh()` 공용 드로우콜 헬퍼 추출
- [x] `PrepareMaterial()` 머티리얼→GPU 유니폼 변환 헬퍼 추출
- [x] `SetUnlitLightData()` 스프라이트/텍스트 unlit 모드 헬퍼 추출
- [x] 4개 Draw 메서드 (Opaque, All, Sprites, Texts) 단순화

#### 8.5 데드코드 정리
- [x] PBRDemo 주석 처리된 바닥 코드 삭제
- [x] ScriptDomain TODO 스텁 (SaveState/RestoreState) 삭제

#### 8.6 DemoLauncher 핫리로드 로직 정리
- [x] `SwitchDemo()` 공통 메서드 추출 (Scene Clear → 재등록 → 인스턴스화)
- [x] LoadDemo / LoadLiveCodeDemo → SwitchDemo 위임
- [x] Awake 빌트인 데모 복원 인라인화

### 주요 설계 결정
- **OnComponentDestroy 가상 메서드**: 타입 디스패치를 다형성으로 교체 (OCP 준수)
- **unsafe 포인터 인덱싱**: GPU uniform struct 필드 접근을 switch 없이 연속 메모리 접근으로 최적화
- **DemoUtils 정적 클래스**: 데모 간 공통 패턴을 단일 헬퍼로 통합

### 빌드 결과
- ✅ `dotnet build` 성공 (경고 1개 — 기존 Collider.isRegistered CS0649, 오류 0개)

> 상세 계획: [Phase8_Cleanup.md](Phase8_Cleanup.md)

---

## 개발 메트릭

### 코드 통계 (Phase 40 완료 기준)
- **프로젝트 수**: 9개 (Engine, Demo, Standalone, Contracts, Scripting, Rendering, AssetPipeline, Physics + FrozenCode/LiveCode)
- **총 라인 수**: ~44,475줄 C# SLOC (271파일), ~46,650줄 총 유효 코드
- **NuGet 패키지**: 18개+
- **개발 기간**: 17일 (2026-02-13 ~ 2026-03-02), ~197시간
- **AI 생산성 배수**: 13-18x (맨먼스 18-24 상당)

### 빌드 통계
- **빌드 결과**: 성공

### 실행 통계
- **윈도우**: 1280x720
- **그래픽 백엔드**: Vulkan
- **FPS**: 59-60 (안정적, Stopwatch 기반)
- **프레임타임**: 16-17ms (목표: 16.67ms)

---

## 변경 이력

### 2026-03-02 (Phase 39-40 완료)
- **Phase 40 완료** ✅ (`6231f5f`, `f8968a1`) — UI 2D Gizmo + SceneView 에디터 개선
  - RectGizmoEditor (T키 Rect Tool), UITransformGizmo2D (2D Move/Rotate/Scale)
  - CanvasRenderer 회전/스케일 트랜스폼, HitTest, CollectHitsInRect
  - 카메라 활성 잠금, Unity 스타일 마우스 화면 가장자리 래핑
  - 버그 수정 4건 (GizmoRenderer, Skybox, PostProcessVolume, UIInputField)
  - 중간 포스트모템: ~44,475 SLOC, AI 13-18x 생산성 배수
- **Phase 39 완료** ✅ (`0538028`, `350b6cd`) — ImGui Multi-Viewport + Custom Component + Standalone 빌드
  - ImGui Multi-Viewport: Platform/Renderer 백엔드, 보조 OS 윈도우, 독립 Swapchain
  - Custom Component: Vector4/Quaternion/long/double/byte, 배열/리스트, 씬 참조, 중첩 직렬화
  - LiveCode 핫 리로드 개선: Editor 모드 GO 보존, 타입 캐시 자동 무효화
  - IronRose.Standalone 프로젝트: HeadlessEditor, 시작 씬 ProjectSettings 연동

### 2026-02-15 (Phase 7+ 완료)
- **네임스페이스 마이그레이션** (`0fa6f56`)
  - `UnityEngine/` → `RoseEngine/` 디렉토리 이동
  - MeshImporter 머티리얼 자동 추출 (albedo, metallic, roughness, emission, 텍스처)
  - GlbTextureExtractor 추가 (GLB 임베디드 텍스처 추출)
- **Demo 구조 개편 + PostProcessing 모듈화** (`4cc5e4d`)
  - FrozenCode/LiveCode 이원화 구조
  - PostProcessStack → BloomEffect + TonemapEffect 개별 클래스 분리
  - EngineCore 핫 리로드 개선 (상태 보존 강화)
- **큐브맵 기반 IBL** (`3049434`)
  - Split-sum PBR approximation, 디퓨즈 irradiance
  - CameraClearFlags 지원 (Skybox/SolidColor)
  - skybox.vert/frag 셰이더 추가
- **Phase 7 Deferred PBR 완료** (`1197355`)
  - G-Buffer (4 RT + Depth), Cook-Torrance BRDF
  - Bloom + ACES Tone Mapping 포스트프로세싱
  - Forward/Deferred 하이브리드 렌더링
  - PBRDemo 씬 (5x5 구체 + 멀티라이트)

### 2026-02-14 (Phase 4-6 완료)
- **Phase 6 완료** ✅ (`066922a`) — 물리 엔진 통합
  - BepuPhysics 3D + Aether.Physics2D 2D
  - FixedUpdate 50Hz 누적기
  - Rigidbody/Collider 3D+2D 컴포넌트
  - PhysicsManager Transform↔Physics 동기화
  - PhysicsDemo3D.cs 데모 씬
- **Phase 5A/5B 완료** ✅ (`ba8c715`) — SpriteRenderer + TextRenderer
  - 3D 공간 스프라이트 렌더링 (알파 블렌딩, Unlit)
  - Font 아틀라스 기반 텍스트 렌더링
  - SpriteDemo.cs, TextDemo.cs 데모 씬
- **Phase 5 완료** ✅ (`d43a185`) — 에셋 임포터 파이프라인
  - AssetDatabase GUID 매핑
  - MeshImporter (AssimpNet), TextureImporter (ImageSharp)
  - PrefabImporter (Unity YAML), RoseMetadata (.rose TOML)
  - AssetImportDemo.cs 데모 씬
- **Phase 4 완료** ✅ (3D 렌더링 파이프라인 및 Unity 호환성 대폭 확장)
  - **렌더링 파이프라인**: Forward Rendering (Solid + Wireframe 듀얼)
  - **메시 시스템**: Mesh (GPU dirty-flag), MeshFilter, MeshRenderer
  - **셰이더**: vertex.glsl + fragment.glsl (Lambert + Blinn-Phong + 멀티라이트 8개)
  - **카메라**: Camera.main, LookAt/Perspective (왼손 좌표계)
  - **프리미티브**: Cube, Sphere, Capsule, Plane, Quad (5종)
  - **라이팅**: Light (Directional/Point), 최대 8개 동시
  - **Material**: color, emission, mainTexture
  - **Texture2D**: ImageSharp → GPU 업로드
  - **Screen**: Width/Height/DPI
  - **데모**: Cornell Box 씬 (5벽 + 2블록 + 포인트 라이트)
  - **예제 에셋**: `Assets/houseInTheForest/` 58개 3D 모델 (GLB + OBJ + PNG + TXT)
  - 커밋: `8a9d809`, `8b1c508`
- **Phase 3.5++ 완료** ✅ (Unity 호환성 대폭 확장)
  - **신규 6파일**:
    - `Mathf.cs`: Sin, Cos, Lerp, Clamp, SmoothDamp, PingPong, Repeat, Approximately 등 ~40개 메서드
    - `Random.cs`: Range, insideUnitSphere, onUnitSphere, insideUnitCircle, rotation, ColorHSV
    - `Object.cs`: 기반 클래스 — Destroy(deferred), DestroyImmediate, Instantiate(deep clone), FindObjectOfType, implicit bool
    - `Attributes.cs`: SerializeField, Header, Range, Tooltip, RequireComponent 등 Unity 어트리뷰트
    - `YieldInstruction.cs`: WaitForSeconds, WaitForEndOfFrame, WaitUntil, WaitWhile, CustomYieldInstruction
    - `Coroutine.cs`: 코루틴 핸들 클래스
  - **기존 수정 8파일**:
    - `Component.cs`: Object 상속, GetComponentInChildren/InParent
    - `GameObject.cs`: Object 상속, SetActive/activeSelf/activeInHierarchy, Find/FindWithTag, tag/layer
    - `MonoBehaviour.cs`: OnEnable/OnDisable, StartCoroutine/StopCoroutine, Invoke/InvokeRepeating/CancelInvoke
    - `Transform.cs`: parent/children 계층, localPosition/localRotation, 월드↔로컬 변환, LookAt, RotateAround
    - `SceneManager.cs`: GO 레지스트리, 코루틴 스케줄러, Invoke 타이머, Deferred Destroy 큐
    - `Quaternion.cs`: Inverse, Lerp/Slerp, LookRotation, RotateTowards, FromToRotation, Angle
    - `Vector3.cs`: MoveTowards, SmoothDamp, Angle/SignedAngle, Scale, Project, Reflect, ClampMagnitude
    - `Color.cs`: HSVToRGB, RGBToHSV
  - RoseEngine 전체: 27파일 ~3300줄 + InputSystem 7파일 ~500줄
  - Demo 프로젝트 정상 실행 (60 FPS)
- **Debug 토글 & ImageSharp 취약성 수정**
  - `Debug.Enabled` 프로퍼티 추가 (로그 출력 ON/OFF 토글)
  - `ScreenCaptureEnabled` ON/OFF 토글 테스트 통과
  - SixLabors.ImageSharp 1.0.4 → 3.1.12 업그레이드 (보안 취약성 해결)
  - ImageSharp 3.x API 마이그레이션: `GetPixelRowSpan` → `ProcessPixelRows`
  - NU 취약성 경고 28개 → 0개
- **Phase 3 완료** ✅ (Unity Architecture)
  - RoseEngine 네임스페이스 구현 (Vector3, Vector2, Quaternion, Color, Time, Debug)
  - GameObject/Component/Transform/MonoBehaviour 아키텍처
  - SceneManager 라이프사이클 (Awake → Start → Update → LateUpdate → OnDestroy)
  - ScriptDomain TypeFilter + ALC Resolving fallback
  - EngineCore에 RegisterMonoBehaviours() 통합
  - LiveCode 스크립트 MonoBehaviour 패턴으로 전환
  - "그래픽스 프레임워크"에서 "게임 엔진"으로 전환 달성
- **Phase 3.5 완료** ✅ (입력 시스템 + Silk.NET 마이그레이션)
  - **Veldrid.Sdl2 → Silk.NET.Windowing + Silk.NET.Input 전면 교체**
    - Program.cs: Silk.NET 이벤트 루프 (Load/Update/Render/Closing 콜백)
    - GraphicsManager: 네이티브 핸들(X11/Wayland/Win32) → Veldrid SwapchainSource
    - EngineCore: IWindow 인터페이스로 전환
    - Veldrid.StartupUtilities, Silk.NET.SDL 패키지 제거
  - **Unity 스타일 Input 정적 클래스** (RoseEngine.Input)
    - GetKey/GetKeyDown/GetKeyUp, GetMouseButton/Down/Up
    - mousePosition, mouseScrollDelta
    - GetAxis("Horizontal"/"Vertical"/"Mouse X"/"Mouse Y")
    - anyKey, anyKeyDown
  - **KeyCode enum** + Silk.NET.Input.Key 매핑 (A-Z, 0-9, F1-F12, 화살표, 수정자, 키패드)
  - LiveCode TestScript에 WASD/마우스 입력 데모 추가
- **Phase 3.5+ 완료** ✅ (Unity InputSystem - 액션 기반 입력)
  - **RoseEngine.InputSystem 네임스페이스** (7개 신규 파일)
    - InputAction: 콜백 기반 입력 (started/performed/canceled)
    - InputActionType: Button, Value, PassThrough
    - InputActionPhase: Disabled → Waiting → Started → Performed → Canceled
    - InputBinding + CompositeBinder: 2DVector/1DAxis 컴포짓 바인딩
    - InputControlPath: `<Keyboard>/space` 경로 파싱 → 레거시 Input 재활용
    - InputActionMap: 액션 그룹 관리
    - InputSystem: 정적 매니저 (Update 루프 연동)
  - Program.cs에 `InputSystem.Update()` 추가 (Input.Update() 직후)
  - TestScript에 InputSystem 데모 추가 (WASD 2DVector + Space 버튼 콜백)

### 2026-02-13
- **Phase 0 완료** ✅
  - 프로젝트 초기 설정 완료
  - Progress.md, Claude.md, .gitignore 생성
  - IronRose 테마 색상 정의
- **Phase 1 완료** ✅
  - Bootstrapper 기본 구조 완성 (~85줄)
  - GraphicsManager 구현 (Veldrid.Sdl2 사용)
  - Vulkan 초기화 성공
  - 첫 윈도우 오픈 및 렌더링 성공!
  - Stopwatch 기반 타이밍 시스템 완성
  - FPS 카운터 (59-60 FPS 안정적 유지)
- **Phase 2 완료** ✅
  - Roslyn 컴파일러 래퍼 구현
  - AssemblyLoadContext 핫 스왑 시스템
  - ScriptDomain (동적 로드/언로드/GC)
  - TestScript.cs 컴파일 및 실행 성공!
  - 코드 변경 시 새 버전 자동 로드
- **Phase 2A 완료** ✅ ("Everything is Hot-Reloadable")
  - IronRose.Contracts 인터페이스 분리
  - EngineCore + EngineLoader 구현
  - Shadow Copy (파일 잠금 방지)
  - EngineWatcher (자동 감지 + 빌드)
  - **bin-hot 전략 성공!**
  - **Engine.dll 런타임 핫 리로드 작동!**
