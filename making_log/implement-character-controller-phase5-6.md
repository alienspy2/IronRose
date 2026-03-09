# CharacterController Phase 5+6: SimpleMove + Overlap Recovery + PhysicsManager 통합

## 수행한 작업
- **SimpleMove() 구현**: XZ 평면 speed * deltaTime 이동 + Physics.gravity 기반 수직 속도 누적. isGrounded 시 수직 속도를 -0.5f로 리셋하여 바닥 접착. 내부적으로 Move() 호출.
- **enableOverlapRecovery 구현**: Move() 시작 전 OverlapCapsule로 겹침 검사, 겹친 물체가 있으면 위 방향으로 skinWidth만큼 밀어내기 (최대 4회 반복)
- **PhysicsManager.SyncCharacterControllerPoses() 추가**: PushTransformsToPhysics() 마지막에 모든 CharacterController의 static body pose를 동기화
- **CharacterController.SyncStaticPose() 추가**: PhysicsManager에서 호출하는 internal 메서드 (protected GetWorldPosition/GetWorldRotation 접근 문제 해결)
- **CharacterController + Rigidbody 경고 추가**: EnsureStaticColliders()에서 CC가 있는 GO에 Rigidbody가 있으면 LogWarning 출력
- **PhysicsManager.cs frontmatter 추가**
- **CharacterController.cs frontmatter 갱신**: SimpleMove, SyncStaticPose, overlap recovery 관련 내용 반영

## 변경된 파일
- `src/IronRose.Engine/RoseEngine/CharacterController.cs` — SimpleMove() 구현, PerformOverlapRecovery() 추가, SyncStaticPose() 추가, Move()에 overlap recovery 호출 추가, frontmatter 갱신
- `src/IronRose.Engine/Physics/PhysicsManager.cs` — SyncCharacterControllerPoses() 추가, EnsureStaticColliders()에 CC+RB 경고 추가, frontmatter 추가

## 주요 결정 사항
- **Overlap Recovery 방향**: 정밀한 depenetration 벡터 계산 대신, 겹침 감지 시 Vector3.up 방향으로 skinWidth만큼 밀어내는 간단한 구현 채택. 대부분의 경우 위로 밀어내면 충분하며, 복잡한 구현은 불필요하다는 작업 지시에 따름.
- **SyncStaticPose 패턴**: GetWorldPosition/GetWorldRotation이 protected이라 PhysicsManager에서 직접 호출 불가. CharacterController에 internal SyncStaticPose(PhysicsManager) 메서드를 추가하여 해결.
- **SimpleMove 수직 속도 리셋값**: isGrounded 시 0이 아닌 -0.5f로 리셋. 약간의 하향 속도를 유지하여 바닥에 붙어있도록 함 (Unity 동일 패턴).
- **CC+RB 경고 위치**: EnsureStaticColliders()에서 Rigidbody 체크 시 CC인 경우 경고 출력. OnAddedToGameObject()보다 이 위치가 적절 — 런타임에 Rigidbody가 나중에 추가되는 경우도 감지 가능.

## 다음 작업자 참고
- Overlap Recovery는 현재 단순히 위로 밀어내는 방식이므로, 옆벽에 끼인 경우에는 효과가 제한적임. 추후 정밀한 depenetration이 필요하면 OverlapCapsule 결과에서 개별 충돌체 방향을 계산해야 함.
- SyncCharacterControllerPoses()는 매 FixedUpdate마다 _allColliders를 순회하므로, CC가 많은 씬에서는 성능 고려 필요. 별도 리스트로 관리하는 방법도 있음.
- Phase 1~6 모두 완료됨. CharacterController 기능 구현 완료 상태.
