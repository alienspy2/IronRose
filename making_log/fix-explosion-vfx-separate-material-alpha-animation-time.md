# 폭탄 폭발 VFX 머티리얼 분리, alpha 조정, 애니메이션 시간 확인

## 유저 보고 내용
- 폭탄 폭발 VFX의 머티리얼을 별도 에셋으로 분리해야 한다.
- alpha 값을 0.8로 설정해야 한다.
- 애니메이션 시간을 0.3초로 줄여야 한다.

## 원인
- 기존 `ExplosionVfxScript.SpawnAt()`에서 `new Material(new Color(1f, 0.2f, 0.1f, 0.5f))`로 인라인 생성하고 있었음.
  - 머티리얼이 코드에 하드코딩되어 에디터에서 조정 불가.
  - alpha 값이 0.5로 설정되어 있었음 (요구 사항: 0.8).
- 애니메이션 시간(SHRINK_DURATION)은 이미 0.3초로 설정되어 있어 변경 불필요.

## 수정 내용
1. **머티리얼 에셋 분리**: `mat_explosion_vfx.mat` + `mat_explosion_vfx.mat.rose` 에셋 파일 신규 생성.
   - color: `(1.0, 0.2, 0.1, 0.8)` -- 빨간색 반투명, alpha 0.8.
2. **코드 수정**: `ExplosionVfxScript.SpawnAt()`에서 인라인 Material 생성 대신 `Resources.GetAssetDatabase().LoadByGuid<Material>(GUID)` 패턴으로 에셋 로드.
   - 에셋 로드 실패 시 폴백으로 인라인 Material 생성 (alpha 0.8 적용).
   - PileScript 등 다른 스크립트와 동일한 GUID 기반 로드 패턴 사용.
3. **애니메이션 시간**: 기존 0.3초 유지 (변경 불필요).

## 변경된 파일
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/ExplosionVfxScript.cs` -- 인라인 Material 생성을 GUID 기반 에셋 로드로 변경, alpha 폴백값 0.8
- `/home/alienspy/git/MyGame/Assets/AngryClawdAssets/mat_explosion_vfx.mat` -- 신규. 폭발 VFX 전용 머티리얼 에셋 (color alpha=0.8)
- `/home/alienspy/git/MyGame/Assets/AngryClawdAssets/mat_explosion_vfx.mat.rose` -- 신규 (에디터 자동 생성). 머티리얼 에셋 메타 파일

## 검증
- LiveCode 빌드 성공 확인 (`dotnet build LiveCode/LiveCode.csproj`)
- IronRose 엔진 빌드 성공 확인 (`dotnet build`)
- 실제 VFX 동작은 에디터에서 Play 모드로 테스트 필요 (GUI 게임이므로 직접 실행 불가)
