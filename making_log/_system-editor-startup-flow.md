# 에디터 시작 흐름 (Startup Flow)

## 구조
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiStartupPanel.cs` — 프로젝트 미로드 시 표시되는 시작 화면 패널
  - Welcome 화면: "IronRose Engine" 타이틀, New Project / Open Project 버튼
  - New Project 다이얼로그: 프로젝트 이름/위치 입력, 템플릿 기반 생성
  - Restart Notice 모달: 프로젝트 선택 후 재시작 안내
- `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs` — `Update()`에서 `ProjectContext.IsProjectLoaded` 체크 후 StartupPanel 또는 정상 에디터 렌더링 분기
- `src/IronRose.Engine/ProjectContext.cs` — 프로젝트 경로 관리, 글로벌 설정 파일(`~/.ironrose/settings.toml`) 읽기/쓰기
- `src/IronRose.Engine/Editor/ProjectCreator.cs` — 프로젝트 템플릿 복사 및 생성

## 핵심 동작

### 프로젝트 선택 흐름 (Phase 2 이후)
1. 사용자가 New Project 또는 Open Project 선택
2. New Project: `ProjectCreator.CreateFromTemplate()` -> 성공 시 `SetProjectAndNotifyRestart()`
3. Open Project: `NativeFileDialog.PickFolder()` -> project.toml 검증 -> `SetProjectAndNotifyRestart()`
4. `SetProjectAndNotifyRestart()`: `ProjectContext.SaveLastProjectPath()`로 글로벌 설정에 저장, 재시작 안내 모달 표시
5. 사용자가 "Exit" 클릭 -> `Environment.Exit(0)`으로 프로세스 종료
6. 다음 실행 시 `ProjectContext.Initialize()`가 설정 파일에서 last_project 읽어 자동 로드

### ImGuiOverlay의 분기
- `ProjectContext.IsProjectLoaded == false`: `_startupPanel.Draw()`만 호출하고 early return
- `ProjectContext.IsProjectLoaded == true`: DockSpace, 메뉴바, 모든 에디터 패널 렌더링

## 주의사항
- mid-session 프로젝트 전환은 더 이상 지원하지 않음. 프로젝트 변경은 반드시 프로세스 재시작을 통해서만 이루어짐.
- `NativeFileDialog.PickFolder()`는 `Task.Run()`에서 비동기 호출됨. `_waitingForDialog` 플래그로 중복 호출 방지.
- `DrawRestartNoticeDialog()`는 `Draw()` 끝에서 호출되어, New Project 다이얼로그와 동시에 표시 가능 (New Project 다이얼로그는 닫힌 후 모달이 뜸).
- `Environment.Exit(0)` 호출 전에 반드시 `NativeFileDialog.KillRunning()`을 호출해야 함. Linux에서 zenity/kdialog는 독립 프로세스로 실행되므로, .NET 런타임 종료 시 자동으로 종료되지 않아 좀비 프로세스가 남을 수 있음.

## 사용하는 외부 라이브러리
- ImGuiNET: ImGui 바인딩 (모달 팝업, 버튼, 텍스트 등)
- NativeFileDialog: OS 네이티브 폴더 선택 다이얼로그
