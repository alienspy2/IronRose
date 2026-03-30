# Phase 45b: PlayerPrefs 클래스 구현 및 EngineCore 통합

## 목표
- `RoseEngine` 네임스페이스에 Unity 호환 `PlayerPrefs` 정적 클래스를 신규 구현한다.
- TOML 포맷으로 사용자 홈 디렉토리에 키-값 데이터를 저장/로드한다.
- `EngineCore`의 초기화/종료 흐름에 `PlayerPrefs`를 통합한다.

## 선행 조건
- Phase 45a 완료 (`Application.companyName`, `Application.productName`, `Application.persistentDataPath`, `Application.InitializePaths()` 구현 완료)
- `ProjectContext.Initialize()`에서 `[project] company` 필드 읽기 구현 완료

## 생성할 파일

### `src/IronRose.Engine/RoseEngine/PlayerPrefs.cs`
- **역할**: Unity 호환 PlayerPrefs API. 메모리에 값을 캐시하고 `Save()` 호출 시 TOML 파일에 기록한다.
- **네임스페이스**: `RoseEngine`
- **클래스**: `public static class PlayerPrefs`
- **TOML I/O**: 반드시 `IronRose.Engine.TomlConfig` 래퍼 API를 사용한다. Tomlyn 직접 사용 금지.

#### 파일 헤더
```csharp
// ------------------------------------------------------------
// @file    PlayerPrefs.cs
// @brief   Unity 호환 PlayerPrefs API. TOML 포맷으로 사용자 홈 디렉토리에 저장.
//          에디터와 런타임이 같은 파일을 공유한다.
// @deps    IronRose.Engine/ProjectContext, IronRose.Engine/TomlConfig, RoseEngine/Debug
// @exports
//   static class PlayerPrefs
//     SetInt(string, int): void
//     GetInt(string, int): int
//     SetFloat(string, float): void
//     GetFloat(string, float): float
//     SetString(string, string): void
//     GetString(string, string): string
//     HasKey(string): bool
//     DeleteKey(string): void
//     DeleteAll(): void
//     Save(): void
// @note    값은 메모리에 캐시되며 Save() 호출 시 디스크에 기록된다.
//          앱 종료 시 자동으로 Save()가 호출된다.
//          스레드 안전성: lock으로 보호한다.
//          TOML I/O는 TomlConfig 래퍼 API만 사용한다.
// ------------------------------------------------------------
```

