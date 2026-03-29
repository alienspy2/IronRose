# rose-cli 프리팹 필드 조회/수정 명령 추가

## 배경
- 현재 rose-cli에는 `prefab.instantiate`(씬에 인스턴스 생성)와 `prefab.save`(GO를 프리팹으로 저장) 명령만 존재한다.
- 디스크에 있는 프리팹 에셋의 컴포넌트 필드를 조회하거나 수정하려면 에디터 GUI를 사용해야 한다.
- CLI/스킬에서 프리팹 에셋을 직접 조회/수정할 수 있으면 자동화 및 배치 작업이 가능해진다.

## 목표
- `prefab.info` 명령으로 프리팹 에셋의 GO 계층, 컴포넌트, 필드 값을 조회한다.
- `prefab.set_field` 명령으로 프리팹 에셋의 컴포넌트 필드를 수정하고 디스크에 저장한다.
- 기존 `go.get` / `go.set_field`와 일관된 인터페이스를 유지한다.

## 현재 상태

### 관련 CLI 명령 (CliCommandDispatcher.cs)
- `go.get <id|name>` -- `GameObjectSnapshot.From(go)`으로 GO의 컴포넌트/필드를 JSON 반환
- `go.set_field <id> <component> <field> <value>` -- 리플렉션으로 필드 수정, `ParseFieldValue()`로 값 파싱
- `prefab.instantiate <guid> [pos]` -- `PrefabUtility.InstantiatePrefab()` 호출
- `prefab.save <goId> <path>` -- `PrefabUtility.SaveAsPrefab()` 호출

### 프리팹 로드/저장 (SceneSerializer)
- `LoadPrefabGameObjects(filePath)` -- .prefab TOML 파일에서 GO 목록 로드 (메모리에 `_isEditorInternal=true`로 생성)
- `SavePrefab(root, path)` -- GO 계층을 .prefab TOML로 저장

### 에셋 해석 (AssetDatabase)
- `GetPathFromGuid(guid)` -- GUID에서 파일 경로
- `GetGuidFromPath(path)` -- 파일 경로에서 GUID
- `LoadByGuid<T>(guid)` -- GUID로 에셋 로드 (프리팹의 경우 GO 반환)

### 값 파싱 (ParseFieldValue)
- float, int, bool, string, Vector3, Color, Enum 지원
- 에셋 참조: Material, GameObject, Mesh, Texture2D, Sprite, AnimationClip, Font (GUID 문자열로 로드)

### GameObjectSnapshot / ComponentSnapshot
- `GameObjectSnapshot.From(go)` -- GO의 id, name, active, parentId, components 스냅샷
- `ComponentSnapshot.From(comp)` -- 리플렉션으로 public/[SerializeField] 필드 추출, 값을 ToString()

## 설계

### 개요
엔진이 실행 중인 상태에서 Named Pipe로 명령을 받으므로, 프리팹 에셋을 `LoadByGuid<GameObject>`로 메모리에 로드하고, 기존 `GameObjectSnapshot`과 리플렉션 인프라를 재활용하여 조회/수정한 뒤, `SceneSerializer.SavePrefab()`으로 디스크에 다시 저장한다.

### 상세 설계

#### 1. `prefab.info <guid|path>` 명령

**용도**: 프리팹 에셋의 GO 계층, 컴포넌트, 필드 값을 JSON으로 반환한다.

**처리 흐름**:
1. 인자가 GUID인지 경로인지 판별 (GUID 형식이면 GUID, 아니면 경로로 처리)
2. GUID/경로로 `AssetDatabase`에서 프리팹 경로를 확정
3. `AssetDatabase.LoadByGuid<GameObject>(guid)`로 템플릿 GO를 로드
4. 루트 GO와 모든 자식을 순회하며 `GameObjectSnapshot.From()`으로 스냅샷 수집
5. JSON 응답 반환

**인자 판별 로직**:
- `Guid.TryParse(args[0])` 성공 시 GUID로 처리
- 실패 시 `ResolveProjectPath(args[0])`로 경로 확정 후 `GetGuidFromPath()`

**응답 형식**:
```json
{
  "status": "ok",
  "data": {
    "guid": "...",
    "path": "Assets/Prefabs/Bomb.prefab",
    "gameObjects": [
      {
        "name": "Bomb",
        "isRoot": true,
        "components": [
          {
            "typeName": "Transform",
            "fields": [
              { "name": "localPosition", "typeName": "Vector3", "value": "(0, 0, 0)" }
            ]
          },
          {
            "typeName": "PileScript",
            "fields": [
              { "name": "explosionVfxPrefab", "typeName": "GameObject", "value": "null" },
              { "name": "damage", "typeName": "Single", "value": "10" }
            ]
          }
        ]
      },
      {
        "name": "ChildSprite",
        "isRoot": false,
        "components": [ ... ]
      }
    ]
  }
}
```

**구현 위치**: `CliCommandDispatcher.cs`의 생성자 내 `_handlers` 등록부 (기존 `prefab.save` 핸들러 뒤)

