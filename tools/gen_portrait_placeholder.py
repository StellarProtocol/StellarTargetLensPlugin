# Usage: python tools/gen_portrait_placeholder.py Resources
# Generic monster silhouette drawn inside the portrait disc when a monster has no game portrait (design "A", approved
# 2026-11-20). Source shape = a 32x32 viewBox: rounded head with two pointed ears/horns
#   M6 4l5 7h10l5-7-1 10c2 2 3 5 3 8 0 5-5 8-12 8S4 27 4 22c0-3 1-6 3-8z
# filled #c8d4f0 at ~85 % alpha, two eye holes (r 2.2 at (12,19) / (20,19)) punched out so the disc shows through.
# Pre-drawn PER SIZE (house rule: never one master scaled down): one PNG per portrait-disc size 44 / 66 / 88 (Slim /
# Medium / Large), the same canvas as portrait-disc-<px>.png. Per size the shape is scaled so its height is ~65 % of the
# disc, its bounding box is snapped to whole pixels (crisp outer edges) and the eye centres to pixel centres, then it is
# rasterised at 8x and box-downsampled (antialiased edges).
import sys
from PIL import Image, ImageDraw

out = sys.argv[1]
FILL = (0xc8, 0xd4, 0xf0, round(0.85 * 255))


def cubic(p0, p1, p2, p3, n=24):
    pts = []
    for i in range(1, n + 1):
        t = i / n; u = 1 - t
        pts.append((u * u * u * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t * t * t * p3[0],
                    u * u * u * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t * t * t * p3[1]))
    return pts


# The path above, made absolute (viewBox units).
OUTLINE = [(6, 4), (11, 11), (21, 11), (26, 4), (25, 14)]
OUTLINE += cubic((25, 14), (27, 16), (28, 19), (28, 22))
OUTLINE += cubic((28, 22), (28, 27), (23, 30), (16, 30))
OUTLINE += cubic((16, 30), (9, 30), (4, 27), (4, 22))      # S = reflected control point (9,30)
OUTLINE += cubic((4, 22), (4, 19), (5, 16), (7, 14))
X0, X1, Y0, Y1 = 4, 28, 4, 30                              # shape bounds in viewBox units
EYES = [(12, 19), (20, 19)]
EYE_R = 2.2

for size in (44, 66, 88):
    # Pixel box of the shape: height ~65 % of the disc, width by aspect; both whole px, centred on whole/half px.
    h = round(size * 0.65)
    w = round(h * (X1 - X0) / (Y1 - Y0))
    left = (size - w) // 2
    top = (size - h) // 2
    sx, sy = w / (X1 - X0), h / (Y1 - Y0)

    def to_px(p):
        return (left + (p[0] - X0) * sx, top + (p[1] - Y0) * sy)

    ss = 8
    big = Image.new("L", (size * ss, size * ss), 0)
    d = ImageDraw.Draw(big)
    d.polygon([(x * ss, y * ss) for x, y in map(to_px, OUTLINE)], fill=255)
    r = max(EYE_R * sx, 1.5)                                 # never thinner than ~3 px across
    for e in EYES:
        cx, cy = to_px(e)
        cx, cy = int(cx) + 0.5, int(cy) + 0.5                 # eye centre snapped to a pixel centre
        d.ellipse([(cx - r) * ss, (cy - r) * ss, (cx + r) * ss, (cy + r) * ss], fill=0)
    mask = big.resize((size, size), Image.BOX)
    im = Image.new("RGBA", (size, size), FILL[:3] + (0,))
    alpha = mask.point(lambda v: round(v * FILL[3] / 255))
    im.putalpha(alpha)
    im.save(f"{out}/portrait-placeholder-{size}.png", optimize=True)
    print(size, "shape", w, "x", h, "at", left, top)
