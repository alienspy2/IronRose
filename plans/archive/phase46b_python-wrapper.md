# Phase 46b: Python CLI 래퍼

## 목표
- Python CLI 래퍼(`ironrose_cli.py`)를 구현하여 Claude Code가 Bash로 에디터와 통신할 수 있도록 한다.
- 래퍼는 명령을 해석하지 않고 문자열만 중계하여, 엔진에 새 명령을 추가해도 래퍼 수정이 불필요하다.
- 이 phase 완료 시 `python tools/ironrose-cli/ironrose_cli.py ping` 실행으로 엔진 응답을 받을 수 있다.

## 선행 조건
- Phase 46a 완료 (Named Pipe 서버가 엔진에서 동작 중이어야 함)

## 생성할 파일

### `tools/ironrose-cli/ironrose_cli.py`

- **역할**: Named Pipe에 연결하여 CLI 인자를 그대로 전송하고, 응답을 stdout으로 출력하는 경량 래퍼.
- **언어**: Python 3.8+ (외부 패키지 없음, 표준 라이브러리만 사용)
- **인코딩**: UTF-8 (BOM 없음)

- **사용법**:
  ```bash
  python tools/ironrose-cli/ironrose_cli.py ping
  python tools/ironrose-cli/ironrose_cli.py scene.list
  python tools/ironrose-cli/ironrose_cli.py go.get 42
  python tools/ironrose-cli/ironrose_cli.py go.set_field 42 Transform position "1, 2, 3"
  python tools/ironrose-cli/ironrose_cli.py --project MyGame ping
  ```

- **CLI 인자 구조**:
  - `--project <name>`: 프로젝트 이름 지정 (선택, 기본값 자동 감지 -- 아래 설명)
  - `--timeout <seconds>`: 연결 타임아웃 (선택, 기본값 3초)
  - 나머지 인자: 명령어와 인자들 (그대로 파이프로 전송)

