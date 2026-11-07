"""Turns a generated UI picture into a clean 9-slice PNG (standard library only).

Steps: remove the plain background (flood fill from the border) -> crop -> mirror the top-left
quarter to all four corners (perfect symmetry) -> make the edges uniform along their length and
the centre flat -> scale to the target size -> write RGBA PNG + margins.

    python tools/ui/nine_slice.py SRC.png OUT.png --size 512x512 --margins 96,96,96,96 [--no-slice]

`--no-slice` only removes the background, crops and scales. `--tail` keeps only the part below
the widest body (a speech-bubble tail drawn together with its bubble). `--tolerance` sets how close
to the background colour a pixel must be to be removed (lower for light elements).
"""

from __future__ import annotations

import argparse
import json
import struct
import zlib
from collections import deque
from pathlib import Path

Pixel = tuple[int, int, int, int]


# --- minimal PNG io (8-bit RGB/RGBA, non-interlaced) -------------------------------------------


def read_png(path: Path) -> tuple[int, int, list[list[Pixel]]]:
    data = path.read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a PNG")
    pos, idat, width = 8, b"", 0
    height = color = 0
    while pos < len(data):
        (length,) = struct.unpack(">I", data[pos : pos + 4])
        kind, body = data[pos + 4 : pos + 8], data[pos + 8 : pos + 8 + length]
        if kind == b"IHDR":
            width, height, depth, color, _, _, interlace = struct.unpack(">IIBBBBB", body)
            if depth != 8 or color not in (2, 6) or interlace:
                raise ValueError("only 8-bit RGB/RGBA non-interlaced PNGs are supported")
        elif kind == b"IDAT":
            idat += body
        pos += 12 + length
    channels = 3 if color == 2 else 4
    raw = zlib.decompress(idat)
    stride = width * channels
    rows: list[list[Pixel]] = []
    prev = bytearray(stride)
    i = 0
    for _ in range(height):
        ftype, line = raw[i], bytearray(raw[i + 1 : i + 1 + stride])
        i += 1 + stride
        for x in range(stride):
            a = line[x - channels] if x >= channels else 0
            b = prev[x]
            c = prev[x - channels] if x >= channels else 0
            if ftype == 1:
                line[x] = (line[x] + a) & 255
            elif ftype == 2:
                line[x] = (line[x] + b) & 255
            elif ftype == 3:
                line[x] = (line[x] + (a + b) // 2) & 255
            elif ftype == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        prev = line
        rows.append(
            [
                (line[k], line[k + 1], line[k + 2], line[k + 3] if channels == 4 else 255)
                for k in range(0, stride, channels)
            ]
        )
    return width, height, rows


def write_png(path: Path, rows: list[list[Pixel]]) -> None:
    height, width = len(rows), len(rows[0])
    raw = b"".join(b"\x00" + bytes(v for px in row for v in px) for row in rows)

    def chunk(kind: bytes, body: bytes) -> bytes:
        return (
            struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))
        )

    header = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    path.write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", header)
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )


# --- processing --------------------------------------------------------------------------------


