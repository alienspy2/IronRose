# CharacterController가 sleep 상태의 Rigidbody를 밀지 못하는 버그 수정

## 유저 보고 내용
- CharacterController.Move()로 Rigidbody가 있는 오브젝트를 밀 때, Rigidbody가 sleep 상태이면 밀리지 않음
- OnControllerColliderHit 콜백은 발생하지만, BepuPhysics에서 body가 sleep이면 충돌 반응이 없음

## 원인
- CharacterController.Move()의 sweep 충돌 처리에서 충돌 대상 dynamic body가 sleep 상태인지 확인하지 않았음
- BepuPhysics v2.4.0에서 sleep 상태의 body는 외부에서 명시적으로 깨우지 않으면 충돌 반응을 하지 않음
- PhysicsWorld3D에 body를 깨우는 public 메서드가 없었음

## 수정 내용
1. **PhysicsWorld3D.WakeBody(BodyHandle)** 메서드 추가
   - body 존재 확인 후 `Simulation.Awakener.AwakenBody(handle)` 호출
   - 이미 깨어있으면 아무 동작 안 함
   - 기존 SetBodyVelocity, ApplyLinearImpulse 등에서 동일한 패턴 사용 중이었으므로 일관성 유지

2. **CharacterController.Move()** sweep 충돌 처리에 WakeBody 호출 추가
   - 충돌 대상의 `CollidableReference.Mobility`가 Static이 아닌 경우 (Dynamic/Kinematic)
   - `mgr.World3D.WakeBody(hit.Collidable.BodyHandle)` 호출
   - OnControllerColliderHit 콜백 발송 직전에 배치 (body가 깨어난 상태에서 콜백 내 힘 적용이 정상 동작하도록)

## 변경된 파일
- `src/IronRose.Physics/PhysicsWorld3D.cs` -- WakeBody(BodyHandle) public 메서드 추가
- `src/IronRose.Engine/RoseEngine/CharacterController.cs` -- Move()의 sweep 충돌 시 dynamic body WakeBody 호출 추가

## 검증
- dotnet build 성공 (오류 0개)
- 실행 테스트는 유저 확인 필요 (CharacterController로 sleep 상태 Rigidbody를 밀어보기)
