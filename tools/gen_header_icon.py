"""Generate the Settings header glyph: a white crosshair (ring + centre dot + 4 tick marks) -> Resources/header-<N>.png.

WHITE on transparent: it sits on the live theme-accent icon tile (art can't follow the theme, so the colour lives in the
tile). One pre-drawn, pixel-hinted picture per size (house rule: never scale one master down): every size is EVEN and
every stroke width is EVEN, so the tick marks (centred on n/2) land on whole-pixel boundaries and stay crisp; only the
ring and the dot are anti-aliased (8x supersample, box-filtered down). Geometry follows the approved prototype's 24-unit
SVG (targetlens-settings.html): ring r=8 stroke 2.2, dot r=2.6, ticks 1..6 / 18..23 on both axes.
The Settings header draws the 24 px picture (40 px tile, 8 px all round); the other sizes are the house set.

Run (conda python, from the plugin root):  python tools/gen_header_icon.py
"""
import os
from PIL import Image, ImageDraw

SIZES = [16, 24, 32, 64, 128]
SS = 8
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Resources")


def draw(n):
    s = n / 24.0
    img = Image.new("L", (n * SS, n * SS), 0)
    d = ImageDraw.Draw(img)
    c = n * SS / 2.0
    t = max(2, 2 * round(s))                # even stroke -> ticks centred on n/2 sit on whole pixels
    r_out = round(9 * s)                    # ring: outer radius (prototype r=8 + half its 2.2 stroke)
    r_in = r_out - t
    d.ellipse([c - r_out * SS, c - r_out * SS, c + r_out * SS - 1, c + r_out * SS - 1], fill=255)
    d.ellipse([c - r_in * SS, c - r_in * SS, c + r_in * SS - 1, c + r_in * SS - 1], fill=0)

    dot = 2.6 * s                           # centre dot
    d.ellipse([c - dot * SS, c - dot * SS, c + dot * SS - 1, c + dot * SS - 1], fill=255)

    a, b = max(0, round(1 * s)), round(6 * s)   # tick span from the edge (prototype 1..6)
    h0, h1 = n // 2 - t // 2, n // 2 + t // 2   # tick thickness band, whole pixels around the centre
    for (x0, y0, x1, y1) in [
        (h0, a, h1, b), (h0, n - b, h1, n - a),     # top / bottom
        (a, h0, b, h1), (n - b, h0, n - a, h1),     # left / right
    ]:
        d.rectangle([x0 * SS, y0 * SS, x1 * SS - 1, y1 * SS - 1], fill=255)

    alpha = img.resize((n, n), Image.BOX)
    out = Image.new("RGBA", (n, n), (255, 255, 255, 0))
    out.putalpha(alpha)
    return out


if __name__ == "__main__":
    for n in SIZES:
        path = os.path.join(OUT, f"header-{n}.png")
        draw(n).save(path, optimize=True)
        print("wrote", os.path.normpath(path))
