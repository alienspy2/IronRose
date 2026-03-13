# Nested Prefab 캐시 미갱신으로 씬 인스턴스에 변경사항 미반영

> **NOTE**: 이 문서에서 기술된 `UnloadAllPrefabs()` 일괄 무효화 방식은
> `fix-prefab-dependency-graph-invalidation.md`에서 dependency graph 기반
> cascade invalidation으로 교체되었음. `UnloadAllPrefabs()` 메서드는 제거됨.

## 유저 보고 내용
- Sphere prefab의 child node 위치를 수정 후 저장
- Cube prefab (Sphere를 nested로 포함) 편집 시에는 변경이 반영됨
- 씬에 배치된 Cube prefab 인스턴스에서는 Sphere child의 좌표가 반영되지 않음

## 원인
`AssetDatabase._loadedAssets` 캐시 무효화 범위가 부족했음.

### 상세 흐름

1. 씬 로드 시 Cube prefab이 `db.LoadByGuid()` 호출로 로드되어 `_loadedAssets[cubePath]`에 캐시됨
2. 이 Cube 템플릿은 내부적으로 nested Sphere를 `db.LoadByGuid(sphereGuid)`로 로드하여 Instantiate한 결과를 포함
3. Sphere prefab 편집/저장 시 `PrefabEditMode.Save()`에서 `db.Unload(spherePath)`를 호출하지만, 이는 Sphere 캐시만 제거
4. **Cube의 캐시에는 이전 Sphere 데이터가 포함된 채로 남아있음**
5. 씬 복원 시 `db.LoadByGuid(cubeGuid)` -> 캐시된 이전 Cube 템플릿 반환 -> 이전 Sphere 좌표

### 프리팹 편집 모드에서는 반영되는 이유

`PrefabEditMode.Enter()`는 `new PrefabImporter(db).LoadPrefab(prefabPath)`를 직접 호출하여 항상 디스크에서 fresh 로드함. `AssetDatabase._loadedAssets` 캐시를 거치지 않으므로 최신 데이터가 반영됨.

## 수정 내용

### 1. `AssetDatabase.UnloadAllPrefabs()` 메서드 추가
- `_loadedAssets`에서 `.prefab` 확장자를 가진 모든 에셋의 캐시를 일괄 제거
- Nested prefab 의존성 추적 대신 단순한 일괄 무효화 채택 (프리팹 캐시 비용 미미)

### 2. `PrefabEditMode.Save()`에서 `Unload(prefabPath)` -> `UnloadAllPrefabs()` 변경
- 저장된 프리팹뿐 아니라, 이를 참조하는 부모 프리팹의 캐시도 함께 무효화

### 3. `AssetDatabase.Reimport()`에 PrefabImporter 케이스 추가
- FileSystemWatcher가 `.prefab` 파일 변경을 감지할 때도 모든 프리팹 캐시를 무효화
- 외부 에디터로 프리팹 수정 후 씬 재로드 시에도 최신 데이터 반영

## 변경된 파일
- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` -- `UnloadAllPrefabs()` 메서드 추가, `Reimport()`에 PrefabImporter 케이스 추가
- `src/IronRose.Engine/Editor/PrefabEditMode.cs` -- `Save()`에서 `db.Unload(prefabPath)` -> `db.UnloadAllPrefabs()` 변경

## 검증
- 정적 분석으로 코드 경로 추적하여 원인 확정
- `dotnet build` 성공 확인 (오류 0, 기존 경고만 존재)
- 실행 테스트는 유저 확인 필요 (GUI 조작으로 프리팹 편집/저장/씬 복원 필요)
