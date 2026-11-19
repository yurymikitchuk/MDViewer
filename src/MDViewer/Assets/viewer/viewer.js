(() => {
  const content = document.getElementById("content");
  const hljsLight = document.getElementById("hljs-light");
  const hljsDark = document.getElementById("hljs-dark");
  const ZOOM_STEP = 0.1;
  const ZOOM_MIN = 0.5;
  const ZOOM_MAX = 2;
  let mermaidLoaded = false;
  let mermaidLoading = null;
  let findMatches = [];
  let findIndex = -1;
  let zoom = 1;
  let activeTocId = "";
  let tocRaf = 0;

  function applyZoom(value, notify) {
    zoom = Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, value));
    document.documentElement.style.zoom = String(zoom);
    if (notify) post({ type: "zoomChanged", zoom });
  }

  function detectTheme() {
    const forced = document.documentElement.getAttribute("data-theme");
    if (forced === "dark") return "dark";
    if (forced === "light") return "default";
    return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "default";
  }

  function isDarkTheme() {
    return detectTheme() === "dark";
  }

  function applyHighlightTheme() {
    const dark = isDarkTheme();
    if (hljsLight) hljsLight.disabled = dark;
    if (hljsDark) hljsDark.disabled = !dark;
  }

  function setTheme(theme) {
    const value = (theme || "system").toLowerCase();
    if (value === "light" || value === "dark") {
      document.documentElement.setAttribute("data-theme", value);
    } else {
      document.documentElement.removeAttribute("data-theme");
    }
    applyHighlightTheme();
    void refreshMermaidTheme();
  }

  function mermaidOptions() {
    return {
      startOnLoad: false,
      theme: detectTheme(),
      securityLevel: "strict",
      flowchart: { htmlLabels: false, useMaxWidth: true },
      er: { useMaxWidth: true }
    };
  }

  async function refreshMermaidTheme() {
    if (!mermaidLoaded || !window.mermaid) return;
    const hosts = [...content.querySelectorAll(".mermaid[data-source]")];
    if (!hosts.length) return;
    try {
      window.mermaid.initialize(mermaidOptions());
      for (const host of hosts) {
        host.removeAttribute("data-processed");
        host.removeAttribute("data-mermaid-id");
        host.textContent = host.dataset.source || "";
      }
      await withTimeout(window.mermaid.run({ nodes: hosts }), 8000, "Mermaid theme timeout");
    } catch (err) {
      console.error("mermaid theme refresh", err);
    }
  }

  function post(msg) {
    window.chrome?.webview?.postMessage(msg);
  }

  function loadMermaid() {
    if (mermaidLoaded) return Promise.resolve();
    if (mermaidLoading) return mermaidLoading;

    mermaidLoading = new Promise((resolve, reject) => {
      const script = document.createElement("script");
      script.src = "mermaid.min.js";
      script.onload = () => {
        window.mermaid.initialize(mermaidOptions());
        mermaidLoaded = true;
        resolve();
      };
      script.onerror = () => reject(new Error("Mermaid load failed"));
      document.head.appendChild(script);
    });
    return mermaidLoading;
  }

  function collectMermaidSources() {
    const sources = [];
    content.querySelectorAll("pre.mermaid").forEach((pre) => {
      sources.push({ el: pre, text: pre.textContent || "" });
    });
    content.querySelectorAll("pre > code.language-mermaid").forEach((code) => {
      const pre = code.parentElement;
      if (!pre || pre.classList.contains("mermaid")) return;
      sources.push({ el: pre, text: code.textContent || "" });
    });
    return sources;
  }

  function withTimeout(promise, ms, label) {
    let timer;
    const timeout = new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error(label || "timeout")), ms);
    });
    return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
  }

  function markMermaidError(host, err) {
    host.classList.add("mermaid-error");
    const src = host.dataset.source || host.textContent || "";
    host.textContent =
      "Не удалось отрисовать схему.\n\n" +
      (err && err.message ? err.message + "\n\n" : "") +
      src;
  }

  async function renderMermaidBlocks() {
    const sources = collectMermaidSources();
    if (!sources.length) return;
    await withTimeout(loadMermaid(), 15000, "Mermaid load timeout");
    // One-by-one so a single stuck diagram cannot block the whole page.
    for (const item of sources) {
      const host = document.createElement("div");
      host.className = "mermaid";
      const source = item.text.trim();
      host.dataset.source = source;
      host.textContent = source;
      item.el.replaceWith(host);
      try {
        await withTimeout(window.mermaid.run({ nodes: [host] }), 8000, "Mermaid render timeout");
        if (!host.querySelector("svg")) {
          markMermaidError(host, null);
        }
      } catch (err) {
        console.error("mermaid.run", err);
        markMermaidError(host, err);
      }
    }
  }

  function highlightCode() {
    if (!window.hljs) return;
    content.querySelectorAll("pre code").forEach((block) => {
      if (block.classList.contains("language-mermaid")) return;
      try { window.hljs.highlightElement(block); } catch (err) { console.error(err); }
    });
  }

  function unwrapMarkdigMath(tex) {
    // Markdig emits \(...\) for inline and \[...\] for block math.
    let displayMode = false;
    let body = (tex || "").trim();
    const block = body.match(/^\\\[([\s\S]*)\\\]$/);
    const inline = body.match(/^\\\(([\s\S]*)\\\)$/);
    if (block) {
      body = block[1];
      displayMode = true;
    } else if (inline) {
      body = inline[1];
      displayMode = false;
    }
    return { tex: body.trim(), displayMode };
  }

  function renderMath() {
    if (!window.katex) return;
    content.querySelectorAll("span.math, div.math").forEach((el) => {
      const raw = (el.textContent || "").trim();
      if (!raw) return;
      const parsed = unwrapMarkdigMath(raw);
      const displayMode =
        el.tagName === "DIV" ||
        el.classList.contains("math-display") ||
        parsed.displayMode;
      try {
        window.katex.render(parsed.tex, el, {
          throwOnError: false,
          displayMode
        });
      } catch (err) {
        console.error("katex", err);
      }
    });
  }

  function wrapTables() {
    content.querySelectorAll("table").forEach((table) => {
      if (table.parentElement?.classList.contains("table-wrap")) return;
      const wrap = document.createElement("div");
      wrap.className = "table-wrap";
      table.replaceWith(wrap);
      wrap.appendChild(table);
    });
  }

  function headingSelector() {
    return "h1, h2, h3, h4";
  }

  function buildToc() {
    const items = [];
    content.querySelectorAll(headingSelector()).forEach((h, i) => {
      if (!h.id) h.id = "heading-" + i;
      items.push({
        id: h.id,
        level: Number(h.tagName.substring(1)),
        text: (h.textContent || "").trim()
      });
    });
    post({ type: "toc", items });
    updateActiveToc();
  }

  function updateActiveToc() {
    const headings = [...content.querySelectorAll(headingSelector())];
    if (!headings.length) return;
    const offset = 48;
    let current = headings[0];
    for (const h of headings) {
      const top = h.getBoundingClientRect().top;
      if (top <= offset) current = h;
      else break;
    }
    if (current?.id && current.id !== activeTocId) {
      activeTocId = current.id;
      post({ type: "tocActive", id: activeTocId });
    }
  }

  function scheduleTocUpdate() {
    if (tocRaf) return;
    tocRaf = requestAnimationFrame(() => {
      tocRaf = 0;
      updateActiveToc();
    });
  }

  function clearFind() {
    content.querySelectorAll("mark.md-find").forEach((mark) => {
      const parent = mark.parentNode;
      if (!parent) return;
      parent.replaceChild(document.createTextNode(mark.textContent || ""), mark);
      parent.normalize();
    });
    findMatches = [];
    findIndex = -1;
  }

  function walkTextNodes(root, fn) {
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
      acceptNode(node) {
        if (!node.nodeValue || !node.nodeValue.trim()) return NodeFilter.FILTER_REJECT;
        const p = node.parentElement;
        if (!p || p.closest("script, style, .mermaid, .katex")) return NodeFilter.FILTER_REJECT;
        return NodeFilter.FILTER_ACCEPT;
      }
    });
    const nodes = [];
    let n;
    while ((n = walker.nextNode())) nodes.push(n);
    nodes.forEach(fn);
  }

  const MAX_FIND_MATCHES = 500;

  function find(query) {
    clearFind();
    const q = (query || "").trim();
    if (!q) {
      // Empty query = no active search (not "no matches").
      post({ type: "findResult", count: 0, index: -1, cleared: true });
      return;
    }

    const lower = q.toLowerCase();
    let capped = false;
    walkTextNodes(content, (textNode) => {
      if (findMatches.length >= MAX_FIND_MATCHES) {
        capped = true;
        return;
      }

      const text = textNode.nodeValue || "";
      const lowerText = text.toLowerCase();
      let start = 0;
      const parts = [];
      let idx;
      while ((idx = lowerText.indexOf(lower, start)) !== -1) {
        if (findMatches.length >= MAX_FIND_MATCHES) {
          capped = true;
          if (start < text.length) parts.push(document.createTextNode(text.slice(start)));
          break;
        }
        if (idx > start) parts.push(document.createTextNode(text.slice(start, idx)));
        const mark = document.createElement("mark");
        mark.className = "md-find";
        mark.textContent = text.slice(idx, idx + q.length);
        parts.push(mark);
        findMatches.push(mark);
        start = idx + q.length;
      }
      if (!parts.length) return;
      if (!capped && start < text.length) parts.push(document.createTextNode(text.slice(start)));
      const frag = document.createDocumentFragment();
      parts.forEach((p) => frag.appendChild(p));
      textNode.parentNode.replaceChild(frag, textNode);
    });

    findIndex = findMatches.length ? 0 : -1;
    highlightCurrent();
    post({
      type: "findResult",
      count: findMatches.length,
      index: findIndex,
      capped
    });
  }

  function highlightCurrent() {
    findMatches.forEach((m, i) => m.classList.toggle("current", i === findIndex));
    if (findIndex >= 0 && findMatches[findIndex]) {
      findMatches[findIndex].scrollIntoView({ block: "center", behavior: "smooth" });
    }
  }

  function findNext(delta) {
    if (!findMatches.length) return;
    findIndex = (findIndex + delta + findMatches.length) % findMatches.length;
    highlightCurrent();
    post({ type: "findResult", count: findMatches.length, index: findIndex });
  }

  function isMarkdownHref(href) {
    if (!href) return false;
    const clean = href.split("#")[0].split("?")[0].trim();
    if (!clean) return !!href.includes("#");
    if (/^[a-z]+:/i.test(clean)) return false;
    return /\.(md|markdown|mdown)$/i.test(clean);
  }

  function scrollToFragment(fragment, smooth) {
    if (!fragment) return false;
    const id = decodeURIComponent(fragment.replace(/^#/, ""));
    const el = document.getElementById(id);
    if (!el) return false;
    el.scrollIntoView({ behavior: smooth ? "smooth" : "auto", block: "start" });
    return true;
  }

  let contentGen = 0;

  async function setContent(html, hasMermaid, options) {
    const gen = ++contentGen;
    const restoreScroll = !!options?.restoreScroll;
    const skipToc = !!options?.skipToc;
    const fragment = options?.fragment || "";
    const scrollY = restoreScroll && !fragment ? window.scrollY : 0;

    clearFind();
    activeTocId = "";
    content.classList.remove("ready");
    content.innerHTML = html || "";
    wrapTables();
    try { highlightCode(); } catch (err) { console.error(err); }
    try { renderMath(); } catch (err) { console.error(err); }
    if (!skipToc) {
      try { buildToc(); } catch (err) { console.error(err); }
    }
    if (gen !== contentGen) return;

    // Reveal text immediately. Mermaid can be slow (first script load + many diagrams);
    // waiting for it kept #content at opacity:0 and looked like a hang on «Открытие…».
    requestAnimationFrame(() => {
      if (gen !== contentGen) return;
      content.classList.add("ready");
      if (fragment) {
        scrollToFragment(fragment, false);
      } else if (restoreScroll) {
        window.scrollTo(0, scrollY);
      } else {
        // New document: content is swapped in-place, so scrollY would otherwise stick.
        window.scrollTo(0, 0);
      }
    });

    const shouldRender =
      hasMermaid || !!content.querySelector("pre.mermaid, pre > code.language-mermaid");
    if (shouldRender) {
      try {
        await renderMermaidBlocks();
      } catch (err) {
        console.error(err);
      }
    }
  }

  window.chrome?.webview?.addEventListener("message", (event) => {
    const data = event.data;
    if (!data || typeof data !== "object") return;
    switch (data.type) {
      case "setContent":
        setContent(data.html || "", !!data.hasMermaid, {
          restoreScroll: !!data.restoreScroll,
          skipToc: !!data.skipToc,
          fragment: data.fragment || ""
        });
        break;
      case "scrollTo":
        scrollToFragment(data.id || "", true);
        break;
      case "scrollTop":
        window.scrollTo(0, 0);
        break;
      case "find":
        find(data.query || "");
        break;
      case "findNext":
        findNext(1);
        break;
      case "findPrev":
        findNext(-1);
        break;
      case "findClear":
        clearFind();
        post({ type: "findResult", count: 0, index: -1, cleared: true });
        break;
      case "getHtml":
        post({ type: "html", html: content.innerHTML || "" });
        break;
      case "getText":
        post({ type: "text", text: (content.innerText || "").trim() });
        break;
      case "setZoom":
        applyZoom(Number(data.zoom) || 1, false);
        break;
      case "setTheme":
        setTheme(data.theme || "system");
        break;
    }
  });

  content.addEventListener("click", (event) => {
    const link = event.target.closest("a[href]");
    if (!link) return;
    const href = link.getAttribute("href") || "";
    if (href.startsWith("#")) {
      event.preventDefault();
      scrollToFragment(href, true);
      return;
    }
    if (isMarkdownHref(href)) {
      event.preventDefault();
      post({ type: "openMarkdown", href });
    }
  });

  window.addEventListener("scroll", scheduleTocUpdate, { passive: true });
  window.addEventListener("wheel", (event) => {
    if (!event.ctrlKey) return;
    event.preventDefault();
    applyZoom(zoom + (event.deltaY < 0 ? ZOOM_STEP : -ZOOM_STEP), true);
  }, { passive: false });

  applyHighlightTheme();
  post({ type: "ready" });
})();
