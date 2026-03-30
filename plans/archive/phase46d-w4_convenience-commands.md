# Phase 46d-w4: 편의 기능 명령 (나머지 전부)

## 목표
- 설계 문서의 Wave 4에 해당하는 모든 나머지 명령을 CLI에 추가한다.
- Transform 추가: `transform.translate`, `transform.rotate`, `transform.look_at`, `transform.get_children`, `transform.set_local_position`
- Prefab 추가: `prefab.create_variant`, `prefab.is_instance`, `prefab.unpack`
- Asset 추가: `asset.import`, `asset.scan`
- Editor 추가: `editor.screenshot`, `editor.copy`, `editor.paste`, `editor.select_all`, `editor.undo_history`
- Screen: `screen.info`
- Scene 추가: `scene.clear`
- Camera 추가: `camera.set_clip`
- Light 추가: `light.set_type`, `light.set_range`, `light.set_shadows`
- Render 추가: `render.set_skybox_exposure`

## 선행 조건
- Phase 46d-w3 완료 (렌더링/비주얼 명령이 CliCommandDispatcher에 등록되어 있음)
- `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`에 Wave 1~3 핸들러가 존재

## 수정할 파일

### `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`

- **변경 내용**: `RegisterHandlers()` 메서드 끝에 Wave 4 핸들러를 추가한다. 기존 핸들러는 수정하지 않는다.
- **이유**: 설계 문서의 Wave 4 편의 기능 명령 세트를 구현하기 위함.
- **인코딩**: UTF-8 with BOM

#### using 추가 불필요
Wave 1~3에서 추가한 using으로 충분하다.

#### 핸들러 구현 상세

`RegisterHandlers()` 메서드 끝 (Wave 3 핸들러 뒤)에 아래 핸들러들을 순서대로 추가한다.

---

### Transform 확장

##### 1. `transform.translate` -- 상대 이동 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// transform.translate -- 상대 이동 (메인 스레드)
// ----------------------------------------------------------------
_handlers["transform.translate"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: transform.translate <id> <x,y,z>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        try
        {
            go.transform.Translate(ParseVector3(args[1]));
            SceneManager.GetActiveScene().isDirty = true;
            return JsonOk(new { ok = true });
        }
        catch (Exception ex)
        {
            return JsonError($"Failed to parse translation: {ex.Message}");
        }
    });
};
```

- **인자**: `<id> <x,y,z>` (상대 이동량)
- **응답**: `{ "ok": true }`
- **API**: `transform.Translate(Vector3 translation)` -- `position += translation` (월드 좌표 기준 상대 이동).

---

##### 2. `transform.rotate` -- 상대 회전 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// transform.rotate -- 상대 회전 (메인 스레드)
// ----------------------------------------------------------------
_handlers["transform.rotate"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: transform.rotate <id> <x,y,z>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        try
        {
            go.transform.Rotate(ParseVector3(args[1]));
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

- **인자**: `<id> <x,y,z>` (오일러 각도 추가분)
- **응답**: `{ "ok": true }`
- **API**: `transform.Rotate(Vector3 eulers)` -- `rotation = rotation * Quaternion.Euler(eulers)` (로컬 기준 회전).

---

##### 3. `transform.look_at` -- 타겟을 바라봄 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// transform.look_at -- 타겟을 바라봄 (메인 스레드)
// ----------------------------------------------------------------
_handlers["transform.look_at"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: transform.look_at <id> <targetId>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    if (!int.TryParse(args[1], out var targetId))
        return JsonError($"Invalid target GameObject ID: {args[1]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        var target = FindGameObjectById(targetId);
        if (target == null)
            return JsonError($"Target GameObject not found: {targetId}");

        go.transform.LookAt(target.transform);
        SceneManager.GetActiveScene().isDirty = true;
        return JsonOk(new { ok = true });
    });
};
```

