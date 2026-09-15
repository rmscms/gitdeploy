(() => {
  const stage = document.getElementById("bgStage");
  if (stage) {
    window.addEventListener(
      "pointermove",
      (event) => {
        const x = (event.clientX / window.innerWidth - 0.5) * 12;
        const y = (event.clientY / window.innerHeight - 0.5) * 10;
        stage.style.transform = `translate3d(${x}px, ${y}px, 0)`;
      },
      { passive: true }
    );
  }

  const lightbox = document.getElementById("lightbox");
  const lightboxImage = document.getElementById("lightboxImage");
  const lightboxCaption = document.getElementById("lightboxCaption");
  const closeBtn = document.getElementById("lightboxClose");

  const closeLightbox = () => lightbox?.classList.remove("show");

  document.querySelectorAll(".screen[data-full]").forEach((el) => {
    el.addEventListener("click", () => {
      if (!lightbox || !lightboxImage) {
        return;
      }
      lightboxImage.src = el.getAttribute("data-full") || "";
      lightboxImage.alt = el.getAttribute("data-caption") || "";
      if (lightboxCaption) {
        lightboxCaption.textContent = el.getAttribute("data-caption") || "";
      }
      lightbox.classList.add("show");
    });
  });

  closeBtn?.addEventListener("click", closeLightbox);
  lightbox?.addEventListener("click", (event) => {
    if (event.target === lightbox) {
      closeLightbox();
    }
  });
  window.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      closeLightbox();
    }
  });

  // Keep hero CTA in sync once site.js fills the download button.
  const syncHero = () => {
    const download = document.getElementById("download-button");
    const hero = document.getElementById("hero-download");
    if (!download || !hero) {
      return;
    }
    if (download.getAttribute("href")) {
      hero.setAttribute("href", download.getAttribute("href"));
    }
    if (download.textContent && !download.textContent.includes("unavailable")) {
      const versionMatch = download.textContent.match(/(\d+\.\d+\.\d+)/);
      if (versionMatch) {
        hero.textContent = `Download ${versionMatch[1]}`;
      }
    }
  };

  const observer = new MutationObserver(syncHero);
  const downloadButton = document.getElementById("download-button");
  if (downloadButton) {
    observer.observe(downloadButton, {
      attributes: true,
      childList: true,
      characterData: true,
      subtree: true,
    });
  }
  window.setTimeout(syncHero, 800);
})();