**구현 코드 구조**:
```
_handlers["prefab.info"] = args => {
    if (args.Length < 1) return JsonError("Usage: prefab.info <guid|path>");
    return ExecuteOnMainThread(() => {
        // 1. GUID 확정
        var (guid, path) = ResolvePrefabAsset(args[0]);
        if (guid == null) return JsonError(...);

        // 2. 프리팹 로드
        var db = Resources.GetAssetDatabase();
        var templateGo = db.LoadByGuid<GameObject>(guid);
        if (templateGo == null) return JsonError(...);

        // 3. 계층 순회 → 스냅샷 수집
        var goList = new List<GameObject>();
        PrefabUtility.CollectHierarchy(templateGo, goList);

        var goSnapshots = goList.Select(go => {
            var snapshot = GameObjectSnapshot.From(go);
            return new {
                name = snapshot.Name,
                isRoot = (go == templateGo),
                components = snapshot.Components.Select(c => new {
                    typeName = c.TypeName,
                    fields = c.Fields.Select(f => new {
                        name = f.Name,
                        typeName = f.TypeName,
                        value = f.Value
                    })
                })
            };
        });

        return JsonOk(new { guid, path, gameObjects = goSnapshots });
    });
};
```

#### 2. `prefab.set_field` 명령

**용도**: 프리팹 에셋의 특정 GO의 컴포넌트 필드를 수정하고 디스크에 저장한다.

**명령 형식**:
- 루트 GO 대상 (GO 이름 생략): `prefab.set_field <guid|path> <componentType> <fieldName> <value>`
- 특정 GO 대상: `prefab.set_field <guid|path> <goName> <componentType> <fieldName> <value>`

**인자 구분 로직**:
- 인자가 4개이면: `[asset, component, field, value]` -- 루트 GO 대상
- 인자가 5개이면: `[asset, goName, component, field, value]` -- 특정 GO 대상

**에셋 참조 값 형식**:
- `asset:` 접두사로 에셋 참조 구분: `prefab.set_field <guid> PileScript explosionVfxPrefab asset:dcc25465-...`
- `ParseFieldValue()` 수정 필요: 에셋 참조 타입 필드에 `asset:` 접두사가 있으면 접두사를 제거하고 GUID로 로드

**처리 흐름**:
1. 인자 파싱 및 GUID 확정 (`ResolvePrefabAsset()`)
2. `AssetDatabase.LoadByGuid<GameObject>(guid)`로 템플릿 로드
3. 대상 GO 찾기:
   - 루트 모드: 로드된 루트 GO 사용
   - goName 모드: `CollectHierarchy()`로 순회하며 이름 매칭
4. 대상 GO의 컴포넌트에서 리플렉션으로 필드 찾기 (기존 `go.set_field`와 동일)
5. `ParseFieldValue()`로 값 파싱 후 필드에 설정
6. `SceneSerializer.SavePrefab(root, path)`로 디스크에 저장
7. `PrefabUtility.RefreshPrefabInstances(guid)`로 씬 내 인스턴스 갱신

**응답 형식**:
```json
{
  "status": "ok",
  "data": {
    "ok": true,
    "guid": "...",
    "goName": "Bomb",
    "component": "PileScript",
    "field": "explosionVfxPrefab",
    "newValue": "asset:dcc25465-..."
  }
}
```

**구현 코드 구조**:
```
_handlers["prefab.set_field"] = args => {
    // 인자 개수로 루트/특정 GO 모드 판별
    if (args.Length < 4)
        return JsonError("Usage: prefab.set_field <guid|path> [goName] <component> <field> <value>");

    bool hasGoName = args.Length >= 5;
    var assetArg = args[0];
    var goName = hasGoName ? args[1] : null;
    var componentType = hasGoName ? args[2] : args[1];
    var fieldName = hasGoName ? args[3] : args[2];
    var newValue = hasGoName ? args[4] : args[3];

    return ExecuteOnMainThread(() => {
        // 1. GUID/경로 확정
        var (guid, path) = ResolvePrefabAsset(assetArg);
        if (guid == null) return JsonError(...);

        // 2. 프리팹 로드
        var db = Resources.GetAssetDatabase();
        var rootGo = db.LoadByGuid<GameObject>(guid);
        if (rootGo == null) return JsonError(...);

        // 3. 대상 GO 결정
        GameObject targetGo;
        if (goName == null) {
            targetGo = rootGo;
        } else {
            var allGos = new List<GameObject>();
            PrefabUtility.CollectHierarchy(rootGo, allGos);
            targetGo = allGos.FirstOrDefault(g => g.name == goName);
            if (targetGo == null) return JsonError($"GO not found in prefab: {goName}");
        }

        // 4. 컴포넌트/필드 찾기
        var comp = targetGo.InternalComponents
            .FirstOrDefault(c => c.GetType().Name == componentType);
        if (comp == null) return JsonError(...);

        var field = comp.GetType().GetField(fieldName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null) return JsonError(...);

        // 5. 값 파싱 (asset: 접두사 처리)
        var parsedValue = ParseFieldValueWithAssetPrefix(field.FieldType, newValue);
        if (parsedValue == null) return JsonError(...);

        field.SetValue(comp, parsedValue);

        // 6. 디스크에 저장
        SceneSerializer.SavePrefab(rootGo, path);

        // 7. 씬 내 인스턴스 갱신
        PrefabUtility.RefreshPrefabInstances(guid);

        return JsonOk(new {
            ok = true, guid,
            goName = targetGo.name,
            component = componentType,
            field = fieldName,
            newValue
        });
    });
};
```

