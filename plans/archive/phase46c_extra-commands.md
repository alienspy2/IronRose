# Phase 46c: 추가 명령 세트

## 목표
- `CliCommandDispatcher`에 나머지 명령 핸들러를 추가하여 실질적인 에디터 조작을 가능하게 한다.
- 추가되는 명령: `go.get`, `go.set_active`, `go.set_field`, `go.find`, `select`, `play.enter`, `play.stop`, `play.pause`, `play.resume`, `play.state`, `scene.save`, `scene.load`, `log.recent`
- 이 phase 완료 시 모든 초기 명령 세트가 CLI에서 동작한다.

## 선행 조건
- Phase 46a 완료 (CliCommandDispatcher, CliPipeServer, CliLogBuffer 존재)
- Phase 46b 완료 (Python CLI 래퍼로 테스트 가능)

## 코딩 규칙
- C# 파일은 UTF-8 with BOM 인코딩
- 파일 경로는 항상 `Path.Combine()` 사용
- 네이밍: PascalCase(클래스/메서드), camelCase(필드/변수), UPPER_CASE(상수)
- 디버깅 로그는 `RoseEngine.EditorDebug.Log()` 사용

## 수정할 파일

### `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`

- **변경 내용**: `RegisterHandlers()` 메서드에 나머지 명령 핸들러를 추가한다.
- **이유**: Phase 46a에서는 `ping`, `scene.info`, `scene.list` 3개만 구현했다. 나머지 명령을 추가하여 전체 초기 명령 세트를 완성한다.

**추가할 using 문** (필요한 것만, 기존에 없는 것):
```csharp
using IronRose.Engine.Editor;  // EditorPlayMode, EditorSelection, SceneSerializer, SceneSnapshot 등
using System.Linq;             // FirstOrDefault
using System.Reflection;       // BindingFlags (go.get 상세 정보용)
using System.Globalization;    // CultureInfo (go.set_field의 ParseValue용)
```

**추가할 핸들러 목록과 구현 상세**:

---

#### 1. `go.get` -- 특정 GameObject 상세 정보 (메인 스레드)

- **인자**: `<id|name>` -- 숫자면 InstanceID로 검색, 문자열이면 이름으로 첫 매칭 GO를 찾는다.
- **응답**: `{ "id": int, "name": "...", "active": bool, "components": [{ "typeName": "...", "fields": [{ "name": "...", "typeName": "...", "value": "..." }] }] }`

```csharp
_handlers["go.get"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: go.get <id|name>");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObject(args[0]);
        if (go == null)
            return JsonError($"GameObject not found: {args[0]}");

        var snapshot = GameObjectSnapshot.From(go);
        return JsonOk(new
        {
            id = snapshot.InstanceId,
            name = snapshot.Name,
            active = snapshot.ActiveSelf,
            parentId = snapshot.ParentId,
            components = snapshot.Components.Select(c => new
            {
                typeName = c.TypeName,
                fields = c.Fields.Select(f => new
                {
                    name = f.Name,
                    typeName = f.TypeName,
                    value = f.Value
                })
            })
        });
    });
};
```

- **구현 힌트**:
  - `GameObjectSnapshot.From(go)`는 `IronRose.Engine.Editor.GameObjectSnapshot` 클래스의 정적 메서드이다. `SceneSnapshot.cs` 파일에 정의되어 있다.
  - `GameObjectSnapshot`의 필드: `InstanceId`, `Name`, `ActiveSelf`, `ParentId`, `Components` (ComponentSnapshot[])
  - `ComponentSnapshot`의 필드: `TypeName`, `Fields` (FieldSnapshot[])
  - `FieldSnapshot`의 필드: `Name`, `TypeName`, `Value`
  - `using IronRose.Engine.Editor;` 필요.
  - `using System.Linq;` 필요 (Select 사용).

---

#### 2. `go.find` -- 이름으로 GameObject 검색 (메인 스레드)

- **인자**: `<name>` -- 부분 매칭이 아닌 정확 매칭. 대소문자 구분.
- **응답**: `{ "gameObjects": [{ "id": int, "name": "..." }] }`

```csharp
_handlers["go.find"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: go.find <name>");

    var searchName = args[0];
    return ExecuteOnMainThread(() =>
    {
        var matches = new List<object>();
        foreach (var go in SceneManager.AllGameObjects)
        {
            if (go._isDestroyed) continue;
            if (go.name == searchName)
            {
                matches.Add(new
                {
                    id = go.GetInstanceID(),
                    name = go.name
                });
            }
        }
        return JsonOk(new { gameObjects = matches });
    });
};
```

---

#### 3. `go.set_active` -- GameObject 활성/비활성 (메인 스레드)

