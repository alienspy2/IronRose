# IronRose 디버깅 가이드

## 로깅 전략

모든 주요 동작에 대해 상세한 로그를 남겨야 합니다:

```csharp
// RoseEngine.Debug 사용 (권장 — 파일 로그 + 콘솔 + LogSink 동시 출력)
Debug.Log($"[Engine] Initializing scene: {sceneName}");
Debug.LogWarning($"[Renderer] Fallback to software rasterizer");
Debug.LogError($"[Physics] Timestep overflow: {deltaTime:F4}s");

// Debug 를 사용할 수 없는 경우에만 stdout 으로 대체
// (예: 엔진 초기화 이전, 정적 생성자, 외부 라이브러리 콜백 등)
Console.WriteLine($"[Bootstrap] Pre-engine init: {message}");
```

**로그 카테고리**:
- `[IronRose]`: 엔진 시작/종료
- `[Engine]`: 게임 오브젝트, 씬, 컴포넌트 생명주기
- `[Renderer]`: 렌더링 파이프라인, 그래픽스 API 호출
- `[Physics]`: 물리 시뮬레이션, 충돌 감지
- `[Scripting]`: 스크립트 컴파일, 핫 리로드
- `[Asset]`: 에셋 로딩, 임포팅

---

## 디버깅 전략

문제 발생 시 **로그 기반 디버깅**을 최우선으로 사용합니다:

```
1. 의심 지점에 Debug.Log 로그 추가
2. 빌드 후 실행 테스트 (필요시 Human-in-the-Loop로 사용자에게 확인 요청)
3. 로그 출력 확인 → 원인 분석 → 수정
4. 문제 해결 확인 후 디버깅용 로그 정리
```

**원칙**:
- 코드만 읽고 추측하여 수정하지 말 것 — 반드시 로그를 추가하고 실행하여 실제 동작을 확인
- 한 번에 너무 많은 곳을 수정하지 말 것 — 로그로 문제 범위를 좁힌 후 최소한의 수정 적용
- 실행이 필요한 경우 사용자에게 실행 결과를 요청하거나, 자동화 명령 파일을 활용

**사용자 테스트 요청 시 making_log 작성**:
- 사용자에게 실행/테스트를 요청하기 전에 반드시 `making_log/` 디렉토리에 작업 로그를 작성
- 현재 작업 내용, 진단 로그 위치, 테스트 절차, 다음 단계를 기록
- 사용자 피드백 후 해당 로그를 이어서 업데이트하며 작업을 계속할 수 있도록 함
- 파일명: `making_log/fix-{간략한-설명}.md` (예: `fix-variant-tree-disappear.md`)

---

## 비전 정보 활용 (스크린캡처)

렌더링 결과, UI 레이아웃, 시각적 버그 등 비전 정보가 필요한 경우:

- `EngineCore.ScreenCaptureEnabled = true` 로 디버그 스크린캡처를 활성화
- 프레임 1, 60, 이후 300프레임마다 자동으로 `logs/screenshot_frame{N}_{timestamp}.png` 에 저장됨
- 저장된 스크린샷을 Read 도구로 읽어 시각적 상태를 확인
- 로그만으로 원인을 파악하기 어려운 렌더링/UI 문제에 적극 활용할 것

---

## 자동화 테스트 명령

엔진은 JSON 기반 명령 파일(`.claude/test_commands.json`)을 통해 키 입력, 씬 로드 등을 자동화할 수 있습니다.
디버깅 중 특정 입력 시퀀스를 재현해야 할 때 이 인터페이스를 활용합니다:

```json
{
  "commands": [
    {"type": "scene.load", "scene": "Assets/Scenes/MyScene.toml"},
    {"type": "play_mode", "action": "enter"},
    {"type": "wait", "duration": 1.0},
    {"type": "input.key_press", "key": "Space"},
    {"type": "wait", "duration": 0.5},
    {"type": "screenshot", "path": ".claude/test_outputs/result.png"},
    {"type": "play_mode", "action": "stop"},
    {"type": "quit"}
  ]
}
```

- 엔진은 시작 시 `.claude/test_commands.json` 존재 여부를 확인하고, 있으면 자동 실행
- 파일이 없으면 아무 동작 없이 정상 실행됨
- 각 명령 실행 후 `[Automation]` 태그로 성공/실패 상태를 로그에 기록
- 빌드 → 명령 파일 생성 → 실행 → 로그/스크린샷 확인의 전체 디버깅 루프를 자동화 가능
- **디버깅 완료 후 반드시 `.claude/test_commands.json` 파일을 삭제할 것** — 남겨두면 다음 엔진 실행 시 의도치 않게 자동 명령이 재실행됨

**지원 명령 타입**:
| type | 필드 | 설명 |
|------|------|------|
| `scene.load` | `scene`: 씬 파일 경로 (.toml) | SceneSerializer.Load()로 씬 로드 |
| `input.key_press` | `key`: KeyCode enum 이름 (예: `Space`, `Return`, `A`, `F1`) | 다음 프레임에 KeyDown+KeyUp 시뮬레이션 |
| `wait` | `duration`: 대기 시간(초) | 지정된 시간만큼 프레임 단위로 대기 |
| `screenshot` | `path`: 저장 경로 (생략 시 `.claude/test_outputs/` 자동 생성) | 다음 렌더 프레임에 스크린샷 캡처 |
| `play_mode` | `action`: `enter` / `stop` / `pause` / `resume` (기본: `enter`) | 에디터 플레이 모드 제어 |
| `quit` | — | 엔진 종료 |
