#!/usr/bin/env python3
"""Graphics for the GitHub Pages site (site/assets), built from the app icon and the manual screenshots.

  python3 tools/generate_site_graphics.py

Outputs:
  favicon.png, apple-touch-icon.png, icon-256.png
  hero.png / hero-dark.png        status window with the menu bar menu overlapping (landing page hero)
  og-image.png                    1200x630 social preview card (also usable as the repo's social preview)
"""
import os

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "site", "assets")
IMAGES = os.path.join(ROOT, "docs", "images")
ICON = os.path.join(ROOT, "installer", "macos", "AppIcon-1024.png")
ORANGE = (234, 88, 12)
AMBER = (251, 176, 36)


def font(size, weight=None):
    path = "/System/Library/Fonts/SFNS.ttf"
    if not os.path.exists(path):
        return ImageFont.load_default()
    f = ImageFont.truetype(path, size)
    if weight:
        try:
            f.set_variation_by_name(weight)
        except Exception:
            pass
    return f


def gradient(w, h, a, b):
    g = Image.new("RGBA", (w, h))
    px = g.load()
    for y in range(h):
        for x in range(w):
            t = (x / w * 0.6 + y / h * 0.4)
            px[x, y] = tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3)) + (255,)
    return g


def trim(img):
    """Crop transparent margins (the screenshots carry generous shadow padding)."""
    bbox = img.getchannel("A").point(lambda v: 255 if v > 8 else 0).getbbox()
    return img.crop(bbox) if bbox else img


def hero(dark):
    suffix = "-dark" if dark else ""
    status = trim(Image.open(os.path.join(IMAGES, f"status-macos{suffix}.png")).convert("RGBA"))
    menu = trim(Image.open(os.path.join(IMAGES, f"menu-macos{suffix}.png")).convert("RGBA"))
    menu = menu.resize((int(menu.width * 0.82), int(menu.height * 0.82)), Image.LANCZOS)
    pad = 70
    w = status.width + menu.width // 2 + pad * 2
    h = max(status.height, menu.height) + 160 + pad * 2
    canvas = Image.new("RGBA", (w, h), (0, 0, 0, 0))

    def place(img, x, y, blur=36, alpha=80):
        shadow = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
        ImageDraw.Draw(shadow).rounded_rectangle((x + 10, y + 30, x + img.width - 10, y + img.height + 20), radius=24, fill=(60, 25, 0, alpha))
        canvas.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(blur)))
        canvas.alpha_composite(img, (x, y))

    place(status, pad, pad)
    place(menu, w - menu.width - pad, h - menu.height - pad, blur=30, alpha=100)
    canvas = trim(canvas)
    # keep a little transparent breathing room for the CSS drop
    out = Image.new("RGBA", (canvas.width + 40, canvas.height + 40), (0, 0, 0, 0))
    out.alpha_composite(canvas, (20, 20))
    max_w = 1600
    if out.width > max_w:
        out = out.resize((max_w, int(out.height * max_w / out.width)), Image.LANCZOS)
    return out


def og_image():
    W, H = 1200, 630
    img = gradient(W, H, ORANGE, AMBER)
    # soft light blobs
    glow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.ellipse((-200, -260, 520, 360), fill=(255, 255, 255, 46))
    gd.ellipse((760, 380, 1400, 900), fill=(255, 255, 255, 30))
    img = Image.alpha_composite(img, glow.filter(ImageFilter.GaussianBlur(60)))

    icon = Image.open(ICON).convert("RGBA").resize((150, 150), Image.LANCZOS)
    img.alpha_composite(icon, (64, 70))
    d = ImageDraw.Draw(img)
    d.text((64, 250), "Sweepbox", font=font(92, "Bold"), fill="white")
    for i, line in enumerate(["Homebrew, Chocolatey and npm,", "updated and swept clean", "on a schedule."]):
        d.text((68, 362 + i * 42), line, font=font(34, "Medium"), fill=(255, 255, 255, 240))
    d.text((68, 540), "macOS  ·  Windows  ·  Free and open source", font=font(24), fill=(255, 245, 235, 230))

    shot = trim(Image.open(os.path.join(IMAGES, "status-macos.png")).convert("RGBA"))
    target_w = 540
    shot = shot.resize((target_w, int(shot.height * target_w / shot.width)), Image.LANCZOS)
    x, y = W - target_w - 50, (H - shot.height) // 2
    shadow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle((x, y + 20, x + shot.width, y + shot.height + 20), radius=18, fill=(80, 30, 0, 110))
    img = Image.alpha_composite(img, shadow.filter(ImageFilter.GaussianBlur(26)))
    img.alpha_composite(shot, (x, y))
    return img.convert("RGB")


def main():
    os.makedirs(OUT, exist_ok=True)
    icon = Image.open(ICON).convert("RGBA")
    icon.resize((64, 64), Image.LANCZOS).save(os.path.join(OUT, "favicon.png"))
    # apple-touch icons get rounded by iOS, so use a full-bleed version
    bleed = gradient(180, 180, ORANGE, AMBER)
    glyph = icon.crop((150, 150, 874, 874)).resize((180, 180), Image.LANCZOS)
    bleed.alpha_composite(glyph)
    bleed.convert("RGB").save(os.path.join(OUT, "apple-touch-icon.png"))
    icon.resize((256, 256), Image.LANCZOS).save(os.path.join(OUT, "icon-256.png"))
    hero(False).save(os.path.join(OUT, "hero.png"), optimize=True)
    hero(True).save(os.path.join(OUT, "hero-dark.png"), optimize=True)
    og_image().save(os.path.join(OUT, "og-image.png"), optimize=True)
    print("Site graphics written to", OUT)


if __name__ == "__main__":
    main()
