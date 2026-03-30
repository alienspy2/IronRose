# Phase 46d-w1: 핵심 명령 (go CRUD, transform, component, editor undo/redo)

## 목표
- CLI 브릿지에 씬 구성에 필수적인 핵심 명령을 추가한다.
- `go.create`, `go.create_primitive`, `go.destroy`, `go.rename`, `go.duplicate` (GO CRUD)
- `transform.get`, `transform.set_position`, `transform.set_rotation`, `transform.set_scale`, `transform.set_parent` (Transform 기본)
- `component.add`, `component.remove`, `component.list` (Component 관리)
- `editor.undo`, `editor.redo` (Undo/Redo)

## 선행 조건
- Phase 46a~46c 완료 (CliPipeServer, CliCommandDispatcher, CliLogBuffer, EngineCore 통합 완료)
- `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` 파일이 존재하며, `RegisterHandlers()` 메서드에 기존 핸들러(ping, scene.*, go.get, go.find, go.set_active, go.set_field, select, play.*, log.recent)가 구현되어 있음

## 수정할 파일

### `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`

- **변경 내용**: `RegisterHandlers()` 메서드 끝에 Wave 1 핸들러를 추가한다. 기존 핸들러는 수정하지 않는다.
- **이유**: 설계 문서의 Wave 1 명령 세트를 구현하기 위함.
- **인코딩**: UTF-8 with BOM

#### 필요한 using 추가

파일 상단 using 블록에 아래를 추가한다 (없는 것만):
```csharp
using IronRose.Engine.Editor;
using IronRose.Scripting;
```
참고: `IronRose.Engine.Editor`는 이미 있을 수 있다. `IronRose.Scripting`은 `component.add`의 LiveCode 타입 검색에 필요하다.

#### 핸들러 구현 상세

`RegisterHandlers()` 메서드 끝 (기존 `log.recent` 핸들러 뒤)에 아래 핸들러들을 순서대로 추가한다.

---

##### 1. `go.create` -- 빈 GameObject 생성 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// go.create -- 빈 GameObject 생성 (메인 스레드)
// ----------------------------------------------------------------
_handlers["go.create"] = args =>
{
    var name = args.Length > 0 ? args[0] : "GameObject";
    return ExecuteOnMainThread(() =>
    {
        var go = new GameObject(name);
        return JsonOk(new { id = go.GetInstanceID(), name = go.name });
    });
};
```

- **인자**: `<name>` (선택, 기본값 "GameObject")
- **응답**: `{ "id": int, "name": "..." }`
- **API**: `new GameObject(name)` -- `RoseEngine.GameObject` 생성자. 내부에서 `SceneManager.RegisterGameObject(this)`를 호출하므로 별도 등록 불필요.

---

##### 2. `go.create_primitive` -- Primitive GO 생성 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// go.create_primitive -- Primitive 생성 (메인 스레드)
// ----------------------------------------------------------------
_handlers["go.create_primitive"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: go.create_primitive <type> (Cube|Sphere|Capsule|Cylinder|Plane|Quad)");

    if (!Enum.TryParse<PrimitiveType>(args[0], ignoreCase: true, out var primitiveType))
        return JsonError($"Unknown primitive type: {args[0]}. Valid: Cube, Sphere, Capsule, Cylinder, Plane, Quad");

    return ExecuteOnMainThread(() =>
    {
        var go = GameObject.CreatePrimitive(primitiveType);
        return JsonOk(new { id = go.GetInstanceID(), name = go.name });
    });
};
```

- **인자**: `<type>` (Cube/Sphere/Capsule/Cylinder/Plane/Quad, case-insensitive)
- **응답**: `{ "id": int, "name": "..." }`
- **API**: `GameObject.CreatePrimitive(PrimitiveType)` -- MeshFilter, MeshRenderer, Collider를 자동으로 추가한다.
- **참고**: `PrimitiveType` enum은 `RoseEngine` 네임스페이스 (`RoseEngine.PrimitiveType`)

---

