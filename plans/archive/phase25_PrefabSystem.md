# Phase 25: Prefab System — Prefab Asset + Prefab Variant + Prefab Edit Mode + Instantiate

## Context

Phase 24(Physics Collider Pipeline)까지 IronRose 엔진은 씬 직렬화, 에셋 파이프라인(GUID), Inspector/Hierarchy/Project 에디터 패널, 그리고 `Object.Instantiate()` 기반 딥 클로닝을 갖추고 있습니다. 하지만 **프리팹 워크플로**가 없어서:

- 재사용 가능한 오브젝트 템플릿을 에셋으로 저장할 수 없음
- 프리팹 상속(Variant)이 없어 변형 관리가 어려움
- 프리팹 에디터 모드가 없어 격리된 편집이 불가능

**설계 철학 — "No Instance Override":**
> Unity는 프리팹 인스턴스에서 프로퍼티 오버라이드를 허용하지만,
> IronRose는 **Transform(위치/회전/스케일)만 씬에서 다를 수 있고**,
> 그 외 프로퍼티를 변경하려면 반드시 **Prefab Variant**를 만들어야 합니다.
>
> 이로써:
> - 프리팹 인스턴스의 상태가 항상 예측 가능 (원본 = 인스턴스)
> - 오버라이드 추적/비교/Apply/Revert의 복잡성 제거
> - Variant 트리로 변형을 명시적으로 관리

**핵심 변경 영역:**
1. Prefab Asset 직렬화 (`.prefab` TOML 포맷)
2. PrefabInstance 컴포넌트 (프리팹 연결 — Transform만 오버라이드)
3. Prefab Variant (상속 체인 — 프로퍼티 변경은 여기서만)
4. Prefab Variant Tree View (선택된 프리팹의 Variant 계층 표시)
5. Prefab Edit Mode (격리된 프리팹 편집 환경)
6. Instantiate 파이프라인 (PrefabUtility API)
7. 에디터 통합 (Hierarchy/Inspector/Project Panel)

---

## 1. Prefab Asset 직렬화

### 1.1 `.prefab` 파일 포맷

기존 `.scene` TOML 직렬화(`SceneSerializer.cs`)를 재활용하되, 프리팹 전용 메타데이터를 추가합니다.

```toml
# Assets/Prefabs/Enemy.prefab
[prefab]
version = 1
rootName = "Enemy"

# 루트 GameObject + 자식 계층 전체를 씬과 동일한 포맷으로 저장
[[gameObjects]]
name = "Enemy"
activeSelf = true
[gameObjects.transform]
localPosition = [0.0, 0.0, 0.0]
localRotation = [0.0, 0.0, 0.0, 1.0]
localScale = [1.0, 1.0, 1.0]
parentIndex = -1

[[gameObjects.components]]
type = "MeshFilter"
[gameObjects.components.fields]
primitiveType = "Cube"

[[gameObjects.components]]
type = "MeshRenderer"
[gameObjects.components.fields]
color = [1.0, 0.0, 0.0, 1.0]

# 자식 오브젝트
[[gameObjects]]
name = "Weapon"
activeSelf = true
[gameObjects.transform]
localPosition = [0.5, 0.0, 0.0]
localRotation = [0.0, 0.0, 0.0, 1.0]
localScale = [1.0, 1.0, 1.0]
parentIndex = 0

[[gameObjects.components]]
type = "BoxCollider"
[gameObjects.components.fields]
```

### 1.2 `.prefab.rose` 메타데이터

기존 `RoseMetadata` 시스템을 활용합니다.

```toml
guid = "a1b2c3d4-..."
version = 1

[importer]
type = "PrefabImporter"
```

### 1.3 수정 파일

| 파일 | 변경 내용 |
|------|----------|
| `SceneSerializer.cs` | `SavePrefab(GameObject root, string path)` — 루트 GO + 자식 계층을 `.prefab` TOML로 저장 |
| `SceneSerializer.cs` | `LoadPrefab(string path)` — `.prefab` TOML을 파싱하여 GameObject 계층 복원 (씬에 등록하지 않음) |
| `PrefabImporter.cs` | 전면 재작성 — IronRose TOML 포맷 전용 (Unity YAML 레거시 제거) |
| `AssetDatabase.cs` | `.prefab` 확장자를 `PrefabImporter`에 등록, `Load<GameObject>(path)` 지원 |

