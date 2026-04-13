# ClaudeManager 시스템

## 개요
Claude CLI 연동 호출의 **단일 진입점**. 현재는 Feedback 패널에서 aca-fix 에이전트를 스트리밍으로
호출하는 용도로만 사용된다. 명령 실행 규약/출력 파싱/세션 라이프사이클을 중앙화하여,
향후 다른 곳에서 Claude를 호출할 때 이 매니저만 거치도록 한다.

## 구조
- `src/IronRose.Engine/Editor/ClaudeManager.cs` — 정적 클래스 `ClaudeManager` + `ClaudeSession` 클래스.
- `src/IronRose.Engine/EditorPreferences.cs` — `EnableClaudeUsage` 게이트 값.
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiFeedbackPanel.cs` — 현재 유일한 소비처.

## 허용 호출
`claude -p --verbose --output-format stream-json` 형태의 스트리밍 호출만 허용한다.
프로세스는 `/bin/bash -c`로 spawn되며 사용자 홈의 PATH를 상속하기 위해 `login shell`을 사용한다
(구현 세부는 `StartFix` 참조).

## 게이트: `EditorPreferences.EnableClaudeUsage`
- `ClaudeManager.IsEnabled`는 `EditorPreferences.EnableClaudeUsage`의 프록시.
- **`IsEnabled == false`** 이면:
  - `StartFix()`는 `null`을 반환하고 경고 로그만 남긴다 (프로세스를 spawn하지 않는다).
  - `ImGuiFeedbackPanel`은 Fix/Stop 버튼과 Fix 출력 영역을 **아예 렌더링하지 않는다**.
- 따라서 Preferences에서 체크를 해제하면 UI도 즉시 사라진다(재시작 불필요).

## `ClaudeSession` 라이프사이클
```
StartFix(prompt, workDir)  →  IsRunning = true
                          ↓
      워커 스레드: stdout 한 줄씩 읽어 ProcessStreamLine → AppendOutput
      UI 스레드:  SnapshotOutput(out dirty)로 주기 폴링
                          ↓
        프로세스 자연 종료 or session.Stop()
                          ↓
              IsRunning = false, 워커 종료
                          ↓
              session.Dispose()로 Process 정리
```

- `IsRunning`은 `volatile`로 스레드 간 가시성 보장.
- `SnapshotOutput`은 UI 스레드 전용, 내부 버퍼를 잠금 하에 복사하고 `dirty` 플래그를 소비한다.
- `AppendOutput`은 워커 스레드 전용.
- `Stop()`은 프로세스 트리 전체를 종료한다(자식 프로세스 포함).
- `Dispose()`는 `Stop()` 후 `Process` 핸들을 정리한다.

## stream-json 이벤트 파싱
`ProcessStreamLine(session, line)`이 각 stdout 라인을 파싱한다.

| `type` 값 | 의미 | 처리 |
|-----------|------|------|
| `content_block_delta` | Anthropic API 토큰 델타 | `delta.text`를 `AppendOutput`. |
| `assistant` | Claude Code 어시스턴트 메시지 | `message.content[]` 중 `type == "text"`인 블록의 `text`를 `AppendOutput`. |
| `result` | 세션 최종 결과 문자열 | 버퍼가 비어 있을 때만 `AppendIfEmpty`로 fallback 저장 (스트리밍이 있었으면 무시). |
| 기타 | 무시 | — |

JSON 파싱에 실패한 라인은 **raw line 그대로** `AppendOutput`한다(`\n` 포함).
로그 형식이 바뀌거나 예상치 못한 텍스트가 섞여도 그대로 표시하기 위함.

## 현재 사용처
- `ImGuiFeedbackPanel.StartFix(index)` — 선택된 피드백 항목의 내용과 경로를 프롬프트로 조립하여
  `ClaudeManager.StartFix(prompt, engineRoot)` 호출. 반환된 `ClaudeSession`을 패널 필드에 보관하고
  매 프레임 `SnapshotOutput`으로 렌더링.
- 그 외 호출처 없음. 새 Claude 연동 기능을 붙일 때 `ClaudeManager`를 통해서만 호출할 것.

## 확장 시 주의
- **게이트 우회 금지**: `EditorPreferences.EnableClaudeUsage`가 false일 때 `claude` 프로세스를
  실행해서는 안 된다. `ClaudeManager`를 거치지 않고 직접 `Process.Start`로 claude를 띄우는 코드는
  감사/검토 대상.
- **명령 규약 고정**: 출력 파서가 stream-json에 맞춰져 있으므로 `--output-format`을 바꾸면
  `ProcessStreamLine`도 함께 변경해야 한다.
- **스레드 경계**: `ClaudeSession` 필드를 UI 스레드에서 직접 쓰지 말 것. 반드시 `SnapshotOutput`,
  `IsRunning`, `Stop`, `Dispose`만 사용한다.

## 관련 파일
- `src/IronRose.Engine/Editor/ClaudeManager.cs`
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiFeedbackPanel.cs`
- `src/IronRose.Engine/EditorPreferences.cs`
