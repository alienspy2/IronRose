# Phase 44: TomlConfig 래퍼 클래스 -- 상세 구현 명세서

이 문서는 `plans/phase44_toml-config-wrapper.md` 설계를 기반으로, aca-coder-csharp가 바로 구현에 착수할 수 있도록 phase별로 분할한 상세 구현 명세서이다.

---

# Phase 44A: 래퍼 클래스 작성 (TomlConfig.cs, TomlConvert.cs)

## 목표
- `TomlConfig`, `TomlConfigArray`, `TomlConvert` 클래스를 신규 작성한다.
- 기존 코드 변경 없이 빌드 성공을 확인한다.

## 선행 조건
- 없음 (첫 번째 phase)

## 생성할 파일

### `src/IronRose.Engine/TomlConfig.cs`

- **역할**: Tomlyn의 `TomlTable`을 감싸는 래퍼 클래스. 타입 안전한 Get/Set API와 파일 I/O를 제공한다.
- **네임스페이스**: `IronRose.Engine`
- **인코딩**: UTF-8 with BOM
- **클래스**: `TomlConfig`, `TomlConfigArray`

#### `TomlConfig` 클래스

- **접근 제한자**: `public class TomlConfig`
- **필드**: `private readonly TomlTable _table;`
- **생성자**: `private TomlConfig(TomlTable table)` -- 외부 생성 불가, 팩토리 메서드 사용

**정적 팩토리 메서드**:

```csharp
/// <summary>파일에서 TOML을 로드한다. 파일이 없거나 파싱 실패 시 null 반환.</summary>
public static TomlConfig? LoadFile(string filePath, string? logTag = null)
```
- `File.Exists(filePath)` 확인. 없으면 `logTag`가 있을 때 `EditorDebug.Log($"{logTag} File not found: {filePath}")` 출력 후 `null` 반환.
- `File.ReadAllText(filePath)` + `Toml.ToModel(text)`로 파싱.
- 파싱 실패 시 `logTag`가 있을 때 `EditorDebug.LogWarning($"{logTag} Failed to parse {filePath}: {ex.Message}")` 출력 후 `null` 반환.
- 성공 시 `new TomlConfig(table)` 반환.

```csharp
/// <summary>TOML 문자열에서 로드한다. 파싱 실패 시 null 반환.</summary>
public static TomlConfig? LoadString(string tomlString, string? logTag = null)
```
- `Toml.ToModel(tomlString)`으로 파싱. 실패 시 `logTag`가 있으면 경고 로그 후 `null`.

```csharp
/// <summary>빈 TomlConfig를 생성한다.</summary>
public static TomlConfig CreateEmpty()
```
- `new TomlConfig(new TomlTable())` 반환.

**저장 메서드**:

```csharp
/// <summary>TOML 파일로 저장. 디렉토리 자동 생성. 성공 여부 반환.</summary>
public bool SaveToFile(string filePath, string? logTag = null)
```
- `Path.GetDirectoryName(filePath)`로 디렉토리 확인, `Directory.CreateDirectory()` 호출.
- `File.WriteAllText(filePath, Toml.FromModel(_table))`.
- 실패 시 `logTag`가 있으면 경고 로그 후 `false` 반환.

```csharp
/// <summary>TOML 문자열로 변환.</summary>
public string ToTomlString()
```
- `Toml.FromModel(_table)` 반환.

**값 읽기 메서드** (모두 키가 없거나 타입 불일치 시 기본값 반환):

```csharp
public string GetString(string key, string defaultValue = "")
```
- `_table.TryGetValue(key, out var val)` && `val is string s` 이면 `s`, 아니면 `defaultValue`.

```csharp
public int GetInt(string key, int defaultValue = 0)
```
- `val`이 `long l`이면 `(int)l`, `double d`이면 `(int)d`, 아니면 `defaultValue`.

```csharp
public long GetLong(string key, long defaultValue = 0)
```
- `val`이 `long l`이면 `l`, `double d`이면 `(long)d`, 아니면 `defaultValue`.

```csharp
public float GetFloat(string key, float defaultValue = 0f)
```
- `val switch { double d => (float)d, long l => (float)l, float f => f, _ => defaultValue }`.

```csharp
public double GetDouble(string key, double defaultValue = 0.0)
```
- `val`이 `double d`이면 `d`, `long l`이면 `(double)l`, 아니면 `defaultValue`.

```csharp
public bool GetBool(string key, bool defaultValue = false)
```
- `val is bool b` 이면 `b`, 아니면 `defaultValue`.

**중첩 구조 접근**:

```csharp
/// <summary>하위 테이블(섹션)을 TomlConfig로 래핑하여 반환. 없으면 null.</summary>
public TomlConfig? GetSection(string key)
```
- `_table.TryGetValue(key, out var val)` && `val is TomlTable t` 이면 `new TomlConfig(t)`, 아니면 `null`.
- 주의: 생성자가 `private`이므로 같은 클래스 내에서 호출 가능.

```csharp
/// <summary>테이블 배열을 TomlConfigArray로 반환. 없으면 null.</summary>
public TomlConfigArray? GetArray(string key)
```
- `val is TomlTableArray ta` 이면 `new TomlConfigArray(ta)`, 아니면 `null`.

```csharp
/// <summary>값 배열(TomlArray)을 IReadOnlyList<object>로 반환. 없으면 null.</summary>
public IReadOnlyList<object>? GetValues(string key)
```
- `val is TomlArray arr` 이면 `arr` 자체를 반환 (TomlArray는 `IList<object>`를 구현하므로 `List<object>(arr)`로 감싸거나 직접 캐스트).
- 구현 힌트: `TomlArray`는 `List<object>`를 상속하므로 그대로 `IReadOnlyList<object>`로 반환 가능. 불가능할 경우 `arr.ToList()` 사용.

**값 쓰기**:

```csharp
/// <summary>값을 설정한다. value는 string, long, double, bool, TomlTable, TomlTableArray, TomlArray.</summary>
public void SetValue(string key, object value)
```
- `_table[key] = value;`

```csharp
/// <summary>TomlConfig를 하위 섹션으로 설정한다.</summary>
public void SetSection(string key, TomlConfig section)
```
- `_table[key] = section._table;`
- 주의: `_table` 필드에 직접 접근해야 하므로 같은 클래스 내에서만 가능.

```csharp
/// <summary>TomlConfigArray를 테이블 배열로 설정한다.</summary>
public void SetArray(string key, TomlConfigArray array)
```
- `_table[key] = array.GetRawArray();`

**유틸**:

```csharp
public bool HasKey(string key) => _table.ContainsKey(key);
public bool Remove(string key) => _table.Remove(key);
public IEnumerable<string> Keys => _table.Keys;
```

```csharp
/// <summary>내부 TomlTable을 직접 반환 (점진적 마이그레이션용).</summary>
public TomlTable GetRawTable() => _table;
```

**using 문**:
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using Tomlyn;
using Tomlyn.Model;
using RoseEngine;
```

#### `TomlConfigArray` 클래스 (같은 파일에 정의)

- **접근 제한자**: `public class TomlConfigArray : IEnumerable<TomlConfig>`
- **필드**: `private readonly TomlTableArray _array;`

```csharp
internal TomlConfigArray(TomlTableArray array) { _array = array; }
public TomlConfigArray() { _array = new TomlTableArray(); }

public int Count => _array.Count;

public TomlConfig this[int index]
```
- `new TomlConfig(_array[index])` 반환.
- 주의: `TomlConfig` 생성자가 `private`이므로, 이 클래스를 `TomlConfig.cs` 파일 내에 두되 `TomlConfig` 클래스 외부에 별도 클래스로 정의한다. `TomlConfig` 생성자에 `internal` 오버로드를 추가하거나, `TomlConfigArray`를 `TomlConfig`의 nested class로 만들어야 한다.
- **권장 해결책**: `TomlConfig` 생성자를 `internal`로 변경한다. `internal TomlConfig(TomlTable table) { _table = table; }`. 팩토리 메서드는 유지. 이렇게 하면 같은 어셈블리 내에서 `new TomlConfig(table)` 호출이 가능하다.

```csharp
public void Add(TomlConfig config) => _array.Add(config.GetRawTable());

public IEnumerator<TomlConfig> GetEnumerator()
{
    for (int i = 0; i < _array.Count; i++)
        yield return new TomlConfig(_array[i]);
}

System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

