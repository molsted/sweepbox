#!/usr/bin/env python3
"""Generates all Sweepbox graphics from simple vector shapes.

Outputs (in src/Sweepbox.Desktop/Assets and installer/):
  app-icon.png              256px window icon
  tray-mac-{state}.png      macOS menu bar template icons (black + alpha), 44px (@2x of 22pt)
  tray-win-{state}.png      Windows tray icons (colour), 64px
  installer/macos/AppIcon.icns  macOS app icon (via iconutil)
  installer/windows/AppIcon.ico Windows app/installer icon
  installer/macos/dmg-background{,@2x}.png  DMG window background (1x/2x)
  installer/macos/AppIcon.icon  Icon Composer source for macOS 26 (compiled by actool)

Requires Pillow. Run: python3 tools/generate_icons.py
"""
import os
import shutil
import subprocess
import sys
import tempfile

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "src", "Sweepbox.Desktop", "Assets")
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

def _font(size, bold=False):
    for path, index in (("/System/Library/Fonts/SFNS.ttf", 0), ("/System/Library/Fonts/Helvetica.ttc", 1 if bold else 0)):
        if os.path.exists(path):
            f = ImageFont.truetype(path, size, index=index)
            if bold and path.endswith("SFNS.ttf"):
                try:
                    f.set_variation_by_name("Semibold")
                except Exception:
                    pass
            return f
    return ImageFont.load_default()


def _centered_text(d, y, text, font, fill, width):
    w = d.textlength(text, font=font)
    d.text(((width - w) / 2, y), text, font=font, fill=fill)


def _glow(size, centre, radius, colour, alpha):
    layer = Image.new("RGBA", size, colour + (0,))
    m = Image.new("L", size, 0)
    cx, cy = centre
    ImageDraw.Draw(m).ellipse((cx - radius, cy - radius, cx + radius, cy + radius), fill=alpha)
    layer.putalpha(m.filter(ImageFilter.GaussianBlur(radius * 0.45)))
    return layer


def dmg_background(scale, width=660, height=400):
    """DMG window background (points * scale). Icons sit at (180,190) and (480,190), 112pt."""
    s = scale * SS
    W, H = width * s, height * s
    P = lambda v: v * s  # points -> pixels

    # warm vertical gradient: orange-50 at the top fading to white
    top, bottom = (255, 244, 230), (255, 252, 248)
    img = Image.new("RGBA", (W, H))
    px = ImageDraw.Draw(img)
    for y in range(H):
        t = y / (H - 1)
        px.line([(0, y), (W, y)], fill=tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,))

    # soft amber glows in the corners
    img = Image.alpha_composite(img, _glow((W, H), (P(40), P(20)), P(170), AMBER, 70))
    img = Image.alpha_composite(img, _glow((W, H), (P(640), P(390)), P(200), ORANGE, 45))

    # faint oversized parcel watermark, bottom right
    mark = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    box_glyph(ImageDraw.Draw(mark), P(600), P(330), P(110), P(10), ORANGE + (22,))
    img = Image.alpha_composite(img, mark)

    # white "pedestals" with a soft shadow under each icon
    for cx in (180, 480):
        shadow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        ImageDraw.Draw(shadow).ellipse((P(cx - 64), P(190 - 56), P(cx + 64), P(190 + 72)), fill=(120, 60, 0, 40))
        img = Image.alpha_composite(img, shadow.filter(ImageFilter.GaussianBlur(P(10))))
        disc = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        ImageDraw.Draw(disc).ellipse((P(cx - 66), P(190 - 66), P(cx + 66), P(190 + 66)), fill=(255, 255, 255, 235))
        img = Image.alpha_composite(img, disc)

    d = ImageDraw.Draw(img)

    # top accent band
    img.paste(gradient(256, ORANGE, AMBER).resize((W, P(5))), (0, 0))

    # header
    _centered_text(d, P(26), "Sweepbox", _font(P(22), bold=True), (41, 37, 36, 255), W)
    _centered_text(d, P(56), "Keeps your package managers fresh and tidy", _font(P(12)), (120, 113, 108, 255), W)

    # curved dashed arrow (quadratic bezier) from the app towards Applications
    p0, p1, p2 = (P(268), P(196)), (P(330), P(150)), (P(390), P(190))
    pts = []
    for i in range(121):
        t = i / 120
        pts.append(((1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * p1[0] + t * t * p2[0],
                    (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * p1[1] + t * t * p2[1]))
    colour = ORANGE + (255,)
    for i in range(0, 120, 12):
        seg = pts[i:i + 8]
        d.line(seg, fill=colour, width=int(P(4)), joint="curve")
        for x, y in (seg[0], seg[-1]):
            r = P(2)
            d.ellipse((x - r, y - r, x + r, y + r), fill=colour)
    # arrow head along the final tangent
    import math
    (x1, y1), (x2, y2) = pts[-6], pts[-1]
    a = math.atan2(y2 - y1, x2 - x1)
    tip = (x2 + math.cos(a) * P(10), y2 + math.sin(a) * P(10))
    left = (tip[0] - math.cos(a - 0.5) * P(18), tip[1] - math.sin(a - 0.5) * P(18))
    right = (tip[0] - math.cos(a + 0.5) * P(18), tip[1] - math.sin(a + 0.5) * P(18))
    d.polygon([tip, left, right], fill=colour)

    # caption + footer
    _centered_text(d, P(318), "Drag Sweepbox to Applications to install", _font(P(14), bold=True), (68, 64, 60, 255), W)
    _centered_text(d, P(342), "Then find it in the menu bar  ·  Free and open source (GPL-3.0)", _font(P(11)), (140, 132, 126, 255), W)

    return img.resize((width * scale, height * scale), Image.LANCZOS)


def icon_composer_bundle(path):
    """Icon Composer (.icon) source for macOS 26+: orange fill + white glyph layer.
    Compiled by actool (Xcode 26) into Assets.car, so macOS renders it natively instead of
    placing a legacy icon inside a grey frame."""
    shutil.rmtree(path, ignore_errors=True)
    os.makedirs(os.path.join(path, "Assets"))
    s = 1024 * SS
    glyph = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(glyph)
    box_glyph(d, s * 0.47, s * 0.53, s * 0.28, s * 0.054, WHITE)
    sparkle(d, s * 0.70, s * 0.29, s * 0.092, WHITE)
    sparkle(d, s * 0.785, s * 0.425, s * 0.042, WHITE)
    finish(glyph, 1024).save(os.path.join(path, "Assets", "glyph.png"))
    r, g, b = (c / 255 for c in ORANGE)
    with open(os.path.join(path, "icon.json"), "w") as f:
        f.write("""{
  "fill" : {
    "automatic-gradient" : "srgb:%.5f,%.5f,%.5f,1.00000"
  },
  "groups" : [
    {
      "layers" : [
        {
          "image-name" : "glyph.png",
          "name" : "glyph"
        }
      ],
      "shadow" : {
        "kind" : "neutral",
        "opacity" : 0.5
      },
      "translucency" : {
        "enabled" : true,
        "value" : 0.4
      }
    }
  ],
  "supported-platforms" : {
    "squares" : [
      "macOS"
    ]
  }
}
""" % (r, g, b))


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

    dmg_background(1).save(os.path.join(BUILD, "macos", "dmg-background.png"))
    dmg_background(2).save(os.path.join(BUILD, "macos", "dmg-background@2x.png"))
    icon_composer_bundle(os.path.join(BUILD, "macos", "AppIcon.icon"))
    print("Icons written to", ASSETS, "and", BUILD)


if __name__ == "__main__":
    main()