### 설계 원칙

- **SceneSerializer 코드 재사용**: `BuildSceneToml()` / `LoadFromTable()`의 GameObject/Component 직렬화 로직을 `internal`로 공개하여 PrefabSerializer에서도 호출
- **프리팹 루트**: `localPosition`은 항상 `(0,0,0)`으로 저장 (인스턴스 배치 시 씬 위치 덮어씀)
- **에셋 참조 보존**: MeshFilter.meshGuid, MeshRenderer.materialGuid 등 기존 GUID 참조 시스템 그대로 사용

---

## 2. PrefabInstance 컴포넌트 (Transform만 오버라이드)

### 2.1 설계 철학: No Instance Override

**유니티와의 차이점:**

| | Unity | IronRose |
|--|-------|----------|
| 인스턴스 프로퍼티 오버라이드 | O (모든 프로퍼티) | **X (Transform만)** |
| 프로퍼티 변경 방법 | 인스턴스에서 직접 변경 | **Prefab Variant 생성 필수** |
| Apply/Revert (인스턴스) | O | **X (불필요)** |
| 오버라이드 추적 복잡성 | 높음 | **없음** |

**장점:**
- 프리팹 인스턴스 = 원본과 항상 동일 (Transform 제외) → 예측 가능한 동작
- 오버라이드 비교/기록/직렬화의 복잡한 코드가 불필요
- Variant 트리로 모든 변형을 명시적으로 관리 → 변경 이력 추적 용이

### 2.2 신규 파일: `PrefabInstance.cs`

프리팹 인스턴스에 자동 부착되는 **내부 컴포넌트**로, 원본 프리팹 에셋과의 연결만 추적합니다.

```csharp
namespace RoseEngine
{
    /// <summary>
    /// 프리팹 인스턴스에 자동 부착.
    /// 원본 프리팹 에셋과의 연결을 유지.
    /// Transform(position/rotation/scale)만 씬에서 다를 수 있음.
    /// 그 외 프로퍼티 변경은 Prefab Variant를 통해서만 가능.
    /// </summary>
    [DisallowMultipleComponent]
    [HideInInspector]
    public class PrefabInstance : Component
    {
        /// <summary>프리팹 에셋 GUID (AssetDatabase 참조)</summary>
        public string prefabGuid { get; internal set; }

        /// <summary>
        /// Inspector에서 Transform 외 프로퍼티 편집을 차단하는 데 사용.
        /// Prefab Edit Mode에서는 false → 편집 허용.
        /// 씬에서는 true → Transform만 편집 가능.
        /// </summary>
        public bool isLockedInScene => !EditorState.IsEditingPrefab;
    }
}
```

### 2.3 씬 직렬화에서의 프리팹 인스턴스 저장

프리팹 인스턴스는 **컴포넌트를 저장하지 않고**, GUID + Transform만 저장합니다.

```toml
# .scene 파일 내 프리팹 인스턴스 — 매우 간결
[[gameObjects]]
name = "Enemy (1)"
activeSelf = true

[gameObjects.prefabInstance]
prefabGuid = "a1b2c3d4-..."

# 씬 내 배치 Transform만 저장
[gameObjects.transform]
localPosition = [5.0, 0.0, 3.0]
localRotation = [0.0, 0.45, 0.0, 0.89]
localScale = [1.0, 1.0, 1.0]
parentIndex = -1
```

**로드 시 흐름:**
```
1. prefabGuid로 프리팹 에셋 로드 (AssetDatabase.LoadByGuid<GameObject>)
2. Object.Instantiate()로 클론
3. 씬 Transform (localPosition/localRotation/localScale) 적용
4. PrefabInstance 컴포넌트 부착 (prefabGuid 설정)
5. 끝 — 프로퍼티 오버라이드 없음
```

### 2.4 Inspector에서의 편집 제한

프리팹 인스턴스(씬에 배치됨)를 Inspector에서 볼 때:

```
┌─ Inspector ─────────────────────────────────┐
│  Enemy (1)                                   │
│  ┌──────────────────────────────────────────┐│
│  │ Prefab: Enemy.prefab                     ││
│  │ [Open Prefab]  [Select Asset]  [Unpack]  ││
│  │                                          ││
│  │ ℹ️ Transform만 편집 가능.                 ││
│  │   프로퍼티 변경은 Variant를 만드세요.     ││
│  │            [Create Variant]              ││
│  └──────────────────────────────────────────┘│
│                                              │
│  ▼ Transform               ← 편집 가능       │
│     Position  5.0  0.0  3.0                  │
│     Rotation  0.0  45.0  0.0                 │
│     Scale     1.0  1.0  1.0                  │
│                                              │
│  ▼ Mesh Renderer           ← 읽기 전용 (회색)│
│     Color     ■ Red                          │
│     Metallic  0.0                            │
│     Roughness 0.5                            │
│                                              │
│  ▼ Box Collider            ← 읽기 전용 (회색)│
│     Size      1.0  1.0  1.0                  │
└──────────────────────────────────────────────┘
```

- **Transform**: 자유롭게 편집 가능 (위치/회전/스케일)
- **그 외 컴포넌트**: 읽기 전용으로 표시 (회색 배경, 편집 비활성화)
- **"Create Variant"** 버튼: 클릭 시 현재 프리팹 기반 Variant 생성 → 인스턴스가 새 Variant를 참조하도록 전환

### 수정 파일

| 파일 | 변경 내용 |
|------|----------|
| (신규) `PrefabInstance.cs` | 프리팹 연결 컴포넌트 (~40줄) |
| `SceneSerializer.cs` | `prefabInstance` 블록 저장/로드 지원 |
| `ImGuiInspectorPanel.cs` | PrefabInstance 감지 → Transform 외 읽기 전용 + 프리팹 헤더 UI |

---

## 3. Prefab Variant (프리팹 상속)

### 3.1 개념

**Prefab Variant**는 기존 프리팹(Base)을 부모로 참조하면서, **차이점(오버라이드)만** 별도 `.prefab` 파일에 저장합니다.
프로퍼티 변경이 필요하면 **반드시 Variant를 통해서만** 가능합니다.

```
Enemy.prefab (Base)
  ├── EnemyRed.prefab (Variant) — color만 변경
  ├── EnemyBlue.prefab (Variant) — color만 변경
  └── EnemyBoss.prefab (Variant) — scale + 컴포넌트 추가
       └── EnemyBossElite.prefab (Variant of Variant) — HP만 변경
```

### 3.2 `.prefab` 포맷 (Variant)

Variant 프리팹은 `basePrefabGuid`가 존재하고, `[[gameObjects]]` 대신 `[[overrides]]`로 차이점만 저장합니다.

```toml
[prefab]
version = 1
rootName = "EnemyRed"
basePrefabGuid = "a1b2c3d4-..."    # 부모 프리팹 GUID → 이 필드가 있으면 Variant

# 오버라이드: 부모와 다른 프로퍼티만 저장
# path 형식: "gameObjectIndex/componentType/fieldName"
[[overrides]]
path = "0/MeshRenderer/color"
value = [1.0, 0.0, 0.0, 1.0]

[[overrides]]
path = "0/Transform/localScale"
value = [2.0, 2.0, 2.0]

# 부모에 없는 추가 컴포넌트
[[addedComponents]]
gameObjectIndex = 0
componentType = "Rigidbody"
[addedComponents.fields]
mass = 5.0
useGravity = true

# 부모에 없는 추가 자식 GameObject (전체 직렬화)
[[addedChildren]]
name = "Shield"
[addedChildren.transform]
localPosition = [0.0, 1.0, 0.0]
localRotation = [0.0, 0.0, 0.0, 1.0]
localScale = [0.5, 0.5, 0.5]
[[addedChildren.components]]
type = "MeshFilter"
[addedChildren.components.fields]
primitiveType = "Sphere"

# 부모에서 제거한 컴포넌트
[[removedComponents]]
gameObjectIndex = 1
componentType = "BoxCollider"
```

### 3.3 Variant 로드 흐름

```
LoadPrefab("EnemyBossElite.prefab")
  ├─ basePrefabGuid → LoadPrefab("EnemyBoss.prefab")     재귀
  │   ├─ basePrefabGuid → LoadPrefab("Enemy.prefab")     재귀
  │   │   └─ Base 프리팹: 전체 GameObject 목록 로드       기반
  │   └─ EnemyBoss overrides 적용
  │   └─ EnemyBoss addedComponents 부착
  │   └─ EnemyBoss addedChildren 추가
  └─ EnemyBossElite overrides 적용
  └─ 최종 GameObject 반환
```

