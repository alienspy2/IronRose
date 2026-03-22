# Phase 46b: Python CLI 래퍼(ironrose_cli.py) 구현

## 수행한 작업
- `tools/ironrose-cli/ironrose_cli.py` 신규 생성
- Python 3.8+ 표준 라이브러리만 사용하는 경량 CLI 래퍼 구현
- Linux Named Pipe (Unix Domain Socket, `/tmp/CoreFxPipe_ironrose-cli-{project}`) 연결
- Windows Named Pipe (`\\.\pipe\ironrose-cli-{project}`) 기본 지원 구조 포함
- 길이 접두사 메시지 프레임 (4바이트 little-endian uint32) 구현
- `--project` 옵션으로 프로젝트 이름 지정 가능
- `--timeout` 옵션으로 연결 타임아웃 지정 가능 (기본 3초)
- `~/.ironrose/settings.toml`의 `[editor] last_project` 경로에서 `project.toml`을 읽어 프로젝트명 감지
- 공백 포함 인자는 쌍따옴표로 감싸서 전송

## 변경된 파일
- `tools/ironrose-cli/ironrose_cli.py` -- 신규. Python CLI 래퍼. Named Pipe로 명령 전송 및 JSON 응답 출력.

## 주요 결정 사항
- `argparse.REMAINDER`를 사용하여 `--` 없이도 나머지 인자를 모두 캡처. 단, 첫 인자가 `--`인 경우 제거 처리.
- JSON 응답에서 `ok=true`이면 `data`를 pretty-print (stdout, exit 0), `ok=false`이면 `error`를 stderr 출력 (exit 1).
- 비-JSON 응답은 그대로 stdout 출력.
- Windows 지원은 기본 구조만 갖추고 (파일 핸들 방식), 주요 테스트는 Linux에서 수행.
- shebang (`#!/usr/bin/env python3`)과 실행 권한을 설정하여 직접 실행 가능하게 함.

## 다음 작업자 참고
- Phase 46c: 추가 명령 핸들러(`go.get`, `go.set_field`, `play.*`, `log.recent` 등)를 C# 서버에 추가해야 함
- Python 래퍼는 명령을 해석하지 않으므로, C# 서버에 명령만 추가하면 래퍼 수정 없이 동작함
- 실제 테스트는 IronRose Editor가 실행 중이어야 함 (Phase 46a의 CliPipeServer가 동작 상태)
