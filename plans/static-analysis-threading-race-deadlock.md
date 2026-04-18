---
name: 멀티스레드 경합/데드락 정적 분석 보고서
type: analysis
date: 2026-04-18
scope: IronRose.Engine / IronRose.Rendering / IronRose.Physics / IronRose.AssetPipeline / IronRose.Editor
status: read-only (수정 없음)
---

# IronRose 멀티스레드 경합 조건 & 데드락 정적 분석 보고서

> 작성일: 2026-04-18
> 범위: `src/` 전 영역의 C# 코드 정적 분석
> 목적: 경합 조건 / 데드락 위험 지점 **식별만** 수행 (수정은 포함하지 않음)

---

## 0. 개요

IronRose는 다음과 같은 다중 스레드 경로를 가진다:

| 스레드 | 주체 | 주요 책임 |
|--------|------|-----------|
| 메인 | `EngineCore.Update` | 씬 업데이트, 렌더 커맨드 발행, 에디터 UI |
| CLI 서버 | `CliPipeServer._serverThread` (Background Thread) | 파이프 listen, `Dispatch` 실행 |
| 워커(Task) | `Task.Run` 풀 | `ReimportAsync` 백그라운드 임포트, `AssetWarmupManager` 메시 캐시, AI 이미지 생성, BC6H 인코딩 등 |
| 렌더 | Veldrid/Vulkan 내부 | (현재는 메인 스레드에서 드라이브됨) |
| FileSystemWatcher | .NET ThreadPool | `_watcher.Changed/Created/Deleted/Renamed` 콜백 |
| Parallel.For | TPL 파티션 스레드 | `Animator.Update`의 target evaluate |

이 보고서는 해당 경계에서 **공유 상태가 동기화 없이 접근되거나, 락을 잡은 채로 블로킹 호출을 하거나, 락 순서가 역전될 수 있는 지점**을 심각도 순으로 정리한다.

## 1. 심각도 요약

