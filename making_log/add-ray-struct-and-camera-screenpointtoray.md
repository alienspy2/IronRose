# Ray struct 생성 및 Camera.ScreenPointToRay 구현

## 수행한 작업
- Unity API 호환 `Ray` struct를 새로 생성
- `Camera` 클래스에 `ScreenPointToRay(Vector3 screenPoint)` 메서드를 추가

## 변경된 파일
- `src/IronRose.Engine/RoseEngine/Ray.cs` — 새 파일. `origin`, `direction` 필드, `GetPoint()`, `ToString()` 메서드 포함. 생성자에서 direction을 자동 정규화.
- `src/IronRose.Engine/RoseEngine/Camera.cs` — `ScreenPointToRay` 메서드 추가. 화면 좌표(좌하단 원점, Unity 컨벤션)를 NDC로 변환 후 FOV/aspect를 이용해 뷰 공간 방향을 계산하고, transform의 forward/right/up으로 월드 공간 레이를 생성. frontmatter 추가.

## 주요 결정 사항
- Unity 호환을 위해 `origin`, `direction` 필드명을 camelCase로 사용 (기존 에디터 내부의 private Ray struct는 PascalCase Origin/Direction이지만, Unity public API 호환이 목적이므로 Unity와 동일하게)
- `ScreenPointToRay`의 NDC 변환에서 Y를 뒤집지 않음. Unity 화면 좌표는 좌하단 원점이므로 ndcY = (screenPoint.y / screenH) * 2 - 1로 그대로 매핑
- `Screen.width`/`Screen.height`를 사용하여 화면 크기를 가져옴. aspect도 이로부터 계산

## 다음 작업자 참고
- 에디터 코드(TransformGizmo.cs, ColliderGizmoEditor.cs)에 `private struct Ray`가 각각 정의되어 있다. 이들을 `RoseEngine.Ray`로 통합하는 리팩토링이 가능하지만, 에디터 Ray는 PascalCase 필드(Origin/Direction)를 사용하므로 필드명 변환이 필요함.
- `ScreenPointToRay`는 perspective projection만 지원. orthographic projection이 필요하면 별도 분기 로직 추가 필요.
