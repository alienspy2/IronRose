# Phase C: ClaudeManager 신설 + FeedbackPanel 이관

## 목표

`ImGuiFeedbackPanel.StartFix()`의 `claude -p` 프로세스 실행 및 stream-json 파싱 로직을 신규 `ClaudeManager` 정적 클래스로 이관한다. `EnableClaudeUsage`가 false일 때 Fix 관련 UI(버튼/Stop/출력 영역)를 렌더링하지 않으며, `ClaudeManager` API 자체도 호출을 거부한다. Preferences UI는 아직 없고(Phase D) 기본값이 false이므로 이 phase 완료 시점에서 Fix 기능은 기본 숨김 상태가 된다.

## 선행 조건

- Phase A 완료 (`EditorPreferences.EnableClaudeUsage` 존재).
- Phase B는 독립적이므로 Phase A만 선행돼 있으면 됨. 단 실제 구현은 A→B→C 순서가 안전.

## 생성할 파일

### `src/IronRose.Engine/Editor/ClaudeManager.cs`

- **역할**: Claude 연동 호출의 단일 진입점. 현재는 `aca-fix` 스트리밍 호출 하나만 지원.
- **네임스페이스**: `IronRose.Engine.Editor`
- **타입 정의**:
  - `public sealed class ClaudeSession : IDisposable`
  - `public static class ClaudeManager`

- **`ClaudeSession` 멤버**:
  - `internal System.Diagnostics.Process? Process { get; set; }`
  - `public volatile bool IsRunning;` (또는 `public bool IsRunning { get; internal set; }` + volatile 필드 백킹)
  - `private readonly System.Text.StringBuilder _output = new();`
  - `private readonly object _lock = new();`
  - `private bool _outputDirty;`
  - `internal void AppendOutput(string text)` — lock 하에 `_output.Append(text)` + `_outputDirty = true`.
  - `public string SnapshotOutput(out bool dirtyConsumed)` — lock 하에 `dirtyConsumed = _outputDirty`, dirty면 `_outputDirty = false` 세팅하고 `_output.ToString()` 반환. 아니면 null/빈 문자열.
  - `public void Stop()` — `Process`가 존재하고 `!HasExited`면 `Kill(entireProcessTree: true)`. try/catch로 예외 무시.
  - `public void Dispose()` — `Stop()` 호출 후 `Process?.Dispose()`, `Process = null`.

- **`ClaudeManager` 멤버**:
  - `public static bool IsEnabled => EditorPreferences.EnableClaudeUsage;`
  - `public static ClaudeSession? StartFix(string prompt, string workDir)`:
    - `if (!IsEnabled) { EditorDebug.LogWarning("[ClaudeManager] Disabled by preferences."); return null; }`
    - 새 `ClaudeSession` 생성, `IsRunning = true`.
    - `Task.Run(() => { ... })` 내부에 현재 `ImGuiFeedbackPanel.StartFix` line 344~400의 프로세스 실행 로직을 그대로 이관:
      - `ProcessStartInfo { FileName = "claude", Arguments = "-p --verbose --output-format stream-json", RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir }`
      - `Process.Start` 실패 시 `session.AppendOutput("[Error] Failed to start claude process.\n")`, `IsRunning = false`, return.
      - `StandardInput.Write(prompt)` + `Close()`.
      - 별도 Task로 `StandardError.ReadToEnd()` 수집.
      - `while ((line = StandardOutput.ReadLine()) != null) ProcessStreamLine(session, line);`
      - `WaitForExit()`, stderr task 5초 wait, 비정상 종료 + stderr 존재 시 AppendOutput.
    - `finally`에서 `session.IsRunning = false;`, Process는 session이 Dispose될 때 정리.
    - 세션 객체 리턴 (Task.Run 전에).
  - `private static void ProcessStreamLine(ClaudeSession session, string line)` — 현재 `ImGuiFeedbackPanel.ProcessStreamLine` (line 429~491) 로직 그대로 이관:
    - `content_block_delta` → `session.AppendOutput(deltaText)`
    - `assistant` 메시지 content 배열 → text 블록 AppendOutput
    - `result` → 빈 경우에만 result 문자열 AppendOutput
    - JSON 파싱 실패 시 raw line + "\n" AppendOutput
  - 명령 실행은 **`claude -p --verbose --output-format stream-json`만** 허용. 다른 인자 경로 추가 금지 (plan 섹션 2-1). 파일 상단 `@file` 주석에 명시.

- **의존**: `IronRose.Engine.EditorPreferences`, `System.Diagnostics.Process`, `System.Text.Json.JsonDocument`, `System.Text.StringBuilder`, `System.Threading.Tasks`, `RoseEngine.EditorDebug`.

- **구현 힌트**:
  - `volatile bool IsRunning` 필드 + `public bool IsRunning => _isRunning;` 프로퍼티 조합이 깔끔. 또는 `public volatile bool IsRunning;` 직접 노출도 무방 (기존 패널 구현과 동일 스타일).
  - `Kill(entireProcessTree: true)` 이후 stdout ReadLine 루프가 IOException을 던질 수 있으므로 워커 전체를 `try/catch (Exception ex)`로 감싸고 에러 메시지를 AppendOutput.
  - `ClaudeSession`의 Process 필드는 워커 스레드에서 할당 → 메인 스레드에서 Stop 호출 가능해야 하므로 할당 시점 이후에야 Stop이 안전하다. `session.Process = proc;`을 `Process.Start` 직후 바로 세팅.

