# Phase D-I: ComponentRegistry 도입 + Scene/Render 레지스트리 thread-safe 전환

## 목표

마스터 계획 `plans/threading-safety-fix-master.md` §Phase D (C5, H5)의 **첫 번째 서브 phase**. 다음을 수행한다.

- 공통 헬퍼 `ComponentRegistry<T>` 를 `IronRose.Contracts` 에 도입한다.
- 씬/렌더 계열 6개 정적 리스트를 `ComponentRegistry<T>` 기반으로 전환한다 (**기존 `internal static readonly List<T> _allXxx` 필드 이름은 유지** — 외부 호출자가 많기 때문. 대신 필드를 **감싸는 Registry 패턴**을 도입한다).
  - `SceneManager._allGameObjects`, `_behaviours`, `_pendingStart`, `_destroyQueue`
  - `MeshRenderer._allRenderers`
  - `Light._allLights`
  - `Canvas._allCanvases`
  - `SpriteRenderer._allSpriteRenderers`
  - `TextRenderer._allTextRenderers`
  - `MipMeshFilter._allMipMeshFilters`
- 외부 순회 지점(RenderSystem, SceneViewRenderer, CanvasRenderer, AssetDatabase 등 약 30여 곳)을 **스냅샷 기반**으로 전환한다.
- 각 컴포넌트의 라이프사이클(Register/Unregister) 진입에 `ThreadGuard.DebugCheckMainThread(...)` 를 삽입한다 (Debug 빌드 전용).

**이 Phase가 건드리지 않는 것**:
- 물리/UI/Volume 쪽 `_all*` (`Collider`, `Collider2D`, `Rigidbody`, `Rigidbody2D`, `UIText`, `UIInputField`, `PostProcessVolume`) → **Phase D-II 범위**.
- `RoseMetadata.OnSaved` event 동기화 → **Phase D-III 범위**.
- `AssetDatabase.cs`의 기존 일부 `ToArray()` 임시 스냅샷(Phase B에서 이미 들어가 있을 수 있음)은 그대로 유지하고, 본 Phase에서는 **새 `Snapshot()` API를 같이 쓰도록** 마이그레이션한다.

## 선행 조건

- **Phase A 머지 완료**: `RoseEngine.ThreadGuard.CaptureMainThread()`, `ThreadGuard.CheckMainThread(string)`, `ThreadGuard.DebugCheckMainThread(string)` 사용 가능.
- **Phase B 머지 완료**: `AssetDatabase.cs` 내 일부 `.ToArray()` 스냅샷(있는 경우)은 유지되어 있음.
- **Phase C 머지 완료**: CLI dispatcher 라이프사이클은 본 Phase와 독립.
- `IronRose.Contracts.csproj` 는 `net10.0`, `ImplicitUsings=enable`, `Nullable=enable`. 외부 의존 없음.
- `IronRose.Engine.csproj` 는 이미 `IronRose.Contracts` 를 참조 중.

## Worktree 전략

- **단일 worktree**: `feat/phase-d-i-component-registry`.
- 한 번의 코더 호출로 구현 → `dotnet build` 성공 → 커밋 → 리뷰.
- 이 Phase만 머지된 상태에서도 빌드/런타임이 정상 동작해야 한다 (Phase D-II/D-III 미머지 시에도).

---

## 배경: 현재 코드 구조 (aca-coder가 파일을 다시 열지 않아도 되도록)

### 정적 리스트 정의 (현재)

| 파일 | 라인 | 선언 | 비고 |
|------|------|------|------|
| `src/IronRose.Engine/RoseEngine/SceneManager.cs` | 59 | `private static readonly List<MonoBehaviour> _behaviours = new();` | Update 루프에서 순회 |
| `src/IronRose.Engine/RoseEngine/SceneManager.cs` | 60 | `private static readonly List<MonoBehaviour> _pendingStart = new();` | Update 초반에 처리 |
| `src/IronRose.Engine/RoseEngine/SceneManager.cs` | 61 | `private static readonly List<GameObject> _allGameObjects = new();` | public `AllGameObjects` 로 노출 |
| `src/IronRose.Engine/RoseEngine/SceneManager.cs` | 64 | `private static readonly List<DestroyEntry> _destroyQueue = new();` | Destroy 지연 처리 |
| `src/IronRose.Engine/RoseEngine/MeshRenderer.cs` | 10 | `internal static readonly List<MeshRenderer> _allRenderers = new();` | RenderSystem/SceneViewRenderer/AssetDatabase 순회 |
| `src/IronRose.Engine/RoseEngine/Light.cs` | 55 | `internal static readonly List<Light> _allLights = new();` | RenderSystem.Shadow/Lighting 순회 |
| `src/IronRose.Engine/RoseEngine/Canvas.cs` | 27 | `internal static readonly List<Canvas> _allCanvases = new();` | CanvasRenderer/CLI.UI 순회 |
| `src/IronRose.Engine/RoseEngine/SpriteRenderer.cs` | 19 | `internal static readonly List<SpriteRenderer> _allSpriteRenderers = new();` | RenderSystem.Draw/AssetDatabase 순회 |
| `src/IronRose.Engine/RoseEngine/TextRenderer.cs` | 23 | `internal static readonly List<TextRenderer> _allTextRenderers = new();` | RenderSystem.Draw/AssetDatabase 순회 |
| `src/IronRose.Engine/RoseEngine/MipMeshFilter.cs` | 35 | `internal static readonly List<MipMeshFilter> _allMipMeshFilters = new();` | MipMeshSystem 순회 |

### 외부 순회 지점 (Phase D-I 범위만 열거)

**`SceneManager.AllGameObjects`** — 현재 `public static IReadOnlyList<GameObject> AllGameObjects => _allGameObjects;`. 호출자 **45+곳** (아래 요약):
- 엔진/에디터/CLI/SceneSerializer/ScriptReloadManager/Editor* 등 광범위.
- 파일 및 라인은 본 문서 끝 "부록 A" 참조.

