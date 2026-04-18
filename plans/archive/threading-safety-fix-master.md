---
name: IronRose 스레드 안전성 마스터 계획 — 정적 분석 기반 전면 수정
type: plan
date: 2026-04-18
scope: IronRose.Engine / IronRose.Rendering / IronRose.Physics / IronRose.AssetPipeline / IronRose.Editor / IronRose.Contracts
depends_on: plans/static-analysis-threading-race-deadlock.md
status: draft
---

# IronRose 스레드 안전성 마스터 계획

> 참조: [plans/static-analysis-threading-race-deadlock.md](static-analysis-threading-race-deadlock.md)
> 대상: 해당 보고서가 식별한 Critical 6 + High 6 + Medium 4 = 16건을 **모두** 해결
> 산출: 본 문서는 상위 설계. 각 Phase 상세 명세서는 **`aca-archi`** 가 이어서 작성.

---

## 배경

IronRose 엔진은 현재 다음과 같은 다중 스레드 경로를 가진다:

- 메인 스레드(`EngineCore.Update`)
- CLI 서버 스레드(`CliPipeServer._serverThread`)
- 워커 Task(`Task.Run` — `ReimportAsync`, `AssetWarmupManager`, `Bc6hEncoder`, AI 이미지 생성)
- FileSystemWatcher 콜백(.NET ThreadPool)
- `Parallel.For` 파티션 스레드(`Animator.Update`)
- Veldrid/Vulkan 내부 스레드(현재는 메인에서만 드라이브)

정적 분석 결과 다음 공통 패턴이 반복적으로 나타났다:

1. **공유 `_all*` 정적 리스트**가 동기화 없이 접근됨 (C5, H4, H5)
2. **백그라운드 Task가 메인 스레드 자료구조를 직접 수정** (C1, C2, H4, H6)
3. **CLI 파이프 라이프사이클과 메인 스레드 블로킹이 얽혀 데드락 가능** (C3, C6, H1)
4. **static event / lazy init이 동기화 없음** (C4, M2)
5. **FileSystemWatcher 콜백이 공유 상태를 직접 변경** (H2, H5)
6. **락 보유 상태에서 파일 I/O** (M1)
7. **"메인 스레드 전용" 제약이 주석만으로 표현됨** (M3, M4)

이 문서는 위 16건을 **심각도 순(Critical → High → Medium)** 으로 수정하고, **재발 방지를 위한 인프라(`ThreadGuard`)와 개발자 가이드(CLAUDE.md)** 를 함께 도입하는 마스터 계획이다.

---

## 목표

1. **정적 분석 보고서의 16건 이슈를 모두 해결** (Critical 6 / High 6 / Medium 4).
2. **재발 방지 인프라**: 스레드 체크 유틸(`RoseEngine.ThreadGuard`) 도입, 위반 감지 시 `EditorDebug.LogError`로 로그만 남기고 **크래시/데드락 없이 graceful하게 진행**.
3. **개발자 가이드**: CLAUDE.md에 스레드 규칙 섹션 추가. 새 코드 작성 시 `ThreadGuard.CheckMainThread(...)` 삽입을 관례화.
4. **회귀 방지**: 각 Phase 종료 시 수동 스모크 테스트 시나리오를 통과해야 함.
5. **머지 경로**: 각 Phase는 독립 worktree에서 진행하고, `aca-code-review` PASS 후 메인에 머지.

---

## 현재 상태 (요약)

| 영역 | 관련 파일 | 현재 이슈 번호 |
|------|-----------|----------------|
| AssetDatabase 비동기 리임포트 | `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` | C1, C2, H2, H4, H5 |
| CLI 파이프 서버/디스패처 | `src/IronRose.Engine/Cli/CliPipeServer.cs`, `CliCommandDispatcher.cs` | C3, C5, C6, H1 |
| Static 이벤트 | `src/IronRose.Engine/AssetPipeline/RoseMetadata.cs` | C4 |
| Static `_all*` 리스트 | 15+ 컴포넌트 파일 (PostProcessVolume, MeshRenderer, Light, Canvas, ...) | C5, H4 |
| GPU 리소스 백그라운드 접근 | `GpuTextureCompressor.cs`, `Bc6hEncoder.cs`, Veldrid 전역 | H6 |
| Animator Parallel.For | `src/IronRose.Engine/RoseEngine/Animator.cs` | H3 |
| PlayerPrefs 파일 I/O | `src/IronRose.Engine/RoseEngine/PlayerPrefs.cs` | M1 |
| Texture2D lazy init | `src/IronRose.Engine/RoseEngine/Texture2D.cs` | M2 |
| PhysicsWorld3D contacts | `src/IronRose.Physics/PhysicsWorld3D.cs` | M3 |
| EditorAssetSelection | `src/IronRose.Engine/Editor/EditorAssetSelection.cs` | M4 |

---

## 설계

### 개요