- **인자**: `<id> <true|false>`
- **응답**: `{ "ok": true }`

```csharp
_handlers["go.set_active"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: go.set_active <id> <true|false>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    if (!bool.TryParse(args[1], out var active))
        return JsonError($"Invalid bool value: {args[1]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        go.SetActive(active);
        return JsonOk(new { ok = true });
    });
};
```

- **구현 힌트**: `go.SetActive(bool)` 메서드는 `GameObject` 클래스에 정의되어 있다. 기존 `SetActiveCommand`와 동일한 로직이지만, `EditorBridge.EnqueueCommand()`를 거치지 않고 직접 실행한다 (이미 메인 스레드에서 실행되므로).

---

#### 4. `go.set_field` -- 컴포넌트 필드 수정 (메인 스레드)

- **인자**: `<id> <componentType> <fieldName> <value>`
- **응답**: `{ "ok": true }`

```csharp
_handlers["go.set_field"] = args =>
{
    if (args.Length < 4)
        return JsonError("Usage: go.set_field <id> <component> <field> <value>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    var componentType = args[1];
    var fieldName = args[2];
    var newValue = args[3];

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        var comp = go.InternalComponents
            .FirstOrDefault(c => c.GetType().Name == componentType);
        if (comp == null)
            return JsonError($"Component not found: {componentType}");

        var field = comp.GetType().GetField(fieldName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
            return JsonError($"Field not found: {fieldName}");

        var value = ParseFieldValue(field.FieldType, newValue);
        if (value == null)
            return JsonError($"Cannot parse value '{newValue}' as {field.FieldType.Name}");

        field.SetValue(comp, value);
        SceneManager.GetActiveScene().isDirty = true;
        return JsonOk(new { ok = true });
    });
};
```

- **구현 힌트**:
  - `go.InternalComponents`는 `IReadOnlyList<Component>` 타입이다.
  - `ParseFieldValue` 메서드를 새로 추가해야 한다. 기존 `SetFieldCommand.ParseValue`의 로직을 재활용한다:
  ```csharp
  private static object? ParseFieldValue(Type type, string raw)
  {
      try
      {
          if (type == typeof(float)) return float.Parse(raw, CultureInfo.InvariantCulture);
          if (type == typeof(int)) return int.Parse(raw, CultureInfo.InvariantCulture);
          if (type == typeof(bool)) return bool.Parse(raw);
          if (type == typeof(string)) return raw;
          if (type == typeof(RoseEngine.Vector3)) return ParseVector3(raw);
          if (type == typeof(RoseEngine.Color)) return ParseColor(raw);
          if (type.IsEnum) return Enum.Parse(type, raw);
      }
      catch { }
      return null;
  }

  private static RoseEngine.Vector3 ParseVector3(string raw)
  {
      var cleaned = raw.Trim('(', ')', ' ');
      var parts = cleaned.Split(',');
      return new RoseEngine.Vector3(
          float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
          float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
          float.Parse(parts[2].Trim(), CultureInfo.InvariantCulture));
  }

  private static RoseEngine.Color ParseColor(string raw)
  {
      var cleaned = raw.Trim('(', ')', ' ');
      var parts = cleaned.Split(',');
      return new RoseEngine.Color(
          float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
          float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
          float.Parse(parts[2].Trim(), CultureInfo.InvariantCulture),
          parts.Length > 3 ? float.Parse(parts[3].Trim(), CultureInfo.InvariantCulture) : 1f);
  }
  ```
  - 이 메서드들은 `EditorCommand.cs`의 `SetFieldCommand`에 있는 것과 동일한 로직이다. 직접 복사하여 `CliCommandDispatcher` 내부에 추가한다 (SetFieldCommand의 ParseValue는 private이므로 호출 불가).
  - `SceneManager.GetActiveScene().isDirty = true;` 설정하여 변경 사항을 추적한다.

---

#### 5. `select` -- 에디터 선택 변경 (메인 스레드)

- **인자**: `<id>` -- GameObject InstanceID. `none` 또는 빈 인자이면 선택 해제.
- **응답**: `{ "ok": true }`

```csharp
_handlers["select"] = args =>
{
    return ExecuteOnMainThread(() =>
    {
        if (args.Length == 0 || args[0].Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            EditorSelection.Clear();
            return JsonOk(new { ok = true });
        }

        if (!int.TryParse(args[0], out var id))
            return JsonError($"Invalid GameObject ID: {args[0]}");

        EditorSelection.Select(id);
        return JsonOk(new { ok = true });
    });
};
```

- **구현 힌트**: `EditorSelection`은 `IronRose.Engine.Editor` 네임스페이스의 static 클래스이다. `Select(int?)` 메서드와 `Clear()` 메서드를 사용한다.