**`MeshRenderer._allRenderers`**:
- `src/IronRose.Engine/RenderSystem.Draw.cs:31, 62, 115`
- `src/IronRose.Engine/RenderSystem.Shadow.cs:155, 219`
- `src/IronRose.Engine/Rendering/SceneViewRenderer.cs:466, 734, 795`
- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs:2185, 2212, 2314`

**`Light._allLights`**:
- `src/IronRose.Engine/RenderSystem.cs:1566`
- `src/IronRose.Engine/RenderSystem.Lighting.cs:192, 262, 375`
- `src/IronRose.Engine/RenderSystem.Shadow.cs:77, 90`

**`Canvas._allCanvases`**:
- `src/IronRose.Engine/RoseEngine/CanvasRenderer.cs:97, 115, 261, 264, 304, 307, 383, 386`
- `src/IronRose.Engine/Cli/CliCommandDispatcher.UI.cs:362, 403, 466, 2503`

**`SpriteRenderer._allSpriteRenderers`**:
- `src/IronRose.Engine/RenderSystem.cs:1620`
- `src/IronRose.Engine/RenderSystem.Draw.cs:162`
- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs:2237, 2283`

**`TextRenderer._allTextRenderers`**:
- `src/IronRose.Engine/RenderSystem.cs:1625`
- `src/IronRose.Engine/RenderSystem.Draw.cs:205`
- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs:2324`

**`MipMeshFilter._allMipMeshFilters`**:
- `src/IronRose.Engine/RoseEngine/MipMeshSystem.cs:29, 40`

### 라이프사이클 진입 지점

각 컴포넌트는 `OnAddedToGameObject()` 에서 리스트에 `Add`, `OnComponentDestroy()` 에서 `Remove`, `ClearAll()` 에서 `Clear` 하는 동일 패턴. `SceneManager` 는 `RegisterGameObject`, `RegisterBehaviour`, `UnregisterBehaviour`, `UnregisterBehaviours`, `ScheduleDestroy`, `ExecuteDestroy`, `Clear`, `ProcessDestroyQueue`, `MoveGameObjectIndex`, `Update`, `FixedUpdate` 에서 접근.

### 이미 메인 스레드에서만 호출되는 경로 확인

- `GameObject.cs:47, 84, 101` — `SceneManager.RegisterGameObject/RegisterBehaviour` 호출. 모두 GameObject 생성/AddComponent 경로이며 씬 조작은 메인 스레드 불변식(Phase B/C로 확립).
- `ScriptReloadManager.cs:514-516` — 핫리로드 경로. 메인 스레드 (EngineCore.Update 내부).
- `SceneSerializer.cs:1051, 1107` — 씬 로드 경로. `EditorSceneLoader`에서 메인 호출.
- `PrefabEditMode.cs:89, 170` — 에디터 UI 이벤트. 메인.
- CLI의 `SceneManager.Clear()` 호출(`CliCommandDispatcher.cs:1188, 2215`) — `ExecuteOnMainThread` 람다 내부이므로 메인.

따라서 **라이프사이클 경로는 이미 메인만 호출**되고 있으며, 본 Phase의 lock은 **호출자 감사/런타임 방어** 용도이다. 외부 순회 쪽(특히 AssetDatabase 내부 `ReplaceXxxInScene` 일부)이 Phase B에서 메인으로 이미 정렬됐는지도 함께 확인한다.

---

## 생성할 파일

### `src/IronRose.Contracts/ComponentRegistry.cs`

- **역할**: 정적 `List<T>` 기반 레지스트리의 lock/snapshot 패턴 공통 헬퍼. 엔진 어디에서나 이 헬퍼로 `_all*` 컬렉션을 구성하면 lock 일관성이 자동 확보된다.
- **클래스**: `public sealed class ComponentRegistry<T> where T : class` (네임스페이스: `RoseEngine`)
- **주요 멤버**:
  - `public void Register(T item)` — 이미 존재하면 no-op(`Contains` 체크). lock 내부에서 Add.
  - `public bool Unregister(T item)` — 제거 성공 시 `true`. lock 내부에서 Remove.
  - `public void Clear()` — lock 내부에서 Clear.
  - `public int Count { get; }` — lock 내부에서 `_items.Count` 반환.
  - `public bool Contains(T item)` — lock 내부에서 `_items.Contains`.
  - `public T[] Snapshot()` — lock 내부에서 `_items.ToArray()` 반환. **외부 순회 용도**. 반환 배열은 호출자가 자유롭게 수정 가능.
  - `public T[] SnapshotWhere(System.Func<T, bool> predicate)` — lock 내부에서 필터링 후 ToArray. 자주 쓰는 "필터 + 스냅샷" 패턴을 할당 1회로 제공.
  - `public void ForEachSnapshot(System.Action<T> action)` — lock 바깥에서 Snapshot 결과를 순회하는 헬퍼. 내부 구현은 `foreach (var x in Snapshot()) action(x);`. 편의 메서드.
  - `public int IndexOf(T item)` — lock 내부에서 `_items.IndexOf`.
  - `public T GetAt(int index)` — lock 내부에서 `_items[index]`.
  - `public void RemoveAt(int index)` — lock 내부에서 `_items.RemoveAt`.
  - `public void Insert(int index, T item)` — lock 내부에서 `_items.Insert`.
  - **비고**: `SceneManager.MoveGameObjectIndex` 처럼 인덱스 기반 조작이 필요한 용도에서 원자성 확보를 위해 내부 `List` 를 노출하지 않고 `IndexOf`/`RemoveAt`/`Insert`/`Count` 를 제공. `MoveGameObjectIndex` 는 **단일 lock 블록에서** 연속 호출해야 하므로 아래 `WithLock` 도 추가:
  - `public void WithLock(System.Action<System.Collections.Generic.List<T>> action)` — **내부 List 를 직접 lock 아래서 조작**해야 하는 경우에 사용. `SceneManager.MoveGameObjectIndex` 가 유일한 사용처. 람다 내부에서는 lock 이 유지됨.
- **의존**: `System.Collections.Generic.List<T>` (BCL), `System.Func`, `System.Action`. 외부 의존 없음.
- **구현 힌트**:
  - 내부 필드: `private readonly object _lock = new(); private readonly List<T> _items = new();`
  - 모든 public 멤버는 lock 내부에서 List 조작. Snapshot 은 lock 내부에서 `_items.ToArray()` 호출 후 반환.
  - `ForEachSnapshot` 은 일부러 lock 바깥에서 순회 — 순회 중 콜백이 Register/Unregister 를 호출해도 데드락/Collection modified 예외가 발생하지 않는다 (Snapshot 카피 사용).
  - `WithLock` 내부 람다는 외부에서 `Register`/`Unregister` 를 호출하면 **재진입 시도 → 단일 모니터 lock 이므로 같은 스레드에서는 허용됨** (C# `lock` 은 재진입 가능). 따라서 안전.
- **파일 헤더 주석** (기존 `ThreadGuard.cs` 와 동일 포맷):

```csharp
// ------------------------------------------------------------
// @file    ComponentRegistry.cs
// @brief   정적 컬렉션 기반 컴포넌트 레지스트리의 lock/snapshot 공통 헬퍼.
//          엔진 어디에서든 이 헬퍼로 _all* 리스트를 감싸면 라이프사이클 변경과
//          외부 순회가 동기화된다. Snapshot() 은 락 내부에서 ToArray() 카피를
//          반환하므로 호출자는 안전하게 순회할 수 있다.
// @deps    (none — BCL 만 사용)
// @exports
//   sealed class ComponentRegistry<T> where T : class
//     Register(T): void
//     Unregister(T): bool
//     Clear(): void
//     Count: int
//     Contains(T): bool
//     Snapshot(): T[]                      -- 락 내부 ToArray() 카피
//     SnapshotWhere(Func<T,bool>): T[]     -- 필터 + 카피
//     ForEachSnapshot(Action<T>): void     -- 편의: Snapshot() 후 foreach
//     IndexOf/GetAt/RemoveAt/Insert        -- 인덱스 기반 조작 (단일 원자 연산 단위)
//     WithLock(Action<List<T>>): void      -- 복합 원자 조작용 (SceneManager 루트 순서 조작 전용)
// @note    내부 List<T> 를 노출하지 않는다. 복합 조작은 WithLock 으로 원자성 확보.
// ------------------------------------------------------------
```

- **정확한 소스 (그대로 복붙 가능)**:

```csharp
// ------------------------------------------------------------
// @file    ComponentRegistry.cs
// @brief   정적 컬렉션 기반 컴포넌트 레지스트리의 lock/snapshot 공통 헬퍼.
//          엔진 어디에서든 이 헬퍼로 _all* 리스트를 감싸면 라이프사이클 변경과
//          외부 순회가 동기화된다. Snapshot() 은 락 내부에서 ToArray() 카피를
//          반환하므로 호출자는 안전하게 순회할 수 있다.
// @deps    (none — BCL 만 사용)
// @exports
//   sealed class ComponentRegistry<T> where T : class
//     Register(T): void
//     Unregister(T): bool
//     Clear(): void
//     Count: int
//     Contains(T): bool
//     Snapshot(): T[]
//     SnapshotWhere(Func<T,bool>): T[]
//     ForEachSnapshot(Action<T>): void
//     IndexOf/GetAt/RemoveAt/Insert
//     WithLock(Action<List<T>>): void
// @note    내부 List<T> 를 노출하지 않는다. 복합 조작은 WithLock 으로 원자성 확보.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace RoseEngine
{
    public sealed class ComponentRegistry<T> where T : class
    {
        private readonly object _lock = new();
        private readonly List<T> _items = new();

        public void Register(T item)
        {
            if (item == null) return;
            lock (_lock)
            {
                if (_items.Contains(item)) return;
                _items.Add(item);
            }
        }

        public bool Unregister(T item)
        {
            if (item == null) return false;
            lock (_lock)
            {
                return _items.Remove(item);
            }
        }

        public void Clear()
        {
            lock (_lock) { _items.Clear(); }
        }

        public int Count
        {
            get { lock (_lock) { return _items.Count; } }
        }

        public bool Contains(T item)
        {
            if (item == null) return false;
            lock (_lock) { return _items.Contains(item); }
        }

        public T[] Snapshot()
        {
            lock (_lock) { return _items.ToArray(); }
        }

        public T[] SnapshotWhere(Func<T, bool> predicate)
        {
            if (predicate == null) return Snapshot();
            lock (_lock)
            {
                var buf = new List<T>(_items.Count);
                foreach (var it in _items)
                    if (predicate(it)) buf.Add(it);
                return buf.ToArray();
            }
        }

        public void ForEachSnapshot(Action<T> action)
        {
            if (action == null) return;
            var snap = Snapshot();
            foreach (var it in snap) action(it);
        }

        public int IndexOf(T item)
        {
            if (item == null) return -1;
            lock (_lock) { return _items.IndexOf(item); }
        }

        public T GetAt(int index)
        {
            lock (_lock) { return _items[index]; }
        }

        public void RemoveAt(int index)
        {
            lock (_lock) { _items.RemoveAt(index); }
        }

        public void Insert(int index, T item)
        {
            if (item == null) return;
            lock (_lock) { _items.Insert(index, item); }
        }

        /// <summary>
        /// 복합 원자 조작용. 람다 내부에서는 lock 이 유지되므로
        /// 콜백은 즉시 리턴하는 짧은 조작만 수행해야 한다.
        /// 외부 Register/Unregister 재진입은 동일 스레드에서 허용된다 (C# lock 재진입).
        /// </summary>
        public void WithLock(Action<List<T>> action)
        {
            if (action == null) return;
            lock (_lock) { action(_items); }
        }
    }
}
```

---

## 수정할 파일

각 파일의 패턴은 동일하다. **기존 `internal static readonly List<T> _allXxx = new();` 필드는 이름을 유지**하되, 타입을 `ComponentRegistry<T>` 로 변경한다. 외부 호출자는 대부분 `_allXxx.Count` / `foreach (var x in _allXxx)` 만 사용하므로, `foreach` 는 `ComponentRegistry` 에 직접 붙지 않는다 → 외부 호출자를 `_allXxx.Snapshot()` 으로 전환해야 한다.

**중요 — 네이밍 결정**: 본 Phase 에서는 **필드를 `ComponentRegistry<T>` 로 바꾸고 기존 이름을 유지**한다. 외부 호출자는 `_allXxx.Count` 는 그대로 작동(Registry 에도 `Count` 존재), `foreach (var x in _allXxx)` 는 **빌드 오류**가 난다 → 모든 외부 순회를 `_allXxx.Snapshot()` 으로 수정해야 한다. 이는 컴파일러가 누락된 호출자를 즉시 잡아낸다는 장점이 있다.

### `src/IronRose.Engine/RoseEngine/MeshRenderer.cs`

- **변경 내용**:
  - `using System.Collections.Generic;` 제거(더 이상 List 사용 안 함).
  - 필드: `internal static readonly List<MeshRenderer> _allRenderers = new();` → `internal static readonly ComponentRegistry<MeshRenderer> _allRenderers = new();`
  - `OnAddedToGameObject`: `_allRenderers.Add(this);` → `ThreadGuard.DebugCheckMainThread("MeshRenderer.Register"); _allRenderers.Register(this);`
  - `OnComponentDestroy`: `_allRenderers.Remove(this);` → `ThreadGuard.DebugCheckMainThread("MeshRenderer.Unregister"); _allRenderers.Unregister(this);`
  - `ClearAll`: `_allRenderers.Clear();` 유지 (API 동일).

- **이유**: `_allRenderers` 를 Registry 로 교체하여 자동으로 lock + snapshot 지원.

### `src/IronRose.Engine/RoseEngine/Light.cs`

- **변경 내용**:
  - `using System.Collections.Generic;` 제거.
  - `internal static readonly List<Light> _allLights = new();` → `internal static readonly ComponentRegistry<Light> _allLights = new();`
  - `OnAddedToGameObject` / `OnComponentDestroy` / `ClearAll` 를 MeshRenderer 와 동일 패턴으로 전환 (`ThreadGuard.DebugCheckMainThread("Light.Register")` 등).

### `src/IronRose.Engine/RoseEngine/Canvas.cs`

- **변경 내용**:
  - `using System.Collections.Generic;` 제거 (다른 List 사용이 없으므로. 실제로 이 파일에는 `System.Collections.Generic` 외 다른 Generic 쓰임이 없다).
  - `internal static readonly List<Canvas> _allCanvases = new();` → `internal static readonly ComponentRegistry<Canvas> _allCanvases = new();`
  - `OnAddedToGameObject` 내부 분기 유지. `_allCanvases.Add(this);` → `ThreadGuard.DebugCheckMainThread("Canvas.Register"); _allCanvases.Register(this);`
  - `OnComponentDestroy`: `_allCanvases.Remove(this);` → `ThreadGuard.DebugCheckMainThread("Canvas.Unregister"); _allCanvases.Unregister(this);`
  - `ClearAll` 유지.

### `src/IronRose.Engine/RoseEngine/SpriteRenderer.cs`

- **변경 내용**: Light.cs 와 동일 패턴.
  - `using System.Collections.Generic;` 제거.
  - 필드 → `ComponentRegistry<SpriteRenderer>`.
  - `ThreadGuard.DebugCheckMainThread("SpriteRenderer.Register"/"Unregister")` 추가.

### `src/IronRose.Engine/RoseEngine/TextRenderer.cs`

- **변경 내용**: 동일 패턴. **주의**: 이 파일은 `BuildTextMesh` 내부에서 `new List<Vertex>()` / `new List<uint>()` 를 사용하므로 `using System.Collections.Generic;` 는 **유지**해야 한다.
  - 필드 → `ComponentRegistry<TextRenderer>`.
  - `ThreadGuard.DebugCheckMainThread("TextRenderer.Register"/"Unregister")` 추가.

### `src/IronRose.Engine/RoseEngine/MipMeshFilter.cs`

- **변경 내용**:
  - `using System.Collections.Generic;` 제거.
  - 필드 → `ComponentRegistry<MipMeshFilter>`.
  - `ThreadGuard.DebugCheckMainThread("MipMeshFilter.Register"/"Unregister")` 추가.

### `src/IronRose.Engine/RoseEngine/SceneManager.cs`

- **변경 내용**: 가장 복잡. 단계별:

1. `using System.Collections.Generic;` 는 `List<MonoBehaviour>` 를 `pending` 변수 등에서 쓰므로 **유지**.

2. 필드 선언부 (line 59-64) 교체:

```csharp
// --- Core registries ---
private static readonly ComponentRegistry<MonoBehaviour> _behaviours = new();
private static readonly ComponentRegistry<MonoBehaviour> _pendingStart = new();
private static readonly ComponentRegistry<GameObject> _allGameObjects = new();