/// <summary>내부 TomlTableArray 직접 접근 (마이그레이션용).</summary>
public TomlTableArray GetRawArray() => _array;
```

**using 문** (TomlConfigArray):
```csharp
using System.Collections;
using System.Collections.Generic;
using Tomlyn.Model;
```

---

### `src/IronRose.Engine/TomlConvert.cs`

- **역할**: TOML 값 타입 변환 유틸리티. Tomlyn 타입과 엔진 타입(Vector2/3/4, Quaternion, Color) 간 변환을 담당한다.
- **네임스페이스**: `IronRose.Engine`
- **인코딩**: UTF-8 with BOM
- **클래스**: `public static class TomlConvert`

**기본 타입 변환**:

```csharp
/// <summary>object를 float로 변환. double, long, float, int, string을 처리.</summary>
public static float ToFloat(object? val, float defaultValue = 0f)
```
- 구현:
```csharp
return val switch
{
    double d => (float)d,
    long l => (float)l,
    float f => f,
    int i => i,
    string s => float.TryParse(s, System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : defaultValue,
    _ => defaultValue,
};
```
- 주의: SceneSerializer의 `ToFloat`에는 `int` 케이스와 `string` 파싱이 포함되어 있다. 이를 통합하여 가장 완전한 버전을 사용한다.

```csharp
/// <summary>object를 int로 변환. long, double을 처리.</summary>
public static int ToInt(object? val, int defaultValue = 0)
```
- `val switch { long l => (int)l, double d => (int)d, int i => i, _ => defaultValue }`

**Vector/Quaternion/Color 변환** (모두 `TomlArray` 사용):

```csharp
public static TomlArray Vec2ToArray(Vector2 v)
```
- `new TomlArray { (double)v.x, (double)v.y }` 반환.

```csharp
public static Vector2 ArrayToVec2(object? val)
```
- `val is not TomlArray arr || arr.Count < 2` 이면 `Vector2.zero`.
- `new Vector2(ToFloat(arr[0]), ToFloat(arr[1]))` 반환.

```csharp
public static TomlArray Vec3ToArray(Vector3 v)
```
- `new TomlArray { (double)v.x, (double)v.y, (double)v.z }` 반환.

```csharp
public static Vector3 ArrayToVec3(object? val)
```
- `val is not TomlArray arr || arr.Count < 3` 이면 `Vector3.zero`.
- `new Vector3(ToFloat(arr[0]), ToFloat(arr[1]), ToFloat(arr[2]))` 반환.

```csharp
public static TomlArray Vec4ToArray(Vector4 v)
```
- `new TomlArray { (double)v.x, (double)v.y, (double)v.z, (double)v.w }` 반환.

```csharp
public static Vector4 ArrayToVec4(object? val)
```
- `val is not TomlArray arr || arr.Count < 4` 이면 `Vector4.zero`.

```csharp
public static TomlArray QuatToArray(Quaternion q)
```
- `new TomlArray { (double)q.x, (double)q.y, (double)q.z, (double)q.w }` 반환.

```csharp
public static Quaternion ArrayToQuat(object? val)
```
- `val is not TomlArray arr || arr.Count < 4` 이면 `Quaternion.identity`.

```csharp
public static TomlArray ColorToArray(Color c)
```
- `new TomlArray { (double)c.r, (double)c.g, (double)c.b, (double)c.a }` 반환.

```csharp
public static Color ArrayToColor(object? val)
```
- `val is not TomlArray arr || arr.Count < 4` 이면 `Color.white`.

**TomlTable 기반 편의 메서드** (SceneSerializer 패턴 호환):

```csharp
/// <summary>TomlTable에서 키로 Vector3을 읽는다. 키가 없거나 배열이 아니면 기본값.</summary>
public static Vector3 GetVec3(TomlTable table, string key, Vector3? defaultValue = null)
```
- `table.TryGetValue(key, out var val)` && `val is TomlArray arr && arr.Count >= 3` 이면 변환, 아니면 `defaultValue ?? Vector3.zero`.

```csharp
/// <summary>TomlTable에서 키로 Quaternion을 읽는다. 키가 없으면 identity.</summary>
public static Quaternion GetQuat(TomlTable table, string key)
```
- `table.TryGetValue(key, out var val)` && `val is TomlArray arr && arr.Count >= 4` 이면 변환, 아니면 `Quaternion.identity`.

**using 문**:
```csharp
using System.Globalization;
using RoseEngine;
using Tomlyn.Model;
```

**파일 헤더** (@file 주석):
```csharp
// ------------------------------------------------------------
// @file    TomlConvert.cs
// @brief   TOML 값 타입 변환 유틸리티. Tomlyn 타입과 엔진 타입 간 변환을 담당한다.
//          기존 SceneSerializer, AnimationClipImporter 등에서 중복되던
//          ToFloat, Vec3ToArray, ArrayToColor 등의 변환 로직을 통합한다.
// @deps    Tomlyn.Model, RoseEngine (Vector2/3/4, Quaternion, Color)
// @exports
//   static class TomlConvert
//     ToFloat(object?, float): float
//     ToInt(object?, int): int
//     Vec2ToArray/ArrayToVec2, Vec3ToArray/ArrayToVec3
//     Vec4ToArray/ArrayToVec4, QuatToArray/ArrayToQuat
//     ColorToArray/ArrayToColor
//     GetVec3(TomlTable, string, Vector3?): Vector3
//     GetQuat(TomlTable, string): Quaternion
// ------------------------------------------------------------
```

## 수정할 파일

없음. 이 phase에서는 기존 코드를 변경하지 않는다.

## NuGet 패키지

없음. `Tomlyn 0.20.0`은 이미 `IronRose.Engine.csproj`에 참조되어 있다.

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] `TomlConfig.cs`, `TomlConvert.cs` 두 파일이 `src/IronRose.Engine/` 에 생성됨
- [ ] 기존 코드에 영향 없음 (변경 없음)

## 참고
- `TomlConfig` 생성자는 `internal`로 설정하여 같은 어셈블리(IronRose.Engine) 내에서 `TomlConfigArray`와 다른 코드에서 사용할 수 있게 한다. 외부 어셈블리에서는 팩토리 메서드(`LoadFile`, `LoadString`, `CreateEmpty`)로만 생성 가능.
- `TomlConvert.ToFloat()`는 SceneSerializer의 것(가장 완전)을 기준으로 `string` 파싱과 `int` 케이스를 포함한다.
- `TomlConvert`의 `GetVec3`, `GetQuat` 메서드는 SceneSerializer의 `ArrayToVec3(TomlTable, string, Vector3?)`, `ArrayToQuat(TomlTable, string)` 패턴을 래핑한 것이다. Phase 44D에서 SceneSerializer의 로컬 메서드를 이 메서드로 대체한다.

---

# Phase 44B: 설정 파일 마이그레이션 (ProjectContext, RoseConfig, ProjectSettings, EditorState)

## 목표
- 4개 설정 파일 클래스의 TOML 읽기 부분을 `TomlConfig` API로 전환한다.
- 쓰기 부분: `ProjectContext.SaveLastProjectPath()`는 `TomlConfig`로 전환한다. `ProjectSettings.Save()`와 `EditorState.Save()`의 문자열 직접 조합은 유지한다.

## 선행 조건
- Phase 44A 완료 (TomlConfig.cs, TomlConvert.cs 존재)

## 수정할 파일

### `src/IronRose.Engine/ProjectContext.cs`

#### Initialize() 메서드의 읽기 부분 변경

**현재 코드** (89~103행):
```csharp
var table = Toml.ToModel(File.ReadAllText(tomlPath));
if (table.TryGetValue("project", out var projVal) && projVal is TomlTable projTable)
{
    if (projTable.TryGetValue("name", out var nameVal) && nameVal is string nameStr)
        ProjectName = nameStr;
}
var engineRelPath = "../IronRose";
if (table.TryGetValue("engine", out var engineVal) && engineVal is TomlTable engineTable)
{
    if (engineTable.TryGetValue("path", out var pathVal) && pathVal is string pathStr)
        engineRelPath = pathStr;
}
```

**변경 후**:
```csharp
var config = TomlConfig.LoadFile(tomlPath, "[ProjectContext]");
if (config == null)
{
    EngineRoot = ProjectRoot;
    IsProjectLoaded = false;
    return;
}

var project = config.GetSection("project");
if (project != null)
    ProjectName = project.GetString("name", "");

var engineRelPath = "../IronRose";
var engine = config.GetSection("engine");
if (engine != null)
    engineRelPath = engine.GetString("path", "../IronRose");
```

- try/catch 블록은 제거한다 (`TomlConfig.LoadFile` 내부에서 처리).
- catch 블록의 에러 처리 (`EngineRoot = ProjectRoot; IsProjectLoaded = false;`)는 `config == null` 체크로 대체.

#### ReadLastProjectPath() 메서드 변경

**현재 코드** (160~173행):
```csharp
var table = Toml.ToModel(File.ReadAllText(settingsPath));
if (table.TryGetValue("editor", out var editorVal) && editorVal is TomlTable editorTable)
{
    if (editorTable.TryGetValue("last_project", out var pathVal) && pathVal is string pathStr)
    {
        if (!string.IsNullOrEmpty(pathStr) && File.Exists(Path.Combine(pathStr, "project.toml")))
            return pathStr;
    }
}
```

**변경 후**:
```csharp
var config = TomlConfig.LoadFile(settingsPath, "[ProjectContext]");
if (config != null)
{
    var editor = config.GetSection("editor");
    if (editor != null)
    {
        var pathStr = editor.GetString("last_project", "");
        if (!string.IsNullOrEmpty(pathStr) && File.Exists(Path.Combine(pathStr, "project.toml")))
            return pathStr;
    }
}
```

- 기존 try/catch 제거 (`TomlConfig.LoadFile` 내부 처리).
- 하위 호환 마이그레이션 부분 (176~195행)은 TOML과 무관하므로 그대로 유지.

#### SaveLastProjectPath() 메서드 변경

**현재 코드** (206~232행): `TomlTable` 직접 사용 + `Toml.FromModel()` 쓰기.

**변경 후**:
```csharp
public static void SaveLastProjectPath(string projectPath)
{
    try
    {
        Directory.CreateDirectory(GlobalSettingsDir);
        var normalizedPath = Path.GetFullPath(projectPath).Replace("\\", "/");

        // 기존 settings.toml 로드 또는 빈 생성
        var config = TomlConfig.LoadFile(GlobalSettingsPath) ?? TomlConfig.CreateEmpty();

        // [editor] 섹션 가져오기 또는 생성
        var editor = config.GetSection("editor");
        if (editor == null)
        {
            editor = TomlConfig.CreateEmpty();
            config.SetSection("editor", editor);
        }
        editor.SetValue("last_project", normalizedPath);

        config.SaveToFile(GlobalSettingsPath, "[ProjectContext]");
        EditorDebug.Log($"[ProjectContext] Saved last project to settings: {projectPath}");
    }
    catch (Exception ex)
    {
        EditorDebug.LogWarning($"[ProjectContext] Failed to save settings: {ex.Message}");
    }
}
```

#### using 문 변경
- `using Tomlyn;` 제거
- `using Tomlyn.Model;` 제거
- `using IronRose.Engine;`는 이미 같은 네임스페이스이므로 불필요

---

### `src/IronRose.Engine/RoseConfig.cs`

#### Load() 메서드 변경

**현재 코드** (90~98행):
```csharp
var table = Toml.ToModel(File.ReadAllText(path));
if (table.TryGetValue("editor", out var editorVal) && editorVal is TomlTable editor)
{
    if (editor.TryGetValue("enable_editor", out var v4) && v4 is bool b4)
        EnableEditor = b4;
}
```

**변경 후**:
```csharp
var config = TomlConfig.LoadFile(path, "[RoseConfig]");
if (config == null) continue;

var editor = config.GetSection("editor");
if (editor != null)
    EnableEditor = editor.GetBool("enable_editor", EnableEditor);

EditorDebug.Log($"[RoseConfig] Loaded: {path} (EnableEditor={EnableEditor})");
return;
```

- try/catch 블록 제거 (LoadFile 내부 처리). `catch` 블록의 경고 로그는 `LoadFile`의 `logTag`가 처리.
- `continue`로 다음 경로 시도 (null 반환 시).

#### using 문 변경
- `using Tomlyn;` 제거
- `using Tomlyn.Model;` 제거

---

### `src/IronRose.Engine/ProjectSettings.cs`

#### Load() 메서드의 읽기 부분 변경

**현재 코드** (71~98행): `Toml.ToModel` + 중첩 `TryGetValue` 체인.

**변경 후**:
```csharp
var config = TomlConfig.LoadFile(path, "[ProjectSettings]");
if (config != null)
{
    var renderer = config.GetSection("renderer");
    if (renderer != null)
    {
        var sg = renderer.GetString("active_profile_guid", "");
        if (!string.IsNullOrEmpty(sg))
            ActiveRendererProfileGuid = sg;
    }

    var build = config.GetSection("build");
    if (build != null)
    {
        var ss = build.GetString("start_scene", "");
        if (!string.IsNullOrEmpty(ss))
            StartScenePath = ss;
    }

    var editor = config.GetSection("editor");
    if (editor != null)
    {
        var se = editor.GetString("external_script_editor", "");
        if (!string.IsNullOrEmpty(se))
            ExternalScriptEditor = se;
    }

    var cache = config.GetSection("cache");
    if (cache != null)
    {
        DontUseCache = cache.GetBool("dont_use_cache", DontUseCache);
        DontUseCompressTexture = cache.GetBool("dont_use_compress_texture", DontUseCompressTexture);
        ForceClearCache = cache.GetBool("force_clear_cache", ForceClearCache);
    }

    EditorDebug.Log($"[ProjectSettings] Loaded: {path}");
}
```

- try/catch 블록은 제거 (LoadFile 내부 처리).
- `Save()` 메서드는 **변경하지 않는다** (문자열 직접 조합 유지).

#### using 문 변경
- `using Tomlyn;` 제거
- `using Tomlyn.Model;` 제거 (Save()에서도 사용하지 않으므로)

---

### `src/IronRose.Engine/Editor/EditorState.cs`

#### Load() 메서드의 읽기 부분 변경

**현재 코드** (136~188행): `Toml.ToModel` + 5개 섹션의 중첩 `TryGetValue` 체인.

**변경 후**:
```csharp
public static void Load()
{
    var path = FindOrCreatePath();
    if (!File.Exists(path)) return;

    var config = TomlConfig.LoadFile(path, "[EditorState]");
    if (config == null) return;

    var editor = config.GetSection("editor");
    if (editor != null)
    {
        var s = editor.GetString("last_scene", "");
        if (!string.IsNullOrEmpty(s))
            LastScenePath = ToAbsolute(s);
        UiScale = Math.Clamp(editor.GetFloat("ui_scale", UiScale), 0.5f, 3.0f);
        var sf = editor.GetString("editor_font", "");
        if (!string.IsNullOrEmpty(sf))
            EditorFont = sf;
        var sr = editor.GetString("scene_view_render_style", "");
        if (!string.IsNullOrEmpty(sr))
            SceneViewRenderStyle = sr;
        var sarp = editor.GetString("active_renderer_profile_guid", "");
        if (!string.IsNullOrEmpty(sarp))
            ActiveRendererProfileGuid = sarp;
    }

    var snap = config.GetSection("snap");
    if (snap != null)
    {
        SnapTranslate = Math.Max(snap.GetFloat("translate", SnapTranslate), 0.001f);
        SnapRotate = Math.Max(snap.GetFloat("rotate", SnapRotate), 0.001f);
        SnapScale = Math.Max(snap.GetFloat("scale", SnapScale), 0.001f);
        SnapGrid2D = Math.Max(snap.GetFloat("grid_2d", SnapGrid2D), 0.001f);
    }

    var window = config.GetSection("window");
    if (window != null)
    {
        if (window.HasKey("x")) WindowX = window.GetInt("x");
        if (window.HasKey("y")) WindowY = window.GetInt("y");
        if (window.HasKey("w")) WindowW = window.GetInt("w");
        if (window.HasKey("h")) WindowH = window.GetInt("h");
    }

    var panels = config.GetSection("panels");
    if (panels != null)
    {
        PanelHierarchy = panels.GetBool("hierarchy", PanelHierarchy);
        PanelInspector = panels.GetBool("inspector", PanelInspector);
        PanelSceneEnvironment = panels.GetBool("scene_environment", PanelSceneEnvironment);
        PanelConsole = panels.GetBool("console", PanelConsole);
        PanelGameView = panels.GetBool("game_view", PanelGameView);
        PanelSceneView = panels.GetBool("scene_view", PanelSceneView);
        PanelProject = panels.GetBool("project", PanelProject);
        PanelTextureTool = panels.GetBool("texture_tool", PanelTextureTool);
        PanelProjectSettings = panels.GetBool("project_settings", PanelProjectSettings);
    }

    var layout = config.GetSection("imgui_layout");
    if (layout != null)
    {
        var sd = layout.GetString("data", "");
        if (!string.IsNullOrEmpty(sd))
            ImGuiLayoutData = sd;
    }

    var winInfo = WindowX.HasValue ? $"{WindowX},{WindowY} {WindowW}x{WindowH}" : "default";
    Debug.Log($"[EditorState] Loaded: last_scene={LastScenePath ?? "(none)"}, window={winInfo}");
}
```

- try/catch 블록 제거 (LoadFile 내부 처리). 단, 기존 catch 블록의 `Debug.LogWarning`은 `LoadFile`의 `logTag`가 처리.
- `Save()` 메서드는 **변경하지 않는다** (문자열 직접 조합 유지).

#### using 문 변경
- `using Tomlyn;` 제거
- `using Tomlyn.Model;` 제거 (Save()에서도 사용하지 않으므로)
- `using IronRose.Engine;` 추가 필요 (`TomlConfig` 사용을 위해, 네임스페이스가 `IronRose.Engine.Editor`이므로)

---

## NuGet 패키지

없음.

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] `ProjectContext.cs`에서 `using Tomlyn;` 및 `using Tomlyn.Model;` 제거됨
- [ ] `RoseConfig.cs`에서 `using Tomlyn;` 및 `using Tomlyn.Model;` 제거됨
- [ ] `ProjectSettings.cs`에서 `using Tomlyn;` 및 `using Tomlyn.Model;` 제거됨
- [ ] `EditorState.cs`에서 `using Tomlyn;` 및 `using Tomlyn.Model;` 제거됨
- [ ] 각 파일의 Load()/읽기 로직이 `TomlConfig` API를 사용
- [ ] Save() 메서드는 기존 동작과 동일 (문자열 직접 조합)
- [ ] 앱 실행 시 project.toml, settings.toml, EditorState, ProjectSettings 파일이 정상적으로 읽힘

## 참고
- `EditorState.Save()`와 `ProjectSettings.Save()`의 문자열 직접 조합 패턴은 의도적으로 유지한다. 설계 문서에서 "쓰기 부분은 기존 문자열 조합을 유지"로 결정되었다.
- `ProjectContext.SaveLastProjectPath()`는 read-modify-write 패턴이므로 `TomlConfig` API로 전환한다.
- `EditorState`는 네임스페이스가 `IronRose.Engine.Editor`이므로 `IronRose.Engine.TomlConfig`를 사용하려면 `using IronRose.Engine;`이 필요하다.

---

# Phase 44C: 에셋 임포터 마이그레이션 (MaterialImporter, PostProcessProfileImporter, RendererProfileImporter, AnimationClipImporter, RoseMetadata)

## 목표
- 5개 에셋 임포터 파일의 TOML 읽기/쓰기를 `TomlConfig`/`TomlConvert` API로 전환한다.
- 3개 파일에서 중복 정의된 로컬 `ToFloat` 메서드를 제거하고 `TomlConvert.ToFloat()`로 대체한다.
- `RoseMetadata.importer` 필드 타입은 `TomlTable` 유지한다 (설계 문서 결정: Phase 5까지 변경 보류).

## 선행 조건
- Phase 44A 완료 (TomlConfig.cs, TomlConvert.cs 존재)

## 수정할 파일

### `src/IronRose.Engine/AssetPipeline/MaterialImporter.cs`

#### Import() 메서드 변경

**현재 코드**: `Toml.ToModel(File.ReadAllText(path))` + `TryGetValue` 체인.

**변경 후**:
```csharp
public Material Import(string path, RoseMetadata meta, IAssetDatabase? db)
{
    var config = TomlConfig.LoadString(File.ReadAllText(path), "[MaterialImporter]");
    if (config == null) return new Material { name = Path.GetFileNameWithoutExtension(path) };

    var mat = new Material();
    mat.name = Path.GetFileNameWithoutExtension(path);

    var colorSection = config.GetSection("color");
    if (colorSection != null)
        mat.color = ReadColorFromConfig(colorSection);

    var emissionSection = config.GetSection("emission");
    if (emissionSection != null)
        mat.emission = ReadColorFromConfig(emissionSection);

    mat.metallic = config.GetFloat("metallic", mat.metallic);
    mat.roughness = config.GetFloat("roughness", mat.roughness);
    mat.occlusion = config.GetFloat("occlusion", mat.occlusion);
    mat.normalMapStrength = config.GetFloat("normalMapStrength", mat.normalMapStrength);

    // Texture transform
    float sx = config.GetFloat("textureScaleX", 1f);
    float sy = config.GetFloat("textureScaleY", 1f);
    mat.textureScale = new RoseEngine.Vector2(sx, sy);
    float ox = config.GetFloat("textureOffsetX", 0f);
    float oy = config.GetFloat("textureOffsetY", 0f);
    mat.textureOffset = new RoseEngine.Vector2(ox, oy);

    // Texture references by GUID
    if (db != null)
    {
        var mtg = config.GetString("mainTextureGuid", "");
        if (!string.IsNullOrEmpty(mtg))
            mat.mainTexture = db.LoadByGuid<Texture2D>(mtg);
        var nmg = config.GetString("normalMapGuid", "");
        if (!string.IsNullOrEmpty(nmg))
            mat.normalMap = db.LoadByGuid<Texture2D>(nmg);
        var mrog = config.GetString("MROMapGuid", "");
        if (!string.IsNullOrEmpty(mrog))
            mat.MROMap = db.LoadByGuid<Texture2D>(mrog);
    }

    return mat;
}
```

#### ReadColor 메서드 변경

기존 `ReadColor(TomlTable ct)` -> `ReadColorFromConfig(TomlConfig section)`:

```csharp
private static Color ReadColorFromConfig(TomlConfig section)
{
    float r = section.GetFloat("r", 0f);
    float g = section.GetFloat("g", 0f);
    float b = section.GetFloat("b", 0f);
    float a = section.GetFloat("a", 1f);
    return new Color(r, g, b, a);
}
```

기존 `ReadColor(TomlTable ct)` 메서드는 제거한다.

#### WriteDefault(), WriteMaterial() 변경

`BuildTomlTable()` 내부에서 `new TomlTable { ... }` 패턴을 `TomlConfig.CreateEmpty()` + `SetValue()`로 변경:

```csharp
private static TomlConfig BuildConfig(Color color, Color emission,
    float metallic, float roughness, float occlusion, float normalMapStrength,
    RoseEngine.Vector2 textureScale, RoseEngine.Vector2 textureOffset,
    string? mainTexGuid, string? normalMapGuid, string? mroMapGuid)
{
    var config = TomlConfig.CreateEmpty();

    var colorSection = TomlConfig.CreateEmpty();
    colorSection.SetValue("r", (double)color.r);
    colorSection.SetValue("g", (double)color.g);
    colorSection.SetValue("b", (double)color.b);
    colorSection.SetValue("a", (double)color.a);
    config.SetSection("color", colorSection);

    var emissionSection = TomlConfig.CreateEmpty();
    emissionSection.SetValue("r", (double)emission.r);
    emissionSection.SetValue("g", (double)emission.g);
    emissionSection.SetValue("b", (double)emission.b);
    emissionSection.SetValue("a", (double)emission.a);
    config.SetSection("emission", emissionSection);

    config.SetValue("metallic", (double)metallic);
    config.SetValue("roughness", (double)roughness);
    config.SetValue("occlusion", (double)occlusion);
    config.SetValue("normalMapStrength", (double)normalMapStrength);

    if (textureScale.x != 1f || textureScale.y != 1f)
    {
        config.SetValue("textureScaleX", (double)textureScale.x);
        config.SetValue("textureScaleY", (double)textureScale.y);
    }
    if (textureOffset.x != 0f || textureOffset.y != 0f)
    {
        config.SetValue("textureOffsetX", (double)textureOffset.x);
        config.SetValue("textureOffsetY", (double)textureOffset.y);
    }

    if (!string.IsNullOrEmpty(mainTexGuid))
        config.SetValue("mainTextureGuid", mainTexGuid);
    if (!string.IsNullOrEmpty(normalMapGuid))
        config.SetValue("normalMapGuid", normalMapGuid);
    if (!string.IsNullOrEmpty(mroMapGuid))
        config.SetValue("MROMapGuid", mroMapGuid);

    return config;
}
```

`WriteDefault()`, `WriteMaterial()`:
```csharp
public static void WriteDefault(string path)
{
    var config = BuildConfig(Color.white, Color.black, 0f, 0.5f, 1f, 1f,
        RoseEngine.Vector2.one, RoseEngine.Vector2.zero, null, null, null);
    config.SaveToFile(path);
}

public static void WriteMaterial(string path, Material mat,
    string? mainTexGuid = null, string? normalMapGuid = null, string? mroMapGuid = null)
{
    var config = BuildConfig(mat.color, mat.emission,
        mat.metallic, mat.roughness, mat.occlusion, mat.normalMapStrength,
        mat.textureScale, mat.textureOffset,
        mainTexGuid, normalMapGuid, mroMapGuid);
    config.SaveToFile(path);
}
```

#### using 문 변경
- `using Tomlyn;` 제거
- `using Tomlyn.Model;` 제거
- `using IronRose.Engine;` 추가 (네임스페이스가 `IronRose.AssetPipeline`이므로)

---

### `src/IronRose.Engine/AssetPipeline/PostProcessProfileImporter.cs`

#### Import() 메서드 변경

```csharp
public PostProcessProfile? Import(string path, RoseMetadata? meta = null)
{
    if (!File.Exists(path))
    {
        EditorDebug.LogError($"[PostProcessProfileImporter] File not found: {path}");
        return null;
    }

    var config = TomlConfig.LoadFile(path, "[PostProcessProfileImporter]");
    if (config == null) return null;

    return ParseProfile(config, path);
}
```

#### ParseProfile() 변경

`TomlTable doc` 파라미터 -> `TomlConfig config`:

```csharp
private static PostProcessProfile ParseProfile(TomlConfig config, string path)
{
    var profile = new PostProcessProfile
    {
        name = Path.GetFileNameWithoutExtension(path),
    };

    foreach (var key in config.Keys)
    {
        var effectSection = config.GetSection(key);
        if (effectSection == null) continue;

        var ov = new EffectOverride { effectName = key };
        ov.enabled = effectSection.GetBool("enabled", false);

        foreach (var paramKey in effectSection.Keys)
        {
            if (paramKey == "enabled") continue;
            ov.parameters[paramKey] = effectSection.GetFloat(paramKey, 0f);
        }

        profile.effects[key] = ov;
    }

    return profile;
}
```

#### Export() 변경

```csharp
public static void Export(PostProcessProfile profile, string path)
{
    var config = TomlConfig.CreateEmpty();

    foreach (var kvp in profile.effects)
    {
        var ov = kvp.Value;
        var effectSection = TomlConfig.CreateEmpty();
        effectSection.SetValue("enabled", ov.enabled);

        foreach (var param in ov.parameters)
            effectSection.SetValue(param.Key, (double)param.Value);

        config.SetSection(kvp.Key, effectSection);
    }

    config.SaveToFile(path);
    EditorDebug.Log($"[PostProcessProfileImporter] Exported: {path}");
}
```

#### 로컬 ToFloat 제거
- `private static float ToFloat(object? val)` 메서드 삭제. 이 phase에서 더 이상 직접 호출하지 않는다.

#### using 문 변경
- `using Tomlyn;` 제거
- `using Tomlyn.Model;` 제거
- `using IronRose.Engine;` 추가

---

### `src/IronRose.Engine/AssetPipeline/RendererProfileImporter.cs`

#### Import() 메서드 변경

```csharp
public RendererProfile? Import(string path, RoseMetadata? meta = null)
{
    if (!File.Exists(path))
    {
        EditorDebug.LogError($"[RendererProfileImporter] File not found: {path}");
        return null;
    }

    var config = TomlConfig.LoadFile(path, "[RendererProfileImporter]");
    if (config == null) return null;

    return ParseProfile(config, path);
}
```

#### ParseProfile() 변경

`TomlTable doc` 파라미터 -> `TomlConfig config`:

```csharp
private static RendererProfile ParseProfile(TomlConfig config, string path)
{
    var profile = new RendererProfile
    {
        name = Path.GetFileNameWithoutExtension(path),
    };

    var fsr = config.GetSection("fsr");
    if (fsr != null)
    {
        profile.fsrEnabled = fsr.GetBool("enabled", profile.fsrEnabled);
        var scaleMode = fsr.GetString("scale_mode", "");
        if (!string.IsNullOrEmpty(scaleMode) && Enum.TryParse<FsrScaleMode>(scaleMode, true, out var mode))
            profile.fsrScaleMode = mode;
        profile.fsrCustomScale = fsr.GetFloat("custom_scale", profile.fsrCustomScale);
        profile.fsrSharpness = fsr.GetFloat("sharpness", profile.fsrSharpness);
        profile.fsrJitterScale = fsr.GetFloat("jitter_scale", profile.fsrJitterScale);
    }

    var ssil = config.GetSection("ssil");
    if (ssil != null)
    {
        profile.ssilEnabled = ssil.GetBool("enabled", profile.ssilEnabled);
        profile.ssilRadius = ssil.GetFloat("radius", profile.ssilRadius);
        profile.ssilFalloffScale = ssil.GetFloat("falloff_scale", profile.ssilFalloffScale);
        profile.ssilSliceCount = ssil.GetInt("slice_count", profile.ssilSliceCount);
        profile.ssilStepsPerSlice = ssil.GetInt("steps_per_slice", profile.ssilStepsPerSlice);
        profile.ssilAoIntensity = ssil.GetFloat("ao_intensity", profile.ssilAoIntensity);
        profile.ssilIndirectEnabled = ssil.GetBool("indirect_enabled", profile.ssilIndirectEnabled);
        profile.ssilIndirectBoost = ssil.GetFloat("indirect_boost", profile.ssilIndirectBoost);
        profile.ssilSaturationBoost = ssil.GetFloat("saturation_boost", profile.ssilSaturationBoost);
    }

    return profile;
}
```

#### Export() 변경

```csharp
public static void Export(RendererProfile profile, string path)
{
    var config = TomlConfig.CreateEmpty();

    var fsr = TomlConfig.CreateEmpty();
    fsr.SetValue("enabled", profile.fsrEnabled);
    fsr.SetValue("scale_mode", profile.fsrScaleMode.ToString());
    fsr.SetValue("custom_scale", (double)profile.fsrCustomScale);
    fsr.SetValue("sharpness", (double)profile.fsrSharpness);
    fsr.SetValue("jitter_scale", (double)profile.fsrJitterScale);
    config.SetSection("fsr", fsr);

    var ssil = TomlConfig.CreateEmpty();
    ssil.SetValue("enabled", profile.ssilEnabled);
    ssil.SetValue("radius", (double)profile.ssilRadius);
    ssil.SetValue("falloff_scale", (double)profile.ssilFalloffScale);
    ssil.SetValue("slice_count", (long)profile.ssilSliceCount);
    ssil.SetValue("steps_per_slice", (long)profile.ssilStepsPerSlice);
    ssil.SetValue("ao_intensity", (double)profile.ssilAoIntensity);
    ssil.SetValue("indirect_enabled", profile.ssilIndirectEnabled);
    ssil.SetValue("indirect_boost", (double)profile.ssilIndirectBoost);
    ssil.SetValue("saturation_boost", (double)profile.ssilSaturationBoost);
    config.SetSection("ssil", ssil);

    config.SaveToFile(path);
    EditorDebug.Log($"[RendererProfileImporter] Exported: {path}");
}
```

#### 로컬 ToFloat 제거
- `private static float ToFloat(object? val)` 메서드 삭제.

#### using 문 변경
- `using Tomlyn;` 제거
- `using Tomlyn.Model;` 제거
- `using IronRose.Engine;` 추가

---

### `src/IronRose.Engine/AssetPipeline/AnimationClipImporter.cs`

#### Import() 메서드 변경

```csharp
public AnimationClip? Import(string path, RoseMetadata? meta = null)
{
    if (!File.Exists(path))
    {
        EditorDebug.LogError($"[AnimationClipImporter] File not found: {path}");
        return null;
    }

    var config = TomlConfig.LoadFile(path, "[AnimationClipImporter]");
    if (config == null) return null;

    return ParseClip(config, path);
}
```

#### ParseClip() 변경

`TomlTable doc` -> `TomlConfig config`:

```csharp
private static AnimationClip ParseClip(TomlConfig config, string path)
{
    var clip = new AnimationClip
    {
        name = Path.GetFileNameWithoutExtension(path),
    };

    clip.frameRate = config.GetFloat("frame_rate", clip.frameRate);
    var wmStr = config.GetString("wrap_mode", "");
    if (!string.IsNullOrEmpty(wmStr) && Enum.TryParse<WrapMode>(wmStr, true, out var wm))
        clip.wrapMode = wm;
    clip.length = config.GetFloat("length", clip.length);

    // Curves
    var curvesArr = config.GetArray("curves");
    if (curvesArr != null)
    {
        foreach (var curveConfig in curvesArr)
        {
            var curvePath = curveConfig.GetString("path", "");
            if (string.IsNullOrEmpty(curvePath)) continue;

            var curve = new AnimationCurve();
            var keysArr = curveConfig.GetArray("keys");
            if (keysArr != null)
            {
                foreach (var keyConfig in keysArr)
                {
                    float time = keyConfig.GetFloat("time", 0f);
                    float value = keyConfig.GetFloat("value", 0f);
                    float inTan = keyConfig.GetFloat("in_tangent", 0f);
                    float outTan = keyConfig.GetFloat("out_tangent", 0f);
                    curve.AddKey(new Keyframe(time, value, inTan, outTan));
                }
            }
            clip.SetCurve(curvePath, curve);
        }
    }

    // Events
    var eventsArr = config.GetArray("events");
    if (eventsArr != null)
    {
        foreach (var evtConfig in eventsArr)
        {
            float time = evtConfig.GetFloat("time", 0f);
            string func = evtConfig.GetString("function", "");
            float fp = evtConfig.GetFloat("float_param", 0f);
            int ip = evtConfig.GetInt("int_param", 0);
            string? sp = evtConfig.GetString("string_param", "");
            if (string.IsNullOrEmpty(sp)) sp = null;

            clip.events.Add(new AnimationEvent(time, func)
            {
                floatParameter = fp,
                intParameter = ip,
                stringParameter = sp,
            });
        }
    }

    if (clip.length <= 0f)
        clip.RecalculateLength();

    EditorDebug.Log($"[AnimationClipImporter] Loaded: {path} ({clip.curves.Count} curves, {clip.events.Count} events, {clip.length:F2}s)");
    return clip;
}
```

#### Export() 변경

Export는 `TomlTableArray`의 중첩 빌드가 필요하므로 `TomlConfig`/`TomlConfigArray`를 사용:

```csharp
public static void Export(AnimationClip clip, string path)
{
    var config = TomlConfig.CreateEmpty();
    config.SetValue("frame_rate", (double)clip.frameRate);
    config.SetValue("wrap_mode", clip.wrapMode.ToString());
    config.SetValue("length", (double)clip.length);

    // Curves
    var curvesArr = new TomlConfigArray();
    foreach (var (curvePath, curve) in clip.curves)
    {
        var curveConfig = TomlConfig.CreateEmpty();
        curveConfig.SetValue("path", curvePath);

        var keysArr = new TomlConfigArray();
        for (int i = 0; i < curve.length; i++)
        {
            var key = curve[i];
            var keyConfig = TomlConfig.CreateEmpty();
            keyConfig.SetValue("time", (double)key.time);
            keyConfig.SetValue("value", (double)key.value);
            keyConfig.SetValue("in_tangent", (double)key.inTangent);
            keyConfig.SetValue("out_tangent", (double)key.outTangent);
            keysArr.Add(keyConfig);
        }
        curveConfig.SetArray("keys", keysArr);
        curvesArr.Add(curveConfig);
    }
    config.SetArray("curves", curvesArr);

    // Events
    if (clip.events.Count > 0)
    {
        var eventsArr = new TomlConfigArray();
        foreach (var evt in clip.events)
        {
            var evtConfig = TomlConfig.CreateEmpty();
            evtConfig.SetValue("time", (double)evt.time);
            evtConfig.SetValue("function", evt.functionName);
            evtConfig.SetValue("float_param", (double)evt.floatParameter);
            evtConfig.SetValue("int_param", (long)evt.intParameter);
            if (evt.stringParameter != null)
                evtConfig.SetValue("string_param", evt.stringParameter);
            eventsArr.Add(evtConfig);
        }
        config.SetArray("events", eventsArr);
    }

    config.SaveToFile(path);
    EditorDebug.Log($"[AnimationClipImporter] Exported: {path}");
}
```

#### 로컬 ToFloat 제거
- `private static float ToFloat(object? val)` 메서드 삭제.

#### using 문 변경
- `using Tomlyn;` 제거
- `using Tomlyn.Model;` 제거
- `using IronRose.Engine;` 추가

---

### `src/IronRose.Engine/AssetPipeline/RoseMetadata.cs`

#### 중요 결정: `importer` 필드 타입 유지
- `public TomlTable importer` 타입은 **변경하지 않는다**.
- 이유: `importer` 필드는 `AssetDatabase`, `TextureImporter`, `FontImporter`, `ImGuiInspectorPanel`, `ImGuiSpriteEditorPanel`, `EditorClipboard` 등 10+ 파일에서 `TryGetValue`로 직접 접근한다. 타입 변경 시 연쇄 수정 범위가 너무 크다.
- Phase 5에서 검토한다.

#### FromToml() 메서드 변경

`TomlTable table` 파라미터는 유지하되, 값 읽기를 간소화:

```csharp
private static RoseMetadata FromToml(TomlTable table)
{
    var config = new TomlConfig(table); // internal 생성자 사용
    var meta = new RoseMetadata();

    meta.guid = config.GetString("guid", meta.guid);
    meta.version = config.GetInt("version", meta.version);

    var labelsValues = config.GetValues("labels");
    if (labelsValues != null)
    {
        meta.labels = labelsValues
            .Where(x => x != null)
            .Select(x => x!.ToString()!)
            .ToArray();
    }

    // importer는 TomlTable 그대로 사용 (Phase 5까지 유지)
    if (table.TryGetValue("importer", out var impVal) && impVal is TomlTable impTable)
        meta.importer = impTable;

    var subArr = config.GetArray("sub_assets");
    if (subArr != null)
    {
        foreach (var subConfig in subArr)
        {
            var entry = new SubAssetEntry();
            entry.name = subConfig.GetString("name", "");
            entry.type = subConfig.GetString("type", "");
            entry.index = subConfig.GetInt("index", 0);
            entry.guid = subConfig.GetString("guid", entry.guid);
            meta.subAssets.Add(entry);
        }
    }

    return meta;
}
```

#### LoadOrCreate() 메서드 변경

```csharp
public static RoseMetadata LoadOrCreate(string assetPath)
{
    var rosePath = assetPath + ".rose";

    if (File.Exists(rosePath))
    {
        var config = TomlConfig.LoadFile(rosePath, "[RoseMetadata]");
        if (config != null)
            return FromToml(config.GetRawTable());
    }

    var meta = new RoseMetadata();
    meta.importer = InferImporter(assetPath);
    meta.Save(rosePath);
    return meta;
}
```

#### ToToml() 및 Save() 메서드 변경

`ToToml()`을 `TomlConfig` 기반으로 변경:

```csharp
private TomlConfig ToConfig()
{
    var config = TomlConfig.CreateEmpty();
    config.SetValue("guid", guid);
    config.SetValue("version", (long)version);

    if (labels != null && labels.Length > 0)
    {
        var arr = new TomlArray();
        foreach (var label in labels)
            arr.Add(label);
        config.SetValue("labels", arr);
    }

    if (importer.Count > 0)
        config.GetRawTable()["importer"] = importer; // TomlTable 직접 삽입

    if (subAssets.Count > 0)
    {
        var subArr = new TomlConfigArray();
        foreach (var sub in subAssets)
        {
            var subConfig = TomlConfig.CreateEmpty();
            subConfig.SetValue("name", sub.name);
            subConfig.SetValue("type", sub.type);
            subConfig.SetValue("index", (long)sub.index);
            subConfig.SetValue("guid", sub.guid);
            subArr.Add(subConfig);
        }
        config.SetArray("sub_assets", subArr);
    }

    return config;
}
```

`Save()` 변경:
```csharp
public void Save(string rosePath)
{
    var config = ToConfig();
    config.SaveToFile(rosePath);

    if (rosePath.EndsWith(".rose", StringComparison.OrdinalIgnoreCase))
        OnSaved?.Invoke(rosePath[..^5]);
}
```

기존 `ToToml()` 메서드는 제거하고 `ToConfig()`로 대체.

#### InferImporter()는 그대로
- `new TomlTable { ... }` 패턴을 사용하는 `InferImporter()`는 **변경하지 않는다**.
- 이유: 반환 타입이 `TomlTable`이고, `importer` 필드 타입도 `TomlTable`이므로.

#### using 문 변경
- `using Tomlyn;` 제거 (FromModel은 `ToConfig().SaveToFile()`로 대체)
- `using Tomlyn.Model;` 유지 (InferImporter가 TomlTable, TomlArray 사용)
- `using IronRose.Engine;` 추가

---

## NuGet 패키지

없음.

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] `PostProcessProfileImporter.cs`, `RendererProfileImporter.cs`, `AnimationClipImporter.cs`에서 로컬 `ToFloat` 메서드 제거됨
- [ ] `MaterialImporter.cs`에서 `using Tomlyn;` 제거됨
- [ ] `PostProcessProfileImporter.cs`에서 `using Tomlyn;` 제거됨
- [ ] `RendererProfileImporter.cs`에서 `using Tomlyn;` 제거됨
- [ ] `AnimationClipImporter.cs`에서 `using Tomlyn;` 제거됨
- [ ] `RoseMetadata.importer` 필드 타입은 `TomlTable` 유지
- [ ] 에셋 임포트/익스포트 동작이 기존과 동일

## 참고
- `RoseMetadata.cs`에서 `using Tomlyn.Model;`은 `InferImporter()`의 `TomlTable`, `TomlArray` 사용 때문에 유지해야 한다. `using Tomlyn;`만 제거 가능.
- `MaterialImporter`의 색상은 `[color]`, `[emission]` 서브테이블로 표현된다. `TomlConvert.ColorToArray()`와 다른 형태이므로 `ReadColorFromConfig()` 헬퍼를 별도 유지한다.
- `AnimationClipImporter.Export()`의 `string_param`이 `null`인 경우 `SetValue`를 호출하지 않도록 주의한다.

---

# Phase 44D: SceneSerializer + 기타 마이그레이션

## 목표
- `SceneSerializer`의 벡터/색상 변환 메서드를 `TomlConvert` 호출로 대체한다.
- `SceneSerializer.Load()`, `LoadFromString()`, `LoadPrefabGameObjectsFromString()`, `GetBasePrefabGuid()`의 파싱 부분을 `TomlConfig`로 전환한다.
- `AssetDatabase`의 프리팹 의존성 스캔 부분을 `TomlConfig`로 전환한다.
- `PrefabImporter`의 `LoadPrefabInternal()`을 `TomlConfig`로 전환한다.

## 선행 조건
- Phase 44A 완료 (TomlConfig.cs, TomlConvert.cs 존재)

## 수정할 파일

### `src/IronRose.Engine/Editor/SceneSerializer.cs`

#### 변환 메서드를 `TomlConvert` 호출로 대체

다음 로컬 메서드를 삭제하고 모든 호출처를 `TomlConvert.Xxx()`로 변경:

| 삭제할 메서드 | 대체 호출 |
|---|---|
| `Vec2ToArray(Vector2 v)` | `TomlConvert.Vec2ToArray(v)` |
| `Vec3ToArray(Vector3 v)` | `TomlConvert.Vec3ToArray(v)` |
| `Vec4ToArray(Vector4 v)` | `TomlConvert.Vec4ToArray(v)` |
| `QuatToArray(Quaternion q)` | `TomlConvert.QuatToArray(q)` |
| `ColorToArray(Color c)` | `TomlConvert.ColorToArray(c)` |
| `ArrayToVec2(object? val)` | `TomlConvert.ArrayToVec2(val)` |
| `ArrayToVec3Direct(object? val)` | `TomlConvert.ArrayToVec3(val)` |
| `ArrayToVec3(TomlTable, string, Vector3?)` | `TomlConvert.GetVec3(table, key, defaultValue)` |
| `ArrayToVec4(object? val)` | `TomlConvert.ArrayToVec4(val)` |
| `ArrayToQuat(TomlTable, string)` | `TomlConvert.GetQuat(table, key)` |
| `ArrayToQuatDirect(object? val)` | `TomlConvert.ArrayToQuat(val)` |
| `ArrayToColor(object? val)` | `TomlConvert.ArrayToColor(val)` |
| `ToFloat(object? val)` | `TomlConvert.ToFloat(val)` |

**주의사항**:
- `ArrayToVec3Direct(val)` 호출은 `TomlConvert.ArrayToVec3(val)`로 변경.
- `ArrayToVec3(table, key, defaultVal)` 호출은 `TomlConvert.GetVec3(table, key, defaultVal)`로 변경.
- `ArrayToQuat(table, key)` 호출은 `TomlConvert.GetQuat(table, key)`로 변경.
- `ArrayToQuatDirect(val)` 호출은 `TomlConvert.ArrayToQuat(val)`로 변경.
- `IsChildOfPrefabInstance()` 메서드는 변환 메서드 사이에 위치하지만 삭제 대상이 아님 -- 유지한다.
- `DeserializeFieldValue()` 내부의 호출도 모두 대체.
- `ValueToToml()` 내부의 호출도 모두 대체.

**파일 내 호출 위치 목록** (검색 결과 기반):
- 101~103행: `Vec3ToArray`, `QuatToArray`
- 114~118행: `Vec2ToArray`
- 161~166행: `ColorToArray`
- 175행: `TomlArray` (expandedGuids -- 변환 메서드 아님, 유지)
- 189~191행: `Vec3ToArray`, `QuatToArray`
- 217행: `ColorToArray`
- 301행: `ColorToArray`
- 592, 600, 609행: `Vec3ToArray`, `QuatToArray`
- 792~798행: `ArrayToVec3Direct`, `ArrayToQuatDirect`
- 872~874행: `Vec3ToArray`, `QuatToArray`
- 947~949행: `ArrayToVec3`, `ArrayToQuat`
- 978~980행: `ArrayToVec3`, `ArrayToQuat`
- 996~1000행: `ArrayToVec2`
- 1140행: `TomlArray` (유지)
- 1151~1155행: `ArrayToVec3Direct`, `ArrayToQuatDirect`
- 1187행: `ArrayToColor`
- 1293행: `ArrayToColor`
- 1523, 1540행: `TomlArray` (유지)
- 1606~1610행: `Vec2ToArray`, `Vec3ToArray`, `QuatToArray`, `ColorToArray`, `Vec4ToArray`
- 1865, 1883행: `TomlArray` (유지)
- 1912~1917행: `ArrayToVec2`, `ArrayToVec3Direct`, `ArrayToColor`, `ArrayToVec4`
- 2148~2152행: `ArrayToVec2`, `ArrayToVec3Direct`, `ArrayToQuatDirect`, `ArrayToColor`, `ArrayToVec4`
- 2171~2182행: `ToFloat` (삭제 대상)
- 2220, 2230, 2232행: `ArrayToColor`

#### Load(), LoadFromString() 파싱 부분 TomlConfig 사용

**Load() 변경**:
```csharp
public static void Load(string filePath)
{
    if (!File.Exists(filePath))
    {
        EditorDebug.LogError($"[Scene] File not found: {filePath}");
        return;
    }

    var config = TomlConfig.LoadFile(filePath, "[Scene]");
    if (config == null) return;

    LoadFromTable(config.GetRawTable(), filePath);
    EditorDebug.Log($"[Scene] Loaded: {filePath}");
}
```

**LoadFromString() 변경**:
```csharp
public static void LoadFromString(string tomlStr)
{
    var config = TomlConfig.LoadString(tomlStr, "[Scene]");
    if (config == null) return;

    LoadFromTable(config.GetRawTable(), null);
}
```

#### LoadPrefabGameObjectsFromString() 변경

```csharp
public static List<GameObject> LoadPrefabGameObjectsFromString(string tomlStr)
{
    var config = TomlConfig.LoadString(tomlStr, "[Prefab]");
    if (config == null) return new List<GameObject>();

    var root = config.GetRawTable();
    if (!root.TryGetValue("gameObjects", out var gosVal) || gosVal is not TomlTableArray goArray)
        return new List<GameObject>();

    var created = DeserializeGameObjectHierarchy(goArray);
    foreach (var go in created)
        SetEditorInternalRecursive(go, true);
    FlushShortNameWarnings();
    return created;
}
```

#### GetBasePrefabGuid() 변경

```csharp
public static string? GetBasePrefabGuid(string tomlStr)
{
    var config = TomlConfig.LoadString(tomlStr);
    if (config == null) return null;

    var prefabSection = config.GetSection("prefab");
    if (prefabSection == null) return null;

    var bg = prefabSection.GetString("basePrefabGuid", "");
    return string.IsNullOrEmpty(bg) ? null : bg;
}
```

#### using 문 변경
- `using Tomlyn;` 제거
- `using Tomlyn.Model;` 유지 (LoadFromTable, SerializeComponent 등에서 TomlTable, TomlTableArray 직접 사용이 남아 있음)
- `using IronRose.Engine;` 추가 (네임스페이스가 `IronRose.Engine.Editor`이므로)

---

### `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs`

#### 프리팹 의존성 스캔 부분 변경 (약 949~1000행)

**현재 코드**:
```csharp
var tomlStr = File.ReadAllText(prefabPath);
TomlTable root = Toml.ToModel(tomlStr);

if (root.TryGetValue("prefab", out var pVal) && pVal is TomlTable prefabTable)
{
    if (prefabTable.TryGetValue("basePrefabGuid", out var bgVal)
        && bgVal is string bg && !string.IsNullOrEmpty(bg))
    {
        oldDeps.Add(bg);
    }
}

if (root.TryGetValue("gameObjects", out var goVal) && goVal is TomlTableArray goArray)
{
    foreach (TomlTable goTable in goArray)
    {
        if (goTable.TryGetValue("prefabInstance", out var piObj) && piObj is TomlTable piTable)
        {
            if (piTable.TryGetValue("prefabGuid", out var pgVal)
                && pgVal is string pg && !string.IsNullOrEmpty(pg))
            {
                oldDeps.Add(pg);
            }
        }
    }
}
```

**변경 후**:
```csharp
var config = TomlConfig.LoadFile(prefabPath, "[AssetDatabase]");
if (config == null) return;

// 1. Variant의 basePrefabGuid
var prefabSection = config.GetSection("prefab");
if (prefabSection != null)
{
    var bg = prefabSection.GetString("basePrefabGuid", "");
    if (!string.IsNullOrEmpty(bg))
        oldDeps.Add(bg);
}

// 2. Nested prefab의 prefabGuid (gameObjects 배열)
var goArray = config.GetArray("gameObjects");
if (goArray != null)
{
    foreach (var goConfig in goArray)
    {
        var piSection = goConfig.GetSection("prefabInstance");
        if (piSection != null)
        {
            var pg = piSection.GetString("prefabGuid", "");
            if (!string.IsNullOrEmpty(pg))
                oldDeps.Add(pg);
        }
    }
}
```

- try/catch 블록은 유지한다 (다른 코드 경로의 예외도 잡아야 함).
- 기존 `File.ReadAllText` + `Toml.ToModel` 부분만 `TomlConfig.LoadFile`으로 대체.

#### using 문 변경
- `using Tomlyn;` 제거 가능 여부 확인: `AssetDatabase`의 다른 부분에서 `Toml.` 사용이 없다면 제거. 스프라이트 슬라이스 부분(1077행)에서 `Tomlyn.Model.TomlTable`을 사용하므로 `using Tomlyn.Model;`은 유지해야 한다.
- `using Tomlyn;` 제거 (직접 호출 없음 확인 필요).
- `using IronRose.Engine;` 추가

---

### `src/IronRose.Engine/AssetPipeline/PrefabImporter.cs`

#### LoadPrefabInternal() 변경

**현재 코드** (63~70행):
```csharp
var tomlStr = File.ReadAllText(prefabPath);
TomlTable root;
try { root = Toml.ToModel(tomlStr); }
catch (Exception ex) { ... }

string? basePrefabGuid = null;
if (root.TryGetValue("prefab", out var pVal) && pVal is TomlTable prefabTable)
{
    if (prefabTable.TryGetValue("basePrefabGuid", out var bgVal) && bgVal is string bg && !string.IsNullOrEmpty(bg))
        basePrefabGuid = bg;
}
```

**변경 후**:
```csharp
var config = TomlConfig.LoadFile(prefabPath, "[PrefabImporter]");
if (config == null) return null;

// Variant 감지
string? basePrefabGuid = null;
var prefabSection = config.GetSection("prefab");
if (prefabSection != null)
{
    var bg = prefabSection.GetString("basePrefabGuid", "");
    if (!string.IsNullOrEmpty(bg))
        basePrefabGuid = bg;
}
```

#### LoadBase() 변경

기존: `Toml.FromModel(root)`로 다시 문자열로 만들어 `SceneSerializer.LoadPrefabGameObjectsFromString`에 전달.

**주의**: `LoadBase`는 `TomlTable root`를 받아서 `Toml.FromModel(root)`로 문자열 변환 후 전달한다. 이 패턴은 비효율적이지만 `SceneSerializer`의 API가 문자열을 받으므로 유지해야 한다.

```csharp
private GameObject? LoadBase(TomlConfig config, string prefabPath)
{
    var gameObjects = SceneSerializer.LoadPrefabGameObjectsFromString(config.ToTomlString());
    if (gameObjects.Count == 0)
    {
        EditorDebug.LogWarning($"[PrefabImporter] No GameObjects in prefab: {prefabPath}");
        return null;
    }

    var rootGo = gameObjects[0];
    EditorDebug.Log($"[PrefabImporter] Loaded prefab: {prefabPath} -> '{rootGo.name}' ({gameObjects.Count} GOs)");
    return rootGo;
}
```

기존 시그니처 `LoadBase(TomlTable root, string prefabPath)` -> `LoadBase(TomlConfig config, string prefabPath)`.

#### LoadVariant() 변경

시그니처: `LoadVariant(TomlTable root, ...)` -> `LoadVariant(TomlConfig config, ...)`:

```csharp
private GameObject? LoadVariant(TomlConfig config, string basePrefabGuid, string variantPath, int depth)
{
    var basePath = _assetDatabase.GetPathFromGuid(basePrefabGuid);
    if (string.IsNullOrEmpty(basePath))
    {
        EditorDebug.LogWarning($"[PrefabImporter] Base prefab not found for guid: {basePrefabGuid}");
        return LoadBase(config, variantPath);
    }

    var baseRoot = LoadPrefabInternal(basePath!, depth + 1);
    if (baseRoot == null)
    {
        EditorDebug.LogWarning($"[PrefabImporter] Failed to load base prefab: {basePath}");
        return LoadBase(config, variantPath);
    }

    var allGOs = new List<GameObject>();
    CollectHierarchy(baseRoot, allGOs);

    // 오버라이드 적용 -- GetRawTable() 사용 (SceneSerializer.ApplyOverrides가 TomlTableArray를 받으므로)
    var rawTable = config.GetRawTable();
    if (rawTable.TryGetValue("overrides", out var ovVal) && ovVal is TomlTableArray overrides)
    {
        SceneSerializer.ApplyOverrides(allGOs, overrides);
    }

    // Variant 이름 적용
    var prefabSection = config.GetSection("prefab");
    if (prefabSection != null)
    {
        var rn = prefabSection.GetString("rootName", "");
        if (!string.IsNullOrEmpty(rn))
            baseRoot.name = rn;
    }

    EditorDebug.Log($"[PrefabImporter] Loaded variant: {variantPath} (base: {basePath})");
    return baseRoot;
}
```

#### LoadPrefabInternal() 호출 부분 업데이트

`LoadVariant`와 `LoadBase` 호출 시 `root` (TomlTable) 대신 `config` (TomlConfig) 전달:

```csharp
if (basePrefabGuid != null)
    return LoadVariant(config, basePrefabGuid, prefabPath, depth);

return LoadBase(config, prefabPath);
```

#### using 문 변경
- `using Tomlyn;` 제거
- `using Tomlyn.Model;` 유지 (`TomlTableArray` 참조, `SceneSerializer.ApplyOverrides`에 전달)
- `using IronRose.Engine;` 추가

---

## NuGet 패키지

없음.

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] SceneSerializer에서 로컬 `Vec3ToArray`, `ArrayToVec3`, `ToFloat` 등 13개 변환 메서드 삭제됨
- [ ] SceneSerializer에서 `using Tomlyn;` 제거됨
- [ ] AssetDatabase에서 `using Tomlyn;` 제거됨
- [ ] PrefabImporter에서 `using Tomlyn;` 제거됨
- [ ] 씬 로드/저장 동작이 기존과 동일
- [ ] 프리팹 로드 동작이 기존과 동일

## 참고
- SceneSerializer는 2235행의 대형 파일이다. 변환 메서드 대체 시 `replace_all` 사용을 권장한다.
- SceneSerializer 내부에서 `TomlTable`, `TomlTableArray`, `TomlArray`를 직접 사용하는 직렬화/역직렬화 코드(SerializeComponent, DeserializeComponent 등)는 이 phase에서 변경하지 않는다. Phase 5 범위이다.
- `PrefabImporter.LoadBase()`에서 `config.ToTomlString()`으로 재직렬화하는 것은 기존 `Toml.FromModel(root)` 패턴과 동일하다. 비효율적이지만 SceneSerializer API가 문자열 기반이므로 유지한다.

---

# Phase 44E: 에디터 내부 데이터 컨테이너 (선택적)

## 목표
- 에디터 내부에서 `TomlTable`/`TomlTableArray`를 데이터 컨테이너로 사용하는 6개 파일을 `TomlConfig`/`TomlConfigArray`로 전환한다.
- SceneSerializer의 직렬화 메서드 반환 타입 변경 영향을 분석하고, 가능한 범위에서 마이그레이션한다.

## 선행 조건
- Phase 44D 완료
- 주의: 이 phase는 설계 문서에서 "선택적"으로 표시되어 있다. Phase 44A~D가 안정화된 후 수행한다.

## 영향 분석

### 핵심 의존성 체인
SceneSerializer의 다음 메서드가 `TomlTable`/`TomlTableArray`를 반환하며, Undo 액션과 클립보드가 이를 직접 사용한다:

1. `SceneSerializer.SerializeComponent(Component) -> TomlTable`
2. `SceneSerializer.SerializeGameObjectHierarchy(GameObject) -> TomlTableArray`

이 반환 타입을 변경하면 다음 파일이 연쇄 수정된다:
- `PasteComponentAction.cs` -- `TomlTable` 필드
- `RemoveComponentAction.cs` -- `TomlTable` 필드
- `DeleteGameObjectAction.cs` -- `TomlTableArray` 필드
- `EditorClipboard.cs` -- `List<TomlTableArray>` 필드
- `ImGuiInspectorPanel.cs` -- `TomlTable` 필드 (`_clipboardComponent`, `_editedImporter`, `_editedMatTable`)

### 수정 대상 파일

### `src/IronRose.Engine/Editor/Undo/Actions/PasteComponentAction.cs`
- **변경**: `TomlTable _serializedComponent` -> `TomlConfig _serializedComponent`
- **생성자**: `TomlTable` 파라미터 -> `TomlConfig` 파라미터
- 호출처에서 SceneSerializer가 `TomlConfig`를 반환하도록 변경 필요

### `src/IronRose.Engine/Editor/Undo/Actions/RemoveComponentAction.cs`
- **변경**: `TomlTable _serializedComponent` -> `TomlConfig _serializedComponent`

### `src/IronRose.Engine/Editor/Undo/Actions/DeleteGameObjectAction.cs`
- **변경**: `TomlTableArray? Components` -> `TomlConfigArray? Components`
- foreach 루프: `foreach (TomlTable ct in snap.Components)` -> `foreach (var ct in snap.Components)`

### `src/IronRose.Engine/Editor/EditorClipboard.cs`
- **변경**: `List<TomlTableArray>? _goEntries` -> `List<TomlConfigArray>? _goEntries`
- `new TomlTable()` -> `TomlConfig.CreateEmpty().GetRawTable()` 또는 `TomlConfig.CreateEmpty()`

### `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs`
- **변경**:
  - `_clipboardComponent` 타입: `TomlTable?` -> `TomlConfig?`
  - `_editedImporter` 타입: 이 phase에서는 변경하지 않음 (RoseMetadata.importer가 TomlTable이므로)
  - `_editedMatTable` 타입: `TomlTable?` -> 유지 또는 `TomlConfig?`로 변경

### `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectPanel.cs`
- **변경**: `new TomlTable()` 사용 부분을 `TomlConfig.CreateEmpty()` 사용으로 변경

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] Undo/Redo 동작이 기존과 동일
- [ ] 컴포넌트 복사-붙여넣기 동작이 기존과 동일
- [ ] GO 삭제 Undo 동작이 기존과 동일

## 참고
- 이 phase는 SceneSerializer의 `SerializeComponent()` 반환 타입을 `TomlConfig`로 변경해야 하므로 연쇄 수정 범위가 크다. 변경 전 SceneSerializer 전체를 재확인해야 한다.
- `ImGuiInspectorPanel`의 `_editedImporter`는 `RoseMetadata.importer` (TomlTable)을 클론한 것이므로, `RoseMetadata.importer` 타입이 변경되지 않는 한 `TomlTable`으로 유지하는 것이 안전하다.
- 설계 문서에서 "Phase 5는 SceneSerializer와의 결합이 깊어 Phase 4 이후 별도 판단이 필요"라고 명시하고 있다. 실제 구현 시 영향 범위를 재평가한 후 진행 여부를 결정한다.

---

# Phase Index

| Phase | 제목 | 파일 | 선행 | 상태 |
|-------|------|------|------|------|
| 44A | 래퍼 클래스 작성 | phase44_toml-config-wrapper_spec.md (Phase 44A 섹션) | - | 미완료 |
| 44B | 설정 파일 마이그레이션 | phase44_toml-config-wrapper_spec.md (Phase 44B 섹션) | 44A | 미완료 |
| 44C | 에셋 임포터 마이그레이션 | phase44_toml-config-wrapper_spec.md (Phase 44C 섹션) | 44A | 미완료 |
| 44D | SceneSerializer + 기타 마이그레이션 | phase44_toml-config-wrapper_spec.md (Phase 44D 섹션) | 44A | 미완료 |
| 44E | 에디터 내부 데이터 컨테이너 (선택적) | phase44_toml-config-wrapper_spec.md (Phase 44E 섹션) | 44D | 미완료 |

## 의존 관계
```
Phase 44A (래퍼 클래스)
  ├──> Phase 44B (설정 파일)
  ├──> Phase 44C (에셋 임포터) -- 44B와 독립
  └──> Phase 44D (SceneSerializer + 기타)
         └──> Phase 44E (에디터 내부, 선택적)
```

Phase 44B와 44C는 서로 독립적이며 병렬 진행 가능하다.
Phase 44D는 44A만 선행 조건이지만, 44B/44C와 함께 완료되면 코드 일관성이 높아진다.
Phase 44E는 44D 이후 안정성 확인 후 선택적으로 진행한다.
