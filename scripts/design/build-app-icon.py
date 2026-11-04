#!/usr/bin/env python3
"""Builds src/SSHExplorer.App/Assets/AppIcon.ico from the editable SVG sources in assets/brand.

Each ICO size is rendered from the variant drawn for that size range (16-24, 32-48, 64-256);
no size is produced by enlarging a smaller bitmap. 16-48 px are stored as 32-bit DIB entries,
64-256 px as PNG entries (both supported by Windows Vista+ and the Win32 resource compiler).
Requires rsvg-convert and Pillow. Usage: python3 scripts/design/build-app-icon.py
"""
import io, struct, subprocess, sys, tempfile, pathlib
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parents[2]
BRAND = ROOT / "assets" / "brand"
OUT = ROOT / "src" / "SSHExplorer.App" / "Assets" / "AppIcon.ico"
PREVIEW = BRAND / "png"
SOURCES = {16: "app-icon-16.svg", 20: "app-icon-16.svg", 24: "app-icon-16.svg",
           32: "app-icon-32.svg", 40: "app-icon-32.svg", 48: "app-icon-32.svg",
           64: "app-icon.svg", 128: "app-icon.svg", 256: "app-icon.svg"}

def render(svg: pathlib.Path, size: int) -> Image.Image:
    with tempfile.TemporaryDirectory() as tmp:
        target = pathlib.Path(tmp) / "icon.png"
        subprocess.run(["rsvg-convert", "-w", str(size), "-h", str(size), str(svg), "-o", str(target)], check=True)
        return Image.open(target).convert("RGBA").copy()

def dib(image: Image.Image) -> bytes:
    w, h = image.size
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    rows = []
    for y in range(h - 1, -1, -1):
        row = bytearray()
        for x in range(w):
            r, g, b, a = image.getpixel((x, y))
            row += bytes((b, g, r, a))
        rows.append(bytes(row))
    mask_stride = ((w + 31) // 32) * 4
    mask = bytes(mask_stride * h)  # alpha channel carries transparency; AND mask all zero
    return header + b"".join(rows) + mask

def main() -> int:
    PREVIEW.mkdir(parents=True, exist_ok=True)
    entries = []
    for size, name in SOURCES.items():
        image = render(BRAND / name, size)
        image.save(PREVIEW / f"app-icon-{size}.png")
        if size <= 48:
            data = dib(image)
        else:
            buffer = io.BytesIO(); image.save(buffer, format="PNG", optimize=True); data = buffer.getvalue()
        entries.append((size, data))
    count = len(entries)
    offset = 6 + 16 * count
    directory = b""; blobs = b""
    for size, data in entries:
        dim = 0 if size >= 256 else size
        directory += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset + len(blobs))
        blobs += data
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_bytes(struct.pack("<HHH", 0, 1, count) + directory + blobs)
    print(f"{OUT.relative_to(ROOT)}: {count} images, {OUT.stat().st_size} bytes")
    return 0

if __name__ == "__main__":
    sys.exit(main())