// --- Deferred destroy ---
private static readonly object _destroyQueueLock = new();
private static readonly List<DestroyEntry> _destroyQueue = new();
```

**주의**: `_destroyQueue` 는 `DestroyEntry` 가 struct 이므로 `ComponentRegistry<T> where T : class` 제약에 맞지 않는다. 따라서 **`_destroyQueue` 만 전용 lock 오브젝트를 추가**하고 기존 List 를 유지한다.

3. `AllGameObjects` public API 유지 필요:
```csharp
public static IReadOnlyList<GameObject> AllGameObjects
{
    get
    {
        // Snapshot array 를 IReadOnlyList 로 반환.
        // 호출자는 순회 중 Register/Unregister 되어도 영향받지 않는다.
        return _allGameObjects.Snapshot();
    }
}
```

**주의**: 기존에 `AllGameObjects` 가 **호출당 동일 List 참조**를 반환하던 것을 **매 호출마다 새 배열**을 반환하도록 변경한다. 호출자 중 `Count` 조회 후 `for` 루프를 돌리는 패턴은 매번 ToArray 가 발생하므로 **한 번 변수에 담아 사용**하도록 호출자 쪽 규약을 유지해야 한다(대부분 이미 그렇게 쓰고 있음 — 아래 부록 A 참조).

4. `MoveGameObjectIndex` (line 69-91) 를 `WithLock` 기반으로 전환:

```csharp
public static void MoveGameObjectIndex(GameObject go, int newRootIndex)
{
    ThreadGuard.DebugCheckMainThread("SceneManager.MoveGameObjectIndex");
    _allGameObjects.WithLock(list =>
    {
        int old = list.IndexOf(go);
        if (old < 0) return;
        list.RemoveAt(old);

        if (newRootIndex < 0) newRootIndex = 0;
        int insertAt = list.Count;
        int rootCount = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].transform.parent != null) continue;
            if (rootCount == newRootIndex) { insertAt = i; break; }
            rootCount++;
        }

        list.Insert(insertAt, go);
    });
}
```

5. `RegisterGameObject` (line 97):

```csharp
public static void RegisterGameObject(GameObject go)
{
    ThreadGuard.DebugCheckMainThread("SceneManager.RegisterGameObject");
    _allGameObjects.Register(go);
}
```

6. `UnregisterBehaviours(GameObject go)` (line 103-113) 은 `_behaviours` 를 역방향 순회하며 조건부 Remove 한다 → `WithLock` 사용:

```csharp
public static void UnregisterBehaviours(GameObject go)
{
    ThreadGuard.DebugCheckMainThread("SceneManager.UnregisterBehaviours");
    _behaviours.WithLock(list =>
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].gameObject == go)
            {
                _pendingStart.Unregister(list[i]);
                list.RemoveAt(i);
            }
        }
    });
}
```

7. `RegisterBehaviour` (line 115-144):

```csharp
public static void RegisterBehaviour(MonoBehaviour behaviour)
{
    ThreadGuard.DebugCheckMainThread("SceneManager.RegisterBehaviour");
    if (_behaviours.Contains(behaviour)) return;
    if (behaviour.gameObject != null && behaviour.gameObject._isEditorInternal) return;

    _behaviours.Register(behaviour);

    try { behaviour.Awake(); }
    catch (Exception ex) { Debug.LogError($"Exception in Awake() of {behaviour.GetType().Name}: {ex.Message}"); }

    behaviour._hasAwoken = true;

    if (behaviour.enabled && behaviour.gameObject.activeSelf)
    {
        try { behaviour.OnEnable(); }
        catch (Exception ex) { Debug.LogError($"Exception in OnEnable() of {behaviour.GetType().Name}: {ex.Message}"); }
    }

    _pendingStart.Register(behaviour);
}
```

8. `UnregisterBehaviour` (internal, line 149-155):

```csharp
internal static void UnregisterBehaviour(MonoBehaviour behaviour)
{
    ThreadGuard.DebugCheckMainThread("SceneManager.UnregisterBehaviour");
    CoroutineScheduler.StopAllCoroutines(behaviour);
    InvokeScheduler.CancelAll(behaviour);
    _behaviours.Unregister(behaviour);
    _pendingStart.Unregister(behaviour);
}
```

9. `FixedUpdate` (line 161-173): 순회를 스냅샷 기반으로 변경:

```csharp
public static void FixedUpdate(float fixedDeltaTime)
{
    var snap = _behaviours.Snapshot();
    for (int i = 0; i < snap.Length; i++)
    {
        var b = snap[i];
        if (!IsActive(b)) continue;
        try { b.FixedUpdate(); }
        catch (Exception ex) { Debug.LogError($"Exception in FixedUpdate() of {b.GetType().Name}: {ex.Message}"); }
    }
}
```

10. `Update` (line 179-237): pendingStart 처리 + 3개 순회를 모두 스냅샷 기반으로. `_pendingStart.Snapshot()` 후 `_pendingStart.Clear()` 시 **snapshot 후 Clear 까지의 사이에 새로 추가된 pending 을 잃지 않도록** `WithLock` 으로 atomic 하게 drain 한다:

```csharp
public static void Update(float deltaTime)
{
    Time.unscaledDeltaTime = deltaTime;
    float clampedDt = deltaTime > Time.maximumDeltaTime ? Time.maximumDeltaTime : deltaTime;
    Time.deltaTime = clampedDt * Time.timeScale;
    Time.time += Time.deltaTime;

    // 1. Process pending Start() calls (atomic drain)
    MonoBehaviour[]? pending = null;
    _pendingStart.WithLock(list =>
    {
        if (list.Count == 0) return;
        pending = list.ToArray();
        list.Clear();
    });
    if (pending != null)
    {
        foreach (var b in pending)
        {
            if (!IsActive(b)) continue;
            try { b.Start(); }
            catch (Exception ex) { Debug.LogError($"Exception in Start() of {b.GetType().Name}: {ex.Message}"); }
        }
    }

    // 2. Invokes
    InvokeScheduler.Process(Time.deltaTime);

    // 3. Update
    var updSnap = _behaviours.Snapshot();
    for (int i = 0; i < updSnap.Length; i++)
    {
        var b = updSnap[i];
        if (!IsActive(b)) continue;
        try { b.Update(); }
        catch (Exception ex) { Debug.LogError($"Exception in Update() of {b.GetType().Name}: {ex.Message}"); }
    }

    // 4. Coroutines
    CoroutineScheduler.Process(Time.deltaTime);

    // 5. LateUpdate
    var lateSnap = _behaviours.Snapshot();
    for (int i = 0; i < lateSnap.Length; i++)
    {
        var b = lateSnap[i];
        if (!IsActive(b)) continue;
        try { b.LateUpdate(); }
        catch (Exception ex) { Debug.LogError($"Exception in LateUpdate() of {b.GetType().Name}: {ex.Message}"); }
    }

    // 6. Deferred destroy
    ProcessDestroyQueue(Time.deltaTime);

    Time.frameCount++;
}
```

11. `ScheduleDestroy` (line 280), `ProcessDestroyQueue` (line 290), `ExecuteDestroy` (line 332): `_destroyQueue` 는 struct 리스트이므로 `lock (_destroyQueueLock)` 으로 보호:

```csharp
internal static void ScheduleDestroy(Object obj, float delay)
{
    ThreadGuard.DebugCheckMainThread("SceneManager.ScheduleDestroy");
    lock (_destroyQueueLock)
    {
        _destroyQueue.Add(new DestroyEntry { target = obj, timer = delay });
    }
}

