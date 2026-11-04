#!/usr/bin/env python3
"""Renders the review sheet for IconCatalog vector icons (and the app mark sizes).

Parses src/SSHExplorer.App/IconCatalog.cs (single source of path data), draws each vector at
16, 20, 24 and 32 DIP on light and dark surfaces with the same 1 DIP stroke rule as VectorIcon,
and writes docs/design/icon-sheet.svg + .png. System glyphs (Segoe Fluent Icons) are listed by
code point only: that font is a Windows component and is rendered in the Windows CI gallery.
Requires rsvg-convert.
"""
import pathlib, re, subprocess, html
ROOT = pathlib.Path(__file__).resolve().parents[2]
SRC = (ROOT / "src/SSHExplorer.App/IconCatalog.cs").read_text(encoding="utf-8")
OUT = ROOT / "docs/design"; OUT.mkdir(parents=True, exist_ok=True)
vectors = re.findall(r'new\("([a-z0-9-]+)", "([^"]+)", "SSH Explorer vector", null, "([^"]+)"\)', SRC)
glyphs = re.findall(r'new\("([a-z0-9-]+)", "([^"]+)", "(Segoe Fluent Icons U\+[0-9A-F]{4} \w+)"', SRC)
sizes = [16, 20, 24, 32]
col_w = 360; row_h = 52; top = 70
width = 40 + col_w * 2; height = top + row_h * len(vectors) + 60 + 18 * ((len(glyphs) + 1) // 2) + 280
parts = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" font-family="Segoe UI, DejaVu Sans, sans-serif">',
         f'<rect width="{width}" height="{height}" fill="#ffffff"/>',
         '<text x="20" y="32" font-size="18" font-weight="600" fill="#1a1a1a">SSH Explorer — командные иконки (вектор, сетка 16 DIP, штрих 1 DIP)</text>',
         '<text x="20" y="54" font-size="12" fill="#626262">Слева светлая поверхность, справа тёмная; размеры 16 / 20 / 24 / 32 DIP при 100 %.</text>']
for i, (key, purpose, path) in enumerate(vectors):
    y = top + i * row_h
    for side, (bg, ink, label) in enumerate([("#f3f3f3", "#1a1a1a", "#626262"), ("#202020", "#f4f4f4", "#b7b7b7")]):
        x0 = 20 + side * col_w
        parts.append(f'<rect x="{x0}" y="{y}" width="{col_w - 10}" height="{row_h - 6}" rx="6" fill="{bg}"/>')
        x = x0 + 10
        for s in sizes:
            scale = s / 16; stroke = (1.25 if s >= 19 else 1.0) / scale
            parts.append(f'<g transform="translate({x},{y + (row_h - 6 - s) / 2}) scale({scale})"><path d="{path}" fill="none" stroke="{ink}" stroke-width="{stroke:.4f}" stroke-linecap="round" stroke-linejoin="round"/></g>')
            x += s + 12
        parts.append(f'<text x="{x + 4}" y="{y + 20}" font-size="12" fill="{ink}">{html.escape(key)}</text>')
        parts.append(f'<text x="{x + 4}" y="{y + 36}" font-size="10" fill="{label}">{html.escape(purpose[:40])}</text>')
y = top + len(vectors) * row_h + 30
parts.append(f'<text x="20" y="{y}" font-size="14" font-weight="600" fill="#1a1a1a">Системные глифы Segoe Fluent Icons (отрисовываются Windows; проверка — CI-галерея)</text>')
for i, (key, purpose, source) in enumerate(glyphs):
    gx = 20 + (i % 2) * col_w; gy = y + 22 + (i // 2) * 18
    parts.append(f'<text x="{gx}" y="{gy}" font-size="11" fill="#1a1a1a">{html.escape(key)} — {html.escape(source.replace("Segoe Fluent Icons ", ""))}</text>')
y = y + 30 + 18 * ((len(glyphs) + 1) // 2)
parts.append(f'<text x="20" y="{y}" font-size="14" font-weight="600" fill="#1a1a1a">Знак приложения: варианты 16–24 / 32–48 / 64–256</text>')
x = 20
for s, src in [(16, "app-icon-16.svg"), (24, "app-icon-16.svg"), (32, "app-icon-32.svg"), (48, "app-icon-32.svg"), (64, "app-icon.svg"), (96, "app-icon.svg")]:
    body = (ROOT / "assets/brand" / src).read_text(encoding="utf-8")
    inner = re.sub(r'^.*?<svg[^>]*>|</svg>\s*$', '', body, flags=re.S)
    vb = re.search(r'viewBox="0 0 (\d+) (\d+)"', body).group(1)
    for bg, dy in [("#f3f3f3", 14), ("#202020", 14 + 124)]:
        parts.append(f'<rect x="{x - 6}" y="{y + dy - 6}" width="{s + 12}" height="{s + 12}" rx="6" fill="{bg}"/>')
        parts.append(f'<svg x="{x}" y="{y + dy}" width="{s}" height="{s}" viewBox="0 0 {vb} {vb}">{inner}</svg>')
    x += s + 24
parts.append('</svg>')
svg = OUT / "icon-sheet.svg"; svg.write_text("\n".join(parts), encoding="utf-8")
subprocess.run(["rsvg-convert", "-z", "2", str(svg), "-o", str(OUT / "icon-sheet.png")], check=True)
print(svg.relative_to(ROOT), (OUT / "icon-sheet.png").relative_to(ROOT), len(vectors), "vectors", len(glyphs), "glyphs")
