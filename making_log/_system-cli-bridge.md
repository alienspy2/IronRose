# CLI 브릿지 시스템

## 구조
- `src/IronRose.Engine/Cli/CliPipeServer.cs` -- Named Pipe 서버. 백그라운드 스레드에서 클라이언트 연결 수신/응답.
- `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` -- 명령 파싱 및 핸들러 디스패치. 메인 스레드 큐 동기화.
- `src/IronRose.Engine/Cli/CliLogBuffer.cs` -- 최근 로그 링 버퍼 (최대 1000개, 스레드 안전).
- `src/IronRose.Engine/EngineCore.cs` -- CLI 서버 생명주기 관리 (Initialize/Update/Shutdown).
- `tools/ironrose-cli/ironrose_cli.py` -- Python CLI 래퍼. Named Pipe로 명령 전송 및 JSON 응답 출력.

### 의존 관계
```
EngineCore --> CliPipeServer --> CliCommandDispatcher --> CliLogBuffer
                                                     --> SceneManager, ProjectContext (RoseEngine)

Claude Code --Bash--> ironrose_cli.py --Unix Domain Socket--> CliPipeServer
```

## 핵심 동작

### 데이터 흐름
1. EngineCore.Initialize()에서 CliLogBuffer 생성 -> LogSink 연결 -> CliCommandDispatcher 생성 -> CliPipeServer 시작
2. 클라이언트가 Named Pipe에 연결하여 평문 명령 전송 (길이 접두사 프레임)
3. CliPipeServer(백그라운드 스레드)가 요청을 읽어 CliCommandDispatcher.Dispatch() 호출
4. 백그라운드에서 직접 처리 가능한 명령(ping)은 즉시 응답
5. 메인 스레드 접근이 필요한 명령(scene.*)은 ConcurrentQueue에 넣고 ManualResetEventSlim으로 대기
6. EngineCore.Update()에서 ProcessMainThreadQueue() 호출 -> 큐 소비 -> Done.Set()으로 대기 해제
7. 응답 JSON을 클라이언트에 전송

### Python CLI 래퍼 동작
1. `argparse`로 `--project`, `--timeout`, 나머지 command 인자를 파싱
2. `--project` 미지정 시 `~/.ironrose/settings.toml`의 `[editor] last_project` 경로에서 `project.toml`을 읽어 프로젝트명 감지
3. 파이프 경로 결정: Linux `/tmp/CoreFxPipe_ironrose-cli-{name}`, Windows `\\.\pipe\ironrose-cli-{name}`
4. 공백 포함 인자에 쌍따옴표를 씌워서 평문 요청 문자열 생성
5. Unix Domain Socket (Linux) 또는 파일 핸들 (Windows)로 연결
6. 길이 접두사 프레임으로 요청 전송, 응답 수신
7. JSON 파싱: `ok=true`이면 `data` pretty-print (stdout, exit 0), `ok=false`이면 `error` (stderr, exit 1)

### 메시지 프레임 포맷
- [4 bytes little-endian 길이][N bytes UTF-8 문자열]
- 최대 메시지 크기: 16MB

### 파이프 이름
- 형식: `ironrose-cli-{SanitizedProjectName}`
- Linux 실제 경로: `/tmp/CoreFxPipe_ironrose-cli-{name}` (.NET 런타임 규칙, Unix Domain Socket)
- Windows 실제 경로: `\\.\pipe\ironrose-cli-{name}`

## 지원 명령 목록 (Phase 46d-w2 완료)

| 명령 | 실행 위치 | 설명 |
|------|-----------|------|
| `ping` | 백그라운드 | 연결 테스트 |
| `scene.info` | 메인 스레드 | 현재 씬 정보 |
| `scene.list` | 메인 스레드 | 전체 GameObject 목록 |
| `scene.save` | 메인 스레드 | 현재 씬 저장 ([path] 선택) |
| `scene.load` | 메인 스레드 | 씬 파일 로드 |
| `scene.tree` | 메인 스레드 | 계층 트리 (부모-자식 재귀 구조) |
| `scene.new` | 메인 스레드 | 새 빈 씬 생성 (Clear + 새 Scene) |
| `go.get` | 메인 스레드 | GO 상세 정보 (컴포넌트/필드 포함) |
| `go.find` | 메인 스레드 | 이름으로 GO 검색 (정확 매칭) |
| `go.set_active` | 메인 스레드 | GO 활성/비활성 |
| `go.set_field` | 메인 스레드 | 컴포넌트 필드 수정 (리플렉션) |
| `select` | 메인 스레드 | 에디터 선택 변경 |
| `play.enter` | 메인 스레드 | Play 모드 진입 |
| `play.stop` | 메인 스레드 | Play 모드 종료 |
| `play.pause` | 메인 스레드 | 일시정지 |
| `play.resume` | 메인 스레드 | 재개 |
| `play.state` | 메인 스레드 | 현재 Play 상태 조회 |
| `prefab.instantiate` | 메인 스레드 | GUID로 프리팹 인스턴스 생성 ([x,y,z] 위치 옵션) |
| `prefab.save` | 메인 스레드 | GO를 .prefab 파일로 저장 (GUID 반환) |
| `asset.list` | 메인 스레드 | 에셋 DB 전체/필터 목록 (Contains 부분 매칭) |
| `asset.find` | 메인 스레드 | 이름으로 에셋 검색 (case-insensitive 부분 매칭) |
| `asset.guid` | 메인 스레드 | 경로에서 GUID 조회 |
| `asset.path` | 메인 스레드 | GUID에서 경로 조회 |
| `log.recent` | 백그라운드 | 최근 로그 조회 (스레드 안전) |

### go.set_field 지원 타입
- float, int, bool, string, Vector3, Color, enum
- `ParseFieldValue`/`ParseVector3`/`ParseColor` 헬퍼 메서드로 파싱
- `SetFieldCommand.ParseValue`와 동일한 로직 (private이므로 별도 복사)

## 주의사항
- CLI 서버는 프로젝트 미로드 상태에서도 동작한다 (ping 등 기본 명령만 사용 가능)
- 메인 스레드 큐 대기 타임아웃은 5초. 모달 대화상자 등으로 메인 스레드가 블로킹되면 타임아웃 에러 반환
- `maxNumberOfServerInstances = 1`: 동시 클라이언트 연결 미지원
- Linux에서 Stop() 시 소켓 파일을 직접 삭제하여 WaitForConnectionAsync 블로킹을 해제함
- LogSink 람다에서 CliLogBuffer.Push()를 호출하므로, CliLogBuffer는 반드시 LogSink 연결 전에 생성해야 함
- Python 래퍼는 명령을 해석하지 않으므로, C# 서버에 명령만 추가하면 래퍼 수정 불필요

## 사용하는 외부 라이브러리
- `System.IO.Pipes` -- .NET 표준 라이브러리. Named Pipe 서버/클라이언트.
- `System.Text.Json` -- .NET 표준 라이브러리. JSON 직렬화.
- Python 표준 라이브러리만 사용 (`socket`, `struct`, `json`, `argparse`, `os`, `sys`, `re`, `time`).
- 추가 NuGet/pip 패키지 없음.
