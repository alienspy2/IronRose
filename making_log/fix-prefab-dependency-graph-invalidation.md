# Prefab Dependency Graph 기반 Cascade Invalidation 구현

## 유저 보고 내용
- `PrefabEditMode.Save()`에서 `db.UnloadAllPrefabs()`를 호출하여 모든 프리팹 캐시를 일괄 삭제하는 비효율적 구현
- 프리팹이 많아지면 불필요한 캐시 무효화로 인한 성능 저하 우려
- Dependency graph 기반 cascade invalidation으로 교체 필요

## 원인
기존 `fix-nested-prefab-stale-cache`에서 nested prefab 수정 시 부모 캐시에 stale 데이터가 남는 문제를 "모든 prefab 캐시 일괄 삭제"로 해결했으나, 이는 근본적 해결이 아닌 임시 방편이었음.

## 수정 내용

### 1. Prefab Dependency Graph 구축 (`AssetDatabase`)
- `_prefabDependents`: `Dictionary<string, HashSet<string>>` (childGuid -> 이 child를 참조하는 parent guid들)
- `_prefabDependencies`: `Dictionary<string, HashSet<string>>` (parentGuid -> 이 parent가 참조하는 child guid들, 그래프 갱신 시 역참조 정리용)
- 프리팹 로드 시(`ImportPrefab()`) TOML 파싱하여 `prefabInstance.prefabGuid`와 `prefab.basePrefabGuid`를 수집하고 그래프에 등록

### 2. `UpdatePrefabDependencies(string parentGuid, string prefabPath)` 추가
- 기존 의존성을 정리한 후 TOML 파일을 파싱하여 새 의존성 수집
- Variant의 basePrefabGuid와 nested prefab의 prefabGuid 두 가지 경로 모두 추적
- 역참조 맵(`_prefabDependents`)을 갱신

### 3. `InvalidatePrefabAndDependents(string guid)` 추가
- BFS로 수정된 프리팹에서 시작하여 이를 참조하는 부모 프리팹들을 재귀적으로 탐색
- `visited` 집합으로 순환 참조에 안전
- 탐색된 프리팹의 캐시(`_loadedAssets`)만 선별적으로 제거

### 4. `UnloadAllPrefabs()` 제거
- 기존 메서드 완전 제거
- `PrefabEditMode.Save()`: `db.UnloadAllPrefabs()` -> `db.InvalidatePrefabAndDependents(guid)` 교체
- `AssetDatabase.Reimport()` PrefabImporter 케이스: 동일하게 교체

### 5. 정리 코드
- `RemovePrefabDependencies(string guid)`: 프리팹 언로드/삭제 시 그래프에서 해당 항목 제거
- `UnregisterAsset()`: 프리팹 삭제 시 dependency graph 정리 호출
- `UnloadAll()`: 전체 언로드 시 dependency graph 클리어

## 변경된 파일
- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` -- Dependency graph 필드 추가, `UnloadAllPrefabs()` 제거, `InvalidatePrefabAndDependents()`/`UpdatePrefabDependencies()`/`RemovePrefabDependencies()` 추가, `ImportPrefab()` 수정, `Reimport()` PrefabImporter 케이스 수정, `UnregisterAsset()` 수정, `UnloadAll()` 수정
- `src/IronRose.Engine/Editor/PrefabEditMode.cs` -- `Save()`에서 `db.UnloadAllPrefabs()` -> `db.InvalidatePrefabAndDependents(guid)` 교체

## 검증
- `dotnet build` 성공 확인 (오류 0, 기존 경고만 존재)
- 정적 분석으로 코드 경로 추적하여 수정 방향 확정
- 실행 테스트는 유저 확인 필요 (프리팹 편집/저장 후 씬 인스턴스 갱신 확인)
