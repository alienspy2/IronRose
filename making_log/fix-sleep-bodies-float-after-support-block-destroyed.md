# 지지 블록 파괴 시 위의 sleep 상태 블록이 공중에 떠 있는 버그 수정

## 유저 보고 내용
- 쌓여있는 블록의 아래 블록이 파괴될 때, 위에 있는 블록들이 sleep 상태로 남아서 공중에 떠 있는 채로 움직이지 않음
- 아래 블록이 없어지면 위 블록들이 깨어나서(wake up) 중력에 의해 떨어져야 함

## 원인
- BepuPhysics에서 body가 안정 상태에 도달하면 자동으로 sleep 상태가 됨 (에너지 절약)
- sleep 상태의 body는 외부에서 명시적으로 깨우지 않으면 시뮬레이션에 참여하지 않음
- `PhysicsWorld3D.RemoveBody()`에서 body를 제거할 때, 해당 body와 접촉(contact) 중인 다른 body들을 깨우는 로직이 없었음
- 따라서 지지 구조물(아래 블록)이 파괴되어도, 위의 sleep 상태 블록들은 접촉 상대가 사라진 것을 감지하지 못하고 공중에 떠 있게 됨

## 수정 내용
1. **ContactEventCollector.GetContactingIds(int collidableId)** 메서드 추가
   - `_previousContacts` (직전 Step의 접촉 쌍)에서 지정된 collidable ID와 접촉 중인 다른 ID 목록을 반환

2. **PhysicsWorld3D.WakeContactingBodies(int collidableId)** private 메서드 추가
   - `GetContactingIds`를 호출하여 접촉 중인 body 목록을 조회
   - 양수 ID (dynamic body)만 대상으로, body가 존재하고 sleep 상태인 경우에만 `Awakener.AwakenBody` 호출
   - 음수 ID (static body)는 깨울 필요 없으므로 스킵

3. **PhysicsWorld3D.RemoveBody()** 수정
   - `_simulation.Bodies.Remove(handle)` 호출 직전에 `WakeContactingBodies(handle.Value)` 호출
   - body가 실제로 제거되기 전에 접촉 정보가 유효한 시점에서 인접 body들을 깨움

### 수정이 근본적인 이유
- 게임 스크립트(BlockScript 등)에서 파괴 시 주변을 깨우는 코드를 추가하는 것은 workaround임
  - 모든 Destroy 호출 지점에 일일이 추가해야 하고 (BlockScript, CannonballScript, BombScript 등)
  - 새로운 스크립트가 추가되면 또 빠질 수 있음
- 물리 엔진 레벨(PhysicsWorld3D.RemoveBody)에서 처리하면, 어떤 경로로든 body가 제거될 때 항상 인접 body가 깨어남

## 변경된 파일
- `src/IronRose.Physics/PhysicsWorld3D.cs` -- ContactEventCollector에 GetContactingIds 메서드 추가, RemoveBody에서 body 제거 전 접촉 중인 body들을 wake up

## 검증
- dotnet build 성공 (오류 0개)
- 실행 테스트는 유저 확인 필요: AngryClawd 게임에서 쌓인 블록의 아래 블록을 포탄으로 파괴했을 때 위 블록들이 정상적으로 떨어지는지 확인
