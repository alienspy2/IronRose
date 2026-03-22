# Phase 46d-w3: 렌더링/비주얼 명령 (material.*, light.*, camera.*, render.*)

## 목표
- 시각적 조작에 필요한 렌더링 관련 명령을 CLI에 추가한다.
- `material.info`, `material.set_color`, `material.set_metallic`, `material.set_roughness` (머티리얼)
- `light.info`, `light.set_color`, `light.set_intensity` (라이트)
- `camera.info`, `camera.set_fov` (카메라)
- `render.info`, `render.set_ambient` (렌더 설정)

## 선행 조건
- Phase 46d-w2 완료 (에셋/프리팹 명령이 CliCommandDispatcher에 등록되어 있음)
- `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`에 Wave 1, Wave 2 핸들러가 존재

## 수정할 파일

### `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`

- **변경 내용**: `RegisterHandlers()` 메서드 끝에 Wave 3 핸들러를 추가한다. 기존 핸들러는 수정하지 않는다.
- **이유**: 설계 문서의 Wave 3 명령 세트를 구현하기 위함.
- **인코딩**: UTF-8 with BOM

#### using 추가 불필요
Wave 1, 2에서 추가한 using으로 충분하다. `RoseEngine` 네임스페이스에 모든 렌더링 관련 클래스(Material, Light, Camera, RenderSettings, MeshRenderer)가 있다.

#### 핸들러 구현 상세

`RegisterHandlers()` 메서드 끝 (Wave 2 핸들러 뒤)에 아래 핸들러들을 순서대로 추가한다.

---

##### 1. `material.info` -- GO의 머티리얼 정보 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// material.info -- GO의 MeshRenderer 머티리얼 정보 (메인 스레드)
// ----------------------------------------------------------------
_handlers["material.info"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: material.info <goId>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        var renderer = go.GetComponent<MeshRenderer>();
        if (renderer == null)
            return JsonError($"No MeshRenderer on GameObject: {id}");

        var mat = renderer.material;
        if (mat == null)
            return JsonError($"No material assigned to MeshRenderer on: {id}");

        return JsonOk(new
        {
            name = mat.name,
            color = FormatColor(mat.color),
            metallic = mat.metallic,
            roughness = mat.roughness,
            occlusion = mat.occlusion,
            emission = FormatColor(mat.emission),
            hasMainTexture = mat.mainTexture != null,
            hasNormalMap = mat.normalMap != null,
            textureScale = $"{mat.textureScale.x.ToString(CultureInfo.InvariantCulture)}, {mat.textureScale.y.ToString(CultureInfo.InvariantCulture)}",
            textureOffset = $"{mat.textureOffset.x.ToString(CultureInfo.InvariantCulture)}, {mat.textureOffset.y.ToString(CultureInfo.InvariantCulture)}"
        });
    });
};
```

- **인자**: `<goId>` (InstanceID)
- **응답**: `{ "name": "...", "color": "r,g,b,a", "metallic": float, "roughness": float, "occlusion": float, "emission": "r,g,b,a", "hasMainTexture": bool, "hasNormalMap": bool, "textureScale": "x,y", "textureOffset": "x,y" }`
- **API**:
  - `go.GetComponent<MeshRenderer>()` -- MeshRenderer 컴포넌트 조회.
  - `MeshRenderer.material` -- Material 객체 (null 가능).
  - `Material.color`, `Material.metallic`, `Material.roughness`, `Material.occlusion`, `Material.emission`, `Material.mainTexture`, `Material.normalMap`, `Material.textureScale`, `Material.textureOffset`

**헬퍼 메서드 추가** (헬퍼 메서드 영역에 추가):

```csharp
/// <summary>Color를 "r, g, b, a" 문자열로 포맷한다.</summary>
private static string FormatColor(Color c)
{
    return $"{c.r.ToString(CultureInfo.InvariantCulture)}, {c.g.ToString(CultureInfo.InvariantCulture)}, {c.b.ToString(CultureInfo.InvariantCulture)}, {c.a.ToString(CultureInfo.InvariantCulture)}";
}
```

---

##### 2. `material.set_color` -- 머티리얼 색상 변경 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// material.set_color -- 머티리얼 색상 변경 (메인 스레드)
// ----------------------------------------------------------------
_handlers["material.set_color"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: material.set_color <goId> <r,g,b,a>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        var renderer = go.GetComponent<MeshRenderer>();
        if (renderer?.material == null)
            return JsonError($"No material on GameObject: {id}");

        try
        {
            renderer.material.color = ParseColor(args[1]);
            SceneManager.GetActiveScene().isDirty = true;
            return JsonOk(new { ok = true });
        }
        catch (Exception ex)
        {
            return JsonError($"Failed to parse color: {ex.Message}");
        }
    });
};
```

