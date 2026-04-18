# Phase A: ThreadGuard 인프라

## 목표

- `RoseEngine.ThreadGuard` 정적 유틸리티를 도입하여, 이후 Phase들(B~E)이 **"이 API는 메인 스레드에서만 호출되어야 한다"** 를 런타임에 검증할 수 있는 공통 기반을 마련한다.
- `EngineCore.Initialize(IWindow)` 진입 직후 메인 스레드 ID를 캡처하여, 이후 어떤 스레드에서 호출되든 `IsMainThread` / `CheckMainThread(context)` 로 확인 가능하게 한다.
- 위반 감지 시 **throw 없이** `EditorDebug.LogError`만 기록하고 호출 측이 안전하게 fallback 할 수 있도록 `bool` 을 반환한다 (데드락/크래시 회피).
- Phase A 자체는 아무도 `CheckMainThread`를 아직 호출하지 않으므로, 런타임 동작은 기존과 **완전히 동일**해야 한다 (신규 코드만 추가).

## 선행 조건

- 없음. 이 Phase가 마스터 계획상 최초 선행 인프라.

## 배경 (aca-coder는 다른 문서를 읽을 필요가 없도록)

- `EditorDebug`는 `RoseEngine` 네임스페이스에 있으며, 위치는 `src/IronRose.Contracts/EditorDebug.cs` 이다.
- `EditorDebug.LogError(object message)` 시그니처: 단일 `object` 인자. 내부에서 `Console.WriteLine` + 로그 파일 append + `LogSink?.Invoke(...)` 를 수행한다.
- `IronRose.Contracts.csproj` 는 `TargetFramework=net10.0`, `ImplicitUsings=enable`, `Nullable=enable`. 외부 의존 없음 (Contracts는 최하위 레이어).
- `EngineCore.cs` 는 이미 line 48에 `using RoseEngine;` 선언이 있으므로, `ThreadGuard.CaptureMainThread()` 호출에 using 추가는 필요 없다.
- 기존 파일들의 주석 컨벤션: 파일 상단에 `// ------...` 로 둘러싸인 `@file / @brief / @deps / @exports / @note` 블록이 있다. 신규 파일도 동일 포맷을 따른다.
- 네임스페이스 중괄호는 **블록 스타일** (`namespace RoseEngine { ... }`) 을 따른다 (기존 `EditorDebug.cs` 참고).

## 생성할 파일

### `src/IronRose.Contracts/ThreadGuard.cs`

- **역할**: 엔진 전역 메인 스레드 검증 유틸리티. `CaptureMainThread()` 로 메인 스레드 ID를 한 번 기록하고, 이후 어느 스레드에서든 `IsMainThread` / `CheckMainThread(context)` 로 확인한다.
- **클래스**: `public static class ThreadGuard` (네임스페이스: `RoseEngine`)
- **주요 멤버 (확정 시그니처)**:
  - `public static void CaptureMainThread()` — 메인 스레드 ID를 내부 static 필드에 기록. `EngineCore.Initialize(IWindow)` 진입 직후 1번째 라인에서 호출된다.
  - `public static int MainThreadId { get; }` — 캡처된 메인 스레드 `ManagedThreadId`. 아직 캡처되지 않았으면 `-1`.
  - `public static bool IsMainThread { get; }` — 현재 스레드가 메인 스레드인지 (캡처 전이면 `false`).
  - `public static bool CheckMainThread(string context)` — 위반 시 `EditorDebug.LogError` 로 로그 후 `false` 반환. 메인이면 `true`. 캡처 전(`_mainThreadId == -1`)이면 체크 스킵하고 `true` 반환. **절대 throw 하지 않는다.**
  - `[Conditional("DEBUG")] public static void DebugCheckMainThread(string context)` — Debug 빌드에서만 `CheckMainThread` 호출, Release 빌드에서는 no-op. 핫 패스 전용.
