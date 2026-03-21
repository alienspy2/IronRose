# Phase 44: TomlConfig 래퍼 클래스 설계

## 배경

현재 프로젝트 전체에서 Tomlyn 라이브러리(`TomlTable`, `TomlTableArray`, `TomlArray`, `Toml.ToModel()`, `Toml.FromModel()`)를 직접 사용하고 있다. 총 **18개 C# 파일**에서 Tomlyn API를 직접 호출하며, 각 파일마다 동일한 보일러플레이트 패턴이 반복되고 있다:

- `table.TryGetValue("key", out var v) && v is Type t` 패턴의 반복
- `double` -> `float` 캐스팅, `long` -> `int` 캐스팅의 반복
- 에러 핸들링 코드의 중복
- 기본값 처리 로직의 분산

Phase 43에서 "TomlConfig 래퍼 -- 전체 TOML 사용처 리팩토링, 별도 Phase 필요"로 스킵된 항목이다. 또한 PlayerPrefs 구현 시에도 이 래퍼를 활용할 수 있다.

## 목표

1. Tomlyn 직접 사용을 캡슐화하는 `TomlConfig` 래퍼 클래스를 제공한다.
2. 타입 안전한 값 접근 API를 제공한다 (기본값 지원, 캐스팅 자동 처리).
3. 기존 코드를 점진적으로 마이그레이션할 수 있도록 하위 호환성을 유지한다.
4. PlayerPrefs 등 향후 TOML 기반 기능에서 재사용 가능한 범용 설계를 한다.

## 현재 상태

### TOML 사용처 전체 목록 (18개 파일)

#### 카테고리 1: 설정 파일 읽기/쓰기 (Read-Modify-Write 패턴)

| 파일 | 용도 | TOML 패턴 |
|------|------|-----------|
| `ProjectContext.cs` | project.toml, settings.toml 읽기/쓰기 | `Toml.ToModel()` 파싱 + TryGetValue 중첩 테이블 접근 + `Toml.FromModel()` 쓰기 |
| `RoseConfig.cs` | rose_config.toml 읽기 | `Toml.ToModel()` 파싱 + 중첩 테이블 접근 (읽기 전용) |
| `ProjectSettings.cs` | rose_projectSettings.toml 읽기/쓰기 | `Toml.ToModel()` 읽기 + 문자열 직접 조합 쓰기 |
| `EditorState.cs` | .rose_editor_state.toml 읽기/쓰기 | `Toml.ToModel()` 읽기 + 문자열 직접 조합 쓰기 |

#### 카테고리 2: 에셋 직렬화/역직렬화 (도메인 특화)

| 파일 | 용도 | TOML 패턴 |
|------|------|-----------|
| `SceneSerializer.cs` | 씬 파일 (.scene) 직렬화/역직렬화 | 가장 복잡. TomlTable/TomlTableArray/TomlArray 대량 사용. 직렬화(TomlTable 빌드) + 역직렬화(TryGetValue 체인) |
| `PrefabImporter.cs` | 프리팹 파일 (.prefab) 로드 | `Toml.ToModel()` 파싱 + 중첩 테이블 접근 |
| `MaterialImporter.cs` | 머티리얼 파일 (.mat) 읽기/쓰기 | TomlTable 빌드(직렬화) + TryGetValue(역직렬화) |
| `PostProcessProfileImporter.cs` | 포스트 프로세스 프로파일 (.ppprofile) 읽기/쓰기 | 동적 키 순회 + TomlTable 빌드 |
| `RendererProfileImporter.cs` | 렌더러 프로파일 (.renderer) 읽기/쓰기 | TomlTable 빌드 + TryGetValue |
| `AnimationClipImporter.cs` | 애니메이션 클립 (.anim) 읽기/쓰기 | TomlTableArray 중첩 사용 (curves > keys) |
| `RoseMetadata.cs` | .rose 메타데이터 파일 읽기/쓰기 | TomlTable + TomlArray + TomlTableArray 모두 사용 |
| `AssetDatabase.cs` | 프리팹 의존성 스캔, 스프라이트 슬라이스 | TomlTable/TomlTableArray/TomlArray 읽기 |