private static void ProcessDestroyQueue(float deltaTime)
{
    // Snapshot under lock, process outside lock (ExecuteDestroy 가 Register/Unregister 호출 가능)
    DestroyEntry[] snap;
    lock (_destroyQueueLock)
    {
        if (_destroyQueue.Count == 0) return;
        // timer 갱신은 원본에 해야 함 → 직접 루프
        for (int i = _destroyQueue.Count - 1; i >= 0; i--)
        {
            var entry = _destroyQueue[i];
            entry.timer -= deltaTime;
            if (entry.timer <= 0f)
            {
                // 처리는 lock 밖에서
            }
            else
            {
                _destroyQueue[i] = entry;
            }
        }
        // expired 항목을 별도 배열로 추출
        var expired = new List<DestroyEntry>();
        for (int i = _destroyQueue.Count - 1; i >= 0; i--)
        {
            if (_destroyQueue[i].timer <= 0f)
            {
                expired.Add(_destroyQueue[i]);
                _destroyQueue.RemoveAt(i);
            }
        }
        snap = expired.ToArray();
    }
    foreach (var entry in snap) ExecuteDestroy(entry.target);
}
```

**중요**: 위 로직이 복잡하므로 **더 간단한 대안**을 권장:

```csharp
private static void ProcessDestroyQueue(float deltaTime)
{
    // ScheduleDestroy 는 메인에서만 호출되고 ProcessDestroyQueue 도 메인에서만 호출.
    // 유일한 관심사는 "ExecuteDestroy 내부에서 ScheduleDestroy 가 재호출될 때" 의 재진입.
    // lock 재진입은 C# 에서 허용되므로 단순히 전체를 lock 으로 감싸도 괜찮다.
    lock (_destroyQueueLock)
    {
        for (int i = _destroyQueue.Count - 1; i >= 0; i--)
        {
            var entry = _destroyQueue[i];
            entry.timer -= deltaTime;
            if (entry.timer <= 0f)
            {
                ExecuteDestroy(entry.target);
                _destroyQueue.RemoveAt(i);
            }
            else
            {
                _destroyQueue[i] = entry;
            }
        }
    }
}
```

**권장**: 위 단순 버전 (lock 재진입 허용). `ExecuteDestroy` 내부가 다시 `ScheduleDestroy` 를 호출해도 모니터 lock 은 같은 스레드 재진입을 허용하므로 데드락 없음. 그리고 현재 구조상 메인 스레드에서만 호출되므로 경합도 없음.

12. `ExecuteDestroy` (line 332-353):

```csharp
private static void ExecuteDestroy(Object obj)
{
    if (obj._isDestroyed) return;

    if (obj is GameObject go)
    {
        // 자식도 파괴 — 재귀
        for (int i = go.transform.childCount - 1; i >= 0; i--)
            ExecuteDestroy(go.transform.GetChild(i).gameObject);

        foreach (var comp in go._components)
            DestroyComponent(comp);

        go.transform.SetParent(null, false);
        _allGameObjects.Unregister(go);   // ← List.Remove → Registry.Unregister
        go._isDestroyed = true;
    }
    else if (obj is Component comp)
    {
        DestroyComponent(comp);
        comp.gameObject.RemoveComponent(comp);
    }
}
```

13. `DestroyComponent` (line 309-330) 내부 `_behaviours.Remove(mb); _pendingStart.Remove(mb);` → Registry API:

```csharp
private static void DestroyComponent(Component comp)
{
    if (comp is MonoBehaviour mb && !mb._isDestroyed)
    {
        try
        {
            if (mb._hasAwoken && mb.enabled) mb.OnDisable();
            mb.OnDestroy();
        }
        catch (Exception ex) { Debug.LogError($"Exception in OnDestroy() of {mb.GetType().Name}: {ex.Message}"); }
        CoroutineScheduler.StopAllCoroutines(mb);
        InvokeScheduler.CancelAll(mb);
        _behaviours.Unregister(mb);
        _pendingStart.Unregister(mb);
    }

    comp.OnComponentDestroy();
    comp._isDestroyed = true;
}
```

14. `Clear` (line 359-397): `_behaviours` 순회를 Snapshot 기반으로:

```csharp
public static void Clear()
{
    ThreadGuard.DebugCheckMainThread("SceneManager.Clear");
    var snap = _behaviours.Snapshot();
    foreach (var b in snap)
    {
        try
        {
            if (b._hasAwoken && b.enabled) b.OnDisable();
            b.OnDestroy();
        }
        catch (Exception ex) { Debug.LogError($"Exception in OnDestroy() of {b.GetType().Name}: {ex.Message}"); }
    }

    _behaviours.Clear();
    _pendingStart.Clear();
    _allGameObjects.Clear();
    CoroutineScheduler.Clear();
    InvokeScheduler.Clear();
    lock (_destroyQueueLock) { _destroyQueue.Clear(); }

    MeshRenderer.ClearAll();
    SpriteRenderer.ClearAll();
    TextRenderer.ClearAll();
    UIText.ClearAll();
    UIInputField.ClearAll();
    Light.ClearAll();
    Camera.ClearMain();
    Canvas.ClearAll();
    CanvasRenderer.ClearTextureCache();

    Collider.ClearAll();
    Collider2D.ClearAll();
    Rigidbody.ClearAll();
    Rigidbody2D.ClearAll();

    IronRose.Engine.PhysicsManager.Instance?.Reset();
}
```

- **의존**: `RoseEngine.ThreadGuard` (같은 네임스페이스), `RoseEngine.ComponentRegistry<T>` (같은 네임스페이스).
- **이유**: SceneManager 는 엔진의 중심축이며 `_allGameObjects` 가 45+곳에서 순회됨. lock + snapshot 전환이 가장 중요.

---

## 외부 호출자 수정 (Snapshot 기반으로 전환)

아래 모든 호출자에서 **`foreach (var x in _allXxx)` 가 `ComponentRegistry` 에 대한 foreach 가 아니므로 컴파일 오류가 발생**한다. 각 호출자를 스냅샷으로 변환한다. 권장 패턴:

**변경 전**:
```csharp
foreach (var r in MeshRenderer._allRenderers) { /* body */ }
```

**변경 후 (패턴 A — 간단)**:
```csharp
var snap = MeshRenderer._allRenderers.Snapshot();
foreach (var r in snap) { /* body */ }
```

**변경 후 (패턴 B — ForEachSnapshot)**:
```csharp
MeshRenderer._allRenderers.ForEachSnapshot(r => { /* body */ });
```

**루프 본문에 `continue` 가 있거나 분기가 복잡하면 패턴 A 권장**. `ForEachSnapshot` 은 단순 본문에만 사용.

### `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs`

| 라인 | 현재 | 변경 |
|------|------|------|
| 865 | `foreach (var vol in PostProcessVolume._allVolumes)` | **Phase D-II 범위** — D-I 에서는 건드리지 않음 (PostProcessVolume 은 D-II 에서 전환). Phase B 에서 이미 `.ToArray()` 임시 스냅샷이 들어갔을 수 있으니 **현 상태 확인 필수**. 아직 `foreach (var vol in PostProcessVolume._allVolumes)` 라면 D-I 에서도 `.ToArray()` 임시 스냅샷 추가 유지. |
| 2185 | `foreach (var renderer in MeshRenderer._allRenderers)` | `var rs = MeshRenderer._allRenderers.Snapshot(); foreach (var renderer in rs) { ... }` |
| 2212 | 동일 | 동일 |
| 2237 | `foreach (var sr in SpriteRenderer._allSpriteRenderers)` | `var srs = SpriteRenderer._allSpriteRenderers.Snapshot(); foreach (...)` |
| 2283 | 동일 | 동일 |
| 2314 | `foreach (var renderer in MeshRenderer._allRenderers)` | `var rs = MeshRenderer._allRenderers.Snapshot(); foreach (...)` |
| 2324 | `foreach (var tr in TextRenderer._allTextRenderers)` | Snapshot 기반 |
| 2332 | `foreach (var ut in UIText._allUITexts)` | **Phase D-II 범위** — 임시 `.ToArray()` 유지 |
| 2337 | `foreach (var uif in UIInputField._allUIInputFields)` | **Phase D-II 범위** — 임시 `.ToArray()` 유지 |

**Phase B 머지 상태 확인**: `AssetDatabase.cs` 에는 Phase B 에서 이미 `MeshRenderer._allRenderers.ToArray()` 같은 임시 스냅샷이 들어간 곳이 있을 수 있다. 만약 이미 `.ToArray()` 가 있으면, 이를 **Snapshot()** 호출로 바꾸면 동등 효과 + lock 보호. **코더는 먼저 실제 파일을 열어 현재 코드가 `foreach (var x in MeshRenderer._allRenderers)` 인지 `foreach (var x in MeshRenderer._allRenderers.ToArray())` 인지 확인**하고, 어느 쪽이든 최종 결과가 `foreach (var x in MeshRenderer._allRenderers.Snapshot())` 이 되도록 수정한다.

### `src/IronRose.Engine/RenderSystem.cs`

| 라인 | 현재 | 변경 |
|------|------|------|
| 1566 | `foreach (var light in Light._allLights)` | `var lights = Light._allLights.Snapshot(); foreach (...)` |
| 1620 | `if (_spritePipeline != null && SpriteRenderer._allSpriteRenderers.Count > 0)` | `.Count` 는 Registry 에도 동일하게 동작 → **변경 불필요** |
| 1625 | `if (_spritePipeline != null && TextRenderer._allTextRenderers.Count > 0)` | **변경 불필요** |

### `src/IronRose.Engine/RenderSystem.Lighting.cs`

| 라인 | 현재 | 변경 |
|------|------|------|
| 192 | `foreach (var light in Light._allLights)` | Snapshot |
| 262 | 동일 | Snapshot |
| 375 | 동일 | Snapshot |

### `src/IronRose.Engine/RenderSystem.Shadow.cs`

| 라인 | 현재 | 변경 |
|------|------|------|
| 77 | `foreach (var light in Light._allLights)` | Snapshot |
| 90 | 동일 | Snapshot |
| 155 | `foreach (var renderer in MeshRenderer._allRenderers)` | Snapshot |
| 219 | 동일 | Snapshot |

### `src/IronRose.Engine/RenderSystem.Draw.cs`

| 라인 | 현재 | 변경 |
|------|------|------|
| 31 | `foreach (var renderer in MeshRenderer._allRenderers)` | Snapshot |
| 62 | 동일 | Snapshot |
| 115 | 동일 | Snapshot |
| 162 | `var active = SpriteRenderer._allSpriteRenderers` (LINQ 체이닝) | `var active = SpriteRenderer._allSpriteRenderers.Snapshot()` (이후 LINQ 그대로 유지) |
| 205 | `var active = TextRenderer._allTextRenderers` (LINQ 체이닝) | `.Snapshot()` 추가 |

**LINQ 파이프 주의**: `.Snapshot()` 은 `T[]` 를 반환하므로 `.Where(...)` 등 LINQ 가 그대로 이어진다.

### `src/IronRose.Engine/Rendering/SceneViewRenderer.cs`

| 라인 | 현재 | 변경 |
|------|------|------|
| 466 | `foreach (var renderer in MeshRenderer._allRenderers)` | Snapshot |
| 734 | `foreach (var r in MeshRenderer._allRenderers)` | Snapshot |
| 795 | `foreach (var renderer in MeshRenderer._allRenderers)` | Snapshot |

### `src/IronRose.Engine/RoseEngine/CanvasRenderer.cs`

| 라인 | 현재 | 변경 |
|------|------|------|
| 97 | `if (Canvas._allCanvases.Count == 0)` | **변경 불필요** (`.Count` 지원) |
| 115 | `foreach (var c in Canvas._allCanvases)` | Snapshot |
| 261 | `.Count == 0` 체크 | **변경 불필요** |
| 264 | `foreach (var c in Canvas._allCanvases)` | Snapshot |
| 304 | `.Count == 0` 체크 | **변경 불필요** |
| 307 | `foreach (var c in Canvas._allCanvases)` | Snapshot |
| 383 | `.Count == 0` 체크 | **변경 불필요** |
| 386 | `foreach (var c in Canvas._allCanvases)` | Snapshot |

### `src/IronRose.Engine/Cli/CliCommandDispatcher.UI.cs`

| 라인 | 현재 | 변경 |
|------|------|------|
| 362 | `foreach (var canvas in Canvas._allCanvases)` (in ExecuteOnMainThread 람다) | Snapshot |
| 403 | 동일 | Snapshot |
| 466 | 동일 | Snapshot |
| 2503 | 동일 | Snapshot |

**CLI 감사 결과**: 모든 Canvas iteration 이 `ExecuteOnMainThread` 람다 내부에 있다 → 메인 스레드 안전. Snapshot 은 이중 안전장치로만 추가.

### `src/IronRose.Engine/RoseEngine/MipMeshSystem.cs`

| 라인 | 현재 | 변경 |
|------|------|------|
| 29 | `foreach (var mipFilter in MipMeshFilter._allMipMeshFilters)` | Snapshot |
| 40 | 동일 | Snapshot |

### `src/IronRose.Engine/Physics/PhysicsManager.cs`

- line 61 에 `$"...colliders={Collider._allColliders.Count} rigidbodies={Rigidbody._rigidbodies.Count}"` 가 있는데, **Collider/Rigidbody 는 Phase D-II 에서 전환**되므로 D-I 에서는 건드리지 않는다. D-II 가 머지되면 이 `.Count` 는 Registry 의 `Count` 로 여전히 동작한다.

### `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs:2293` 의 `SceneManager.AllGameObjects` 순회

`SceneManager.AllGameObjects` 는 `IReadOnlyList<GameObject>` 를 유지하되 내부적으로 Snapshot 배열을 반환한다 (위 SceneManager 수정 참조). 따라서 **호출자 코드는 변경 불필요**. 단 **매 호출 할당**이 발생하므로, **같은 함수 내에서 여러 번 `AllGameObjects` 를 호출하면 매번 새 배열**이라는 점은 주의해야 한다. 호출자 대부분은 이미 `var allGOs = SceneManager.AllGameObjects;` 로 한번 받아 쓰므로 성능 영향 미미.

**성능 검토 대상 호출자** (매 프레임 호출되는 경우):
- `src/IronRose.Engine/Editor/SceneView/GizmoCallbackRunner.cs:13`
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiHierarchyPanel.cs:95, 123, 1122`
- `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs` (여러 곳)
- `src/IronRose.Engine/RoseEngine/GameObject.cs:226, 236, 247` (`Find`, `FindWithTag` 등)
- `src/IronRose.Engine/RoseEngine/Transform.cs:115`
- `src/IronRose.Engine/RoseEngine/Object.cs:88, 107` (`FindObjectOfType` 등)

