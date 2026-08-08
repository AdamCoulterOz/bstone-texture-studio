#!/usr/bin/env python3
"""Generate the app icon at every size the browser and OS ask for.

The icon is a 2x2 checker: a texture tile, and a nod to the transparency checkerboard the
studio spends its life keying. Four squares in two colours, edge to edge with no gaps —
an app icon is mostly seen at 16-32px, where anything finer turns to mush and gaps blur into
the fill. An earlier version subdivided the quadrants 1/2x2/3x3/4x4 to say "increasing
resolution"; it was unreadable below 48px.

Kept as code rather than checked-in binaries so a colour can be changed without a design
tool, and so the tiny sizes are rendered directly rather than downsampled from 512.

No third-party imaging library is used: PNG is a container around zlib-compressed scanlines,
and the artwork is rectangles, so both are cheaper to write than to depend on.

    python3 tools/make-icons.py src/TextureStudio.App/wwwroot
"""
import struct
import sys
import zlib
from pathlib import Path

BG = (0x16, 0x18, 0x1C)
SS = 4  # supersampling factor, for antialiased corners

VIOLET = (0xA8, 0x55, 0xF7)
AMBER = (0xF5, 0xB3, 0x01)

# Checker: violet on one diagonal, amber on the other.
QUADRANTS = [(0, 0, VIOLET), (1, 1, VIOLET), (1, 0, AMBER), (0, 1, AMBER)]


def blocks(size, pad):
    """Rectangles to paint, in output-pixel coordinates, as (x0, y0, x1, y1, rgb)."""
    inset = size * (0.15 + pad)
    half = (size - inset * 2) / 2
    return [(inset + qx * half, inset + qy * half,
             inset + (qx + 1) * half, inset + (qy + 1) * half, colour)
            for qx, qy, colour in QUADRANTS]


def render(size, pad):
    """RGBA pixel rows, supersampled so the rounded corners are antialiased."""
    radius = size * 0.22
    rects = [(x0 * SS, y0 * SS, x1 * SS, y1 * SS, c) for x0, y0, x1, y1, c in blocks(size, pad)]
    r_ss, n_ss = radius * SS, size * SS

    def inside_rounded(x, y):
        cx = min(max(x, r_ss), n_ss - r_ss)
        cy = min(max(y, r_ss), n_ss - r_ss)
        return (x - cx) ** 2 + (y - cy) ** 2 <= r_ss ** 2

    rows = []
    for py in range(size):
        row = bytearray()
        for px in range(size):
            acc = [0, 0, 0, 0]
            for sy in range(SS):
                y = py * SS + sy + 0.5
                for sx in range(SS):
                    x = px * SS + sx + 0.5
                    if not inside_rounded(x, y):
                        continue
                    colour = BG
                    for x0, y0, x1, y1, c in rects:
                        if x0 <= x < x1 and y0 <= y < y1:
                            colour = c
                            break
                    acc[0] += colour[0]; acc[1] += colour[1]; acc[2] += colour[2]; acc[3] += 255
            n = SS * SS
            a = acc[3] // n
            # Premultiplied averaging would darken the edge; divide colour by covered samples.
            covered = acc[3] // 255 or 1
            row += bytes((acc[0] // covered, acc[1] // covered, acc[2] // covered, a))
        rows.append(bytes(row))
    return rows


def write_png(path, size, rows):
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    path.write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b""))


def write_svg(path, size=512):
    """Same geometry as vector, for the crisp favicon."""
    r = size * 0.22
    parts = [
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {size} {size}">',
        f'<rect width="{size}" height="{size}" rx="{r:.1f}" fill="#16181c"/>',
    ]
    for x0, y0, x1, y1, (r_, g_, b_) in blocks(size, 0):
        parts.append(f'<rect x="{x0:.1f}" y="{y0:.1f}" width="{x1 - x0:.1f}" '
                     f'height="{y1 - y0:.1f}" fill="#{r_:02x}{g_:02x}{b_:02x}"/>')
    parts.append("</svg>")
    path.write_text("\n".join(parts) + "\n")


def main():
    out = Path(sys.argv[1] if len(sys.argv) > 1 else "src/TextureStudio.App/wwwroot")
    out.mkdir(parents=True, exist_ok=True)
    # A maskable icon is cropped to a circle by the OS, so its art needs a safe-zone margin.
    for name, size, pad in [
        ("favicon-16.png", 16, 0), ("favicon-32.png", 32, 0),
        ("apple-touch-icon.png", 180, 0), ("icon-192.png", 192, 0),
        ("icon-512.png", 512, 0), ("icon-maskable-512.png", 512, 0.09),
    ]:
        write_png(out / name, size, render(size, pad))
        print(f"  {name}  {size}x{size}")
    write_svg(out / "favicon.svg")
    print("  favicon.svg")


if __name__ == "__main__":
    main()