- **인자**: `<id> <targetId>` (자신 ID, 타겟 ID)
- **응답**: `{ "ok": true }`
- **API**: `transform.LookAt(Transform target)` -- `LookAt(target.position, Vector3.up)` 호출. 타겟의 월드 위치를 향해 회전.

---

##### 4. `transform.get_children` -- 자식 목록 조회 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// transform.get_children -- 자식 목록 조회 (메인 스레드)
// ----------------------------------------------------------------
_handlers["transform.get_children"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: transform.get_children <id>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        var children = new List<object>();
        for (int i = 0; i < go.transform.childCount; i++)
        {
            var child = go.transform.GetChild(i).gameObject;
            if (!child._isDestroyed)
            {
                children.Add(new
                {
                    id = child.GetInstanceID(),
                    name = child.name,
                    active = child.activeSelf
                });
            }
        }
        return JsonOk(new { children });
    });
};
```

- **인자**: `<id>` (InstanceID)
- **응답**: `{ "children": [{ "id": int, "name": "...", "active": bool }] }`
- **API**: `transform.childCount`, `transform.GetChild(int index)` -- 직접 자식만 반환 (손자 제외).

---

##### 5. `transform.set_local_position` -- 로컬 위치 설정 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// transform.set_local_position -- 로컬 위치 설정 (메인 스레드)
// ----------------------------------------------------------------
_handlers["transform.set_local_position"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: transform.set_local_position <id> <x,y,z>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        try
        {
            go.transform.localPosition = ParseVector3(args[1]);
            SceneManager.GetActiveScene().isDirty = true;
            return JsonOk(new { ok = true });
        }
        catch (Exception ex)
        {
            return JsonError($"Failed to parse local position: {ex.Message}");
        }
    });
};
```

- **인자**: `<id> <x,y,z>` (부모 기준 로컬 좌표)
- **응답**: `{ "ok": true }`
- **API**: `transform.localPosition` setter.

---

### Prefab 확장

##### 6. `prefab.create_variant` -- Variant 프리팹 생성 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// prefab.create_variant -- Variant 프리팹 생성 (메인 스레드)
// ----------------------------------------------------------------
_handlers["prefab.create_variant"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: prefab.create_variant <baseGuid> <path>");

    var baseGuid = args[0];
    var path = args[1];
    return ExecuteOnMainThread(() =>
    {
        try
        {
            var variantGuid = PrefabUtility.CreateVariant(baseGuid, path);
            if (string.IsNullOrEmpty(variantGuid))
                return JsonError($"Failed to create variant for base: {baseGuid}");

            return JsonOk(new { created = true, path, guid = variantGuid });
        }
        catch (Exception ex)
        {
            return JsonError($"Failed to create variant: {ex.Message}");
        }
    });
};
```

- **인자**: `<baseGuid> <path>` (base 프리팹 GUID, Variant 저장 경로)
- **응답**: `{ "created": true, "path": "...", "guid": "..." }`
- **API**: `PrefabUtility.CreateVariant(string basePrefabGuid, string variantPath)` -- basePrefabGuid만 있는 빈 Variant .prefab TOML 파일 생성. .rose 메타 생성. AssetDatabase에 등록. Variant GUID 반환.
- **주의**: IronRose는 Prefab Override를 지원하지 않으므로, Variant는 base와 동일한 값을 가진다. 값을 변경하려면 Variant 파일을 직접 수정해야 한다.

---

##### 7. `prefab.is_instance` -- 프리팹 인스턴스 여부 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// prefab.is_instance -- 프리팹 인스턴스 여부 (메인 스레드)
// ----------------------------------------------------------------
_handlers["prefab.is_instance"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: prefab.is_instance <goId>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        var isPrefab = PrefabUtility.IsPrefabInstance(go);
        var guid = PrefabUtility.GetPrefabGuid(go);
        return JsonOk(new { isPrefab, guid = guid ?? "" });
    });
};
```

