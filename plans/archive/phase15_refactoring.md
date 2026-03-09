# IronRose Refactoring Plan — Phase 15

> **대상 코드베이스**: ~18,000줄 / 116 소스 파일 / 8 프로젝트
> **분석 기준일**: 2026-02-18

---

## HIGH PRIORITY — God Class 분리

### H-1. RenderSystem.cs 분리 (2,890줄)

**현재 책임:**
- G-Buffer 생성/관리
- 메시 렌더링
- 라이트 처리 (Directional, Point, Spot)
- 그림자 매핑
- 포스트프로세싱 (Bloom, Tonemap)
- 디버그 오버레이 (와이어프레임, 바운딩박스)
- GPU 리소스 생성/해제

**리팩터링:**
1. `RenderSystem` → 오케스트레이터 역할만 유지
2. `GeometryPass` — G-Buffer 기록, 메시 렌더링
3. `LightingPass` — Directional/Point/Spot 라이트 적용
4. `ShadowPass` — 그림자 맵 생성 및 샘플링
5. `PostProcessPass` — Bloom, Tonemap 등 이펙트 체인
6. `DebugOverlayPass` — 와이어프레임, 바운딩박스, 그리드

**파일:** `src/IronRose.Engine/RenderSystem.cs`
**예상 결과:** RenderSystem 500줄 이하, 각 패스 300–500줄

---

### H-2. EngineCore.cs 분리 (799줄)

**현재 책임:**
- 윈도우/그래픽스 초기화
- LiveCode 컴파일 & 핫리로드
- 에셋 워밍업 파이프라인
- 메인 업데이트 루프
- 렌더링 호출 조율

**리팩터링:**
1. `EngineCore` — 라이프사이클(Initialize/Update/Shutdown)만 유지
2. `LiveCodeManager` — 핫리로드 컴파일, 파일 감시, 디바운스 로직
3. `AssetWarmupManager` — 에셋 프리로드, 워밍업 큐, 진행 추적
4. `Initialize()` 메서드 → 서브시스템별 private 메서드로 분리:
   - `InitGraphics()`
   - `InitPhysics()`
   - `InitAssets()`
   - `InitEditor()`
   - `InitLiveCode()`

**파일:** `src/IronRose.Engine/EngineCore.cs`
**예상 결과:** EngineCore 300줄 이하

---

### H-3. SceneManager.cs 분리 (503줄)

**현재 책임:**
- GameObject/MonoBehaviour 등록/해제
- Awake → Start → Update → LateUpdate 라이프사이클
- 코루틴 스케줄링
- Invoke/InvokeRepeating 타이머
- 지연 파괴 (Deferred Destroy)
- FindObjectOfType / FindObjectsOfType 검색

**리팩터링:**
1. `SceneManager` — 씬 단위 오브젝트 등록/해제만
2. `BehaviorLifecycle` — Awake/Start/Update/LateUpdate 구동
3. `CoroutineScheduler` — 코루틴 스케줄링, yield 처리
4. `InvokeScheduler` — Invoke/InvokeRepeating 타이머
5. `ObjectQuery` — Find 계열 검색 메서드

**파일:** `src/IronRose.Engine/RoseEngine/SceneManager.cs`
**예상 결과:** SceneManager 150줄 이하

---

## MEDIUM PRIORITY — 커플링 완화 & 중복 제거

### M-1. PhysicsManager 중복 필터링 제거

- **위치:** `src/IronRose.Engine/Physics/PhysicsManager.cs:43-76`
- **문제:** `if (rb._isDestroyed || !rb.gameObject.activeInHierarchy) continue;` **4회 반복**
- **조치:** `IsActiveBody(Rigidbody)` / `IsActiveBody(Rigidbody2D)` 헬퍼 추출

---

### M-2. Rigidbody / Rigidbody2D 공통 베이스 추출

**위치:**
- `src/IronRose.Engine/RoseEngine/Rigidbody.cs`
- `src/IronRose.Engine/RoseEngine/Rigidbody2D.cs`

**문제:**
- `_registered` 플래그 + `EnsureRegistered()` 패턴 중복
- `PhysicsManager.Instance` 직접 접근 반복
- velocity getter/setter null 체크 중복

**조치:**
1. `PhysicsComponent` 베이스 클래스 도입 — `_registered`, `EnsureRegistered()` 공통화
2. `IPhysicsWorld` 인터페이스 도입 — 싱글톤 직접 접근 제거

---

### M-3. 인터페이스 도입

| 인터페이스 | 구현 대상 |
|-----------|----------|
| `IPhysicsWorld` | `PhysicsWorld3D`, `PhysicsWorld2D` |
| `IAssetDatabase` | `AssetDatabase` |
| `IEditorPanel` | `ImGuiHierarchyPanel`, `ImGuiInspectorPanel`, ... |

**목적:** 테스트 용이성, 구현 교체 가능

---

### M-4. ImGuiOverlay 분리 (587줄)

- **위치:** `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs`
- **조치:**
  1. `ImGuiRenderTargetManager` — 오프스크린 렌더 타겟 생성/리사이즈
  2. `ImGuiLayoutManager` — 독 레이아웃 저장/복원
  3. `ImGuiOverlay` — 패널 라이프사이클만

