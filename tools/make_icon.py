"""Generate the plugin icon: a stylized Bozja / Occult Crescent motif.

Kept as a script (not a committed binary blob) so the icon is reproducible and
reviewable. Output: assets/icon.png at 512x512.
"""
from PIL import Image, ImageDraw
import math
import pathlib

SIZE = 512
OUT = pathlib.Path(r"D:\work\DSH\BozjaAssistant\assets")
OUT.mkdir(parents=True, exist_ok=True)

img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# --- background: deep indigo radial-ish disc -----------------------------
cx = cy = SIZE / 2
r = SIZE / 2 - 6

for i in range(int(r), 0, -1):
    t = i / r
    # outer edge lighter, centre darker
    col = (
        int(28 + 26 * t),
        int(24 + 20 * t),
        int(58 + 40 * t),
        255,
    )
    d.ellipse([cx - i, cy - i, cx + i, cy + i], fill=col)

# --- crescent moon (Occult Crescent) ------------------------------------
moon_r = 130
mx, my = cx + 30, cy - 30
d.ellipse([mx - moon_r, my - moon_r, mx + moon_r, my + moon_r], fill=(232, 214, 150, 255))
# bite out of it, offset toward upper-right, to form the crescent
cut_r = 118
d.ellipse([mx - cut_r + 62, my - cut_r - 30, mx + cut_r + 62, my + cut_r - 30],
          fill=(38, 31, 74, 255))

# --- sword (Bozja) -------------------------------------------------------
# blade
blade = [
    (cx - 150, cy + 150),
    (cx - 132, cy + 168),
    (cx + 118, cy - 82),
    (cx + 100, cy - 100),
]
d.polygon(blade, fill=(214, 220, 232, 255))

# tip
d.polygon([
    (cx + 100, cy - 100),
    (cx + 118, cy - 82),
    (cx + 146, cy - 128),
], fill=(238, 242, 250, 255))

# crossguard
d.polygon([
    (cx - 168, cy + 132),
    (cx - 132, cy + 168),
    (cx - 100, cy + 136),
    (cx - 136, cy + 100),
], fill=(196, 160, 84, 255))

# pommel
d.ellipse([cx - 196, cy + 150, cx - 150, cy + 196], fill=(196, 160, 84, 255))

# --- a few "meteor" dots for the Zadnor / Southern Front feel -----------
for (px, py, rad, alpha) in [
    (cx - 150, cy - 140, 7, 220),
    (cx - 108, cy - 178, 4, 180),
    (cx - 176, cy - 96, 5, 200),
    (cx + 150, cy + 120, 6, 200),
    (cx + 178, cy + 74, 3, 160),
]:
    d.ellipse([px - rad, py - rad, px + rad, py + rad], fill=(240, 236, 210, alpha))

img.save(OUT / "icon.png", "PNG")
print("wrote", OUT / "icon.png", img.size)
