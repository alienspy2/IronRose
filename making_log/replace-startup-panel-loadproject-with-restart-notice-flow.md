# StartupPanel "다시 실행" 흐름으로 전환 (Phase 2)

## 수행한 작업
- `ImGuiStartupPanel`에서 mid-session 프로젝트 로딩(`LoadProject()`)을 제거하고, 프로젝트 경로를 설정 파일에 저장한 뒤 재시작 안내 모달을 표시하는 흐름으로 전환
- 설계 문서 `plans/project-based-startup-flow.md`의 Phase 2에 해당

## 변경된 파일
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiStartupPanel.cs`
  - `_showRestartNotice` (bool), `_selectedProjectPath` (string?) 필드 추가
  - `SetProjectAndNotifyRestart(string projectDir)` 메서드 추가: 경로를 `ProjectContext.SaveLastProjectPath()`로 저장하고 재시작 안내 플래그 설정
  - `DrawRestartNoticeDialog()` 메서드 추가: 모달 팝업으로 "Project has been set. Please restart the editor." 메시지와 Exit 버튼 표시
  - `OpenExistingProject()` 내 `LoadProject(folder)` -> `SetProjectAndNotifyRestart(folder)` 교체
  - `DrawNewProjectDialog()` 내 `LoadProject(fullPath)` -> `SetProjectAndNotifyRestart(fullPath)` 교체
  - `Draw()` 끝에 `DrawRestartNoticeDialog()` 호출 추가
  - 기존 `LoadProject()` 메서드 삭제
  - frontmatter 갱신: @brief, @exports, @note 업데이트

## 주요 결정 사항
- `DrawRestartNoticeDialog()`는 `Draw()` 메서드 끝에서 호출하여, New Project 다이얼로그가 닫힌 후에도 재시작 안내가 표시되도록 함
- `SetProjectAndNotifyRestart()`는 instance 메서드로 구현 (기존 `LoadProject()`는 static이었으나, `_showRestartNotice`와 `_selectedProjectPath` 필드 접근이 필요하므로)
- 추가 using 문 불필요 (`System`, `System.Numerics`, `ImGuiNET` 이미 존재)

## 다음 작업자 참고
- Phase 3에서 `EngineCore.cs`의 mid-session 프로젝트 전환 코드(`_loadedProjectRoot`, `Update()` 내 전환 감지 블록)를 제거해야 함
- `ImGuiOverlay.cs`의 File 메뉴에서 New/Open Project 호출 시에도 `SetProjectAndNotifyRestart()` 경로를 타게 되는데, 이는 내부적으로 `_startupPanel.ShowNewProjectDialog()`와 `_startupPanel.OpenExistingProject()`를 호출하므로 별도 수정 불필요
- 빌드 검증은 사용자 요청에 따라 일괄 빌드 시 수행 예정