---

#### 6. `play.enter` -- Play 모드 진입 (메인 스레드)

```csharp
_handlers["play.enter"] = args => ExecuteOnMainThread(() =>
{
    EditorPlayMode.EnterPlayMode();
    return JsonOk(new { state = EditorPlayMode.State.ToString() });
});
```

#### 7. `play.stop` -- Play 모드 종료 (메인 스레드)

```csharp
_handlers["play.stop"] = args => ExecuteOnMainThread(() =>
{
    EditorPlayMode.StopPlayMode();
    return JsonOk(new { state = EditorPlayMode.State.ToString() });
});
```

#### 8. `play.pause` -- 일시정지 (메인 스레드)

```csharp
_handlers["play.pause"] = args => ExecuteOnMainThread(() =>
{
    EditorPlayMode.PausePlayMode();
    return JsonOk(new { state = EditorPlayMode.State.ToString() });
});
```

#### 9. `play.resume` -- 재개 (메인 스레드)

```csharp
_handlers["play.resume"] = args => ExecuteOnMainThread(() =>
{
    EditorPlayMode.ResumePlayMode();
    return JsonOk(new { state = EditorPlayMode.State.ToString() });
});
```

#### 10. `play.state` -- 현재 Play 상태 조회 (메인 스레드)

```csharp
_handlers["play.state"] = args => ExecuteOnMainThread(() =>
{
    return JsonOk(new { state = EditorPlayMode.State.ToString() });
});
```

- **구현 힌트**: `EditorPlayMode`은 `IronRose.Engine.Editor` 네임스페이스의 static 클래스이다.
  - `State` 프로퍼티 타입: `PlayModeState` enum (`Edit`, `Playing`, `Paused`)
  - `EnterPlayMode()` -- Edit 상태에서만 동작, 아니면 무시
  - `StopPlayMode()` -- Playing 또는 Paused 상태에서 동작
  - `PausePlayMode()` -- Playing 상태에서만 동작
  - `ResumePlayMode()` -- Paused 상태에서만 동작

---

#### 11. `scene.save` -- 현재 씬 저장 (메인 스레드)

- **인자**: `[path]` (선택) -- 경로를 지정하면 해당 경로에 저장, 미지정이면 현재 씬 경로에 저장
- **응답**: `{ "saved": true, "path": "..." }`

```csharp
_handlers["scene.save"] = args => ExecuteOnMainThread(() =>
{
    var scene = SceneManager.GetActiveScene();
    var savePath = args.Length > 0 ? args[0] : scene.path;

    if (string.IsNullOrEmpty(savePath))
        return JsonError("No save path specified and scene has no existing path");

    SceneSerializer.Save(savePath);
    return JsonOk(new { saved = true, path = savePath });
});
```

- **구현 힌트**: `SceneSerializer.Save(string filePath)` -- 현재 씬을 TOML로 직렬화하여 파일에 저장한다. `using IronRose.Engine.Editor;` 필요.

---

#### 12. `scene.load` -- 씬 파일 로드 (메인 스레드)

- **인자**: `<path>` -- 씬 파일 절대 경로
- **응답**: `{ "loaded": true }`

```csharp
_handlers["scene.load"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: scene.load <path>");

    var loadPath = args[0];
    return ExecuteOnMainThread(() =>
    {
        if (!System.IO.File.Exists(loadPath))
            return JsonError($"File not found: {loadPath}");

        SceneSerializer.Load(loadPath);
        return JsonOk(new { loaded = true });
    });
};
```

- **구현 힌트**: `SceneSerializer.Load(string filePath)` -- 파일 존재 확인 후 TOML에서 씬을 역직렬화한다.

---

#### 13. `log.recent` -- 최근 로그 조회 (스레드 안전, 직접 실행)

- **인자**: `[count]` (선택, 기본 50)
- **응답**: `{ "logs": [{ "level": "Info", "message": "...", "timestamp": "..." }] }`

```csharp
_handlers["log.recent"] = args =>
{
    int count = 50;
    if (args.Length > 0 && int.TryParse(args[0], out var c))
        count = c;

    var entries = _logBuffer.GetRecent(count);
    var logs = entries.Select(e => new
    {
        level = e.Level.ToString(),
        source = e.Source.ToString(),
        message = e.Message,
        timestamp = e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")
    });
    return JsonOk(new { logs });
};
```