- **선행 Phase A (인프라)** 를 먼저 머지하여 이후 모든 Phase가 `ThreadGuard` 기반 검증을 공유한다.
- 이후 Phase B ~ E는 **주제별 수직 슬라이스**로 나눠, 각 Phase가 한 영역을 완결하도록 한다.
- Phase F는 CLAUDE.md 가이드 및 최종 스모크 테스트 체크리스트 작성.
- **Phase 의존성**: A → (B, C, D 병렬 가능) → E → F. 단, 실제로는 순차 진행을 권장 (머지 충돌 최소화).

```
       Phase A (ThreadGuard 인프라)
                  │
     ┌────────────┼────────────┐
     ▼            ▼            ▼
  Phase B      Phase C      Phase D
 (Asset       (CLI          (Static
  Reimport)    Lifecycle)    Lists/Events)
     │            │            │
     └────────────┼────────────┘
                  ▼
            Phase E (Animator/Physics/
                    PlayerPrefs/Texture2D/
                    EditorAssetSelection)
                  │
                  ▼
            Phase F (CLAUDE.md 가이드 +
                    통합 스모크 테스트)
```

---

### Phase A: 인프라 — `ThreadGuard` + 메인 스레드 캡처

**다루는 이슈**: (없음 — 선행 인프라) — 이후 모든 Phase가 이 위에서 동작.

**핵심 변경 방향**: 새 파일 작성 + `EngineCore.Initialize` 1줄 변경.

#### A-1. `RoseEngine.ThreadGuard` 도입

**신규 파일**: `src/IronRose.Contracts/ThreadGuard.cs` (또는 `src/IronRose.Engine/RoseEngine/ThreadGuard.cs`)

- Contracts에 두는 이유: AssetPipeline, Rendering, Physics, Editor가 모두 참조 가능해야 함. `EditorDebug`와 동일 레이어.
- 네임스페이스: `RoseEngine` (EditorDebug와 동일).

```csharp
namespace RoseEngine
{
    public static class ThreadGuard
    {
        private static int _mainThreadId = -1;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _lastLogTicks = new();
        private const long LogCooldownTicks = TimeSpan.TicksPerSecond * 5; // 동일 call site 5초 쿨다운

        /// <summary>메인 스레드에서 한 번 호출. EngineCore.Initialize 최상단에서 호출.</summary>
        public static void CaptureMainThread()
        {
            _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>메인 스레드 ID (캡처 전에는 -1).</summary>
        public static int MainThreadId => _mainThreadId;

        /// <summary>현재 스레드가 메인인지.</summary>
        public static bool IsMainThread =>
            _mainThreadId != -1 && System.Threading.Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>
        /// 메인 스레드에서 호출되었는지 검증. 위반 시 EditorDebug.LogError를 호출하고
        /// false를 반환하지만 throw하지 않는다. 호출자는 반환값을 보고 안전하게 스킵할 수 있다.
        /// context는 call site 식별자(예: "AssetDatabase.Reimport").
        /// </summary>
        public static bool CheckMainThread(string context)
        {
            if (_mainThreadId == -1) return true; // 캡처 전에는 체크 스킵
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == _mainThreadId) return true;

            var now = DateTime.UtcNow.Ticks;
            if (_lastLogTicks.TryGetValue(context, out var last) && (now - last) < LogCooldownTicks)
                return false;
            _lastLogTicks[context] = now;

            EditorDebug.LogError(
                $"[ThreadGuard] {context} must be called on main thread " +
                $"(called from thread {System.Threading.Thread.CurrentThread.ManagedThreadId}, " +
                $"main={_mainThreadId}). Continuing in unsafe mode.");
            return false;
        }

        /// <summary>Debug 빌드에서만 체크. Release에서는 no-op.</summary>
        [System.Diagnostics.Conditional("DEBUG")]
        public static void DebugCheckMainThread(string context) => CheckMainThread(context);
    }
}
```

**설계 포인트**:
- **throw 금지**: 위반 시 `EditorDebug.LogError`만 호출. 데드락/크래시 회피.
- **로그 홍수 방지**: `context` 문자열 기반 5초 쿨다운. `ConcurrentDictionary<string, long>`로 스레드 안전.
- **캡처 전 상태**: `_mainThreadId == -1`이면 체크 스킵 (초기화 전 호출을 허용).
- **`[Conditional("DEBUG")]` 바리에이션**: 핫 패스(예: `_all*` 리스트 enum 내부 루프)에서는 `DebugCheckMainThread` 사용.

#### A-2. `EngineCore.Initialize` 최상단에 캡처 삽입

**수정 파일**: `src/IronRose.Engine/EngineCore.cs` (line 139 `Initialize(IWindow)` 최상단)

```csharp
public void Initialize(IWindow window)
{
    ThreadGuard.CaptureMainThread(); // 추가
    // CLI 로그 버퍼 생성 ...
}
```

