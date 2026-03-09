# Phase 22: Hierarchy Multi-Select + Inspector Multi-Edit

## Context

현재 IronRose 에디터는 Hierarchy에서 단일 오브젝트만 선택 가능하며, Inspector도 단일 오브젝트만 편집할 수 있습니다. Unity처럼 Ctrl+Click(토글), Shift+Click(범위 선택)으로 복수 오브젝트를 선택하고, 같은 컴포넌트를 공유하는 오브젝트들의 프로퍼티를 동시에 편집할 수 있도록 합니다.

**핵심 원칙:**
- 마지막 클릭된 오브젝트가 "Primary" — TransformGizmo, EditorCamera Focus는 Primary만 대상
- Inspector에서 같은 컴포넌트 타입을 공유하는 필드만 표시
- 값이 다른 필드는 "---" (mixed) 표시, 편집 시 모든 오브젝트에 일괄 적용
- Undo는 `CompoundUndoAction`으로 단일 Ctrl+Z에 전체 복원

## 수정 대상 파일 (6개)

| 파일 | 변경 내용 |
|------|----------|
| `EditorSelection.cs` | `int?` → `List<int>` + `HashSet<int>` 멀티셀렉션, ToggleSelect/RangeSelect 추가 |
| `ImGuiHierarchyPanel.cs` | Ctrl+Click 토글, Shift+Click 범위 선택, flat ordered ID 리스트 |
| `ImGuiInspectorPanel.cs` | DrawMultiGameObjectInspector, DrawMultiValue (mixed 감지), CompoundUndo |
| `ImGuiOverlay.cs` | Delete/Duplicate 복수 처리, Scene View Pick 모디파이어 키, Renderer에 멀티ID 전달 |
| `SceneViewRenderer.cs` | `int?` → `IReadOnlyList<int>?`, 선택된 모든 오브젝트 아웃라인 |
| (신규) `CompoundUndoAction.cs` | 복수 IUndoAction을 하나로 묶는 컴포지트 Undo |

---

## Step 1: CompoundUndoAction (기반 작업)

**신규 파일:** `src/IronRose.Engine/Editor/Undo/Actions/CompoundUndoAction.cs`

```csharp
public sealed class CompoundUndoAction : IUndoAction
{
    public string Description { get; }
    private readonly IUndoAction[] _actions;

    public CompoundUndoAction(string description, IEnumerable<IUndoAction> actions)
    {
        Description = description;
        _actions = actions.ToArray();
    }

    public void Undo()
    {
        for (int i = _actions.Length - 1; i >= 0; i--)
            _actions[i].Undo();
    }

    public void Redo()
    {
        for (int i = 0; i < _actions.Length; i++)
            _actions[i].Redo();
    }
}
```

---

## Step 2: EditorSelection — 멀티셀렉션 코어

**파일:** `src/IronRose.Engine/Editor/EditorSelection.cs`

```csharp
public static class EditorSelection
{
    private static readonly List<int> _selectedIds = new();
    private static readonly HashSet<int> _selectedIdSet = new();

    public static long SelectionVersion { get; private set; }

    // 하위 호환: Primary (마지막 클릭) 반환
    public static int? SelectedGameObjectId =>
        _selectedIds.Count > 0 ? _selectedIds[^1] : null;

    public static GameObject? SelectedGameObject { get; } // 기존 LINQ 유지

    // 멀티셀렉션 API
    public static IReadOnlyList<int> SelectedGameObjectIds => _selectedIds;
    public static int Count => _selectedIds.Count;
    public static bool IsSelected(int id) => _selectedIdSet.Contains(id);

    // 단일 선택 (기존 동작, 클릭)
    public static void Select(int? id);

    // Ctrl+Click: 토글
    public static void ToggleSelect(int id);

    // Shift+Click: anchor(Primary) ~ target 범위 선택
    public static void RangeSelect(int targetId, IReadOnlyList<int> orderedIds);

    // 프로그래밍적 선택 교체 (Duplicate 후 새 오브젝트들 선택)
    public static void SetSelection(IEnumerable<int> ids);

    public static void SelectGameObject(GameObject? go); // 기존 유지
    public static void Clear(); // 전체 해제
}
```

