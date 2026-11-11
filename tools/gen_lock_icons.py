"""Generate the Nearby Monsters hard-lock padlock glyphs (Resources/lock-<N>.png).

WHITE on transparent — the plugin tints them gold at draw time (ImageElement.Tint). One pre-drawn, pixel-hinted
picture per on-screen size (house rule: never scale one master down): every straight edge sits on a whole-pixel
boundary, so only the shackle arc and the body's corner rounding get anti-aliased (8x supersample, box-filtered down).
All sizes are EVEN so the glyph centres on the pixel grid inside the even-height row (WindowBuilder-Patterns parity).

Run (conda python, from the plugin root):  python tools/gen_lock_icons.py
"""
import os
from PIL import Image, ImageDraw

SIZES = [14, 16, 18, 20, 24, 28, 32, 36]   # covers round(14 * scale) for List size 1.0x .. 2.5x
SS = 8                                     # supersample factor
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Resources")


def even_like(v, n):
    """Round v to an int with the same parity as n, so (n - v) / 2 is a whole pixel (centred, grid-aligned)."""
    r = int(round(v))
    return r if (n - r) % 2 == 0 else r + 1


def draw(n):
    img = Image.new("L", (n * SS, n * SS), 0)
    d = ImageDraw.Draw(img)
    s = lambda v: v * SS   # whole pixels -> supersampled units

    pad = 1 if n >= 18 else 0
    bw = even_like(n * 0.80, n)               # body width
    bh = int(round(n * 0.50))                 # body height
    bx0 = (n - bw) // 2
    by1 = n - pad
    by0 = by1 - bh
    t = max(2, int(round(n * 0.14)))          # shackle stroke
    sw = even_like(n * 0.58, n)               # shackle outer width
    sx0 = (n - sw) // 2
    top = pad

    # Shackle: outer rounded rect minus inner rounded rect (legs run down INTO the body so there's no seam).
    d.rounded_rectangle([s(sx0), s(top), s(sx0 + sw) - 1, s(by0 + t) - 1], radius=s(sw) / 2, fill=255, corners=(True, True, False, False))
    d.rounded_rectangle([s(sx0 + t), s(top + t), s(sx0 + sw - t) - 1, s(by0 + t) - 1],
                        radius=max(0, s(sw) / 2 - s(t)), fill=0, corners=(True, True, False, False))

    # Body: solid block, slight corner rounding from 20px up (1px corners read as mush below that).
    rad = s(max(1, n // 10)) if n >= 20 else 0
    d.rounded_rectangle([s(bx0), s(by0), s(bx0 + bw) - 1, s(by1) - 1], radius=rad, fill=255)

    # Keyhole: a transparent vertical slot, whole-pixel aligned, centred in the body.
    kw = 2 if n < 28 else 4
    kh = max(2, int(round(bh * 0.42)))
    kx0 = (n - kw) // 2
    ky0 = by0 + (bh - kh) // 2
    d.rectangle([s(kx0), s(ky0), s(kx0 + kw) - 1, s(ky0 + kh) - 1], fill=0)

    alpha = img.resize((n, n), Image.BOX)
    out = Image.new("RGBA", (n, n), (255, 255, 255, 0))
    out.putalpha(alpha)
    return out


if __name__ == "__main__":
    for n in SIZES:
        path = os.path.join(OUT, f"lock-{n}.png")
        draw(n).save(path, optimize=True)
        print("wrote", os.path.normpath(path))
