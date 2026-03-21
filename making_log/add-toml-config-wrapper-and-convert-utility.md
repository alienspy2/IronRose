# Phase 44A: TomlConfig 래퍼 클래스 및 TomlConvert 유틸리티 신규 작성

## 수행한 작업
- `TomlConfig` 래퍼 클래스 신규 작성: Tomlyn의 `TomlTable`을 감싸는 타입 안전한 Get/Set API와 파일 I/O 제공
- `TomlConfigArray` 클래스 신규 작성: `TomlTableArray`를 래핑하여 `IEnumerable<TomlConfig>` 지원
- `TomlConvert` 정적 유틸리티 클래스 신규 작성: Tomlyn 타입과 엔진 타입(Vector2/3/4, Quaternion, Color) 간 변환 통합

## 변경된 파일
- `src/IronRose.Engine/TomlConfig.cs` — TomlConfig, TomlConfigArray 클래스 신규 생성
- `src/IronRose.Engine/TomlConvert.cs` — TomlConvert 정적 유틸리티 클래스 신규 생성

## 주요 결정 사항
- `TomlConfig` 생성자를 `internal`로 설정하여 같은 어셈블리(IronRose.Engine) 내에서 `TomlConfigArray`가 `new TomlConfig(table)` 호출 가능. 외부 어셈블리에서는 `LoadFile`, `LoadString`, `CreateEmpty` 팩토리 메서드로만 생성 가능.
- `GetValues()` 메서드에서 `TomlArray`를 `IReadOnlyList<object>`로 반환 시, `TomlArray`가 `IReadOnlyList<object>`로 직접 캐스팅되지 않아 null 필터링된 `List<object>` 복사본을 반환하도록 구현.
- `TomlConvert.ToFloat()`는 SceneSerializer의 가장 완전한 버전을 기준으로 `double`, `long`, `float`, `int`, `string` 파싱을 모두 포함.

## 다음 작업자 참고
- Phase 44B~D에서 기존 코드(SceneSerializer, AnimationClipImporter, ProjectSettings 등)의 중복 변환 로직을 이 클래스들로 대체하는 마이그레이션 진행 예정.
- `GetValues()`가 복사본을 반환하므로 원본 수정 불가. 원본 TomlArray에 값을 쓰려면 `GetRawTable()`을 통해 직접 접근 필요.
