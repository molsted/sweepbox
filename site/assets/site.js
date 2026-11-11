// Sweepbox site: latest-release download links, OS detection, small reveals.
(() => {
  const repo = document.body.dataset.repo;
  const $ = (sel, root = document) => root.querySelectorAll(sel);

  // ---- OS detection: highlight the right download and set the hero button
  const ua = navigator.userAgent;
  const platform = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || "";
  const os = /Win/i.test(platform) || /Windows/i.test(ua) ? "win" : /Mac/i.test(platform) || /Mac OS X/i.test(ua) ? "mac" : null;
  const winArm = os === "win" && /ARM64|aarch64/i.test(ua);
  if (os) {
    const card = document.querySelector(`.label-card[data-platform="${os}"]`);
    if (card) card.classList.add("current");
  }
  const primary = document.getElementById("primary-download");
  const primaryAsset = os === "mac" ? "macos-arm64.dmg" : os === "win" ? (winArm ? "windows-arm64-setup.exe" : "windows-x64-setup.exe") : null;
  const primaryLabel = os === "mac" ? "Download for macOS" : os === "win" ? "Download for Windows" : "Download Sweepbox";
  $('[data-download="label"]').forEach(el => (el.textContent = primaryLabel));
  if (os === "mac") $('[data-download="note"]').forEach(el => (el.textContent = "Apple Silicon build. Free, open source (GPL-3.0)."));

  // ---- Latest release from the GitHub API (falls back to /releases/latest links if it fails)
  const fmtSize = b => (b >= 1e9 ? (b / 1e9).toFixed(1) + " GB" : Math.round(b / 1e6) + " MB");
  fetch(`https://api.github.com/repos/${repo}/releases/latest`, { headers: { Accept: "application/vnd.github+json" } })
    .then(r => (r.ok ? r.json() : Promise.reject(r.status)))
    .then(release => {
      const version = release.tag_name;
      const date = new Date(release.published_at).toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
      const find = suffix => release.assets.find(a => a.name.endsWith(suffix));

      $('[data-release="label"]').forEach(el => (el.textContent = `Sweepbox ${version}`));
      $('[data-release="line"]').forEach(el => {
        el.innerHTML = "";
        el.append(`${version}, released ${date}. `);
        const link = Object.assign(document.createElement("a"), { href: release.html_url, textContent: "Release notes and all files" });
        el.append(link);
      });
      $("[data-asset]").forEach(a => {
        const asset = find(a.dataset.asset);
        if (asset) {
          a.href = asset.browser_download_url;
          a.title = `${asset.name} (${fmtSize(asset.size)})`;
        }
      });
      $("[data-size]").forEach(el => {
        const asset = find(el.dataset.size.includes("windows") ? el.dataset.size + "-setup.exe" : el.dataset.size + ".dmg");
        if (asset) el.textContent = `${version} · ${fmtSize(asset.size)}`;
      });
      if (primaryAsset) {
        const asset = find(primaryAsset);
        if (asset) {
          primary.href = asset.browser_download_url;
          primary.title = asset.name;
          primary.insertAdjacentHTML("beforeend", ` <small>${version}</small>`);
        }
      }
    })
    .catch(() => { /* keep the /releases/latest fallbacks */ });

  // ---- Count-up on the receipt total
  const total = document.querySelector("[data-count-to]");
  if (total && !matchMedia("(prefers-reduced-motion: reduce)").matches) {
    const to = parseFloat(total.dataset.countTo), unit = total.dataset.unit || "";
    setTimeout(() => {
      const start = performance.now();
      const tick = now => {
        const t = Math.min(1, (now - start) / 900);
        total.textContent = (to * (1 - Math.pow(1 - t, 3))).toFixed(1) + unit;
        if (t < 1) requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
    }, 2600);
  } else if (total) {
    total.textContent = parseFloat(total.dataset.countTo).toFixed(1) + (total.dataset.unit || "");
  }

  // ---- Reveal on scroll
  const io = "IntersectionObserver" in window && new IntersectionObserver(entries => {
    entries.forEach(e => { if (e.isIntersecting) { e.target.classList.add("in"); io.unobserve(e.target); } });
  }, { rootMargin: "0px 0px -8% 0px" });
  $(".reveal").forEach((el, i) => {
    el.style.transitionDelay = `${(i % 4) * 70}ms`;
    io ? io.observe(el) : el.classList.add("in");
  });

  // ---- Docs: highlight the current section in the table of contents
  const tocLinks = [...$(".toc a")];
  if (tocLinks.length && "IntersectionObserver" in window) {
    const byId = new Map(tocLinks.map(a => [decodeURIComponent(a.hash.slice(1)), a]));
    const spy = new IntersectionObserver(entries => {
      entries.forEach(e => {
        if (e.isIntersecting) {
          tocLinks.forEach(a => a.classList.remove("active"));
          byId.get(e.target.id)?.classList.add("active");
        }
      });
    }, { rootMargin: "-80px 0px -70% 0px" });
    $(".prose h2[id], .prose h3[id]").forEach(h => spy.observe(h));
  }
})();
