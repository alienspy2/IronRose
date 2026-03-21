#!/usr/bin/env python3
# ------------------------------------------------------------
# @file    ironrose_cli.py
# @brief   IronRose Editor CLI wrapper.
#          Connects to the IronRose Editor via Named Pipe and sends commands.
#          The wrapper does not interpret commands -- it relays plain-text strings
#          so that adding new commands to the engine requires no wrapper changes.
# @deps    None (Python 3.8+ standard library only)
# @exports
#   find_project_name() -> str                    -- auto-detect project name from project.toml
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
    """project.toml을 현재 디렉토리부터 상위로 탐색하여 프로젝트 이름을 반환한다."""
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
        help="Project name (default: auto-detect from project.toml)",
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
