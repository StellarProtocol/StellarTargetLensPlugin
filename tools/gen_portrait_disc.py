# Usage: python tools/gen_portrait_disc.py Resources
# Portrait backing disc for the TargetLens boss bar: radial gradient #2a4aa0 (centre) -> #0e1a3a (edge), antialiased
# edge via 8x supersampling, exactly filling the portrait circle. One PNG per size: 88 / 66 / 44 px.
import sys, math
from PIL import Image
out = sys.argv[1]
C, E = (0x2a, 0x4a, 0xa0), (0x0e, 0x1a, 0x3a)
for size in (88, 66, 44):
    ss = 8; n = size * ss; r = n / 2.0
    im = Image.new("RGBA", (n, n), (0, 0, 0, 0)); px = im.load()
    for y in range(n):
        for x in range(n):
            d = math.hypot(x + 0.5 - r, y + 0.5 - r) / r
            if d <= 1.0:
                t = d ** 1.2
                px[x, y] = tuple(round(C[i] + (E[i] - C[i]) * t) for i in range(3)) + (255,)
    im = im.resize((size, size), Image.LANCZOS)
    im.save(f"{out}/portrait-disc-{size}.png", optimize=True)
    print(size, im.getpixel((size // 2, size // 2)), im.getpixel((0, 0)), im.getpixel((size // 2, 0)))