#### 필요한 using
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using IronRose.Engine;
```

#### 내부 타입 정의
```csharp
private enum PrefType { Int, Float, String }
private readonly record struct PrefEntry(PrefType Type, object Value);
```

#### 내부 필드
```csharp
private static readonly Dictionary<string, PrefEntry> _data = new();
private static readonly object _lock = new();
private static bool _dirty = false;
private static bool _loaded = false;
```

#### TOML 섹션 이름 상수
```csharp
private const string SECTION_INT = "int";
private const string SECTION_FLOAT = "float";
private const string SECTION_STRING = "string";
```

#### 주요 멤버 상세

**Set 메서드들**:
- `public static void SetInt(string key, int value)` -- `_data`에 `PrefEntry(PrefType.Int, value)` 저장, `_dirty = true`
- `public static void SetFloat(string key, float value)` -- `_data`에 `PrefEntry(PrefType.Float, value)` 저장, `_dirty = true`
- `public static void SetString(string key, string value)` -- `_data`에 `PrefEntry(PrefType.String, value)` 저장, `_dirty = true`
- 모든 Set 메서드에서:
  - `lock (_lock)` 사용
  - `EnsureLoaded()` 호출 (lazy 로드)
  - 키가 `null` 또는 빈 문자열이면 `ArgumentException` throw

**Get 메서드들** (각 타입별 2개 오버로드):
- `public static int GetInt(string key)` -- `GetInt(key, 0)` 호출
- `public static int GetInt(string key, int defaultValue)` -- `_data`에서 key를 찾아 PrefEntry를 읽음
- `public static float GetFloat(string key)` -- `GetFloat(key, 0f)` 호출
- `public static float GetFloat(string key, float defaultValue)`
- `public static string GetString(string key)` -- `GetString(key, "")` 호출
- `public static string GetString(string key, string defaultValue)`
- 모든 Get 메서드에서:
  - `lock (_lock)` 사용
  - `EnsureLoaded()` 호출
  - key가 없으면 defaultValue 반환

**타입 변환 규칙** (Unity 호환):
- `GetInt`에서 PrefType.Float인 경우: `(int)(float)entry.Value` (truncate)
- `GetInt`에서 PrefType.String인 경우: defaultValue 반환
- `GetFloat`에서 PrefType.Int인 경우: `(float)(int)entry.Value` (int -> float 변환)
- `GetFloat`에서 PrefType.String인 경우: defaultValue 반환
- `GetString`에서 PrefType.Int 또는 PrefType.Float인 경우: defaultValue 반환

구현 예시 (`GetInt`):
```csharp
public static int GetInt(string key, int defaultValue)
{
    lock (_lock)
    {
        EnsureLoaded();
        if (!_data.TryGetValue(key, out var entry))
            return defaultValue;

        return entry.Type switch
        {
            PrefType.Int => (int)entry.Value,
            PrefType.Float => (int)(float)entry.Value,
            _ => defaultValue,
        };
    }
}
```

**관리 메서드들**:
- `public static bool HasKey(string key)` -- `lock` + `EnsureLoaded()` + `_data.ContainsKey(key)`
- `public static void DeleteKey(string key)` -- `lock` + `EnsureLoaded()` + `_data.Remove(key)` + `_dirty = true`
- `public static void DeleteAll()` -- `lock` + `EnsureLoaded()` + `_data.Clear()` + `_dirty = true`
- `public static void Save()` -- TOML 파일에 기록 (아래 상세 설명)

**Save() 구현 상세**:
```csharp
public static void Save()
{
    lock (_lock)
    {
        var config = TomlConfig.CreateEmpty();

        // 타입별 섹션 생성
        var intSection = TomlConfig.CreateEmpty();
        var floatSection = TomlConfig.CreateEmpty();
        var stringSection = TomlConfig.CreateEmpty();

        foreach (var kvp in _data)
        {
            switch (kvp.Value.Type)
            {
                case PrefType.Int:
                    intSection.SetValue(kvp.Key, (long)(int)kvp.Value.Value);
                    break;
                case PrefType.Float:
                    floatSection.SetValue(kvp.Key, (double)(float)kvp.Value.Value);
                    break;
                case PrefType.String:
                    stringSection.SetValue(kvp.Key, (string)kvp.Value.Value);
                    break;
            }
        }

        config.SetSection(SECTION_INT, intSection);
        config.SetSection(SECTION_FLOAT, floatSection);
        config.SetSection(SECTION_STRING, stringSection);

        var filePath = GetPrefsFilePath();
        config.SaveToFile(filePath, "[PlayerPrefs]");
        _dirty = false;
    }
}
```

**중요**: `SetValue()`에 int를 넘길 때는 `(long)` 캐스트, float를 넘길 때는 `(double)` 캐스트가 필요하다. TOML(Tomlyn)은 정수를 `long`, 부동소수점을 `double`로 저장하기 때문이다.

**Initialize() 구현 상세**:
```csharp
internal static void Initialize()
{
    lock (_lock)
    {
        _data.Clear();
        _dirty = false;
        _loaded = false;
        LoadFromFile();
        _loaded = true;
    }
}
```

**LoadFromFile() private 메서드**:
```csharp
private static void LoadFromFile()
{
    var filePath = GetPrefsFilePath();
    var config = TomlConfig.LoadFile(filePath);
    if (config == null)
        return;  // 파일이 없으면 빈 상태로 시작

    // [int] 섹션
    var intSection = config.GetSection(SECTION_INT);
    if (intSection != null)
    {
        foreach (var key in intSection.Keys)
        {
            var value = intSection.GetInt(key, 0);
            _data[key] = new PrefEntry(PrefType.Int, value);
        }
    }

    // [float] 섹션
    var floatSection = config.GetSection(SECTION_FLOAT);
    if (floatSection != null)
    {
        foreach (var key in floatSection.Keys)
        {
            var value = floatSection.GetFloat(key, 0f);
            _data[key] = new PrefEntry(PrefType.Float, value);
        }
    }

    // [string] 섹션
    var stringSection = config.GetSection(SECTION_STRING);
    if (stringSection != null)
    {
        foreach (var key in stringSection.Keys)
        {
            var value = stringSection.GetString(key, "");
            _data[key] = new PrefEntry(PrefType.String, value);
        }
    }
}
```

**EnsureLoaded() private 메서드**:
```csharp
private static void EnsureLoaded()
{
    if (!_loaded)
    {
        LoadFromFile();
        _loaded = true;
    }
}
```
- `Initialize()`가 호출되기 전에도 PlayerPrefs API를 사용할 수 있도록 lazy 로드 지원.

**Shutdown() 구현**:
```csharp
internal static void Shutdown()
{
    lock (_lock)
    {
        if (_dirty)
            Save();
    }
}
```

**GetPrefsFilePath() private 메서드**:
```csharp
private static string GetPrefsFilePath()
{
    var projectName = IronRose.Engine.ProjectContext.ProjectName;
    if (string.IsNullOrEmpty(projectName))
        projectName = "Default";

    var safeName = SanitizeFileName(projectName);

    string baseDir;
    if (OperatingSystem.IsWindows())
    {
        baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "IronRose", "playerprefs");
    }
    else
    {
        baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ironrose", "playerprefs");
    }

    return Path.Combine(baseDir, safeName + ".toml");
}
```

**SanitizeFileName() private 메서드**:
```csharp
private static string SanitizeFileName(string name)
{
    var invalid = Path.GetInvalidFileNameChars();
    var chars = name.ToCharArray();
    for (int i = 0; i < chars.Length; i++)
    {
        if (Array.IndexOf(invalid, chars[i]) >= 0)
            chars[i] = '_';
    }
    return new string(chars);
}
```

#### TOML 파일 포맷 예시
```toml
[int]
score = 100
level = 5

