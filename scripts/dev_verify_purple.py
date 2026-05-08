#!/usr/bin/env python3
"""
Color-agnostic per-eye disc verifier for Quest stereo screencaps.

Reads a stereo screencap PNG (Quest's full HMD framebuffer, left+right eye
side by side) and checks the AVERAGE RGB at the center of each eye half
against an expected color. Per-eye expected colors can differ (useful for
stereo asymmetry tests); default is the same color for both eyes.

Usage:
  dev_verify_purple.py [--left NAME-OR-RGB] [--right NAME-OR-RGB]
                       [--tolerance N] [--coverage P] [PATH]

  --left   COLOR     expected left-eye color (default: yellow)
  --right  COLOR     expected right-eye color (default: same as --left)
  --tolerance N      max per-channel RGB delta (default: 60)
  --coverage P       optional: also require at least P% coverage in each eye
                     half (default: 0 = skip coverage check, only check center)
  PATH               PNG path (default: /tmp/quest_latest_shot.png)

  COLOR forms:
    name           e.g.  yellow, purple, red, green, blue, cyan, magenta,
                         orange, white, black
    R,G,B          e.g.  255,255,0
    #RRGGBB        e.g.  #FFFF00

Exit code 0 = PASS (both eye centers match within tolerance).
"""
from __future__ import annotations
import argparse
import sys
from pathlib import Path

try:
    from PIL import Image
except ImportError:
    print("FAIL: PIL/Pillow not installed. Run: pip3 install --break-system-packages --user Pillow")
    sys.exit(2)


COLORS = {
    "yellow":  (255, 255, 0),
    "purple":  (153, 0, 217),
    "red":     (255, 0, 0),
    "green":   (0, 200, 0),
    "blue":    (0, 0, 255),
    "cyan":    (0, 255, 255),
    "magenta": (255, 0, 255),
    "orange":  (255, 128, 0),
    "white":   (255, 255, 255),
    "black":   (0, 0, 0),
}


def parse_color(spec: str) -> tuple[int, int, int]:
    spec = spec.strip().lower()
    if spec in COLORS:
        return COLORS[spec]
    if spec.startswith("#") and len(spec) == 7:
        return tuple(int(spec[i:i+2], 16) for i in (1, 3, 5))  # type: ignore
    parts = spec.split(",")
    if len(parts) == 3:
        return tuple(int(p) for p in parts)  # type: ignore
    raise ValueError(f"unrecognized color spec: {spec!r}")


def sample_center_rgb(img: Image.Image, cx: int, cy: int, half: int = 96) -> tuple[int, int, int]:
    crop = img.crop((cx - half, cy - half, cx + half, cy + half))
    px = crop.load()
    cw, ch = crop.size
    rs = gs = bs = n = 0
    for y in range(0, ch, 4):
        for x in range(0, cw, 4):
            r, g, b = px[x, y][:3]
            rs += r; gs += g; bs += b; n += 1
    return (rs // n, gs // n, bs // n) if n else (0, 0, 0)


def is_match(rgb: tuple[int, int, int], target: tuple[int, int, int], tol: int) -> bool:
    return all(abs(c - t) <= tol for c, t in zip(rgb, target))


def coverage_pct(img: Image.Image, target: tuple[int, int, int], tol: int) -> float:
    px = img.load()
    w, h = img.size
    hits = total = 0
    for y in range(0, h, 8):
        for x in range(0, w, 8):
            r, g, b = px[x, y][:3]
            total += 1
            if all(abs(c - t) <= tol for c, t in zip((r, g, b), target)):
                hits += 1
    return (hits / total * 100) if total else 0.0


def main() -> int:
    ap = argparse.ArgumentParser(add_help=True)
    ap.add_argument("--left", default="yellow")
    ap.add_argument("--right", default=None)
    ap.add_argument("--tolerance", type=int, default=60)
    ap.add_argument("--coverage", type=float, default=0.0)
    ap.add_argument("path", nargs="?", default="/tmp/quest_latest_shot.png")
    args = ap.parse_args()

    if args.right is None:
        args.right = args.left

    try:
        target_l = parse_color(args.left)
        target_r = parse_color(args.right)
    except ValueError as e:
        print(f"FAIL: {e}")
        return 2

    p = Path(args.path)
    if not p.is_file():
        print(f"FAIL: screencap not found at {p}")
        return 1

    img = Image.open(p).convert("RGB")
    w, h = img.size
    print(f"image: {w} x {h}, path: {p}")
    print(f"expected: left={args.left}{target_l}  right={args.right}{target_r}  tolerance=±{args.tolerance}")

    left_half = img.crop((0, 0, w // 2, h))
    right_half = img.crop((w // 2, 0, w, h))
    lw, lh = left_half.size
    rw, rh = right_half.size

    avg_l = sample_center_rgb(left_half, lw // 2, lh // 2)
    avg_r = sample_center_rgb(right_half, rw // 2, rh // 2)
    match_l = is_match(avg_l, target_l, args.tolerance)
    match_r = is_match(avg_r, target_r, args.tolerance)
    print(f"left  eye center 192x192 avg RGB: {avg_l}  -> {'MATCH' if match_l else 'NO MATCH'}")
    print(f"right eye center 192x192 avg RGB: {avg_r}  -> {'MATCH' if match_r else 'NO MATCH'}")

    cov_l = cov_r = -1.0
    if args.coverage > 0:
        cov_l = coverage_pct(left_half, target_l, args.tolerance)
        cov_r = coverage_pct(right_half, target_r, args.tolerance)
        print(f"left  eye coverage: {cov_l:.2f}%  (need >= {args.coverage:.2f}%)")
        print(f"right eye coverage: {cov_r:.2f}%  (need >= {args.coverage:.2f}%)")

    cov_ok = (cov_l >= args.coverage) and (cov_r >= args.coverage) if args.coverage > 0 else True
    if match_l and match_r and cov_ok:
        print("PASS")
        return 0

    failures = []
    if not match_l: failures.append(f"left eye center {avg_l} != target {target_l}")
    if not match_r: failures.append(f"right eye center {avg_r} != target {target_r}")
    if not cov_ok:  failures.append(f"coverage below {args.coverage:.2f}%")
    print("FAIL: " + "; ".join(failures))
    return 1


if __name__ == "__main__":
    sys.exit(main())
