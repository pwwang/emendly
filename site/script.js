// Emendly landing page — no build step, no dependencies.
//
// To enable the Pro checkout, set PRO_CHECKOUT_URL to your store link
// (Polar.sh, Lemon Squeezy, a GitHub Sponsors page, …). While it is empty the
// button opens a friendly "not open yet" dialog instead of a broken 404.

const PRO_CHECKOUT_URL = "";

const SHOTS = {
  menu: {
    src: "assets/menu.png",
    width: 380,
    height: 351,
    caption:
      "The menu fits the selection — a passage gets five actions; one word gets three.",
    alt:
      "Emendly action menu floating over a text field, offering five passage actions.",
  },
  streaming: {
    src: "assets/streaming.png",
    width: 380,
    height: 420,
    caption:
      "Results stream in place — read-only while generating, then Replace, Copy or Back.",
    alt:
      "A proofread result streaming into the Emendly panel with Replace, Copy and Back controls.",
  },
  result: {
    src: "assets/result.png",
    width: 380,
    height: 420,
    caption:
      "Enter replaces the selection through the clipboard, then restores focus to the app you came from.",
    alt:
      "A finished proofread result with Replace and Back, ready to write into the original field.",
  },
  settings: {
    src: "assets/settings.png",
    width: 420,
    height: 454,
    caption:
      "Shortcut, language, endpoint, model — with the hotkey set by pressing it, not by typing its name.",
    alt:
      "The Emendly settings window with shortcut, target language, base URL and model fields.",
  },
};

const ORDER = ["menu", "streaming", "result", "settings"];

function initTabs() {
  const tabs = Array.from(document.querySelectorAll(".tab[data-shot]"));
  if (tabs.length === 0) {
    return;
  }

  const image = document.getElementById("shot-image");
  const caption = document.getElementById("shot-caption");
  const index = document.getElementById("shot-index");
  const panel = document.getElementById("shot-panel");
  const live = document.getElementById("live-region");

  let current = "menu";

  function select(key, { focus } = {}) {
    const shot = SHOTS[key];
    if (!shot || key === current || !image) {
      return;
    }
    current = key;

    tabs.forEach((tab) => {
      const isActive = tab.dataset.shot === key;
      tab.setAttribute("aria-selected", String(isActive));
      tab.tabIndex = isActive ? 0 : -1;
    });

    if (panel) {
      panel.setAttribute("aria-labelledby", "tab-" + key);
    }

    image.classList.add("is-swapping");

    const swap = () => {
      image.src = shot.src;
      image.width = shot.width;
      image.height = shot.height;
      image.alt = shot.alt;
      image.classList.remove("is-swapping");
    };

    if (typeof image.decode === "function") {
      const probe = new Image();
      probe.src = shot.src;
      probe
        .decode()
        .then(swap)
        .catch(swap);
    } else {
      image.addEventListener("load", swap, { once: true });
      image.src = shot.src;
      image.width = shot.width;
      image.height = shot.height;
      image.alt = shot.alt;
    }

    if (caption) {
      caption.textContent = shot.caption;
    }
    if (index) {
      index.textContent = String(ORDER.indexOf(key) + 1).padStart(2, "0");
    }
    if (live) {
      live.textContent = shot.caption;
    }
    if (focus) {
      const active = tabs.find((tab) => tab.dataset.shot === key);
      if (active) {
        active.focus();
      }
    }
  }

  tabs.forEach((tab, i) => {
    tab.addEventListener("click", () => select(tab.dataset.shot));

    tab.addEventListener("keydown", (event) => {
      let next = null;
      if (event.key === "ArrowRight" || event.key === "ArrowDown") {
        next = tabs[(i + 1) % tabs.length];
      } else if (event.key === "ArrowLeft" || event.key === "ArrowUp") {
        next = tabs[(i - 1 + tabs.length) % tabs.length];
      } else if (event.key === "Home") {
        next = tabs[0];
      } else if (event.key === "End") {
        next = tabs[tabs.length - 1];
      }

      if (next) {
        event.preventDefault();
        select(next.dataset.shot, { focus: true });
      }
    });
  });
}

async function copyText(text) {
  if (navigator.clipboard && window.isSecureContext) {
    await navigator.clipboard.writeText(text);
    return;
  }

  // Fallback for file:// previews and older browsers.
  const scratch = document.createElement("textarea");
  scratch.value = text;
  scratch.setAttribute("readonly", "");
  scratch.style.position = "fixed";
  scratch.style.opacity = "0";
  document.body.appendChild(scratch);
  scratch.select();
  try {
    document.execCommand("copy");
  } finally {
    document.body.removeChild(scratch);
  }
}

function initCopyButtons() {
  const buttons = document.querySelectorAll("[data-copy]");
  const live = document.getElementById("live-region");

  buttons.forEach((button) => {
    const label = button.querySelector(".copy-label");
    let resetTimer = null;

    button.addEventListener("click", async () => {
      const text = button.dataset.copy || "";
      try {
        await copyText(text);
      } catch (error) {
        if (live) {
          live.textContent = "Could not copy — select the command and copy it manually.";
        }
        return;
      }

      button.dataset.copied = "true";
      if (label) {
        label.textContent = "Copied!";
      }
      if (live) {
        live.textContent = "Command copied to clipboard.";
      }

      if (resetTimer) {
        clearTimeout(resetTimer);
      }
      resetTimer = setTimeout(() => {
        delete button.dataset.copied;
        if (label) {
          label.textContent = "Copy";
        }
      }, 2000);
    });
  });
}

function initCheckout() {
  const trigger = document.querySelector("[data-checkout]");
  const dialog = document.getElementById("checkout-dialog");
  if (!trigger) {
    return;
  }

  if (PRO_CHECKOUT_URL) {
    trigger.setAttribute("href", PRO_CHECKOUT_URL);
    trigger.setAttribute("rel", "noopener");
    trigger.removeAttribute("data-checkout");
    return;
  }

  trigger.addEventListener("click", (event) => {
    event.preventDefault();
    if (dialog && typeof dialog.showModal === "function") {
      dialog.showModal();
    } else if (dialog) {
      dialog.setAttribute("open", "");
    }
  });

  if (dialog) {
    dialog.querySelectorAll("[data-close-dialog]").forEach((button) => {
      button.addEventListener("click", () => {
        if (typeof dialog.close === "function") {
          dialog.close();
        } else {
          dialog.removeAttribute("open");
        }
      });
    });

    dialog.addEventListener("click", (event) => {
      if (event.target === dialog && typeof dialog.close === "function") {
        dialog.close();
      }
    });
  }
}

function initYear() {
  const year = document.getElementById("year");
  if (year) {
    year.textContent = String(new Date().getFullYear());
  }
}

function init() {
  initTabs();
  initCopyButtons();
  initCheckout();
  initYear();
}

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", init);
} else {
  init();
}
