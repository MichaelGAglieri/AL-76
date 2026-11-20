#!/usr/bin/env python3
"""AL-76's icon without image libraries: a cyan ring with a bright core on a dark disc, like the sphere.
Writes the MSIX logos (assets/*.png) and AL-76.ico (PNG images inside, 16-256 px) for the exe and the tray.
    python3 -I packaging/make_icons.py
"""
import math, os, struct, zlib

HERE = os.path.dirname(os.path.abspath(__file__))
ACCENT = (0x4F, 0xDB, 0xFF)
DARK = (0x0B, 0x12, 0x20)


def pixel(x, y, size, disc):
    """Colour and alpha at (x, y) in [0, size), sampled 4x4 for smooth edges."""
    r = g = b = a = 0.0
    for sy in range(4):
        for sx in range(4):
            px = (x + (sx + 0.5) / 4) / size * 2 - 1
            py = (y + (sy + 0.5) / 4) / size * 2 - 1
            d = math.hypot(px, py)
            cr, cg, cb, ca = 0, 0, 0, 0.0
            if disc and d <= 0.98:
                cr, cg, cb, ca = *DARK, 1.0
            ring = abs(d - 0.66) < 0.11
            glow = max(0.0, 1 - d / 0.30)
            if ring:
                cr, cg, cb, ca = *ACCENT, 1.0
            elif glow > 0:
                t = glow ** 1.5
                base = (cr, cg, cb) if ca else DARK
                cr, cg, cb = (base[i] * (1 - t) + (255 if i else 220, 250, 255)[i] * t for i in range(3))
                ca = max(ca, min(1.0, t * 1.6) if not disc else 1.0)
            r += cr * ca; g += cg * ca; b += cb * ca; a += ca
    if a == 0:
        return (0, 0, 0, 0)
    return (round(r / a), round(g / a), round(b / a), round(a / 16 * 255))


def png(width, height, disc=True, pad=0.0):
    """A square icon drawn inside a width x height canvas (wide logos keep the icon centred)."""
    side = int(min(width, height) * (1 - pad))
    ox, oy = (width - side) // 2, (height - side) // 2
    rows = []
    for y in range(height):
        row = bytearray([0])
        for x in range(width):
            if ox <= x < ox + side and oy <= y < oy + side:
                row += bytes(pixel(x - ox, y - oy, side, disc))
            else:
                row += bytes((0, 0, 0, 0))
        rows.append(bytes(row))
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(b"".join(rows), 9)) + chunk(b"IEND", b""))


def main():
    logos = {
        "StoreLogo.png": (50, 50, 0.0), "Square44x44Logo.png": (44, 44, 0.0), "Square150x150Logo.png": (150, 150, 0.2),
        "Wide310x150Logo.png": (310, 150, 0.2), "Square44x44Logo.targetsize-256.png": (256, 256, 0.0),
    }
    for name, (w, h, pad) in logos.items():
        with open(os.path.join(HERE, "assets", name), "wb") as f:
            f.write(png(w, h, pad=pad))
    sizes = [16, 24, 32, 48, 64, 256]
    images = [png(s, s) for s in sizes]
    header = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    entries = b""
    for s, data in zip(sizes, images):
        entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(os.path.join(HERE, "..", "src", "AL76.App", "AL-76.ico"), "wb") as f:
        f.write(header + entries + b"".join(images))
    print("icone scritte")


if __name__ == "__main__":
    main()
