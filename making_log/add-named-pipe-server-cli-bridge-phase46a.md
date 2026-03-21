# Phase 46a: Named Pipe 서버 + 기본 명령 + EngineCore 통합

## 수행한 작업
- Named Pipe 서버(`CliPipeServer`)를 백그라운드 스레드로 구현하여 CLI 클라이언트 요청을 수신하도록 함
- CLI 명령 디스패처(`CliCommandDispatcher`)를 구현하여 `ping`, `scene.info`, `scene.list` 3개 기본 명령 처리
- 로그 링 버퍼(`CliLogBuffer`)를 구현하여 최근 1000개 로그를 CLI에서 조회 가능하게 함
- `EngineCore.cs`를 수정하여 CLI 서버 초기화/업데이트/종료 및 LogSink 연결

## 변경된 파일
- `src/IronRose.Engine/Cli/CliLogBuffer.cs` -- 신규. 링 버퍼 기반 로그 수집 (스레드 안전, lock 기반)
- `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` -- 신규. 따옴표 인식 파싱, 핸들러 맵, 메인 스레드 큐 동기화 (ManualResetEventSlim, 타임아웃 5초)
- `src/IronRose.Engine/Cli/CliPipeServer.cs` -- 신규. Named Pipe 서버 (백그라운드 스레드, 길이 접두사 메시지 프레임, CancellationToken 기반 종료)
- `src/IronRose.Engine/EngineCore.cs` -- 수정. using 추가, 필드 추가, Initialize()에서 LogSink 연결 변경 + CLI 서버 시작, Update()에서 메인 스레드 큐 처리, Shutdown()에서 CLI 서버 정지

## 주요 결정 사항
- `CliLogBuffer`는 `Initialize()` 최상단에서 생성하여 LogSink 람다에서 참조 가능하게 함
- CLI 서버는 프로젝트 미로드 상태(Startup Panel)에서도 동작 (ping 등 기본 명령 사용 가능)
- `scene.info`, `scene.list`는 메인 스레드 접근이 필요하여 `ExecuteOnMainThread()` 패턴 사용
- `WaitForConnectionAsync` + CancellationToken으로 `Stop()` 시 블로킹 해제
- Linux에서 소켓 파일 삭제로 추가적인 블로킹 해제 보장

## 다음 작업자 참고
- Phase 46b: Python CLI 래퍼(`tools/ironrose-cli/ironrose_cli.py`) 구현 필요
- Phase 46c: 추가 명령 핸들러(`go.get`, `go.set_field`, `play.*`, `log.recent` 등) 추가 필요
- 파이프 이름 규칙: Linux에서 `/tmp/CoreFxPipe_ironrose-cli-{ProjectName}` (.NET 런타임 내부 규칙)
- `maxNumberOfServerInstances = 1`로 동시 연결 미지원. 추후 확장 시 변경 필요할 수 있음
