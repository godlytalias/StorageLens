const menuToggle = document.querySelector("[data-menu-toggle]");
const menu = document.querySelector("[data-menu]");

if (menuToggle && menu) {
  menuToggle.addEventListener("click", () => {
    const isOpen = menu.getAttribute("data-open") === "true";
    menu.setAttribute("data-open", String(!isOpen));
    menuToggle.setAttribute("aria-expanded", String(!isOpen));
  });

  menu.addEventListener("click", (event) => {
    if (event.target.closest("a")) {
      menu.setAttribute("data-open", "false");
      menuToggle.setAttribute("aria-expanded", "false");
    }
  });
}

const galleryTabs = Array.from(document.querySelectorAll("[data-gallery-tab]"));
const galleryImage = document.querySelector("[data-gallery-image]");
const galleryTitle = document.querySelector("[data-gallery-title]");
const galleryCopy = document.querySelector("[data-gallery-copy]");
const galleryIndex = document.querySelector("[data-gallery-index]");
let activeGalleryIndex = 0;

function selectGalleryItem(index) {
  if (!galleryImage || !galleryTitle || !galleryCopy || !galleryIndex || galleryTabs.length === 0) {
    return;
  }

  activeGalleryIndex = (index + galleryTabs.length) % galleryTabs.length;
  const selected = galleryTabs[activeGalleryIndex];

  galleryImage.src = selected.dataset.src;
  galleryImage.alt = selected.dataset.alt;
  galleryTitle.textContent = selected.dataset.title;
  galleryCopy.textContent = selected.dataset.copy;
  galleryIndex.textContent = `${String(activeGalleryIndex + 1).padStart(2, "0")} / ${String(galleryTabs.length).padStart(2, "0")}`;

  galleryTabs.forEach((tab, tabIndex) => {
    const isSelected = tabIndex === activeGalleryIndex;
    tab.classList.toggle("is-active", isSelected);
    if (isSelected) {
      tab.setAttribute("aria-current", "true");
    } else {
      tab.removeAttribute("aria-current");
    }
  });
}

galleryTabs.forEach((tab, index) => {
  tab.addEventListener("click", () => selectGalleryItem(index));
});

document.querySelector("[data-gallery-prev]")?.addEventListener("click", () => {
  selectGalleryItem(activeGalleryIndex - 1);
});

document.querySelector("[data-gallery-next]")?.addEventListener("click", () => {
  selectGalleryItem(activeGalleryIndex + 1);
});

document.querySelector(".gallery-stage")?.addEventListener("keydown", (event) => {
  if (event.key === "ArrowLeft") {
    selectGalleryItem(activeGalleryIndex - 1);
  } else if (event.key === "ArrowRight") {
    selectGalleryItem(activeGalleryIndex + 1);
  }
});

const year = document.querySelector("[data-current-year]");
if (year) {
  year.textContent = String(new Date().getFullYear());
}
