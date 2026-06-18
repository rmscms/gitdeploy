(() => {
  const page = document.body?.dataset?.page || "";
  const navLinks = document.querySelectorAll("[data-nav]");
  navLinks.forEach((link) => {
    if (link.getAttribute("data-nav") === page) {
      link.classList.add("active");
    }
  });
})();