### 3.4 Variant에서의 Override 적용

```csharp
// 의사코드
void ApplyOverrides(GameObject root, List<Override> overrides)
{
    foreach (var ov in overrides)
    {
        // path: "0/MeshRenderer/color" → goIndex=0, compType="MeshRenderer", field="color"
        var (goIdx, compType, fieldName) = ParsePath(ov.path);
        var go = allGameObjects[goIdx];
        var comp = go.GetComponent(compType);
        var field = comp.GetType().GetField(fieldName) ?? comp.GetType().GetProperty(fieldName);
        field.SetValue(comp, DeserializeValue(ov.value, field.FieldType));
    }
}
```

### 3.5 Variant 생성 API

```csharp
// Inspector의 "Create Variant" 버튼 또는 Project Panel 컨텍스트 메뉴:
PrefabUtility.CreateVariant(
    basePrefabGuid: "a1b2c3d4-...",
    variantPath: "Assets/Prefabs/EnemyRed.prefab"
);
// → basePrefabGuid만 있는 빈 Variant .prefab 파일 생성
// → Prefab Edit Mode로 자동 진입하여 편집 시작
```

### 수정/신규 파일

| 파일 | 변경 내용 |
|------|----------|
| (신규) `PrefabUtility.cs` | CreatePrefab, CreateVariant, InstantiatePrefab, Unpack 등 |
| `SceneSerializer.cs` | Variant 오버라이드 포맷 직렬화/역직렬화 |
| `PrefabImporter.cs` | Variant 로드 체인 (재귀 부모 해석) |

---

## 4. Prefab Variant Tree View

### 4.1 개념

Project Panel에서 `.prefab` 파일을 선택하면 **Variant Tree View** 패널이 표시됩니다.
해당 프리팹을 기반으로 한 모든 Variant의 상속 계층을 트리로 보여줍니다.

### 4.2 UI 레이아웃

```
┌─ Variant Tree ──────────────────────────────┐
│                                              │
│  ▼ 🔵 Enemy.prefab              (Base)      │
│     ├── 🟣 EnemyRed.prefab      (Variant)   │
│     ├── 🟣 EnemyBlue.prefab     (Variant)   │
│     └── ▼ 🟣 EnemyBoss.prefab   (Variant)   │
│            └── 🟣 EnemyBossElite (Variant)   │
│                                              │
│  ─────────────────────────────────────────── │
│  Selected: EnemyBoss.prefab                  │
│  Base: Enemy.prefab                          │
│  Overrides: 3 properties, 1 added component  │
│  Children: 1 variant                         │
│                                              │
│  [Open in Prefab Editor]                     │
│  [Create Variant]                            │
└──────────────────────────────────────────────┘
```

### 4.3 표시 조건

- **Project Panel에서 `.prefab` 파일 선택 시**: Variant Tree View 패널 활성화
- **Hierarchy에서 프리팹 인스턴스 선택 시**: 해당 프리팹의 Variant Tree 표시
- **그 외 선택 시**: Variant Tree View 숨김 또는 빈 상태

### 4.4 트리 구축 방식

```csharp
/// <summary>
/// 프로젝트 내 모든 .prefab 파일을 스캔하여 Variant 관계 맵을 구축.
/// AssetDatabase 초기화 시 1회 빌드 + .prefab 파일 변경 시 갱신.
/// </summary>
public class PrefabVariantTree
{
    // basePrefabGuid → List<variantGuid> 매핑
    private Dictionary<string, List<string>> _childVariants;

    // prefabGuid → basePrefabGuid 매핑
    private Dictionary<string, string> _parentMap;

    /// <summary>주어진 프리팹의 루트(Base) GUID를 찾아 반환</summary>
    public string FindRootBase(string prefabGuid);

    /// <summary>주어진 프리팹 기준 전체 트리 (조상 + 자손) 반환</summary>
    public TreeNode BuildTree(string prefabGuid);

    /// <summary>.prefab 파일 추가/삭제/수정 시 트리 갱신</summary>
    public void Rebuild();
}
```

### 4.5 트리 갱신 타이밍