- **인자**: `<goId>` (InstanceID)
- **응답**: `{ "isPrefab": bool, "guid": "..." }`
- **API**:
  - `PrefabUtility.IsPrefabInstance(GameObject go)` -- `go.GetComponent<PrefabInstance>() != null`.
  - `PrefabUtility.GetPrefabGuid(GameObject go)` -- PrefabInstance 컴포넌트의 `prefabGuid`.

---

##### 8. `prefab.unpack` -- 프리팹 인스턴스 언팩 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// prefab.unpack -- 프리팹 인스턴스 언팩 (메인 스레드)
// ----------------------------------------------------------------
_handlers["prefab.unpack"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: prefab.unpack <goId>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        if (!PrefabUtility.IsPrefabInstance(go))
            return JsonError($"GameObject is not a prefab instance: {id}");

        PrefabUtility.UnpackPrefabInstance(go);
        SceneManager.GetActiveScene().isDirty = true;
        return JsonOk(new { ok = true });
    });
};
```

- **인자**: `<goId>` (InstanceID)
- **응답**: `{ "ok": true }`
- **API**: `PrefabUtility.UnpackPrefabInstance(GameObject instanceRoot)` -- PrefabInstance 컴포넌트를 제거하여 일반 GO로 변환.

---

### Asset 확장

##### 9. `asset.import` -- 에셋 임포트/리임포트 트리거 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// asset.import -- 에셋 임포트/리임포트 트리거 (메인 스레드)
// ----------------------------------------------------------------
_handlers["asset.import"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: asset.import <path>");

    var path = args[0];
    return ExecuteOnMainThread(() =>
    {
        if (!System.IO.File.Exists(path))
            return JsonError($"File not found: {path}");

        var db = Resources.GetAssetDatabase();
        if (db == null)
            return JsonError("AssetDatabase not initialized");

        // Load를 호출하면 내부적으로 import 트리거가 발생한다.
        // 이미 로드된 에셋은 캐시에서 반환되므로, 강제 리임포트를 위해
        // 에셋 경로를 ScanAssets로 재스캔한다.
        var projectPath = ProjectContext.AssetsPath;
        if (!string.IsNullOrEmpty(projectPath))
        {
            db.ScanAssets(projectPath);
            return JsonOk(new { ok = true });
        }

        return JsonError("Project assets path not configured");
    });
};
```

- **인자**: `<path>` (에셋 파일 경로)
- **응답**: `{ "ok": true }`
- **API**: `db.ScanAssets(string projectPath)` -- 프로젝트 Assets 폴더를 재스캔. 내부적으로 GUID 매핑을 갱신한다.
- **참고**: `ProjectContext.AssetsPath`로 Assets 폴더 경로를 가져온다.

---

##### 10. `asset.scan` -- 에셋 스캔 실행 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// asset.scan -- 에셋 스캔 실행 (메인 스레드)
// ----------------------------------------------------------------
_handlers["asset.scan"] = args =>
{
    return ExecuteOnMainThread(() =>
    {
        var db = Resources.GetAssetDatabase();
        if (db == null)
            return JsonError("AssetDatabase not initialized");

        var projectPath = args.Length > 0 ? args[0] : ProjectContext.AssetsPath;
        if (string.IsNullOrEmpty(projectPath))
            return JsonError("No assets path specified");

        db.ScanAssets(projectPath);
        return JsonOk(new { count = db.AssetCount });
    });
};
```

- **인자**: `[path]` (선택, 기본 ProjectContext.AssetsPath)
- **응답**: `{ "count": int }`
- **API**: `db.ScanAssets(string projectPath)`, `db.AssetCount` (int property -- `_guidToPath.Count`).

---

### Editor 확장

##### 11. `editor.screenshot` -- 현재 화면 캡처 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// editor.screenshot -- 현재 화면 캡처 (메인 스레드)
// ----------------------------------------------------------------
_handlers["editor.screenshot"] = args =>
{
    return ExecuteOnMainThread(() =>
    {
        string path;
        if (args.Length > 0)
        {
            path = args[0];
        }
        else
        {
            var dir = System.IO.Path.Combine(ProjectContext.ProjectRoot, "Screenshots");
            System.IO.Directory.CreateDirectory(dir);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            path = System.IO.Path.Combine(dir, $"cli_screenshot_{timestamp}.png");
        }

        // GraphicsManager에 스크린샷 요청을 전달하기 위해 static 필드 사용
        _pendingScreenshotPath = path;
        return JsonOk(new { saved = true, path });
    });
};
```