##### 3. `go.destroy` -- GameObject 삭제 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// go.destroy -- GameObject 삭제 (메인 스레드)
// ----------------------------------------------------------------
_handlers["go.destroy"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: go.destroy <id>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        Object.DestroyImmediate(go);
        return JsonOk(new { ok = true });
    });
};
```

- **인자**: `<id>` (InstanceID)
- **응답**: `{ "ok": true }`
- **API**: `RoseEngine.Object.DestroyImmediate(go)` -- 즉시 삭제. 자식도 재귀적으로 삭제된다. `SceneManager.DestroyImmediate()` 내부에서 컴포넌트 정리, AllGameObjects 제거까지 처리.

---

##### 4. `go.rename` -- GameObject 이름 변경 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// go.rename -- GameObject 이름 변경 (메인 스레드)
// ----------------------------------------------------------------
_handlers["go.rename"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: go.rename <id> <name>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    var newName = args[1];
    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        go.name = newName;
        SceneManager.GetActiveScene().isDirty = true;
        return JsonOk(new { ok = true });
    });
};
```

- **인자**: `<id> <name>` (이름에 공백이 있으면 쌍따옴표로 감싼다)
- **응답**: `{ "ok": true }`
- **API**: `go.name = newName` -- `Object.name` setter. 씬을 dirty로 표시한다.

---

##### 5. `go.duplicate` -- GameObject 복제 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// go.duplicate -- GameObject 복제 (메인 스레드)
// ----------------------------------------------------------------
_handlers["go.duplicate"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: go.duplicate <id>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        var clone = Object.Instantiate(go);
        clone.name = go.name + "_copy";
        if (go.transform.parent != null)
            clone.transform.SetParent(go.transform.parent);
        SceneManager.GetActiveScene().isDirty = true;
        return JsonOk(new { id = clone.GetInstanceID(), name = clone.name });
    });
};
```

- **인자**: `<id>` (InstanceID)
- **응답**: `{ "id": int, "name": "..." }`
- **API**: `Object.Instantiate<T>(original)` -- `RoseEngine.Object`의 deep clone. Transform, 모든 컴포넌트, 자식까지 복제. 복제된 GO는 `SceneManager.RegisterGameObject()`를 통해 자동 등록된다.

---

##### 6. `transform.get` -- Transform 정보 조회 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// transform.get -- position/rotation/scale 조회 (메인 스레드)
// ----------------------------------------------------------------
_handlers["transform.get"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: transform.get <id>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        var t = go.transform;
        return JsonOk(new
        {
            position = FormatVector3(t.position),
            localPosition = FormatVector3(t.localPosition),
            rotation = FormatVector3(t.eulerAngles),
            localRotation = FormatVector3(t.localEulerAngles),
            localScale = FormatVector3(t.localScale),
            lossyScale = FormatVector3(t.lossyScale)
        });
    });
};
```

- **인자**: `<id>` (InstanceID)
- **응답**: `{ "position": "x,y,z", "localPosition": "x,y,z", "rotation": "x,y,z", "localRotation": "x,y,z", "localScale": "x,y,z", "lossyScale": "x,y,z" }`
- **API**: `transform.position`, `transform.localPosition`, `transform.eulerAngles`, `transform.localEulerAngles`, `transform.localScale`, `transform.lossyScale`

**헬퍼 메서드 추가** (헬퍼 메서드 영역에 추가):

```csharp
/// <summary>Vector3를 "x, y, z" 문자열로 포맷한다.</summary>
private static string FormatVector3(Vector3 v)
{
    return $"{v.x.ToString(CultureInfo.InvariantCulture)}, {v.y.ToString(CultureInfo.InvariantCulture)}, {v.z.ToString(CultureInfo.InvariantCulture)}";
}
```

---

##### 7. `transform.set_position` -- 월드 위치 설정 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// transform.set_position -- 월드 위치 설정 (메인 스레드)
// ----------------------------------------------------------------
_handlers["transform.set_position"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: transform.set_position <id> <x,y,z>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        try
        {
            go.transform.position = ParseVector3(args[1]);
            SceneManager.GetActiveScene().isDirty = true;
            return JsonOk(new { ok = true });
        }
        catch (Exception ex)
        {
            return JsonError($"Failed to parse position: {ex.Message}");
        }
    });
};
```

- **인자**: `<id> <x,y,z>` (예: `42 1.5,2.0,3.5`)
- **응답**: `{ "ok": true }`
- **API**: `transform.position` setter -- 월드 좌표 설정. 부모가 있으면 자동으로 localPosition을 역산한다.
- **파싱**: 기존 `ParseVector3(string)` 헬퍼를 사용. 포맷: `x,y,z` 또는 `(x,y,z)`.

---

##### 8. `transform.set_rotation` -- 오일러 회전 설정 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// transform.set_rotation -- 오일러 회전 설정 (메인 스레드)
// ----------------------------------------------------------------
_handlers["transform.set_rotation"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: transform.set_rotation <id> <x,y,z>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        try
        {
            go.transform.eulerAngles = ParseVector3(args[1]);
            SceneManager.GetActiveScene().isDirty = true;
            return JsonOk(new { ok = true });
        }
        catch (Exception ex)
        {
            return JsonError($"Failed to parse rotation: {ex.Message}");
        }
    });
};
```

