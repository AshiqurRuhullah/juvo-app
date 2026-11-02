"""Generate logo variants for the front end from Logo/Official Logo.jpg (Pillow).

Usage: python -X utf8 scripts/generate_brand_assets.py

Finds the circular badge (the cream ring and letter stand out from the red background), then writes into juvo-front:
  public/brand/logo-badge.png          transparent circle, 512 px (header, login, error pages)
  public/brand/logo-badge-256.png      transparent circle, 256 px
  public/brand/logo-original.webp      the full artwork, 1024 px, for hero and social use
  public/brand/icon-192.png, icon-512.png            PWA icons (badge on the brand red)
  public/brand/icon-maskable-512.png                 maskable icon with safe zone
  public/brand/og-image.png                          1200x630 share image
  src/app/icon.png, apple-icon.png, favicon.ico      browser tab and home-screen icons
Re-run after changing the logo. The generated files are committed.
"""
from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "Logo" / "Official Logo.jpg"
BRAND = ROOT / "juvo-front" / "public" / "brand"
APP = ROOT / "juvo-front" / "src" / "app"

BRAND_RED = (200, 40, 31)  # #C8281F, sampled from the logo background (top-left)


def find_badge(img: Image.Image) -> tuple[float, float, float]:
    """Return (cx, cy, radius) of the cream ring by taking the extent of very light pixels."""
    rgb = img.convert("RGB")
    w, h = rgb.size
    px = rgb.load()
    xs: list[int] = []
    ys: list[int] = []
    for y in range(0, h, 2):
        for x in range(0, w, 2):
            r, g, b = px[x, y]
            if r > 205 and g > 195 and b > 185:
                xs.append(x)
                ys.append(y)
    left, right, top, bottom = min(xs), max(xs), min(ys), max(ys)
    cx, cy = (left + right) / 2, (top + bottom) / 2
    radius = max(right - left, bottom - top) / 2
    return cx, cy, radius


def circle_crop(img: Image.Image, cx: float, cy: float, radius: float, size: int, pad: float = 1.0) -> Image.Image:
    r = radius * pad
    box = (int(cx - r), int(cy - r), int(cx + r), int(cy + r))
    crop = img.convert("RGBA").crop(box).resize((size * 4, size * 4), Image.LANCZOS)
    mask = Image.new("L", crop.size, 0)
    ImageDraw.Draw(mask).ellipse((0, 0, crop.size[0] - 1, crop.size[1] - 1), fill=255)
    crop.putalpha(mask)
    return crop.resize((size, size), Image.LANCZOS)


def on_background(badge: Image.Image, size: int, scale: float, bg=BRAND_RED) -> Image.Image:
    canvas = Image.new("RGBA", (size, size), bg + (255,))
    inner = int(size * scale)
    b = badge.resize((inner, inner), Image.LANCZOS)
    canvas.alpha_composite(b, ((size - inner) // 2, (size - inner) // 2))
    return canvas


def main() -> None:
    img = Image.open(SRC)
    detected = find_badge(img)
    # The badge is drawn in slight perspective, so the light-pixel estimate clips the 3D edge. These values were read off
    # the artwork (ring outer edge x 190-822, y 150-810 on the 1024 px source).
    cx, cy, radius = 504.0, 482.0, 330.0
    print(f"light-pixel estimate {tuple(round(v) for v in detected)}; using centre ({cx:.0f}, {cy:.0f}) radius {radius:.0f}")

    BRAND.mkdir(parents=True, exist_ok=True)
    APP.mkdir(parents=True, exist_ok=True)

    badge512 = circle_crop(img, cx, cy, radius, 512)
    badge512.save(BRAND / "logo-badge.png", optimize=True)
    circle_crop(img, cx, cy, radius, 256).save(BRAND / "logo-badge-256.png", optimize=True)

    img.convert("RGB").resize((1024, 1024), Image.LANCZOS).save(BRAND / "logo-original.webp", quality=88, method=6)

    on_background(badge512, 192, 0.86).convert("RGB").save(BRAND / "icon-192.png", optimize=True)
    on_background(badge512, 512, 0.86).convert("RGB").save(BRAND / "icon-512.png", optimize=True)
    # Maskable icons keep the artwork inside the central 80% safe zone.
    on_background(badge512, 512, 0.62).convert("RGB").save(BRAND / "icon-maskable-512.png", optimize=True)

    # Browser tab icon: the circle on a transparent background stays crisp at 16-48 px.
    circle_crop(img, cx, cy, radius, 512).save(APP / "icon.png", optimize=True)
    on_background(badge512, 180, 0.86).convert("RGB").save(APP / "apple-icon.png", optimize=True)
    ico_base = circle_crop(img, cx, cy, radius, 256)
    ico_base.save(APP / "favicon.ico", sizes=[(16, 16), (32, 32), (48, 48), (64, 64)])

    # Share image 1200x630: brand red, centred badge.
    og = Image.new("RGB", (1200, 630), BRAND_RED)
    big = circle_crop(img, cx, cy, radius, 460)
    og.paste(big, ((1200 - 460) // 2, (630 - 460) // 2), big)
    og.save(BRAND / "og-image.png", optimize=True)

    for path in sorted(list(BRAND.glob("*")) + [APP / "icon.png", APP / "apple-icon.png", APP / "favicon.ico"]):
        print(f"  {path.relative_to(ROOT)}  {path.stat().st_size / 1024:.0f} KB")


if __name__ == "__main__":
    main()
