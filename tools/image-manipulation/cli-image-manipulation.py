#!/usr/bin/env python3
"""
cli-image-manipulation.py — Local raster image manipulation CLI for IronRose.

General-purpose post-processing for raster images. Network-free, deterministic.
Functionality is grouped into subcommands and extended over time.

Subcommands (current):
  trim     Auto-crop transparent borders from an image with alpha channel.
  convert  Change image format (PNG/JPEG/WEBP). Overwrites input by default.
  resize   Resize image by width/height/scale. Overwrites input by default.

Planned (add as subcommands, not flags):
  pad, pot, tint, ...

Conventions for new subcommands:
  - Single-file input; use shell loops for batch.
  - `--json` prints {"ok": true, "output": "...", ...} or {"ok": false, "error": "..."}.
  - Default output = overwrite input. Use `-o` for a different path.
  - Stick to Pillow + stdlib; no network calls.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Optional

try:
    from PIL import Image
except ImportError:
    print(json.dumps({"ok": False, "error": "Pillow (PIL) is not installed. pip install Pillow"}))
    sys.exit(2)


# ---------- shared helpers ----------

_FORMAT_ALIASES = {
    "jpg": "JPEG",
    "jpeg": "JPEG",
    "png": "PNG",
    "webp": "WEBP",
}

_FORMAT_EXTS = {
    "JPEG": ".jpg",
    "PNG": ".png",
    "WEBP": ".webp",
}

_RESAMPLE = {
    "lanczos": Image.Resampling.LANCZOS,
    "bilinear": Image.Resampling.BILINEAR,
    "bicubic": Image.Resampling.BICUBIC,
    "nearest": Image.Resampling.NEAREST,
    "box": Image.Resampling.BOX,
    "hamming": Image.Resampling.HAMMING,
}


def _emit(result: dict, as_json: bool) -> int:
    if as_json:
        print(json.dumps(result, ensure_ascii=False))
    else:
        if result.get("ok"):
            print(f"OK: {result.get('output')}")
            if "bbox" in result:
                print(f"  bbox: {result['bbox']}  size: {result.get('original_size')} -> {result.get('new_size')}")
            elif "original_size" in result and "new_size" in result:
                print(f"  size: {result['original_size']} -> {result['new_size']}")
            elif "format" in result:
                print(f"  format: {result['format']}")
        else:
            print(f"ERROR: {result.get('error')}", file=sys.stderr)
    return 0 if result.get("ok") else 1


def _parse_color(spec: str) -> tuple[int, int, int]:
    """Parse #rgb/#rrggbb/'r,g,b'/'white' etc into an (R,G,B) tuple."""
    s = spec.strip().lower()
    named = {
        "white": (255, 255, 255),
        "black": (0, 0, 0),
        "red": (255, 0, 0),
        "green": (0, 255, 0),
        "blue": (0, 0, 255),
        "gray": (128, 128, 128),
        "grey": (128, 128, 128),
        "transparent": (0, 0, 0),
    }
    if s in named:
        return named[s]
    if s.startswith("#"):
        hx = s[1:]
        if len(hx) == 3:
            hx = "".join(c * 2 for c in hx)
        if len(hx) == 6:
            return (int(hx[0:2], 16), int(hx[2:4], 16), int(hx[4:6], 16))
    if "," in s:
        parts = [p.strip() for p in s.split(",")]
        if len(parts) == 3:
            return tuple(max(0, min(255, int(p))) for p in parts)  # type: ignore[return-value]
    raise ValueError(f"unparseable color: {spec!r}")


# ---------- trim ----------

def cmd_trim(args: argparse.Namespace) -> int:
    as_json = args.json
    input_path = Path(args.input).expanduser().resolve()
    if not input_path.is_file():
        return _emit({"ok": False, "error": f"input not found: {input_path}"}, as_json)

    try:
        img = Image.open(input_path)
    except Exception as e:
        return _emit({"ok": False, "error": f"failed to open image: {e}"}, as_json)

    if img.mode != "RGBA":
        img = img.convert("RGBA")

    alpha = img.getchannel("A")
    threshold = max(0, min(255, int(args.threshold)))

    if threshold <= 0:
        mask = alpha
    else:
        mask = alpha.point(lambda a: 255 if a > threshold else 0)

    bbox = mask.getbbox()
    if bbox is None:
        return _emit({"ok": False, "error": "image is fully transparent under given threshold"}, as_json)

    left, upper, right, lower = bbox
    pad = max(0, int(args.padding))
    if pad > 0:
        w, h = img.size
        left = max(0, left - pad)
        upper = max(0, upper - pad)
        right = min(w, right + pad)
        lower = min(h, lower + pad)

    original_size = img.size
    new_bbox = (left, upper, right, lower)

    if new_bbox == (0, 0, original_size[0], original_size[1]):
        if args.skip_if_noop:
            return _emit({
                "ok": True,
                "output": str(input_path),
                "bbox": list(new_bbox),
                "original_size": list(original_size),
                "new_size": list(original_size),
                "noop": True,
            }, as_json)

    cropped = img.crop(new_bbox)

    output_path = Path(args.output).expanduser().resolve() if args.output else input_path.with_name(f"{input_path.stem}_trimmed.png")
    output_path.parent.mkdir(parents=True, exist_ok=True)

    try:
        cropped.save(output_path, format="PNG")
    except Exception as e:
        return _emit({"ok": False, "error": f"failed to save: {e}"}, as_json)

    return _emit({
        "ok": True,
        "output": str(output_path),
        "bbox": list(new_bbox),
        "original_size": list(original_size),
        "new_size": list(cropped.size),
        "noop": False,
    }, as_json)


