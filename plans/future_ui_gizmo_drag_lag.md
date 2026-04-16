# UI Transform Gizmo: 드래그 중 기즈모가 element를 따라오지 않음

## 현상
- 3D 메쉬: TransformGizmo로 위치 이동 시 기즈모가 동시에 따라감.
- 2D UI (RectTransform): UITransformGizmo2D로 element를 잡고 드래그하면 element만 이동하고 기즈모는 시작 위치에 머물러 있음.

## 원인 추정
- `UITransformGizmo2D.DrawTranslateOverlay`가 드래그 중에는 `_dragGizmoCenter`(드래그 시작 시 캡처한 화면 좌표)를 사용하고, 그 외에는 `GetMultiGizmoScreenPos(rt, mode)`(현재 RT의 lastScreenRect 기반)를 사용.
- 드래그 중 element의 anchoredPosition은 갱신되지만, 화면에 그려지는 기즈모는 `_dragGizmoCenter`로 고정되어 있어 따라가지 않음.
- 3D는 드래그 중에도 `transform.position`을 매 프레임 읽어 `objPos`를 계산하므로 자연스럽게 따라감.

## 관련 파일
- `src/IronRose.Engine/Editor/SceneView/UITransformGizmo2D.cs`
  - `DrawTranslateOverlay` (`_isDragging ? _dragGizmoCenter : ...`)
  - `DrawRotateOverlay`, `DrawScaleOverlay`도 같은 패턴.
- 비교: `src/IronRose.Engine/Editor/SceneView/TransformGizmo.cs:Render`

## 메모
- Translate에서는 기즈모가 따라가야 자연스러움.
- Rotate에서는 기즈모 중심이 고정되어야 함(orbit pivot이라서 의도된 동작).
- Scale은 위치가 안 바뀌므로 차이 없음.
- 즉, Translate만 드래그 중에도 live center를 사용하도록 분기하거나, `_dragGizmoCenter`에 누적 delta를 더해 그리는 방식이 필요.
