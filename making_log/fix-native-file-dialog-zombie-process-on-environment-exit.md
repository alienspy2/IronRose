# NativeFileDialog 좀비 프로세스 수정 (Environment.Exit 시 zenity/kdialog 미종료)

## 유저 보고 내용
- New Project 시 "Browse..." 버튼으로 폴더 선택 다이얼로그(zenity/kdialog)를 열어 놓은 상태에서, 네이티브 다이얼로그를 닫지 않고 수동으로 경로 입력 후 "Create" -> restart notice "Exit"을 누르면 `Environment.Exit(0)`이 호출되어 메인 프로세스는 종료되지만 zenity/kdialog 프로세스가 남아 폴더 선택 창이 계속 표시됨

## 원인
- `NativeFileDialog.RunProcess()`에서 zenity/kdialog를 `Process.Start()`로 실행하지만, 프로세스 참조를 외부로 노출하지 않아 종료 수단이 없었음
- `Task.Run()`에서 비동기로 호출된 `PickFolder()` -> `RunProcess()`가 `StandardOutput.ReadToEnd()`에서 블로킹 대기 중인 상태에서 `Environment.Exit(0)`이 호출됨
- `Environment.Exit()`은 .NET 런타임만 종료하며, 독립 자식 프로세스(zenity/kdialog)는 자동 종료되지 않음
- `using var process`의 `Dispose()`도 프로세스를 Kill하지 않고 핸들만 해제함

## 수정 내용
1. `NativeFileDialog`에 `_runningProcess` static 필드와 `_processLock` 동기화 객체 추가
2. `RunProcess()` 내에서 프로세스 시작 후 `_runningProcess`에 저장, 완료/예외 시 `finally`에서 null로 정리
3. `KillRunning()` public static 메서드 추가: `_runningProcess`가 존재하고 아직 실행 중이면 `Kill()` 호출
4. `ImGuiStartupPanel.DrawRestartNoticeDialog()`의 `Environment.Exit(0)` 직전에 `NativeFileDialog.KillRunning()` 호출 추가

## 변경된 파일
- `src/IronRose.Engine/Editor/ImGui/NativeFileDialog.cs` -- `_runningProcess` 추적 필드, `KillRunning()` 메서드 추가, `RunProcess()`에서 프로세스 추적 로직 추가
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiStartupPanel.cs` -- `Environment.Exit(0)` 직전에 `NativeFileDialog.KillRunning()` 호출 추가

## 검증
- 빌드 성공 확인 (`dotnet build` 에러 0)
- 실행 검증은 유저 확인 필요 (GUI 조작이 필요한 시나리오)
