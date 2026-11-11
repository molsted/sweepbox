#!/usr/bin/env python3
"""Generates all PkgCleaner graphics from simple vector shapes.

Outputs (in src/PkgCleaner.Desktop/Assets and installer/):
  app-icon.png              256px window icon
  tray-mac-{state}.png      macOS menu bar template icons (black + alpha), 44px (@2x of 22pt)
  tray-win-{state}.png      Windows tray icons (colour), 64px
  installer/macos/AppIcon.icns  macOS app icon (via iconutil)
  installer/windows/AppIcon.ico Windows app/installer icon
  installer/macos/dmg-background.png  DMG window background

Requires Pillow. Run: python3 tools/generate_icons.py
"""
import os
import shutil
import subprocess
import sys
import tempfile

from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "src", "PkgCleaner.Desktop", "Assets")
BUILD = os.path.join(ROOT, "installer")
SS = 4  # supersampling factor

ORANGE = (234, 88, 12)   # orange-600
AMBER = (251, 176, 36)   # warm amber
BLUE = (37, 99, 235)     # running badge, contrasts with orange
RED = (239, 68, 68)
WHITE = (255, 255, 255)


# ---------------------------------------------------------------- primitives

def canvas(size):
    return Image.new("RGBA", (size * SS, size * SS), (0, 0, 0, 0))


def finish(img, size):
    return img.resize((size, size), Image.LANCZOS)


def polyline(draw, pts, width, fill, closed=False):
    if closed:
        pts = pts + [pts[0], pts[1]]
    draw.line(pts, fill=fill, width=int(width), joint="curve")
    r = width / 2
    for x, y in (pts[0], pts[-1]):
        draw.ellipse((x - r, y - r, x + r, y + r), fill=fill)


def box_glyph(draw, cx, cy, scale, width, fill):
    """Isometric parcel outline centred on (cx, cy); scale = half-height in px."""
    def p(x, y):  # x, y in -1..1 glyph units
        return (cx + x * scale, cy + y * scale)

    outline = [p(0, -0.82), p(0.74, -0.41), p(0.74, 0.41), p(0, 0.82), p(-0.74, 0.41), p(-0.74, -0.41)]
    polyline(draw, outline, width, fill, closed=True)
    polyline(draw, [p(-0.74, -0.41), p(0, 0), p(0.74, -0.41)], width, fill)
    polyline(draw, [p(0, 0), p(0, 0.82)], width, fill)
    polyline(draw, [p(-0.37, -0.615), p(0.37, -0.205)], width, fill)  # tape


def sparkle(draw, cx, cy, r, fill):
    """Four-point star."""
    k = r * 0.28
    pts = [(cx, cy - r), (cx + k, cy - k), (cx + r, cy), (cx + k, cy + k),
           (cx, cy + r), (cx - k, cy + k), (cx - r, cy), (cx - k, cy - k)]
    draw.polygon(pts, fill=fill)


def gradient(size, top, bottom):
    """Diagonal gradient from top-left to bottom-right."""
    g = Image.new("RGBA", (size, size))
    px = g.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * (size - 1))
            px[x, y] = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
    return g


def rounded_mask(size, inset, radius):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle((inset, inset, size - inset - 1, size - inset - 1), radius=radius, fill=255)
    return m


# ---------------------------------------------------------------- app icon

def app_icon(size, macos_margin=True):
    """Rounded tile with gradient, white parcel and sparkle."""
    s = size * SS
    inset = int(s * 0.098) if macos_margin else int(s * 0.02)
    radius = int((s - 2 * inset) * 0.225)
    tile = gradient(256, ORANGE, AMBER).resize((s, s), Image.BICUBIC)
    mask = rounded_mask(s, inset, radius)

    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    if macos_margin:  # soft drop shadow like native macOS icons
        shadow = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        shadow.putalpha(mask.point(lambda v: v * 0.35))
        shadow = shadow.transform((s, s), Image.AFFINE, (1, 0, 0, 0, 1, -int(s * 0.012)))
        img = Image.alpha_composite(img, shadow.filter(ImageFilter.GaussianBlur(s * 0.012)))
    tile.putalpha(mask)
    img = Image.alpha_composite(img, tile)

    # subtle top highlight
    hl = Image.new("RGBA", (s, s), (255, 255, 255, 0))
    ImageDraw.Draw(hl).ellipse((-s * 0.3, -s * 0.85, s * 1.3, s * 0.45), fill=(255, 255, 255, 34))
    hl.putalpha(ImageChops.multiply(hl.getchannel("A"), mask))
    img = Image.alpha_composite(img, hl)

    d = ImageDraw.Draw(img)
    inner = s - 2 * inset
    box_glyph(d, s * 0.47, s * 0.53, inner * 0.33, inner * 0.065, WHITE)
    sparkle(d, s * 0.70, s * 0.30, inner * 0.11, WHITE)
    sparkle(d, s * 0.78, s * 0.43, inner * 0.05, (255, 255, 255, 220))
    return finish(img, size)


# ---------------------------------------------------------------- tray icons