1. **AssetDatabase 초기화**: 모든 `.prefab` 파일의 `[prefab].basePrefabGuid` 읽어서 맵 구축
2. **`.prefab` 파일 생성/삭제**: `FileSystemWatcher` 이벤트 → `Rebuild()`
3. **Prefab Edit Mode 저장**: 프리팹 저장 시 → `Rebuild()` (basePrefabGuid 변경 가능)

### 수정/신규 파일

| 파일 | 변경 내용 |
|------|----------|
| (신규) `PrefabVariantTree.cs` | Variant 관계 맵 + 트리 빌더 (~150줄) |
| (신규) `ImGuiVariantTreePanel.cs` | ImGui 트리 뷰 패널 (~200줄) |
| `ImGuiOverlay.cs` | Variant Tree Panel 등록 + 표시 조건 |
| `AssetDatabase.cs` | 초기화 시 PrefabVariantTree 빌드 트리거 |

---

## 5. Prefab Edit Mode (격리된 프리팹 편집)

### 5.1 개념

프리팹을 **격리된 환경**에서 열어 편집하고 저장합니다.
**프로퍼티 변경이 가능한 유일한 장소**입니다 (씬에서는 Transform만 편집 가능하므로).

**진입 방법:**
- Project Panel에서 `.prefab` 파일 더블클릭
- Hierarchy에서 프리팹 인스턴스의 Inspector "Open Prefab" 버튼
- Variant Tree View에서 "Open in Prefab Editor" 버튼

### 5.2 동작

```
진입:
1. SceneSerializer.BuildSceneToml() → _savedSceneSnapshot에 메모리 저장
2. SceneManager.Clear()
3. PrefabImporter.Load(prefabPath) → 격리된 프리팹 씬에 로드
4. EditorState.IsEditingPrefab = true
5. SceneView 카메라를 프리팹 중심으로 이동
6. Hierarchy에 프리팹 계층만 표시
7. Inspector: 모든 프로퍼티 편집 가능 (isLockedInScene = false)

저장:
1. 현재 프리팹 GO 계층 → .prefab TOML 저장
2. (Variant인 경우) 부모와 비교하여 오버라이드 자동 계산 → Variant TOML 저장
3. PrefabVariantTree.Rebuild()

복귀:
1. (변경사항이 있으면) 저장 확인 다이얼로그
2. SceneManager.Clear()
3. SceneSerializer.LoadFromTable(_savedSceneSnapshot) → 원래 씬 복원
4. EditorState.IsEditingPrefab = false
5. 씬 내 해당 프리팹 인스턴스 자동 갱신 (RefreshPrefabInstances)
```

### 5.3 Breadcrumb UI

```
┌───────────────────────────────────────────────────────────┐
│  ◀ DefaultScene  >  Enemy.prefab  >  EnemyBoss.prefab    │
│                                       [Save] [Back]       │
└───────────────────────────────────────────────────────────┘
```

- 중첩 진입 지원: 프리팹 안의 중첩 프리팹을 더블클릭하면 스택에 push
- "Back" 또는 breadcrumb 클릭으로 이전 레벨로 복귀

### 5.4 Variant 편집 시 오버라이드 자동 계산

Variant를 Prefab Edit Mode에서 편집 → 저장할 때:

```
1. basePrefabGuid로 부모 프리팹 로드
2. 현재 편집 중인 GO 계층과 부모를 필드 단위 비교
3. 다른 부분만 [[overrides]]로 추출
4. 부모에 없는 컴포넌트 → [[addedComponents]]
5. 부모에 없는 자식 → [[addedChildren]]
6. 부모에 있지만 제거된 컴포넌트 → [[removedComponents]]
7. Variant .prefab 파일에 저장
```

### 5.5 에디터 상태

```csharp
// EditorState.cs 확장
public static class EditorState
{
    // 기존
    public static bool IsEditingCollider { get; set; }

    // 신규
    public static bool IsEditingPrefab { get; set; }
    public static string EditingPrefabPath { get; set; }
    public static string EditingPrefabGuid { get; set; }

    // 프리팹 편집 스택 (중첩 진입 지원)
    internal static Stack<PrefabEditContext> _prefabEditStack = new();

    // 씬 복원용 스냅샷 (최초 진입 시 저장)
    internal static TomlTable? _savedSceneSnapshot;
    internal static string? _savedScenePath;
}

internal struct PrefabEditContext
{
    public string prefabPath;
    public string prefabGuid;
    public TomlTable sceneSnapshot;  // 이전 레벨의 상태
}
```

