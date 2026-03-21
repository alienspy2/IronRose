# ImGui 레이아웃이 저장/복원되지 않는 버그 수정

## 유저 보고 내용
- 에디터의 창 레이아웃(ImGui 윈도우 배치)이 저장되지 않는다.
- "프로젝트 기반 시작 흐름 전환" 작업 이후 발생.

## 원인
`EditorState.Load()`와 `ImGuiOverlay.Initialize()` 호출 순서가 역전되어 있었다.

**기존 흐름** (`Program.cs`):
1. `_engine.Initialize(_window)` -- 내부에서 `InitEditor()` -> `ImGuiOverlay.Initialize()` -> `_layoutManager.TryLoadSaved()` 호출
2. `EditorState.Load()` -- `.rose_editor_state.toml`에서 데이터 로드

`TryLoadSaved()`는 `EditorState.ImGuiLayoutData`를 확인하는데, 이 시점에서는 `EditorState.Load()`가 아직 호출되지 않아 `ImGuiLayoutData`가 항상 null이었다. 따라서 저장된 레이아웃이 절대 복원되지 않고, 매번 기본 레이아웃이 적용되었다.

같은 이유로 `EditorState.EditorFont`와 `EditorState.UiScale`도 `ImGuiOverlay.Initialize()`에서 참조 시 기본값만 사용되었다.

## 수정 내용
`EditorState.Load()`를 `EngineCore.Initialize()` 내부로 이동하여, `ProjectContext.Initialize()` 이후이고 `InitEditor()` 이전에 호출되도록 했다.

**수정 후 흐름** (`EngineCore.Initialize()`):
1. `ProjectContext.Initialize()` -- 프로젝트 경로 확정
2. `RoseConfig.Load()`, `ProjectSettings.Load()`
3. **`EditorState.Load()`** -- `.rose_editor_state.toml`에서 ImGuiLayoutData, EditorFont, UiScale 등 로드
4. `InitEditor()` -> `ImGuiOverlay.Initialize()` -> `_layoutManager.TryLoadSaved()` -- 이미 로드된 데이터 사용

`Program.cs`(양쪽)에서 중복 `EditorState.Load()` 호출은 제거. 창 크기 복원 등 `EditorState` 프로퍼티 참조 코드는 그대로 유지 (이미 로드된 상태이므로).

## 변경된 파일
- `src/IronRose.Engine/EngineCore.cs` -- `Initialize()` 안에 `EditorState.Load()` 추가 (InitEditor() 이전 위치). @deps, @note 주석 업데이트.
- `src/IronRose.RoseEditor/Program.cs` -- 중복 `EditorState.Load()` 호출 제거
- `templates/default/Program.cs` -- 중복 `EditorState.Load()` 호출 제거

## 검증
- 정적 분석으로 호출 순서 역전을 확인.
- `dotnet build` 성공 (에러 0개).
- 실행 검증은 유저 확인 필요 (에디터 실행 후 패널 배치를 변경하고 재시작하여 레이아웃이 복원되는지 확인).