- **의존**: 같은 어셈블리의 `RoseEngine.EditorDebug` (동일 네임스페이스이므로 using 불필요).
- **구현 힌트**:
  - 내부 필드: `private static int _mainThreadId = -1;`
  - 쿨다운 자료구조: `private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _lastLogTicks = new();`
  - 쿨다운 상수: `private const long LogCooldownTicks = TimeSpan.TicksPerSecond * 5;`
  - 쿨다운 판정: `DateTime.UtcNow.Ticks` 를 사용. 같은 `context` 문자열이면 첫 발생만 로그, 이후 5초간 억제. 이후 갱신은 `_lastLogTicks[context] = now;` (indexer 사용. `AddOrUpdate` 대신 indexer 로 간결하게).
  - 로그 포맷 (**정확히 이 형식**):
    ```
    [ThreadGuard] {context} must be called on main thread (called from thread {cur}, main={_mainThreadId}). Continuing in unsafe mode.
    ```
  - 캡처 전이면 (`_mainThreadId == -1`) 체크 스킵하고 `true` 반환 — 초기화 전 호출 허용.
  - 중괄호 스타일은 `EditorDebug.cs` 와 동일하게 new-line 중괄호.

**전체 소스 코드 (그대로 복붙 가능)**:

```csharp
// ------------------------------------------------------------
// @file    ThreadGuard.cs
// @brief   엔진 전역 메인 스레드 검증 유틸리티. CaptureMainThread()로 메인 스레드
//          ID를 기록하고, CheckMainThread(context)로 호출 스레드가 메인인지 검증한다.
// @deps    RoseEngine/EditorDebug
// @exports
//   static class ThreadGuard
//     CaptureMainThread(): void                              -- 메인 스레드 ID 기록 (엔진 초기화 시 1회)
//     MainThreadId: int                                      -- 캡처된 메인 스레드 ID (없으면 -1)
//     IsMainThread: bool                                     -- 현재 스레드가 메인인지
//     CheckMainThread(string context): bool                  -- 검증. 위반 시 LogError 후 false 반환
//     DebugCheckMainThread(string context): void             -- Debug 빌드에서만 체크, Release는 no-op
// @note    throw 금지: 위반 감지 시 EditorDebug.LogError만 호출하고 false 반환한다.
//          호출자는 반환값을 보고 안전하게 fallback 할 수 있다 (데드락/크래시 회피).
//          동일 context 문자열은 5초 쿨다운으로 로그 홍수를 방지한다 (ConcurrentDictionary 기반).
//          _mainThreadId == -1 (캡처 전) 상태에서는 체크를 스킵하고 true 를 반환한다.
// ------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace RoseEngine
{
    public static class ThreadGuard
    {
        private static int _mainThreadId = -1;
        private static readonly ConcurrentDictionary<string, long> _lastLogTicks = new();
        private const long LogCooldownTicks = TimeSpan.TicksPerSecond * 5;

        /// <summary>
        /// 메인 스레드에서 1회 호출하여 해당 스레드의 ManagedThreadId를 기록한다.
        /// EngineCore.Initialize(IWindow) 진입 직후 최상단에서 호출된다.
        /// </summary>
        public static void CaptureMainThread()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>캡처된 메인 스레드 ID. 아직 캡처되지 않았으면 -1.</summary>
        public static int MainThreadId => _mainThreadId;

        /// <summary>현재 스레드가 메인 스레드인지 여부 (캡처 전에는 false).</summary>
        public static bool IsMainThread =>
            _mainThreadId != -1 && Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>
        /// 현재 호출이 메인 스레드에서 발생했는지 검증한다.
        /// 위반 시 EditorDebug.LogError로 기록하고 false를 반환한다. throw 하지 않는다.
        /// context는 call site 식별자(예: "AssetDatabase.Reimport").
        /// 동일 context는 5초간 중복 로그가 억제된다.
        /// _mainThreadId == -1 (캡처 전)이면 체크를 스킵하고 true를 반환한다.
        /// </summary>
        public static bool CheckMainThread(string context)
        {
            if (_mainThreadId == -1) return true;
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId) return true;

            var now = DateTime.UtcNow.Ticks;
            if (_lastLogTicks.TryGetValue(context, out var last) && (now - last) < LogCooldownTicks)
                return false;
            _lastLogTicks[context] = now;

            EditorDebug.LogError(
                $"[ThreadGuard] {context} must be called on main thread " +
                $"(called from thread {Thread.CurrentThread.ManagedThreadId}, " +
                $"main={_mainThreadId}). Continuing in unsafe mode.");
            return false;
        }

        /// <summary>Debug 빌드에서만 CheckMainThread를 호출한다. Release 빌드에서는 no-op.</summary>
        [Conditional("DEBUG")]
        public static void DebugCheckMainThread(string context) => CheckMainThread(context);
    }
}
```

