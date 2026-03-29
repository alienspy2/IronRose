# 돼지/폭탄 블록 파괴 시 VFX가 안 나오는 버그 수정

## 유저 보고 내용
- 돼지 블럭이 터질 때 VFX가 안 나옴
- 폭탄 블럭이 터질 때 VFX가 안 나옴

## 정적 분석 결과

### 문제 1: 돼지 파괴 시 VFX 없음
- PigScript.OnCollisionEnter()에서 `Object.Destroy(gameObject)`만 호출하고 부스러기 VFX를 생성하지 않음
- CannonballScript에서 Pig를 파괴할 때도 VFX 없이 바로 Destroy만 수행
- BombScript.Explode()에서도 "Pig" 태그 오브젝트를 처리하지 않아 폭발 범위 안의 돼지가 파괴되지 않음
- **원인**: 돼지 파괴 시 debris VFX를 호출하는 코드가 아예 구현되지 않았음

### 문제 2: 폭탄 폭발 시 VFX 없음 (진단 중)
- BombScript.Explode()에서 `explosionVfxPrefab` 프리팹을 Instantiate하는 코드가 있음
- PileScript.BuildPile()에서 BombScript 생성 시 `bomb.explosionVfxPrefab = explosionVfxPrefab` 전달
- PileScript의 explosionVfxPrefab은 pile.prefab에서 GUID로 설정됨 (ExplosionVFX.prefab)
- 코드 흐름 자체는 올바르게 보이나, 실행 시 explosionVfxPrefab이 null일 가능성 존재
- **진단 로그 추가 완료** -- 실행 결과 확인 필요

## 진단 로그 위치
- `PileScript.cs` Start()에서 explosionVfxPrefab null 여부 로그
- `BombScript.cs` Explode()에서 explosionVfxPrefab null 여부, VFX GO 생성 결과 로그
- `PigScript.cs` OnCollisionEnter()에서 돼지 파괴 시 로그

## 테스트 절차
1. 에디터를 실행하여 Play 모드 진입
2. 폭탄 블럭에 포탄을 발사하여 폭발 유도
3. 콘솔 로그에서 `[BombScript]`, `[PileScript]` 로그 확인
4. 특히 `explosionVfxPrefab null=True/False` 값 확인

## 다음 단계
- 진단 로그 결과를 보고 폭탄 VFX 원인 특정
- 돼지 VFX는 PigScript, CannonballScript, BombScript에 debris VFX 추가 구현
