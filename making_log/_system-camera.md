# Camera 시스템

## 구조
- `src/IronRose.Engine/RoseEngine/Camera.cs` — Camera 컴포넌트 클래스. Component를 상속.
  - 뷰/프로젝션 매트릭스 생성, ScreenPointToRay, 기즈모 렌더링
- `src/IronRose.Engine/RoseEngine/Ray.cs` — Ray struct (origin + direction). Unity 호환 API.
- `src/IronRose.Engine/RoseEngine/Screen.cs` — 화면 크기(width/height/dpi) 정적 클래스. Camera.ScreenPointToRay에서 참조.

## 핵심 동작

### Camera.main 자동 등록
- `OnAddedToGameObject()`에서 `main == null`이고 에디터 내부 오브젝트가 아닌 경우 자동으로 `main`에 등록
- `OnComponentDestroy()`에서 `main == this`이면 null로 해제

### ScreenPointToRay 변환 흐름
1. 화면 좌표(좌하단 원점, Unity 컨벤션)를 NDC(-1~1)로 변환
2. NDC를 FOV와 aspect ratio로 뷰 공간 방향 벡터로 변환
3. transform의 forward/right/up 벡터로 월드 공간 방향으로 변환
4. 카메라 위치를 origin으로, 계산된 방향을 direction으로 Ray 생성

### 에디터 카메라와의 관계
- 에디터(TransformGizmo, ColliderGizmoEditor)는 `EditorCamera`를 사용하며 별도의 private `ScreenToRay` 메서드를 가짐
- 게임 런타임에서는 `Camera.ScreenPointToRay`를 사용

## 주의사항
- `ScreenPointToRay`는 perspective projection만 지원. orthographic camera에는 별도 처리가 필요.
- `Screen.width`/`Screen.height`가 올바르게 설정된 후에 호출해야 정확한 결과를 얻음.
- Ray 생성자에서 direction을 자동 정규화하므로 ScreenPointToRay에서도 direction이 이중 정규화될 수 있으나, 이미 normalized된 벡터의 re-normalize는 부작용 없음.

## 사용하는 외부 라이브러리
- 없음 (모두 엔진 내부 수학 타입 사용)