[float]
volume = 0.8
sensitivity = 1.5

[string]
player_name = "Alice"
language = "ko"
```

- **저장 경로**:
  - Linux: `~/.ironrose/playerprefs/{ProjectName}.toml`
  - Windows: `%APPDATA%/IronRose/playerprefs/{ProjectName}.toml`

## 수정할 파일

### `src/IronRose.Engine/EngineCore.cs`
- **변경 내용 1**: `InitApplication()` 메서드 끝에 `PlayerPrefs.Initialize()` 호출 추가.
- **변경 내용 2**: `Shutdown()` 메서드의 기존 코드 앞에 `PlayerPrefs.Shutdown()` 호출 추가.
- **이유**: 엔진 초기화 시 PlayerPrefs 파일을 로드하고, 종료 시 더티 상태의 데이터를 자동 저장하기 위함.

`InitApplication()` 변경 - Phase 45a에서 수정한 코드 끝에 한 줄 추가:
```csharp
private void InitApplication()
{
    Application.isPlaying = false;
    Application.isPaused = false;
    Application.QuitAction = () => _window!.Close();
    Application.PauseCallback = IronRose.Engine.Editor.EditorPlayMode.PausePlayMode;
    Application.ResumeCallback = IronRose.Engine.Editor.EditorPlayMode.ResumePlayMode;

    // 경로 초기화 (ProjectContext가 이미 초기화된 상태)
    var company = Application.companyName;
    var product = ProjectContext.ProjectName;
    if (string.IsNullOrEmpty(product)) product = "DefaultProduct";
    Application.InitializePaths(company, product);

    // PlayerPrefs 초기화 (TOML 파일에서 로드)
    PlayerPrefs.Initialize();
}
```

`Shutdown()` 변경 - 기존 코드의 `Application.isPlaying = false;` 바로 위에 추가:
```csharp
public void Shutdown()
{
    RoseEngine.EditorDebug.Log("[Engine] EngineCore shutting down...");
    PlayerPrefs.Shutdown();  // 더티 상태면 자동 Save
    Application.isPlaying = false;
    Application.QuitAction = null;
    // ... 기존 코드 이하 동일 ...
}
```

- **주의**: `PlayerPrefs`는 `RoseEngine` 네임스페이스이고, `EngineCore.cs`에는 이미 `using RoseEngine;`이 있으므로 추가 using 불필요.

## NuGet 패키지
- 없음 (TomlConfig 래퍼를 통해 기존 Tomlyn 패키지 사용)

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] `PlayerPrefs.SetInt("score", 100)` 후 `PlayerPrefs.GetInt("score")` 호출 시 100 반환
- [ ] `PlayerPrefs.Save()` 호출 시 `~/.ironrose/playerprefs/{ProjectName}.toml` 파일이 생성됨
- [ ] 엔진 종료 시 `PlayerPrefs.Shutdown()`이 호출되어 미저장 데이터가 자동 기록됨
- [ ] `PlayerPrefs.SetInt("x", 5)` 후 `PlayerPrefs.GetFloat("x")` 호출 시 `5.0f` 반환 (타입 변환)
- [ ] `PlayerPrefs.SetFloat("y", 3.7f)` 후 `PlayerPrefs.GetInt("y")` 호출 시 `3` 반환 (truncate)
- [ ] `PlayerPrefs.SetString("s", "hello")` 후 `PlayerPrefs.GetInt("s")` 호출 시 기본값 `0` 반환
- [ ] `PlayerPrefs.DeleteKey("score")` 후 `PlayerPrefs.HasKey("score")`가 `false` 반환
- [ ] `PlayerPrefs.DeleteAll()` 후 모든 키가 삭제됨

## 참고
- TOML에서 정수는 `long`으로, 부동소수점은 `double`로 저장된다. `Save()` 시 `int`를 `(long)`으로, `float`를 `(double)`로 캐스트해야 하고, `LoadFromFile()` 시 `TomlConfig.GetInt()`와 `TomlConfig.GetFloat()`가 내부적으로 `long -> int`, `double -> float` 변환을 처리한다.
- `TomlConfig.SetValue()`에 넘기는 값의 타입: string은 `string`, 정수는 `long`, 부동소수점은 `double`, 불리언은 `bool`. `int`나 `float`를 직접 넘기면 Tomlyn이 인식하지 못할 수 있으므로 반드시 `long`/`double`로 캐스트한다.
- `EnsureLoaded()`는 `Initialize()` 없이 PlayerPrefs API를 사용하는 경우를 대비한 안전장치이다. `EngineCore.InitApplication()`에서 `Initialize()`가 먼저 호출되므로 정상 흐름에서는 `EnsureLoaded()`가 이미 로드된 상태이다.
- 키 유효성: TOML bare key에서 사용할 수 없는 문자(`.`, `[`, `]`, `"`, `\` 등)가 포함된 키는 Tomlyn이 자동으로 quoted key로 처리하므로 별도 이스케이프 로직은 불필요하다. 단, 빈 문자열 키는 `ArgumentException`을 throw한다.
- 파일 인코딩: UTF-8 with BOM.
- `lock` 범위: 모든 public 메서드에서 `lock (_lock)` 블록으로 전체 메서드 본문을 감싼다. `Save()` 내부에서도 lock이 잡히므로, `Shutdown()`에서 `Save()`를 호출할 때는 lock 밖에서 호출하거나 `Save()` 내부에서 이미 lock을 잡고 있으므로 `Shutdown()`은 lock 안에서 `_dirty` 체크 후 lock 범위 내에서 직접 저장 로직을 실행하거나, 아래와 같이 설계한다:
  - `Shutdown()`은 `lock (_lock)` 안에서 `_dirty` 플래그만 체크하고, dirty이면 lock을 해제한 뒤 `Save()`를 호출하는 방식은 race condition이 생길 수 있다.
  - **권장 방식**: `Save()`의 실제 저장 로직을 `SaveInternal()` private 메서드로 분리하고, `Save()`에서는 `lock + SaveInternal()`, `Shutdown()`에서도 `lock + if (_dirty) SaveInternal()`로 호출한다.

```csharp
public static void Save()
{
    lock (_lock)
    {
        SaveInternal();
    }
}

internal static void Shutdown()
{
    lock (_lock)
    {
        if (_dirty)
            SaveInternal();
    }
}

private static void SaveInternal()
{
    // lock 내부에서만 호출됨 (lock은 호출자가 잡음)
    var config = TomlConfig.CreateEmpty();
    // ... 섹션 구성 및 SaveToFile ...
    _dirty = false;
}
```