### 수정/신규 파일

| 파일 | 변경 내용 |
|------|----------|
| (신규) `PrefabEditMode.cs` | Enter/Exit/Save 로직 + 오버라이드 자동 계산 (~300줄) |
| `EditorState.cs` | IsEditingPrefab, prefab edit stack 추가 |
| `ImGuiOverlay.cs` | Breadcrumb UI, Save/Back 버튼 |
| `ImGuiHierarchyPanel.cs` | Prefab Edit 모드 시 프리팹 계층만 표시 |
| `ImGuiInspectorPanel.cs` | "Open Prefab" 버튼 (PrefabInstance 감지 시) |

---

## 6. PrefabUtility API + Instantiate 파이프라인

### 6.1 PrefabUtility 정적 API

```csharp
namespace RoseEngine
{
    public static class PrefabUtility
    {
        // ── 프리팹 에셋 생성 ──

        /// <summary>씬의 GameObject를 .prefab 파일로 저장 (Base 프리팹)</summary>
        public static string SaveAsPrefab(GameObject go, string path);

        /// <summary>프리팹 Variant 생성 (basePrefab 기반, 빈 오버라이드)</summary>
        public static string CreateVariant(string basePrefabGuid, string variantPath);

        // ── 프리팹 인스턴스 생성 ──

        /// <summary>
        /// 프리팹을 씬에 인스턴스화. PrefabInstance 컴포넌트 자동 부착.
        /// Object.Instantiate와 달리 프리팹 연결 유지.
        /// </summary>
        public static GameObject InstantiatePrefab(string prefabGuid);
        public static GameObject InstantiatePrefab(string prefabGuid, Vector3 position, Quaternion rotation);
        public static GameObject InstantiatePrefab(string prefabGuid, Transform parent);

        // ── 프리팹 관계 조회 ──

        /// <summary>GameObject가 프리팹 인스턴스인지 확인</summary>
        public static bool IsPrefabInstance(GameObject go);

        /// <summary>프리팹 인스턴스의 원본 프리팹 GUID 반환</summary>
        public static string GetPrefabGuid(GameObject instanceRoot);

        /// <summary>해당 프리팹이 Variant인지 확인</summary>
        public static bool IsVariant(string prefabGuid);

        /// <summary>Variant의 Base 프리팹 GUID 반환</summary>
        public static string GetBasePrefabGuid(string prefabGuid);

        // ── 프리팹 에셋 갱신 ──

        /// <summary>프리팹 에셋 변경 후 씬 내 모든 인스턴스에 반영</summary>
        public static void RefreshPrefabInstances(string prefabGuid);

        // ── Unpack (프리팹 연결 해제) ──

        /// <summary>프리팹 연결 해제 (일반 GameObject로 변환, PrefabInstance 제거)</summary>
        public static void UnpackPrefabInstance(GameObject instanceRoot);
    }
}
```

### 6.2 InstantiatePrefab 흐름

```
PrefabUtility.InstantiatePrefab(prefabGuid)
  ├─ 1. AssetDatabase.LoadByGuid<GameObject>(prefabGuid) → 프리팹 템플릿 로드
  │      (Variant인 경우 재귀 로드 + 오버라이드 적용된 결과)
  ├─ 2. Object.Instantiate(template) → 딥 클론
  ├─ 3. PrefabInstance 컴포넌트 부착
  │     └─ prefabGuid = prefabGuid
  ├─ 4. SceneManager에 등록
  └─ 5. 반환
```

### 6.3 Object.Instantiate vs PrefabUtility.InstantiatePrefab

| 기능 | Object.Instantiate | PrefabUtility.InstantiatePrefab |
|------|-------------------|-------------------------------|
| 딥 클론 | O | O |
| 프리팹 연결 | X (독립 복사본) | O (PrefabInstance 부착) |
| 씬에서 편집 | 모든 프로퍼티 | Transform만 |
| 런타임 사용 | O (스크립트용) | O (에디터 + 런타임) |

### 6.4 RefreshPrefabInstances

프리팹 에셋이 변경되면 씬 내 해당 프리팹의 모든 인스턴스를 갱신합니다:

