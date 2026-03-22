#!/usr/bin/env python3
# ------------------------------------------------------------
# @file    ironrose_cli.py
# @brief   IronRose Editor CLI wrapper.
#          Connects to the IronRose Editor via Named Pipe and sends commands.
#          The wrapper does not interpret commands -- it relays plain-text strings
#          so that adding new commands to the engine requires no wrapper changes.
# @deps    None (Python 3.8+ standard library only)
# @exports
#   find_project_name() -> str                    -- read project name from ~/.ironrose/settings.toml
#   _read_toml_string(file, section, key) -> str   -- simple TOML string value reader
#   sanitize_pipe_name(name: str) -> str          -- sanitize project name for pipe path
#   get_pipe_path(project_name: str) -> str       -- build platform-specific pipe path
#   build_request(command_args: list) -> str       -- join CLI args into a request string
#   connect_pipe(pipe_path: str, timeout: float) -> socket  -- connect via Unix Domain Socket
#   send_message(sock, message: str) -> None      -- send length-prefixed message
#   recv_message(sock) -> str                     -- receive length-prefixed message
#   handle_response(response_json: str) -> int    -- parse JSON response and print output
#   main() -> None                                -- entry point
# @note    Linux: connects to /tmp/CoreFxPipe_ironrose-cli-{project} via AF_UNIX.
#          Windows: opens \\.\pipe\ironrose-cli-{project} as a binary file.
#          --project 미지정 시 ~/.ironrose/settings.toml의 last_project에서 프로젝트명을 읽는다.
#          Message frame: [4 bytes LE uint32 length][N bytes UTF-8 string].
#          Max message size: 16MB.
# ------------------------------------------------------------
"""
IronRose Editor CLI wrapper.
Connects to the IronRose Editor via Named Pipe and sends commands.
Usage: python ironrose_cli.py [--project NAME] [--timeout SECONDS] <command> [args...]
"""

import argparse
import json
import os
import re
import socket
import struct
import sys
import time

MAX_MESSAGE_SIZE = 16 * 1024 * 1024  # 16MB


def find_project_name():
    """~/.ironrose/settings.toml의 last_project 경로에서 프로젝트 이름을 읽어 반환한다.

    1. ~/.ironrose/settings.toml을 열어 [editor] 섹션의 last_project 값을 읽는다.
    2. 해당 경로의 project.toml에서 [project] 섹션의 name 필드를 읽는다.
    settings.toml이나 project.toml을 찾을 수 없으면 에러 메시지를 출력하고 종료한다.
    """
    settings_path = os.path.join(
        os.path.expanduser("~"), ".ironrose", "settings.toml"
    )
    if not os.path.isfile(settings_path):
        print(
            f"Error: Settings file not found: {settings_path}\n"
            "Please open a project in IronRose Editor first.",
            file=sys.stderr,
        )
        sys.exit(1)

    # settings.toml에서 [editor] last_project 읽기
    project_path = _read_toml_string(settings_path, "editor", "last_project")
    if not project_path:
        print(
            f"Error: 'last_project' not found in [editor] section of {settings_path}\n"
            "Please open a project in IronRose Editor first.",
            file=sys.stderr,
        )
        sys.exit(1)

    # project.toml에서 [project] name 읽기
    toml_path = os.path.join(project_path, "project.toml")
    if not os.path.isfile(toml_path):
        print(
            f"Error: project.toml not found at: {toml_path}\n"
            f"The last_project path '{project_path}' may be invalid.",
            file=sys.stderr,
        )
        sys.exit(1)

    name = _read_toml_string(toml_path, "project", "name")
    if not name:
        print(
            f"Error: 'name' not found in [project] section of {toml_path}",
            file=sys.stderr,
        )
        sys.exit(1)

    return name


def _read_toml_string(file_path, section, key):
    """간이 TOML 파서. 지정한 섹션의 문자열 키 값을 반환한다.

    정규 TOML 파서 없이 [section]과 key = "value" 패턴만 처리한다.
    해당 키가 없으면 None을 반환한다.
    """
    try:
        with open(file_path, "r", encoding="utf-8") as f:
            content = f.read()
    except Exception:
        return None

    # 섹션 시작 위치 찾기
    section_pattern = re.compile(r'^\s*\[\s*' + re.escape(section) + r'\s*\]', re.MULTILINE)
    section_match = section_pattern.search(content)
    if not section_match:
        return None

    # 섹션 시작 이후의 내용에서 다음 섹션 전까지 범위 결정
    after_section = content[section_match.end():]
    next_section = re.search(r'^\s*\[', after_section, re.MULTILINE)
    if next_section:
        section_body = after_section[:next_section.start()]
    else:
        section_body = after_section

    # key = "value" 패턴 매칭
    key_pattern = re.compile(r'^\s*' + re.escape(key) + r'\s*=\s*"([^"]*)"', re.MULTILINE)
    key_match = key_pattern.search(section_body)
    if key_match:
        return key_match.group(1)

    return None


def sanitize_pipe_name(name):
    """파이프 이름에 안전하지 않은 문자를 제거한다."""
    return re.sub(r'[^a-zA-Z0-9_-]', '', name) or "default"


def get_pipe_path(project_name):
    """플랫폼에 맞는 Named Pipe 경로를 반환한다."""
    safe = sanitize_pipe_name(project_name)
    pipe_name = f"ironrose-cli-{safe}"
    if os.name == "nt":
        return rf"\\.\pipe\{pipe_name}"
    else:
        return f"/tmp/CoreFxPipe_{pipe_name}"