## 수정할 파일

### `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiFeedbackPanel.cs`

- **변경 내용**:
  1. **필드 정리** (line 53~60):
     - 제거: `_fixProcess`, `_fixOutput`, `_fixOutputLock`, `_fixRunning`, `_fixOutputDirty`.
     - 유지: `_fixingPath`, `_fixDisplayText`.
     - 추가: `private ClaudeSession? _fixSession;`
  2. **`StartFix(int index)` 교체** (line 317~402):
     ```csharp
     private void StartFix(int index)
     {
         if (!ClaudeManager.IsEnabled) return;
         if (_fixSession != null && _fixSession.IsRunning) return;
         if (index < 0 || index >= _entries.Count) return;

         var entry = _entries[index];
         var engineRoot = ProjectContext.EngineRoot;
         if (string.IsNullOrEmpty(engineRoot) || !Directory.Exists(engineRoot))
         {
             _statusMessage = "Error: EngineRoot path not available.";
             return;
         }

         _fixingPath = entry.FilePath;
         _fixDisplayText = "";
         _fixSession?.Dispose();

         var prompt = $"aca-fix: {entry.Content}";
         _fixSession = ClaudeManager.StartFix(prompt, engineRoot);
         if (_fixSession == null)
         {
             _statusMessage = "Error: Failed to start claude process.";
             _fixingPath = null;
         }
     }
     ```
  3. **`StopFix()` 교체** (line 404~416):
     ```csharp
     private void StopFix()
     {
         _fixSession?.Stop();
     }
     ```
  4. **삭제**: `AppendFixOutput` (line 418~426), `ProcessStreamLine` (line 429~491) — ClaudeManager로 이관됨.
  5. **`DrawFixOutput()` 수정** (line 493~547):
     - 기존 `lock (_fixOutputLock) { if (_fixOutputDirty) { _fixDisplayText = _fixOutput.ToString(); _fixOutputDirty = false; } }`를 교체:
       ```csharp
       if (_fixSession == null) return;
       var snap = _fixSession.SnapshotOutput(out bool dirty);
       if (dirty) _fixDisplayText = snap ?? "";
       ```
     - `_fixRunning`을 `_fixSession.IsRunning`으로 교체 (Running/Completed 분기 라벨, 자동 스크롤 조건 둘 다).
     - Clear 버튼 클릭 시 `_fixSession?.Dispose(); _fixSession = null; _fixingPath = null; _fixDisplayText = "";`.
  6. **`DrawFeedbackList()`의 Fix UI 게이트** (line 103~):
     - Fix/Stop 버튼 블록(line 148~177 전체)을 `if (ClaudeManager.IsEnabled) { ... }`로 감쌈.
     - `DrawFixOutput` 호출(line 180~183)도 `if (ClaudeManager.IsEnabled && _fixingPath == _entries[i].FilePath)`로 조건화.
     - `_fixRunning` 참조(line 150)는 `(_fixSession?.IsRunning ?? false)`로 교체.
  7. **using 추가**: `using IronRose.Engine.Editor;` (ClaudeManager/ClaudeSession 참조). 기존 using 블록 확인 후 없으면 추가.

- **이유**: plan 섹션 2-1, 2-2, 5 — Claude 연동 일원화 + UI 게이트 + API 게이트.

## NuGet 패키지

- 추가 없음.

## 검증 기준

- [ ] `dotnet build` 성공.
- [ ] `EnableClaudeUsage = false` (기본값) 상태에서 Feedback 패널을 열면 Fix/Stop 버튼과 출력 영역이 **전혀 보이지 않음**.
- [ ] 수동으로 `~/.ironrose/settings.toml`에서 `enable_claude_usage = true`로 편집 후 재시작하면 Fix 버튼이 나타나고 실제 Fix 실행 가능.
- [ ] Fix 실행 중 Stop 버튼 동작 정상, 완료 시 "Completed" + Clear 버튼 표시.
- [ ] 여러 feedback 항목이 있을 때 한 번에 하나만 Fix 가능한 제약이 유지됨 (다른 항목 Fix 버튼이 disabled).
- [ ] Clear 버튼 클릭 후 세션/출력이 초기화되어 다시 Fix 실행 가능.

## 참고

- `ClaudeSession` thread-safety는 기존 `ImGuiFeedbackPanel`의 `_fixOutputLock` 패턴을 그대로 유지. UI 스레드는 `SnapshotOutput`만 호출, 워커 스레드는 `AppendOutput`만 호출.
- 호출 API는 `aca-fix` 하나만 지원. 향후 다른 에이전트 필요 시 `StartAgent(agentName, prompt, workDir)` 형태 일반화 고려하되 현 phase 범위 밖.
- Preferences UI가 아직 없어 토글은 파일 수동 편집으로 확인.