#### 3. 공통 헬퍼: `ResolvePrefabAsset()`

**용도**: GUID 또는 경로 문자열을 받아 (guid, path) 튜플을 반환한다.

**구현 위치**: `CliCommandDispatcher.cs`의 private 헬퍼 메서드 영역 (기존 `ResolveProjectPath()` 근처)

```
private static (string? guid, string? path) ResolvePrefabAsset(string arg)
{
    var db = Resources.GetAssetDatabase();
    if (db == null) return (null, null);

    // GUID 형식 판별
    if (Guid.TryParse(arg, out _))
    {
        var path = db.GetPathFromGuid(arg);
        return (arg, path);
    }

    // 경로로 처리
    var resolved = ResolveProjectPath(arg);
    var guid = db.GetGuidFromPath(resolved);
    return (guid, resolved);
}
```

#### 4. `ParseFieldValue()` 확장: `asset:` 접두사 지원

**기존 동작**: 에셋 참조 타입(Material, GameObject 등)인 경우 raw 값을 그대로 GUID로 취급하여 `LoadByGuid<T>()` 호출

**변경**: `asset:` 접두사 처리를 추가. 이렇게 하면 에셋 참조와 일반 문자열을 명시적으로 구분할 수 있다.

**구현**:
- 기존 `ParseFieldValue()` 메서드의 에셋 참조 분기 앞에 `asset:` 접두사 체크 추가
- `raw`가 `asset:`으로 시작하면 접두사를 제거하고 GUID 부분만 추출
- 에셋 참조 타입이 아닌 필드에 `asset:` 접두사가 사용되면 에러

```
// ParseFieldValue() 내부, 에셋 참조 분기
string assetGuid = raw;
if (raw.StartsWith("asset:"))
    assetGuid = raw.Substring(6);

// 에셋 레퍼런스 타입: GUID 문자열로 에셋 로드
var db = Resources.GetAssetDatabase();
if (db != null)
{
    if (type == typeof(Material)) return db.LoadByGuid<Material>(assetGuid);
    if (type == typeof(GameObject)) return db.LoadByGuid<GameObject>(assetGuid);
    // ... 나머지 동일
}
```

이 변경은 기존 `go.set_field`에도 동일하게 적용되어, `go.set_field`에서도 `asset:` 접두사를 사용할 수 있게 된다 (하위 호환 유지: 접두사 없이 GUID만 넘겨도 기존대로 동작).

### 영향 범위

| 파일 | 변경 내용 |
|------|-----------|
| `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` | `prefab.info`, `prefab.set_field` 핸들러 추가, `ResolvePrefabAsset()` 헬퍼 추가, `ParseFieldValue()` 내 `asset:` 접두사 처리 추가, 파일 헤더 주석의 지원 명령 목록 업데이트 |

- 기존 기능에 미치는 영향: `ParseFieldValue()`의 `asset:` 접두사 처리는 하위 호환 (접두사 없으면 기존 동작 유지)
- 새로운 의존성 추가: 없음 (모두 기존 API 활용)

## 구현 단계

- [ ] 1. `ResolvePrefabAsset()` 헬퍼 메서드 추가
- [ ] 2. `ParseFieldValue()`에 `asset:` 접두사 처리 추가
- [ ] 3. `prefab.info` 핸들러 구현
- [ ] 4. `prefab.set_field` 핸들러 구현
- [ ] 5. 파일 헤더 주석의 명령 목록 업데이트
- [ ] 6. 빌드 확인 (`dotnet build`)
- [ ] 7. 에디터 실행 후 CLI로 테스트
    - `prefab.info <guid>` -- 프리팹 조회 확인
    - `prefab.set_field <guid> <comp> <field> <value>` -- 필드 수정 후 .prefab 파일 확인
    - `prefab.set_field <guid> <comp> <field> asset:<guid>` -- 에셋 참조 필드 수정 확인

## 대안 검토

### 방식 A: TOML 직접 파싱/수정
- SceneSerializer의 TOML 구조를 직접 파싱하여 값을 수정하고 다시 직렬화
- 장점: 에셋을 메모리에 로드하지 않아도 됨
- 단점: 타입 검증 불가, 에셋 참조 해석 불가, TOML 구조 변경에 취약, SceneSerializer의 직렬화 로직 중복
- **선택하지 않은 이유**: rose-cli는 실행 중인 에디터에 명령을 보내므로 엔진이 항상 실행 중이고, 엔진의 로드/저장 인프라를 활용하는 것이 안전하고 일관적

## 미결 사항
- 없음
