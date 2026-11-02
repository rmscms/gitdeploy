(() => {
  // ---- screenshot tabs ----
  const tabs = document.querySelectorAll(".tab[data-shot]");
  tabs.forEach((tab) => {
    tab.addEventListener("click", () => {
      tabs.forEach((t) => t.classList.toggle("active", t === tab));
      document.querySelectorAll(".shot").forEach((s) => {
        s.classList.toggle("active", s.id === "shot-" + tab.dataset.shot);
      });
    });
  });

  // ---- lightbox ----
  const lightbox = document.getElementById("lightbox");
  const lightboxImage = document.getElementById("lightboxImage");
  const lightboxCaption = document.getElementById("lightboxCaption");
  const closeBtn = document.getElementById("lightboxClose");
  const closeLightbox = () => lightbox?.classList.remove("show");

  document.querySelectorAll("img[data-zoom]").forEach((img) => {
    img.addEventListener("click", () => {
      if (!lightbox || !lightboxImage) return;
      lightboxImage.src = img.currentSrc || img.src;
      lightboxImage.alt = img.alt || "";
      if (lightboxCaption) lightboxCaption.textContent = img.dataset.zoom || "";
      lightbox.classList.add("show");
    });
  });
  closeBtn?.addEventListener("click", closeLightbox);
  lightbox?.addEventListener("click", (e) => { if (e.target === lightbox) closeLightbox(); });
  window.addEventListener("keydown", (e) => { if (e.key === "Escape") closeLightbox(); });

  // ---- hero CTA follows the real download link once site.js fills it ----
  const syncHero = () => {
    const download = document.getElementById("download-button");
    const hero = document.getElementById("hero-download");
    if (!download || !hero) return;
    const href = download.getAttribute("href");
    if (href && href !== "latest.json") hero.setAttribute("href", href);
    const v = (download.textContent || "").match(/(\d+\.\d+\.\d+)/);
    if (v) {
      hero.lastChild.textContent = ` Download ${v[1]} for Windows`;
    }
  };
  const dl = document.getElementById("download-button");
  if (dl) {
    new MutationObserver(syncHero).observe(dl, { attributes: true, childList: true, characterData: true, subtree: true });
  }
  window.setTimeout(syncHero, 800);
})();
