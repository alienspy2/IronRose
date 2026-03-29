# 핫 리로드 후 프리팹 캐시의 stale ALC 타입 문제 수정

## 유저 보고 내용
- 핫 리로드 후 play mode에서 `PrefabUtility.InstantiatePrefab()`로 생성된 프리팹의 스크립트 컴포넌트가 이전 ALC 타입을 사용함
- `AddComponent<T>()`로 직접 추가한 컴포넌트(예: CannonballScript)는 최신 ALC 타입을 사용하지만, 프리팹 인스턴스의 컴포넌트(PileScript, BlockScript 등)는 과거 ALC 타입을 사용
- 이전 ALC가 GC되지 않음 (stale 타입을 참조하는 컴포넌트가 살아있으므로)

## 원인
`AssetDatabase._loadedAssets` 캐시에 저장된 프리팹 템플릿 GameObject가 이전 ALC의 컴포넌트 타입을 보유하고 있었음.

핫 리로드 흐름:
1. `ExecuteReload()` -> `BuildScripts()` -> 새 ALC 생성, 이전 ALC unload
2. `MigrateEditorComponents()` -> 씬에 배치된 컴포넌트를 새 ALC 타입으로 교체
3. `SceneSerializer.InvalidateComponentTypeCache()` -> 타입 캐시 무효화

그러나 `AssetDatabase._loadedAssets`에 캐시된 프리팹 템플릿은 건드리지 않았음.
Play mode에서 `PrefabUtility.InstantiatePrefab()` 호출 시:
1. `db.LoadByGuid<GameObject>()` -> 캐시된 프리팹 템플릿 반환 (이전 ALC 타입)
2. `Object.CloneGameObject()` -> `clone.AddComponent(comp.GetType())` -> 이전 ALC의 `comp.GetType()` 사용
3. 결과: 새로 생성된 인스턴스의 스크립트 컴포넌트가 이전 ALC 타입

반면 `AddComponent<CannonballScript>()`는 호출자의 IL에서 타입 토큰이 resolve되므로 최신 ALC 타입을 사용.

## 수정 내용
1. `AssetDatabase.InvalidateScriptPrefabCache()` 메서드 추가: 캐시된 프리팹 중 Scripts 어셈블리 컴포넌트를 포함하는 항목을 제거
2. `ScriptReloadManager.ExecuteReload()`에서 `MigrateEditorComponents()` 직후 `InvalidateScriptPrefabCache()` 호출

이로써 핫 리로드 후 프리팹을 인스턴스화할 때 캐시 미스가 발생하여 재역직렬화되고, `GetComponentTypeCache()`가 새 ALC 타입을 반환하므로 최신 타입으로 컴포넌트가 생성됨.

## 변경된 파일
- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` -- `InvalidateScriptPrefabCache()` 및 `HasScriptComponent()` 헬퍼 추가
- `src/IronRose.Engine/ScriptReloadManager.cs` -- `ExecuteReload()`에서 `InvalidateScriptPrefabCache()` 호출 추가

## 검증
- 빌드 성공 확인
- 유저에게 실행 검증 요청 필요 (핫 리로드 후 play mode에서 프리팹 인스턴스 ALC 확인)