## 수정할 파일

### `src/IronRose.Engine/EngineCore.cs`

- **변경 내용**: `Initialize(IWindow window)` 메서드 진입 직후 **1번째 라인** 에 `ThreadGuard.CaptureMainThread();` 호출을 삽입한다.
- **위치 (Grep 확인 완료)**: 현재 line 139-142:
  ```csharp
  public void Initialize(IWindow window)
  {
      // CLI 로그 버퍼 생성 (LogSink 연결 전에 생성)
      _cliLogBuffer = new CliLogBuffer();
  ```
  여기서 `{` 직후, 기존 첫 주석/코드 위에 캡처 호출을 추가한다.
- **using 추가 필요 여부**: **불필요**. 현재 파일 line 48에 `using RoseEngine;` 가 이미 존재하므로 `ThreadGuard` 타입이 수식 없이 보인다.
- **정확한 diff (Edit 도구 파라미터 그대로 사용 가능)**:

  - `old_string`:
    ```
            public void Initialize(IWindow window)
            {
                // CLI 로그 버퍼 생성 (LogSink 연결 전에 생성)
                _cliLogBuffer = new CliLogBuffer();
    ```
  - `new_string`:
    ```
            public void Initialize(IWindow window)
            {
                // 메인 스레드 ID 캡처 — 이후 모든 ThreadGuard.CheckMainThread 호출의 기준
                ThreadGuard.CaptureMainThread();

                // CLI 로그 버퍼 생성 (LogSink 연결 전에 생성)
                _cliLogBuffer = new CliLogBuffer();
    ```
- **이유**: 이후 Phase(B: AssetDatabase, C: Rendering, D: Physics, E: Editor) 들이 `ThreadGuard.CheckMainThread(...)` 를 호출할 수 있으려면, 가장 이른 시점에 메인 스레드 ID가 캡처되어야 한다. `Initialize` 진입 직후가 엔진 초기화 경로의 첫 지점이다.

## NuGet 패키지

- 없음. `System.Collections.Concurrent`, `System.Threading`, `System.Diagnostics` 는 모두 .NET 10 BCL에 포함되어 있다.

## 검증 기준

### 빌드 검증

- [ ] `dotnet build src/IronRose.Contracts/IronRose.Contracts.csproj` — Contracts 단독 빌드 성공 (warning/error 0).
- [ ] `dotnet build` — 솔루션 전체 빌드 성공 (warning/error 0).
- [ ] Release 구성 빌드도 성공: `dotnet build -c Release` (warning/error 0).
  - `[Conditional("DEBUG")]` 가 Release 에서 no-op 이 되는지 검증용.

### 수동 동작 검증

- [ ] 에디터 실행 → 정상 시작 → 정상 종료.
- [ ] 로그 파일 / 콘솔에 `[ThreadGuard]` 문자열이 **한 번도 나타나지 않아야** 한다 (이번 Phase 에서는 아무도 `CheckMainThread` 를 호출하지 않으므로).
- [ ] 기존 동작 회귀 없음: 씬 로드, 에셋 리임포트, 플레이 모드 전환 등 일상 워크플로우가 Phase 이전과 동일하게 작동.
- [ ] Release 빌드로 실행했을 때도 정상 시작 / 종료.

## Worktree 브랜치명

`feat/phase-a-thread-guard`

## 리뷰 체크리스트 (aca-code-review 용)

