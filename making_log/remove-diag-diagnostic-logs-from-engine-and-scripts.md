# [DIAG] 진단 로그 제거

## 수행한 작업
- 엔진 및 게임 스크립트 전반에서 디버깅용 `[DIAG]`, `[Diag]`, `[DIAG-PileScript]`, `[DIAG-BombScript]` 접두사 진단 로그를 모두 제거
- 기능 코드(가드, 로직, return문 등)는 그대로 유지

## 변경된 파일
- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` -- `[DIAG]` 접두사 EditorDebug 로그 4줄 제거 (캐시 HIT/MISS, FAILED_IMPORTS, import null 로그)
- `src/IronRose.Engine/RoseEngine/Object.cs` -- CopyFields의 explosionVfxPrefab 진단 로그 제거, Destroy의 _isEditorInternal 진단 로그 제거, val 변수를 인라인으로 복원
- `src/IronRose.Engine/RoseEngine/SceneManager.cs` -- RegisterBehaviour/Update/PendingStart/Clear/ExecuteDestroy에서 [Diag] 로그 블록 전체 제거, 미사용 bType 변수 제거, frontmatter 갱신
- `Scripts/AngryClawd/PileScript.cs` -- [DIAG-PileScript] 로그 제거, Start() 로그를 원래대로 복원
- `Scripts/AngryClawd/BombScript.cs` -- Explode() 내 [DIAG-BombScript] 로그 4줄 제거

## 주요 결정 사항
- SceneManager.cs의 `bType` 변수가 진단 로그에서만 사용되어 미사용 변수가 되므로 함께 제거
- BombScript.cs에서 `else` 블록(explosionVfxPrefab is null 분기)은 진단 로그만 출력하므로 통째로 제거
- PileScript.cs의 Start() 로그는 지시에 따라 `name=` 부분 없이 원래 형태로 복원

## 다음 작업자 참고
- 없음
