# 폭발 VFX 미출현 - pile.prefab에 explosionVfxPrefab 에셋 링크 누락

## 유저 보고 내용
- 플레이 모드에서 폭탄이 폭발할 때 ExplosionVFX가 나타나지 않음.
- BombScript가 기존 static 메서드 방식에서 프리팹 Instantiate 방식으로 변경된 이후 발생.

## 원인
`pile.prefab` 파일의 PileScript 컴포넌트 필드에 `explosionVfxPrefab` 값이 설정되지 않았다.

BombScript.Explode()에서 `explosionVfxPrefab != null` 조건으로 VFX를 생성하는데, PileScript에서 BombScript에 주입하는 `explosionVfxPrefab`이 null이므로 VFX 생성 코드가 실행되지 않았다.

코드 변경 시 pile.prefab 데이터 파일의 PileScript 필드에 프리팹 에셋 참조를 추가하는 것을 누락한 것이 근본 원인.

## 수정 내용
`pile.prefab`의 PileScript 컴포넌트 fields에 `explosionVfxPrefab` 에셋 참조를 추가.
- `_assetGuid`: `dcc25465-58da-4eb6-a706-5ee27f37897f` (ExplosionVFX.prefab의 GUID)
- `_assetType`: `GameObject`

IronRose의 직렬화 인프라에서 `GameObject` 타입 필드는 `AssetReferenceTypes`에 포함되어 `_assetGuid`/`_assetType` 형태로 직렬화/역직렬화된다.

## 변경된 파일
- `IronRoseSimpleGameDemoProject/Assets/AngryClawdAssets/pile.prefab` -- PileScript 컴포넌트에 explosionVfxPrefab 에셋 링크 추가

## 검증
- 빌드 성공 확인 (`dotnet build`)
- 유저에게 플레이 모드에서 폭탄 폭발 시 VFX 출현 검증 요청 필요