#### 카테고리 3: 에디터 내부 데이터 (TomlTable을 데이터 컨테이너로 사용)

| 파일 | 용도 | TOML 패턴 |
|------|------|-----------|
| `ImGuiInspectorPanel.cs` | 컴포넌트 클립보드, 머티리얼 편집 | TomlTable을 메모리 내 데이터 컨테이너로 사용 |
| `ImGuiProjectPanel.cs` | 에셋 복제 시 메타데이터 클론 | `new TomlTable()` + 키 복사 |
| `EditorClipboard.cs` | GO/에셋 복사-붙여넣기 | `TomlTableArray`를 클립보드 데이터 형식으로 사용 |
| `PasteComponentAction.cs` | 컴포넌트 붙여넣기 Undo | `TomlTable`을 직렬화된 컴포넌트 스냅샷으로 사용 |
| `RemoveComponentAction.cs` | 컴포넌트 제거 Undo | `TomlTable`을 직렬화된 컴포넌트 스냅샷으로 사용 |
| `DeleteGameObjectAction.cs` | GO 삭제 Undo | `TomlTableArray`를 컴포넌트 스냅샷으로 사용 |

### 반복되는 공통 패턴

**패턴 1: 중첩 테이블 값 읽기 (가장 빈번)**
```csharp
if (table.TryGetValue("section", out var sVal) && sVal is TomlTable section)
{
    if (section.TryGetValue("key", out var v) && v is string s)
        SomeProperty = s;
    if (section.TryGetValue("key2", out var v2) && v2 is bool b)
        SomeFlag = b;
    if (section.TryGetValue("key3", out var v3) && v3 is double d)
        SomeFloat = (float)d;
    if (section.TryGetValue("key4", out var v4) && v4 is long l)
        SomeInt = (int)l;
}
```

**패턴 2: ToFloat 헬퍼 (3개 파일에서 동일하게 중복 정의)**
```csharp
private static float ToFloat(object? val) => val switch
{
    double d => (float)d,
    long l => l,    // 또는 (float)l
    float f => f,
    _ => 0f,
};
```

**패턴 3: 파일 읽기 + 파싱 + 에러 핸들링**
```csharp
try
{
    var table = Toml.ToModel(File.ReadAllText(path));
    // ... 값 읽기 ...
}
catch (Exception ex)
{
    EditorDebug.LogWarning($"[SomeClass] Failed to parse {path}: {ex.Message}");
}
```

**패턴 4: TomlTable 빌드 + 파일 쓰기**
```csharp
var doc = new TomlTable { ["key1"] = value1, ["key2"] = value2 };
File.WriteAllText(path, Toml.FromModel(doc));
```

**패턴 5: Vector/Color 변환 (SceneSerializer에 정의, 다른 곳에서도 필요)**
```csharp
// TomlArray -> Vector3
private static Vector3 ArrayToVec3Direct(object? val) { ... }
// Vector3 -> TomlArray
private static TomlArray Vec3ToArray(Vector3 v) { ... }
```

## 설계

### 개요

`TomlConfig`를 **래퍼 클래스(인스턴스 기반)**로 설계한다. 내부에 `TomlTable`을 감싸고, 타입 안전한 Get/Set API와 파일 I/O를 제공한다. `Tomlyn` 직접 의존을 `TomlConfig` 클래스 내부로 격리하여, 사용처에서는 `using Tomlyn`이 불필요하게 만드는 것이 최종 목표이다.

단, 마이그레이션은 점진적으로 수행한다. 카테고리 3(에디터 내부에서 TomlTable을 데이터 컨테이너로 사용하는 곳)은 래퍼 도입 범위에서 제외하고, 카테고리 1(설정 파일)과 카테고리 2(에셋 임포터) 중심으로 래퍼를 적용한다.

### 상세 설계

#### 1. TomlConfig 클래스

**파일**: `src/IronRose.Engine/TomlConfig.cs`
**네임스페이스**: `IronRose.Engine`

