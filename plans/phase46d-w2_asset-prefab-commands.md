# Phase 46d-w2: 에셋/프리팹 명령 (prefab.*, asset.*, scene.tree/new)

## 목표
- 실용적 워크플로우에 필요한 에셋/프리팹/씬 확장 명령을 CLI에 추가한다.
- `prefab.instantiate`, `prefab.save` (프리팹)
- `asset.list`, `asset.find`, `asset.guid`, `asset.path` (에셋)
- `scene.tree`, `scene.new` (씬 확장)

## 선행 조건
- Phase 46d-w1 완료 (핵심 명령 세트가 CliCommandDispatcher에 등록되어 있음)
- `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`에 Wave 1 핸들러가 존재

## 수정할 파일

### `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`

- **변경 내용**: `RegisterHandlers()` 메서드 끝에 Wave 2 핸들러를 추가한다. 기존 핸들러는 수정하지 않는다.
- **이유**: 설계 문서의 Wave 2 명령 세트를 구현하기 위함.
- **인코딩**: UTF-8 with BOM

#### 필요한 using 추가

파일 상단 using 블록에 아래를 추가한다 (없는 것만):
```csharp
using IronRose.AssetPipeline;
```
참고: AssetDatabase 접근에 필요하다. `RoseEngine`은 이미 있다.

#### 핸들러 구현 상세

`RegisterHandlers()` 메서드 끝 (Wave 1 핸들러 뒤)에 아래 핸들러들을 순서대로 추가한다.

---

##### 1. `prefab.instantiate` -- 프리팹 인스턴스 생성 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// prefab.instantiate -- 프리팹 인스턴스 생성 (메인 스레드)
// ----------------------------------------------------------------
_handlers["prefab.instantiate"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: prefab.instantiate <guid> [x,y,z]");

    var guid = args[0];
    return ExecuteOnMainThread(() =>
    {
        GameObject? go;
        if (args.Length >= 2)
        {
            try
            {
                var pos = ParseVector3(args[1]);
                go = PrefabUtility.InstantiatePrefab(guid, pos, Quaternion.identity);
            }
            catch (Exception ex)
            {
                return JsonError($"Failed to parse position: {ex.Message}");
            }
        }
        else
        {
            go = PrefabUtility.InstantiatePrefab(guid);
        }

        if (go == null)
            return JsonError($"Failed to instantiate prefab: guid={guid}");

        SceneManager.GetActiveScene().isDirty = true;
        return JsonOk(new { id = go.GetInstanceID(), name = go.name });
    });
};
```

- **인자**: `<guid> [x,y,z]` (GUID 필수, 위치 선택)
- **응답**: `{ "id": int, "name": "..." }`
- **API**:
  - `PrefabUtility.InstantiatePrefab(string guid)` -- 원점에 인스턴스화. PrefabInstance 컴포넌트를 자동 부착.
  - `PrefabUtility.InstantiatePrefab(string guid, Vector3 pos, Quaternion rot)` -- 위치/회전 지정.
  - 내부적으로 `AssetDatabase.LoadByGuid<GameObject>(guid)`로 템플릿 로드 후 `Object.Instantiate()`로 복제.
- **실패 시**: 프리팹 GUID를 찾을 수 없거나 로드 실패 시 null 반환.

---

##### 2. `prefab.save` -- GO를 프리팹으로 저장 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// prefab.save -- GO를 프리팹으로 저장 (메인 스레드)
// ----------------------------------------------------------------
_handlers["prefab.save"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: prefab.save <goId> <path>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    var path = args[1];
    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        try
        {
            var guid = PrefabUtility.SaveAsPrefab(go, path);
            return JsonOk(new { saved = true, path, guid });
        }
        catch (Exception ex)
        {
            return JsonError($"Failed to save prefab: {ex.Message}");
        }
    });
};
```

- **인자**: `<goId> <path>` (저장할 .prefab 파일 경로)
- **응답**: `{ "saved": true, "path": "...", "guid": "..." }`
- **API**: `PrefabUtility.SaveAsPrefab(GameObject go, string path)` -- GO 계층을 .prefab 파일로 저장. .rose 메타 자동 생성. AssetDatabase에 자동 등록. GUID를 반환.
- **주의**: 경로에 `.prefab` 확장자를 포함해야 한다. 디렉토리가 없으면 `SceneSerializer.SavePrefab()`이 내부적으로 생성한다.

---

