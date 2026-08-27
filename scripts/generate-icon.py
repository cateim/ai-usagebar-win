"""Generates AiUsageBar/Assets/app.ico from code, with no imaging dependency.

The mark is an AI sparkle inside a thin progress ring: the sparkle says "AI",
the ring says "how much of the quota is gone". A plain bar chart was rejected as
too generic, and anything finer than this stops reading at 16px, which is the
size that actually matters for a tray icon.

TrayIconFactory draws the same silhouette in C#, tinted by severity, so the
executable and the notification area match.

Run: python scripts/generate-icon.py
"""

import math
import os
import struct
import zlib

SS = 4  # supersampling factor, for anti-aliasing
SIZES = [16, 24, 32, 48, 64, 128, 256]
OUT = os.path.join("AiUsageBar", "Assets", "app.ico")

PLATE = (0x22, 0x23, 0x26, 255)   # dark rounded plate
SPARK = (0xF2, 0xF3, 0xF5, 255)   # the sparkle

# The ring is split into the app's real severity bands, the same cut-offs
# SeverityRules.ForPct uses, so the icon states the scale it measures on:
# green below 50%, amber to 75%, orange to 90%, red above.
BANDS = [
    (0, 50, (0x4C, 0xAF, 0x50, 255)),    # green
    (50, 75, (0xFF, 0xC1, 0x07, 255)),   # amber
    (75, 90, (0xFF, 0x98, 0x00, 255)),   # orange
    (90, 100, (0xF4, 0x43, 0x36, 255)),  # red
]


def blend(dst, i, color):
    sr, sg, sb, sa = color
    if sa == 255:
        dst[i:i + 4] = bytes((sr, sg, sb, 255))
        return
    a = sa / 255.0
    for k, s in enumerate((sr, sg, sb)):
        dst[i + k] = int(s * a + dst[i + k] * (1 - a))
    dst[i + 3] = max(dst[i + 3], sa)


def rounded_rect(buf, w, x0, y0, x1, y1, r, color):
    for y in range(int(y0), int(y1)):
        for x in range(int(x0), int(x1)):
            cx = cy = None
            if x < x0 + r and y < y0 + r:
                cx, cy = x0 + r, y0 + r
            elif x > x1 - r and y < y0 + r:
                cx, cy = x1 - r, y0 + r
            elif x < x0 + r and y > y1 - r:
                cx, cy = x0 + r, y1 - r
            elif x > x1 - r and y > y1 - r:
                cx, cy = x1 - r, y1 - r
            if cx is not None and (x - cx) ** 2 + (y - cy) ** 2 > r * r:
                continue
            blend(buf, (y * w + x) * 4, color)


def arc(buf, w, h, cx, cy, r_in, r_out, a_start, a_end, color):
    """Ring segment. Angles in degrees, 0 = twelve o'clock, growing clockwise."""
    for y in range(h):
        for x in range(w):
            dx, dy = x - cx, y - cy
            d = math.hypot(dx, dy)
            if not (r_in <= d <= r_out):
                continue
            ang = math.degrees(math.atan2(dx, -dy)) % 360
            if a_start <= ang <= a_end:
                blend(buf, (y * w + x) * 4, color)


def sparkle(buf, w, h, cx, cy, size, color, power=0.62):
    """Four-pointed star with concave sides: the superellipse
    |x|^power + |y|^power <= 1, which is the shape used for the AI sparkle."""
    for y in range(int(cy - size), int(cy + size) + 1):
        for x in range(int(cx - size), int(cx + size) + 1):
            if not (0 <= x < w and 0 <= y < h):
                continue
            dx, dy = abs(x - cx) / size, abs(y - cy) / size
            if dx ** power + dy ** power <= 1.0:
                blend(buf, (y * w + x) * 4, color)


def downsample(src, w, h, factor):
    """Box-filter the supersampled buffer down to its final size."""
    ow, oh = w // factor, h // factor
    out = bytearray(ow * oh * 4)
    n = factor * factor
    for y in range(oh):
        for x in range(ow):
            r = g = b = a = 0
            for dy in range(factor):
                for dx in range(factor):
                    i = ((y * factor + dy) * w + (x * factor + dx)) * 4
                    sa = src[i + 3]
                    r += src[i] * sa
                    g += src[i + 1] * sa
                    b += src[i + 2] * sa
                    a += sa
            o = (y * ow + x) * 4
            if a:
                out[o] = r // a
                out[o + 1] = g // a
                out[o + 2] = b // a
            out[o + 3] = a // n
    return out, ow, oh


def render(size, plate=True):
    w = h = size * SS
    buf = bytearray(w * h * 4)

    if plate:
        rounded_rect(buf, w, 0, 0, w, h, w * 0.22, PLATE)

    cx = cy = w / 2
    r_out, r_in = w * 0.43, w * 0.375

    for start_pct, end_pct, color in BANDS:
        arc(buf, w, h, cx, cy, r_in, r_out,
            start_pct / 100 * 360, end_pct / 100 * 360, color)

    sparkle(buf, w, h, cx, cy, w * 0.30, SPARK)

    return downsample(buf, w, h, SS)


def png(width, height, rgba):
    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    raw = bytearray()
    for y in range(height):
        raw.append(0)  # filter type: none
        raw += rgba[y * width * 4:(y + 1) * width * 4]

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
            + chunk(b"IEND", b""))


def main():
    images = []
    for size in SIZES:
        rgba, w, h = render(size)
        images.append((size, png(w, h, rgba)))
        print(f"  rendered {size}x{size}")

    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    blobs = b""
    for size, data in images:
        dim = 0 if size >= 256 else size  # 0 means 256 in the ICO format
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
        blobs += data

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "wb") as f:
        f.write(header + entries + blobs)
    print(f"wrote {OUT} ({os.path.getsize(OUT)} bytes, {len(images)} sizes)")


if __name__ == "__main__":
    main()