```csharp
// ------------------------------------------------------------
// @file    TomlConfig.cs
// @brief   TOML 파일 읽기/쓰기 래퍼. Tomlyn 직접 사용을 캡슐화하여
//          타입 안전한 값 접근, 기본값 지원, 에러 핸들링을 제공한다.
// @deps    Tomlyn, RoseEngine/Debug
// @exports
//   class TomlConfig
//     static LoadFile(string): TomlConfig?           -- 파일에서 로드 (실패 시 null)
//     static LoadString(string): TomlConfig?         -- 문자열에서 로드 (실패 시 null)
//     static CreateEmpty(): TomlConfig               -- 빈 설정 생성
//     SaveToFile(string): bool                       -- 파일에 저장
//     ToTomlString(): string                         -- TOML 문자열로 변환
//     GetString(string, string): string              -- 문자열 값 읽기
//     GetInt(string, int): int                       -- 정수 값 읽기
//     GetLong(string, long): long                    -- long 값 읽기
//     GetFloat(string, float): float                 -- 실수 값 읽기
//     GetDouble(string, double): double              -- double 값 읽기
//     GetBool(string, bool): bool                    -- 불리언 값 읽기
//     GetSection(string): TomlConfig?                -- 하위 섹션(테이블) 접근
//     GetArray(string): TomlConfigArray?             -- 테이블 배열 접근
//     GetValues(string): IReadOnlyList<object>?      -- 값 배열 접근
//     SetValue(string, object): void                 -- 값 설정
//     SetSection(string, TomlConfig): void           -- 섹션 설정
//     HasKey(string): bool                           -- 키 존재 여부
//     Remove(string): bool                           -- 키 제거
//     Keys: IEnumerable<string>                      -- 모든 키 열거
//     GetRawTable(): TomlTable                       -- 내부 TomlTable 직접 접근 (마이그레이션용)
//   class TomlConfigArray
//     Count: int                                     -- 배열 크기
//     this[int]: TomlConfig                          -- 인덱서 접근
//     Add(TomlConfig): void                          -- 항목 추가
//     GetEnumerator(): IEnumerator<TomlConfig>       -- 열거자
//   static class TomlConvert
//     ToFloat(object?): float                        -- object -> float 변환
//     ToInt(object?): int                            -- object -> int 변환
//     Vec3ToArray(Vector3): TomlArray                -- Vector3 -> TomlArray
//     ArrayToVec3(object?): Vector3                  -- TomlArray -> Vector3
//     ... (기타 수학 타입 변환)
// @note    GetRawTable()은 점진적 마이그레이션을 위해 제공된다.
//          완전 마이그레이션 후 internal로 변경하거나 제거할 수 있다.
// ------------------------------------------------------------
```

##### 핵심 API 상세