**RangeSelect 로직:**
- Primary(=anchor)와 target 사이의 모든 노드를 `orderedIds` (Hierarchy 순회 순서)에서 선택
- target을 리스트 마지막에 놓아 Primary로 만듦

---

## Step 3: ImGuiHierarchyPanel — 멀티셀렉션 입력

**파일:** `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiHierarchyPanel.cs`

변경사항:
1. `_flatOrderedIds` (List<int>) 추가 — 매 프레임 트리 순회 시 구축
2. `isSelected` 체크를 `EditorSelection.IsSelected(id)`로 변경
3. 클릭 핸들링에 모디파이어 키 분기 추가

```csharp
private readonly List<int> _flatOrderedIds = new();

public void Draw()
{
    // ... 기존 tree 빌드 ...
    _flatOrderedIds.Clear();
    foreach (var root in roots)
        DrawNode(root, childMap);
}

private void DrawNode(GameObject go, Dictionary<int, List<GameObject>> childMap)
{
    int id = go.GetInstanceID();
    _flatOrderedIds.Add(id);

    bool isSelected = EditorSelection.IsSelected(id);  // 변경
    // ... flags, TreeNodeEx ...

    if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
    {
        var io = ImGui.GetIO();
        if (io.KeyCtrl)
            EditorSelection.ToggleSelect(id);
        else if (io.KeyShift)
            EditorSelection.RangeSelect(id, _flatOrderedIds);
        else
            EditorSelection.Select(id);
    }
    // ... children ...
}
```

---

## Step 4: SceneViewRenderer — 복수 아웃라인

**파일:** `src/IronRose.Engine/Rendering/SceneViewRenderer.cs`

- `Render()`와 `RenderOverlays()`의 `int? selectedObjectId` → `IReadOnlyList<int>? selectedObjectIds`
- 내부에서 foreach로 `DrawSelectionOutline` 호출

**파일:** `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs`

호출부 변경:
```csharp
var selIds = EditorSelection.Count > 0 ? EditorSelection.SelectedGameObjectIds : null;
_sceneRenderer.RenderOverlays(cl, fb, _editorCamera, _sceneView.ShowGrid, selIds);
_sceneRenderer.Render(cl, _editorCamera, mode, matcapTex, _sceneView.ShowGrid, selIds);
```

---

## Step 5: ImGuiOverlay — Delete/Duplicate 복수 처리

**파일:** `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs`

### DeleteSelectedGameObject()
```csharp
var ids = EditorSelection.SelectedGameObjectIds;
if (ids.Count == 0) return;

var actions = new List<IUndoAction>();
for (int i = ids.Count - 1; i >= 0; i--)
{
    var go = FindById(ids[i]);
    if (go == null) continue;
    actions.Add(new DeleteGameObjectAction($"Delete {go.name}", go));
    Object.DestroyImmediate(go);
}

if (actions.Count == 1)
    UndoSystem.Record(actions[0]);
else if (actions.Count > 1)
    UndoSystem.Record(new CompoundUndoAction($"Delete {actions.Count} objects", actions));

EditorSelection.Clear();
```

### DuplicateSelected()
```csharp
var ids = EditorSelection.SelectedGameObjectIds;
if (ids.Count == 0) return;

var newIds = new List<int>();
foreach (var id in ids)
{
    var go = FindById(id);
    if (go == null) continue;
    var clone = Object.Instantiate(go);
    clone.name = go.name + " (Copy)";
    if (go.transform.parent != null)
        clone.transform.SetParent(go.transform.parent);
    newIds.Add(clone.GetInstanceID());
}
EditorSelection.SetSelection(newIds);
```

### Scene View Pick 콜백
모디파이어 키 상태를 캡처 후 비동기 콜백에서 사용:
```csharp
bool ctrlHeld = io.KeyCtrl;
_sceneRenderer.RequestPick(px, py, pickedId =>
{
    if (pickedId == 0)
    {
        if (!ctrlHeld) EditorSelection.Clear();
    }
    else
    {
        if (ctrlHeld) EditorSelection.ToggleSelect((int)pickedId);
        else EditorSelection.Select((int)pickedId);
    }
});
```

---

## Step 6: ImGuiInspectorPanel — 멀티 에디트

