# CharacterController Phase 1: Sweep API + UserData 매핑 추가

## 수행한 작업
- PhysicsWorld3D에 SweepHit 구조체 정의
- ClosestHitHandler (ISweepHitHandler 구현) 내부 구조체 추가 — 자기 자신 BodyHandle/StaticHandle 제외 기능 포함
- OverlapCollectHandler (ISweepHitHandler 구현) 내부 구조체 추가 — 겹침 검출용
- SweepCapsule() 메서드 구현 — BepuPhysics Simulation.Sweep 사용
- OverlapCapsule() 메서드 구현 — 짧은 거리 sweep으로 겹침 검출
- SetStaticPose() 메서드 추가
- UserData 매핑 (bodyUserData, staticUserData Dictionary) 추가
- RemoveBody/RemoveStatic/Reset에서 UserData 정리 로직 추가
- 기존 Collider 서브클래스 4개(Box, Sphere, Capsule, Cylinder)의 RegisterAsStatic에 SetStaticUserData 호출 추가
- Rigidbody의 RegisterWithPhysics에 SetBodyUserData 호출 추가 (kinematic + dynamic 모두)

## 변경된 파일
- `src/IronRose.Physics/PhysicsWorld3D.cs` — SweepHit, ClosestHitHandler, OverlapCollectHandler 구조체 추가; SweepCapsule, OverlapCapsule, SetStaticPose, UserData 관련 메서드 추가; RemoveBody/RemoveStatic/Reset에 UserData 정리
- `src/IronRose.Engine/RoseEngine/BoxCollider.cs` — RegisterAsStatic에 SetStaticUserData 호출 추가
- `src/IronRose.Engine/RoseEngine/SphereCollider.cs` — RegisterAsStatic에 SetStaticUserData 호출 추가
- `src/IronRose.Engine/RoseEngine/CapsuleCollider.cs` — RegisterAsStatic에 SetStaticUserData 호출 추가
- `src/IronRose.Engine/RoseEngine/CylinderCollider.cs` — RegisterAsStatic에 SetStaticUserData 호출 추가
- `src/IronRose.Engine/RoseEngine/Rigidbody.cs` — RegisterWithPhysics에서 kinematic/dynamic 모두 SetBodyUserData 호출 추가

## 주요 결정 사항
- ISweepHitHandler.OnHit의 Vector3 파라미터는 `in` 수식자 사용 (BepuPhysics v2.4.0 인터페이스 규격)
- OverlapCapsule은 매우 짧은 거리(0.001f)로 sweep하여 겹침을 검출하는 간단한 방식 채택 — 추후 정밀 overlap이 필요하면 BepuPhysics의 ContactQuery 등으로 교체 가능
- OverlapCollectHandler는 Span을 필드로 가질 수 없으므로 (ref struct가 아닌 일반 struct여야 ISweepHitHandler 구현 가능) 배열로 버퍼링 후 결과를 Span에 복사하는 방식 사용
- SweepCapsule의 excludeStatic 파라미터는 CharacterController가 자기 자신의 static body를 제외하는 용도
- sweepDuration=1.0f, velocity=direction*maxDistance 방식으로 t가 0~1 범위로 정규화됨

## 다음 작업자 참고
- Phase 3에서 CharacterController.Move()가 SweepCapsule을 호출할 때 excludeStatic에 자신의 _staticHandle을 전달해야 함
- GetUserData로 CollidableReference에서 Collider/Rigidbody 컴포넌트를 역매핑할 수 있음
- Rigidbody에서 Collider가 아닌 Rigidbody 자체를 UserData로 설정하고 있음 — CharacterController에서 충돌 상대가 Rigidbody인지 Collider인지 구분 시 GetUserData 타입 체크 필요
