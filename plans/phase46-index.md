# Phase 46 Index: IronRose Editor CLI 브릿지

## 설계 문서
- `plans/phase46_cli-bridge.md`

## Phase 목록

| Phase | 제목 | 파일 | 선행 | 상태 |
|-------|------|------|------|------|
| 46a | Named Pipe 서버 + 기본 명령 + EngineCore 통합 | phase46a_pipe-server.md | - | 미완료 |
| 46b | Python CLI 래퍼 | phase46b_python-wrapper.md | 46a | 미완료 |
| 46c | 추가 명령 세트 | phase46c_extra-commands.md | 46b | 미완료 |

## 의존 관계
```
Phase 46a (Named Pipe 서버 + 기본 명령 + EngineCore 통합)
    |
    v
Phase 46b (Python CLI 래퍼)
    |
    v
Phase 46c (추가 명령 세트)
```

## Phase 분할 근거
- **46a**: 엔진 측 핵심 인프라. Named Pipe 서버, 명령 디스패처, 로그 버퍼, EngineCore 통합. 파이프 수동 연결로 동작 확인 가능. C# 파일 3개 신규 + EngineCore 수정.
- **46b**: Python 래퍼. 46a의 파이프 서버에 연결하는 클라이언트. 엔진 코드 수정 없음. Python 파일 1개 신규.
- **46c**: 실질적인 명령 핸들러 추가. go.get, go.set_field, play.*, scene.save/load, log.recent 등. CliCommandDispatcher에 핸들러만 추가.

## 영향 범위 요약

| 파일 | Phase | 변경 유형 |
|------|-------|-----------|
| `src/IronRose.Engine/Cli/CliPipeServer.cs` | 46a | 신규 - Named Pipe 서버 |
| `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` | 46a, 46c | 신규 - 명령 디스패처 + 핸들러 추가 |
| `src/IronRose.Engine/Cli/CliLogBuffer.cs` | 46a | 신규 - 로그 링 버퍼 |
| `src/IronRose.Engine/EngineCore.cs` | 46a | 수정 - CLI 서버 초기화/업데이트/종료, LogSink 연결 |
| `tools/ironrose-cli/ironrose_cli.py` | 46b | 신규 - Python CLI 래퍼 |