**권장**: 매 프레임 호출되는 경로는 할당 부담이 있을 수 있으나, 씬 규모(수백~수천 GO)에서는 ToArray 할당이 frame budget 내에서 허용 가능. **Phase D-I 에서는 측정 없이 도입**하고, 성능 이슈가 발견되면 **Phase E 이후에** `AllGameObjectsReadOnly` 같은 lock-free 순회 API 를 추가하는 방식으로 후속 조치.

---

## NuGet 패키지 (해당 시)

- 없음 (`IronRose.Contracts` 는 BCL 만 사용).

---

## 검증 기준

- [ ] `dotnet build` 성공 — 누락된 호출자가 없어야 함. 컴파일 오류가 나면 그 파일의 `_allXxx` 순회 부분을 `.Snapshot()` 으로 수정.
- [ ] 빈 씬 + 기본 큐브로 에디터 기동 → 렌더 정상(큐브 가시성).
- [ ] Light 추가/삭제 반복 → 그림자/조명 즉시 반영.
- [ ] UI Canvas 추가/삭제 반복 → UI 렌더 정상.
- [ ] 씬 로드/저장/클리어 반복 (10회) → 누락/크래시 없음.
- [ ] Debug 빌드에서 CLI 백그라운드가 `SceneManager.AllGameObjects` 에 우회 접근하면 `ThreadGuard` 경고 로그 출력 확인 (실제로는 모두 ExecuteOnMainThread 래핑됨을 확인하는 용도).
- [ ] 플레이모드 진입 → Update/FixedUpdate 루프가 에러 없이 동작.
- [ ] `dotnet build` Release 빌드에서 `DebugCheckMainThread` 호출부 IL 이 제거되는지 확인(`[Conditional("DEBUG")]`).