```
RefreshPrefabInstances(prefabGuid)
  ├─ 1. 씬에서 prefabGuid를 참조하는 모든 PrefabInstance 검색
  ├─ 2. 각 인스턴스의 현재 Transform 저장 (position/rotation/scale + parent)
  ├─ 3. 기존 인스턴스 Destroy
  ├─ 4. 새로 InstantiatePrefab + 저장된 Transform 복원
  └─ 5. (Variant 자손도 재귀적으로 갱신)
```

### 수정/신규 파일

| 파일 | 변경 내용 |
|------|----------|
| (신규) `PrefabUtility.cs` | 프리팹 유틸리티 정적 API (~300줄) |
| `AssetSpawner.cs` | `SpawnPrefab()` → `PrefabUtility.InstantiatePrefab()` 사용으로 변경 |

---

## 7. 에디터 통합

### 7.1 Hierarchy Panel — 프리팹 표시

```
┌─ Scene Hierarchy ──────────────────────────┐
│  ▼ DefaultScene                             │
│     📷 Main Camera                          │
│     💡 Directional Light                    │
│     🔵 Enemy (1)          ← 파란색: 프리팹  │
│       └─ 🔵 Weapon                          │
│     🔵 Enemy (2)                            │
│       └─ 🔵 Weapon                          │
│     🔵 EnemyBoss (1)     ← 파란색: 프리팹  │
│       └─ 🔵 Weapon                          │
│       └─ 🔵 Shield                          │
│     ⬜ Cube               ← 일반 GameObject │
└─────────────────────────────────────────────┘
```

- **프리팹 인스턴스**: 이름을 파란색으로 표시 (Base든 Variant든 동일)
- **더블클릭**: Prefab Edit Mode 진입
- **프리팹 인스턴스의 자식**: 같은 파란색으로 표시 (프리팹 계층의 일부임을 표시)

### 7.2 Inspector Panel — 프리팹 인스턴스

(섹션 2.4에서 상세 설명됨)

- 프리팹 헤더: "Open Prefab", "Select Asset", "Unpack", "Create Variant"
- Transform: 편집 가능
- 그 외 컴포넌트: 읽기 전용 (회색)

### 7.3 Project Panel — 프리팹 관리

**컨텍스트 메뉴 추가:**
- "Create > Prefab Variant" — 선택된 `.prefab` 파일의 Variant 생성

**드래그 앤 드롭:**
- Project Panel → SceneView/Hierarchy: 프리팹을 드래그하여 씬에 인스턴스 배치
- Hierarchy → Project Panel: 씬 GameObject를 드래그하여 프리팹으로 저장

**더블클릭:**
- `.prefab` 파일 더블클릭 → Prefab Edit Mode 진입

**Hierarchy 컨텍스트 메뉴 추가:**
- "Save As Prefab..." — 선택된 GameObject를 프리팹으로 저장 (파일 대화상자)
- "Unpack Prefab" — 프리팹 연결 해제 (일반 GO로 변환)

### 수정 파일

| 파일 | 변경 내용 |
|------|----------|
| `ImGuiHierarchyPanel.cs` | 프리팹 인스턴스 색상 표시, 더블클릭 Prefab Edit, 컨텍스트 메뉴 |
| `ImGuiInspectorPanel.cs` | 프리팹 헤더 UI, Transform 외 읽기 전용, "Create Variant" 버튼 |
| `ImGuiProjectPanel.cs` | "Create Variant" 컨텍스트 메뉴, 프리팹 더블클릭 Edit Mode |
| `ImGuiOverlay.cs` | Prefab Edit Mode breadcrumb 바 |

---

## 구현 순서

