# 폭탄 블록 폭발 시 빨간색 스피어 VFX 추가

## 유저 보고 내용
- 폭탄 블록(bomb block)이 터질 때 시각적 피드백이 없어서, 빨간색 반투명 스피어로 폭발 VFX를 추가해야 한다.
- 폭발 반경 크기에서 시작하여 scale이 0까지 줄어드는 애니메이션 필요.
- 애니메이션 완료 후 스피어 GO 제거.

## 원인
- 기존 BombScript.Explode()는 주변 Block 파괴 + 연쇄 폭발 + 자기 파괴만 수행하고 시각적 VFX가 없었음.

## 수정 내용
- `ExplosionVfxScript.cs` 신규 작성:
  - `SpawnAt(Vector3, float)` static 메서드로 폭발 위치에 빨간색 스피어 GO 생성
  - collider 없이 MeshFilter + MeshRenderer만으로 시각 전용 스피어 생성 (물리 간섭 방지)
  - 머티리얼: `mat_explosion_vfx.mat` 에셋을 GUID 기반으로 로드 (color alpha=0.8)
  - 초기 scale: 폭발 반경 x 2 (지름)
  - Update()에서 0.3초간 scale을 diameter에서 0으로 Lerp 축소
  - 축소 완료 시 `Object.Destroy(gameObject)`로 자동 제거
- `BombScript.cs` 수정:
  - `Explode()` 메서드에서 `Object.Destroy(gameObject)` 직전에 `ExplosionVfxScript.SpawnAt()` 호출 추가

### 설계 결정 사항
- BombScript 자체가 Explode() 끝에서 파괴되므로 코루틴 방식 대신 별도 GO + MonoBehaviour로 분리
- `GameObject.CreatePrimitive()`는 자동으로 collider를 추가하므로, 직접 GO를 만들어 MeshFilter/MeshRenderer만 추가 (VFX에 물리 충돌 불필요)

## 변경된 파일
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/ExplosionVfxScript.cs` -- 신규. 폭발 VFX 스피어 생성 및 축소 애니메이션 스크립트
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/BombScript.cs` -- Explode()에서 ExplosionVfxScript.SpawnAt() 호출 추가

## 검증
- LiveCode 빌드 성공 확인 (`dotnet build LiveCode/LiveCode.csproj`)
- IronRose 엔진 빌드 성공 확인 (`dotnet build`)
- 실제 동작은 에디터에서 Play 모드로 테스트 필요 (GUI 게임이므로 직접 실행 불가)
