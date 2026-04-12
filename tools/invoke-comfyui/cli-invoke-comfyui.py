#!/usr/bin/env python3
"""genimage — AlienHS invoke-comfyui CLI (내부망 전용).

프롬프트를 받아 AlienHS 서버의 /gen/invoke-comfyui/api/generate 를 호출하고
생성된 이미지를 파일로 저장한다. 외부 의존성 없음 (stdlib only).

LAN(192.168.x.x) 또는 같은 머신(127.0.0.1)에서만 동작한다. 외부에서 호출하면
서버 인증 미들웨어가 401을 반환한다.

사용 예:
    python cli-invoke-comfyui.py "푸른 바다의 고래, 저녁노을"
    python cli-invoke-comfyui.py "cat on a sofa" -o cat.png --bypass-refine
    python cli-invoke-comfyui.py "robot on white bg" --rmbg       # 생성 + 배경 제거
    python cli-invoke-comfyui.py "..." --server http://192.168.0.10:25000

환경변수:
    ALIENHS_SERVER   기본 서버 URL (기본값: http://localhost:25000)
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import sys
import time
from pathlib import Path
from urllib import error, request

# Windows 콘솔 UTF-8 강제 (cp1252에서 한글/이모지 출력 실패 방지)
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8")
    except Exception:
        pass

DEFAULT_SERVER = os.environ.get("ALIENHS_SERVER", "http://localhost:25000")
SERVICE_PATH = "/gen/invoke-comfyui"
REQUEST_TIMEOUT = 600  # 초


def _post_json(url: str, body: dict) -> dict:
    data = json.dumps(body).encode("utf-8")
    headers = {"Content-Type": "application/json"}
    req = request.Request(url, data=data, headers=headers, method="POST")
    try:
        with request.urlopen(req, timeout=REQUEST_TIMEOUT) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except error.HTTPError as e:
        body_text = e.read().decode("utf-8", errors="replace")
        try:
            err_json = json.loads(body_text)
            msg = err_json.get("error", body_text)
        except Exception:
            msg = body_text
        raise RuntimeError(f"HTTP {e.code}: {msg}") from None
    except error.URLError as e:
        raise RuntimeError(f"연결 실패: {e.reason}") from None


def _get_bytes(url: str) -> bytes:
    req = request.Request(url, method="GET")
    try:
        with request.urlopen(req, timeout=REQUEST_TIMEOUT) as resp:
            return resp.read()
    except error.HTTPError as e:
        raise RuntimeError(f"HTTP {e.code}: {url}") from None
    except error.URLError as e:
        raise RuntimeError(f"연결 실패: {e.reason}") from None


def _save_images(images_b64: list[str], output: Path, stem: str) -> list[Path]:
    output.parent.mkdir(parents=True, exist_ok=True)
    paths: list[Path] = []
    if len(images_b64) == 1:
        paths.append(output)
        output.write_bytes(base64.b64decode(images_b64[0]))
        return paths
    for idx, b64 in enumerate(images_b64, 1):
        p = output.with_name(f"{stem}_{idx}{output.suffix}")
        p.write_bytes(base64.b64decode(b64))
        paths.append(p)
    return paths


def generate(
    prompt: str,
    *,
    server: str = DEFAULT_SERVER,
    output: Path | None = None,
    bypass_refine: bool = False,
    endpoint: str | None = None,
    comfy_model: str | None = None,
    comfy_url: str | None = None,
) -> dict:
    url = server.rstrip("/") + SERVICE_PATH + "/api/generate"
    body: dict = {"prompt": prompt, "bypass_refine": bypass_refine}
    if endpoint:
        body["endpoint"] = endpoint
    if comfy_model:
        body["comfy_model"] = comfy_model
    if comfy_url:
        body["comfy_url"] = comfy_url

    result = _post_json(url, body)

    images = result.get("images") or []
    if not images:
        raise RuntimeError(f"이미지가 반환되지 않았습니다. 응답: {result}")

    if output is None:
        ts = time.strftime("%Y%m%d_%H%M%S")
        output = Path.cwd() / f"genimage_{ts}.png"

    saved = _save_images(images, output, output.stem)
    return {
        "paths": [str(p) for p in saved],
        "server_filenames": result.get("filenames") or [],
        "refined_prompt": result.get("refined_prompt", ""),
        "prompt_id": result.get("prompt_id", ""),
    }


def remove_bg(
    server_filename: str,
    *,
    server: str = DEFAULT_SERVER,
    comfy_url: str | None = None,
) -> list[tuple[str, bytes]]:
    """서버 output 폴더의 이미지를 RMBG(BEN2)로 배경 제거."""
    base = server.rstrip("/") + SERVICE_PATH
    body: dict = {"filename": server_filename}
    if comfy_url:
        body["comfy_url"] = comfy_url
    result = _post_json(base + "/api/remove-bg", body)
    return _collect_rmbg_outputs(result, base)


def remove_bg_local(
    input_path: Path,
    *,
    server: str = DEFAULT_SERVER,
    comfy_url: str | None = None,
) -> list[tuple[str, bytes]]:
    """로컬 이미지를 업로드해서 RMBG(BEN2)로 배경 제거."""
    data = input_path.read_bytes()
    base = server.rstrip("/") + SERVICE_PATH
    body: dict = {
        "image_base64": base64.b64encode(data).decode("ascii"),
        "filename": input_path.name,
    }
    if comfy_url:
        body["comfy_url"] = comfy_url
    result = _post_json(base + "/api/remove-bg", body)
    return _collect_rmbg_outputs(result, base)


def _collect_rmbg_outputs(result: dict, base: str) -> list[tuple[str, bytes]]:
    filenames = result.get("filenames") or []
    if not filenames:
        raise RuntimeError(f"배경 제거 결과가 없습니다. 응답: {result}")
    images_b64 = result.get("images") or []
    files: list[tuple[str, bytes]] = []
    for idx, fn in enumerate(filenames):
        if idx < len(images_b64):
            data = base64.b64decode(images_b64[idx])
        else:
            data = _get_bytes(base + "/output/" + fn)
        files.append((fn, data))
    return files


def _save_rmbg_outputs(
    server_filenames: list[str],
    local_paths: list[str],
    *,
    server: str,
    comfy_url: str | None,
) -> list[str]:
    """생성된 이미지 각각에 대해 배경 제거를 수행하고 로컬에 저장."""
    nobg_paths: list[str] = []
    for srv_fn, local_path_str in zip(server_filenames, local_paths):
        rmbg_files = remove_bg(srv_fn, server=server, comfy_url=comfy_url)
        base = Path(local_path_str)
        for idx, (_fn, data) in enumerate(rmbg_files, 1):
            suffix_idx = "" if idx == 1 else f"_{idx}"
            out = base.with_name(f"{base.stem}_nobg{suffix_idx}{base.suffix}")
            out.write_bytes(data)
            nobg_paths.append(str(out))
    return nobg_paths


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="genimage",
        description="AlienHS invoke-comfyui CLI (ComfyUI 이미지 생성, 내부망 전용)",
    )
    parser.add_argument("prompt", nargs="?", default=None,
                        help="이미지 생성 프롬프트 (--rmbg-input 모드에서는 생략)")
    parser.add_argument("-o", "--output", type=Path, default=None,
                        help="저장 경로 (기본: ./genimage_<timestamp>.png)")
    parser.add_argument("--server", default=DEFAULT_SERVER,
                        help=f"AlienHS 서버 URL (기본: {DEFAULT_SERVER})")
    parser.add_argument("--bypass-refine", action="store_true",
                        help="AI 프롬프트 정제 건너뛰기 (원본 프롬프트 그대로)")
    parser.add_argument("--endpoint", default=None,
                        help="프롬프트 정제용 엔드포인트 키 (예: genai-31b, gemma4-e4b-q4)")
    parser.add_argument("--model", dest="comfy_model", default=None,
                        help="ComfyUI 모델 파일명 (예: z_image_turbo_nvfp4.safetensors)")
    parser.add_argument("--comfy-url", dest="comfy_url", default=None,
                        help="ComfyUI 서버 URL 덮어쓰기")
    parser.add_argument("--rmbg", action="store_true",
                        help="생성 후 배경 제거(BEN2) 수행. 결과는 <원본>_nobg.png로 저장")
    parser.add_argument("--rmbg-input", dest="rmbg_input", type=Path, default=None,
                        help="로컬 이미지 파일을 업로드해서 배경 제거만 수행 (생성 스킵). "
                             "결과는 -o 경로 또는 <입력>_nobg.png로 저장")
    parser.add_argument("--json", action="store_true",
                        help="결과를 JSON으로 출력 (Claude 툴 연동용)")

    args = parser.parse_args(argv)

    if args.rmbg_input is not None:
        if args.prompt is not None:
            parser.error("--rmbg-input 모드에서는 prompt 인자를 사용할 수 없습니다.")
        if args.rmbg:
            parser.error("--rmbg-input 과 --rmbg 는 동시에 사용할 수 없습니다.")
    else:
        if not args.prompt:
            parser.error("prompt 인자가 필요합니다 (또는 --rmbg-input 사용).")

    try:
        if args.rmbg_input is not None:
            inp = args.rmbg_input
            if not inp.is_file():
                raise RuntimeError(f"입력 파일을 찾을 수 없습니다: {inp}")
            rmbg_files = remove_bg_local(inp, server=args.server, comfy_url=args.comfy_url)
            if args.output is not None:
                out_base = args.output
            else:
                out_base = inp.with_name(f"{inp.stem}_nobg.png")
            nobg_paths: list[str] = []
            for idx, (_fn, data) in enumerate(rmbg_files, 1):
                suffix_idx = "" if idx == 1 else f"_{idx}"
                if idx == 1 and args.output is not None:
                    out = out_base
                else:
                    out = out_base.with_name(f"{out_base.stem}{suffix_idx}{out_base.suffix or '.png'}")
                out.parent.mkdir(parents=True, exist_ok=True)
                out.write_bytes(data)
                nobg_paths.append(str(out))
            result = {"nobg_paths": nobg_paths, "paths": [], "server_filenames": [],
                      "refined_prompt": "", "prompt_id": ""}
        else:
            result = generate(
                args.prompt,
                server=args.server,
                output=args.output,
                bypass_refine=args.bypass_refine,
                endpoint=args.endpoint,
                comfy_model=args.comfy_model,
                comfy_url=args.comfy_url,
            )
            if args.rmbg:
                result["nobg_paths"] = _save_rmbg_outputs(
                    result["server_filenames"],
                    result["paths"],
                    server=args.server,
                    comfy_url=args.comfy_url,
                )
    except RuntimeError as e:
        if args.json:
            print(json.dumps({"ok": False, "error": str(e)}, ensure_ascii=False))
        else:
            print(f"[error] {e}", file=sys.stderr)
        return 1

    if args.json:
        print(json.dumps({"ok": True, **result}, ensure_ascii=False))
    else:
        for p in result["paths"]:
            print(p)
        for p in result.get("nobg_paths", []):
            print(p)
        if result["refined_prompt"] and not args.bypass_refine:
            print(f"[refined] {result['refined_prompt']}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