- **인자**: `<goId> <r,g,b,a>` (예: `42 1,0,0,1` -- 빨강). a 생략 시 기본 1.
- **응답**: `{ "ok": true }`
- **API**: `Material.color` setter. `ParseColor()`는 기존 헬퍼 (a 생략 시 1.0f 기본값).

---

##### 3. `material.set_metallic` -- metallic 변경 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// material.set_metallic -- metallic 변경 (메인 스레드)
// ----------------------------------------------------------------
_handlers["material.set_metallic"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: material.set_metallic <goId> <value>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        return JsonError($"Invalid float value: {args[1]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        var renderer = go.GetComponent<MeshRenderer>();
        if (renderer?.material == null)
            return JsonError($"No material on GameObject: {id}");

        renderer.material.metallic = value;
        SceneManager.GetActiveScene().isDirty = true;
        return JsonOk(new { ok = true });
    });
};
```

- **인자**: `<goId> <value>` (0.0 ~ 1.0)
- **응답**: `{ "ok": true }`
- **API**: `Material.metallic` setter (float, PBR property).
- **주의**: `using System.Globalization;`이 필요하다 (이미 있음). `NumberStyles.Float`로 소수점 파싱.

---

##### 4. `material.set_roughness` -- roughness 변경 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// material.set_roughness -- roughness 변경 (메인 스레드)
// ----------------------------------------------------------------
_handlers["material.set_roughness"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: material.set_roughness <goId> <value>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        return JsonError($"Invalid float value: {args[1]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        var renderer = go.GetComponent<MeshRenderer>();
        if (renderer?.material == null)
            return JsonError($"No material on GameObject: {id}");

        renderer.material.roughness = value;
        SceneManager.GetActiveScene().isDirty = true;
        return JsonOk(new { ok = true });
    });
};
```

- **인자**: `<goId> <value>` (0.0 ~ 1.0)
- **응답**: `{ "ok": true }`
- **API**: `Material.roughness` setter (float, PBR property).

---

##### 5. `light.info` -- 라이트 정보 조회 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// light.info -- 라이트 정보 조회 (메인 스레드)
// ----------------------------------------------------------------
_handlers["light.info"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: light.info <id>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        var light = go.GetComponent<Light>();
        if (light == null)
            return JsonError($"No Light component on GameObject: {id}");

        return JsonOk(new
        {
            type = light.type.ToString(),
            color = FormatColor(light.color),
            intensity = light.intensity,
            range = light.range,
            spotAngle = light.spotAngle,
            spotOuterAngle = light.spotOuterAngle,
            shadows = light.shadows,
            shadowResolution = light.shadowResolution,
            shadowBias = light.shadowBias,
            enabled = light.enabled
        });
    });
};
```

- **인자**: `<id>` (Light가 부착된 GO의 InstanceID)
- **응답**: `{ "type": "Directional|Point|Spot", "color": "r,g,b,a", "intensity": float, "range": float, "spotAngle": float, "spotOuterAngle": float, "shadows": bool, "shadowResolution": int, "shadowBias": float, "enabled": bool }`
- **API**:
  - `go.GetComponent<Light>()` -- Light 컴포넌트 조회.
  - `Light.type` (LightType enum: Directional=0, Point=1, Spot=2)
  - `Light.color` (Color), `Light.intensity` (float), `Light.range` (float)
  - `Light.spotAngle`, `Light.spotOuterAngle` (float, Spot 전용)
  - `Light.shadows` (bool), `Light.shadowResolution` (int)
  - `Light.enabled` (bool)

---

##### 6. `light.set_color` -- 라이트 색상 변경 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// light.set_color -- 라이트 색상 변경 (메인 스레드)
// ----------------------------------------------------------------
_handlers["light.set_color"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: light.set_color <id> <r,g,b,a>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        var light = go.GetComponent<Light>();
        if (light == null)
            return JsonError($"No Light component on GameObject: {id}");

        try
        {
            light.color = ParseColor(args[1]);
            SceneManager.GetActiveScene().isDirty = true;
            return JsonOk(new { ok = true });
        }
        catch (Exception ex)
        {
            return JsonError($"Failed to parse color: {ex.Message}");
        }
    });
};
```