- **구현 힌트**:
  - `CliLogBuffer.GetRecent(int count)` 반환 타입: `List<LogEntry>`. 스레드 안전하므로 메인 스레드 큐를 거치지 않고 직접 실행한다.
  - `LogEntry` record 타입의 필드: `Level` (LogLevel enum: Info, Warning, Error), `Source` (LogSource enum: Editor, Project), `Message` (string), `Timestamp` (DateTime)
  - `using System.Linq;` 필요 (Select 사용).

---

#### 헬퍼 메서드 추가

`CliCommandDispatcher` 클래스에 다음 private 헬퍼 메서드들을 추가한다:

```csharp
/// <summary>ID 또는 이름으로 GameObject를 찾는다. 메인 스레드에서 호출해야 한다.</summary>
private static RoseEngine.GameObject? FindGameObject(string idOrName)
{
    if (int.TryParse(idOrName, out var id))
        return FindGameObjectById(id);

    // 이름으로 검색 (첫 번째 매칭)
    foreach (var go in SceneManager.AllGameObjects)
    {
        if (!go._isDestroyed && go.name == idOrName)
            return go;
    }
    return null;
}

/// <summary>InstanceID로 GameObject를 찾는다. 메인 스레드에서 호출해야 한다.</summary>
private static RoseEngine.GameObject? FindGameObjectById(int id)
{
    foreach (var go in SceneManager.AllGameObjects)
    {
        if (!go._isDestroyed && go.GetInstanceID() == id)
            return go;
    }
    return null;
}
```

- **구현 힌트**:
  - `go._isDestroyed`는 `internal` 필드이며 같은 프로젝트(`IronRose.Engine`) 내에서 접근 가능하다.
  - `RoseEngine.GameObject` 타입 지정이 필요할 수 있다 (네임스페이스 충돌 방지). `using RoseEngine;`이 이미 있으면 `GameObject`로 직접 사용.
  - LINQ의 `FirstOrDefault` 대신 foreach 루프를 사용하는 이유: 기존 `EditorCommand.cs`의 패턴과 일관성 유지 및 불필요한 delegate 할당 방지.

---

## NuGet 패키지
- 없음

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] `go.get <id>` -- 특정 GameObject의 상세 정보(컴포넌트, 필드 포함)가 JSON으로 반환된다.
- [ ] `go.find "Main Camera"` -- 이름으로 검색하여 매칭되는 GameObject 목록이 반환된다.
- [ ] `go.set_active <id> false` -- GameObject가 비활성화된다.
- [ ] `go.set_field <id> Transform position "1, 2, 3"` -- Transform 컴포넌트의 position 필드가 변경되고 에디터 화면에 반영된다.
- [ ] `select <id>` -- 에디터 선택 상태가 변경되고 Inspector에 해당 GO가 표시된다.
- [ ] `play.enter` -> `play.state` -> `play.stop` 시퀀스가 정상 동작한다 ("Playing" -> "Edit" 상태 전환).
- [ ] `play.pause` -> `play.resume` 시퀀스가 정상 동작한다.
- [ ] `scene.save` -- 현재 씬이 저장되고 저장 경로가 반환된다.
- [ ] `scene.load <path>` -- 지정한 씬 파일이 로드된다.
- [ ] `log.recent` -- 최근 로그 엔트리가 JSON 배열로 반환된다.
- [ ] `log.recent 10` -- count 인자를 지정하면 해당 개수만큼 반환된다.
- [ ] 존재하지 않는 GO ID로 `go.get` 호출 시 에러 응답이 반환된다.
- [ ] 인자 부족 시 Usage 메시지가 포함된 에러 응답이 반환된다.

## 참고
- `GameObjectSnapshot.From(go)` 사용: 이 메서드는 `SceneSnapshot.cs` 파일에 정의되어 있으며, GO의 모든 컴포넌트와 필드를 리플렉션으로 스냅샷한다. `go.get` 명령에서 이를 재활용하여 일관된 정보를 제공한다.
- `ParseFieldValue` / `ParseVector3` / `ParseColor`: `EditorCommand.cs`의 `SetFieldCommand` 내부에 동일한 로직이 있지만 `private`이므로 호출할 수 없다. `CliCommandDispatcher` 내부에 별도로 구현한다.
- `scene.save` 시 경로 미지정이면 현재 씬의 `path`를 사용한다. 새 씬(path가 null)인 경우 에러를 반환한다.
- `go.set_field` 실행 후 `SceneManager.GetActiveScene().isDirty = true` 설정이 중요하다. 이를 빠뜨리면 저장 확인 대화상자에서 변경 사항이 감지되지 않는다.
- `log.recent`는 `CliLogBuffer`가 스레드 안전하므로 `ExecuteOnMainThread`를 거치지 않고 직접 실행한다. 이는 메인 스레드가 블로킹된 상태에서도 로그를 조회할 수 있게 해준다.
