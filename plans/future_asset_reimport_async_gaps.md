# Asset Reimport: 비동기 경로 누락·importer 커버리지 개선

## 배경

`Merge: fix asset reimport resilience to in-flight file writes` (2026-04-14)에서 동기 `Reimport(path)` 경로의 예외 안전성을 복구했다. 같은 리뷰 과정에서 **이번 스코프 밖**으로 남겨둔 두 건이 있음. 아래는 후속 작업 메모.

## Gap 1. 비동기 `ProcessReimport` 성공 시 `ReimportVersion++` 누락

### 현상
- 동기 `Reimport(path)` 성공 후에는 `ReimportVersion++`이 실행되어 Inspector 등 버전 기반 소비자가 preview 캐시를 무효화함.
- 비동기 `ProcessReimport` (ReimportAsync 큐잉 → 메인 스레드에서 완료 처리) **성공 분기에서는** `ReimportVersion++`이 없음.
- 결과: async reimport 경로를 타는 에셋 변경 시 Inspector preview가 stale 값 유지 가능. (Play mode 전환 등으로 우회될 수 있어 잘 드러나지 않음.)

### 예상 수정
- `ProcessReimport`의 성공 분기(메인 스레드에서 `_loadedAssets` 스왑 + `ReplaceSpriteInScene` 호출 직후)에서 `ReimportVersion++` 한 번 호출.
- 실패(IsFaulted) 분기는 현행 유지 (이미 oldAsset 복원 + depth 복원 포함).
- 동기/비동기 경로가 동일한 후처리 루틴을 공유하도록 private helper로 추출하는 것도 고려.

### 관련 파일
- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` — `ProcessReimport()` 부근.

### 검증
- 텍스처를 async reimport 경로로 갱신 (FileSystemWatcher Changed → debounce 통과 후 비동기 큐잉) → Inspector의 preview 캐시(ReimportVersion 기반)가 즉시 새 값 반영되는지.

---

## Gap 2. `Reimport` switch에 `AnimationClipImporter` case 누락

### 현상
- `AssetDatabase.cs`의 다른 switch(`AssetDatabase.cs:288` 부근)에서는 `AnimationClipImporter`를 처리하지만, `Reimport(path)`의 importer dispatch switch에는 case가 없음.
- 결과: 애니메이션 클립 에셋이 reimport 대상에 들어오면 어떤 case에도 매치되지 않아 `reimportSucceeded = false`로 판정 → `ReimportVersion++` 누락 + finally의 복원 분기가 오발동하여 oldAsset 덮어쓸 가능성.

### 예상 수정
- `Reimport` switch에 `AnimationClipImporter` 분기 추가. 기존 import 로직을 `ScanAssets`/다른 경로에서 가져와 일관되게.
- 성공 시 `_loadedAssets[path] = newClip;` + `reimportSucceeded = true`.

### 관련 파일
- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` — `Reimport(path)` 내부 switch 블록.
- 다른 switch(`:288` 부근)에서 AnimationClipImporter 처리 방식 참고.

### 검증
- 프로젝트에 AnimationClip 에셋이 존재하는지 확인(없으면 테스트용 생성).
- `.anim` 파일 수정 → FileSystemWatcher → 씬의 Animator가 새 클립을 반영하는지.
- 유닛이 없어도 `asset.import <path>` + Inspector reload로 반영 확인 가능.

---

## 우선순위 / 리스크

- **Gap 1**: 경미한 UX 버그(Inspector stale preview). 작업량 적음(한 줄~함수 추출). 우선 처리 권장.
- **Gap 2**: AnimationClip을 쓰기 시작하면 즉시 드러남. 현재 데모 프로젝트에서 AnimationClip 사용 여부 확인 후 우선순위 조정.

둘 다 동일 파일 수정이라 한 번에 묶어 작업하는 편이 효율적. `aca-fix`로 worktree 분리해 진행 가능.