def badge(img, state, color_running, color_error, cut=True):
    """Bottom-right status badge. Template icons cut a gap around it so it reads at 16pt."""
    s = img.width
    cx, cy, r = s * 0.76, s * 0.76, s * 0.20
    if cut:
        hole = Image.new("L", img.size, 0)
        ImageDraw.Draw(hole).ellipse((cx - r * 1.35, cy - r * 1.35, cx + r * 1.35, cy + r * 1.35), fill=255)
        img.putalpha(ImageChops.subtract(img.getchannel("A"), hole))
    d = ImageDraw.Draw(img)
    if state == "running":
        w = r * 0.42
        d.arc((cx - r + w / 2, cy - r + w / 2, cx + r - w / 2, cy + r - w / 2), 300, 240, fill=color_running, width=int(w))
        # arrow head
        d.polygon([(cx + r * 0.15, cy - r * 1.18), (cx + r * 0.75, cy - r * 0.7), (cx + r * 0.05, cy - r * 0.3)], fill=color_running)
    elif state == "error":
        d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=color_error)
        # exclamation mark punched out
        ex = Image.new("L", img.size, 0)
        e = ImageDraw.Draw(ex)
        bw = r * 0.30
        e.rounded_rectangle((cx - bw / 2, cy - r * 0.62, cx + bw / 2, cy + r * 0.18), radius=bw / 2, fill=255)
        e.ellipse((cx - bw * 0.6, cy + r * 0.34, cx + bw * 0.6, cy + r * 0.34 + bw * 1.2), fill=255)
        img.putalpha(ImageChops.subtract(img.getchannel("A"), ex))


def tray_mac(state, size=44):
    img = canvas(size)
    s = img.width
    d = ImageDraw.Draw(img)
    box_glyph(d, s * 0.46, s * 0.50, s * 0.42, s * 0.085, (0, 0, 0, 255))
    if state == "idle":
        sparkle(d, s * 0.86, s * 0.16, s * 0.13, (0, 0, 0, 255))
    else:
        badge(img, state, (0, 0, 0, 255), (0, 0, 0, 255))
    return finish(img, size)


def tray_win(state, size=64):
    base = app_icon(size * SS, macos_margin=False)
    if state != "idle":
        s = base.width
        cx, cy, r = s * 0.76, s * 0.76, s * 0.22
        d = ImageDraw.Draw(base)
        ring = r * 1.25
        d.ellipse((cx - ring, cy - ring, cx + ring, cy + ring), fill=WHITE)
        if state == "running":
            d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=BLUE)
            w = r * 0.32
            rr = r * 0.62
            d.arc((cx - rr, cy - rr, cx + rr, cy + rr), 300, 220, fill=WHITE, width=int(w))
        else:
            d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=RED)
            bw = r * 0.28
            d.rounded_rectangle((cx - bw / 2, cy - r * 0.6, cx + bw / 2, cy + r * 0.15), radius=bw / 2, fill=WHITE)
            d.ellipse((cx - bw * 0.6, cy + r * 0.3, cx + bw * 0.6, cy + r * 0.3 + bw * 1.2), fill=WHITE)
    return finish(base, size)


# ---------------------------------------------------------------- DMG background

def dmg_background(width=660, height=400):
    s = 2  # retina
    img = Image.new("RGBA", (width * s, height * s), (246, 247, 251, 255))
    d = ImageDraw.Draw(img)
    # gentle top band
    band = gradient(256, ORANGE, AMBER).resize((width * s, int(height * s * 0.012) + 1))
    img.paste(band, (0, 0))
    # arrow between the app icon (x=180) and Applications (x=480), icon centre y=200
    y = 205 * s
    d.line([(265 * s, y), (390 * s, y)], fill=(160, 166, 190, 255), width=6 * s // 2)
    d.polygon([(390 * s, y - 12 * s), (410 * s, y), (390 * s, y + 12 * s)], fill=(160, 166, 190, 255))
    return img


# ---------------------------------------------------------------- outputs

def write_icns(path):
    with tempfile.TemporaryDirectory() as tmp:
        iconset = os.path.join(tmp, "AppIcon.iconset")
        os.makedirs(iconset)
        for base in (16, 32, 128, 256, 512):
            app_icon(base).save(os.path.join(iconset, f"icon_{base}x{base}.png"))
            app_icon(base * 2).save(os.path.join(iconset, f"icon_{base}x{base}@2x.png"))
        if shutil.which("iconutil"):
            subprocess.run(["iconutil", "-c", "icns", iconset, "-o", path], check=True)
        else:
            print("iconutil not found (not macOS?) - skipping .icns", file=sys.stderr)
        app_icon(1024).save(path.replace(".icns", "-1024.png"))


def main():
    os.makedirs(ASSETS, exist_ok=True)
    os.makedirs(os.path.join(BUILD, "macos"), exist_ok=True)
    os.makedirs(os.path.join(BUILD, "windows"), exist_ok=True)

    app_icon(256).save(os.path.join(ASSETS, "app-icon.png"))
    for state in ("idle", "running", "error"):
        tray_mac(state).save(os.path.join(ASSETS, f"tray-mac-{state}.png"))
        tray_win(state).save(os.path.join(ASSETS, f"tray-win-{state}.png"))

    write_icns(os.path.join(BUILD, "macos", "AppIcon.icns"))
    ico_sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    big = app_icon(256, macos_margin=False)
    big.save(os.path.join(BUILD, "windows", "AppIcon.ico"), sizes=[(n, n) for n in ico_sizes])
    shutil.copy(os.path.join(BUILD, "windows", "AppIcon.ico"), os.path.join(ASSETS, "app-icon.ico"))

    # Inno Setup wizard images (BMP): small 55x55 logo, large 164x314 side panel
    small = Image.new("RGB", (55, 55), WHITE)
    small.paste(app_icon(55, macos_margin=False), (0, 0), app_icon(55, macos_margin=False))
    small.save(os.path.join(BUILD, "windows", "wizard-small.bmp"))
    large = gradient(256, ORANGE, AMBER).resize((164, 314)).convert("RGB")
    logo = app_icon(120, macos_margin=False)
    large.paste(logo, (22, 60), logo)
    large.save(os.path.join(BUILD, "windows", "wizard-large.bmp"))

    dmg_background().save(os.path.join(BUILD, "macos", "dmg-background.png"))
    print("Icons written to", ASSETS, "and", BUILD)


if __name__ == "__main__":
    main()
