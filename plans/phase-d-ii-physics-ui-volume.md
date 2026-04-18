# Phase D-II: Physics / UI / PostProcessVolume 레지스트리 thread-safe 전환

## 목표

마스터 계획 `plans/threading-safety-fix-master.md` §Phase D (C5, H5)의 **두 번째 서브 phase**. Phase D-I 에서 도입한 `ComponentRegistry<T>` 패턴을 나머지 컴포넌트에 적용한다.

- `PostProcessVolume._allVolumes`
- `Collider._allColliders`, `Collider2D._allColliders2D`
- `Rigidbody._rigidbodies`, `Rigidbody2D._rigidbodies2D`
- `UIText._allUITexts`, `UIInputField._allUIInputFields`

**이 Phase가 건드리지 않는 것**:
- `SceneManager`/`MeshRenderer`/`Light`/`Canvas`/`SpriteRenderer`/`TextRenderer`/`MipMeshFilter` → **Phase D-I 범위** (선행 완료).
- `RoseMetadata.OnSaved` → **Phase D-III 범위**.

## 선행 조건

- **Phase A 머지 완료**: `ThreadGuard` 사용 가능.
- **Phase B 머지 완료**: `AssetDatabase` 에 임시 `.ToArray()` 스냅샷 배치됨.
- **Phase C 머지 완료**: CLI dispatcher 라이프사이클 안정화됨.
- **Phase D-I 머지 완료**: `src/IronRose.Contracts/ComponentRegistry.cs` 존재, `RoseEngine.ComponentRegistry<T>` 사용 가능.

## Worktree 전략

- **단일 worktree**: `feat/phase-d-ii-physics-ui`.
- 한 번의 코더 호출로 7개 컴포넌트 + 관련 호출자를 일괄 전환. 빌드 성공 → 커밋 → 리뷰.

---

## 배경: 현재 코드 구조 (aca-coder가 파일을 다시 열지 않아도 되도록)

### 정적 리스트 정의 (현재)

| 파일 | 라인 | 선언 |
|------|------|------|
| `src/IronRose.Engine/RoseEngine/PostProcessVolume.cs` | 12 | `internal static readonly List<PostProcessVolume> _allVolumes = new();` |
| `src/IronRose.Engine/RoseEngine/Collider.cs` | 16 | `internal static readonly List<Collider> _allColliders = new();` |
| `src/IronRose.Engine/RoseEngine/Collider2D.cs` | 12 | `internal static readonly List<Collider2D> _allColliders2D = new();` |
| `src/IronRose.Engine/RoseEngine/Rigidbody.cs` | 31 | `internal static readonly List<Rigidbody> _rigidbodies = new();` |
| `src/IronRose.Engine/RoseEngine/Rigidbody2D.cs` | 9 | `internal static readonly List<Rigidbody2D> _rigidbodies2D = new();` |
| `src/IronRose.Engine/RoseEngine/UI/UIText.cs` | 36 | `internal static readonly List<UIText> _allUITexts = new();` |
| `src/IronRose.Engine/RoseEngine/UI/UIInputField.cs` | 63 | `internal static readonly List<UIInputField> _allUIInputFields = new();` |

### 라이프사이클 패턴

모든 7개 파일은 동일한 단순 패턴:
- `OnAddedToGameObject()` 에서 `_allXxx.Add(this);`
- `OnComponentDestroy()` 에서 `_allXxx.Remove(this);` (일부는 `UnregisterStatic()` 등 부가 호출 선행)
- `internal static void ClearAll() => _allXxx.Clear();`

**예외 사례**:
- `Rigidbody.cs:182-188` 의 `OnComponentDestroy` 는 `RemoveFromPhysics()` + `_rigidbodies.Remove(this)` + `MarkSiblingCollidersForStaticReregistration()` 순서로 실행. 순서 유지 필수.
- `Rigidbody2D.cs:84-90` 동일.
- `Collider.cs:25-29` 는 `UnregisterStatic()` → `_allColliders.Remove(this)` 순서.
- `Collider2D.cs:21-25` 동일 패턴.

### 외부 순회 지점 (완전 열거)