- **핵심 동작**:

  1. **인자 파싱** (`argparse` 사용):
     - `--project` 옵션만 래퍼가 소비한다.
     - `--timeout` 옵션으로 연결 타임아웃을 지정할 수 있다 (기본 3초).
     - 나머지 인자(`args.command`)는 명령어와 인자들이다.
     - 공백이 포함된 인자(셸이 따옴표를 벗겨서 하나의 문자열로 전달한 것)는 쌍따옴표로 다시 감싸서 전송한다.

     ```python
     import argparse

     parser = argparse.ArgumentParser(description="IronRose Editor CLI")
     parser.add_argument("--project", default=None, help="Project name (default: auto-detect from project.toml)")
     parser.add_argument("--timeout", type=float, default=3.0, help="Connection timeout in seconds")
     parser.add_argument("command", nargs=argparse.REMAINDER, help="Command and arguments")
     args = parser.parse_args()
     ```

  2. **프로젝트 이름 결정** (`--project` 미지정 시):
     - `project.toml` 파일을 현재 디렉토리부터 상위로 탐색한다.
     - 찾으면 `[project]` 섹션의 `name` 값을 읽는다 (간단한 TOML 파싱, 정규식으로 충분).
     - 못 찾으면 `"default"` 사용.

     ```python
     import os
     import re

     def find_project_name():
         """project.toml을 찾아서 프로젝트 이름을 반환한다."""
         d = os.getcwd()
         while True:
             toml_path = os.path.join(d, "project.toml")
             if os.path.isfile(toml_path):
                 try:
                     with open(toml_path, "r", encoding="utf-8") as f:
                         content = f.read()
                     m = re.search(r'^\s*name\s*=\s*"([^"]+)"', content, re.MULTILINE)
                     if m:
                         return m.group(1)
                 except Exception:
                     pass
             parent = os.path.dirname(d)
             if parent == d:
                 break
             d = parent
         return "default"
     ```

  3. **파이프 이름 결정**:
     ```python
     import re as _re

     def sanitize_pipe_name(name):
         return _re.sub(r'[^a-zA-Z0-9_-]', '', name) or "default"

     def get_pipe_path(project_name):
         safe = sanitize_pipe_name(project_name)
         pipe_name = f"ironrose-cli-{safe}"
         if os.name == "nt":
             return rf"\\.\pipe\{pipe_name}"
         else:
             return f"/tmp/CoreFxPipe_{pipe_name}"
     ```
     - **중요**: Linux에서 .NET의 `NamedPipeServerStream`은 `/tmp/CoreFxPipe_{pipeName}` 경로에 Unix Domain Socket을 생성한다. Python에서는 `socket.AF_UNIX`로 연결한다.

  4. **요청 문자열 생성**:
     ```python
     def build_request(command_args):
         """CLI 인자를 평문 요청 문자열로 변환한다."""
         parts = []
         for arg in command_args:
             if " " in arg or '"' in arg:
                 parts.append(f'"{arg}"')
             else:
                 parts.append(arg)
         return " ".join(parts)
     ```
     - 셸이 따옴표를 벗기므로, 공백이 포함된 인자에 쌍따옴표를 다시 씌운다.

  5. **Named Pipe 연결** (Linux -- Unix Domain Socket):
     ```python
     import socket
     import struct

     def connect_pipe(pipe_path, timeout):
         """Named Pipe에 연결하여 소켓을 반환한다."""
         sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
         sock.settimeout(timeout)
         sock.connect(pipe_path)
         return sock
     ```

     Windows:
     ```python
     def connect_pipe_windows(pipe_path, timeout):
         """Windows Named Pipe에 연결하여 파일 핸들을 반환한다."""
         import time
         deadline = time.time() + timeout
         while time.time() < deadline:
             try:
                 handle = open(pipe_path, "r+b", buffering=0)
                 return handle
             except FileNotFoundError:
                 time.sleep(0.1)
         raise ConnectionError(f"Cannot connect to pipe: {pipe_path}")
     ```

  6. **메시지 프레임 읽기/쓰기** (4바이트 little-endian 길이 접두사):

     Unix Domain Socket 버전:
     ```python
     def send_message(sock, message):
         """길이 접두사 프레임으로 메시지를 전송한다."""
         data = message.encode("utf-8")
         header = struct.pack("<I", len(data))
         sock.sendall(header + data)

     def recv_message(sock):
         """길이 접두사 프레임으로 메시지를 수신한다."""
         header = recv_exact(sock, 4)
         if not header:
             raise ConnectionError("Connection closed by server")
         length = struct.unpack("<I", header)[0]
         if length <= 0 or length > 16 * 1024 * 1024:
             raise ValueError(f"Invalid message length: {length}")
         data = recv_exact(sock, length)
         if not data:
             raise ConnectionError("Connection closed by server")
         return data.decode("utf-8")

     def recv_exact(sock, n):
         """정확히 n바이트를 수신한다."""
         buf = bytearray()
         while len(buf) < n:
             chunk = sock.recv(n - len(buf))
             if not chunk:
                 return None
             buf.extend(chunk)
         return bytes(buf)
     ```

  7. **응답 처리 및 출력**:
     ```python
     import json
     import sys

     def handle_response(response_json):
         """JSON 응답을 파싱하여 출력하고 exit code를 결정한다."""
         try:
             result = json.loads(response_json)
         except json.JSONDecodeError:
             print(response_json, file=sys.stdout)
             return 0

         if result.get("ok", False):
             data = result.get("data")
             if data is not None:
                 print(json.dumps(data, indent=2, ensure_ascii=False))
             return 0
         else:
             error = result.get("error", "Unknown error")
             print(f"Error: {error}", file=sys.stderr)
             return 1
     ```
     - `ok=true`: `data`를 JSON pretty-print로 stdout 출력, exit code 0.
     - `ok=false`: `error`를 stderr 출력, exit code 1.

  8. **메인 함수**:
     ```python
     def main():
         args = parse_args()  # argparse

         if not args.command:
             print("Error: No command specified", file=sys.stderr)
             print("Usage: ironrose_cli.py [--project NAME] <command> [args...]", file=sys.stderr)
             sys.exit(1)

         project = args.project or find_project_name()
         pipe_path = get_pipe_path(project)
         request = build_request(args.command)
         timeout = args.timeout

         try:
             if os.name == "nt":
                 # Windows: 파일 핸들 방식
                 handle = connect_pipe_windows(pipe_path, timeout)
                 try:
                     send_message_file(handle, request)
                     response = recv_message_file(handle)
                 finally:
                     handle.close()
             else:
                 # Linux/Mac: Unix Domain Socket
                 sock = connect_pipe(pipe_path, timeout)
                 try:
                     send_message(sock, request)
                     response = recv_message(sock)
                 finally:
                     sock.close()

             exit_code = handle_response(response)
             sys.exit(exit_code)

         except FileNotFoundError:
             print(f"Error: IronRose Editor is not running (pipe not found: {pipe_path})", file=sys.stderr)
             sys.exit(1)
         except ConnectionRefusedError:
             print(f"Error: Connection refused (pipe: {pipe_path})", file=sys.stderr)
             sys.exit(1)
         except socket.timeout:
             print(f"Error: Connection timed out ({timeout}s)", file=sys.stderr)
             sys.exit(1)
         except Exception as e:
             print(f"Error: {e}", file=sys.stderr)
             sys.exit(1)

     if __name__ == "__main__":
         main()
     ```

  9. **Windows 파일 핸들 방식의 send/recv** (Windows 전용, 필요 시):
     ```python
     def send_message_file(handle, message):
         data = message.encode("utf-8")
         header = struct.pack("<I", len(data))
         handle.write(header + data)
         handle.flush()

     def recv_message_file(handle):
         header = handle.read(4)
         if not header or len(header) < 4:
             raise ConnectionError("Connection closed by server")
         length = struct.unpack("<I", header)[0]
         if length <= 0 or length > 16 * 1024 * 1024:
             raise ValueError(f"Invalid message length: {length}")
         data = handle.read(length)
         if not data or len(data) < length:
             raise ConnectionError("Connection closed by server")
         return data.decode("utf-8")
     ```