**의존 Phase**: 없음
**예상 리스크**: 낮음 (신규 코드만 추가, 기존 동작 변경 없음)
**리뷰 체크리스트**:
- [ ] `ThreadGuard`가 `RoseEngine` 네임스페이스에 있는가?
- [ ] `CheckMainThread`가 throw하지 않고 bool 반환인가?
- [ ] 쿨다운이 `ConcurrentDictionary` 기반으로 thread-safe한가?
- [ ] `CaptureMainThread()`가 `Initialize(IWindow)` 진입 직후에 호출되는가?
- [ ] `EditorDebug.LogError` 호출 시 재귀 위험이 없는가?
- [ ] Debug/Release 빌드 모두 빌드가 깨지지 않는가?

---

### Phase B: AssetDatabase 비동기 리임포트 경로

**다루는 이슈**: **C1, C2, H2, H4, H6**
**파일**: `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs`, `RoseMetadata.cs`, `GpuTextureCompressor.cs`, `Bc6hEncoder.cs`

#### B-1. (C1) `_reimport*` 개별 필드 → 단일 불변 결과 객체

- `record ReimportResult(string Path, RoseMetadata? Meta, string Type, object? OldAsset, MeshImportResult? Mesh, Texture2D? Tex, SpriteImportResult? Sprite, Exception? Error)` 신규 정의.
- `ReimportAsync`가 `Task<ReimportResult>`를 반환하도록 변경. 개별 필드(`_reimportPath` 등) 제거.
- `ProcessReimport()`는 `_reimportTask.Result`에서 결과를 받아 처리. `IsFaulted` 분기 추가 (현재 없음).
- **목표**: 여러 필드에 대한 happens-before 보장을 Task 완료 fence 하나로 통일.

#### B-2. (C2) `_loadedAssets` 동기화

두 가지 선택지 중 **선택지 1 권장**:

- **선택지 1 (권장)**: `_loadedAssets`를 `Dictionary<string, object>` → `ConcurrentDictionary<string, object>`로 교체.
  - 장점: 변경 최소화, 백그라운드 `RegisterSubAssets` 경로가 그대로 안전해짐.
  - 단점: 순회 시 snapshot 필요 (현재 몇 군데서 `foreach (var kvp in _loadedAssets)` 사용).
- **선택지 2**: plain Dict + 전용 lock 도입. 모든 접근을 lock으로 감싸기.
  - 단점: long-hold 락 위험, 리팩토링 범위 큼.

선택지 1로 진행하며, 순회 코드는 `ToArray()` snapshot으로 래핑.

#### B-3. (C2, H4, H6) 백그라운드 Task가 공유 자료구조를 직접 수정하지 못하도록 분리

- `ReimportAsync` 내부 Task.Run 람다는 **순수 계산만** 수행 (디코딩, 압축, 정점 생성).
- `RegisterSubAssets` / `RegisterSpriteSubAssets` / `_pendingCacheTextures` 등 **공유 상태 쓰기는 메인의 `ProcessReimport`에서만** 수행.
- GPU 리소스 생성(`Texture2D` 객체화, Veldrid `ResourceFactory`) 또한 메인에서만.
- 백그라운드 Task의 파일 I/O, CPU 디코드, BC6H 인코드, 메시 삼각화는 계속 허용.

#### B-4. (H2) FileSystemWatcher 디바운스 dedup 개선

- 현재: `_pendingChanges` 큐 + 락 밖 타임스탬프 검사 → re-enqueue race (보고서 H2 참조).
- 변경: `Dictionary<path, FileChangeEntry>` 기반 "최신 타임스탬프만 보관" dedup로 교체.
  - `EnqueueChange`는 `lock` 안에서 path 키를 기준으로 덮어쓰기.
  - `ProcessFileChanges`는 `lock` 안에서 현재 시점에 "디바운스 경과한 항목"만 추출하고 나머지는 맵에 남겨둔다.
- 결과: 재투입 윈도우 자체가 소멸.

#### B-5. (H4) `RoseMetadata.OnSaved` 핸들러에서 `Reimport` 직접 호출 금지

- 현재: FSW 콜백 스레드에서 `OnSaved` → `Reimport(path)` → GPU 호출 가능.
- 변경: 핸들러 안에서는 `AssetDatabase`의 "재임포트 요청 큐"에 enqueue만 하고, 메인 `Update`에서 pull.
- AssetDatabase 내부에 `ConcurrentQueue<string> _metadataSavedQueue` 신설 → 메인 틱에서 처리.

#### B-6. (H6) GPU 리소스 생성에 ThreadGuard 삽입

- `GpuTextureCompressor.Compress*`, `Bc6hEncoder.EncodeGpu*`, `Texture2D` 생성자, `GraphicsDevice` 커맨드 빌더 진입점에 `ThreadGuard.CheckMainThread(...)` 삽입.
- 위반 시 **로그만 남기고 해당 GPU 호출을 스킵** (메서드는 `null`/빈 값 반환). 호출자는 다음 프레임에 메인에서 재시도하도록 큐잉.
- **주의**: `Bc6hEncoder`의 CPU 인코드 경로는 계속 백그라운드 허용. GPU 경로만 가드.

