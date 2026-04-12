# Phase 2: Inspector "Edit Canvas" 버튼 + Hierarchy 필터링

## 목표
- Inspector 패널에서 Canvas 컴포넌트에 "Edit Canvas" 토글 버튼을 추가하여 Canvas Edit Mode 진입/퇴출 경로를 만든다.
- Hierarchy 패널에서 Canvas Edit Mode일 때 Canvas GO와 그 하위 자식만 표시하도록 필터링한다.
- Canvas Edit Mode 중 루트 레벨 드래그/삭제를 제한한다.

## 선행 조건
- Phase 1 (add-canvas-edit-mode-a_state-and-core) 완료
- `CanvasEditMode` 클래스와 `EditorState`의 Canvas 관련 필드가 존재해야 함

## 수정할 파일

### `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs`

- **변경 내용**: Canvas 컴포넌트에 "Edit Canvas" 토글 버튼 추가
- **위치**: line 868 부근, "Edit Collider" 블록의 닫는 `}` 직후에 추가

추가할 코드:
```csharp
// "Edit Canvas" toggle button for Canvas components
if (comp is RoseEngine.Canvas)
{
    bool editingCanvas = EditorState.IsEditingCanvas
        && EditorState.EditingCanvasGoId == selected.GetInstanceID();
    if (editingCanvas)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.2f, 0.7f, 0.3f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0.3f, 0.8f, 0.4f, 1f));
    }
    if (ImGui.Button("Edit Canvas"))
    {
        if (editingCanvas)
            CanvasEditMode.Exit();
        else
            CanvasEditMode.Enter(selected);
    }
    if (editingCanvas)
        ImGui.PopStyleColor(2);
}
```

- **이유**: "Edit Collider" 버튼과 동일한 패턴. `activeColor` (녹색)으로 활성 상태를 시각적으로 표시한다. `selected`는 현재 Inspector에서 보고 있는 `GameObject` 변수.

- **구현 힌트**:
  - `using IronRose.Engine.Editor;`가 파일 상단에 이미 있으므로 `CanvasEditMode`에 직접 접근 가능.
  - `comp is RoseEngine.Canvas`로 타입 체크. `Canvas`가 `RoseEngine` 네임스페이스에 있으므로 정규화된 이름을 사용해야 한다. 파일 상단에 `using RoseEngine;`이 없을 수 있으므로 확인 후 추가하거나 정규화된 이름을 사용.
  - `selected`는 Inspector가 표시하는 `GameObject`이며, 이 코드 블록에서 이미 사용 중인 변수명이다 (line 800 부근에서 `var selected = ...`로 정의됨).

### `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiHierarchyPanel.cs`

- **변경 내용**: Canvas Edit Mode일 때 Canvas GO의 자식만 표시하는 필터링 추가

- **수정 위치 1**: `Draw()` 메서드 내부, roots/childMap 구축 루프 이후 (line 175 부근 `foreach (var root in roots)` 직전)

Canvas Edit Mode일 때 roots 리스트를 Canvas GO 하나로 교체하는 로직 추가:
```csharp
// Canvas Edit Mode: Canvas GO를 유일한 루트로 표시
if (EditorState.IsEditingCanvas && EditorState.EditingCanvasGoId.HasValue)
{
    var canvasGo = UndoUtility.FindGameObjectById(EditorState.EditingCanvasGoId.Value);
    if (canvasGo != null)
    {
        roots.Clear();
        roots.Add(canvasGo);
    }
}
```

- **수정 위치 2**: `DrawRootDropZone()` 메서드 내부의 `blockRootLevel` 변수 (line 712 부근)

기존:
```csharp
bool blockRootLevel = EditorState.IsEditingPrefab;
```

변경:
```csharp
bool blockRootLevel = EditorState.IsEditingPrefab || EditorState.IsEditingCanvas;
```

- **수정 위치 3**: `ExecuteRootDrop()` 메서드 시작 부분 (line 869 부근)

기존:
```csharp
if (EditorState.IsEditingPrefab) return;
```

변경:
```csharp
if (EditorState.IsEditingPrefab || EditorState.IsEditingCanvas) return;
```

- **수정 위치 4**: 드래그앤드롭 처리 부분에서 루트 레벨 이동 차단 (line 837 부근)

기존:
```csharp
if (EditorState.IsEditingPrefab && !newParentId.HasValue)
    continue;
```

변경:
```csharp
if ((EditorState.IsEditingPrefab || EditorState.IsEditingCanvas) && !newParentId.HasValue)
    continue;
```

- **이유**: Canvas Edit Mode 중에는 Canvas GO와 그 하위만 보여야 하며, Canvas 외부로 오브젝트를 이동하거나 루트 레벨에 새 오브젝트를 생성하는 것을 차단해야 한다. Prefab Edit Mode의 동일 제한 패턴을 확장한다.

- **구현 힌트**:
  - `UndoUtility.FindGameObjectById()`는 `RoseEngine.SceneManager.AllGameObjects`에서 ID로 검색하는 유틸리티 메서드로, 기존 코드에서 널리 사용됨.
  - roots 교체 시 childMap은 이미 전체 씬으로 구축되어 있으므로, Canvas GO의 자식들은 childMap에서 자동으로 찾아진다. roots만 교체하면 트리가 Canvas 하위만 표시된다.
  - 검색 필터(`_searchFilter`)와 Canvas Edit Mode 필터가 동시에 적용될 수 있다. roots 교체는 검색 필터 적용 후에 수행하므로, Canvas 하위 + 검색 결과의 교집합이 표시된다.

## 생성할 파일
- 없음

## NuGet 패키지
- 없음

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] Canvas 컴포넌트가 있는 GameObject를 선택하면 Inspector에 "Edit Canvas" 버튼이 표시됨
- [ ] "Edit Canvas" 클릭 시 버튼이 녹색으로 변하고, 다시 클릭하면 원래 색으로 돌아옴
- [ ] Canvas Edit Mode 중 Hierarchy에 Canvas GO와 그 자식만 표시됨
- [ ] Canvas Edit Mode 중 루트 레벨에 오브젝트를 드롭할 수 없음
- [ ] Canvas Edit Mode 중 빈 공간 우클릭 컨텍스트 메뉴가 차단됨

## 참고
- "Edit Collider" 버튼 코드 (ImGuiInspectorPanel.cs line 856~868)를 정확히 참조하여 동일 패턴으로 구현한다.
- Hierarchy 필터링에서 Prefab Edit Mode의 `IsEditingPrefab` 체크를 확장하는 패턴을 사용한다.
- `selected` 변수는 ImGuiInspectorPanel의 컴포넌트 순회 루프 스코프에서 사용 가능한 `GameObject` 변수이다.