# ---------- convert ----------

def cmd_convert(args: argparse.Namespace) -> int:
    as_json = args.json
    input_path = Path(args.input).expanduser().resolve()
    if not input_path.is_file():
        return _emit({"ok": False, "error": f"input not found: {input_path}"}, as_json)

    # Resolve target format from -o suffix if present, else --format, else error.
    target_format: Optional[str] = None
    if args.format:
        target_format = _FORMAT_ALIASES.get(args.format.lower())
        if not target_format:
            return _emit({"ok": False, "error": f"unsupported --format: {args.format}"}, as_json)

    if args.output:
        output_path = Path(args.output).expanduser().resolve()
        if not target_format:
            ext = output_path.suffix.lower().lstrip(".")
            target_format = _FORMAT_ALIASES.get(ext)
            if not target_format:
                return _emit({"ok": False, "error": f"cannot infer format from output suffix: {output_path.suffix!r}. Pass --format."}, as_json)
    else:
        if not target_format:
            return _emit({"ok": False, "error": "either --format or -o must specify the target format"}, as_json)
        # Default: overwrite — place the converted file next to input with the new extension.
        output_path = input_path.with_suffix(_FORMAT_EXTS[target_format])

    try:
        img = Image.open(input_path)
        img.load()
    except Exception as e:
        return _emit({"ok": False, "error": f"failed to open image: {e}"}, as_json)

    # Handle alpha → JPEG (JPEG has no alpha).
    if target_format == "JPEG":
        if img.mode in ("RGBA", "LA", "P"):
            bg_rgb = _parse_color(args.background) if args.background else (255, 255, 255)
            rgba = img.convert("RGBA")
            flat = Image.new("RGB", rgba.size, bg_rgb)
            flat.paste(rgba, mask=rgba.getchannel("A"))
            img = flat
        elif img.mode != "RGB":
            img = img.convert("RGB")

    save_kwargs: dict = {}
    if target_format == "JPEG":
        save_kwargs["quality"] = int(args.quality) if args.quality is not None else 92
        save_kwargs["optimize"] = True
    elif target_format == "WEBP":
        save_kwargs["quality"] = int(args.quality) if args.quality is not None else 90
        save_kwargs["method"] = 6
    elif target_format == "PNG":
        save_kwargs["optimize"] = True

    output_path.parent.mkdir(parents=True, exist_ok=True)

    try:
        img.save(output_path, format=target_format, **save_kwargs)
    except Exception as e:
        return _emit({"ok": False, "error": f"failed to save: {e}"}, as_json)

    # Remove original unless caller said to keep it, and unless the save path is identical.
    removed = False
    if not args.keep_original and input_path != output_path and input_path.exists():
        try:
            input_path.unlink()
            removed = True
        except Exception as e:
            return _emit({"ok": False, "error": f"saved {output_path} but failed to remove original: {e}"}, as_json)

    return _emit({
        "ok": True,
        "output": str(output_path),
        "format": target_format,
        "original_removed": removed,
    }, as_json)


# ---------- resize ----------