```csharp
namespace IronRose.Engine
{
    /// <summary>
    /// TOML 파일 읽기/쓰기 래퍼. Tomlyn 직접 사용을 캡슐화한다.
    /// </summary>
    public class TomlConfig
    {
        private readonly TomlTable _table;

        // ── 생성 ──

        private TomlConfig(TomlTable table) { _table = table; }

        /// <summary>파일에서 TOML을 로드한다. 파일이 없거나 파싱 실패 시 null 반환.</summary>
        /// <param name="filePath">TOML 파일 절대 경로.</param>
        /// <param name="logTag">로그 태그 (예: "[ProjectContext]"). null이면 로그 생략.</param>
        public static TomlConfig? LoadFile(string filePath, string? logTag = null);

        /// <summary>TOML 문자열에서 로드한다. 파싱 실패 시 null 반환.</summary>
        public static TomlConfig? LoadString(string tomlString, string? logTag = null);

        /// <summary>빈 TomlConfig를 생성한다.</summary>
        public static TomlConfig CreateEmpty();

        // ── 저장 ──

        /// <summary>TOML 파일로 저장한다. 디렉토리가 없으면 생성한다.</summary>
        /// <returns>저장 성공 여부.</returns>
        public bool SaveToFile(string filePath, string? logTag = null);

        /// <summary>TOML 문자열로 변환한다.</summary>
        public string ToTomlString();

        // ── 값 읽기 (기본값 지원) ──

        /// <summary>문자열 값을 읽는다. 키가 없거나 타입이 다르면 기본값 반환.</summary>
        public string GetString(string key, string defaultValue = "");

        /// <summary>정수 값을 읽는다. Tomlyn은 long으로 저장하므로 int로 캐스팅한다.</summary>
        public int GetInt(string key, int defaultValue = 0);

        /// <summary>long 값을 읽는다.</summary>
        public long GetLong(string key, long defaultValue = 0);

        /// <summary>실수 값을 읽는다. Tomlyn은 double로 저장하므로 float로 캐스팅한다.</summary>
        public float GetFloat(string key, float defaultValue = 0f);

        /// <summary>double 값을 읽는다.</summary>
        public double GetDouble(string key, double defaultValue = 0.0);

        /// <summary>불리언 값을 읽는다.</summary>
        public bool GetBool(string key, bool defaultValue = false);

        // ── 중첩 구조 접근 ──

        /// <summary>하위 테이블(섹션)을 TomlConfig로 래핑하여 반환한다. 없으면 null.</summary>
        public TomlConfig? GetSection(string key);

        /// <summary>테이블 배열을 TomlConfigArray로 래핑하여 반환한다. 없으면 null.</summary>
        public TomlConfigArray? GetArray(string key);

        /// <summary>값 배열(TomlArray)을 IReadOnlyList로 반환한다. 없으면 null.</summary>
        public IReadOnlyList<object>? GetValues(string key);

        // ── 값 쓰기 ──

        /// <summary>값을 설정한다. value는 string, long, double, bool, TomlTable, TomlTableArray, TomlArray.</summary>
        public void SetValue(string key, object value);

        /// <summary>TomlConfig를 하위 섹션으로 설정한다.</summary>
        public void SetSection(string key, TomlConfig section);

        /// <summary>TomlConfigArray를 테이블 배열로 설정한다.</summary>
        public void SetArray(string key, TomlConfigArray array);

        // ── 유틸 ──

        public bool HasKey(string key);
        public bool Remove(string key);
        public IEnumerable<string> Keys { get; }

        /// <summary>
        /// 내부 TomlTable을 직접 반환한다.
        /// 점진적 마이그레이션 중 기존 코드와의 호환을 위해 제공.
        /// 완전 마이그레이션 후 internal로 변경 예정.
        /// </summary>
        public TomlTable GetRawTable();
    }
}
```

##### TomlConfigArray 클래스

```csharp
namespace IronRose.Engine
{
    /// <summary>
    /// TomlTableArray의 래퍼. 테이블 배열([[section]])을 다룬다.
    /// </summary>
    public class TomlConfigArray : IEnumerable<TomlConfig>
    {
        private readonly TomlTableArray _array;

        internal TomlConfigArray(TomlTableArray array) { _array = array; }

        public TomlConfigArray() { _array = new TomlTableArray(); }

        public int Count => _array.Count;

        public TomlConfig this[int index] => new TomlConfig(_array[index]);

        public void Add(TomlConfig config) => _array.Add(config.GetRawTable());

        public IEnumerator<TomlConfig> GetEnumerator() { ... }

        /// <summary>내부 TomlTableArray 직접 접근 (마이그레이션용).</summary>
        public TomlTableArray GetRawArray();
    }
}
```

##### TomlConvert 정적 유틸리티 클래스

```csharp
namespace IronRose.Engine
{
    /// <summary>
    /// TOML 값 타입 변환 유틸리티. Tomlyn 타입과 엔진 타입 간 변환을 담당한다.
    /// 기존 SceneSerializer 등에서 중복되던 변환 로직을 통합한다.
    /// </summary>
    public static class TomlConvert
    {
        // ── 기본 타입 변환 ──

        /// <summary>object를 float로 변환한다. double, long, float을 처리.</summary>
        public static float ToFloat(object? val, float defaultValue = 0f);

        /// <summary>object를 int로 변환한다. long, double을 처리.</summary>
        public static int ToInt(object? val, int defaultValue = 0);

        // ── Vector/Quaternion/Color 변환 ──

        public static TomlArray Vec2ToArray(Vector2 v);
        public static Vector2 ArrayToVec2(object? val);

        public static TomlArray Vec3ToArray(Vector3 v);
        public static Vector3 ArrayToVec3(object? val);

        public static TomlArray Vec4ToArray(Vector4 v);
        public static Vector4 ArrayToVec4(object? val);

        public static TomlArray QuatToArray(Quaternion q);
        public static Quaternion ArrayToQuat(object? val);

        public static TomlArray ColorToArray(Color c);
        public static Color ArrayToColor(object? val);

        /// <summary>TomlTable에서 키로 Vector3을 읽는다. 키가 없으면 기본값.</summary>
        public static Vector3 GetVec3(TomlTable table, string key, Vector3? defaultValue = null);

        /// <summary>TomlTable에서 키로 Quaternion을 읽는다. 키가 없으면 identity.</summary>
        public static Quaternion GetQuat(TomlTable table, string key);
    }
}
```