- **인자**: `[path]` (선택, 기본: `{ProjectRoot}/Screenshots/cli_screenshot_{timestamp}.png`)
- **응답**: `{ "saved": true, "path": "..." }`
- **구현 특이사항**: 스크린샷은 GPU 렌더링 프레임의 끝에서만 캡처할 수 있다. CLI 핸들러에서 직접 캡처할 수 없으므로, static 필드에 경로를 저장해두고 EngineCore가 다음 프레임에서 캡처를 수행하도록 한다.

**CliCommandDispatcher에 static 필드 추가** (클래스 필드 영역):

```csharp
/// <summary>CLI에서 요청한 스크린샷 경로. EngineCore.Update()에서 소비.</summary>
internal static string? _pendingScreenshotPath;
```

**참고**: 이 필드는 EngineCore에서 읽어야 한다. EngineCore.cs 수정은 아래 별도 섹션 참조.

---

##### 12. `editor.copy` -- GO 복사 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// editor.copy -- GO 복사 (클립보드, 메인 스레드)
// ----------------------------------------------------------------
_handlers["editor.copy"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: editor.copy <goId>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        // EditorSelection에 임시로 선택 후 Copy 호출
        EditorSelection.Select(id);
        EditorClipboard.CopyGameObjects(cut: false);
        return JsonOk(new { ok = true });
    });
};
```

- **인자**: `<goId>` (InstanceID)
- **응답**: `{ "ok": true }`
- **API**:
  - `EditorSelection.Select(int? id)` -- 단일 선택.
  - `EditorClipboard.CopyGameObjects(bool cut)` -- 현재 선택된 GO를 클립보드에 복사. 내부적으로 `SceneSerializer.SerializeGameObjectHierarchy()`로 직렬화.
- **주의**: `EditorClipboard`는 `internal static class`이므로, 같은 어셈블리(IronRose.Engine)에서만 접근 가능. CliCommandDispatcher는 같은 어셈블리이므로 접근 가능.

---

##### 13. `editor.paste` -- 클립보드에서 붙여넣기 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// editor.paste -- 클립보드에서 붙여넣기 (메인 스레드)
// ----------------------------------------------------------------
_handlers["editor.paste"] = args =>
{
    return ExecuteOnMainThread(() =>
    {
        if (EditorClipboard.ClipboardKind != EditorClipboard.Kind.GameObjects)
            return JsonError("No GameObject in clipboard");

        EditorClipboard.PasteGameObjects();

        var selected = EditorSelection.SelectedGameObject;
        if (selected != null)
            return JsonOk(new { id = selected.GetInstanceID(), name = selected.name });

        return JsonOk(new { ok = true });
    });
};
```

- **인자**: 없음
- **응답**: `{ "id": int, "name": "..." }` (붙여넣은 GO) 또는 `{ "ok": true }`
- **API**:
  - `EditorClipboard.PasteGameObjects()` -- 클립보드의 GO를 역직렬화하여 씬에 생성. 현재 선택된 GO의 자식으로 배치 (없으면 루트).
  - `EditorSelection.SelectedGameObject` -- Paste 후 자동으로 새 GO가 선택됨.

---