- **인자**: `<id> <x,y,z>` (오일러 각도)
- **응답**: `{ "ok": true }`
- **API**: `transform.eulerAngles` setter -- `Quaternion.Euler(value)`을 내부적으로 호출하여 `rotation`에 설정한다.

---

##### 9. `transform.set_scale` -- 로컬 스케일 설정 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// transform.set_scale -- 로컬 스케일 설정 (메인 스레드)
// ----------------------------------------------------------------
_handlers["transform.set_scale"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: transform.set_scale <id> <x,y,z>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        try
        {
            go.transform.localScale = ParseVector3(args[1]);
            SceneManager.GetActiveScene().isDirty = true;
            return JsonOk(new { ok = true });
        }
        catch (Exception ex)
        {
            return JsonError($"Failed to parse scale: {ex.Message}");
        }
    });
};
```

- **인자**: `<id> <x,y,z>`
- **응답**: `{ "ok": true }`
- **API**: `transform.localScale` setter

---

##### 10. `transform.set_parent` -- 부모 변경 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// transform.set_parent -- 부모 변경 (메인 스레드)
// ----------------------------------------------------------------
_handlers["transform.set_parent"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: transform.set_parent <id> <parentId|none>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        if (args[1].Equals("none", StringComparison.OrdinalIgnoreCase) || args[1] == "0")
        {
            go.transform.SetParent(null);
        }
        else
        {
            if (!int.TryParse(args[1], out var parentId))
                return JsonError($"Invalid parent ID: {args[1]}");

            var parentGo = FindGameObjectById(parentId);
            if (parentGo == null)
                return JsonError($"Parent GameObject not found: {parentId}");

            go.transform.SetParent(parentGo.transform);
        }

        SceneManager.GetActiveScene().isDirty = true;
        return JsonOk(new { ok = true });
    });
};
```

- **인자**: `<id> <parentId|none>` ("none" 또는 "0"이면 루트로 이동)
- **응답**: `{ "ok": true }`
- **API**: `transform.SetParent(newParent, worldPositionStays: true)` -- 기본적으로 월드 위치를 유지하며 부모를 변경한다.

---