#### 2. 구현 세부사항

##### LoadFile 구현

```csharp
public static TomlConfig? LoadFile(string filePath, string? logTag = null)
{
    if (!File.Exists(filePath))
    {
        if (logTag != null)
            EditorDebug.Log($"{logTag} File not found: {filePath}");
        return null;
    }

    try
    {
        var text = File.ReadAllText(filePath);
        var table = Toml.ToModel(text);
        return new TomlConfig(table);
    }
    catch (Exception ex)
    {
        if (logTag != null)
            EditorDebug.LogWarning($"{logTag} Failed to parse {filePath}: {ex.Message}");
        return null;
    }
}
```

##### GetFloat 구현 예시 (Tomlyn 타입 자동 처리)

```csharp
public float GetFloat(string key, float defaultValue = 0f)
{
    if (!_table.TryGetValue(key, out var val))
        return defaultValue;

    return val switch
    {
        double d => (float)d,
        long l => (float)l,
        float f => f,
        _ => defaultValue,
    };
}
```

##### SaveToFile 구현

```csharp
public bool SaveToFile(string filePath, string? logTag = null)
{
    try
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(filePath, Toml.FromModel(_table));
        return true;
    }
    catch (Exception ex)
    {
        if (logTag != null)
            EditorDebug.LogWarning($"{logTag} Failed to save {filePath}: {ex.Message}");
        return false;
    }
}
```

#### 3. 마이그레이션 전후 비교

##### Before (ProjectContext.cs의 읽기 부분)
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

##### After
```csharp
var config = TomlConfig.LoadFile(tomlPath, "[ProjectContext]");
if (config == null) { /* 에러 처리 */ return; }

var project = config.GetSection("project");
if (project != null)
    ProjectName = project.GetString("name", "");

var engine = config.GetSection("engine");
engineRelPath = engine?.GetString("path", "../IronRose") ?? "../IronRose";
```

##### Before (RendererProfileImporter.cs)
```csharp
var doc = Toml.ToModel(text);
if (doc.TryGetValue("fsr", out var fsrVal) && fsrVal is TomlTable fsr)
{
    if (fsr.TryGetValue("enabled", out var v) && v is bool b) profile.fsrEnabled = b;
    if (fsr.TryGetValue("scale_mode", out var sm) && sm is string sms)
    {
        if (Enum.TryParse<FsrScaleMode>(sms, true, out var mode))
            profile.fsrScaleMode = mode;
    }
    if (fsr.TryGetValue("custom_scale", out var cs)) profile.fsrCustomScale = ToFloat(cs);
    if (fsr.TryGetValue("sharpness", out var sh)) profile.fsrSharpness = ToFloat(sh);
}
```

##### After
```csharp
var config = TomlConfig.LoadString(text, "[RendererProfileImporter]");
if (config == null) return null;

var fsr = config.GetSection("fsr");
if (fsr != null)
{
    profile.fsrEnabled = fsr.GetBool("enabled", false);
    var scaleMode = fsr.GetString("scale_mode", "");
    if (Enum.TryParse<FsrScaleMode>(scaleMode, true, out var mode))
        profile.fsrScaleMode = mode;
    profile.fsrCustomScale = fsr.GetFloat("custom_scale", profile.fsrCustomScale);
    profile.fsrSharpness = fsr.GetFloat("sharpness", profile.fsrSharpness);
}
```

