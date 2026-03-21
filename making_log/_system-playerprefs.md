# PlayerPrefs 시스템

## 구조
- `src/IronRose.Engine/RoseEngine/PlayerPrefs.cs` -- 정적 클래스. Unity 호환 PlayerPrefs API.
  - 메모리에 `Dictionary<string, PrefEntry>` 캐시
  - `Save()` 호출 시 TOML 파일에 기록
  - `Initialize()` / `Shutdown()`은 EngineCore에서 호출

## 핵심 동작

### 데이터 흐름
1. `EngineCore.InitApplication()` -> `PlayerPrefs.Initialize()` -> `LoadFromFile()`
2. 사용자 코드에서 `SetInt/SetFloat/SetString` -> 메모리 캐시 + `_dirty = true`
3. `Save()` 호출 또는 `EngineCore.Shutdown()` -> `SaveInternal()` -> TOML 파일 기록

### 저장 경로
- Linux: `~/.ironrose/playerprefs/{ProjectName}.toml`
- Windows: `%APPDATA%/IronRose/playerprefs/{ProjectName}.toml`
- ProjectName이 비어있으면 "Default"로 폴백

### TOML 파일 구조
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

### 타입 변환 규칙 (Unity 호환)
- `GetInt`에서 Float 타입: `(int)(float)value` (truncate)
- `GetFloat`에서 Int 타입: `(float)(int)value`
- String과 다른 타입 간 변환 불가 (defaultValue 반환)

### 스레드 안전성
- 모든 public 메서드는 `lock (_lock)` 블록으로 감싸져 있음
- `SaveInternal()`은 lock 내부에서만 호출되어 재진입 문제 없음
- `Save()`는 `lock + SaveInternal()`, `Shutdown()`도 `lock + if (_dirty) SaveInternal()`

### Lazy 로딩
- `EnsureLoaded()`: `Initialize()` 없이도 PlayerPrefs API 사용 가능
- 정상 흐름에서는 `EngineCore.InitApplication()`에서 `Initialize()`가 먼저 호출됨

## 의존 관계
- `IronRose.Engine.ProjectContext` -- ProjectName으로 파일명 결정
- `IronRose.Engine.TomlConfig` -- TOML 파일 I/O
- `RoseEngine.Debug` -- (TomlConfig 내부에서 로그 출력)

## 주의사항
- TOML에서 정수는 `long`, 부동소수점은 `double`로 저장됨. `SetValue()`에 넘길 때 반드시 `(long)` / `(double)` 캐스트 필요.
- 빈 문자열 키는 `ArgumentException` throw.
- `DeleteKey()` / `DeleteAll()`은 `_dirty = true`로 설정되므로 Shutdown 시 빈 파일이 저장됨.
- `EnsureLoaded()`는 lock 내부에서 호출되므로 별도 lock 불필요.

## 사용하는 외부 라이브러리
- TomlConfig (내부): TOML 파일 로드/저장/섹션 접근
