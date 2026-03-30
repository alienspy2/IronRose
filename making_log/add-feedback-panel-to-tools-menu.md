# Tools 메뉴에 Feedback 패널 추가

## 수행한 작업
- Tools 메뉴에 Feedback 항목을 추가하여 사용자 피드백 텍스트를 파일로 저장/조회/삭제할 수 있는 패널 구현
- 프로젝트 폴더의 `feedback/` 디렉토리에 `feedback_XX.txt` 형식으로 자동 번호 부여하여 저장
- 기존 피드백 파일 목록을 CollapsingHeader로 표시하고 각 항목에 삭제 버튼 제공
- EditorState에 패널 가시성 상태를 영속화

## 변경된 파일
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiFeedbackPanel.cs` — 새 패널 클래스 생성 (IEditorPanel 구현)
- `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs` — 필드 선언, 생성자 초기화, Tools 메뉴 항목, Draw 호출, RestorePanelStates/SyncPanelStatesToEditorState 동기화 추가
- `src/IronRose.Engine/Editor/EditorState.cs` — PanelFeedback 프로퍼티 추가, TOML Load/Save에 feedback 항목 추가

## 주요 결정 사항
- Vector2 타입 충돌 (RoseEngine.Vector2 vs System.Numerics.Vector2) 해결을 위해 `using Vector2 = System.Numerics.Vector2` alias 사용
- 파일 목록 갱신은 1초 간격으로 제한하여 I/O 부하 최소화
- feedback 폴더가 없으면 저장 시 자동 생성
- 파일 번호는 기존 파일의 최대 번호 + 1로 결정 (중간 빈 번호 재사용하지 않음)

## 다음 작업자 참고
- 현재 .txt 파일만 지원. 다른 확장자가 필요하면 GetNextFileNumber와 RefreshEntries 수정 필요
- feedback 폴더는 프로젝트 루트(ProjectContext.ProjectRoot) 기준