- **인자**: `<id> <r,g,b,a>`
- **응답**: `{ "ok": true }`
- **API**: `Light.color` setter.

---

##### 7. `light.set_intensity` -- 라이트 강도 변경 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// light.set_intensity -- 라이트 강도 변경 (메인 스레드)
// ----------------------------------------------------------------
_handlers["light.set_intensity"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: light.set_intensity <id> <value>");

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

        light.intensity = value;
        SceneManager.GetActiveScene().isDirty = true;
        return JsonOk(new { ok = true });
    });
};
```

- **인자**: `<id> <value>` (float, 0 이상)
- **응답**: `{ "ok": true }`
- **API**: `Light.intensity` setter.

---

##### 8. `camera.info` -- 카메라 정보 조회 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// camera.info -- 카메라 정보 조회 (메인 스레드)
// ----------------------------------------------------------------
_handlers["camera.info"] = args =>
{
    return ExecuteOnMainThread(() =>
    {
        Camera? cam;
        if (args.Length >= 1 && int.TryParse(args[0], out var id))
        {
            var go = FindGameObjectById(id);
            if (go == null)
                return JsonError($"GameObject not found: {id}");
            cam = go.GetComponent<Camera>();
            if (cam == null)
                return JsonError($"No Camera component on GameObject: {id}");
        }
        else
        {
            cam = Camera.main;
            if (cam == null)
                return JsonError("No main camera found");
        }

        return JsonOk(new
        {
            id = cam.gameObject.GetInstanceID(),
            name = cam.gameObject.name,
            fov = cam.fieldOfView,
            near = cam.nearClipPlane,
            far = cam.farClipPlane,
            clearFlags = cam.clearFlags.ToString(),
            backgroundColor = FormatColor(cam.backgroundColor)
        });
    });
};
```

- **인자**: `[id]` (선택. 미지정 시 Camera.main 사용)
- **응답**: `{ "id": int, "name": "...", "fov": float, "near": float, "far": float, "clearFlags": "Skybox|SolidColor", "backgroundColor": "r,g,b,a" }`
- **API**:
  - `Camera.main` -- static property. 첫 번째 비에디터 카메라 (null 가능).
  - `Camera.fieldOfView` (float field), `Camera.nearClipPlane` (float field), `Camera.farClipPlane` (float field)
  - `Camera.clearFlags` (CameraClearFlags enum: Skybox=1, SolidColor=2)
  - `Camera.backgroundColor` (Color field)

---

##### 9. `camera.set_fov` -- FOV 설정 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// camera.set_fov -- FOV 설정 (메인 스레드)
// ----------------------------------------------------------------
_handlers["camera.set_fov"] = args =>
{
    if (args.Length < 2)
        return JsonError("Usage: camera.set_fov <id> <fov>");

    if (!int.TryParse(args[0], out var id))
        return JsonError($"Invalid GameObject ID: {args[0]}");

    if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var fov))
        return JsonError($"Invalid float value: {args[1]}");

    return ExecuteOnMainThread(() =>
    {
        var go = FindGameObjectById(id);
        if (go == null)
            return JsonError($"GameObject not found: {id}");

        var cam = go.GetComponent<Camera>();
        if (cam == null)
            return JsonError($"No Camera component on GameObject: {id}");

        cam.fieldOfView = fov;
        SceneManager.GetActiveScene().isDirty = true;
        return JsonOk(new { ok = true });
    });
};
```

- **인자**: `<id> <fov>` (float, 각도)
- **응답**: `{ "ok": true }`
- **API**: `Camera.fieldOfView` (public float field, 직접 할당).

---

##### 10. `render.info` -- 렌더 설정 조회 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// render.info -- 현재 렌더 설정 조회 (메인 스레드)
// ----------------------------------------------------------------
_handlers["render.info"] = args => ExecuteOnMainThread(() =>
{
    return JsonOk(new
    {
        ambientColor = FormatColor(RenderSettings.ambientLight),
        ambientIntensity = RenderSettings.ambientIntensity,
        skyboxExposure = RenderSettings.skyboxExposure,
        skyboxRotation = RenderSettings.skyboxRotation,
        hasSkybox = RenderSettings.skybox != null,
        skyboxTextureGuid = RenderSettings.skyboxTextureGuid ?? "",
        fsrEnabled = RenderSettings.fsrEnabled,
        fsrScaleMode = RenderSettings.fsrScaleMode.ToString(),
        ssilEnabled = RenderSettings.ssilEnabled
    });
});
```