def remove_background(rows: list[list[Pixel]], tolerance: int = 28) -> list[list[Pixel]]:
    h, w = len(rows), len(rows[0])
    border = [rows[0][x] for x in range(w)] + [rows[h - 1][x] for x in range(w)]
    border += [rows[y][0] for y in range(h)] + [rows[y][w - 1] for y in range(h)]
    bg = tuple(sorted(p[c] for p in border)[len(border) // 2] for c in range(3))

    def is_bg(p: Pixel) -> bool:
        return max(abs(p[0] - bg[0]), abs(p[1] - bg[1]), abs(p[2] - bg[2])) <= tolerance

    out = [list(r) for r in rows]
    seen = [[False] * w for _ in range(h)]
    queue = deque((x, y) for x in range(w) for y in (0, h - 1)) + deque(
        (x, y) for y in range(h) for x in (0, w - 1)
    )
    if is_bg(rows[h // 2][w // 2]):
        queue.append((w // 2, h // 2))  # enclosed open centre (e.g. a frame without fill)
    while queue:
        x, y = queue.popleft()
        if seen[y][x] or not is_bg(rows[y][x]):
            continue
        seen[y][x] = True
        out[y][x] = (0, 0, 0, 0)
        for nx, ny in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
            if 0 <= nx < w and 0 <= ny < h and not seen[ny][nx]:
                queue.append((nx, ny))
    return out


def tail_only(rows: list[list[Pixel]]) -> list[list[Pixel]]:
    """Keep the rows below the bubble body (opaque width under 40 % of the widest row)."""
    widths = [sum(1 for p in r if p[3]) for r in rows]
    widest = max(widths)
    body_end = max(i for i, wdt in enumerate(widths) if wdt >= 0.4 * widest)
    return [list(r) for r in rows[body_end + 1 :]] or [list(rows[-1])]


def crop(rows: list[list[Pixel]]) -> list[list[Pixel]]:
    ys = [y for y, r in enumerate(rows) if any(p[3] for p in r)]
    xs = [x for x in range(len(rows[0])) if any(r[x][3] for r in rows)]
    return [r[min(xs) : max(xs) + 1] for r in rows[min(ys) : max(ys) + 1]]


def symmetrize(rows: list[list[Pixel]]) -> list[list[Pixel]]:
    h, w = len(rows), len(rows[0])
    hh, hw = (h + 1) // 2, (w + 1) // 2
    out = [[(0, 0, 0, 0)] * w for _ in range(h)]
    for y in range(hh):
        for x in range(hw):
            p = rows[y][x]
            out[y][x] = out[y][w - 1 - x] = out[h - 1 - y][x] = out[h - 1 - y][w - 1 - x] = p
    return out


def scale(rows: list[list[Pixel]], width: int, height: int) -> list[list[Pixel]]:
    """Box-filter resample (area average, alpha-weighted), good for downscaling."""
    sh, sw = len(rows), len(rows[0])
    out: list[list[Pixel]] = []
    for ty in range(height):
        y0, y1 = ty * sh / height, (ty + 1) * sh / height
        row: list[Pixel] = []
        for tx in range(width):
            x0, x1 = tx * sw / width, (tx + 1) * sw / width
            acc = [0.0, 0.0, 0.0, 0.0]
            total = 0.0
            for y in range(int(y0), min(sh, int(y1) + 1)):
                wy = min(y + 1, y1) - max(y, y0)
                if wy <= 0:
                    continue
                for x in range(int(x0), min(sw, int(x1) + 1)):
                    wx = min(x + 1, x1) - max(x, x0)
                    if wx <= 0:
                        continue
                    p = rows[y][x]
                    weight = wx * wy
                    a = p[3] * weight
                    acc[0] += p[0] * a
                    acc[1] += p[1] * a
                    acc[2] += p[2] * a
                    acc[3] += a
                    total += weight
            alpha = acc[3] / total if total else 0
            if acc[3] > 0:
                row.append(
                    (
                        round(acc[0] / acc[3]),
                        round(acc[1] / acc[3]),
                        round(acc[2] / acc[3]),
                        round(alpha),
                    )
                )
            else:
                row.append((0, 0, 0, 0))
        out.append(row)
    return out


def fit(rows: list[list[Pixel]], width: int, height: int) -> list[list[Pixel]]:
    """Scale keeping the aspect ratio, centred, attached at the top (where the tail joins)."""
    sh, sw = len(rows), len(rows[0])
    factor = min(width / sw, height / sh)
    tw, th = max(1, round(sw * factor)), max(1, round(sh * factor))
    scaled = scale(rows, tw, th)
    out = [[(0, 0, 0, 0)] * width for _ in range(height)]
    ox = (width - tw) // 2
    for y in range(th):
        out[y][ox : ox + tw] = scaled[y]
    return out


def uniform_slices(
    rows: list[list[Pixel]], margins: tuple[int, int, int, int]
) -> list[list[Pixel]]:
    """Edges uniform along their length, centre flat (stretchable without distortion)."""
    left, top, right, bottom = margins
    h, w = len(rows), len(rows[0])
    cx, cy = w // 2, h // 2
    out = [list(r) for r in rows]
    for y in range(h):
        for x in range(left, w - right):
            if y < top or y >= h - bottom:
                out[y][x] = rows[y][cx]  # top/bottom edge: repeat the middle column
    for x in range(w):
        for y in range(top, h - bottom):
            if x < left or x >= w - right:
                out[y][x] = rows[cy][x]  # left/right edge: repeat the middle row
    fill = rows[cy][cx]
    for y in range(top, h - bottom):
        for x in range(left, w - right):
            out[y][x] = fill
    return out


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("src", type=Path)
    parser.add_argument("out", type=Path)
    parser.add_argument("--size", required=True, help="WIDTHxHEIGHT")
    parser.add_argument(
        "--margins", default="0,0,0,0", help="left,top,right,bottom in output pixels"
    )
    parser.add_argument("--no-slice", action="store_true")
    parser.add_argument("--tail", action="store_true")
    parser.add_argument("--tolerance", type=int, default=28)
    args = parser.parse_args(argv)
    width, height = (int(v) for v in args.size.lower().split("x"))
    margins = tuple(int(v) for v in args.margins.split(","))
    assert len(margins) == 4
    _, _, rows = read_png(args.src)
    rows = crop(remove_background(rows, args.tolerance))
    if args.tail:
        rows = crop(tail_only(rows))
    if not args.no_slice and not args.tail:
        rows = symmetrize(rows)
    rows = fit(rows, width, height) if args.tail else scale(rows, width, height)
    if not args.no_slice and not args.tail:
        rows = uniform_slices(rows, margins)  # type: ignore[arg-type]
    args.out.parent.mkdir(parents=True, exist_ok=True)
    write_png(args.out, rows)
    meta = {
        "source": args.src.name,
        "size": [width, height],
        "margins": dict(zip(("left", "top", "right", "bottom"), margins, strict=True)),
        "nine_slice": not (args.no_slice or args.tail),
    }
    args.out.with_suffix(".json").write_text(
        json.dumps(meta, indent=2) + "\n", encoding="utf-8", newline="\n"
    )


if __name__ == "__main__":
    main()