- [ ] 파일 위치가 정확히 `src/IronRose.Contracts/ThreadGuard.cs` 인가.
- [ ] 네임스페이스가 `RoseEngine` 인가 (`EditorDebug` 와 동일).
- [ ] `CheckMainThread` 가 **어떤 경로에서도 throw 하지 않는가** (try/catch 필요 없음 — 모든 연산이 non-throwing).
- [ ] 쿨다운이 `ConcurrentDictionary<string, long>` 기반으로 lock-free / thread-safe 한가.
- [ ] 쿨다운 키가 `context` 문자열이며, 동일 context 는 5초간 로그가 억제되는가.
- [ ] `_mainThreadId == -1` 일 때 `CheckMainThread` 가 `true` 를 반환 (캡처 전 호출 허용) 하는가.
- [ ] `CaptureMainThread()` 가 `EngineCore.Initialize(IWindow)` 진입 직후 **첫 번째 실행 문장** 인가 (기존 `_cliLogBuffer = new CliLogBuffer();` 앞).
- [ ] `EditorDebug.LogError` 호출 시 메시지 문자열이 기존 로그 컨벤션 (`[Prefix] ...`) 과 일치하는가 — 여기서는 `[ThreadGuard]` prefix 사용.
- [ ] `LogError` 인자가 단일 `object` (문자열) 로 올바르게 전달되는가 — `EditorDebug.LogError(object message)` 시그니처 준수.
- [ ] `[Conditional("DEBUG")]` 속성이 `DebugCheckMainThread` 에 적용되어 있는가. Release 빌드에서 해당 호출이 IL 에서 제거됨.
- [ ] `using` 선언에 불필요한 것이 없는가 (`System`, `System.Collections.Concurrent`, `System.Diagnostics`, `System.Threading` 만 사용).
- [ ] Debug / Release 양쪽 빌드 성공 (warning 0, error 0).
- [ ] 파일 상단 주석 블록이 프로젝트 컨벤션 (`// ------`, `@file / @brief / @deps / @exports / @note`) 을 따르는가.
- [ ] 주석이 한국어로 작성되어 있는가 (기존 엔진 코드 컨벤션 일치).
- [ ] 에디터 실행 시 `[ThreadGuard]` 로그가 출력되지 않는가 (아직 아무도 `CheckMainThread` 를 호출하지 않으므로 침묵 상태여야 함).

## 참고

- 본 Phase 는 **신규 API 도입만** 하며, 어떤 기존 코드에도 `CheckMainThread` 호출을 추가하지 않는다. 실제 활용은 Phase B 이후.
- `EditorDebug.LogError` 는 내부에서 `LogSink` 를 호출하여 에디터 콘솔 패널에 빨간 로그로 뜰 수 있다. Phase A 자체는 호출하지 않으므로 문제 없지만, 이후 Phase 에서 `[ThreadGuard]` 로그가 에디터 콘솔에 빨간 에러로 표시될 수 있음을 염두에 둔다.
- `DateTime.UtcNow.Ticks` 는 OS 타이머 해상도 이슈가 있으나, 5초 쿨다운 판정 용도로는 충분하다. `Stopwatch.GetTimestamp()` 로 교체하지 않는다 (마스터 계획과 시그니처 일치).
- `CheckMainThread` 가 재귀적으로 `EditorDebug.LogError` → `LogSink` → (어떤 Phase 에서 또 `CheckMainThread` 호출?) 로 이어질 가능성은, 본 Phase 시점에서는 없다. Phase B 이후 `LogSink` 경로에 `CheckMainThread` 를 넣지 않도록 주의해야 한다 (무한 재귀 위험).
- Phase B/C/D/E 에서 `CheckMainThread` 를 광범위하게 삽입했을 때 로그 스팸이 발생할 수 있으나, 5초 쿨다운으로 완화된다. 쿨다운이 너무 길다고 판단되면 후속 Phase 에서 `LogCooldownTicks` 상수를 조정한다.

## 미결 사항

- 없음. 모든 결정 사항이 본 명세서에 포함됨.
