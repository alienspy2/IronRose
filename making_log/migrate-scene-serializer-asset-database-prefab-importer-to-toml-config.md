# Phase 44D: SceneSerializer, AssetDatabase, PrefabImporter를 TomlConfig/TomlConvert로 마이그레이션

## 수행한 작업
- SceneSerializer에서 13개의 로컬 변환 메서드(Vec2ToArray, Vec3ToArray, Vec4ToArray, QuatToArray, ColorToArray, ArrayToVec2, ArrayToVec3Direct, ArrayToVec3(table,key), ArrayToVec4, ArrayToQuat(table,key), ArrayToQuatDirect, ArrayToColor, ToFloat)를 삭제하고 모든 호출처를 `TomlConvert.Xxx()` 정적 메서드로 대체
- SceneSerializer의 Load(), LoadFromString(), LoadPrefabGameObjectsFromString(), GetBasePrefabGuid()에서 `Toml.ToModel()` 직접 호출을 `TomlConfig.LoadFile()`/`TomlConfig.LoadString()` + `GetRawTable()` 패턴으로 전환
- AssetDatabase의 프리팹 의존성 스캔 부분에서 `Toml.ToModel()` 직접 호출을 `TomlConfig.LoadFile()` + `GetSection()`/`GetArray()` 패턴으로 전환
- PrefabImporter의 LoadPrefabInternal(), LoadBase(), LoadVariant()에서 `Toml.ToModel()` 직접 호출을 `TomlConfig.LoadFile()` + API 메서드로 전환
- AssetDatabase, PrefabImporter에서 `using Tomlyn;` 제거
- SceneSerializer에서 `using IronRose.Engine;` 추가, `using System.Globalization;` 제거 (삭제된 ToFloat에서만 사용)
- SceneSerializer, PrefabImporter에 frontmatter 추가/갱신

## 변경된 파일
- `src/IronRose.Engine/Editor/SceneSerializer.cs` -- 13개 변환 메서드 삭제, 호출처 TomlConvert로 대체, Load 메서드들 TomlConfig 사용, frontmatter 추가
- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` -- 프리팹 의존성 스캔 TomlConfig 사용, `using Tomlyn;` 제거
- `src/IronRose.Engine/AssetPipeline/PrefabImporter.cs` -- LoadPrefabInternal/LoadBase/LoadVariant 시그니처 및 구현 TomlConfig 사용, `using Tomlyn;` 제거, frontmatter 갱신

## 주요 결정 사항
- **SceneSerializer에서 `using Tomlyn;` 유지**: 명세서는 제거를 지시했으나, Save 메서드들(BuildSceneToml, BuildPrefabToml, BuildVariantPrefabToml)에서 `Toml.FromModel(root)`를 여전히 사용하므로 빌드를 위해 유지. Save 코드 변경은 이 phase 범위 밖.
- **DeserializeFieldValue 내 ToFloat 호출도 TomlConvert.ToFloat로 대체**: 명세서 목록에는 없지만 로컬 ToFloat 메서드를 삭제하므로 필연적으로 대체 필요.
- **TomlToValue 내 Quaternion 패턴 단순화**: `tomlVal is TomlArray qa && qa.Count >= 4` 가드 + 수동 생성 대신 `TomlConvert.ArrayToQuat(tomlVal)` 호출로 통합 (내부에서 동일한 검증 수행).
- **카메라 rotation 로드도 동일하게 단순화**: LoadFromTable 내 sceneViewCamera의 rotation 로드를 `TomlConvert.ArrayToQuat(rv)` 호출로 통합.

## 다음 작업자 참고
- SceneSerializer의 직렬화 코드(SerializeComponent, SerializeComponentGeneric, ValueToToml 등)에서 TomlTable, TomlTableArray, TomlArray를 직접 사용하는 부분은 Phase 5 범위.
- SceneSerializer의 `using Tomlyn;`은 Save 관련 `Toml.FromModel()` 호출이 모두 전환된 후 제거 가능.
- AssetDatabase의 스프라이트 슬라이스 임포트 부분(1076-1103행)에서 `Tomlyn.Model.TomlTable/TomlArray`를 fully qualified로 직접 사용하는 코드가 남아있음 -- 향후 마이그레이션 대상.
