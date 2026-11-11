"""Generate the Target Effects header glyph: a white filled 5-point star -> Resources/star-<N>.png.

WHITE on transparent: it sits on the live theme-accent icon tile (art can't follow the theme, so the colour lives in the
tile). One pre-drawn picture per size (house rule: never scale one master down). Geometry is the approved prototype's
24-unit SVG path (targetlens-select-effects.html): tips (12,3) (20.7,9.3) (17.4,19.5) (6.6,19.5) (3.3,9.3), inner
corners between them. Hinting: every size is EVEN and the star is mirror-symmetric about x = n/2, so the top tip and the
vertical axis sit on the pixel grid; the top tip and the two lower tips are snapped to whole pixels at each size so the
flat bottom edge and the apex stay crisp. Diagonal edges are anti-aliased (8x supersample, box-filtered down).
The window header draws the 24 px picture (40 px tile, 8 px all round); the other sizes are the house set.

Run (conda python, from the plugin root):  python tools/gen_star_icon.py
"""
import os
from PIL import Image, ImageDraw

SIZES = [16, 24, 32, 64, 128]
SS = 8
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Resources")

# Prototype path, 24-unit grid, clockwise from the top tip.
STAR = [(12, 3), (14.6, 8.6), (20.7, 9.3), (16.2, 13.5), (17.4, 19.5),
        (12, 16.6), (6.6, 19.5), (7.8, 13.5), (3.3, 9.3), (9.4, 8.6)]
SNAP_Y = {0, 4, 6}   # the apex and the two lower tips: whole-pixel rows


def draw(n):
    s = n / 24.0
    pts = []
    for i, (x, y) in enumerate(STAR):
        px, py = x * s, y * s
        if i in SNAP_Y:
            py = round(py)
        pts.append((px * SS, py * SS))
    img = Image.new("L", (n * SS, n * SS), 0)
    ImageDraw.Draw(img).polygon(pts, fill=255)
    alpha = img.resize((n, n), Image.BOX)
    out = Image.new("RGBA", (n, n), (255, 255, 255, 0))
    out.putalpha(alpha)
    return out


if __name__ == "__main__":
    for n in SIZES:
        path = os.path.join(OUT, f"star-{n}.png")
        draw(n).save(path, optimize=True)
        print("wrote", os.path.normpath(path))
