#!/usr/bin/env python3
"""Builds the GitHub Pages site into _site/.

  pip install markdown==3.7
  python3 tools/build_site.py            # repo/base URL from $GITHUB_REPOSITORY, default molsted/sweepbox

- site/index.html and site/assets/* are copied as-is (placeholders filled in)
- docs.html is rendered from docs/MANUAL.md (single source of truth for the manual)
- license.html is rendered from LICENSE
- docs/images and the tray icons are copied so the manual's images work
"""
import html
import os
import re
import shutil

import markdown

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "site")
OUT = os.path.join(ROOT, "_site")
REPO = os.environ.get("GITHUB_REPOSITORY", "molsted/sweepbox")
OWNER, NAME = REPO.split("/")
BASE_URL = os.environ.get("SITE_BASE_URL", f"https://{OWNER.lower()}.github.io/{NAME}/")


def fill(text, **values):
    values = {"REPO": REPO, "BASE_URL": BASE_URL, "NAV_DOCS": "", "NAV_LICENSE": "", **values}
    for key, value in values.items():
        text = text.replace("{{" + key + "}}", value)
    leftover = re.findall(r"\{\{[A-Z_]+\}\}", text)
    if leftover:
        raise SystemExit(f"Unfilled placeholders: {leftover}")
    return text


def write(name, text):
    with open(os.path.join(OUT, name), "w", encoding="utf-8") as f:
        f.write(text)


def toc_html(tokens, depth=2):
    """Nested list from markdown's toc tokens, h2 and h3 only."""
    items = []
    for t in tokens:
        if t["level"] > 3:
            continue
        children = toc_html(t["children"], depth + 1) if t["level"] < 3 else ""
        items.append(f'<li><a href="#{t["id"]}">{t["name"]}</a>{children}</li>')
    return f"<ul>{''.join(items)}</ul>" if items else ""


def build_docs(layout):
    with open(os.path.join(ROOT, "docs", "MANUAL.md"), encoding="utf-8") as f:
        text = f.read()
    # The site has its own sidebar TOC: drop the manual's bullet list of section links.
    text = re.sub(r"\n(- \[[^\]]+\]\(#[^)]+\)\n)+", "\n", text, count=1)
    # Repo-relative links -> site paths
    text = (text.replace("../src/Sweepbox.Desktop/Assets/", "images/tray/")
                .replace("(../LICENSE)", "(license.html)"))
    md = markdown.Markdown(extensions=["tables", "fenced_code", "toc", "md_in_html"],
                           extension_configs={"toc": {"toc_depth": "2-3"}})
    body = md.convert(text)
    # The intro paragraph(s) before the first rule become the hero text.
    intro = "Everything about installing, configuring and troubleshooting Sweepbox on macOS and Windows."
    tokens = md.toc_tokens
    if tokens and tokens[0]["level"] == 1:
        tokens = tokens[0]["children"]
    write("docs.html", fill(layout, TITLE="Manual", KICKER="Documentation", INTRO=intro,
                            DESCRIPTION="The Sweepbox manual: install, menu, settings, space freed, command line, troubleshooting.",
                            PAGE_URL=f"{BASE_URL}docs.html",
                            TOC=toc_html(tokens), CONTENT=body, NAV_DOCS=' aria-current="page"'))


def build_license(layout):
    with open(os.path.join(ROOT, "LICENSE"), encoding="utf-8") as f:
        license_text = f.read()
    summary = f"""
<div class="callout">
  <div>
    <h3>In short</h3>
    <p>Sweepbox is free software under the GNU General Public License v3.0. You may use it for any purpose,
       study and change the source, and share copies or your changes, as long as what you distribute is
       also under the GPL-3.0 with its source available.</p>
    <ul>
      <li>Source code: <a href="https://github.com/{REPO}">github.com/{REPO}</a></li>
      <li>License file in the repository: <a href="https://github.com/{REPO}/blob/main/LICENSE">LICENSE</a></li>
      <li>The same text ships with the app (<em>Status window → License</em>) and the Windows installer</li>
    </ul>
    <p style="margin-top:10px;font-size:14px">This summary is not legal advice; the full license text below is what applies.</p>
  </div>
</div>
<h2 id="full-text">Full license text</h2>
<pre class="license-text">{html.escape(license_text)}</pre>
"""
    toc = '<ul><li><a href="#full-text">Full license text</a></li></ul>'
    write("license.html", fill(layout, TITLE="License", KICKER="GPL-3.0", INTRO="Sweepbox is free and open source software.",
                               DESCRIPTION="Sweepbox is licensed under the GNU General Public License v3.0.",
                               PAGE_URL=f"{BASE_URL}license.html",
                               TOC=toc, CONTENT=summary, NAV_LICENSE=' aria-current="page"'))


def main():
    shutil.rmtree(OUT, ignore_errors=True)
    shutil.copytree(SRC, OUT, ignore=shutil.ignore_patterns("_layout.html", ".DS_Store"))
    with open(os.path.join(SRC, "index.html"), encoding="utf-8") as f:
        write("index.html", fill(f.read()))
    with open(os.path.join(SRC, "_layout.html"), encoding="utf-8") as f:
        layout = f.read()

    shutil.copytree(os.path.join(ROOT, "docs", "images"), os.path.join(OUT, "images"),
                    ignore=shutil.ignore_patterns("raw", ".DS_Store"))
    tray = os.path.join(OUT, "images", "tray")
    os.makedirs(tray, exist_ok=True)
    assets = os.path.join(ROOT, "src", "Sweepbox.Desktop", "Assets")
    for name in os.listdir(assets):
        if name.startswith("tray-"):
            shutil.copy(os.path.join(assets, name), tray)

    build_docs(layout)
    build_license(layout)

    write("robots.txt", "User-agent: *\nAllow: /\n\nSitemap: " + BASE_URL + "sitemap.xml\n")
    pages = ["", "docs.html", "license.html"]
    write("sitemap.xml", '<?xml version="1.0" encoding="UTF-8"?>\n'
           '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n'
           + "".join(f"<url><loc>{BASE_URL}{page.lstrip('/')}</loc></url>\n" for page in pages)
           + "</urlset>\n")
    open(os.path.join(OUT, ".nojekyll"), "w").close()
    print(f"Site built in {OUT} for {BASE_URL}")


if __name__ == "__main__":
    main()
