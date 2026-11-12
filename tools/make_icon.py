#!/usr/bin/env python3
"""Generates src/Soundsys.App/Assets/soundsys.ico: the two-ring mark (two speakers, one sound).

Pure standard library. Each size is rasterised from signed-distance functions (analytic anti-aliasing),
then packed into an .ico: 32-bit DIB entries up to 64 px and a PNG entry at 256 px.

Usage: python tools/make_icon.py [output.ico]
"""

import math
import os
import struct
import sys
import zlib

SIZES = [16, 24, 32, 48, 64, 256]

# Hi-fi amber that reads on both light and dark taskbars (between the design's light and dark Signal tokens).
AMBER = (0xD9, 0x8A, 0x14)


def coverage(sd):
    """Signed distance (px, negative inside) to pixel coverage 0..1."""
    return min(1.0, max(0.0, 0.5 - sd))


def over(a_top, a_bottom):
    return a_top + a_bottom * (1.0 - a_top)


def render(size):
    """Returns RGBA rows (top to bottom) for one icon size."""
    s = float(size)
    # Ring radius, centre spacing and stroke. Small sizes get relatively thicker strokes so they stay legible.
    stroke = max(1.6, s * (0.085 if size <= 32 else 0.07))
    radius = s * 0.29
    half_gap = s * 0.15
    cy = s / 2.0
    cxa = s / 2.0 - half_gap
    cxb = s / 2.0 + half_gap

    rows = []
    for y in range(size):
        row = bytearray()
        for x in range(size):
            # 2x2 supersampling on top of the analytic edge keeps the lens tips clean at 16 px.
            acc = 0.0
            for oy in (0.25, 0.75):
                for ox in (0.25, 0.75):
                    px = x + ox
                    py = y + oy
                    da = math.hypot(px - cxa, py - cy)
                    db = math.hypot(px - cxb, py - cy)
                    ring_a = coverage(abs(da - radius) - stroke / 2.0)
                    ring_b = coverage(abs(db - radius) - stroke / 2.0)
                    inside_a = coverage(da - radius)
                    inside_b = coverage(db - radius)
                    lens = min(inside_a, inside_b)
                    soft = max(inside_a, inside_b) * 0.22
                    a = over(max(ring_a, ring_b), over(lens, soft))
                    acc += a
            alpha = acc / 4.0
            row += bytes((AMBER[0], AMBER[1], AMBER[2], int(round(alpha * 255))))
        rows.append(bytes(row))
    return rows


def png(size, rows):
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def dib(size, rows):
    """BITMAPINFOHEADER + bottom-up BGRA pixels + 1-bit AND mask (all zero: alpha carries transparency)."""
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    pixels = bytearray()
    for r in reversed(rows):
        for i in range(0, len(r), 4):
            pixels += bytes((r[i + 2], r[i + 1], r[i], r[i + 3]))
    mask_stride = ((size + 31) // 32) * 4
    mask = bytes(mask_stride * size)
    return header + bytes(pixels) + mask


def ico(images):
    count = len(images)
    out = struct.pack("<HHH", 0, 1, count)
    offset = 6 + 16 * count
    entries = b""
    blobs = b""
    for size, data in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)
    return out + entries + blobs


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    target = sys.argv[1] if len(sys.argv) > 1 else os.path.join(root, "src", "Soundsys.App", "Assets", "soundsys.ico")
    images = []
    for size in SIZES:
        rows = render(size)
        images.append((size, png(size, rows) if size >= 256 else dib(size, rows)))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as f:
        f.write(ico(images))
    print(f"wrote {target} ({', '.join(str(s) for s in SIZES)} px)")


if __name__ == "__main__":
    main()