| # | 위치 | 문제 | 유형 | 심각도 |
|---|------|------|------|--------|
| C1 | [AssetDatabase.cs:1003-1227](src/IronRose.Engine/AssetPipeline/AssetDatabase.cs#L1003-L1227) | `_reimport*` 필드를 Task.Run이 쓰고 메인이 읽음 — 메모리 배리어 없음 | Race | Critical |
| C2 | [AssetDatabase.cs:746-883](src/IronRose.Engine/AssetPipeline/AssetDatabase.cs#L746-L883) | `_loadedAssets` 딕셔너리를 백그라운드 Task와 메인이 동시에 쓰기 | Race | Critical |
| C3 | [CliCommandDispatcher.cs:2730-2737](src/IronRose.Engine/Cli/CliCommandDispatcher.cs#L2730-L2737) | `ExecuteOnMainThread`가 5초 블로킹 대기 — 메인 스레드 stall과 맞물리면 파이프 drain 불가 | Deadlock | Critical |
| C4 | [RoseMetadata.cs:68,102](src/IronRose.Engine/AssetPipeline/RoseMetadata.cs#L68) | `static event OnSaved`에 동기화 없이 구독/해제/Invoke | Race | Critical |
| C5 | [SceneManager.cs:59-100](src/IronRose.Engine/RoseEngine/SceneManager.cs#L59-L100) / [CliCommandDispatcher.cs:145-176](src/IronRose.Engine/Cli/CliCommandDispatcher.cs#L145-L176) | `AllGameObjects`를 CLI 백그라운드가 직접 enum | Race | Critical |
| C6 | [CliPipeServer.cs:62-118](src/IronRose.Engine/Cli/CliPipeServer.cs#L62-L118) | `Stop()`이 `_cts.Dispose()` 후 ServerLoop이 여전히 `_cts.Token` 접근 가능 | Race | High |
| H1 | [CliPipeServer.cs:33,46,78,118](src/IronRose.Engine/Cli/CliPipeServer.cs#L33) | `IsRunning` 플래그를 세 경로에서 동기화 없이 읽기/쓰기 | Race | High |
| H2 | [AssetDatabase.cs:1689-1802](src/IronRose.Engine/AssetPipeline/AssetDatabase.cs#L1689-L1802) | FileSystemWatcher 이벤트 + 디바운스 큐의 타임스탬프 판정에서 큐 재투입 윈도우 | Race | High |
| H3 | [Animator.cs:87-181](src/IronRose.Engine/RoseEngine/Animator.cs#L87-L181) | `_targets`에 Volatile은 있으나 `SampleAt`/`CapturePreviewSnapshot` 내부 루프는 비보호 | Race | High |
| H4 | [PostProcessVolume.cs:12](src/IronRose.Engine/RoseEngine/PostProcessVolume.cs#L12) ∕ `_all*` 리스트 다수 | `_allVolumes`, `_allRenderers`, `_allLights`, `_allCanvases`, `_allTextRenderers`, `_allColliders` … 라이프사이클 변경과 반복이 동기화 없이 섞임 | Race | High |
| H5 | [AssetDatabase.cs:1865](src/IronRose.Engine/AssetPipeline/AssetDatabase.cs#L1865) | `OnRoseMetadataSaved` 내에서 `Reimport()` 호출 → FileSystemWatcher 스레드에서 GPU/씬 접근 위험 | Race | High |
| H6 | Veldrid `GraphicsDevice` 사용 — [GpuTextureCompressor.cs](src/IronRose.Engine/AssetPipeline/GpuTextureCompressor.cs) / [Bc6hEncoder.cs](src/IronRose.Engine/AssetPipeline/Bc6hEncoder.cs) | GPU 리소스를 백그라운드 Task에서 건드릴 경우 드라이버 수준 동시성 위반 가능 | API Violation | High |
| M1 | [PlayerPrefs.cs:184-304](src/IronRose.Engine/RoseEngine/PlayerPrefs.cs#L184-L304) | `lock (_lock)` 상태에서 파일 I/O — long-hold + 예외 시 긴 정체 | Deadlock Risk | Medium |
| M2 | [Texture2D.cs:207-215](src/IronRose.Engine/RoseEngine/Texture2D.cs#L207-L215) | `DefaultNormal`/`DefaultMRO`의 `??=` lazy init이 race-free하지 않음 | Race | Medium |
| M3 | [PhysicsWorld3D.cs](src/IronRose.Physics/PhysicsWorld3D.cs) | `_currentContacts` lock은 있으나 `Flush`가 `RecordContact`와 중첩된다는 가정이 불명확 | Race | Medium |
| M4 | [EditorAssetSelection.cs:33-150](src/IronRose.Engine/Editor/EditorAssetSelection.cs#L33-L150) | "메인 전용"이라 명시돼 있으나 CLI가 간접 호출 가능 | Race | Medium |
| L1 | [AiImageGenerationService.cs:64-113](src/IronRose.Engine/Editor/AiImageGenerationService.cs#L64-L113) | lock 정상 사용 — **안전** (참조 확인만) | OK | — |
| L2 | [CliLogBuffer.cs](src/IronRose.Engine/Cli/CliLogBuffer.cs) | lock 정상 사용 — **안전** (경합 contention만 주의) | OK | — |

---

## 2. Critical 위험 지점 상세

### C1. AssetDatabase.ReimportAsync 의 `_reimport*` 필드

**파일**: [AssetDatabase.cs:982-1075](src/IronRose.Engine/AssetPipeline/AssetDatabase.cs#L982-L1075), [AssetDatabase.cs:1081-1227](src/IronRose.Engine/AssetPipeline/AssetDatabase.cs#L1081-L1227)

**공유 상태**:
```csharp
private Task? _reimportTask;
private string? _reimportPath;
private object? _reimportOldAsset;
private MeshImportResult? _reimportMeshResult;
private RoseMetadata? _reimportMeta;
private string? _reimportType;
private Texture2D? _reimportTexResult;
private SpriteImportResult? _reimportSpriteResult;
```

**접근자**:
- **백그라운드 스레드** (Task.Run, 라인 1036): `_reimportMeshResult = result;`, `_reimportTexResult = tex;`, `_reimportSpriteResult = sr;` 쓰기
- **메인 스레드** (EngineCore.Update → ProcessReimport, 라인 1081+): 동일 필드 읽기
- 동기화 원시 없음 — `volatile` 없음, `lock` 없음, `Interlocked` 없음

**시나리오**:
1. 메인이 `ReimportAsync(path)` 호출 → Task.Run 시작, `_reimportTask` 할당.
2. 백그라운드가 `_reimportMeshResult = result;` 수행 (단순 참조 대입이지만 happens-before 보장 없음).
3. 메인이 다음 프레임에 `ProcessReimport()`에서 `_reimportTask.IsCompleted`로 체크 → `true`일지라도 .NET 메모리 모델상 별도 필드 쓰기가 관측된다는 보장은 **Task 완료 상태에 대해서만** 유효하다. 하지만 Task 람다 안의 단순 필드 쓰기는 Task 완료 release fence와 ordering되므로 이 부분은 실제로는 안전한 편이다.
4. **실제 문제**: 백그라운드 Task가 `StoreCacheOrDefer`, `RegisterSubAssets`를 내부에서 호출하는데 이들이 `_loadedAssets` / `_pendingCacheTextures` / `_roseCache` 같은 **다른 공유 상태**를 건드린다 (C2 참조).
5. `IsReimporting`/`ReimportAssetName` 은 외부에서 조회되는데 `_reimportTask`, `_reimportPath`는 메인에서 할당 후 Task가 완료되면 메인이 null로 복구한다. 그러나 Task 내부에서 발생한 예외가 전파되면 Task는 `IsCompleted=true && IsFaulted=true`로 남고, `ProcessReimport`가 `IsFaulted`를 별도 처리하지 않으면 부분 상태가 `_loadedAssets`에 남을 수 있다.

**심각도**: **Critical** — 메시/텍스처 임포트 중간 상태가 관측될 때 씬 참조 오염, GPU 리소스 누수, 캐시 부정합으로 이어진다.

**권고(향후 수정 시)**:
- 결과 필드들을 단일 불변 결과 객체로 묶어 Task 반환값으로 받기.
- `ProcessReimport`에서 `IsFaulted` 처리 분기 추가.

---

### C2. AssetDatabase.`_loadedAssets` 동시 접근

**파일**: [AssetDatabase.cs:43](src/IronRose.Engine/AssetPipeline/AssetDatabase.cs#L43) (정의), [AssetDatabase.cs:746-883](src/IronRose.Engine/AssetPipeline/AssetDatabase.cs#L746-L883), [AssetDatabase.cs:1022-1169](src/IronRose.Engine/AssetPipeline/AssetDatabase.cs#L1022-L1169)

```csharp
private readonly Dictionary<string, object> _loadedAssets = new();  // plain Dictionary, no lock
```

**접근자**:
- **메인 스레드**: `TryGetValue`, `Remove`, `[path] = asset`, `foreach (kvp in _loadedAssets)` (전체 스캔)
- **백그라운드 Task (ReimportAsync 람다)**: 내부에서 `RegisterSubAssets`/`RegisterSpriteSubAssets` 호출. `RegisterSubAssets`가 `_loadedAssets`에 sub-asset을 등록하므로 **백그라운드에서 `Dictionary`를 수정**.
- **FileSystemWatcher 스레드**: `_pendingChanges` 큐로 라우팅되므로 직접 접근은 아님.

**시나리오**:
```
Main:                           Background Task (Reimport):
_loadedAssets.TryGetValue(p)
                                RegisterSubAssets()
                                  _loadedAssets[subPath] = sub;  ← 딕셔너리 구조 변경
                                
Main:                           
_loadedAssets.TryGetValue(p2)  ← 재해싱과 동시에 읽기 → InvalidOperationException / 무한 루프 / 잘못된 노드 반환
```

`Dictionary<TKey,TValue>`는 동시 read/write 시 **구조적 손상**을 일으킬 수 있다 (MSDN 명시). 특히 `Add` 타이밍에 Capacity가 늘어나면서 내부 버킷 재할당이 일어나면 다른 스레드의 `TryGetValue`가 순회 중 null 역참조 또는 무한 루프를 유발한다.

**심각도**: **Critical** — 재현하기 어렵지만 발생하면 프로세스 hang 또는 크래시.

---

### C3. CliCommandDispatcher.ExecuteOnMainThread 의 5초 블로킹

**파일**: [CliCommandDispatcher.cs:2730-2737](src/IronRose.Engine/Cli/CliCommandDispatcher.cs#L2730-L2737)

```csharp
private string ExecuteOnMainThread(Func<string> action)
{
    var task = new MainThreadTask { Execute = action };
    _mainThreadQueue.Enqueue(task);
    if (!task.Done.Wait(TimeSpan.FromSeconds(5)))
        return JsonError("Main thread timeout (5s)");
    return task.Result!;
}
```

**시나리오 1: 메인 스레드 stall**
- 메인 스레드가 블로킹 I/O (예: `PlayerPrefs.Save` 중 파일 잠김, 또는 플레이모드 리로드 중 Scripting 어셈블리 컴파일) 중이면 `ProcessMainThreadQueue`가 5초 안에 돌지 않음.
- CLI 클라이언트는 `JsonError("Main thread timeout (5s)")`를 받지만, **파이프 서버 루프는 블로킹 대기 내내 새 CLI 요청을 처리하지 못함** (`HandleClient` 내부가 단일 루프).

**시나리오 2: 재진입**
- 메인 스레드가 CLI 명령을 dispatch하는 경로 (예: 에디터 UI → CLI) 안에서 다시 `ExecuteOnMainThread`를 호출하면 자기 자신을 기다리는 **확정 데드락**. 현재 그런 직접 경로는 발견되지 않았으나, 이벤트 핸들러가 간접적으로 CLI를 호출하는 경로가 생기면 즉시 5초 tim timeout으로 실패 반환된다.

**시나리오 3: Task 내부 예외**
- `action()`이 예외를 던지면 `task.Result = JsonError(ex.Message); task.Done.Set();` 순서로 처리되므로 블로킹 해제 자체는 정상. 다만 `task.Result`가 null이 아닌지 확인만 있고 catch가 바깥 try 안에 있어 `Set()`이 먼저 실행되는지 확인 필요. 현재 구현([line 116-132](src/IronRose.Engine/Cli/CliCommandDispatcher.cs#L116-L132))은 `finally { task.Done.Set(); }` 이라 안전.

**심각도**: **Critical** — 사용자가 수행하는 작업 중 메인이 길게 블로킹되는 순간(예: 큰 Reimport, 씬 저장, 플레이 stop의 GC)이 5초를 넘기면 CLI 전체가 멈춘다. 플레이모드/핫리로드 병목과 겹치기 쉽다.

---

### C4. RoseMetadata.OnSaved static event

**파일**: [RoseMetadata.cs:68,102](src/IronRose.Engine/AssetPipeline/RoseMetadata.cs#L68)

```csharp
public static event Action<string>? OnSaved;           // 68
...
OnSaved?.Invoke(rosePath[..^5]);                       // 102
```

**시나리오**:
- `Save()`는 메인, 백그라운드 Task (ReimportAsync 내부에서 RoseMetadata.Save 트리거 가능), 그리고 FileSystemWatcher 스레드(외부 수정 detection 후 재 Save) 세 경로에서 호출될 수 있다.
- C#의 `event` += / -= 연산자가 대리자 체인 덧셈을 하는 동안 `Invoke`가 실행되면 **델리게이트 리스트 재할당 사이 window**에서 새로 구독한 핸들러가 호출되지 않거나, 훅 해제가 누락되는 lost update가 발생 가능.
- C# 6+ 의 `event` 연산자는 내부적으로 `Interlocked.CompareExchange`를 사용해 atomic하지만, **invoke 시점에 이미 제거된 핸들러가 호출되는 경우**는 여전히 가능.

**심각도**: **Critical** — 정적 이벤트는 런타임 내내 살아있고, 에셋 저장/리임포트마다 호출되어 가장 자주 트리거되는 이벤트 중 하나. Subscriber가 Dispose된 객체를 참조하는 상태로 호출되면 `ObjectDisposedException` 또는 UI 손상 발생 가능.

---

### C5. SceneManager.AllGameObjects / 기타 `_all*` 리스트

**파일**:
- [SceneManager.cs:61-90](src/IronRose.Engine/RoseEngine/SceneManager.cs#L61-L90)
- [CliCommandDispatcher.cs:160-176](src/IronRose.Engine/Cli/CliCommandDispatcher.cs#L160-L176) (CLI가 `foreach (var go in SceneManager.AllGameObjects)`)
- [CliCommandDispatcher.cs:2744-2767](src/IronRose.Engine/Cli/CliCommandDispatcher.cs#L2744-L2767) (`FindGameObject`/`FindGameObjectById` 도 `AllGameObjects` 순회)

```csharp
private static readonly List<GameObject> _allGameObjects = new();
public static IReadOnlyList<GameObject> AllGameObjects => _allGameObjects;
```

**중요**: CLI 핸들러 대부분은 `ExecuteOnMainThread`로 감싸져 있지만, 일부 `FindGameObject` 호출이 `ExecuteOnMainThread` 내부에 있는지 외부에 있는지 매 핸들러마다 확인이 필요하다. 현재 확인된 바로는 `FindGameObject`는 주석에 "메인 스레드에서 호출해야 한다"로 명시돼 있고 실제 호출 지점들도 람다 안에 있으므로 문제없어 보인다. 그러나:

- `ping` 같은 명령은 [CliCommandDispatcher.cs:140](src/IronRose.Engine/Cli/CliCommandDispatcher.cs#L140)에서 **`ExecuteOnMainThread` 없이 직접 실행**된다. 만약 미래에 백그라운드에서 직접 실행되는 핸들러가 씬을 순회하는 코드로 확장되면 즉시 race가 된다.
- 렌더링 시스템(RenderSystem, GizmoRenderer 등)이 메인에서 `_all*` 리스트를 enum하는 중, 다른 코드가 `GameObject.Destroy` → 라이프사이클로 리스트에서 Remove 하면 `InvalidOperationException: Collection was modified`가 발생 가능. 그러나 `SceneManager`는 Destroy를 `_destroyQueue`로 지연 처리하므로 이 한정적으로는 완화됨.

**추가 리스크**: 아래 `_all*` 리스트들은 모두 plain `List<T>`이며 라이프사이클이 OnAddedToGameObject/OnComponentDestroy에서 즉시 Add/Remove 한다:

| 리스트 | 파일 | 비고 |
|--------|------|------|
| `_allRenderers` | [MeshRenderer.cs:10-17](src/IronRose.Engine/RoseEngine/MeshRenderer.cs#L10-L17) | 렌더 패스 중 enum |
| `_allLights` | Light.cs | 렌더 패스 중 enum |
| `_allCanvases` | [Canvas.cs:27](src/IronRose.Engine/RoseEngine/Canvas.cs#L27) | UI 렌더 |
| `_allSpriteRenderers` | [SpriteRenderer.cs:19](src/IronRose.Engine/RoseEngine/SpriteRenderer.cs#L19) | 2D 렌더 |
| `_allTextRenderers` | [TextRenderer.cs:23](src/IronRose.Engine/RoseEngine/TextRenderer.cs#L23) | |
| `_allMipMeshFilters` | [MipMeshFilter.cs:35](src/IronRose.Engine/RoseEngine/MipMeshFilter.cs#L35) | |
| `_allUIInputFields` | [UI/UIInputField.cs:63](src/IronRose.Engine/RoseEngine/UI/UIInputField.cs#L63) | |
| `_allUITexts` | [UI/UIText.cs:36](src/IronRose.Engine/RoseEngine/UI/UIText.cs#L36) | |
| `_allVolumes` | [PostProcessVolume.cs:12](src/IronRose.Engine/RoseEngine/PostProcessVolume.cs#L12) | `AssetDatabase.Reimport`에서 enum (메인이지만 ReimportAsync 백그라운드에서 `RegisterSubAssets`로 다른 경로 공유 — 아래 H4 참조) |
| `_allColliders`, `_allColliders2D` | [Collider.cs:16](src/IronRose.Engine/RoseEngine/Collider.cs#L16), [Collider2D.cs:12](src/IronRose.Engine/RoseEngine/Collider2D.cs#L12) | 물리 콜백에서 접근 여부 확인 필요 |
| `_rigidbodies` | [Rigidbody.cs:31](src/IronRose.Engine/RoseEngine/Rigidbody.cs#L31) | 물리 Step 내부 |

현재는 **모든 라이프사이클 관리가 메인 스레드에서 이루어지는 것으로 보이므로** 실질 race는 낮지만, **CLI가 `delete.gameobject`/`create.gameobject` 같은 명령을 `ExecuteOnMainThread` 없이 처리하는 순간** 즉시 Critical race로 승격된다.

**심각도**: 현재는 **Critical (잠재)** — 구조적 취약. 향후 CLI나 AssetPipeline 쪽에서 실수 한 번이면 재현된다.

---

### C6. CliPipeServer.Stop() 의 `_cts.Dispose()` 순서

**파일**: [CliPipeServer.cs:62-119](src/IronRose.Engine/Cli/CliPipeServer.cs#L62-L119)

```csharp
public void Stop()
{
    if (!IsRunning) return;                    // 64
    _cts?.Cancel();                            // 67
    ...
    _serverThread?.Join(TimeSpan.FromSeconds(3));   // 76  ← 3초 타임아웃
    _cts?.Dispose();                           // 77
    IsRunning = false;                         // 78
}

private void ServerLoop()
{
    while (!_cts!.Token.IsCancellationRequested)   // 84
    {
        ...
        pipe.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();  // 96
        if (_cts.Token.IsCancellationRequested) break;                     // 98
        ...
    }
    IsRunning = false;                         // 118
}
```

**시나리오**:
- `_serverThread.Join(3s)`가 3초 안에 끝나지 않으면 `ServerLoop`이 아직 실행 중인 상태에서 `_cts.Dispose()`가 호출된다.
- 그 직후 ServerLoop이 `_cts.Token` (라인 84, 98) 또는 `_cts.Token.IsCancellationRequested` (라인 125)에 접근하면 **`ObjectDisposedException`** 발생.
- `HandleClient` 루프 안에서 외부 클라이언트가 긴 요청을 물고 있으면 3초를 쉽게 넘긴다.

**심각도**: **High** — 에디터 종료 경로에서 예외가 전파되어 깔끔한 종료를 막을 수 있다.

---

## 3. High 위험 지점

### H1. CliPipeServer.IsRunning 플래그

[CliPipeServer.cs:33](src/IronRose.Engine/Cli/CliPipeServer.cs#L33)에 `public bool IsRunning { get; private set; }` — 일반 auto-property. 다음 경로에서 읽기/쓰기:

- Start (메인, line 46): `IsRunning = true`
- Stop (메인, line 64/78): `IsRunning` 읽기, `IsRunning = false`
- ServerLoop (백그라운드, line 118): `IsRunning = false`

`bool` 쓰기는 atomic이지만 visibility 보장은 없음(메모리 모델상 read side가 cache된 값을 볼 수 있음). 외부 코드(에디터/Plugin)가 `IsRunning`을 폴링하는 경우 false 전환을 놓칠 수 있다.

**심각도**: **High** — 단독으로는 경미하나 C6와 결합하면 "멈췄지만 IsRunning이 아직 true"라는 상태에서 파이프 재시작이 거부되는 패턴으로 누적된다.

---

### H2. FileSystemWatcher 디바운스 큐 재투입 윈도우

**파일**: [AssetDatabase.cs:1689-1802](src/IronRose.Engine/AssetPipeline/AssetDatabase.cs#L1689-L1802)

- `_watcher.Changed/Created/Deleted/Renamed` 콜백은 **.NET ThreadPool의 아무 스레드**에서 호출된다.
- `EnqueueChange`는 `lock (_changeLock)` 안에서 `_pendingChanges.Enqueue`를 수행 — 단일 락으로 보호되므로 enqueue는 안전.
- `ProcessFileChanges`(메인)는 락 안에서 큐를 비우고(`_pendingChanges.Clear()`), **락 밖에서** 각 이벤트의 타임스탬프를 검사, 디바운스 시간 미경과면 `toRequeue`에 누적 후 다시 락을 잡아 재투입.

**시나리오**:
1. FSW 스레드가 "file A changed at t=100ms" 이벤트를 enqueue.
2. 메인이 ProcessFileChanges를 호출. 락 획득 후 큐를 비우고 release.
3. 락 밖에서 타임스탬프 검사 중, FSW 스레드가 "file A changed at t=120ms"를 enqueue (새 이벤트).
4. 메인은 (락 밖에서) 디바운스 미경과 판정하여 자신이 방금 꺼낸 t=100ms 이벤트를 락을 다시 잡아 re-enqueue.
5. 결과: 큐에 "file A @ t=100"과 "file A @ t=120"이 둘 다 들어가 있고, 다음 라운드에서 디바운스 윈도우가 **가장 오래된 타임스탬프 기준**으로 계산되어 파일이 짧은 순간에 여러 번 import되거나, 반대로 가장 최근 이벤트만 남기는 dedup 의도가 깨진다.

**심각도**: **High** — 에셋 저장이 반복되는 플로우(특히 에디터 자동 저장 + 외부 에디터 동시 편집)에서 무한 Reimport 루프가 발생할 가능성.

---

### H3. Animator 의 `_targets` / `Parallel.For`

**파일**: [Animator.cs:80-181](src/IronRose.Engine/RoseEngine/Animator.cs#L80-L181)

- `InvalidateTargets()`([line 97](src/IronRose.Engine/RoseEngine/Animator.cs#L97))는 `Volatile.Write(ref _targets, null)` — 에디터 스레드에서 호출.
- `Update()`([line 161](src/IronRose.Engine/RoseEngine/Animator.cs#L161))는 `Volatile.Read(ref _targets)`로 로컬 참조 확보 후 `Parallel.For`.
- 로컬 `targets`로 내려받은 뒤에는 다른 스레드가 `_targets`를 바꿔도 Parallel.For는 기존 배열을 쓰므로 **배열 교체 race는 안전**.
- 그러나 **배열 원소의 상태**(PropertyTarget 내부 참조)가 에디터에서 수정되면(예: `Animator.clip` 교체) `Parallel.For` 내부 `targets[i].Evaluate(evalTime)`에서 NRE/stale 참조 가능.
- 또한 `SampleAt`([line 80-89](src/IronRose.Engine/RoseEngine/Animator.cs#L80-L89))과 `CapturePreviewSnapshot`([line 110-120](src/IronRose.Engine/RoseEngine/Animator.cs#L110-L120))은 `_targets`를 필드 그대로 순회 — `Volatile.Read` 없이 루프 중간에 `InvalidateTargets`가 호출되면 NRE 가능.

**심각도**: **High** — 에디터에서 Animator 프리뷰 중에 Inspector로 clip을 바꾸면 재현될 수 있는 클래식 패턴.

---

### H4. AssetDatabase.OnRoseMetadataSaved → Reimport 재진입

**파일**: [AssetDatabase.cs:1865](src/IronRose.Engine/AssetPipeline/AssetDatabase.cs#L1865)

`RoseMetadata.OnSaved`가 FileSystemWatcher 스레드나 백그라운드 Task에서 invoke되는 경우, 핸들러가 `Reimport(path)`를 호출하는데 `Reimport`는 GPU 리소스 생성(`Texture2D` 생성, `MeshImporter` 등)을 포함한다. Vulkan/Veldrid는 **동일 GraphicsDevice를 다중 스레드에서 동시에 사용하면 UB**.

**심각도**: **High** — GPU crash.

---

### H5. `_allVolumes` 등을 AssetDatabase.Reimport가 enum

**파일**: [AssetDatabase.cs:841](src/IronRose.Engine/AssetPipeline/AssetDatabase.cs#L841) 근처 (`foreach (var vol in PostProcessVolume._allVolumes)` 또는 유사 패턴 — 이전 agent 탐색에서 확인)

- Reimport는 주로 메인 스레드이지만 `ReimportAsync`의 백그라운드 Task 내부에서 `RegisterSubAssets`가 호출되며, 이 경로가 `_allVolumes`를 참조할 수 있다.
- 씬 unload/GameObject.Destroy가 `_allVolumes.Remove`를 호출하는 동안 백그라운드 Task가 순회하면 `InvalidOperationException`.

**심각도**: **High** — C5와 유사.

---

### H6. GraphicsDevice를 백그라운드 Task에서 접근할 가능성

**파일**:
- [GpuTextureCompressor.cs](src/IronRose.Engine/AssetPipeline/GpuTextureCompressor.cs) — 주석상 "GPU 압축"이고 `AssetWarmupManager.cs:94`에서 "메인 스레드 필요"라고 명시된 이유.
- [Bc6hEncoder.cs](src/IronRose.Engine/AssetPipeline/Bc6hEncoder.cs) — Task.Run 사용.

**시나리오**:
- `ReimportAsync`의 백그라운드 Task가 `StoreCacheOrDefer`→`GpuTextureCompressor` 경로를 호출하면 Vulkan 커맨드 빌드/디스크립터 할당이 비메인 스레드에서 일어난다.
- 코드상 `StoreCacheOrDefer`는 "defer"로 메인 지연을 유도하도록 설계되어 있지만, **defer 큐에 넣지 않고 즉시 수행하는 분기가 있으면** 즉시 Vulkan 호출이 발생한다.
- 더욱이 `IsInPlaySession` 같은 분기 조건이 플레이 종료 직후 false로 바뀌는 race가 있으면 같은 세션 안에서 일관성이 깨진다.

**심각도**: **High** — 반드시 defer 경로가 모든 케이스를 커버하는지 런타임 테스트 필요.

---

## 4. Medium 위험 지점

### M1. PlayerPrefs — 락 안에서 파일 I/O

**파일**: [PlayerPrefs.cs:184-304](src/IronRose.Engine/RoseEngine/PlayerPrefs.cs#L184-L304)

`Save()`가 `lock (_lock)` 상태로 `config.SaveToFile(...)` 수행. 파일 I/O가 느리거나 AV 스캐너가 잠그면 다른 스레드(주로 `Get*` 호출)가 블로킹된다. 데드락은 아니지만 **장시간 정체**로 프레임 드랍.

---

### M2. Texture2D.DefaultNormal / DefaultMRO lazy init

**파일**: [Texture2D.cs:207-215](src/IronRose.Engine/RoseEngine/Texture2D.cs#L207-L215)

```csharp
public static Texture2D DefaultNormal => _defaultNormal ??= CreateDefaultNormal();
```

메인에서만 호출되도록 설계되었을 가능성이 높지만, 만약 스크립트 리로드 중 백그라운드 Task가 이 프로퍼티를 건드리면 double-create가 발생. 낭비일 뿐 crash는 아님.

---

### M3. PhysicsWorld3D 의 `_currentContacts` / `_previousContacts`

**파일**: [PhysicsWorld3D.cs](src/IronRose.Physics/PhysicsWorld3D.cs)

- `lock (_lock)` 안에서 `_currentContacts.Add` — BepuPhysics 내부의 worker thread에서 `RecordContact`가 호출될 수 있음.
- `Flush`는 lock으로 swap 후 lock 밖에서 `_currentContacts` 컬렉션 자체를 iterate.
- 설계상 `Flush`는 Physics Step이 끝난 후 메인에서만 호출되어야 하며, 그 시점에는 worker thread의 `RecordContact`가 완료된 것으로 보장되어야 하지만, **문서화된 불변식**이 없으므로 향후 리팩토링 시 깨지기 쉽다.

---

### M4. EditorAssetSelection — "메인 전용" 주석의 취약성

**파일**: [EditorAssetSelection.cs:20-150](src/IronRose.Engine/Editor/EditorAssetSelection.cs#L20-L150)

주석에 "thread-safe 아님 — 에디터 메인 스레드에서만 호출할 것"이라고 명시. CLI가 `asset.select`를 구현한다면 반드시 `ExecuteOnMainThread` 안에서 처리해야 한다. 현재 코드에서는 지켜지는 것으로 보이지만 제약이 주석으로만 표현되어 있어 회귀 위험.

---

## 5. 안전하다고 판단된 지점

- **AiImageGenerationService.`_inFlightPaths`** ([라인 64-113](src/IronRose.Engine/Editor/AiImageGenerationService.cs#L64-L113)) — 모든 HashSet 접근이 `lock (_inFlightLock)` 안에서 이루어지며 `_runningCount`도 `Interlocked`로 처리. OK.
- **CliLogBuffer** ([CliLogBuffer.cs](src/IronRose.Engine/Cli/CliLogBuffer.cs)) — `lock (_lock)`으로 일관되게 보호. OK.
- **CliCommandDispatcher.`_mainThreadQueue`** ([라인 70](src/IronRose.Engine/Cli/CliCommandDispatcher.cs#L70)) — `ConcurrentQueue<MainThreadTask>`로 적절히 선언. enqueue/dequeue 자체는 안전. 데드락 문제(C3)는 별개.
- **ImGuiPreferencesPanel / ImGuiStartupPanel의 Task.Run** — 결과가 단일 필드로 전달되고 메인에서 `IsCompleted` 이후 한 번만 읽히는 패턴이면 안전. (개별 확인은 하지 않음 — 필요 시 추가 감사.)

---

## 6. 특이 패턴 / 잠재 위험 (낮음 혹은 식별 필요)

1. **`EditorBridge.cs`의 Task.Run** — 어떤 공유 상태를 만지는지 추가 확인 필요. (오늘 범위에서는 정적 감사만 수행.)
2. **`ClaudeManager.cs`의 Task.Run** — LLM 호출 비동기. 결과 전달 경로가 메인에서 단일 필드 폴링인지 확인 필요.
3. **`NativeFileDialog.cs`의 Task.Run** — GTK/Win 네이티브 대화상자. 스레딩 모델이 플랫폼별로 다를 수 있음.
4. **Coroutine 스케줄러** ([CoroutineScheduler.cs:13](src/IronRose.Engine/RoseEngine/CoroutineScheduler.cs#L13)) — 메인 전용으로 보이지만 `StartCoroutine`이 백그라운드에서 호출될 경로가 있는지 확인 필요.

---

## 7. 공통 권고 (차후 수정 시)

1. **문서화된 불변식**: "이 필드/리스트는 메인 스레드에서만" 같은 제약은 주석만이 아니라 **assert/Debug.Assert**로 런타임 확인.
2. **`_all*` 정적 리스트**: 최소한 AssetDatabase.Reimport 경로에서는 snapshot(`.ToArray()`)을 만들어 iterate.
3. **`ReimportAsync` 결과 전달**: `_reimport*` 개별 필드 대신 단일 immutable 결과 객체를 Task 반환값으로 받기.
4. **GPU 경계**: Vulkan/Veldrid 접근 메서드에 스레드 체크 가드를 삽입 (`Debug.Assert(Thread.CurrentThread == MainThread)`).
5. **FileSystemWatcher 디바운스**: 디바운스 판정도 락 안에서 수행하거나, 이벤트별 "최신 타임스탬프" dedup을 `Dictionary<path, latestTick>`으로 관리.
6. **Static event**: `RoseMetadata.OnSaved`를 `event` 명시적 add/remove accessor + 전용 lock으로 재작성, 또는 `System.Threading.Channels` 기반 pub/sub으로 대체.
7. **CliPipeServer.Stop**: `_serverThread.Join` 후에만 `_cts.Dispose()`. Join이 timeout되면 Dispose 보류하고 warning 로그.
8. **`ExecuteOnMainThread`**: 5초 timeout을 Dispatcher 측에서 설정 가능하게 하고, 메인 스레드 stall 감지 시 파이프 클라이언트에 "busy" 상태를 먼저 반환.

---

## 8. 결론

가장 시급한 위험은 **AssetDatabase의 비동기 리임포트 경로**에 집중되어 있다(C1, C2, H4, H6). 이 경로는 메인/백그라운드/FileSystemWatcher 세 스레드가 동일한 딕셔너리/GPU 리소스/이벤트를 비보호 상태로 접근하고 있어, 재현은 드물지만 발생 시 씬 참조 오염, GPU 크래시, 무한 reimport 루프로 이어질 수 있다.

다음 시급한 계열은 **CLI 파이프 종료/블로킹 경로**(C3, C6, H1)이며, 이는 에디터 종료 안정성 및 플레이모드 전환 시 hang과 직접 연결된다.

씬 라이프사이클의 `_all*` 정적 리스트(C5, H5)는 현재 메인 단독 접근으로 보호되고 있으나, **CLI가 하나의 명령만 `ExecuteOnMainThread` 감싸기를 놓쳐도 즉시 Critical race로 승격**되는 구조적 취약성을 가진다.
