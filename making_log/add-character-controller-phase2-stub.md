# CharacterController Phase 2 - 타입 정의 + 기본 골격

## 수행한 작업
- CollisionFlags 열거형 생성 (None, Sides, Above, Below 플래그)
- ControllerColliderHit 충돌 정보 클래스 생성
- CharacterController 컴포넌트 stub 생성 (Collider 상속, Move/SimpleMove는 빈 구현)
- MonoBehaviour에 OnControllerColliderHit 콜백 추가

## 변경된 파일
- `src/IronRose.Engine/RoseEngine/CollisionFlags.cs` — 신규 생성. [Flags] 열거형
- `src/IronRose.Engine/RoseEngine/ControllerColliderHit.cs` — 신규 생성. 충돌 정보 클래스
- `src/IronRose.Engine/RoseEngine/CharacterController.cs` — 신규 생성. Collider 상속, 프로퍼티 정의, RegisterAsStatic 캡슐 등록, Move/SimpleMove stub
- `src/IronRose.Engine/RoseEngine/MonoBehaviour.cs` — OnControllerColliderHit 콜백 추가

## 주요 결정 사항
- RegisterAsStatic에서 CapsuleCollider 패턴을 그대로 따르되, halfLength 계산만 설계 문서에 따라 `(scaledHeight - 2*scaledRadius) / 2` 사용
- `_kinematicHandle`을 `_staticHandle`과 동일하게 설정 (Phase 6에서 pose 동기화 시 사용 예정)
- OnDrawGizmosSelected에서 skinWidth 포함/미포함 두 개의 와이어 캡슐을 그려 시각적으로 구분
- Gizmo에서 scale은 Vector3.one 사용 (CharacterController는 스케일 무시, Unity와 동일)

## 다음 작업자 참고
- Phase 3: Move() 핵심 로직 구현 필요 — PhysicsWorld3D.SweepCapsule API가 Phase 1에서 구현되어 있어야 함
- Phase 5: SimpleMove() 구현 시 `_simpleMoveVerticalSpeed` 필드 사용 예정 (현재 미사용 경고 발생)
- Phase 6: PhysicsManager에 CharacterController static body pose 동기화 추가 필요
- `_kinematicHandle` 필드는 internal 접근자로, PhysicsManager에서 접근 가능