**의존 Phase**: A
**예상 리스크**: 중간 — `ReimportAsync` 반환 타입 변경은 호출자 여러 곳에 파급. 단계적으로 나눠 커밋 권장.
**리뷰 체크리스트**:
- [ ] `_reimport*` 개별 필드 전부 제거되었는가?
- [ ] `Task<ReimportResult>` 반환이고 `IsFaulted` 처리되는가?
- [ ] `_loadedAssets`가 `ConcurrentDictionary` 또는 lock 완전 커버인가?
- [ ] `ReimportAsync` 람다가 GPU API나 `_all*` 리스트에 접근하지 않는가?
- [ ] FSW 디바운스가 dedup 맵 기반인가?
- [ ] `OnSaved` 핸들러가 큐잉만 하고 `Reimport`를 직접 호출하지 않는가?
- [ ] GPU 진입 지점에 `ThreadGuard` 삽입되었는가?
- [ ] 위반 시 로그만 남기고 스킵하는지 (throw 없음)?

**스모크 테스트**:
1. 큰 mesh/texture 에셋 반복 Reimport (10회).
2. 외부 에디터로 .png 덮어쓰기 + 메타데이터 Save 동시 발생.
3. 플레이모드 중 에셋 변경 → 플레이 Stop → 리임포트 완료 확인.

---

### Phase C: CLI 파이프 서버 라이프사이클 & 디스패처

**다루는 이슈**: **C3, C6, H1**
**파일**: `src/IronRose.Engine/Cli/CliPipeServer.cs`, `CliCommandDispatcher.cs`

#### C-1. (C6) `CliPipeServer.Stop()` 순서 수정

- 현재: `Cancel()` → `Join(3s)` → `Dispose()` — Join timeout 시 Dispose 이후 ServerLoop이 token에 접근해 ODE.
- 변경:
  1. `Cancel()` 호출.
  2. `_serverThread.Join()` (timeout 없음, 또는 긴 timeout 후 warning log).
  3. Join 성공 후에만 `_cts.Dispose()`.
  4. Join timeout 발생 시 Dispose 스킵 + warning 로그 ("CLI pipe server thread did not terminate within Ns; leaking CancellationTokenSource").
- `ServerLoop` 내부 `_cts.Token` 접근을 `try { ... } catch (ObjectDisposedException) { break; }`로 방어.

#### C-2. (H1) `IsRunning` 플래그 가시성

- `public bool IsRunning { get; private set; }` → 백킹 필드 `private int _isRunning` + `Interlocked.Exchange`.
- 또는 단순히 `private volatile bool _isRunning` + `public bool IsRunning => _isRunning`로 변경.
- 외부 폴링 코드가 false 전환을 즉시 관측하도록.

#### C-3. (C3) `ExecuteOnMainThread` 블로킹 개선

- 5초 고정 timeout → **설정 가능** (`CliExecuteOptions { TimeoutMs = 5000 }`).
- **메인 스레드 stall 감지**: `ProcessMainThreadQueue`가 마지막으로 돈 시각을 `_lastDrainTicks`에 기록. Dispatch 진입 시 `_lastDrainTicks`가 2초 이상 이전이면 **즉시 "busy" 에러** 반환 (블로킹 대기 안 함).
- **재진입 방지**: `ExecuteOnMainThread` 진입 시 `ThreadGuard.IsMainThread == true`면 "recursive main-thread execution" 에러 반환 (현재 메인에서 호출되면 즉시 실행해도 되지만, 재진입은 설계 오류이므로 로그만 남기고 action을 직접 수행).
- **타임아웃 시 큐 cleanup**: 현재는 timeout 후에도 `MainThreadTask`가 큐에 남아 나중에 엉뚱한 시점에 실행됨. Task에 `Cancelled` 플래그 추가하여 메인에서 스킵.

**의존 Phase**: A
**예상 리스크**: 중간 — CLI 종료 경로 변경은 에디터 종료 시 hang 유발 가능. Join에 safety timeout(예: 10초) + warning 로그 병행.
**리뷰 체크리스트**:
- [ ] `Stop()`이 Join 성공 전에 Dispose 하지 않는가?
- [ ] `IsRunning`이 volatile/Interlocked로 보호되는가?
- [ ] `ExecuteOnMainThread`가 메인 stall 감지 시 즉시 busy 반환하는가?
- [ ] Timeout된 `MainThreadTask`가 나중에 실행되지 않는가?
- [ ] ServerLoop이 ODE를 catch하여 graceful break하는가?

**스모크 테스트**:
1. 에디터 시작 → 즉시 종료 반복 (10회) — ODE/crash 없어야 함.
2. CLI 클라이언트가 장기 요청 중 에디터 종료 — 3초 이내 종료.
3. 플레이모드 진입 중(메인 stall) `rose-cli` 호출 → busy 응답 확인.

---

### Phase D: Static `_all*` 리스트 & Static 이벤트