##### Before (AnimationClipImporter.cs 내 ToFloat 중복)
```csharp
// AnimationClipImporter, RendererProfileImporter, PostProcessProfileImporter
// 3개 파일에 동일한 ToFloat 메서드가 중복 정의됨
private static float ToFloat(object? val) => val switch
{
    double d => (float)d,
    long l => l,
    float f => f,
    _ => 0f,
};
```

##### After
```csharp
// 3개 파일 모두 TomlConvert.ToFloat() 사용
// 로컬 ToFloat 메서드 제거
clip.frameRate = TomlConvert.ToFloat(frVal);
```

### 영향 범위

#### Phase 1 (래퍼 클래스 작성)
| 파일 | 변경 내용 |
|------|-----------|
| `src/IronRose.Engine/TomlConfig.cs` | **신규** -- TomlConfig, TomlConfigArray 클래스 |
| `src/IronRose.Engine/TomlConvert.cs` | **신규** -- TomlConvert 정적 유틸리티 클래스 |

#### Phase 2 (설정 파일 마이그레이션)
| 파일 | 변경 내용 |
|------|-----------|
| `ProjectContext.cs` | Toml.ToModel() -> TomlConfig.LoadFile(), TryGetValue 체인 -> GetSection/GetString |
| `RoseConfig.cs` | 동일 패턴 마이그레이션 |
| `ProjectSettings.cs` | 읽기: TomlConfig 사용, 쓰기: TomlConfig.SaveToFile() 사용 |
| `EditorState.cs` | 읽기: TomlConfig 사용, 쓰기: 문자열 직접 조합 패턴은 유지 또는 TomlConfig 사용으로 전환 |

#### Phase 3 (에셋 임포터 마이그레이션)
| 파일 | 변경 내용 |
|------|-----------|
| `MaterialImporter.cs` | TomlConfig 읽기/쓰기 + ToFloat -> TomlConvert.ToFloat |
| `PostProcessProfileImporter.cs` | TomlConfig 읽기/쓰기 + ToFloat 제거 |
| `RendererProfileImporter.cs` | TomlConfig 읽기/쓰기 + ToFloat 제거 |
| `AnimationClipImporter.cs` | TomlConfig 읽기/쓰기 + ToFloat 제거 |
| `RoseMetadata.cs` | TomlConfig 읽기/쓰기, importer 필드 타입을 TomlConfig로 변경 검토 |

#### Phase 4 (SceneSerializer + 에디터 관련 마이그레이션)
| 파일 | 변경 내용 |
|------|-----------|
| `SceneSerializer.cs` | Vec3ToArray/ArrayToVec3 등 -> TomlConvert로 이동. 나머지 TomlTable 직접 사용은 복잡도 높아 선택적 마이그레이션 |
| `AssetDatabase.cs` | 프리팹 의존성 스캔 부분 TomlConfig 사용 |
| `PrefabImporter.cs` | TomlConfig.LoadFile() 사용 |

#### Phase 5 (에디터 내부 데이터 컨테이너 -- 선택적)
| 파일 | 변경 내용 | 비고 |
|------|-----------|------|
| `ImGuiInspectorPanel.cs` | TomlTable -> TomlConfig 사용 | 머티리얼 편집 부분 |
| `ImGuiProjectPanel.cs` | TomlTable 클론 -> TomlConfig 사용 | 간단 |
| `EditorClipboard.cs` | TomlTableArray -> TomlConfigArray | 클립보드 데이터 |
| `PasteComponentAction.cs` | TomlTable -> TomlConfig | Undo 스냅샷 |
| `RemoveComponentAction.cs` | TomlTable -> TomlConfig | Undo 스냅샷 |
| `DeleteGameObjectAction.cs` | TomlTableArray -> TomlConfigArray | Undo 스냅샷 |

Phase 5는 SceneSerializer와의 결합이 깊어 Phase 4 이후 별도 판단이 필요하다. SceneSerializer 내부에서 TomlTable을 직접 빌드하여 반환하는 메서드(`SerializeComponent`, `SerializeGameObjectHierarchy`)의 반환 타입을 변경해야 하므로 연쇄 수정 범위가 크다. TomlConfig가 안정화된 후 수행한다.

