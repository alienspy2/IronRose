# CharacterController Move() 핵심 로직 + 슬로프/스텝 처리 구현

## 수행한 작업
- CharacterController.Move()를 stub에서 완전한 sweep 기반 충돌 감지 이동으로 구현
- 최대 3회 slide iteration으로 벽/바닥/천장 슬라이딩 처리
- slopeLimit 초과 경사면에서 수평 슬라이딩 적용
- isGrounded && Sides 충돌 시 stepOffset 이내 높이차 자동 계단 오르기 (TryStepUp)
- OnControllerColliderHit 콜백 발송 (FireControllerColliderHit)
- RegisterAsStatic에 SetStaticUserData 호출 추가 (Phase 1에서 누락되었던 부분)
- Move() 완료 후 static body pose를 SetStaticPose로 동기화

## 변경된 파일
- `src/IronRose.Engine/RoseEngine/CharacterController.cs` — Move() 전체 구현, TryStepUp/FireControllerColliderHit private 메서드 추가, RegisterAsStatic에 UserData 등록 추가, frontmatter 갱신

## 주요 결정 사항
- **타입 변환**: RoseEngine.Vector3 <-> System.Numerics.Vector3 변환은 클래스 내부 private static 헬퍼로 처리
- **skinWidth 처리**: sweep 시 radius에서 skinWidth를 빼서 sweepRadius로 사용. 충돌 시 skinWidth만큼 뒤에서 멈춤
- **transform.position 역변환**: position = transform.TransformPoint(center)에서 역으로 transform.position을 구할 때 rotation * scaledCenter를 빼는 방식 사용
- **Step 처리**: 3단계 sweep (위-앞-아래)로 계단 오르기 판정. 바닥 법선이 GROUND_NORMAL_THRESHOLD 미만이면 경사면으로 간주하여 거부
- **Slope 처리**: 바닥 충돌이면서 경사각 > slopeLimit일 때 법선의 수평 성분 방향으로 슬라이딩
- **UserData 누락 수정**: RegisterAsStatic에서 SetStaticUserData(handle, this)를 호출하지 않던 버그를 수정

## 다음 작업자 참고
- Phase 5 SimpleMove()는 아직 stub 상태 (_simpleMoveVerticalSpeed 필드가 예약되어 있음)
- Phase 6에서 PhysicsManager 통합 시 CharacterController의 pose 동기화 타이밍을 확인해야 함
- enableOverlapRecovery는 현재 미사용 — Phase 5에서 OverlapCapsule과 함께 구현 예정
- 현재 Move()는 호출 시점의 transform.lossyScale을 사용 — 런타임 스케일 변경 시 캡슐 크기가 즉시 반영됨
