(() => {
  const page = document.body?.dataset?.page || "";
  const navLinks = document.querySelectorAll("[data-nav]");
  navLinks.forEach((link) => {
    if (link.getAttribute("data-nav") === page) {
      link.classList.add("active");
    }
  });

  const downloadBlurb = document.getElementById("download-blurb");
  const downloadButton = document.getElementById("download-button");
  const downloadMeta = document.getElementById("download-meta");
  const changelogTitle = document.getElementById("latest-changelog-title");
  const changelogDate = document.getElementById("latest-changelog-date");
  const changelogList = document.getElementById("latest-changelog-list");

  if (!downloadButton && !changelogList) {
    return;
  }

  const resolveDownloadHref = (manifest) => {
    const raw = (manifest.downloadUrl || manifest.fileName || "").trim();
    if (!raw) {
      return "latest.json";
    }
    if (/^https?:\/\//i.test(raw)) {
      return raw;
    }
    // Prefer ZIP in versions/ for website visitors when only EXE is listed.
    if (raw.toLowerCase().endsWith(".exe")) {
      const zipName = raw.replace(/\.exe$/i, ".zip");
      if (raw.startsWith("versions/")) {
        return raw.replace(/\.exe$/i, ".zip");
      }
      return `versions/${zipName}`;
    }
    if (raw.startsWith("versions/")) {
      return raw;
    }
    return `versions/${raw}`;
  };

  const formatNotes = (manifest) => {
    const fromArray = Array.isArray(manifest.changelog)
      ? manifest.changelog
          .map((line) => String(line || "").trim())
          .filter(Boolean)
      : [];
    if (fromArray.length > 0) {
      return fromArray;
    }
    const notes = manifest.releaseNotes;
    if (!notes) {
      return ["Latest release."];
    }
    return String(notes)
      .split(/\r?\n/)
      .map((line) => line.replace(/^\s*[-*•]\s*/, "").trim())
      .filter(Boolean);
  };

  fetch(`latest.json?_=${Date.now()}`, { cache: "no-store" })
    .then((response) => {
      if (!response.ok) {
        throw new Error(`latest.json HTTP ${response.status}`);
      }
      return response.json();
    })
    .then((manifest) => {
      const version = String(manifest.version || "").trim() || "unknown";
      const href = resolveDownloadHref(manifest);
      const notes = formatNotes(manifest);

      if (downloadBlurb) {
        downloadBlurb.textContent = `Self-contained Windows x64 build v${version}. Auto-update feed uses the same latest.json on this host.`;
      }
      if (downloadButton) {
        downloadButton.href = href;
        downloadButton.textContent = `Download GitDeployPro ${version}`;
      }
      if (downloadMeta) {
        const published = manifest.publishedUtc
          ? new Date(manifest.publishedUtc).toLocaleDateString(undefined, {
              year: "numeric",
              month: "short",
              day: "numeric",
            })
          : "";
        downloadMeta.textContent = published
          ? `Published ${published}`
          : `File: ${href}`;
      }
      if (changelogTitle) {
        changelogTitle.textContent = `v${version}`;
      }
      if (changelogDate) {
        changelogDate.textContent = manifest.publishedUtc
          ? `Released ${new Date(manifest.publishedUtc).toLocaleDateString(undefined, {
              year: "numeric",
              month: "short",
            })}`
          : "Latest release";
      }
      if (changelogList) {
        changelogList.innerHTML = "";
        notes.forEach((note) => {
          const li = document.createElement("li");
          li.textContent = note;
          changelogList.appendChild(li);
        });
      }
    })
    .catch((error) => {
      if (downloadBlurb) {
        downloadBlurb.textContent =
          "Could not load latest.json. Upload site/latest.json with the release.";
      }
      if (downloadButton) {
        downloadButton.textContent = "Download unavailable";
        downloadButton.removeAttribute("href");
      }
      if (changelogDate) {
        changelogDate.textContent = "Unavailable";
      }
      if (changelogList) {
        changelogList.innerHTML = `<li>${error.message || "Failed to load release notes."}</li>`;
      }
    });
})();