##### 14. `editor.select_all` -- 모든 GO 선택 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// editor.select_all -- 모든 GO 선택 (메인 스레드)
// ----------------------------------------------------------------
_handlers["editor.select_all"] = args =>
{
    return ExecuteOnMainThread(() =>
    {
        var ids = new List<int>();
        foreach (var go in SceneManager.AllGameObjects)
        {
            if (!go._isDestroyed)
                ids.Add(go.GetInstanceID());
        }
        EditorSelection.SetSelection(ids);
        return JsonOk(new { count = ids.Count });
    });
};
```

- **인자**: 없음
- **응답**: `{ "count": int }`
- **API**: `EditorSelection.SetSelection(IEnumerable<int> ids)` -- 프로그래밍적 멀티 선택.

---

##### 15. `editor.undo_history` -- Undo/Redo 스택 설명 조회 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// editor.undo_history -- Undo/Redo 스택 설명 조회 (메인 스레드)
// ----------------------------------------------------------------
_handlers["editor.undo_history"] = args =>
{
    return ExecuteOnMainThread(() =>
    {
        return JsonOk(new
        {
            undo = UndoSystem.UndoDescription ?? "",
            redo = UndoSystem.RedoDescription ?? ""
        });
    });
};
```

- **인자**: 없음
- **응답**: `{ "undo": "...", "redo": "..." }` (각각 스택 최상단의 Description)
- **API**: `UndoSystem.UndoDescription` (string? -- `_undoStack[^1].Description`), `UndoSystem.RedoDescription` (string? -- `_redoStack[^1].Description`).

---

### Screen

##### 16. `screen.info` -- 화면 정보 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// screen.info -- 화면 정보 (메인 스레드)
// ----------------------------------------------------------------
_handlers["screen.info"] = args =>
{
    return ExecuteOnMainThread(() =>
    {
        return JsonOk(new
        {
            width = Screen.width,
            height = Screen.height,
            dpi = Screen.dpi
        });
    });
};
```

- **인자**: 없음
- **응답**: `{ "width": int, "height": int, "dpi": float }`
- **API**: `Screen.width` (static int), `Screen.height` (static int), `Screen.dpi` (static float). 모두 `RoseEngine.Screen`의 static property.

---

### Scene 확장

##### 17. `scene.clear` -- 씬 내 모든 GO 삭제 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// scene.clear -- 씬 내 모든 GO 삭제 (메인 스레드)
// ----------------------------------------------------------------
_handlers["scene.clear"] = args =>
{
    return ExecuteOnMainThread(() =>
    {
        SceneManager.Clear();
        return JsonOk(new { ok = true });
    });
};
```

- **인자**: 없음
- **응답**: `{ "ok": true }`
- **API**: `SceneManager.Clear()` -- 모든 GO/MonoBehaviour/코루틴/물리/렌더러 정리. `scene.new`와 달리 새 Scene 객체를 생성하지 않는다 (기존 Scene을 유지하며 내용만 비운다).

---

### Camera 확장

##### 18. `camera.set_clip` -- 클리핑 설정 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// camera.set_clip -- 클리핑 설정 (메인 스레드)
// ----------------------------------------------------------------
_handlers["camera.set_clip"] = args =>
{
    if (args.Length < 3)
        return JsonError("Usage: camera.set_clip <id> <near> <far>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var near))
        return JsonError($"Invalid near value: {args[1]}");

    if (!float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var far))
        return JsonError($"Invalid far value: {args[2]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        var cam = go.GetComponent<Camera>();
        if (cam == null)
            return JsonError($"No Camera component on GameObject: {id}");

        cam.nearClipPlane = near;
        cam.farClipPlane = far;
        SceneManager.GetActiveScene().isDirty = true;
        return JsonOk(new { ok = true });
    });
};
```

- **인자**: `<id> <near> <far>` (float)
- **응답**: `{ "ok": true }`
- **API**: `Camera.nearClipPlane`, `Camera.farClipPlane` (public float field, 직접 할당).

---

### Light 확장

##### 19. `light.set_type` -- 라이트 타입 변경 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// light.set_type -- 라이트 타입 변경 (메인 스레드)
// ----------------------------------------------------------------
_handlers["light.set_type"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: light.set_type <id> <Directional|Point|Spot>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    if (!Enum.TryParse<LightType>(args[1], ignoreCase: true, out var lightType))
        return JsonError($"Unknown light type: {args[1]}. Valid: Directional, Point, Spot");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        var light = go.GetComponent<Light>();
        if (light == null)
            return JsonError($"No Light component on GameObject: {id}");

        light.type = lightType;
        SceneManager.GetActiveScene().isDirty = true;
        return JsonOk(new { ok = true });
    });
};
```