- **인자**: 없음
- **응답**: `{ "ambientColor": "r,g,b,a", "ambientIntensity": float, "skyboxExposure": float, "skyboxRotation": float, "hasSkybox": bool, "skyboxTextureGuid": "...", "fsrEnabled": bool, "fsrScaleMode": "...", "ssilEnabled": bool }`
- **API**:
  - `RenderSettings.ambientLight` (static Color property)
  - `RenderSettings.ambientIntensity` (static float)
  - `RenderSettings.skyboxExposure` (static float)
  - `RenderSettings.skyboxRotation` (static float)
  - `RenderSettings.skybox` (static Material? -- null이면 스카이박스 없음)
  - `RenderSettings.skyboxTextureGuid` (static string?)
  - `RenderSettings.fsrEnabled`, `RenderSettings.fsrScaleMode`, `RenderSettings.ssilEnabled`

---

##### 11. `render.set_ambient` -- 앰비언트 색상 변경 (메인 스레드)

```csharp
// ----------------------------------------------------------------
// render.set_ambient -- 앰비언트 색상 변경 (메인 스레드)
// ----------------------------------------------------------------
_handlers["render.set_ambient"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: render.set_ambient <r,g,b>");

    return ExecuteOnMainThread(() =>
    {
        try
        {
            var parts = args[0].Trim('(', ')', ' ').Split(',');
            float r = float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
            float g = float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
            float b = float.Parse(parts[2].Trim(), CultureInfo.InvariantCulture);
            float a = parts.Length > 3 ? float.Parse(parts[3].Trim(), CultureInfo.InvariantCulture) : 1f;
            RenderSettings.ambientLight = new Color(r, g, b, a);
            SceneManager.GetActiveScene().isDirty = true;
            return JsonOk(new { ok = true });
        }
        catch (Exception ex)
        {
            return JsonError($"Failed to parse color: {ex.Message}");
        }
    });
};
```

- **인자**: `<r,g,b>` (또는 `<r,g,b,a>`)
- **응답**: `{ "ok": true }`
- **API**: `RenderSettings.ambientLight` setter.
- **참고**: `ParseColor()`를 사용하지 않고 직접 파싱하는 이유는 `render.set_ambient`가 RGB 3채널만 받을 수 있기 때문. 하지만 RGBA도 허용한다.

---

#### 전체 변경 요약

1. `RegisterHandlers()` 메서드 끝에 11개 핸들러를 추가한다.
2. 헬퍼 메서드 영역에 `FormatColor()` 1개 메서드를 추가한다.
3. using 추가 불필요 (이미 모두 있음).

## NuGet 패키지
- 없음

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] `material.info <id>` -> 머티리얼 정보 (color, metallic, roughness 등)
- [ ] `material.set_color <id> 1,0,0,1` -> 빨간색으로 변경
- [ ] `material.set_metallic <id> 0.8` -> metallic 변경
- [ ] `material.set_roughness <id> 0.3` -> roughness 변경
- [ ] `light.info <id>` -> 라이트 정보 (type, color, intensity 등)
- [ ] `light.set_color <id> 1,1,0.5,1` -> 라이트 색상 변경
- [ ] `light.set_intensity <id> 2.5` -> 라이트 강도 변경
- [ ] `camera.info` -> 메인 카메라 정보 (fov, near, far 등)
- [ ] `camera.info <id>` -> 특정 카메라 정보
- [ ] `camera.set_fov <id> 90` -> FOV 변경
- [ ] `render.info` -> 렌더 설정 조회 (ambient, skybox 등)
- [ ] `render.set_ambient 0.3,0.3,0.3` -> 앰비언트 색상 변경

## 참고
- 모든 렌더링 관련 클래스(Material, Light, Camera, RenderSettings, MeshRenderer)는 `RoseEngine` 네임스페이스에 있다.
- `Camera.fieldOfView`, `Camera.nearClipPlane`, `Camera.farClipPlane`는 public field (property가 아님)이므로 직접 할당한다.
- `Light` 컴포넌트의 `type` 프로퍼티는 `LightType` enum이며, `ToString()`으로 "Directional"/"Point"/"Spot" 문자열을 얻는다.
- `RenderSettings`는 static 클래스이므로 인스턴스 없이 직접 접근한다.
- `Material`의 PBR 프로퍼티(metallic, roughness, occlusion)는 0.0~1.0 범위이다.
