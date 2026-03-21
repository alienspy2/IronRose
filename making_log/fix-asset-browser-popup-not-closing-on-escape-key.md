# Asset Browser 팝업 ESC 키로 닫히지 않는 문제 수정

## 유저 보고 내용
- Link Browser(Asset Browser) 창에서 ESC 키를 눌러도 창이 닫히지 않음
- ESC 키 입력 시 팝업이 닫혀야 함

## 원인
- `DrawAssetBrowserPopup()`에서 Enter 키 처리(`ImGui.IsKeyPressed(ImGuiKey.Enter)`)는 있었지만, ESC 키 처리가 누락되어 있었음
- `BeginPopupModal`에 `ref modalOpen`을 전달하여 X 버튼 닫기는 동작하지만, 검색 InputText가 키보드 포커스를 가지고 있어 ESC 키가 ImGui 기본 모달 닫기 동작까지 전달되지 않았음
- 프로젝트 내 다른 팝업들(`EditorModal.cs`, `ImGuiScriptsPanel.cs`)에서는 명시적으로 `ImGui.IsKeyPressed(ImGuiKey.Escape)`를 사용하여 ESC 키 처리를 하고 있었음

## 수정 내용
- `DrawAssetBrowserPopup()` 메서드에서 Enter 키 처리 바로 아래에 `ImGui.IsKeyPressed(ImGuiKey.Escape)` 조건을 추가하여, ESC 키를 누르면 `cancelled = true`가 되도록 수정
- 기존 Cancel 버튼 및 `!modalOpen`(X 버튼) 처리와 동일한 경로로 팝업이 닫힘

## 변경된 파일
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs` -- ESC 키 pressed 시 cancelled 플래그 설정 추가 (1줄 추가)

## 검증
- dotnet build 성공 확인 (에러 0개)
- 기존 패턴과 동일한 방식(`ImGui.IsKeyPressed(ImGuiKey.Escape)`)을 사용하므로 안정적