- **인자**: `<id> <type>` (Directional/Point/Spot, case-insensitive)
- **응답**: `{ "ok": true }`
- **API**: `Light.type` setter (`LightType` enum: Directional=0, Point=1, Spot=2).

---

##### 20. `light.set_range` -- 라이트 범위 변경 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// light.set_range -- 라이트 범위 변경 (메인 스레드)
// ----------------------------------------------------------------
_handlers["light.set_range"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: light.set_range <id> <value>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        return JsonError($"Invalid float value: {args[1]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        var light = go.GetComponent<Light>();
        if (light == null)
            return JsonError($"No Light component on GameObject: {id}");

        light.range = value;
        SceneManager.GetActiveScene().isDirty = true;
        return JsonOk(new { ok = true });
    });
};
```

- **인자**: `<id> <value>` (float, Point/Spot 라이트의 범위)
- **응답**: `{ "ok": true }`
- **API**: `Light.range` setter.

---

##### 21. `light.set_shadows` -- 그림자 on/off (메인 스레드)

```csharp
// ----------------------------------------------------------------
// light.set_shadows -- 그림자 on/off (메인 스레드)
// ----------------------------------------------------------------
_handlers["light.set_shadows"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: light.set_shadows <id> <true|false>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    if (!bool.TryParse(args[1], out var enabled))
        return JsonError($"Invalid bool value: {args[1]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        var light = go.GetComponent<Light>();
        if (light == null)
            return JsonError($"No Light component on GameObject: {id}");

        light.shadows = enabled;
        SceneManager.GetActiveScene().isDirty = true;
        return JsonOk(new { ok = true });
    });
};
```

- **인자**: `<id> <true|false>`
- **응답**: `{ "ok": true }`
- **API**: `Light.shadows` setter (bool property).

---

### Render 확장

##### 22. `render.set_skybox_exposure` -- 스카이박스 노출 변경 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// render.set_skybox_exposure -- 스카이박스 노출 변경 (메인 스레드)
// ----------------------------------------------------------------
_handlers["render.set_skybox_exposure"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: render.set_skybox_exposure <value>");

    if (!float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        return JsonError($"Invalid float value: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        RenderSettings.skyboxExposure = value;
        if (RenderSettings.skybox != null)
            RenderSettings.skybox.exposure = value;
        SceneManager.GetActiveScene().isDirty = true;
        return JsonOk(new { ok = true });
    });
};
```

- **인자**: `<value>` (float, 노출 값)
- **응답**: `{ "ok": true }`
- **API**:
  - `RenderSettings.skyboxExposure` setter -- 전역 skybox exposure 값.
  - `RenderSettings.skybox.exposure` -- Material의 exposure도 동기화 (skybox Material이 있는 경우).

---

#### 전체 변경 요약

1. `RegisterHandlers()` 메서드 끝에 22개 핸들러를 추가한다.
2. `_pendingScreenshotPath` static 필드를 CliCommandDispatcher 클래스에 추가한다.
3. using 추가 불필요.

## 수정할 파일 (추가)

### `src/IronRose.Engine/EngineCore.cs`

- **변경 내용**: `Update()` 메서드에서 CLI 스크린샷 요청을 처리하는 코드를 추가한다.
- **이유**: `editor.screenshot` 명령이 `_pendingScreenshotPath`에 경로를 설정하면, EngineCore가 다음 프레임에서 `GraphicsManager.RequestScreenshot()`을 호출해야 한다.