```
Phase 24 (Physics Collider Pipeline)
  ↓
Step 1: Prefab 직렬화 기반
  - SceneSerializer에 SavePrefab/LoadPrefab 추가
  - PrefabImporter 재작성 (IronRose TOML 포맷)
  - AssetDatabase에 .prefab 등록
  - 기본 테스트: GameObject → .prefab 저장 → 로드 → 동일 구조 검증
  ↓
Step 2: PrefabInstance + PrefabUtility
  - PrefabInstance.cs 신규 (prefabGuid 연결만)
  - PrefabUtility.cs 신규 (SaveAsPrefab, InstantiatePrefab)
  - AssetSpawner 연동 (기존 SpawnPrefab → PrefabUtility 사용)
  - SceneSerializer: prefabInstance 블록 저장/로드
  - 테스트: 프리팹 저장 → 인스턴스화 → 씬 저장/로드 → 프리팹 연결 유지 확인
  ↓
Step 3: Inspector 편집 제한 + 에디터 UI
  - Inspector: PrefabInstance 감지 → Transform 외 읽기 전용
  - Inspector: 프리팹 헤더 (Open/Select/Unpack/Create Variant)
  - Hierarchy: 프리팹 인스턴스 색상 표시 (파란)
  - Hierarchy: 컨텍스트 메뉴 ("Save As Prefab", "Unpack")
  ↓
Step 4: Prefab Variant
  - Variant .prefab TOML 포맷 (basePrefabGuid + overrides)
  - PrefabUtility.CreateVariant()
  - PrefabImporter: Variant 로드 체인 (재귀 부모 해석)
  - 오버라이드 자동 계산 (부모 vs 현재 비교)
  - Hierarchy: Variant 인스턴스 보라색 표시
  - 테스트: Base → Variant 생성 → Variant 인스턴스화 → 올바른 프로퍼티 검증
  ↓
Step 5: Prefab Variant Tree View
  - PrefabVariantTree.cs 신규 (관계 맵 + 트리 빌더)
  - ImGuiVariantTreePanel.cs 신규 (ImGui 트리 뷰)
  - 프리팹 선택 시 Variant Tree 표시
  - 트리에서 더블클릭 → Prefab Edit Mode 진입
  ↓
Step 6: Prefab Edit Mode
  - PrefabEditMode.cs 신규 (Enter/Exit/Save)
  - EditorState 확장 (IsEditingPrefab, prefab edit stack)
  - Variant 저장 시 오버라이드 자동 계산
  - ImGuiOverlay breadcrumb UI
  - Hierarchy/Inspector: Prefab Edit 모드 동작
  - 테스트: 프리팹 편집 진입 → 편집 → 저장 → 복귀 → 씬 정상 복원
  ↓
Step 7: RefreshPrefabInstances + Unpack + 최종 통합
  - 프리팹 에셋 변경 시 씬 내 모든 인스턴스 자동 갱신
  - Variant 자손 재귀 갱신
  - Unpack 구현 (PrefabInstance 제거, 일반 GO 변환)
  - Project Panel: "Create Variant" 메뉴, 프리팹 더블클릭
  - Drag & Drop: Hierarchy ↔ Project Panel
  - 최종 통합 테스트
```

---

## 신규 파일 목록 (예상 5개)

| 파일 | 설명 | 예상 라인 |
|------|------|-----------|
| `PrefabInstance.cs` | 프리팹 연결 컴포넌트 (GUID만) | ~40줄 |
| `PrefabUtility.cs` | 프리팹 유틸리티 정적 API | ~300줄 |
| `PrefabEditMode.cs` | 프리팹 편집 모드 + 오버라이드 자동 계산 | ~300줄 |
| `PrefabVariantTree.cs` | Variant 관계 맵 + 트리 빌더 | ~150줄 |
| `ImGuiVariantTreePanel.cs` | Variant Tree View ImGui 패널 | ~200줄 |

## 주요 수정 파일 (예상 8개)

| 파일 | 변경 요약 |
|------|----------|
| `SceneSerializer.cs` | prefabInstance 블록 + Variant 오버라이드 직렬화/역직렬화, 내부 메서드 공개 |
| `PrefabImporter.cs` | IronRose TOML 포맷 전면 재작성 + Variant 로드 체인 |
| `AssetDatabase.cs` | .prefab 등록, PrefabVariantTree 빌드 트리거 |
| `AssetSpawner.cs` | PrefabUtility.InstantiatePrefab 사용 |
| `EditorState.cs` | IsEditingPrefab, prefab edit stack |
| `ImGuiHierarchyPanel.cs` | 프리팹 색상 표시, 더블클릭 Edit, 컨텍스트 메뉴 |
| `ImGuiInspectorPanel.cs` | 프리팹 헤더, Transform 외 읽기 전용, Create Variant |
| `ImGuiOverlay.cs` | Variant Tree Panel 등록, Prefab Edit breadcrumb |
| `ImGuiProjectPanel.cs` | Create Variant 메뉴, 프리팹 더블클릭 |

**예상 변경 통계:** ~13개 파일, +990줄