**다루는 이슈**: **C4, C5, H5**
**파일**: `RoseMetadata.cs`, `SceneManager.cs`, `PostProcessVolume.cs`, `MeshRenderer.cs`, `Light.cs`, `Canvas.cs`, `SpriteRenderer.cs`, `TextRenderer.cs`, `MipMeshFilter.cs`, `UI/UIInputField.cs`, `UI/UIText.cs`, `Collider.cs`, `Collider2D.cs`, `Rigidbody.cs`, `CliCommandDispatcher.cs`

#### D-1. (C5) `_all*` 리스트를 스냅샷 헬퍼로 감싸기

- 각 컴포넌트 클래스에 `public static IReadOnlyList<T> Snapshot()` 정적 메서드 추가.
- 내부 구현: `lock (_allLock) { return _all<T>.ToArray(); }`.
- 외부 순회(렌더 시스템, AssetDatabase.Reimport, CLI) 모두 `Snapshot()` 사용하도록 변경.
- `Add`/`Remove`도 `lock (_allLock)`으로 보호.
- **메인 스레드 불변식**: 라이프사이클 경로(`OnAddedToGameObject`/`OnComponentDestroy`)에 `ThreadGuard.DebugCheckMainThread("XxxRenderer.Register")` 삽입.

**공통 유틸 제안**: `RoseEngine.ComponentRegistry<T>` 제네릭 헬퍼 도입 (선택).
- `Register(T)`, `Unregister(T)`, `Snapshot()`를 제공.
- 각 컴포넌트는 `private static readonly ComponentRegistry<MeshRenderer> _registry = new()` 만 두면 됨.
- 중복 코드 제거 + lock 일관성 확보.

#### D-2. (C4) `RoseMetadata.OnSaved` 이벤트 thread-safe 래퍼

- 선택지 1: `event Action<string>?` → **explicit add/remove + 전용 lock**.
  ```csharp
  private static Action<string>? _onSaved;
  private static readonly object _onSavedLock = new();
  public static event Action<string>? OnSaved
  {
      add { lock (_onSavedLock) _onSaved += value; }
      remove { lock (_onSavedLock) _onSaved -= value; }
  }
  ```
- Invoke 시: `Action<string>? copy; lock (_onSavedLock) copy = _onSaved; copy?.Invoke(path);`
- **추가**: Invoke 호출 스레드가 FSW/백그라운드일 수 있으므로, 핸들러 내부에서 메인 작업이 필요하면 핸들러 쪽에서 큐잉 (Phase B-5와 연동).

#### D-3. (C5, H5) CLI 명령 감사 — 모든 씬/에셋 접근 경로가 `ExecuteOnMainThread` 내부에 있는지 확인

- `CliCommandDispatcher`의 모든 핸들러를 훑어 `SceneManager.AllGameObjects`, `_all*` 참조가 `ExecuteOnMainThread` 람다 안에 있는지 검증.
- `ping` 같은 순수 명령은 예외로 유지하되, 주석에 "씬 접근 금지" 명시.
- 새 핸들러 추가 시 강제하기 위해 `CliCommandDispatcher`에 helper `string DispatchSceneCommand(Func<string> action)` 도입 고려.

**의존 Phase**: A
**예상 리스크**: 높음 — `_all*` 리스트를 사용하는 호출자가 15+곳. 각 파일별 세밀한 수정 필요.
**리뷰 체크리스트**:
- [ ] 모든 `_all*` 리스트가 lock으로 보호되는가?
- [ ] 외부 순회가 `Snapshot()`을 사용하는가?
- [ ] `OnSaved`가 explicit add/remove 패턴인가?
- [ ] Invoke 전에 로컬 변수로 복사하는가?
- [ ] CLI 핸들러 중 씬 접근이 `ExecuteOnMainThread` 밖에 있는 것이 없는가?
- [ ] 라이프사이클에 `DebugCheckMainThread` 삽입되었는가?

**스모크 테스트**:
1. 씬 로드/저장 반복 + CLI `scene.list` 동시 실행.
2. Reimport 중 `delete.gameobject` 호출.
3. Prefab 인스턴스화 반복 (100회).

---

### Phase E: 나머지 (Animator / Physics / PlayerPrefs / Texture2D / EditorAssetSelection)

**다루는 이슈**: **H3, M1, M2, M3, M4**

#### E-1. (H3) `Animator._targets` 순회 보호

**파일**: `src/IronRose.Engine/RoseEngine/Animator.cs`

- `SampleAt`, `CapturePreviewSnapshot`도 `Volatile.Read(ref _targets)` → 로컬 → 로컬로 순회.
- `PropertyTarget` 내부 참조(`clip` 등) 교체 race 방지:
  - `_targets` 재빌드가 필요한 필드(`clip`, `controller`)의 setter에서 `Volatile.Write(ref _targets, null)` 호출 확인.
  - `Parallel.For` 내부에서 `target == null || target.Evaluator == null` 체크 추가하여 NRE 방어.

#### E-2. (M3) `PhysicsWorld3D` 불변식 문서화 + Assert

**파일**: `src/IronRose.Physics/PhysicsWorld3D.cs`

