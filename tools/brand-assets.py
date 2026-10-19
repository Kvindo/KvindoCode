#!/usr/bin/env python3
"""Decontaminate a brand asset that was cropped out of the logo's WHITE page.

    python3 tools/brand-assets.py src/KvindoCode.App/Assets/kvindo-k.png [--check]

The logo artwork is white-backed, so every antialiased edge pixel in a crop is a blend of the ink colour and WHITE.
Alpha alone does not remove it: the edge keeps RGB near 255,255,255, and on a dark background that reads as a pale
halo around the mark — "the brand icon has part of the background in it" (reported repeatedly, 2026-10-18 and
2026-10-19). The previous fix cut the page away with an alpha threshold, which removed the solid page and left
exactly this ring: 206 pixels, 99% of them on the silhouette, alphas 1-12.

The fix is colour decontamination, not another alpha cut: for each pixel whose RGB is whiter than the ink it
represents, the RGB is replaced by the nearest strongly-coloured pixel's colour and the ALPHA IS KEPT. The silhouette
is therefore byte-identical and only the halo changes — a faint ink-coloured edge instead of a white one.

--check exits 1 if any white pixel remains, so it can be used as a guard.
"""
import sys
from collections import deque

try:
    from PIL import Image
except ImportError:                     # pragma: no cover - the tool is run by hand
    sys.exit("Pillow is required: python3 -m pip install --user Pillow")

WHITE = 235                             # a channel at or above this is "page white", not ink
INK = 200                               # a pixel below this is certainly ink (the artwork's own pale lavender is 209)


def decontaminate(path: str) -> int:
    im = Image.open(path).convert("RGBA")
    w, h = im.size
    px = im.load()

    ink = [[px[x, y][3] > 0 and min(px[x, y][:3]) < INK for x in range(w)] for y in range(h)]
    dist = [[10 ** 9] * w for _ in range(h)]
    colour = [[None] * w for _ in range(h)]
    q = deque()
    for y in range(h):
        for x in range(w):
            if ink[y][x]:
                dist[y][x] = 0
                colour[y][x] = px[x, y][:3]
                q.append((x, y))
    if not q:
        return 0
    # 8-connected BFS: each pixel inherits the colour of the nearest ink
    while q:
        x, y = q.popleft()
        for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)):
            nx, ny = x + dx, y + dy
            if 0 <= nx < w and 0 <= ny < h and dist[ny][nx] > dist[y][x] + 1:
                dist[ny][nx] = dist[y][x] + 1
                colour[ny][nx] = colour[y][x]
                q.append((nx, ny))

    changed = 0
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            if a == 0 or min(r, g, b) < WHITE:
                continue                       # transparent, or already ink
            c = colour[y][x]
            if c is None:
                continue
            px[x, y] = (c[0], c[1], c[2], a)
            changed += 1
    im.save(path)
    return changed


def white_pixels(path: str) -> int:
    im = Image.open(path).convert("RGBA")
    px = im.load()
    return sum(1 for y in range(im.height) for x in range(im.width)
               if px[x, y][3] > 0 and min(px[x, y][:3]) >= WHITE)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    target = sys.argv[1]
    if "--check" in sys.argv:
        n = white_pixels(target)
        print(f"{target}: {n} white pixel(s)")
        sys.exit(1 if n else 0)
    before = white_pixels(target)
    fixed = decontaminate(target)
    after = white_pixels(target)
    print(f"{target}: {before} white pixel(s) -> {after} (recoloured {fixed})")
