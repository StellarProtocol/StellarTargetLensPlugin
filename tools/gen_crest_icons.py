# Usage: python tools/gen_crest_icons.py Resources
# "Celestial Crest family" overlay glyphs for TargetLens (approved 2026-11-18, _scratch/prototypes/targetlens-overlays-*):
#   crest-diamond-7.png              7 px gold diamond (caption marker; captions don't scale)
#   crest-hair-l/m/r.png             1 px gold hairline: 24 px fade-in, 4 px solid (stretched), 24 px fade-out
#   crest-med-<kind>-<px>.png        rank medallions (gold / silver / bronze / plain) per row size
#   crest-pill-<boss|elite>-<step>.png  BOSS / ELITE pills (English by design: a symbol, not localized) per row size
# Each size is drawn at its own pixel size from an 8x supersampled render (no master scaled down), so edges stay crisp.
# Row-size steps match Plugin.Crest.cs CrestSteps (the overlays' 1.0–2.5 size sliders snap to the nearest step).
import sys, math
from PIL import Image, ImageDraw, ImageFont

OUT = sys.argv[1]
SS = 8
STEPS = [1.0, 1.1, 1.2, 1.35, 1.5, 1.7, 2.0, 2.25, 2.5]
GOLD = (0xd8, 0xb4, 0x6a)


def hexc(h):
    return tuple(int(h[i:i + 2], 16) for i in (1, 3, 5))


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))


def save(im, name):
    im.save(f"{OUT}/{name}", optimize=True)


def diamond():
    n = 7 * SS
    im = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    px = im.load()
    c = n / 2
    for y in range(n):
        for x in range(n):
            if abs(x + 0.5 - c) + abs(y + 0.5 - c) <= c:
                t = ((x + 0.5) + (y + 0.5)) / (2 * n)          # 135deg: #fff3c8 top-left -> gold bottom-right
                px[x, y] = lerp((0xff, 0xf3, 0xc8), GOLD, t) + (255,)
    save(im.resize((7, 7), Image.LANCZOS), "crest-diamond-7.png")


def hairline():
    # CSS: transparent -> gold at 15 %, gold to 85 %, -> transparent; opacity .7. Caps fade over 24 px.
    for name, w, f in (("l", 24, lambda x: x / 23), ("m", 4, lambda x: 1.0), ("r", 24, lambda x: 1 - x / 23)):
        im = Image.new("RGBA", (w, 1))
        for x in range(w):
            im.putpixel((x, 0), GOLD + (round(255 * 0.7 * max(0.0, min(1.0, f(x)))),))
        save(im, f"crest-hair-{name}.png")


MEDALS = {
    "gold":   ((0xff, 0xf2, 0xc0), (0xd8, 0xa5, 0x40)),
    "silver": ((0xff, 0xff, 0xff), (0x9a, 0xa6, 0xb8)),
    "bronze": ((0xff, 0xd2, 0xa8), (0xb0, 0x70, 0x3a)),
    "plain":  ((0x2a, 0x31, 0x50), (0x2a, 0x31, 0x50)),
}


def medallion(kind, px):
    n = px * SS
    hi, lo = MEDALS[kind]
    im = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    p = im.load()
    r = n / 2
    fx, fy = 0.35 * n, 0.30 * n                                  # radial-gradient(circle at 35% 30%, hi, lo 70%)
    for y in range(n):
        for x in range(n):
            d = math.hypot(x + 0.5 - r, y + 0.5 - r)
            if d > r:
                continue
            t = min(1.0, math.hypot(x + 0.5 - fx, y + 0.5 - fy) / (0.7 * n))
            col = lerp(hi, lo, t)
            if d > r - SS:                                       # inset 1 px ring rgba(0,0,0,.35)
                col = lerp(col, (0, 0, 0), 0.35)
            p[x, y] = col + (255,)
    save(im.resize((px, px), Image.LANCZOS), f"crest-med-{kind}-{px}.png")


def pill(kind, step):
    w, h = round(40 * step), round(16 * step)   # 40x16 at 1.0 (30x14 + 8.5 px text was unreadable in game)
    top, bot, txt, label = ((hexc("#ffe6a0"), hexc("#d8a540"), hexc("#2a1a04"), "BOSS") if kind == "boss"
                            else (hexc("#ffc890"), hexc("#e0781e"), hexc("#2a1204"), "ELITE"))
    W, H = w * SS, h * SS
    im = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    grad = Image.new("RGBA", (W, H))
    gp = grad.load()
    for y in range(H):
        c = lerp(top, bot, y / (H - 1)) + (255,)
        for x in range(W):
            gp[x, y] = c
    mask = Image.new("L", (W, H), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, W - 1, H - 1), radius=H / 2, fill=255)
    im.paste(grad, (0, 0), mask)
    im = im.resize((w, h), Image.LANCZOS)
    # Text drawn at the final size (hinted by FreeType), not supersampled-then-shrunk.
    font = ImageFont.truetype("C:/Windows/Fonts/arialbd.ttf", max(8, round(10 * step)))   # bold 10 px at 1.0
    d = ImageDraw.Draw(im)
    bb = d.textbbox((0, 0), label, font=font)
    tw, th = bb[2] - bb[0], bb[3] - bb[1]
    d.text(((w - tw) / 2 - bb[0], (h - th) / 2 - bb[1]), label, font=font, fill=txt + (255,))
    save(im, f"crest-pill-{kind}-{STEPS.index(step)}.png")


diamond()
hairline()
for s in STEPS:
    for k in MEDALS:
        medallion(k, round(18 * s))
    pill("boss", s)
    pill("elite", s)
print("ok")