### RoseMetadata.importer 필드 처리

현재 `RoseMetadata.importer`의 타입이 `TomlTable`이다. 이 필드는 에셋 임포터 설정을 담는 범용 키-값 컨테이너로, ImGuiInspectorPanel에서 직접 순회하며 편집 UI를 렌더링한다.

**마이그레이션 전략**: Phase 3에서 `RoseMetadata.importer`의 타입을 `TomlConfig`로 변경한다. 단, `GetRawTable()`을 통해 기존 코드와의 호환성을 유지한다. ImGuiInspectorPanel에서의 직접 순회 코드는 Phase 5에서 `TomlConfig.Keys` + `GetString/GetFloat/GetBool`로 전환한다.

### PlayerPrefs 연계

`plans/add-playerprefs-and-persistent-data-path.md`에서 설계된 PlayerPrefs는 현재 `Toml.ToModel()`/`Toml.FromModel()`을 직접 사용하는 방식으로 설계되어 있다. TomlConfig 래퍼가 먼저 구현되면 PlayerPrefs 구현 시 다음과 같이 활용할 수 있다:

```csharp
// PlayerPrefs.Initialize() 내부
var config = TomlConfig.LoadFile(GetPrefsFilePath());
if (config != null)
{
    var intSection = config.GetSection(SECTION_INT);
    if (intSection != null)
    {
        foreach (var key in intSection.Keys)
            _data[key] = new PrefEntry(PrefType.Int, intSection.GetInt(key));
    }
    // float, string 섹션도 동일
}

// PlayerPrefs.Save() 내부
var config = TomlConfig.CreateEmpty();
var intSection = TomlConfig.CreateEmpty();
foreach (var kvp in _data.Where(e => e.Value.Type == PrefType.Int))
    intSection.SetValue(kvp.Key, (long)(int)kvp.Value.Value);
config.SetSection(SECTION_INT, intSection);
// ...
config.SaveToFile(GetPrefsFilePath(), "[PlayerPrefs]");
```

### EditorState/ProjectSettings 쓰기 패턴

현재 `EditorState.Save()`와 `ProjectSettings.Save()`는 `Toml.FromModel()`이 아닌 **문자열 직접 조합**으로 TOML을 생성한다. 이유는 `Toml.FromModel()`의 출력이 가독성 측면에서 불만족스러울 수 있기 때문이다 (빈 줄 없이 연속 출력, 순서 보장 불확실 등).

**마이그레이션 전략**: Phase 2에서는 읽기 부분만 TomlConfig로 전환하고, 쓰기 부분은 기존 문자열 조합을 유지한다. 향후 `TomlConfig`에 `ToFormattedString()` 같은 포매팅 옵션을 추가하거나, Tomlyn의 출력이 충분하다고 판단되면 쓰기도 전환할 수 있다.

## 구현 단계

- [ ] **Phase 1: 래퍼 클래스 작성** (이 Phase에서 빌드 확인, 기존 코드 변경 없음)
  - [ ] `src/IronRose.Engine/TomlConfig.cs` 작성
    - TomlConfig 클래스: LoadFile, LoadString, CreateEmpty, SaveToFile, ToTomlString
    - Get 메서드: GetString, GetInt, GetLong, GetFloat, GetDouble, GetBool
    - 구조 접근: GetSection, GetArray, GetValues
    - Set 메서드: SetValue, SetSection, SetArray
    - 유틸: HasKey, Remove, Keys, GetRawTable
  - [ ] `src/IronRose.Engine/TomlConfig.cs` 내에 TomlConfigArray 클래스 포함
    - Count, 인덱서, Add, IEnumerable 구현, GetRawArray
  - [ ] `src/IronRose.Engine/TomlConvert.cs` 작성
    - ToFloat, ToInt 기본 변환
    - Vec2ToArray/ArrayToVec2, Vec3ToArray/ArrayToVec3, Vec4ToArray/ArrayToVec4
    - QuatToArray/ArrayToQuat, ColorToArray/ArrayToColor
    - GetVec3(TomlTable, string), GetQuat(TomlTable, string)
  - [ ] `dotnet build` 성공 확인

