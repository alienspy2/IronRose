# TomlConfig 래퍼 시스템

## 구조
- `src/IronRose.Engine/TomlConfig.cs` — TomlConfig, TomlConfigArray 클래스
  - `TomlConfig`: Tomlyn의 `TomlTable`을 래핑. 파일 I/O, 타입 안전한 Get/Set API 제공.
  - `TomlConfigArray`: Tomlyn의 `TomlTableArray`를 래핑. `IEnumerable<TomlConfig>` 구현.
- `src/IronRose.Engine/TomlConvert.cs` — 정적 변환 유틸리티
  - 기본 타입 변환: `ToFloat`, `ToInt`
  - 벡터/쿼터니언/컬러 변환: `Vec2ToArray`/`ArrayToVec2`, `Vec3ToArray`/`ArrayToVec3` 등
  - TomlTable 편의 메서드: `GetVec3`, `GetQuat`

## 핵심 동작

### TomlConfig 생성 흐름
1. 외부: `LoadFile(filePath)` / `LoadString(tomlString)` / `CreateEmpty()`
2. 내부(같은 어셈블리): `new TomlConfig(tomlTable)` (internal 생성자)

### 파일 I/O
- `LoadFile`: `File.ReadAllText` + `Toml.ToModel()` → `TomlConfig`. 실패 시 null.
- `SaveToFile`: `Toml.FromModel(_table)` → `File.WriteAllText`. 디렉토리 자동 생성.
- `logTag` 매개변수가 null이 아닐 때만 로그 출력 (EditorDebug 사용).

### 값 접근 패턴
- Get 메서드: 키 미존재 또는 타입 불일치 시 defaultValue 반환 (예외 없음)
- `GetSection()`: 중첩 `[section]`을 새 `TomlConfig`로 래핑
- `GetArray()`: `[[table_array]]`를 `TomlConfigArray`로 래핑
- `GetValues()`: 스칼라 배열을 `IReadOnlyList<object>` 복사본으로 반환

### TomlConvert 변환 규칙
- `ToFloat`: double → float, long → float, float → float, int → float, string → float.TryParse
- 벡터/쿼터니언/컬러: `TomlArray`의 각 요소에 `ToFloat()` 적용하여 변환
- 요소 부족 시 각 타입의 기본값 반환 (Vector3.zero, Quaternion.identity, Color.white 등)

## 주의사항
- `TomlConfig` 생성자가 `internal`이므로 외부 어셈블리에서는 팩토리 메서드만 사용 가능.
- `GetValues()`는 원본 배열의 복사본을 반환. 원본 수정 불가. null 요소는 필터링됨.
- `GetRawTable()`/`GetRawArray()`는 점진적 마이그레이션을 위한 이스케이프 해치. 장기적으로는 사용을 줄여야 함.
- `SaveToFile()`은 `File.WriteAllText`를 사용하며, 이는 TOML 데이터 저장 용도이지 로그 파일 생성이 아님.
- `TomlArray`에 float 값을 넣을 때는 반드시 `(double)` 캐스트 필요 (Tomlyn이 TOML 실수를 double로 처리).
- `TomlConvert.ToFloat()`의 string 파싱은 `CultureInfo.InvariantCulture`를 사용하여 로케일 독립적.

## 마이그레이션된 파일

### Phase 44C: 에셋 임포터 (5개)
- `MaterialImporter.cs` — TomlConfig.LoadString/CreateEmpty, GetSection/GetFloat/GetString, SetValue/SetSection, SaveToFile
- `PostProcessProfileImporter.cs` — TomlConfig.LoadFile/CreateEmpty, GetSection/GetBool/GetFloat, SetValue/SetSection, SaveToFile
- `RendererProfileImporter.cs` — TomlConfig.LoadFile/CreateEmpty, GetSection/GetBool/GetFloat/GetInt/GetString, SetValue/SetSection, SaveToFile
- `AnimationClipImporter.cs` — TomlConfig.LoadFile/CreateEmpty, GetArray/GetFloat/GetString/GetInt, TomlConfigArray, SaveToFile
- `RoseMetadata.cs` — TomlConfig.LoadFile/CreateEmpty, new TomlConfig(table) (internal), GetString/GetInt/GetValues/GetArray, GetRawTable, SetValue/SetArray, TomlConfigArray, SaveToFile

### Phase 44D: SceneSerializer + AssetDatabase + PrefabImporter (3개)
- `SceneSerializer.cs` — 13개 로컬 변환 메서드 삭제 -> TomlConvert.Xxx() 호출. Load/LoadFromString/LoadPrefabGameObjectsFromString/GetBasePrefabGuid에서 TomlConfig.LoadFile/LoadString + GetRawTable/GetSection/GetString 사용. Save 코드는 Toml.FromModel() 유지.
- `AssetDatabase.cs` — 프리팹 의존성 스캔에서 TomlConfig.LoadFile + GetSection/GetArray/GetString 사용. `using Tomlyn;` 제거.
- `PrefabImporter.cs` — LoadPrefabInternal/LoadBase/LoadVariant에서 TomlConfig.LoadFile + GetSection/GetString/GetRawTable/ToTomlString 사용. `using Tomlyn;` 제거.

### Phase 44B: 설정 파일 (4개)
- `ProjectContext.cs` — TomlConfig.LoadFile/CreateEmpty, GetSection/GetString, SetSection/SetValue, SaveToFile (읽기+쓰기)
- `RoseConfig.cs` — TomlConfig.LoadFile, GetSection/GetBool (읽기 전용)
- `ProjectSettings.cs` — TomlConfig.LoadFile, GetSection/GetString/GetBool (읽기 전용). Save()는 문자열 직접 조합 유지.
- `EditorState.cs` — TomlConfig.LoadFile, GetSection/GetString/GetFloat/GetInt/GetBool/HasKey (읽기 전용). Save()는 문자열 직접 조합 유지.

### 미마이그레이션
- `RoseMetadata.InferImporter()` — TomlTable 직접 사용 유지 (importer 필드 타입이 TomlTable)
- `RoseMetadata.importer` 필드 — Phase 5까지 TomlTable 유지 (10+ 파일에서 TryGetValue로 직접 접근)
- `SceneSerializer` 직렬화 코드(SerializeComponent, DeserializeComponent 등) — TomlTable/TomlTableArray 직접 사용 유지. Phase 5 범위.
- `SceneSerializer` Save 메서드 — `Toml.FromModel()` 직접 호출 유지. `using Tomlyn;` 제거 불가.
- `AssetDatabase` 스프라이트 슬라이스 임포트 — `Tomlyn.Model.TomlTable/TomlArray` fully qualified 사용 유지

## 사용하는 외부 라이브러리
- Tomlyn 0.20.0: TOML 파싱/직렬화. `Toml.ToModel()`, `Toml.FromModel()`, `TomlTable`, `TomlTableArray`, `TomlArray`.