##### 11. `component.add` -- 컴포넌트 추가 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// component.add -- 컴포넌트 추가 (메인 스레드)
// ----------------------------------------------------------------
_handlers["component.add"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: component.add <goId> <typeName>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    var typeName = args[1];
    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        var type = ResolveComponentType(typeName);
        if (type == null)
            return JsonError($"Component type not found: {typeName}");

        if (!typeof(Component).IsAssignableFrom(type))
            return JsonError($"{typeName} does not derive from Component");

        var comp = go.AddComponent(type);
        return JsonOk(new { ok = true, typeName = comp.GetType().Name });
    });
};
```

- **인자**: `<goId> <typeName>` (타입명은 네임스페이스 없이 짧은 이름)
- **응답**: `{ "ok": true, "typeName": "..." }`
- **API**: `go.AddComponent(Type)` -- `RoseEngine.GameObject`의 Type 기반 컴포넌트 추가. `Component`의 서브클래스여야 한다. `MonoBehaviour`이면 자동으로 `SceneManager.RegisterBehaviour()`가 호출된다.

**타입 해석 헬퍼 메서드 추가** (헬퍼 메서드 영역에 추가):

```csharp
/// <summary>
/// typeName 문자열로부터 Component Type을 찾는다.
/// 검색 순서: 1) RoseEngine 네임스페이스 (엔진 내장), 2) FrozenCode 어셈블리, 3) LiveCode 어셈블리.
/// </summary>
private static Type? ResolveComponentType(string typeName)
{
    // 1. 엔진 내장 타입 (RoseEngine 네임스페이스)
    var engineAssembly = typeof(Component).Assembly;
    foreach (var type in engineAssembly.GetTypes())
    {
        if (type.Name == typeName && typeof(Component).IsAssignableFrom(type))
            return type;
    }

    // 2. FrozenCode 어셈블리
    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
    {
        if (asm.GetName().Name == "FrozenCode")
        {
            foreach (var type in asm.GetTypes())
            {
                if (type.Name == typeName && typeof(Component).IsAssignableFrom(type))
                    return type;
            }
        }
    }

    // 3. LiveCode 어셈블리 (collectible ALC)
    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
    {
        // LiveCode는 AssemblyLoadContext로 로드되며, 이름이 "LiveCode"이거나
        // 동적으로 생성된 이름을 가질 수 있다.
        var asmName = asm.GetName().Name;
        if (asmName != null && asmName.Contains("LiveCode", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var type in asm.GetTypes())
                {
                    if (type.Name == typeName && typeof(Component).IsAssignableFrom(type))
                        return type;
                }
            }
            catch (ReflectionTypeLoadException)
            {
                // collectible ALC 해제 후 접근 시 발생 가능 -- 무시
            }
        }
    }

    return null;
}
```

- **해석 순서**:
  1. `typeof(Component).Assembly` -- RoseEngine 어셈블리에서 엔진 내장 타입 (MeshRenderer, Light, Camera, Rigidbody 등)
  2. `AppDomain.CurrentDomain.GetAssemblies()`에서 `Name == "FrozenCode"`인 어셈블리
  3. 이름에 "LiveCode"를 포함하는 어셈블리 (ScriptDomain이 collectible ALC로 로드)
- **주의**: LiveCode 어셈블리 접근 시 `ReflectionTypeLoadException`이 발생할 수 있으므로 try-catch로 감싼다.

---

##### 12. `component.remove` -- 컴포넌트 제거 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// component.remove -- 컴포넌트 제거 (메인 스레드)
// ----------------------------------------------------------------
_handlers["component.remove"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: component.remove <goId> <typeName>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    var typeName = args[1];
    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        var comp = go.InternalComponents
            .FirstOrDefault(c => c.GetType().Name == typeName && c.GetType() != typeof(Transform));
        if (comp == null)
            return JsonError($"Component not found: {typeName}");

        if (comp is Transform)
            return JsonError("Cannot remove Transform component");

        Object.DestroyImmediate(comp);
        SceneManager.GetActiveScene().isDirty = true;
        return JsonOk(new { ok = true });
    });
};
```

- **인자**: `<goId> <typeName>`
- **응답**: `{ "ok": true }`
- **API**: `Object.DestroyImmediate(comp)` -- 컴포넌트를 즉시 삭제. `SceneManager.ExecuteDestroy()`에서 `DestroyComponent()` -> `go.RemoveComponent(comp)` 호출까지 처리.
- **주의**: Transform은 제거할 수 없다 (모든 GO에 필수).

---

##### 13. `component.list` -- 컴포넌트 목록 조회 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// component.list -- GO의 모든 컴포넌트 목록 (메인 스레드)
// ----------------------------------------------------------------
_handlers["component.list"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: component.list <goId>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        var components = new List<object>();
        foreach (var comp in go.InternalComponents)
        {
            if (comp._isDestroyed) continue;
            var fields = new List<object>();
            foreach (var field in comp.GetType().GetFields(
                BindingFlags.Public | BindingFlags.Instance))
            {
                try
                {
                    var val = field.GetValue(comp);
                    fields.Add(new
                    {
                        name = field.Name,
                        typeName = field.FieldType.Name,
                        value = val?.ToString() ?? "null"
                    });
                }
                catch { /* skip unreadable fields */ }
            }
            foreach (var prop in comp.GetType().GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                if (prop.Name == "gameObject" || prop.Name == "transform") continue;
                try
                {
                    var val = prop.GetValue(comp);
                    fields.Add(new
                    {
                        name = prop.Name,
                        typeName = prop.PropertyType.Name,
                        value = val?.ToString() ?? "null"
                    });
                }
                catch { /* skip unreadable properties */ }
            }
            components.Add(new
            {
                typeName = comp.GetType().Name,
                fields
            });
        }
        return JsonOk(new { components });
    });
};
```

- **인자**: `<goId>` (InstanceID)
- **응답**: `{ "components": [{ "typeName": "...", "fields": [{ "name": "...", "typeName": "...", "value": "..." }] }] }`
- **API**: `go.InternalComponents` (IReadOnlyList<Component>), 리플렉션으로 public 필드/프로퍼티 조회.
- **주의**: `gameObject`, `transform` 프로퍼티는 제외한다 (순환 참조 방지).

---

##### 14. `editor.undo` -- 실행취소 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// editor.undo -- 실행취소 (메인 스레드)
// ----------------------------------------------------------------
_handlers["editor.undo"] = args => ExecuteOnMainThread(() =>
{
    var desc = UndoSystem.PerformUndo();
    if (desc == null)
        return JsonOk(new { ok = true, description = "Nothing to undo" });
    return JsonOk(new { ok = true, description = desc });
});
```