def build_request(command_args):
    """CLI 인자를 평문 요청 문자열로 변환한다.

    셸이 따옴표를 벗기므로, 공백이 포함된 인자에 쌍따옴표를 다시 씌운다.
    """
    parts = []
    for arg in command_args:
        if " " in arg or '"' in arg:
            parts.append(f'"{arg}"')
        else:
            parts.append(arg)
    return " ".join(parts)


# ---------------------------------------------------------------------------
# Unix Domain Socket (Linux / macOS)
# ---------------------------------------------------------------------------

def connect_pipe(pipe_path, timeout):
    """Named Pipe(Unix Domain Socket)에 연결하여 소켓을 반환한다."""
    sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    sock.settimeout(timeout)
    sock.connect(pipe_path)
    return sock


def send_message(sock, message):
    """길이 접두사 프레임으로 메시지를 전송한다."""
    data = message.encode("utf-8")
    header = struct.pack("<I", len(data))
    sock.sendall(header + data)


def recv_exact(sock, n):
    """정확히 n바이트를 수신한다."""
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            return None
        buf.extend(chunk)
    return bytes(buf)


def recv_message(sock):
    """길이 접두사 프레임으로 메시지를 수신한다."""
    header = recv_exact(sock, 4)
    if not header:
        raise ConnectionError("Connection closed by server")
    length = struct.unpack("<I", header)[0]
    if length <= 0 or length > MAX_MESSAGE_SIZE:
        raise ValueError(f"Invalid message length: {length}")
    data = recv_exact(sock, length)
    if not data:
        raise ConnectionError("Connection closed by server")
    return data.decode("utf-8")


# ---------------------------------------------------------------------------
# Windows Named Pipe (file handle)
# ---------------------------------------------------------------------------

def connect_pipe_windows(pipe_path, timeout):
    """Windows Named Pipe에 연결하여 파일 핸들을 반환한다."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            handle = open(pipe_path, "r+b", buffering=0)
            return handle
        except FileNotFoundError:
            time.sleep(0.1)
    raise ConnectionError(f"Cannot connect to pipe: {pipe_path}")


def send_message_file(handle, message):
    """길이 접두사 프레임으로 메시지를 전송한다 (Windows 파일 핸들)."""
    data = message.encode("utf-8")
    header = struct.pack("<I", len(data))
    handle.write(header + data)
    handle.flush()


def recv_message_file(handle):
    """길이 접두사 프레임으로 메시지를 수신한다 (Windows 파일 핸들)."""
    header = handle.read(4)
    if not header or len(header) < 4:
        raise ConnectionError("Connection closed by server")
    length = struct.unpack("<I", header)[0]
    if length <= 0 or length > MAX_MESSAGE_SIZE:
        raise ValueError(f"Invalid message length: {length}")
    data = handle.read(length)
    if not data or len(data) < length:
        raise ConnectionError("Connection closed by server")
    return data.decode("utf-8")


# ---------------------------------------------------------------------------
# Response handling
# ---------------------------------------------------------------------------

def handle_response(response_json):
    """JSON 응답을 파싱하여 출력하고 exit code를 결정한다.

    - ok=true : data를 JSON pretty-print로 stdout 출력, exit code 0
    - ok=false: error를 stderr 출력, exit code 1
    - 비-JSON : 원문 그대로 stdout 출력, exit code 0
    """
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


# ---------------------------------------------------------------------------
# Argument parsing
# ---------------------------------------------------------------------------

def parse_args():
    """CLI 인자를 파싱한다."""
    parser = argparse.ArgumentParser(
        description="IronRose Editor CLI -- send commands to a running editor instance.",
        usage="%(prog)s [--project NAME] [--timeout SECONDS] <command> [args...]",
    )
    parser.add_argument(
        "--project",
        default=None,
        help="Project name (default: read from ~/.ironrose/settings.toml)",
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=3.0,
        help="Connection timeout in seconds (default: 3)",
    )
    parser.add_argument(
        "command",
        nargs=argparse.REMAINDER,
        help="Command and arguments to send to the editor",
    )
    args = parser.parse_args()

    # argparse.REMAINDER may include a leading '--' separator; strip it.
    if args.command and args.command[0] == "--":
        args.command = args.command[1:]

    return args


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main():
    args = parse_args()

    if not args.command:
        print("Error: No command specified", file=sys.stderr)
        print(
            "Usage: ironrose_cli.py [--project NAME] [--timeout SECONDS] <command> [args...]",
            file=sys.stderr,
        )
        sys.exit(1)

    project = args.project or find_project_name()
    pipe_path = get_pipe_path(project)
    request = build_request(args.command)
    timeout = args.timeout

    try:
        if os.name == "nt":
            # Windows: file handle approach
            handle = connect_pipe_windows(pipe_path, timeout)
            try:
                send_message_file(handle, request)
                response = recv_message_file(handle)
            finally:
                handle.close()
        else:
            # Linux / macOS: Unix Domain Socket
            sock = connect_pipe(pipe_path, timeout)
            try:
                send_message(sock, request)
                response = recv_message(sock)
            finally:
                sock.close()

        exit_code = handle_response(response)
        sys.exit(exit_code)

    except FileNotFoundError:
        print(
            f"Error: IronRose Editor is not running (pipe not found: {pipe_path})",
            file=sys.stderr,
        )
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
