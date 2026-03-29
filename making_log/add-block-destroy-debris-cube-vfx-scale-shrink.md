# 블록 파괴 시 큐브 부스러기 VFX 추가

## 유저 보고 내용
- 폭탄블럭을 제외한 다른 모든 블럭이 파괴될 때, 물리가 적용되지 않는 작은 큐브들을 뿌리는 VFX를 추가해야 한다.
- 부스러기(큐브)들은 scale 애니메이션으로 줄어들면서 사라져야 한다.

## 구현 내용

### DebrisVfxScript.cs (신규)
- `SpawnAt(Vector3 position, Color color)` static 메서드로 부스러기 큐브 6개 생성
- 각 큐브는 크기 0.15f의 MeshFilter + MeshRenderer 전용 GO (collider/Rigidbody 없음)
- 원본 블록의 머티리얼 색상을 그대로 사용
- 랜덤 방향으로 1.0~3.0 속도로 흩어짐 (Random.insideUnitSphere.normalized * speed)
- 0.5초에 걸쳐 DEBRIS_SIZE에서 0으로 scale Lerp 축소
- 축소 완료 시 Object.Destroy(gameObject)로 자동 제거

### BlockScript.cs 수정
- `SpawnDebris()` public 메서드 추가: MeshRenderer에서 머티리얼 색상을 읽어 DebrisVfxScript.SpawnAt() 호출
- OnCollisionEnter() 내 Destroy 직전에 SpawnDebris() 호출
- SpawnDebris()를 public으로 두어 외부(CannonballScript, BombScript)에서도 호출 가능

### CannonballScript.cs 수정
- Block 파괴 시 BlockScript.SpawnDebris()를 먼저 호출한 뒤 Destroy

### BombScript.cs 수정
- Explode() 내 Block 파괴 루프에서 BlockScript.SpawnDebris()를 먼저 호출한 뒤 Destroy

### 설계 결정 사항
- ExplosionVfxScript와 동일한 패턴 (별도 GO + MonoBehaviour로 분리)
- BlockScript에 SpawnDebris()를 public으로 두어 VFX 생성 로직을 한 곳에 집중시킴
- 폭탄블럭(Bomb 태그)에는 이 VFX를 적용하지 않음 (요구사항)
- Pig에도 적용하지 않음 (블럭만 대상)

## 변경된 파일
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/DebrisVfxScript.cs` -- 신규. 블록 부스러기 VFX 큐브 생성 및 축소 애니메이션
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/BlockScript.cs` -- SpawnDebris() public 메서드 추가, OnCollisionEnter에서 호출
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/CannonballScript.cs` -- Block 파괴 시 SpawnDebris() 호출 추가
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/BombScript.cs` -- Explode() 내 Block 파괴 시 SpawnDebris() 호출 추가
- `/home/alienspy/git/IronRose/making_log/_system-angryclawd-game.md` -- DebrisVfxScript 항목 추가
- `/home/alienspy/git/IronRose/making_log/_system-angry-clawd-game.md` -- DebrisVfxScript 항목 추가

## 검증
- LiveCode 빌드 성공 확인 (`dotnet build LiveCode/LiveCode.csproj` -- 0 Error, 0 Warning)
- IronRose 엔진 빌드 성공 확인 (`dotnet build`)
- 실제 동작은 에디터에서 Play 모드로 테스트 필요 (GUI 게임이므로 직접 실행 불가)