##### 3. `asset.list` -- 에셋 폴더 탐색 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// asset.list -- 에셋 폴더 탐색 (메인 스레드)
// ----------------------------------------------------------------
_handlers["asset.list"] = args =>
{
    return ExecuteOnMainThread(() =>
    {
        var db = Resources.GetAssetDatabase();
        if (db == null)
            return JsonError("AssetDatabase not initialized");

        var allPaths = db.GetAllAssetPaths();
        string filterPath = args.Length > 0 ? args[0] : "";

        var assets = new List<object>();
        foreach (var assetPath in allPaths)
        {
            // filterPath가 지정되었으면 해당 경로로 시작하는 에셋만 반환
            if (!string.IsNullOrEmpty(filterPath) &&
                !assetPath.Contains(filterPath, StringComparison.OrdinalIgnoreCase))
                continue;

            var guid = db.GetGuidFromPath(assetPath);
            var ext = System.IO.Path.GetExtension(assetPath).TrimStart('.');
            assets.Add(new
            {
                path = assetPath,
                guid = guid ?? "",
                type = ext
            });
        }
        return JsonOk(new { assets });
    });
};
```

- **인자**: `[path]` (선택, 필터 경로 문자열. 부분 매칭.)
- **응답**: `{ "assets": [{ "path": "...", "guid": "...", "type": "..." }] }`
- **API**:
  - `Resources.GetAssetDatabase()` -- `AssetDatabase?` 반환. 프로젝트 미로드 시 null.
  - `db.GetAllAssetPaths()` -- `IReadOnlyCollection<string>` 반환. Sub-asset 경로 제외.
  - `db.GetGuidFromPath(path)` -- GUID 조회.
- **주의**: `GetAllAssetPaths()`는 `_guidToPath.Values`에서 sub-asset을 제외한 경로 목록을 반환한다.

---

##### 4. `asset.find` -- 이름으로 에셋 검색 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// asset.find -- 이름으로 에셋 검색 (메인 스레드)
// ----------------------------------------------------------------
_handlers["asset.find"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: asset.find <name>");

    var searchName = args[0];
    return ExecuteOnMainThread(() =>
    {
        var db = Resources.GetAssetDatabase();
        if (db == null)
            return JsonError("AssetDatabase not initialized");

        var allPaths = db.GetAllAssetPaths();
        var matches = new List<object>();
        foreach (var assetPath in allPaths)
        {
            var fileName = System.IO.Path.GetFileNameWithoutExtension(assetPath);
            if (fileName.Contains(searchName, StringComparison.OrdinalIgnoreCase))
            {
                var guid = db.GetGuidFromPath(assetPath);
                var ext = System.IO.Path.GetExtension(assetPath).TrimStart('.');
                matches.Add(new
                {
                    path = assetPath,
                    guid = guid ?? "",
                    type = ext
                });
            }
        }
        return JsonOk(new { assets = matches });
    });
};
```

- **인자**: `<name>` (부분 매칭, case-insensitive)
- **응답**: `{ "assets": [{ "path": "...", "guid": "...", "type": "..." }] }`
- **API**: `db.GetAllAssetPaths()` 후 `Path.GetFileNameWithoutExtension()`으로 이름 비교.

---

##### 5. `asset.guid` -- 경로에서 GUID 조회 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// asset.guid -- 경로에서 GUID 조회 (메인 스레드)
// ----------------------------------------------------------------
_handlers["asset.guid"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: asset.guid <path>");

    var path = args[0];
    return ExecuteOnMainThread(() =>
    {
        var db = Resources.GetAssetDatabase();
        if (db == null)
            return JsonError("AssetDatabase not initialized");

        var guid = db.GetGuidFromPath(path);
        if (string.IsNullOrEmpty(guid))
            return JsonError($"No GUID found for path: {path}");

        return JsonOk(new { guid });
    });
};
```

- **인자**: `<path>` (에셋 파일 경로)
- **응답**: `{ "guid": "..." }`
- **API**: `db.GetGuidFromPath(string path)` -- `_guidToPath` 딕셔너리에서 value가 path인 key(GUID)를 찾는다.

---

##### 6. `asset.path` -- GUID에서 경로 조회 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// asset.path -- GUID에서 경로 조회 (메인 스레드)
// ----------------------------------------------------------------
_handlers["asset.path"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: asset.path <guid>");

    var guid = args[0];
    return ExecuteOnMainThread(() =>
    {
        var db = Resources.GetAssetDatabase();
        if (db == null)
            return JsonError("AssetDatabase not initialized");

        var path = db.GetPathFromGuid(guid);
        if (string.IsNullOrEmpty(path))
            return JsonError($"No path found for GUID: {guid}");

        return JsonOk(new { path });
    });
};
```