### 스모크 테스트 (마스터 계획 §Phase D 에서 발췌)

1. 씬 로드/저장 반복 + CLI `scene.list` 동시 실행.
2. Reimport 중 `delete.gameobject` 호출.
3. Prefab 인스턴스화 반복 (100회).

---

## 참고

- **성능**: `ComponentRegistry.Snapshot()` 은 매 호출마다 `T[]` 할당 발생. 매 프레임 수백 호출되는 경로(RenderSystem.Draw 등)에서는 할당 부담이 누적될 수 있다. 현재 씬 규모(~수백 엔티티) 에선 문제 없을 것으로 예상되나, 후속 측정 필요.
- **재진입**: C# `lock` 은 같은 스레드 재진입 허용. `ComponentRegistry.WithLock` 내부에서 외부 `Register` 호출은 허용된다 (`ExecuteDestroy` → `Unregister` → lock 재진입).
- **Phase D-II 와의 경계**: 본 Phase 는 씬/렌더 경로만 다룬다. Physics/UI 컴포넌트, PostProcessVolume 은 D-II 범위이며, **D-I 머지 후에도 Phase B 에서 이미 들어간 `.ToArray()` 임시 스냅샷이 유지**되므로 안전하게 돌아간다.
- **Phase D-III 와의 경계**: `RoseMetadata.OnSaved` 이벤트 동기화는 D-III 범위. 본 Phase 는 touch 하지 않는다.
- **미결 사항**: `AllGameObjects` 를 `IReadOnlyList<GameObject>` 로 유지할지 `ReadOnlySpan<GameObject>` 또는 `T[]` 으로 바꿀지는 본 Phase 범위 밖. 호출자 45+곳에 파급이 크므로 **기존 타입 유지**.