**삽입 위치**: `_cliDispatcher?.ProcessMainThreadQueue();` 바로 아래에 추가한다.

```csharp
// CLI 스크린샷 요청 처리
var cliScreenshotPath = CliCommandDispatcher._pendingScreenshotPath;
if (cliScreenshotPath != null)
{
    CliCommandDispatcher._pendingScreenshotPath = null;
    if (_graphicsManager != null)
    {
        var dir = System.IO.Path.GetDirectoryName(cliScreenshotPath);
        if (!string.IsNullOrEmpty(dir))
            System.IO.Directory.CreateDirectory(dir);
        _graphicsManager.RequestScreenshot(cliScreenshotPath);
    }
}
```

- **API**: `_graphicsManager.RequestScreenshot(string filename)` -- 다음 EndFrame에서 프레임버퍼를 캡처하여 파일로 저장.
- **주의**: `using IronRose.Engine.Cli;`가 EngineCore.cs 상단에 이미 있어야 한다 (Phase 46a에서 추가됨).

## NuGet 패키지
- 없음

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] `transform.translate <id> 1,0,0` -> 상대 이동
- [ ] `transform.rotate <id> 0,45,0` -> 상대 회전
- [ ] `transform.look_at <id> <targetId>` -> 타겟을 바라봄
- [ ] `transform.get_children <id>` -> 자식 목록
- [ ] `transform.set_local_position <id> 0,1,0` -> 로컬 위치 설정
- [ ] `prefab.create_variant <guid> path.prefab` -> Variant 생성
- [ ] `prefab.is_instance <id>` -> 프리팹 인스턴스 여부
- [ ] `prefab.unpack <id>` -> 프리팹 언팩
- [ ] `asset.import <path>` -> 에셋 리임포트
- [ ] `asset.scan` -> 에셋 스캔, count 반환
- [ ] `editor.screenshot` -> 스크린샷 경로 반환 (다음 프레임에서 캡처)
- [ ] `editor.copy <id>` -> GO 복사
- [ ] `editor.paste` -> 붙여넣기, 새 GO 반환
- [ ] `editor.select_all` -> 모든 GO 선택, count 반환
- [ ] `editor.undo_history` -> undo/redo 설명 반환
- [ ] `screen.info` -> 화면 width/height/dpi
- [ ] `scene.clear` -> 모든 GO 삭제
- [ ] `camera.set_clip <id> 0.1 500` -> near/far 변경
- [ ] `light.set_type <id> Point` -> 타입 변경
- [ ] `light.set_range <id> 20` -> 범위 변경
- [ ] `light.set_shadows <id> false` -> 그림자 끄기
- [ ] `render.set_skybox_exposure 1.5` -> 스카이박스 노출 변경

## 참고
- `editor.screenshot`은 비동기적으로 동작한다. CLI 응답은 즉시 반환되지만, 실제 스크린샷 파일은 다음 렌더링 프레임 이후에 생성된다. GPU가 프레임을 렌더링한 후 캡처하기 때문.
- `EditorClipboard`는 `internal static class`이다. CliCommandDispatcher와 같은 어셈블리(IronRose.Engine)이므로 접근 가능.
- `EditorClipboard.CopyGameObjects()`는 `EditorSelection.SelectedGameObjectIds`를 사용하므로, copy 전에 `EditorSelection.Select(id)`를 호출해야 한다.
- `ProjectContext.AssetsPath`가 `asset.import`, `asset.scan`에서 사용된다. 프로젝트 미로드 시 빈 문자열일 수 있다.
- `scene.clear`는 `scene.new`와 달리 새 Scene 객체를 만들지 않는다. 기존 씬의 name/path는 유지되며 내용만 비운다.
- `CliCommandDispatcher._pendingScreenshotPath`는 `internal static` 필드로, EngineCore에서 직접 접근한다. 스레드 안전성: 메인 스레드에서만 읽고 쓰므로 문제없다 (핸들러도 `ExecuteOnMainThread()`에서 실행).
