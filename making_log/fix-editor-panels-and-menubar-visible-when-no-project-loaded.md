# 프로젝트 미로드 시 에디터 패널/메뉴바가 그려지는 문제 수정

## 유저 보고 내용
- `project.toml`이 없는 상태(`ProjectContext.IsProjectLoaded == false`)에서 엔진 실행 시, 시작 화면(ImGuiStartupPanel)은 표시되지만 DockSpace, 메뉴바, Hierarchy, Inspector, SceneView 등 모든 에디터 패널이 함께 그려져 UI가 깨짐
- 기대 동작: 프로젝트 미로드 시 startup panel만 표시되고, 나머지는 모두 스킵

## 원인
- `ImGuiOverlay.Update()` 메서드에서 `ImGui.NewFrame()` 이후 DockSpace, 메뉴바, 모든 패널 Draw를 무조건 수행
- 개별 패널 내부에 `if (!ProjectContext.IsProjectLoaded) return;` 가드가 있는 패널도 있었지만, DockSpace 자체와 메뉴바는 가드 없이 항상 렌더링됨
- 결과적으로 빈 DockSpace 프레임 + 메뉴바가 startup panel 위에 겹쳐 그려짐

## 수정 내용
- `ImGuiOverlay.Update()` 메서드의 `PushCurrentFont()` 직후, DockSpace 설정 시작 전에 `ProjectContext.IsProjectLoaded` 체크를 삽입
- `false`인 경우 `_startupPanel?.Draw()`만 호출하고 `PopCurrentFont()` 후 즉시 return
- `ImGui.Render()`는 별도의 `Render(CommandList cl)` 메서드에서 호출되므로, `Update()`에서는 `PopCurrentFont()`만 필요

## 변경된 파일
- `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs` -- `Update()` 메서드에 프로젝트 미로드 시 early return 분기 추가 (line 422-428)

## 검증
- 정적 분석으로 원인 파악 및 수정
- `dotnet build` 성공 확인 (오류 0개)
- 호출 구조 확인: `Update()` -> `PopCurrentFont()`로 종료, `Render()` -> `ImGui.Render()` 별도 호출이므로 ImGui 스택 불일치 없음
