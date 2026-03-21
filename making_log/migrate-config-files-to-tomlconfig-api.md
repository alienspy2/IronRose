# Phase 44B: 설정 파일 TOML 읽기를 TomlConfig API로 마이그레이션

## 수행한 작업
- 4개 설정 파일(ProjectContext, RoseConfig, ProjectSettings, EditorState)의 TOML 읽기 부분을 `Toml.ToModel()` + `TryGetValue` 체인에서 `TomlConfig.LoadFile()` + `GetSection/GetString/GetBool/GetFloat/GetInt` API로 전환
- `ProjectContext.SaveLastProjectPath()`의 쓰기 부분도 `TomlConfig` API (read-modify-write 패턴)로 전환
- `ProjectSettings.Save()`와 `EditorState.Save()`의 문자열 직접 조합은 의도적으로 유지
- 각 파일에서 `using Tomlyn;` 및 `using Tomlyn.Model;` 제거
- `EditorState.cs`에 `using IronRose.Engine;` 추가 (네임스페이스가 `IronRose.Engine.Editor`이므로 `TomlConfig` 접근에 필요)
- Phase 44A에서 발생한 `SceneSerializer.cs`의 빌드 에러 수정 (불법적인 `TomlConvert.` 접두사가 붙은 메서드 정의 제거, `using Tomlyn;` 복원)

## 변경된 파일
- `src/IronRose.Engine/ProjectContext.cs` — Initialize(), ReadLastProjectPath(), SaveLastProjectPath() 모두 TomlConfig API로 전환. using 정리.
- `src/IronRose.Engine/RoseConfig.cs` — Load() 내 TOML 읽기를 TomlConfig API로 전환. try/catch 제거 (null 체크 + continue). using 정리.
- `src/IronRose.Engine/ProjectSettings.cs` — Load() 내 TOML 읽기를 TomlConfig API로 전환. try/catch 제거. Save() 미변경. using 정리.
- `src/IronRose.Engine/Editor/EditorState.cs` — Load() 내 5개 섹션 읽기를 TomlConfig API로 전환. try/catch 제거. Save() 미변경. using 정리 + `using IronRose.Engine;` 추가.
- `src/IronRose.Engine/Editor/SceneSerializer.cs` — Phase 44A에서 잘못된 `TomlConvert.` 접두사 메서드 정의 제거, 중복 `DeserializeFieldValue` 제거, 사용되지 않는 로컬 `ToFloat` 제거. `using Tomlyn;` 복원 (`Toml.FromModel()` 직접 사용으로 필요).

## 주요 결정 사항
- `EditorState.Load()`에서 `window` 섹션의 x/y/w/h 값은 `HasKey()` 체크 후 `GetInt()`로 읽도록 구현. nullable int? 프로퍼티이므로 키 존재 여부 확인이 필요.
- `RoseConfig.Load()`의 `catch` 블록 제거: `TomlConfig.LoadFile()`이 내부적으로 로그를 출력하므로 `config == null` 시 `continue`로 다음 경로 시도.
- `ProjectContext.Initialize()`의 `catch` 블록 에러 처리 (`EngineRoot = ProjectRoot; IsProjectLoaded = false;`)는 `config == null` 분기로 대체.
- SceneSerializer.cs의 빌드 에러는 Phase 44A의 불완전한 리팩토링으로 인해 발생. `TomlConvert.` 접두사가 붙은 로컬 메서드 정의(C# 문법 오류)를 제거하고, 호출부는 이미 `TomlConvert.X()` 형태로 외부 유틸리티 클래스를 참조하도록 수정되어 있어 정상 동작.

## 다음 작업자 참고
- `ProjectSettings.Save()`와 `EditorState.Save()`는 여전히 문자열 직접 조합 패턴. 향후 필요 시 TomlConfig 기반 쓰기로 전환 가능.
- SceneSerializer.cs는 여전히 `Toml.ToModel()`과 `Toml.FromModel()`을 직접 사용함. 전면적인 TomlConfig API 마이그레이션은 별도 작업 필요.