- `Flush()` 진입에 `ThreadGuard.CheckMainThread("PhysicsWorld3D.Flush")` 삽입.
- `RecordContact`는 BepuPhysics worker에서 호출될 수 있으므로 계속 lock 사용.
- 클래스 주석에 "Flush는 Physics Step 종료 후 메인에서만 호출" 명시.

#### E-3. (M1) `PlayerPrefs.Save` lock 길이 단축

**파일**: `src/IronRose.Engine/RoseEngine/PlayerPrefs.cs`

- 변경: lock 안에서는 **config snapshot**(Dictionary 복사)만 수행. 파일 쓰기는 lock 밖에서.
- 또는 "더티 마킹 + 백그라운드 디바운스 라이터" 패턴: lock은 dict만 보호, 별도 Task가 주기적으로 디스크에 flush.
- 둘 중 **snapshot 후 lock 밖 쓰기**가 변경 범위 작아 권장.

#### E-4. (M2) `Texture2D.DefaultNormal`/`DefaultMRO` race-safe lazy init

**파일**: `src/IronRose.Engine/RoseEngine/Texture2D.cs`

- `??=` → `Lazy<Texture2D>` with `LazyThreadSafetyMode.ExecutionAndPublication`.
- 또는 `Texture2D`가 메인 전용 생성이면 getter에 `ThreadGuard.CheckMainThread(...)` 삽입 후 동작 허용 (double-create만 방어하면 됨).

#### E-5. (M4) `EditorAssetSelection` 런타임 가드

**파일**: `src/IronRose.Engine/Editor/EditorAssetSelection.cs`

- public API 진입에 `ThreadGuard.CheckMainThread("EditorAssetSelection.Xxx")` 삽입.
- 주석의 "메인 전용"을 런타임 체크로 승격.

**의존 Phase**: A (E-2, E-4, E-5에서 ThreadGuard 사용)
**예상 리스크**: 낮음 — 각 영역이 독립적. 병렬 작업 가능.
**리뷰 체크리스트**:
- [ ] Animator SampleAt/CapturePreviewSnapshot가 Volatile.Read 사용?
- [ ] PhysicsWorld3D.Flush에 ThreadGuard 삽입?
- [ ] PlayerPrefs.Save가 파일 I/O를 lock 밖에서 수행?
- [ ] Texture2D.DefaultNormal/DefaultMRO가 thread-safe lazy?
- [ ] EditorAssetSelection 진입점에 ThreadGuard 삽입?

**스모크 테스트**:
1. 에디터 Animator 프리뷰 + Inspector에서 clip 교체 반복.
2. 플레이모드 중 PlayerPrefs.Save 대량 호출.
3. 큰 씬 로드 중 PostProcessVolume 추가/삭제.

---

### Phase F: CLAUDE.md 가이드 + 통합 스모크 테스트

**다루는 이슈**: (없음 — 재발 방지)

#### F-1. CLAUDE.md에 "스레드 안전 규칙" 섹션 추가

아래 초안을 `## 엔진/에디터 우선 개선 원칙` 바로 아래에 삽입 (본 문서 §7 참조).

#### F-2. 통합 스모크 테스트 체크리스트 작성

- `plans/archive/threading-safety-smoke-checklist.md` (또는 QA 문서).
- Phase B~E의 개별 스모크 테스트를 합쳐 에디터 시작 → 프로젝트 전체 반복 씬 로드 → CLI 부하 → Reimport 부하 → 종료까지 한 번에 수행하는 시나리오.

**의존 Phase**: B, C, D, E 전체 완료
**예상 리스크**: 없음 (문서화).
**리뷰 체크리스트**:
- [ ] CLAUDE.md에 섹션이 명확하게 추가되었는가?
- [ ] 스모크 체크리스트가 실행 가능한 수준으로 구체적인가?

---

## 영향 범위

### 신규 파일
- `src/IronRose.Contracts/ThreadGuard.cs` (또는 `src/IronRose.Engine/RoseEngine/ThreadGuard.cs`)
- (선택) `src/IronRose.Engine/RoseEngine/ComponentRegistry.cs` — Phase D 공통 헬퍼

