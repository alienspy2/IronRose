#!/usr/bin/env python3
"""SVG to PNG converter using CairoSVG.

Usage:
    python svg2png.py <input.svg> <output.png> [--width W] [--height H] [--scale S]

Options:
    --width W    Output width in pixels (preserves aspect ratio if height omitted)
    --height H   Output height in pixels (preserves aspect ratio if width omitted)
    --scale S    Scale factor (default: 1.0, ignored if width/height given)
"""

import argparse
import sys
from pathlib import Path

try:
    import cairosvg
except ImportError:
    print("Error: CairoSVG is not installed. Run: pip install cairosvg", file=sys.stderr)
    sys.exit(1)


def main():
    parser = argparse.ArgumentParser(description="Convert SVG to PNG")
    parser.add_argument("input", help="Input SVG file path")
    parser.add_argument("output", help="Output PNG file path")
    parser.add_argument("--width", type=int, default=None, help="Output width in pixels")
    parser.add_argument("--height", type=int, default=None, help="Output height in pixels")
    parser.add_argument("--scale", type=float, default=1.0, help="Scale factor (default: 1.0)")
    args = parser.parse_args()

    input_path = Path(args.input)
    output_path = Path(args.output)

    if not input_path.exists():
        print(f"Error: Input file not found: {input_path}", file=sys.stderr)
        sys.exit(1)

    output_path.parent.mkdir(parents=True, exist_ok=True)

    kwargs = {}
    if args.width:
        kwargs["output_width"] = args.width
    if args.height:
        kwargs["output_height"] = args.height
    if not args.width and not args.height and args.scale != 1.0:
        kwargs["scale"] = args.scale

    cairosvg.svg2png(
        url=str(input_path),
        write_to=str(output_path),
        **kwargs,
    )

    print(f"OK: {output_path} ({output_path.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
