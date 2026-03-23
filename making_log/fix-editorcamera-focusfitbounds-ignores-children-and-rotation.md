# EditorCamera FocusFitBounds가 자식 오브젝트 bounds와 회전을 고려하지 않던 문제 수정

## 유저 보고 내용
- Hierarchy에서 GameObject를 더블클릭할 때 EditorCamera의 FocusFitBounds가 오브젝트의 scale을 고려하지 않는 것 같다는 피드백

## 원인
`FocusFitBounds` 메서드에 두 가지 문제가 있었다:

1. **자식 오브젝트 미포함**: 선택된 GameObject 자신의 `MeshFilter`만 확인하고, 자식 오브젝트들의 `MeshFilter`는 무시했다. 임포트된 3D 모델은 일반적으로 부모 GameObject에는 MeshFilter가 없고 자식에 메시가 있으므로, fallback bounds `(1,1,1)`이 사용되어 실제 모델 크기와 전혀 무관한 영역에 포커스되었다.

2. **회전 미반영**: 로컬 bounds의 size에 scale만 단순 곱하는 방식이라 오브젝트가 회전되어 있을 때 월드 공간 AABB가 정확하지 않았다.

## 수정 내용
`FocusFitBounds` 메서드를 완전히 재작성:

- `go.transform.GetComponentsInChildren<MeshFilter>()`로 자신 및 모든 자식의 MeshFilter를 순회
- 각 MeshFilter의 로컬 bounds를 해당 오브젝트의 world transform (position, rotation, lossyScale)을 사용하여 정확한 월드 공간 AABB로 변환
  - 회전이 있는 경우에도 정확한 AABB를 계산하기 위해 OBB 축 투영 방식 사용 (로컬 extents의 각 축을 회전 적용 후 절대값으로 world extents 합산)
- 모든 자식의 world bounds를 `Bounds.Encapsulate()`로 합산
- MeshFilter가 하나도 없는 경우 fallback으로 오브젝트 위치에 lossyScale 기반 bounds 사용

## 변경된 파일
- `src/IronRose.Engine/Editor/SceneView/EditorCamera.cs` -- `FocusFitBounds` 메서드를 자식 MeshFilter 합산 및 회전 고려 방식으로 재작성

## 검증
- `dotnet build` 빌드 성공 확인 (오류 0개)
- 유저 확인 필요: Hierarchy에서 다양한 오브젝트 (scale 변경, 자식 메시 포함, 회전 적용 등)를 더블클릭하여 카메라 포커스가 정확한지 확인