### 수정 파일 (주요)
- `src/IronRose.Engine/EngineCore.cs` (ThreadGuard 캡처)
- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` (Phase B 대부분)
- `src/IronRose.Engine/AssetPipeline/RoseMetadata.cs` (C4)
- `src/IronRose.Engine/AssetPipeline/GpuTextureCompressor.cs`, `Bc6hEncoder.cs` (H6)
- `src/IronRose.Engine/Cli/CliPipeServer.cs` (C6, H1)
- `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` (C3, C5)
- `src/IronRose.Engine/RoseEngine/Animator.cs` (H3)
- `src/IronRose.Engine/RoseEngine/PlayerPrefs.cs` (M1)
- `src/IronRose.Engine/RoseEngine/Texture2D.cs` (M2)
- `src/IronRose.Engine/RoseEngine/SceneManager.cs` (C5)
- 15+ 컴포넌트 파일 (`_all*` 리스트 보유자) — 리스트는 §현재상태 표 참조
- `src/IronRose.Physics/PhysicsWorld3D.cs` (M3)
- `src/IronRose.Engine/Editor/EditorAssetSelection.cs` (M4)
- `CLAUDE.md` (Phase F)

### 기존 기능에 미치는 영향
- 런타임 동작 변화: ThreadGuard 위반 시 `EditorDebug.LogError`로 새 로그가 찍힘 (사용자 행동 변화 없음).
- 성능: `lock` 증가로 마이크로 오버헤드. 핫 패스는 `DebugCheckMainThread`로 Release에서 제거.
- CLI: `Stop()` 경로 변경으로 에디터 종료 시간이 최대 Join timeout만큼 증가 가능 (현재 3초 → 제안 10초 safety).

---

## 보호장치 정책 (요약)

### throw 금지 원칙
- **`ThreadGuard.CheckMainThread`는 절대 throw하지 않는다.** `EditorDebug.LogError` + `bool false` 반환.
- 호출자는 false 반환 시 해당 작업을 **스킵하거나 안전한 fallback**을 수행.
- 예: GPU API 호출 시 위반 감지되면 호출을 스킵하고 null 반환 → 호출자는 다음 프레임 메인에서 재시도 큐잉.

### 로그 홍수 방지
- `ThreadGuard`는 `context` 문자열 단위로 **5초 쿨다운** (`ConcurrentDictionary<string, long>`).
- 매 프레임 호출되는 지점(예: 렌더 enum)도 동일 context면 첫 발생만 로그.
- 시작 시점 한 번만 찍히도록 `[Conditional("DEBUG")]` 바리에이션(`DebugCheckMainThread`)을 우선 사용.

### GPU 보호 전략
- **1차**: 진입 지점에 `ThreadGuard.CheckMainThread` → 위반 시 **호출 스킵**, 호출자는 `AssetDatabase`의 defer 큐에 enqueue하여 메인에서 재시도.
- **2차**: 장기적으로 GPU 호출을 `_mainThreadQueue` 스타일로 항상 큐잉하도록 리팩토링 (Phase 범위 밖 — 후속 개선 제안).
- **Veldrid `GraphicsDevice`**: 직접 lock을 걸기보다, "메인 전용" 불변식을 ThreadGuard로 enforce.

### 재진입 방어
- `ExecuteOnMainThread` 진입 시 `ThreadGuard.IsMainThread == true`면 재귀 실행 금지 (자기 대기 방지).
- 대신 action을 직접 호출 (이미 메인이므로 안전).

---

## CLAUDE.md 추가 섹션 초안

아래를 `## 엔진/에디터 우선 개선 원칙` 아래에 삽입 (Phase F에서 적용).

```markdown
---

## 스레드 안전 규칙

IronRose는 메인 + 백그라운드 Task + CLI 서버 + FileSystemWatcher 스레드로 돌아간다. 다음 규칙을 어기면 바로 race/crash로 이어진다.

### 절대 금지
- **`Task.Run` 람다 안에서 GPU API(`GraphicsDevice`, `Veldrid.*`, `Texture2D` 생성), 씬 라이프사이클(`GameObject.Instantiate`/`Destroy`), 에셋 자료구조(`_loadedAssets`, `_all*` 리스트) 수정**. 순수 계산(파일 I/O, CPU 디코드, 인코드)만 허용.
- **`FileSystemWatcher` 콜백에서 공유 상태를 직접 수정**. 반드시 `_pendingChanges` 큐 또는 동등한 큐에 enqueue 후 메인에서 pull.
- **CLI 핸들러에서 씬/에셋 접근은 `ExecuteOnMainThread` 밖에서 수행**. 순수 조회(`ping`, 로그 출력)만 예외.
- **`_all*` 정적 리스트를 락 없이 Add/Remove/순회**. 반드시 `Snapshot()` 또는 `lock (_allLock)` 사용.
- **`static event`를 lock 없이 `+=`/`-=`/`Invoke`**. Invoke 시 로컬 복사 후 호출.

### 필수 관례
- 새 다중 스레드 코드 작성 시 메인 전용 메서드에는 `ThreadGuard.CheckMainThread("Class.Method")`를 진입 직후에 삽입. 위반 시 로그만 남기고 false 반환 (throw 없음).
- GPU 리소스 생성/파괴 지점에는 반드시 `ThreadGuard.CheckMainThread(...)` 삽입.
- 락 보유 상태에서 파일 I/O, 네트워크 호출, 다른 락 획득 금지 (데드락/장기 정체 원인).
- Task 결과는 **단일 불변 객체**로 전달. 개별 필드에 대한 happens-before 가정 금지.

### 권장 패턴
- 백그라운드 Task → 메인 전달: `Task<Result>` 반환 + 메인 `Update`에서 `IsCompleted` 체크.
- 컴포넌트 `_all*` 리스트 순회: `Snapshot()` → foreach.
- CLI 씬 명령: `ExecuteOnMainThread(() => { /* 씬 접근 */ })`.

의심스러우면 `ThreadGuard`를 먼저 심고 로그를 본다.
```