- **인자**: 없음
- **응답**: `{ "ok": true, "description": "..." }`
- **API**: `UndoSystem.PerformUndo()` -- undo 스택의 마지막 액션을 실행취소하고, 해당 액션의 Description을 반환. 스택이 비어 있으면 null.

---

##### 15. `editor.redo` -- 다시실행 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// editor.redo -- 다시실행 (메인 스레드)
// ----------------------------------------------------------------
_handlers["editor.redo"] = args => ExecuteOnMainThread(() =>
{
    var desc = UndoSystem.PerformRedo();
    if (desc == null)
        return JsonOk(new { ok = true, description = "Nothing to redo" });
    return JsonOk(new { ok = true, description = desc });
});
```

- **인자**: 없음
- **응답**: `{ "ok": true, "description": "..." }`
- **API**: `UndoSystem.PerformRedo()` -- redo 스택의 마지막 액션을 재실행하고 Description 반환. 비어 있으면 null.

---

#### 전체 변경 요약

1. `RegisterHandlers()` 메서드 끝에 15개 핸들러를 추가한다.
2. 헬퍼 메서드 영역에 `FormatVector3()`, `ResolveComponentType()` 2개 메서드를 추가한다.
3. 파일 상단에 `using IronRose.Scripting;`이 없으면 추가한다 (없을 경우에만).

## NuGet 패키지
- 없음 (모두 엔진 내장 API 사용)

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] `go.create MyObject` -> `{ "ok": true, "data": { "id": ..., "name": "MyObject" } }`
- [ ] `go.create_primitive Cube` -> 큐브 GO 생성
- [ ] `go.destroy <id>` -> GO 삭제
- [ ] `go.rename <id> NewName` -> 이름 변경
- [ ] `go.duplicate <id>` -> GO 복제, 새 ID 반환
- [ ] `transform.get <id>` -> position/rotation/scale 조회
- [ ] `transform.set_position <id> 1,2,3` -> 위치 변경
- [ ] `transform.set_rotation <id> 0,90,0` -> 회전 변경
- [ ] `transform.set_scale <id> 2,2,2` -> 스케일 변경
- [ ] `transform.set_parent <id> <parentId>` -> 부모 변경
- [ ] `transform.set_parent <id> none` -> 루트로 이동
- [ ] `component.add <id> MeshRenderer` -> 컴포넌트 추가
- [ ] `component.remove <id> MeshRenderer` -> 컴포넌트 제거
- [ ] `component.list <id>` -> 컴포넌트 목록 반환
- [ ] `editor.undo` -> 실행취소
- [ ] `editor.redo` -> 다시실행

## 참고
- 모든 핸들러는 `ExecuteOnMainThread()`를 사용한다 (SceneManager, GameObject 등이 메인 스레드 전용).
- `component.add`의 타입 해석 순서: 엔진 내장 -> FrozenCode -> LiveCode. 프로젝트가 로드되지 않은 상태에서는 FrozenCode/LiveCode 어셈블리가 없으므로 엔진 내장 타입만 사용 가능하다.
- `ParseVector3()`는 기존 코드에 이미 구현되어 있다 (`cleaned.Split(',')` 기반).
- `FindGameObjectById()`, `JsonOk()`, `JsonError()`, `ParseVector3()` 등 기존 헬퍼 메서드를 재사용한다.
- `Object.DestroyImmediate()`는 `RoseEngine.Object`의 static 메서드이므로, 기존 using이 있으면 `Object.DestroyImmediate()`로 호출 가능. 만약 `System.Object`와 충돌이 있으면 `RoseEngine.Object.DestroyImmediate()`로 full-qualify 한다.
