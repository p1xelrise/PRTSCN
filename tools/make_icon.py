#!/usr/bin/env python3
"""Draw the PRTSCN app icon (original art): accent rounded square with crop corners. Writes src/PRTSCN/Assets/prtscn.ico."""
import pathlib
from PIL import Image, ImageDraw
root = pathlib.Path(__file__).resolve().parent.parent
def draw(n):
    s = 4; N = n * s
    im = Image.new("RGBA", (N, N), (0, 0, 0, 0)); d = ImageDraw.Draw(im)
    d.rounded_rectangle([0, 0, N - 1, N - 1], radius=int(N * .22), fill=(0xff, 0xb0, 0x20, 255))
    ink = (0x12, 0x13, 0x18, 255); w = max(s, int(N * .09)); a = int(N * .24); b = int(N * .76); L = int(N * .2)
    for (x, y, dx, dy) in [(a, a, 1, 1), (b, a, -1, 1), (a, b, 1, -1), (b, b, -1, -1)]:
        d.line([(x, y), (x + dx * L, y)], fill=ink, width=w); d.line([(x, y), (x, y + dy * L)], fill=ink, width=w)
        r = w // 2; d.ellipse([x - r, y - r, x + r, y + r], fill=ink)
    c = N // 2; r = int(N * .07); d.ellipse([c - r, c - r, c + r, c + r], fill=ink)
    return im.resize((n, n), Image.LANCZOS)
out = root / "src/PRTSCN/Assets/prtscn.ico"
big = draw(256)
big.save(out, sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
         append_images=[draw(n) for n in (16, 20, 24, 32, 48, 64, 128)])
draw(256).save(root / "src/PRTSCN/Assets/prtscn.png")
print("wrote", out)