---

## 대안 검토

### 대안 1: 전역 `GameLoopLock` 하나로 모든 공유 상태 보호
- 장점: 구현 단순.
- 단점: 메인 스레드가 매 틱 락 취득 → 처리량 저하. 백그라운드 Task가 길게 보유 시 메인 stall.
- **기각**: 본 계획의 "경계별 보호"가 더 세분화되고 성능 친화적.

### 대안 2: 모든 백그라운드 작업을 `TaskScheduler.FromCurrentSynchronizationContext`로 메인에 재전파
- 장점: 메인 동기화 자동.
- 단점: 메인 스레드에 SynchronizationContext가 없는 엔진 루프 구조라 도입 비용이 큼. 기존 `_mainThreadQueue`와 중복.
- **기각**: 기존 `_mainThreadQueue` 패턴을 유지하고 다듬는 것이 저비용.

### 대안 3: `ThreadGuard`를 throw 모드로 동작 (Debug.Assert)
- 장점: 개발 중 즉시 실패로 버그 빠르게 발견.
- 단점: 유저 요구사항과 배치됨 — "크래시/데드락 방지, graceful 처리" 필수.
- **기각**: 로그 + 스킵으로 설계 확정.

---

## 미결 사항

1. **`ThreadGuard` 파일 위치**: `IronRose.Contracts` vs `IronRose.Engine/RoseEngine/`. Contracts 쪽이 의존성 역전상 올바르지만, `EditorDebug`와 같은 레이어면 Engine 쪽도 허용 가능. → Phase A 상세 명세서에서 최종 결정.
2. **`_all*` 리스트 락 전략**: 각 컴포넌트별 개별 lock vs 공통 `ComponentRegistry<T>` 헬퍼 — Phase D 상세 명세서에서 결정.
3. **`PlayerPrefs` 파일 I/O 전략**: snapshot-and-write vs 백그라운드 디바운스 라이터 — Phase E 상세 명세서에서 유저 의견 필요.
4. **`CliPipeServer.Stop` Join timeout**: 3초 유지 vs 10초로 완화 — Phase C 상세 명세서에서 확정.
5. **Veldrid 백그라운드 큐잉**: 2차 개선(장기)은 본 계획 범위 밖. 별도 후속 plan으로 분리.

---

## 점검 순서 / 검증 시나리오

### Phase별 수동 스모크 테스트

| Phase | 시나리오 | 기대 결과 |
|-------|----------|-----------|
| A | 에디터 시작 + 콘솔 확인 | ThreadGuard 로그 없음 (위반 없음) |
| B | 텍스처/메시 Reimport 10회 + 외부 편집 겹침 | 로그 오염 없음, 씬 참조 오염 없음 |
| C | 에디터 시작↔종료 10회, CLI 장기 요청 중 종료 | ODE 없음, 3~10초 내 종료 |
| D | 씬 저장/로드 반복 + CLI `scene.list` 동시 실행 | 예외 없음, 일관된 리스트 |
| E | Animator 프리뷰 중 clip 교체, PlayerPrefs 대량 저장 | NRE 없음, 프레임 드랍 최소 |
| F | 위 네 시나리오 통합 1시간 runtime | 장기 stable, 로그에 `[ThreadGuard]` 에러 없음 |

### 회귀 테스트 공통 체크
- 에디터 시작 / 종료
- 에셋 Reimport (큰 텍스처, 큰 메시)
- 플레이모드 진입 / 종료
- 씬 저장 / 로드
- CLI 명령 (list/inspect/set)
- 핫리로드 (스크립트 변경 후)
- 외부 에디터로 에셋 수정 + 메타 저장

### 빌드 검증
- Debug 빌드: `ThreadGuard.DebugCheckMainThread`가 동작.
- Release 빌드: `DebugCheckMainThread`는 no-op, `CheckMainThread`만 살아있음.
- `dotnet build` 실패 없음.

---

## 다음 단계

**→ `aca-archi`로 Phase A 상세 명세서 작성**

Phase A(`ThreadGuard` 인프라)를 먼저 명세서화하여 `aca-coder`가 바로 구현 가능한 수준으로 분해할 것. Phase A가 머지된 후 B~E를 순차(또는 병렬) 진행.

각 Phase별 `aca-archi` 호출 시 다음을 참조로 포함:
- 본 문서: `plans/threading-safety-fix-master.md`
- 정적 분석: `plans/static-analysis-threading-race-deadlock.md`
- 관련 소스 파일 경로 (본 문서 §영향 범위 참조)

Phase A 명세서는 다음을 포함해야 한다:
- `ThreadGuard` 파일 위치 최종 결정 (Contracts vs Engine/RoseEngine)
- 전체 소스 코드 (쿨다운 로직 포함)
- `EngineCore.Initialize` 수정 diff
- 유닛 테스트 또는 수동 검증 스텝
- Worktree 브랜치명 제안 (예: `feat/thread-guard`)