- **의존**: Python 3.8+ 표준 라이브러리만 (`socket`, `struct`, `json`, `argparse`, `os`, `sys`, `re`)

- **파일 구조**:
  ```
  tools/
    ironrose-cli/
      ironrose_cli.py
  ```

- **구현 힌트**:
  - shebang 라인 추가: `#!/usr/bin/env python3`
  - 파일 상단 주석:
    ```python
    """
    IronRose Editor CLI wrapper.
    Connects to the IronRose Editor via Named Pipe and sends commands.
    Usage: python ironrose_cli.py [--project NAME] <command> [args...]
    """
    ```
  - `argparse.REMAINDER`를 사용하면 `--` 없이도 나머지 인자를 모두 캡처할 수 있다. 단, 첫 인자가 `-`로 시작하면 argparse가 옵션으로 해석할 수 있으므로, command 인자 목록에서 `--`가 첫 요소면 제거한다.
  - `socket.AF_UNIX`는 Windows에서 사용 불가하므로, `os.name == "nt"` 분기가 필요하다.

---

## 수정할 파일
- 없음 (엔진 코드 수정 없음, Python 파일 1개 신규 생성만)

## NuGet 패키지
- 없음

## 검증 기준
- [ ] `python tools/ironrose-cli/ironrose_cli.py ping` 실행 시 `{ "pong": true, "project": "..." }` JSON이 stdout에 출력된다.
- [ ] `python tools/ironrose-cli/ironrose_cli.py scene.list` 실행 시 현재 씬의 GameObject 목록이 JSON으로 출력된다.
- [ ] `python tools/ironrose-cli/ironrose_cli.py scene.info` 실행 시 씬 정보가 JSON으로 출력된다.
- [ ] 에디터가 실행 중이 아닐 때 실행하면 `Error: IronRose Editor is not running (pipe not found: ...)` 메시지가 stderr에 출력되고 exit code 1로 종료된다.
- [ ] 존재하지 않는 명령 실행 시 `Error: Unknown command: xxx` 메시지가 stderr에 출력되고 exit code 1로 종료된다.
- [ ] `--project` 옵션으로 다른 프로젝트의 에디터에 연결할 수 있다.
- [ ] `--project` 미지정 시 `project.toml`에서 프로젝트 이름을 자동 감지한다.

## 참고
- Linux에서 .NET `NamedPipeServerStream`이 생성하는 소켓 경로: `/tmp/CoreFxPipe_{pipeName}`. 이 경로 규칙은 .NET 런타임 내부 규칙이며, `CliPipeServer.Start()` 시 로그에 출력된다.
- Python의 `argparse.REMAINDER`는 `--` 구분자 없이 나머지 인자를 수집하지만, 첫 인자가 `-`로 시작하면 에러가 발생할 수 있다. 이 경우 사용자가 `--`를 추가하도록 안내하거나, `sys.argv`를 직접 파싱하는 것도 고려할 수 있다. 기본적으로는 CLI 명령어가 `-`로 시작하지 않으므로 문제없다.
- Windows 지원은 기본 구조만 갖추고, 주요 테스트는 Linux에서 수행한다.