**`PostProcessVolume._allVolumes`**:
- `src/IronRose.Engine/PostProcessManager.cs:42` — `foreach (var vol in PostProcessVolume._allVolumes)`.
- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs:865` — `foreach (var vol in PostProcessVolume._allVolumes)` (Phase B 에서 `.ToArray()` 임시 적용됐는지 확인 필요).

**`Collider._allColliders`**:
- `src/IronRose.Engine/Physics/PhysicsManager.cs:61` — `Collider._allColliders.Count` (로그 출력용. `.Count` 는 Registry 에서도 지원되므로 변경 불필요).
- `src/IronRose.Engine/Physics/PhysicsManager.cs:102` — `foreach (var col in Collider._allColliders)`.
- `src/IronRose.Engine/Physics/PhysicsManager.cs:152` — `foreach (var col in Collider._allColliders)`.

**`Collider2D._allColliders2D`**:
- `src/IronRose.Engine/Physics/PhysicsManager.cs:119` — `foreach (var col2d in Collider2D._allColliders2D)`.

**`Rigidbody._rigidbodies`**:
- `src/IronRose.Engine/Physics/PhysicsManager.cs:61` — `Rigidbody._rigidbodies.Count` (로그).
- `src/IronRose.Engine/Physics/PhysicsManager.cs:85` — `foreach (var rb in Rigidbody._rigidbodies)` (EnsureRigidbodies).
- `src/IronRose.Engine/Physics/PhysicsManager.cs:131` — `foreach (var rb in Rigidbody._rigidbodies)` (PushTransformsToPhysics).
- `src/IronRose.Engine/Physics/PhysicsManager.cs:264` — `foreach (var rb in Rigidbody._rigidbodies)` (PullPhysicsToTransforms).

**`Rigidbody2D._rigidbodies2D`**:
- `src/IronRose.Engine/Physics/PhysicsManager.cs:91` — EnsureRigidbodies.
- `src/IronRose.Engine/Physics/PhysicsManager.cs:138` — PushTransformsToPhysics.
- `src/IronRose.Engine/Physics/PhysicsManager.cs:271` — PullPhysicsToTransforms.

**`UIText._allUITexts`**:
- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs:2332` — `foreach (var ut in UIText._allUITexts)` (Phase B 에서 `.ToArray()` 적용됐을 수 있음).