- [ ] **Phase 2: 설정 파일 마이그레이션**
  - [ ] `ProjectContext.cs`: Initialize(), ReadLastProjectPath(), SaveLastProjectPath() 마이그레이션
  - [ ] `RoseConfig.cs`: Load() 마이그레이션
  - [ ] `ProjectSettings.cs`: Load() 읽기 부분 마이그레이션 (Save()는 유지)
  - [ ] `EditorState.cs`: Load() 읽기 부분 마이그레이션 (Save()는 유지)
  - [ ] `using Tomlyn` 제거 가능 여부 확인 (Save에서 아직 사용하면 유지)
  - [ ] `dotnet build` 성공 확인

- [ ] **Phase 3: 에셋 임포터 마이그레이션**
  - [ ] `MaterialImporter.cs`: Import(), WriteDefault(), WriteMaterial() 마이그레이션
  - [ ] `PostProcessProfileImporter.cs`: Import(), Export() 마이그레이션 + 로컬 ToFloat 제거
  - [ ] `RendererProfileImporter.cs`: Import(), Export() 마이그레이션 + 로컬 ToFloat 제거
  - [ ] `AnimationClipImporter.cs`: Import(), Export() 마이그레이션 + 로컬 ToFloat 제거
  - [ ] `RoseMetadata.cs`: FromToml(), ToToml() 마이그레이션, importer 필드 타입 변경 검토
  - [ ] `dotnet build` 성공 확인

- [ ] **Phase 4: SceneSerializer + 기타 마이그레이션**
  - [ ] `SceneSerializer.cs`: Vec3ToArray 등 변환 메서드를 TomlConvert 호출로 대체
  - [ ] `SceneSerializer.cs`: LoadFile(), LoadFromString()의 파싱 부분 TomlConfig 사용
  - [ ] `AssetDatabase.cs`: 프리팹 의존성 스캔 부분 TomlConfig 사용
  - [ ] `PrefabImporter.cs`: LoadPrefabInternal() TomlConfig 사용
  - [ ] `dotnet build` 성공 확인

- [ ] **Phase 5: 에디터 내부 데이터 컨테이너 (선택적)**
  - [ ] SceneSerializer 반환 타입 변경 영향 분석
  - [ ] ImGuiInspectorPanel, EditorClipboard, Undo 액션 클래스 마이그레이션
  - [ ] `dotnet build` 성공 확인

## 대안 검토

### 인스턴스 기반 vs 정적 유틸리티

| 접근 방식 | 장점 | 단점 | 결정 |
|-----------|------|------|------|
| **인스턴스 기반 래퍼** (채택) | 상태(TomlTable) 캡슐화, 체이닝 가능 (`config.GetSection("a").GetInt("b")`), 파일 경로와 데이터를 함께 관리 가능 | 객체 생성 비용 (무시 가능) | **채택** |
| 정적 유틸리티만 | 간단, 기존 코드 변경 최소 | TomlTable이 여전히 노출됨, 캡슐화 없음 | 미채택 |

### TomlConvert를 TomlConfig 내부로 vs 별도 클래스로

| 접근 방식 | 장점 | 단점 | 결정 |
|-----------|------|------|------|
| TomlConfig 내부 | 클래스 수 감소 | 파일이 너무 길어짐, Vector/Color 변환은 TomlConfig의 책임이 아님 | 미채택 |
| **별도 TomlConvert 클래스** (채택) | 단일 책임 원칙, SceneSerializer에서 TomlConvert만 사용 가능 | 클래스 하나 추가 | **채택** |

### RoseMetadata.importer 타입 변경 시점

| 시점 | 장점 | 단점 | 결정 |
|------|------|------|------|
| Phase 3에서 즉시 변경 | 빠른 통합 | ImGuiInspectorPanel 연쇄 수정 필요 | 미채택 |
| **Phase 3에서 변경하되 GetRawTable()로 호환 유지** (채택) | 점진적 마이그레이션, 컴파일 에러 최소 | GetRawTable() 사용이 일시적으로 늘어남 | **채택** |
| Phase 5에서 변경 | 영향 최소 | 오래 기다려야 함 | 미채택 |

## 미결 사항

없음.
