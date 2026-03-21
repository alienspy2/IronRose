# Fix: 입력 팝업(Rename/CreateFolder)이 즉시 닫히는 문제

## 증상
- F2(Rename) 또는 Create Folder 시 이름 입력창이 뜨자마자 닫힘
- 처음에는 정상 동작하다가 어느 순간부터 발생

## 원인
Multi-Viewport 모드에서 모달 팝업이 별도 OS 윈도우(보조 뷰포트)로 열림.
Enter로 팝업을 확인하면, 같은 프레임에 팝업이 닫히고 보조 윈도우가 파괴됨.
Enter key-up 이벤트가 파괴된 윈도우에 도착하여 유실 → ImGui가 Enter를 영원히 눌린 상태로 인식.
이후 새 팝업을 열면 잔존 Enter 키 리피트로 InputText가 즉시 `enter=True` 반환 → 팝업 즉시 닫힘.

## 수정 내용

### 근본 수정: `ImGuiInputHandler.RemoveSecondaryInput()`
- 보조 뷰포트 파괴 시 현재 눌린 키/마우스 상태를 ImGui IO에 릴리스 이벤트로 전달
- "stuck key" 문제를 원천 차단

### 방어적 보험: `EditorModal.InputTextPopup()` / `ImGuiHierarchyPanel`
- 팝업 열릴 때 Enter가 이미 눌려있으면 릴리스될 때까지 `enter` 결과 무시
- 근본 수정이 커버하지 못하는 엣지 케이스 대비

## 수정 파일
- `src/IronRose.Engine/Editor/ImGui/ImGuiInputHandler.cs` — RemoveSecondaryInput에 키 릴리스 전달
- `src/IronRose.Engine/Editor/ImGui/EditorModal.cs` — _suppressEnterUntilRelease 가드
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiHierarchyPanel.cs` — _renameSuppressEnter 가드