---

## 부록 A: `SceneManager.AllGameObjects` 호출자 목록 (본 Phase 에서 코드 수정 불필요)

| 파일 | 라인 |
|------|------|
| `src/IronRose.Engine/Editor/PrefabEditMode.cs` | 116 |
| `src/IronRose.Engine/Editor/SceneView/RectSelectionTool.cs` | 81 |
| `src/IronRose.Engine/Editor/EditorSelection.cs` | 30 |
| `src/IronRose.Engine/Editor/SceneView/GizmoCallbackRunner.cs` | 13 |
| `src/IronRose.Engine/Editor/SceneSnapshot.cs` | 20 |
| `src/IronRose.Engine/ScriptReloadManager.cs` | 389, 414, 468 |
| `src/IronRose.Engine/Editor/SceneSerializer.cs` | 81, 1790 |
| `src/IronRose.Engine/Editor/EditorCommand.cs` | 38, 101 |
| `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` | 2293 |
| `src/IronRose.Engine/Editor/Undo/UndoUtility.cs` | 18, 52, 67 |
| `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs` | 1818, 1835, 1867, 2015, 2094, 2115, 2181 |
| `src/IronRose.Engine/RoseEngine/PrefabUtility.cs` | 327 |
| `src/IronRose.Engine/Editor/EditorClipboard.cs` | 41, 58, 83, 111, 270 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs` | 319, 2089, 4734, 4805 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiPropertyWindow.cs` | 105 |
| `src/IronRose.Engine/Cli/CliCommandDispatcher.UI.cs` | 426 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiHierarchyPanel.cs` | 95, 123, 1122 |
| `src/IronRose.Engine/RoseEngine/Object.cs` | 88, 107 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectPanel.cs` | 982 |
| `src/IronRose.Engine/RoseEngine/Transform.cs` | 115 |
| `src/IronRose.Engine/RoseEngine/GameObject.cs` | 226, 236, 247 |
| `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` | 185, 194, 291, 1174, 2167, 2828, 2839 |

**이 호출자들은 모두 `public static IReadOnlyList<GameObject> AllGameObjects` 를 통해 접근**하며, `AllGameObjects` 의 내부 구현이 `_allGameObjects.Snapshot()` 를 반환하도록 변경되어 자동으로 스냅샷 기반 동작. **코드 변경 불필요**.

**CLI 감사**: `CliCommandDispatcher.cs:185, 194, 291, 1174, 2167` 은 모두 `ExecuteOnMainThread` 람다 내부이고, `2828, 2839` 는 `FindGameObject` private helper (호출자가 메인 스레드에서만 호출). 안전.
