# Phase 44C: 에셋 임포터 TomlConfig API 마이그레이션

## 수행한 작업
- 5개 에셋 임포터 파일의 TOML 읽기/쓰기를 Tomlyn 직접 사용에서 TomlConfig/TomlConvert API로 전환
- 3개 파일(PostProcessProfileImporter, RendererProfileImporter, AnimationClipImporter)에서 중복 정의된 로컬 `ToFloat` 메서드 제거
- RoseMetadata의 `ToToml()` 메서드를 `ToConfig()`로 대체
- RoseMetadata의 `LoadOrCreate()`에서 `TomlConfig.LoadFile()` 사용
- RoseMetadata의 `Save()`에서 `config.SaveToFile()` 사용

## 변경된 파일
- `src/IronRose.Engine/AssetPipeline/MaterialImporter.cs` — Import()를 TomlConfig.LoadString()+GetSection/GetFloat/GetString으로, WriteDefault/WriteMaterial을 BuildConfig()+SaveToFile()로, ReadColor를 ReadColorFromConfig(TomlConfig)로 변경. using Tomlyn/Tomlyn.Model 제거.
- `src/IronRose.Engine/AssetPipeline/PostProcessProfileImporter.cs` — Import()를 TomlConfig.LoadFile()로, ParseProfile()을 TomlConfig 파라미터로, Export()를 TomlConfig.CreateEmpty()+SetSection+SaveToFile()로 변경. 로컬 ToFloat 삭제. using Tomlyn/Tomlyn.Model 제거.
- `src/IronRose.Engine/AssetPipeline/RendererProfileImporter.cs` — Import()를 TomlConfig.LoadFile()로, ParseProfile()을 TomlConfig 파라미터로, Export()를 TomlConfig.CreateEmpty()+SetSection+SaveToFile()로 변경. 로컬 ToFloat 삭제. using Tomlyn/Tomlyn.Model 제거.
- `src/IronRose.Engine/AssetPipeline/AnimationClipImporter.cs` — Import()를 TomlConfig.LoadFile()로, ParseClip()을 TomlConfig 파라미터로, Export()를 TomlConfig/TomlConfigArray 기반으로 변경. 로컬 ToFloat 삭제. using Tomlyn/Tomlyn.Model 제거.
- `src/IronRose.Engine/AssetPipeline/RoseMetadata.cs` — LoadOrCreate()에서 TomlConfig.LoadFile() 사용, FromToml()에서 new TomlConfig(table)로 값 읽기 간소화, ToToml()을 ToConfig()로 대체, Save()에서 config.SaveToFile() 사용. using Tomlyn 제거, using Tomlyn.Model 유지 (InferImporter). using IronRose.Engine 추가.

## 주요 결정 사항
- RoseMetadata.importer 필드 타입은 TomlTable을 유지 (10+ 파일에서 직접 접근하므로 Phase 5까지 변경 보류)
- InferImporter()는 변경하지 않음 (반환 타입이 TomlTable이고 importer 필드도 TomlTable)
- MaterialImporter의 색상은 [color]/[emission] 서브테이블 구조로 TomlConvert.ColorToArray()와 다른 형태이므로 ReadColorFromConfig() 헬퍼 유지
- RoseMetadata.cs에서 using Tomlyn.Model은 InferImporter의 TomlTable/TomlArray 사용 때문에 유지

## 다음 작업자 참고
- RoseMetadata.importer 타입의 TomlTable -> TomlConfig 전환은 Phase 5에서 검토 예정 (연쇄 수정 범위가 큼)
- SceneSerializer.cs에 기존 빌드 에러가 존재 (Phase 44B 관련 작업 중 발생한 것으로 추정). 이 작업과 무관.