**`UIInputField._allUIInputFields`**:
- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs:2337` — `foreach (var uif in UIInputField._allUIInputFields)` (동).

### 이미 메인 스레드에서만 호출되는 경로 확인

- `PhysicsManager.FixedUpdate` 는 `EngineCore.Update` → `SceneManager.FixedUpdate` 경로로 **메인 스레드 전용**.
- `PostProcessManager.Update` 도 `EngineCore.Update` 내부 (line 344).
- `AssetDatabase.ReplaceFontInScene` (line 2321-2342) 는 Phase B 이후 **메인 스레드에서만 호출**됨.
- 따라서 본 Phase 의 lock 은 1) 위반 감지(ThreadGuard) 2) 순회 중 라이프사이클 변경 재진입 방어 용도.

---

## 생성할 파일

없음. (Phase D-I 에서 `ComponentRegistry<T>` 가 이미 도입되어 있음.)

---

## 수정할 파일

### `src/IronRose.Engine/RoseEngine/PostProcessVolume.cs`

- **변경 내용**:
  - `using System.Collections.Generic;` 제거 (다른 List 미사용 확인).
  - 필드: `internal static readonly List<PostProcessVolume> _allVolumes = new();` → `internal static readonly ComponentRegistry<PostProcessVolume> _allVolumes = new();`
  - `OnAddedToGameObject`: `_allVolumes.Add(this);` → `ThreadGuard.DebugCheckMainThread("PostProcessVolume.Register"); _allVolumes.Register(this);`
  - `OnComponentDestroy`: `_allVolumes.Remove(this);` → `ThreadGuard.DebugCheckMainThread("PostProcessVolume.Unregister"); _allVolumes.Unregister(this);`
  - `ClearAll()` 유지 (API 동일).
- **이유**: H5 (AssetDatabase Reimport 경로에서 `_allVolumes` 접근) 완화.

### `src/IronRose.Engine/RoseEngine/Collider.cs`

- **변경 내용**:
  - `using System.Collections.Generic;` 제거.
  - 필드 → `ComponentRegistry<Collider>`.
  - `OnAddedToGameObject`: `_allColliders.Add(this);` → `ThreadGuard.DebugCheckMainThread("Collider.Register"); _allColliders.Register(this);`
  - `OnComponentDestroy`:
    ```csharp
    internal override void OnComponentDestroy()
    {
        ThreadGuard.DebugCheckMainThread("Collider.Unregister");
        UnregisterStatic();
        _allColliders.Unregister(this);
    }
    ```
  - `ClearAll` 유지.
- **이유**: 물리 스텝 중 Collider 생성/삭제 경합 방어. PhysicsManager 순회가 Snapshot 기반으로 전환되므로 `EnsureStaticColliders` 진행 중 `ScheduleDestroy` 로 Collider 가 제거돼도 안전.

### `src/IronRose.Engine/RoseEngine/Collider2D.cs`

- **변경 내용**: Collider.cs 와 동일 패턴.
  - `using System.Collections.Generic;` 제거 (파일 2-3줄 확인: `using nkast.Aether.Physics2D.Dynamics;` 와 함께 있음. `List` 사용은 `_allColliders2D` 한 곳뿐이므로 제거 가능).
  - 필드 → `ComponentRegistry<Collider2D>`.
  - `OnAddedToGameObject` / `OnComponentDestroy` / `ClearAll` 패턴 적용.

### `src/IronRose.Engine/RoseEngine/Rigidbody.cs`

- **변경 내용**:
  - `using System.Collections.Generic;` 는 파일 상단 (line 22) 에 있음. 다른 List 사용 없음 → 제거 가능.
  - 필드: `internal static readonly List<Rigidbody> _rigidbodies = new();` → `internal static readonly ComponentRegistry<Rigidbody> _rigidbodies = new();`
  - `OnAddedToGameObject` (line 174-180):
    ```csharp
    internal override void OnAddedToGameObject()
    {
        ThreadGuard.DebugCheckMainThread("Rigidbody.Register");
        _rigidbodies.Register(this);
        UnregisterSiblingStaticColliders();
    }
    ```
  - `OnComponentDestroy` (line 182-188):
    ```csharp
    internal override void OnComponentDestroy()
    {
        ThreadGuard.DebugCheckMainThread("Rigidbody.Unregister");
        RemoveFromPhysics();
        _rigidbodies.Unregister(this);
        MarkSiblingCollidersForStaticReregistration();
    }
    ```
  - `ClearAll` (line 316): `_rigidbodies.Clear();` 유지.

### `src/IronRose.Engine/RoseEngine/Rigidbody2D.cs`

- **변경 내용**: Rigidbody.cs 와 동일 패턴.
  - `using System.Collections.Generic;` 제거 (파일 1번 줄).
  - 필드 → `ComponentRegistry<Rigidbody2D>`.
  - `OnAddedToGameObject` / `OnComponentDestroy` / `ClearAll` 패턴 적용. `UnregisterSiblingStaticColliders` / `MarkSiblingCollidersForStaticReregistration` 순서 유지.

### `src/IronRose.Engine/RoseEngine/UI/UIText.cs`

- **변경 내용**: 
  - 파일 1-3줄에 `using System; using ImGuiNET; using SNVector2 = ...`. `System.Collections.Generic` 사용 여부 확인 — `_allUITexts` 외 List 미사용이면 해당 using 추가 불필요 (이미 `List<UIText>` 타입이 선언부에서만 쓰였으므로).
  - **주의**: `_allUITexts` 가 `List<UIText>` 로 선언돼 있으므로 `System.Collections.Generic` using 이 파일에 **있다면** 다른 List 사용 없는지 확인 후 제거. 없다면 추가 변경 없음.
  - 필드 → `internal static readonly ComponentRegistry<UIText> _allUITexts = new();`
  - `OnAddedToGameObject`: `_allUITexts.Add(this);` → `ThreadGuard.DebugCheckMainThread("UIText.Register"); _allUITexts.Register(this);`
  - `OnComponentDestroy`: `_allUITexts.Remove(this);` → `ThreadGuard.DebugCheckMainThread("UIText.Unregister"); _allUITexts.Unregister(this);`
  - `ClearAll` 유지.

### `src/IronRose.Engine/RoseEngine/UI/UIInputField.cs`

- **변경 내용**: UIText.cs 와 동일 패턴.
  - **주의**: 이 파일은 `ProcessKeyboardInput` 내부에서 `Input.inputString` 같은 `IEnumerable<char>` 를 쓰는데 `System.Text.StringBuilder` 만 사용하므로 `System.Collections.Generic` using 이 **필요한지 검토**. 전체 파일 grep 결과 `List<>` 사용 없음 → `System.Collections.Generic` 을 명시적으로 쓰지 않았다면 변경 불필요.
  - 필드 → `internal static readonly ComponentRegistry<UIInputField> _allUIInputFields = new();`
  - `OnAddedToGameObject` / `OnComponentDestroy` 패턴 적용.
  - `ClearAll` 유지.

---

## 외부 호출자 수정 (Snapshot 기반으로 전환)

### `src/IronRose.Engine/Physics/PhysicsManager.cs`

| 라인 | 현재 | 변경 |
|------|------|------|
| 61 | `Collider._allColliders.Count` / `Rigidbody._rigidbodies.Count` (로그) | **변경 불필요** (`.Count` 지원) |
| 85 | `foreach (var rb in Rigidbody._rigidbodies)` | `var rbs = Rigidbody._rigidbodies.Snapshot(); foreach (var rb in rbs) { ... }` |
| 91 | `foreach (var rb2d in Rigidbody2D._rigidbodies2D)` | Snapshot 기반 |
| 102 | `foreach (var col in Collider._allColliders)` | Snapshot 기반 |
| 119 | `foreach (var col2d in Collider2D._allColliders2D)` | Snapshot 기반 |
| 131 | `foreach (var rb in Rigidbody._rigidbodies)` | Snapshot 기반 |
| 138 | `foreach (var rb2d in Rigidbody2D._rigidbodies2D)` | Snapshot 기반 |
| 152 | `foreach (var col in Collider._allColliders)` | Snapshot 기반 |
| 264 | `foreach (var rb in Rigidbody._rigidbodies)` | Snapshot 기반 |
| 271 | `foreach (var rb2d in Rigidbody2D._rigidbodies2D)` | Snapshot 기반 |

**주의**: `EnsureStaticColliders` (line 102-126) 는 순회 중 `col.RegisterAsStatic(this)` 를 호출하는데, 이는 내부적으로 `_staticRegistered = true` 만 세팅하고 `_allColliders` 를 변경하지 않으므로 Snapshot 안에서 안전.

**주의**: `EnsureRigidbodies` 의 `rb.EnsureRegistered()` 도 `_rigidbodies` 목록을 변경하지 않으므로 Snapshot 안전.

### `src/IronRose.Engine/PostProcessManager.cs`

| 라인 | 현재 | 변경 |
|------|------|------|
| 42 | `foreach (var vol in PostProcessVolume._allVolumes)` | `var vols = PostProcessVolume._allVolumes.Snapshot(); foreach (var vol in vols) { ... }` |

### `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs`

| 라인 | 현재 | 변경 |
|------|------|------|
| 865 | `foreach (var vol in PostProcessVolume._allVolumes)` (또는 Phase B 에서 이미 `.ToArray()`) | `foreach (var vol in PostProcessVolume._allVolumes.Snapshot()) { ... }` |
| 2332 | `foreach (var ut in UIText._allUITexts)` (또는 이미 `.ToArray()`) | `foreach (var ut in UIText._allUITexts.Snapshot()) { ... }` |
| 2337 | `foreach (var uif in UIInputField._allUIInputFields)` (또는 `.ToArray()`) | `foreach (var uif in UIInputField._allUIInputFields.Snapshot()) { ... }` |

**코더 지침**: 해당 라인을 열어 현재 패턴이 `foreach (var x in Xxx._allYyy)` 인지 `foreach (var x in Xxx._allYyy.ToArray())` 인지 확인. **어느 쪽이든 최종 결과는 `.Snapshot()` 호출**.

---

## NuGet 패키지 (해당 시)

- 없음.

---

## 검증 기준

- [ ] `dotnet build` 성공.
- [ ] 플레이 모드 진입 후 물리 시뮬레이션 정상(Rigidbody/Collider 동작).
- [ ] UIText/UIInputField 가 있는 UI 씬 로드 → 렌더 정상, 입력 정상.
- [ ] PostProcessVolume 이 있는 씬 로드 → 포스트프로세싱 적용 정상.
- [ ] 씬 로드/클리어 반복 (10회) → 누락/크래시 없음.
- [ ] 플레이 모드 중 Rigidbody 동적 추가/삭제 반복 → 물리 시뮬레이션 유지.
- [ ] 폰트 Reimport 중 UIText 가 있는 씬에서 재료 갱신 정상 (AssetDatabase.ReplaceFontInScene 경로).

### 스모크 테스트

1. 플레이 모드에서 1000개 Rigidbody 생성 후 절반 삭제 → 물리 정상.
2. CanvasRenderer 동작 중 UIText 추가/삭제 반복 → 입력 포커스 유지.
3. PostProcessProfile Reimport + 씬 전환 동시 실행 → crash 없음.

---

## 참고

- **성능**: `PhysicsManager.FixedUpdate` 는 60Hz 호출이며 내부에서 Snapshot 을 7회 생성 (85, 91, 102, 119, 131, 138, 264, 271 — 총 8회). 매 frame 8 allocations → ~480 allocs/sec. 1000개 오브젝트 기준 각 `T[1000]` 이 메모리 압박 가능. **후속 성능 측정 필요**하나 본 Phase 에선 안정성 우선.
- **대안 (후속 Phase)**: 성능이 병목이 되면 `PhysicsManager` 에 private `List<Rigidbody> _rbWorkingSet` 버퍼를 두고 `SnapshotWhere(IsActiveBody)` + 내부 버퍼 재사용 패턴으로 할당 제거 가능.
- **UIText/UIInputField 의 ClearAll**: `SceneManager.Clear()` 에서 이미 호출되므로 본 Phase 에서 추가 변경 불필요.
