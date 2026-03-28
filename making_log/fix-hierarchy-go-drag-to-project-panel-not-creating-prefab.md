# Hierarchy GO를 Project Panel에 드래그 드롭해도 Prefab이 생성되지 않는 문제 수정

## 유저 보고 내용
- Hierarchy View에서 GameObject를 Project View로 드래그 앤 드롭했을 때 prefab이 만들어지지 않는 문제

## 원인
Project Panel의 에셋 리스트 영역(AssetList child window)에서 `HIERARCHY_GO` 드롭 타겟이 child window **내부**에 배치되어 있었다. ImGui의 `BeginDragDropTarget()`는 직전 아이템이 hover 상태일 때만 true를 반환하는데, 에셋 리스트 아이템들 이후의 빈 공간에 마우스가 있으면 마지막 아이템이 hover되지 않아 `BeginDragDropTarget()`가 false를 반환했다.

즉, 에셋 리스트 영역의 빈 공간이나 에셋 아이템 위에 드롭하면 드롭이 인식되지 않았다.

## 수정 내용
1. **드롭 타겟을 `EndChild()` 뒤로 이동**: `ImGui.EndChild()`는 child window를 하나의 "아이템"으로 부모 윈도우에 등록한다. `EndChild()` 바로 뒤에 `BeginDragDropTarget()`를 호출하면 **AssetList child window 영역 전체**가 드롭 타겟이 되어, 에셋 아이템 위든 빈 공간이든 어디에 드롭해도 prefab이 생성된다.

2. **`SaveDraggedGOsAsPrefab()` 후 `RebuildTree()` 호출 추가**: prefab 파일이 디스크에 생성된 후 프로젝트 패널의 에셋 트리를 즉시 갱신하여, 새로 생성된 .prefab 파일이 에셋 리스트에 바로 표시되도록 했다.

## 변경된 파일
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectPanel.cs`
  - 기존 child window 내부의 `HIERARCHY_GO` 드롭 타겟을 `EndChild()` 뒤로 이동하여 AssetList 영역 전체를 커버
  - `SaveDraggedGOsAsPrefab()` 끝에 `RebuildTree()` 호출 추가

## 검증
- dotnet build 성공 확인
- 유저 실행 테스트 필요 (Hierarchy에서 GO를 Project Panel 에셋 영역으로 드래그 드롭하여 prefab 생성 확인)

## 기술 참고
ImGui의 DragDrop 시스템에서 `BeginDragDropTarget()`는 "last item" 기반으로 동작한다:
- child window 내부에서 호출하면 마지막으로 그려진 위젯만 드롭 타겟이 됨
- `EndChild()` 뒤에 호출하면 child window 전체가 하나의 드롭 타겟이 됨
- 폴더 트리의 각 노드는 `TreeNodeEx` 바로 뒤에 `BeginDragDropTarget()`를 호출하므로 정상 동작함
