# Phase 46c: CLI 추가 명령 세트 구현

## 수행한 작업
- `CliCommandDispatcher`에 나머지 13개 CLI 명령 핸들러를 추가하여 초기 명령 세트를 완성했다.
- 추가된 명령:
  - `go.get` -- ID 또는 이름으로 GameObject 상세 정보 (컴포넌트/필드 포함) 조회
  - `go.find` -- 이름으로 GameObject 검색 (정확 매칭)
  - `go.set_active` -- GameObject 활성/비활성 전환
  - `go.set_field` -- 컴포넌트 필드 값 수정 (리플렉션 기반)
  - `select` -- 에디터 선택 상태 변경 (Inspector 연동)
  - `play.enter`, `play.stop`, `play.pause`, `play.resume`, `play.state` -- Play 모드 제어
  - `scene.save`, `scene.load` -- 씬 파일 저장/로드
  - `log.recent` -- 최근 로그 조회 (스레드 안전, 직접 실행)
- 헬퍼 메서드 추가: `FindGameObject`, `FindGameObjectById`, `ParseFieldValue`, `ParseVector3`, `ParseColor`

## 변경된 파일
- `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` -- 13개 명령 핸들러, 5개 헬퍼 메서드 추가. using 문에 `System.Linq`, `System.Reflection`, `System.Globalization`, `IronRose.Engine.Editor` 추가. frontmatter 갱신.

## 주요 결정 사항
- `ParseFieldValue`/`ParseVector3`/`ParseColor`는 `SetFieldCommand` 내부 로직을 복사하여 별도 구현했다. 원본이 private이므로 호출 불가능하기 때문.
- `go.get`에서 `GameObjectSnapshot.From(go)`를 재활용하여 기존 SceneSnapshot과 일관된 정보를 제공한다.
- `log.recent`는 `CliLogBuffer`가 스레드 안전하므로 `ExecuteOnMainThread`를 거치지 않고 직접 실행한다. 메인 스레드가 블로킹된 상태에서도 로그 조회가 가능하다.
- `go.set_field` 실행 후 `SceneManager.GetActiveScene().isDirty = true`를 설정하여 씬 변경 추적이 정상 동작하도록 했다.

## 다음 작업자 참고
- `ParseFieldValue`와 `SetFieldCommand.ParseValue`에 동일한 로직이 중복되어 있다. 추후 리팩토링 시 공통 유틸리티로 추출하는 것이 바람직하다.
- `go.set_field`는 현재 flat 타입(float, int, bool, string, Vector3, Color, enum)만 지원한다. 복합 타입(Quaternion 등) 지원이 필요하면 `ParseFieldValue`에 케이스를 추가해야 한다.
- Python CLI 래퍼는 명령을 해석하지 않으므로 이 변경으로 인한 래퍼 수정은 불필요하다.