**파일:** `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs`

### 6-1. 라우팅 분기

`Draw()` 메서드의 `InspectorMode.GameObject` 분기에서:
```csharp
case InspectorMode.GameObject when selectedGoId != null:
    if (EditorSelection.Count > 1)
        DrawMultiGameObjectInspector();
    else
        DrawGameObjectInspector(selectedGoId.Value);
    break;
```

### 6-2. DrawMultiGameObjectInspector()

1. `EditorSelection.SelectedGameObjectIds`로 모든 GameObject 수집
2. 헤더: `"(N objects selected)"` 표시
3. Transform — 항상 공유, `DrawMultiTransform()` 호출
4. 공유 컴포넌트 교집합 계산 (`HashSet<Type>.IntersectWith`)
5. 각 공유 컴포넌트에 대해 `DrawMultiComponentFields()` / `DrawMultiComponentProperties()` 호출

### 6-3. DrawMultiValue (mixed 감지 + 일괄 적용)

```
모든 Component에서 값 읽기
  ↓
전부 동일? → 기존 DrawValue 호출 (setter만 래핑하여 전체 적용)
  ↓
값이 다름? → "---" (mixed) 표시, 편집 시작하면 Primary 값 기준으로 전체 적용
```

**Mixed 값 표시 규칙:**

| 타입 | Mixed 표시 | 편집 시 동작 |
|------|-----------|-------------|
| float | DragFloat, primary 값 표시 + 텍스트 dim | 드래그하면 모두 동일 값 |
| int | DragInt, 같은 패턴 | 같음 |
| bool | Checkbox에 "-" 표시 | 클릭 시 모두 토글 |
| string | InputText "---" | 입력 시 모두 교체 |
| Vector3 | 축별 mixed 감지 | 편집된 축만 전체 적용 |
| Color | ColorEdit4, primary 색 + dim | 편집 시 모두 동일 |
| enum | Combo "---" 항목 | 선택 시 모두 변경 |

### 6-4. Multi-Edit Undo

`DrawMultiValue`에서 직접 old value 배열 캡처:
```
편집 시작 → 모든 Component의 현재값 캡처 (oldValues[])
편집 완료 → 각 Component별 SetPropertyAction 생성 → CompoundUndoAction으로 묶어 Record
```

기존 단일 편집의 `InspectorUndoTracker` 흐름은 변경 없이 유지.

---

## 변경하지 않는 파일

| 파일 | 이유 |
|------|------|
| `TransformGizmo.cs` | `SelectedGameObject` (Primary) 사용 — 하위 호환 유지 |
| `EditorCamera.cs` | `SelectedGameObject` (Primary) 사용 — Focus는 Primary만 |
| `SceneSerializer.cs` | 선택과 무관 |

## 구현 순서

```
Step 1 (CompoundUndoAction) ← 신규 파일, 의존성 없음
  ↓
Step 2 (EditorSelection) ← 핵심 데이터 모델 변경
  ↓
Step 3 (HierarchyPanel) + Step 4 (SceneViewRenderer) ← 병렬 가능
  ↓
Step 5 (ImGuiOverlay) ← Step 2 의존
  ↓
Step 6 (InspectorPanel) ← Step 1 + 2 의존, 가장 복잡
```

## 검증 방법

1. Hierarchy에서 Ctrl+Click으로 복수 오브젝트 선택/해제 확인
2. Hierarchy에서 Shift+Click으로 범위 선택 확인
3. Scene View에서 선택된 모든 오브젝트에 아웃라인 표시 확인
4. Scene View에서 Ctrl+Click으로 복수 픽 확인
5. Inspector에서 "(N objects selected)" 헤더 표시 확인
6. 같은 컴포넌트의 같은 값 필드 편집 → 모든 선택 오브젝트에 반영 확인
7. 값이 다른 필드에서 "---" (mixed) 표시 확인, 편집 시 일괄 적용 확인
8. Delete 키로 복수 오브젝트 삭제 → 단일 Ctrl+Z로 전체 복원 확인
9. Ctrl+D로 복수 오브젝트 복제 → 새 오브젝트들이 선택됨 확인
10. TransformGizmo가 Primary 오브젝트에만 표시되는지 확인