---

## LOW PRIORITY — 코드 품질 개선

### L-1. 매직 넘버/스트링 상수화

| 위치 | 값 | 상수명 제안 |
|------|-----|-----------|
| `EngineCore.cs:37` | `1f / 50f` | `PhysicsConstants.DefaultTickRate` |
| `EngineCore.cs:389` | `1.0` | `LiveCodeConstants.ReloadDebounceSeconds` |
| `Vector2.cs:30` | `1e-5f` | `MathConstants.NormalizeEpsilon` |
| `Vector3.cs` | (동일 패턴) | (동일) |
| `Input.cs:17` | `new bool[3]` | `MouseButton` enum + `Count` |
| `Time.cs:9` | `1f / 50f` | `PhysicsConstants.DefaultTickRate` |
| 여러 곳 | `"RoseCache"`, `"LiveCode"` | `EngineDirectories` 상수 클래스 |

---

### L-2. 리플렉션 캐싱

- **위치:** `src/IronRose.Engine/RoseEngine/MonoBehaviour.cs:71`
- **문제:** `GetType().GetMethod(...)` 매 호출마다 리플렉션 수행
- **조치:** `static Dictionary<(Type, string), MethodInfo>` 캐시 도입

---

### L-3. Dead Code 정리

| 대상 | 위치 | 조치 |
|------|------|------|
| `Object.DontDestroyOnLoad` — 빈 placeholder | `RoseEngine/Object.cs:56-59` | 제거 또는 TODO 주석 |
| `IronRose.AssetPipeline` — 소스 파일 0개 | 프로젝트 | 솔루션에서 제거 |
| `IronRose.Editor` — 소스 파일 0개 | 프로젝트 | 솔루션에서 제거 |

---

### L-4. 네이밍 일관성

- `_allRigidbodies` vs `_rigidbodies` — `All` 접두사 불일치 통일
- `SyncToPhysics` / `SyncFromPhysics` → `PushToPhysics` / `PullFromPhysics` 고려

---

## 실행 순서

| Step | 항목 | 설명 |
|------|------|------|
| 1 | H-1 | RenderSystem 렌더 패스 분리 |
| 2 | H-2 | EngineCore → LiveCodeManager, AssetWarmupManager 분리 |
| 3 | H-3 | SceneManager → Lifecycle, Coroutine, Query 분리 |
| 4 | M-1 | PhysicsManager 중복 필터링 헬퍼 추출 |
| 5 | M-2 | PhysicsComponent 베이스 + IPhysicsWorld 인터페이스 |
| 6 | M-3 | IAssetDatabase, IEditorPanel 인터페이스 도입 |
| 7 | M-4 | ImGuiOverlay 분리 |
| 8 | L-1~L-4 | 매직넘버, 리플렉션 캐싱, Dead Code, 네이밍 정리 |

> 각 Step 완료 후 **빌드 + 데모 실행**으로 회귀 검증.
> Step 간 의존성 없으므로 병렬 진행 가능하나, **H 그룹 우선 완료 권장**.

---

## Progress

| 항목 | 상태 | 완료일 | 비고 |
|------|------|--------|------|
| H-1. RenderSystem 분리 | ✅ 완료 | 2026-02-18 | partial class 5파일 분리 (Shadow/Lighting/Draw/Debug/SSIL) |
| H-2. EngineCore 분리 | ✅ 완료 | 2026-02-18 | LiveCodeManager, AssetWarmupManager 추출 |
| H-3. SceneManager 분리 | ✅ 완료 | 2026-02-18 | CoroutineScheduler, InvokeScheduler 추출 |
| M-1. PhysicsManager 중복 제거 | ✅ 완료 | 2026-02-18 | IsActiveBody 헬퍼 추출 |
| M-2. Rigidbody 공통 베이스 | ✅ 완료 | 2026-02-18 | PhysicsComponent 베이스 클래스 도입 |
| M-3. 인터페이스 도입 | ✅ 완료 | 2026-02-18 | IAssetDatabase, IEditorPanel 인터페이스 |
| M-4. ImGuiOverlay 분리 | ✅ 완료 | 2026-02-18 | ImGuiRenderTargetManager, ImGuiLayoutManager 추출 |
| L-1. 매직 넘버 상수화 | ✅ 완료 | 2026-02-18 | EngineConstants.cs (PhysicsConstants, MathConstants, EngineDirectories, MouseButtonIndex) |
| L-2. 리플렉션 캐싱 | ✅ 완료 | 2026-02-18 | MonoBehaviour static Dictionary 캐시 도입 |
| L-3. Dead Code 정리 | ✅ 완료 | 2026-02-18 | DontDestroyOnLoad 제거, 빈 프로젝트 2개 솔루션에서 제거 |
| L-4. 네이밍 일관성 | ✅ 완료 | 2026-02-18 | _allRigidbodies→_rigidbodies, Sync→Push/Pull 통일 |

> **범례:** ⬜ 대기 / 🔄 진행중 / ✅ 완료 / ⏸️ 보류