- **인자**: `<guid>` (에셋 GUID)
- **응답**: `{ "path": "..." }`
- **API**: `db.GetPathFromGuid(string guid)` -- `_guidToPath.TryGetValue(guid, out path)`.

---

##### 7. `scene.tree` -- 계층 트리 조회 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// scene.tree -- 계층 트리 (부모-자식 구조, 메인 스레드)
// ----------------------------------------------------------------
_handlers["scene.tree"] = args => ExecuteOnMainThread(() =>
{
    var roots = new List<object>();
    foreach (var go in SceneManager.AllGameObjects)
    {
        if (go._isDestroyed) continue;
        if (go.transform.parent != null) continue; // 루트만
        roots.Add(BuildTreeNode(go));
    }
    return JsonOk(new { tree = roots });
});
```

- **인자**: 없음
- **응답**: `{ "tree": [{ "id": int, "name": "...", "children": [...] }] }`
- 루트 GO만 순회하고, 각 GO에서 재귀적으로 children을 수집.

**헬퍼 메서드 추가** (헬퍼 메서드 영역에 추가):

```csharp
/// <summary>GO를 트리 노드로 변환 (재귀). 메인 스레드에서 호출.</summary>
private static object BuildTreeNode(GameObject go)
{
    var children = new List<object>();
    for (int i = 0; i < go.transform.childCount; i++)
    {
        var child = go.transform.GetChild(i).gameObject;
        if (!child._isDestroyed)
            children.Add(BuildTreeNode(child));
    }
    return new
    {
        id = go.GetInstanceID(),
        name = go.name,
        active = go.activeSelf,
        children
    };
}
```

- **API**: `transform.childCount`, `transform.GetChild(i)` -- Transform의 자식 접근.

---

##### 8. `scene.new` -- 새 빈 씬 생성 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// scene.new -- 새 빈 씬 생성 (메인 스레드)
// ----------------------------------------------------------------
_handlers["scene.new"] = args => ExecuteOnMainThread(() =>
{
    SceneManager.Clear();
    var newScene = new Scene();
    newScene.name = "New Scene";
    SceneManager.SetActiveScene(newScene);
    return JsonOk(new { ok = true });
});
```

- **인자**: 없음
- **응답**: `{ "ok": true }`
- **API**:
  - `SceneManager.Clear()` -- 씬 내 모든 GO 삭제, MonoBehaviour 정리, 물리/렌더러/코루틴 등 전체 초기화.
  - `new Scene()` -- 빈 씬 생성 (RoseEngine.Scene 생성자).
  - `SceneManager.SetActiveScene(scene)` -- 활성 씬 교체.
- **주의**: `SceneManager.Clear()`는 `MeshRenderer.ClearAll()`, `Light.ClearAll()`, `Camera.ClearMain()` 등을 호출하여 렌더링 컴포넌트도 정리한다.

---

#### 전체 변경 요약

1. `RegisterHandlers()` 메서드 끝에 8개 핸들러를 추가한다.
2. 헬퍼 메서드 영역에 `BuildTreeNode()` 1개 메서드를 추가한다.
3. 파일 상단에 `using IronRose.AssetPipeline;`이 없으면 추가한다 (AssetDatabase 타입 접근용). 이미 있으면 생략.

## NuGet 패키지
- 없음

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] `prefab.instantiate <guid>` -> 프리팹 인스턴스 생성
- [ ] `prefab.instantiate <guid> 1,2,3` -> 지정 위치에 생성
- [ ] `prefab.save <id> Assets/MyPrefab.prefab` -> .prefab 파일 생성
- [ ] `asset.list` -> 전체 에셋 목록 반환
- [ ] `asset.list .prefab` -> .prefab 에셋만 필터링
- [ ] `asset.find Cube` -> 이름에 "Cube"를 포함하는 에셋 검색
- [ ] `asset.guid <path>` -> GUID 반환
- [ ] `asset.path <guid>` -> 경로 반환
- [ ] `scene.tree` -> 계층 트리 구조 (id, name, children 포함)
- [ ] `scene.new` -> 모든 GO 삭제, 빈 씬 생성

## 참고
- `Resources.GetAssetDatabase()`가 null을 반환하면 프로젝트가 로드되지 않은 상태이다. 이 경우 에셋 관련 명령은 에러를 반환한다.
- `PrefabUtility`의 메서드들은 모두 `RoseEngine` 네임스페이스의 static 클래스이다.
- `Scene` 클래스는 `RoseEngine.Scene`이며 `name`, `path`, `isDirty` 프로퍼티를 가진다.
- `asset.list`에서 `filterPath`는 경로의 부분 문자열 매칭이다 (Contains). 정확한 디렉토리 필터가 아니다.
