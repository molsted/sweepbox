#!/usr/bin/env python3
"""Turns the raw window renders from tools/Screenshots into manual images (docs/images):
adds macOS / Windows 11 window chrome with shadow, and draws the menu bar / tray menus
(native menus can't be rendered headless).

  dotnet run --project tools/Screenshots -- docs/images/raw
  python3 tools/frame_screenshots.py
"""
import os

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RAW = os.path.join(ROOT, "docs", "images", "raw")
OUT = os.path.join(ROOT, "docs", "images")
ASSETS = os.path.join(ROOT, "src", "Sweepbox.Desktop", "Assets")
S = 2  # raw renders are 2x
ORANGE = (234, 88, 12)


def font(size, weight=None):
    for path in ("/System/Library/Fonts/SFNS.ttf", "/System/Library/Fonts/Helvetica.ttc"):
        if os.path.exists(path):
            f = ImageFont.truetype(path, size)
            if weight and path.endswith("SFNS.ttf"):
                try:
                    f.set_variation_by_name(weight)
                except Exception:
                    pass
            return f
    return ImageFont.load_default()


def rounded(img, radius):
    mask = Image.new("L", img.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, img.width - 1, img.height - 1), radius=radius, fill=255)
    out = img.copy()
    out.putalpha(ImageChops.multiply(out.getchannel("A"), mask))
    return out


def with_shadow(img, radius, pad=60):
    canvas = Image.new("RGBA", (img.width + pad * 2, img.height + pad * 2), (0, 0, 0, 0))
    shadow = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle(
        (pad, pad + 14, pad + img.width, pad + img.height + 14), radius=radius, fill=(0, 0, 0, 70))
    canvas = Image.alpha_composite(canvas, shadow.filter(ImageFilter.GaussianBlur(26)))
    canvas.alpha_composite(img, (pad, pad))
    return canvas


def mac_window(raw, title, dark):
    bar_h = 28 * S
    w = Image.new("RGBA", (raw.width, raw.height + bar_h), (0, 0, 0, 0))
    d = ImageDraw.Draw(w)
    d.rectangle((0, 0, raw.width, bar_h), fill=(54, 54, 56, 255) if dark else (236, 236, 236, 255))
    for i, c in enumerate([(255, 95, 87), (254, 188, 46), (40, 200, 64)]):
        cx, cy, r = (20 + i * 20) * S, bar_h // 2, 6 * S
        d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=c + (255,))
    f = font(13 * S, "Semibold")
    tw = d.textlength(title, font=f)
    d.text(((raw.width - tw) / 2, bar_h / 2 - 8 * S), title, font=f, fill=(225, 225, 225) if dark else (60, 60, 60))
    w.paste(raw, (0, bar_h))
    w = rounded(w, 10 * S)
    ImageDraw.Draw(w).rounded_rectangle((0, 0, w.width - 1, w.height - 1), radius=10 * S,
                                        outline=(90, 90, 92, 255) if dark else (190, 190, 195, 255), width=1)
    return with_shadow(w, 10 * S)


