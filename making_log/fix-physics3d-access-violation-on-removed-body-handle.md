# PhysicsWorld3D AccessViolationException 수정

## 유저 보고 내용
- `PhysicsWorld3D.GetBodyVelocity`에서 `AccessViolationException` 발생
- BepuPhysics.dll에서 보호된 메모리 읽기/쓰기 시도 에러
- 이미 제거된 body의 handle로 접근하거나, 시뮬레이션 Reset 후 무효화된 handle 사용이 원인으로 추정

## 원인
- `PhysicsWorld3D`의 body 접근 메서드들(`GetBodyVelocity`, `SetBodyVelocity`, `GetBodyPose`, `SetBodyPose`, `ApplyLinearImpulse`, `ApplyAngularImpulse`, `RemoveBody`, `SetBodyUseGravity`)이 `BodyHandle` 유효성을 검증하지 않고 `_simulation.Bodies[handle]`에 직접 접근
- `Rigidbody.cs`에서는 `bodyHandle != null` 체크만 수행하여 handle이 할당되었는지만 확인하고, 실제 물리 시뮬레이션에 body가 존재하는지는 확인하지 않음
- 씬 전환 시 `PhysicsWorld3D.Reset()`이 시뮬레이션을 폐기 후 재생성하면 기존 handle이 모두 무효화되는데, Rigidbody 측에서 아직 이전 handle을 보유하고 있을 수 있음
- 무효한 handle로 BepuPhysics의 내부 배열에 접근하면 해제된 메모리를 읽으려 하여 `AccessViolationException` 발생

## 수정 내용
- BepuPhysics의 `Bodies.BodyExists(BodyHandle)` API를 활용하여 모든 body 접근 메서드에 guard 체크 추가
- 무효한 handle 접근 시 `Debug.LogWarning`으로 경고 로그를 출력하고 안전하게 반환 (getter는 `default` 반환, setter/void는 조기 반환)
- 외부에서도 사용할 수 있도록 `PhysicsWorld3D.BodyExists(BodyHandle)` 공개 메서드 추가
- 적용된 메서드 목록:
  - `GetBodyPose` -- 무효 handle 시 `default(RigidPose)` 반환
  - `GetBodyVelocity` -- 무효 handle 시 `default(BodyVelocity)` 반환
  - `SetBodyVelocity` -- 무효 handle 시 조기 반환
  - `ApplyLinearImpulse` -- 무효 handle 시 조기 반환
  - `ApplyAngularImpulse` -- 무효 handle 시 조기 반환
  - `SetBodyPose` -- 무효 handle 시 조기 반환
  - `SetBodyUseGravity` -- 무효 handle 시 조기 반환
  - `RemoveBody` -- 무효 handle 시 조기 반환 (`_noGravityBodies` 정리는 수행)

## 변경된 파일
- `src/IronRose.Physics/PhysicsWorld3D.cs` -- 모든 body 접근 메서드에 `BodyExists` guard 추가, `BodyExists` 공개 메서드 추가

## 검증
- `dotnet build` 빌드 성공 (에러 0, 기존 경고만 존재)
- 정적 분석으로 원인 특정 (BepuPhysics XML 문서에서 `Bodies.BodyExists` API 확인)

## 주의사항
- `RemoveStatic(StaticHandle)` 메서드에는 아직 guard가 없음. BepuPhysics의 `Statics`에 유사한 `StaticExists` API가 있는지 추후 확인 필요
- 씬 전환 시 `PhysicsWorld3D.Reset()` 호출 후 `Rigidbody`의 `bodyHandle`을 null로 초기화하는 흐름이 정확히 동기화되는지 추가 검토 권장
