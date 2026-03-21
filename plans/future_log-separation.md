# 로그 분리: EditorDebug / Debug 클래스 분리

> Unity 방식처럼 에디터 로그와 프로젝트 로그를 **클래스 수준**에서 분리한다.

## 개요

- **`EditorDebug`**: 엔진/에디터 내부 로그. `{EngineRoot}/Logs/`에 기록.
- **`Debug`**: 게임 런타임/유저 스크립트 로그. `{ProjectRoot}/Logs/`에 기록.

두 클래스 모두 `IronRose.Contracts`에 위치 (모든 모듈에서 참조 가능).

## 현재 상태

- `Debug` 클래스(`IronRose.Contracts`)가 단일 `_logPath` 사용
- 정적 생성자에서 `CWD/Logs/` 초기화 → `SetLogDirectory()`로 `ProjectRoot/Logs`로 전환
- 전환 후 **모든 로그**(엔진 + 프로젝트)가 한 파일에 섞임
- `LogSink` delegate도 채널 구분 없이 단일 `LogEntry` 전달

### 현재 스크린샷 경로

| 용도 | 경로 | 비고 |
|------|------|------|
| 디버그 캡처 (`ScreenCaptureEnabled`) | `"Logs"` (상대경로, CWD 기준) | 엔진 디버깅용 |
| F12 유저 스크린샷 | `{ProjectRoot}/Screenshots/` | 유저 기능 |
| 자동화 테스트 | `.claude/test_outputs/` | CI/테스트용 |

## 분리 설계

### 1. 클래스 분리

```
IronRose.Contracts/
  Debug.cs         → 게임/프로젝트 로그 (기존 클래스를 역할 재정의)
  EditorDebug.cs   → 에디터/엔진 내부 로그 (신규)
```

각 클래스가 **자기 로그 파일과 경로를 독립적으로 관리**. 이중 경로, 채널 enum 불필요.

### 2. EditorDebug (신규)

```csharp
// IronRose.Contracts/EditorDebug.cs
public static class EditorDebug
{
    // 항상 {EngineRoot}/Logs/ 에 기록
    // 엔진 시작 시 즉시 초기화 — 프로젝트 로드와 무관

    Log(object message)
    LogWarning(object message)
    LogError(object message)

    LogSink: Action<LogEntry>?   // 에디터 Console 패널 연동
}
```

- 로그 경로: `{EngineRoot}/Logs/editor_{timestamp}.log`
- 엔진 프로세스 시작과 동시에 활성화
- `SetLogDirectory()` 불필요 — 경로가 고정

### 3. Debug (역할 재정의)

```csharp
// IronRose.Contracts/Debug.cs
public static class Debug
{
    // 프로젝트 로드 후 {ProjectRoot}/Logs/ 에 기록
    // 프로젝트 미로드 시 EditorDebug로 폴백

    Log(object message)
    LogWarning(object message)
    LogError(object message)

    SetLogDirectory(string logDir)   // 기존과 동일
    LogSink: Action<LogEntry>?
}
```

- 로그 경로: `{ProjectRoot}/Logs/ironrose_{timestamp}.log`
- 프로젝트 로드 전 `Debug.Log()` 호출 시 → `EditorDebug`로 위임 (폴백)
- 유저 스크립트에서 `using RoseEngine; Debug.Log(...)` — API 변경 없음

### 4. LogEntry 확장

```csharp
public enum LogSource { Editor, Project }

public readonly record struct LogEntry(
    LogLevel Level,
    LogSource Source,    // 추가 — Console 패널 필터링용
    string Message,
    DateTime Timestamp
);
```

### 5. 모듈별 사용 클래스

| 모듈 | 사용 클래스 | 근거 |
|------|-------------|------|
| `IronRose.Engine` (에디터, UI, 에셋) | `EditorDebug` | 에디터 내부 동작 |
| `IronRose.Rendering` | `EditorDebug` | 렌더러/그래픽 시스템 |
| `IronRose.Scripting` — 컴파일러 | `EditorDebug` | 빌드 도구 |
| `IronRose.Scripting` — ScriptDomain.Update | `Debug` | 유저 스크립트 실행 |
| `IronRose.Physics` | `Debug` | 게임 런타임 |
| `IronRose.Standalone` | `Debug` | 빌드된 게임 실행 |

### 6. Standalone 모드

- `EditorDebug`를 참조하지 않거나, 호출 시 no-op 처리
- `Debug`만 사용 — `{ExeRoot}/Logs/`에 기록

## 스크린샷 경로 정책

| 용도 | 저장 경로 | 이유 |
|------|-----------|------|
| 디버그 캡처 (`ScreenCaptureEnabled`) | `{EngineRoot}/Logs/` | 엔진 디버깅 목적. 프로젝트 디렉토리를 오염시키지 않음 |
| F12 유저 스크린샷 | `{ProjectRoot}/Screenshots/` | 유저가 의도적으로 찍는 것. 프로젝트에 귀속 |
| 자동화 테스트 | `.claude/test_outputs/` | CI/에이전트 전용. gitignore 대상 |

## 마이그레이션 순서

1. `EditorDebug` 클래스 신규 생성 (`IronRose.Contracts/EditorDebug.cs`)
2. `LogEntry`에 `LogSource` 필드 추가
3. `Debug`에 프로젝트 미로드 시 `EditorDebug` 폴백 로직 추가
4. `EngineCore` 초기화 코드 정리 — `EditorDebug`는 경로 고정, `Debug.SetLogDirectory()`는 프로젝트 로드 시만 호출
5. 디버그 스크린샷 경로를 `{EngineRoot}/Logs/`로 명시 (현재 상대경로 `"Logs"` → 절대경로)
6. 각 모듈의 `Debug.Log` → `EditorDebug.Log` 점진적 마이그레이션
7. phase43 #12(static 생성자), #14(로그 디렉토리 전환 잔존) 이슈 동시 해소