def win_window(raw, title, dark):
    bar_h = 32 * S
    w = Image.new("RGBA", (raw.width, raw.height + bar_h), (0, 0, 0, 0))
    d = ImageDraw.Draw(w)
    bg = (32, 32, 32, 255) if dark else (243, 243, 243, 255)
    fg = (255, 255, 255) if dark else (0, 0, 0)
    d.rectangle((0, 0, raw.width, bar_h), fill=bg)
    icon = Image.open(os.path.join(ASSETS, "app-icon.png")).convert("RGBA").resize((16 * S, 16 * S), Image.LANCZOS)
    w.alpha_composite(icon, (12 * S, (bar_h - icon.height) // 2))
    d.text((38 * S, bar_h / 2 - 7 * S), title, font=font(12 * S), fill=fg)
    # caption buttons: minimise, maximise, close (46pt wide each)
    cy = bar_h // 2
    x = raw.width - 46 * S * 3
    d.line((x + 18 * S, cy, x + 28 * S, cy), fill=fg, width=S)
    x += 46 * S
    d.rectangle((x + 18 * S, cy - 5 * S, x + 28 * S, cy + 5 * S), outline=fg, width=S)
    x += 46 * S
    d.line((x + 18 * S, cy - 5 * S, x + 28 * S, cy + 5 * S), fill=fg, width=S)
    d.line((x + 18 * S, cy + 5 * S, x + 28 * S, cy - 5 * S), fill=fg, width=S)
    w.paste(raw, (0, bar_h))
    w = rounded(w, 8 * S)
    ImageDraw.Draw(w).rounded_rectangle((0, 0, w.width - 1, w.height - 1), radius=8 * S,
                                        outline=(70, 70, 70, 255) if dark else (200, 200, 200, 255), width=1)
    return with_shadow(w, 8 * S)


# ------------------------------------------------------------------ menus

def menu_items(platform):
    # Same figures as the sample history rendered by tools/Screenshots
    last, total = ("2.1 GB", "4.1 GB") if platform == "macos" else ("825.3 MB", "1.6 GB")
    return [
        ("Sweepbox – Idle", "disabled"),
        (f"Last run: Today 08.02 – OK, freed {last}", "disabled"),
        ("Next run: Tomorrow 08.02", "disabled"),
        (f"Space freed: {total} in 7 runs", "disabled"),
        None,
        ("Run Now", "highlight"),
        ("Run Only", "submenu"),
        ("Cancel Run", "disabled"),
        None,
        ("Pause Schedule", "normal"),
        ("Status and Log…", "normal"),
        ("Settings…", "normal"),
        None,
        ("Quit Sweepbox", "normal"),
    ]


def draw_menu(platform, dark):
    mac = platform == "macos"
    items = menu_items(platform)
    width = 330 * S
    row = (22 if mac else 32) * S
    sep = (11 if mac else 9) * S
    pad = (5 if mac else 4) * S
    height = pad * 2 + sum(sep if i is None else row for i in items)
    menu = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    d = ImageDraw.Draw(menu)
    radius = (10 if mac else 8) * S
    bg = ((44, 44, 46, 245) if dark else (246, 246, 248, 245)) if mac else ((44, 44, 44, 255) if dark else (249, 249, 249, 255))
    d.rounded_rectangle((0, 0, width - 1, height - 1), radius=radius, fill=bg,
                        outline=(80, 80, 82, 255) if dark else (205, 205, 210, 255), width=1)
    fg = (240, 240, 240) if dark else (28, 28, 30)
    muted = (142, 142, 147) if dark else (138, 138, 142)
    f = font((13 if mac else 14) * S)
    y = pad
    for item in items:
        if item is None:
            d.line(((10 if mac else 0) * S, y + sep // 2, width - (10 if mac else 0) * S, y + sep // 2),
                   fill=(72, 72, 74) if dark else (217, 217, 222), width=S)
            y += sep
            continue
        text, kind = item
        x = (12 if mac else 40) * S
        if kind == "highlight":
            hl = ORANGE if mac else ((61, 61, 61) if dark else (234, 234, 234))
            d.rounded_rectangle((pad, y, width - pad, y + row), radius=(5 if mac else 4) * S, fill=hl + (255,))
            if not mac:  # Windows 11 accent pill
                d.rounded_rectangle((pad, y + row * 0.3, pad + 3 * S, y + row * 0.7), radius=S, fill=ORANGE + (255,))
        colour = (255, 255, 255) if (kind == "highlight" and mac) else muted if kind == "disabled" else fg
        d.text((x, y + row / 2 - 8 * S), text, font=f, fill=colour)
        if kind == "submenu":
            d.text((width - 24 * S, y + row / 2 - 8 * S), "›", font=font(15 * S), fill=fg)
        y += row
    return menu, radius


def menu_scene(platform, dark):
    mac = platform == "macos"
    menu, radius = draw_menu(platform, dark)
    W = 560 * S
    if mac:
        bar_h = 26 * S
        H = bar_h + menu.height + 46 * S
        scene = Image.new("RGBA", (W, H), (30, 30, 34, 255) if dark else (226, 232, 240, 255))
        d = ImageDraw.Draw(scene)
        d.rectangle((0, 0, W, bar_h), fill=(40, 40, 44, 255) if dark else (245, 245, 247, 255))
        fg = (255, 255, 255) if dark else (0, 0, 0)
        clock = "Wed 11 Nov  09.41"
        fc = font(13 * S, "Medium")
        cw = d.textlength(clock, font=fc)
        d.text((W - cw - 14 * S, bar_h / 2 - 8 * S), clock, font=fc, fill=fg)
        # menu bar icon (template: black glyph, white on dark bars), highlighted as when the menu is open
        icon_x = int(W - cw - 14 * S - 50 * S)
        d.rounded_rectangle((icon_x - 6 * S, 3 * S, icon_x + 24 * S, bar_h - 3 * S), radius=5 * S,
                            fill=(255, 255, 255, 50) if dark else (0, 0, 0, 30))
        glyph = Image.open(os.path.join(ASSETS, "tray-mac-idle.png")).convert("RGBA").resize((18 * S, 18 * S), Image.LANCZOS)
        tinted = Image.new("RGBA", glyph.size, fg + (255,))
        tinted.putalpha(glyph.getchannel("A"))
        scene.alpha_composite(tinted, (icon_x, (bar_h - glyph.height) // 2))
        mx = int(min(icon_x - 20 * S, W - menu.width - 12 * S))
        my = bar_h + 4 * S
    else:
        bar_h = 48 * S
        H = menu.height + bar_h + 40 * S
        scene = Image.new("RGBA", (W, H), (25, 25, 28, 255) if dark else (220, 230, 242, 255))
        d = ImageDraw.Draw(scene)
        d.rectangle((0, H - bar_h, W, H), fill=(32, 32, 32, 255) if dark else (238, 241, 245, 255))
        fg = (255, 255, 255) if dark else (0, 0, 0)
        fc = font(12 * S)
        d.text((W - 80 * S, H - bar_h + 8 * S), "09:41", font=fc, fill=fg)
        d.text((W - 80 * S, H - bar_h + 25 * S), "11-11-2026", font=fc, fill=fg)
        icon_x = W - 130 * S
        d.rounded_rectangle((icon_x - 6 * S, H - bar_h + 8 * S, icon_x + 26 * S, H - 8 * S), radius=4 * S,
                            fill=(255, 255, 255, 30) if dark else (0, 0, 0, 22))
        icon = Image.open(os.path.join(ASSETS, "tray-win-idle.png")).convert("RGBA").resize((20 * S, 20 * S), Image.LANCZOS)
        scene.alpha_composite(icon, (icon_x, H - bar_h + (bar_h - icon.height) // 2))
        # "show hidden icons" chevron
        cx, cy = icon_x - 26 * S, H - bar_h // 2
        d.line((cx - 5 * S, cy + 3 * S, cx, cy - 2 * S, cx + 5 * S, cy + 3 * S), fill=fg, width=int(1.5 * S), joint="curve")
        mx = W - menu.width - 24 * S
        my = H - bar_h - menu.height - 10 * S

    shadow = Image.new("RGBA", scene.size, (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle((mx, my + 8 * S, mx + menu.width, my + menu.height + 8 * S), radius=radius, fill=(0, 0, 0, 90))
    scene = Image.alpha_composite(scene, shadow.filter(ImageFilter.GaussianBlur(14 * S)))
    scene.alpha_composite(menu, (mx, my))
    return with_shadow(rounded(scene, 10 * S), 10 * S, pad=40)


TITLES = {"settings": "Sweepbox Settings", "status": "Sweepbox", "status-log": "Sweepbox"}


def main():
    os.makedirs(OUT, exist_ok=True)
    for name in sorted(os.listdir(RAW)):
        if not name.endswith(".png"):
            continue
        base = name[:-4]
        dark = base.endswith("-dark")
        stem = base[:-5] if dark else base
        kind, platform = stem.rsplit("-", 1)
        raw = Image.open(os.path.join(RAW, name)).convert("RGBA")
        framed = (mac_window if platform == "macos" else win_window)(raw, TITLES[kind], dark)
        framed.thumbnail((framed.width // 2 * 2, framed.height))  # keep 2x
        framed.save(os.path.join(OUT, name), optimize=True)
    for platform in ("macos", "windows"):
        for dark in (False, True):
            menu_scene(platform, dark).save(os.path.join(OUT, f"menu-{platform}{'-dark' if dark else ''}.png"), optimize=True)
    print("Framed images written to", OUT)


if __name__ == "__main__":
    main()
