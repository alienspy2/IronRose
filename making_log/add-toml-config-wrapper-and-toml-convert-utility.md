# Phase 44A: TomlConfig 래퍼 클래스 및 TomlConvert 유틸리티 신규 작성

## 수행한 작업
- `TomlConfig` 클래스: Tomlyn `TomlTable`을 감싸는 래퍼 클래스 신규 작성. 타입 안전한 Get/Set API, 파일 I/O(LoadFile/SaveToFile), 문자열 I/O(LoadString/ToTomlString), 팩토리 메서드(CreateEmpty) 제공.
- `TomlConfigArray` 클래스: `TomlTableArray`를 감싸는 래퍼. `IEnumerable<TomlConfig>` 구현으로 foreach 사용 가능.
- `TomlConvert` 정적 유틸리티 클래스: Tomlyn 타입과 엔진 타입(Vector2/3/4, Quaternion, Color) 간 변환 로직 통합. 기존 SceneSerializer 등에서 중복되던 ToFloat, Vec3ToArray, ArrayToColor 등을 단일 진입점으로 제공.

## 변경된 파일
- `src/IronRose.Engine/TomlConfig.cs` -- 신규 생성. TomlConfig + TomlConfigArray 클래스 정의
- `src/IronRose.Engine/TomlConvert.cs` -- 신규 생성. 정적 타입 변환 유틸리티

## 주요 결정 사항
- **TomlConfig 생성자를 `internal`로 설정**: 명세서 권장대로 `internal`로 설정. 같은 어셈블리(IronRose.Engine) 내 TomlConfigArray 등에서 `new TomlConfig(TomlTable)` 호출 가능. 외부 어셈블리에서는 팩토리 메서드만 사용 가능.
- **GetValues()의 TomlArray 반환 처리**: `TomlArray`가 `IReadOnlyList<object>`를 직접 구현하지 않으므로, nullable 경고 없이 `List<object>`로 변환하기 위해 foreach로 null 필터링하며 복사하는 방식 채택.
- **TomlConvert.ToFloat()에 int/string 케이스 포함**: SceneSerializer의 가장 완전한 버전을 기반으로 `int` 직접 변환과 `string` 파싱(InvariantCulture)을 포함.
- **UTF-8 BOM 인코딩**: 두 파일 모두 EF BB BF BOM 추가 완료.

## 다음 작업자 참고
- Phase 44B에서 ProjectContext, RoseConfig, ProjectSettings, EditorState의 TOML 읽기 부분을 TomlConfig API로 전환 예정.
- Phase 44D에서 SceneSerializer의 로컬 변환 메서드를 TomlConvert로 대체 예정.
- 기존 코드에 영향 없음 (신규 파일만 추가, 기존 코드 변경 없음).