def cmd_resize(args: argparse.Namespace) -> int:
    as_json = args.json
    input_path = Path(args.input).expanduser().resolve()
    if not input_path.is_file():
        return _emit({"ok": False, "error": f"input not found: {input_path}"}, as_json)

    try:
        img = Image.open(input_path)
        img.load()
    except Exception as e:
        return _emit({"ok": False, "error": f"failed to open image: {e}"}, as_json)

    orig_w, orig_h = img.size

    if args.scale is not None:
        if args.width is not None or args.height is not None:
            return _emit({"ok": False, "error": "--scale is mutually exclusive with --width/--height"}, as_json)
        if args.scale <= 0:
            return _emit({"ok": False, "error": "--scale must be > 0"}, as_json)
        new_w = max(1, round(orig_w * args.scale))
        new_h = max(1, round(orig_h * args.scale))
    else:
        if args.width is None and args.height is None:
            return _emit({"ok": False, "error": "specify --width, --height, or --scale"}, as_json)
        if args.width is not None and args.height is not None:
            fit = args.fit
            if fit == "stretch":
                new_w, new_h = args.width, args.height
            elif fit == "contain":
                ratio = min(args.width / orig_w, args.height / orig_h)
                new_w = max(1, round(orig_w * ratio))
                new_h = max(1, round(orig_h * ratio))
            elif fit == "cover":
                ratio = max(args.width / orig_w, args.height / orig_h)
                new_w = max(1, round(orig_w * ratio))
                new_h = max(1, round(orig_h * ratio))
            else:
                return _emit({"ok": False, "error": f"unknown --fit: {fit}"}, as_json)
        elif args.width is not None:
            ratio = args.width / orig_w
            new_w = args.width
            new_h = max(1, round(orig_h * ratio))
        else:
            ratio = args.height / orig_h
            new_h = args.height
            new_w = max(1, round(orig_w * ratio))

    resample = _RESAMPLE.get(args.filter.lower())
    if resample is None:
        return _emit({"ok": False, "error": f"unknown --filter: {args.filter}"}, as_json)

    resized = img.resize((new_w, new_h), resample=resample)

    # Default: overwrite input.
    output_path = Path(args.output).expanduser().resolve() if args.output else input_path
    output_path.parent.mkdir(parents=True, exist_ok=True)

    # Pick save format: follow output suffix if meaningful, else keep original format.
    save_format: Optional[str] = None
    ext = output_path.suffix.lower().lstrip(".")
    save_format = _FORMAT_ALIASES.get(ext) or img.format or "PNG"

    # If saving as JPEG but mode has alpha, flatten onto white (resize is not the place to customize bg).
    to_save = resized
    if save_format == "JPEG" and to_save.mode in ("RGBA", "LA", "P"):
        rgba = to_save.convert("RGBA")
        flat = Image.new("RGB", rgba.size, (255, 255, 255))
        flat.paste(rgba, mask=rgba.getchannel("A"))
        to_save = flat

    try:
        to_save.save(output_path, format=save_format)
    except Exception as e:
        return _emit({"ok": False, "error": f"failed to save: {e}"}, as_json)

    return _emit({
        "ok": True,
        "output": str(output_path),
        "original_size": [orig_w, orig_h],
        "new_size": [new_w, new_h],
        "filter": args.filter.lower(),
    }, as_json)


# ---------- parser ----------

def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="cli-image-manipulation",
        description="Local image manipulation CLI (Pillow-based).",
    )
    sub = p.add_subparsers(dest="command", required=True)

    # trim
    trim = sub.add_parser("trim", help="Auto-crop transparent borders from an image with alpha.")
    trim.add_argument("input", help="Input image path (PNG/WEBP/etc with alpha).")
    trim.add_argument("-o", "--output", help="Output PNG path. Default: <input_stem>_trimmed.png. Pass same path as input to overwrite.")
    trim.add_argument("--padding", type=int, default=0, help="Transparent pixels to keep around the content bbox (default: 0).")
    trim.add_argument("--threshold", type=int, default=0, help="Alpha threshold; pixels with alpha <= threshold are treated as transparent (0-255, default: 0).")
    trim.add_argument("--skip-if-noop", action="store_true", help="If the bbox equals the full image, do not rewrite; report noop.")
    trim.add_argument("--json", action="store_true", help="Emit JSON result.")
    trim.set_defaults(func=cmd_trim)

    # convert
    conv = sub.add_parser("convert", help="Change image format. Overwrites input by default (deletes original).")
    conv.add_argument("input", help="Input image path.")
    conv.add_argument("-o", "--output", help="Output path. If omitted, writes next to input with the new extension and removes the original.")
    conv.add_argument("--format", help="Target format: png | jpeg (jpg) | webp. Inferred from -o extension if omitted.")
    conv.add_argument("--quality", type=int, help="JPEG/WEBP quality 1-100 (default: 92 jpeg, 90 webp).")
    conv.add_argument("--background", help="Background color for flattening alpha when converting to JPEG. Accepts #rgb, #rrggbb, 'r,g,b', or names. Default: white.")
    conv.add_argument("--keep-original", action="store_true", help="Do not delete the original input after successful conversion.")
    conv.add_argument("--json", action="store_true", help="Emit JSON result.")
    conv.set_defaults(func=cmd_convert)

    # resize
    rsz = sub.add_parser("resize", help="Resize image. Overwrites input by default.")
    rsz.add_argument("input", help="Input image path.")
    rsz.add_argument("-o", "--output", help="Output path. Default: overwrite input. Output format follows the suffix (png/jpg/webp) or input format if omitted.")
    rsz.add_argument("--width", type=int, help="Target width in pixels.")
    rsz.add_argument("--height", type=int, help="Target height in pixels.")
    rsz.add_argument("--scale", type=float, help="Uniform scale factor (e.g. 0.5, 2). Mutually exclusive with --width/--height.")
    rsz.add_argument("--fit", choices=["stretch", "contain", "cover"], default="stretch", help="When both --width and --height are given: stretch (default, ignore aspect), contain (fit inside, preserve aspect), cover (fill, preserve aspect).")
    rsz.add_argument("--filter", default="lanczos", help="Resample filter: lanczos (default) | bilinear | bicubic | nearest | box | hamming.")
    rsz.add_argument("--json", action="store_true", help="Emit JSON result.")
    rsz.set_defaults(func=cmd_resize)

    return p


def main(argv: Optional[list[str]] = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
