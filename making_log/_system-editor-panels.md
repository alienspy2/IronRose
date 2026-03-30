# 에디터 패널 시스템

## 구조
- `IEditorPanel` (`Panels/IEditorPanel.cs`) — 모든 패널의 공통 인터페이스 (IsOpen, Draw)
- `ImGuiOverlay` (`ImGui/ImGuiOverlay.cs`) — 모든 패널의 생명주기 관리 (생성, 메뉴 토글, Draw 호출, 상태 동기화)
- `EditorState` (`Editor/EditorState.cs`) — 패널 가시성 영속화 (.rose_editor_state.toml의 [panels] 섹션)

## 새 패널 추가 절차
1. `Panels/` 폴더에 `ImGui[Name]Panel.cs` 생성, `IEditorPanel` 구현
2. `ImGuiOverlay.cs`에서:
   - 필드 선언: `private ImGui[Name]Panel? _name;`
   - 생성자(Create panels 블록)에서 `new` 초기화
   - 메뉴(Tools 또는 Window)에 MenuItem 추가
   - Draw 호출 추가
   - `RestorePanelStates()`에 `_name!.IsOpen = EditorState.Panel[Name];` 추가
   - `SyncPanelStatesToEditorState()`에 `EditorState.Panel[Name] = _name?.IsOpen ?? false;` 추가
3. `EditorState.cs`에서:
   - `public static bool Panel[Name]` 프로퍼티 추가
   - `Load()` 메서드의 panels 섹션에 `GetBool()` 추가
   - `Save()` 메서드의 panels 섹션에 toml 문자열 추가

## 주의사항
- `System.Numerics.Vector2`와 `RoseEngine.Vector2`가 충돌하므로, ImGui 패널에서는 `using Vector2 = System.Numerics.Vector2` alias 사용 필요
- `ImGui.Begin()` 호출 후 반드시 `ImGui.End()` 호출 (조건문 밖에서)
- 패널 Draw는 `if (!IsOpen) return;` 가드로 시작

## 사용하는 외부 라이브러리
- ImGui.NET (ImGuiNET) — UI 렌더링
