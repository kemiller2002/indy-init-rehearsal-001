// Capture instrument for EX-FORMA-INDY-2026-001. Same code for every arm.
// usage: node capture.mjs <baseUrl> <browser: chromium|webkit> <fixture.json> <outDir>
// Writes <outDir>/<browser>/shots/*.png and <outDir>/<browser>/captures.jsonl,
// axe.jsonl, keyboard.jsonl.
import { appendFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import AxeBuilder from "@axe-core/playwright";
import { chromium, webkit } from "@playwright/test";

const [base, browserName, fixturePath, outRoot] = process.argv.slice(2);
const fixture = JSON.parse(readFileSync(fixturePath, "utf8"));
const out = join(outRoot, browserName);
mkdirSync(join(out, "shots"), { recursive: true });
for (const f of ["captures.jsonl", "axe.jsonl", "keyboard.jsonl"]) writeFileSync(join(out, f), "");
const log = (file, row) => appendFileSync(join(out, file), JSON.stringify(row) + "\n");

const VIEWPORTS = (process.env.EX_VIEWPORTS ?? "320,375,768,1024,1440").split(",").map(Number);
const VARIANTS = browserName === "chromium" ? ["default", "text200", "forced", "reduced"] : ["default", "text200", "reduced"];
const AXE_VIEWPORTS = [375, 1440];
const KEYBOARD_VIEWPORTS = [375, 1440];
const id = fixture.investigation;
const STATES = [
  { name: "home", hash: "#/", target: null, ready: "#list-title" },
  { name: "investigation", hash: `#/i/${id}`, target: null, ready: "[data-testid=resolve-panel]" },
  { name: "hypotheses", hash: `#/i/${id}`, target: "section[aria-labelledby=hyp-title]", ready: "[data-testid=hypothesis]" },
  { name: "contradiction", hash: `#/i/${id}`, target: "section[aria-labelledby=rel-title]", ready: "[data-testid=relationship]" },
  { name: "blocked-resolution", hash: `#/i/${id}`, target: "[data-testid=resolve-panel]", ready: "[data-testid=resolve-blocker]" },
  { name: "report", hash: `#/i/${id}/report`, target: null, ready: "[data-testid=report-kind]" },
  { name: "settings", hash: "#/settings", target: null, ready: "form" }
];

const ONLY = process.env.EX_STATES ? process.env.EX_STATES.split(",") : null;
const ACTIVE = ONLY ? STATES.filter((s) => ONLY.includes(s.name)) : STATES;
const preinstalled = "/opt/pw-browsers/chromium";
const launcher = browserName === "webkit" ? webkit : chromium;
const browser = await launcher.launch(browserName === "chromium" && existsSync(preinstalled) ? { executablePath: preinstalled } : {});

const contextFor = async (width, variant) => {
  const context = await browser.newContext({
    viewport: { width, height: 900 },
    deviceScaleFactor: 1,
    reducedMotion: variant === "reduced" ? "reduce" : "no-preference",
    forcedColors: variant === "forced" ? "active" : "none",
    colorScheme: "light",
    // The app's CSP forbids inline styles; the 200% text variant needs one injected rule.
    bypassCSP: variant === "text200"
  });
  await context.addInitScript(({ storage, text200 }) => {
    if (!sessionStorage.getItem("ex-fixture-loaded")) {
      for (const k of Object.keys(localStorage)) if (k.startsWith("rci")) localStorage.removeItem(k);
      for (const [k, v] of Object.entries(storage)) localStorage.setItem(k, v);
      sessionStorage.setItem("ex-fixture-loaded", "1");
    }
    if (text200) {
      const apply = () => {
        const s = document.createElement("style");
        s.setAttribute("data-ex", "text200");
        s.textContent = "html{font-size:200% !important}";
        document.head.append(s);
      };
      if (document.head) apply(); else document.addEventListener("DOMContentLoaded", apply, { once: true });
    }
  }, { storage: fixture.storage, text200: variant === "text200" });
  const page = await context.newPage();
  await page.clock.setFixedTime(new Date(fixture.clock));
  return { context, page };
};

const settle = async (page, state) => {
  await page.goto(base + state.hash);
  await page.locator("html[data-kernel=running]").waitFor({ timeout: 60_000 });
  await page.locator(state.ready).first().waitFor({ state: "attached", timeout: 30_000 });
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(250);
};

// Geometry, design-system usage and motion, measured in the page.
const measure = (targetSelector) => {
  const root = targetSelector ? document.querySelector(targetSelector) : document.body;
  const vw = document.documentElement.clientWidth;
  const describe = (el) => el.tagName.toLowerCase() + (el.id ? "#" + el.id : "") + (el.classList.length ? "." + [...el.classList].slice(0, 3).join(".") : "") + (el.dataset.testid ? `[${el.dataset.testid}]` : "");
  const visible = (el) => { const r = el.getBoundingClientRect(); const cs = getComputedStyle(el); return r.width > 0 && r.height > 0 && cs.visibility !== "hidden" && cs.display !== "none"; };
  const inScroller = (el) => { for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) { const o = getComputedStyle(p).overflowX; if (o === "auto" || o === "scroll" || o === "hidden" || o === "clip") return true; } return false; };
  const all = [...root.querySelectorAll("*")].filter(visible);
  const beyond = all.filter((el) => el.getBoundingClientRect().right > vw + 1 && !inScroller(el));
  const clipped = all.filter((el) => {
    const cs = getComputedStyle(el);
    return cs.display !== "inline" && cs.overflowX === "visible" && el.clientWidth > 0 && el.scrollWidth > el.clientWidth + 1 && !["svg", "path", "g"].includes(el.tagName.toLowerCase());
  });
  const singleLine = all.filter((el) => el.matches("input:not([type=checkbox]):not([type=radio]):not([type=hidden]):not([type=file]), select:not([multiple]):not([size])"));
  const heights = singleLine.map((el) => Math.round(el.getBoundingClientRect().height * 100) / 100);
  // Side-by-side peer controls: overlapping vertical ranges, distinct columns, same group.
  const controls = all.filter((el) => el.matches("input:not([type=hidden]):not([type=checkbox]):not([type=radio]), select, textarea"));
  const groupOf = (el) => el.closest("form, fieldset, .ef-form-row, [role=group], section") ?? document.body;
  let maxMisalign = 0, misalignedPairs = 0, peerPairs = 0;
  for (let i = 0; i < controls.length; i++) for (let j = i + 1; j < controls.length; j++) {
    const a = controls[i], b = controls[j];
    if (groupOf(a) !== groupOf(b)) continue;
    const ra = a.getBoundingClientRect(), rb = b.getBoundingClientRect();
    const sideBySide = (ra.right <= rb.left + 1 || rb.right <= ra.left + 1) && ra.top < rb.bottom && rb.top < ra.bottom;
    if (!sideBySide) continue;
    peerPairs++;
    const d = Math.abs(ra.top - rb.top);
    maxMisalign = Math.max(maxMisalign, d);
    if (d > 2) misalignedPairs++;
  }
  const classes = new Map();
  const tags = new Set();
  for (const el of root.querySelectorAll("*")) {
    for (const c of el.classList) classes.set(c, (classes.get(c) ?? 0) + 1);
    if (el.tagName.includes("-")) tags.add(el.tagName.toLowerCase());
  }
  const efElements = [...root.querySelectorAll("*")].filter((el) => [...el.classList].some((c) => c.startsWith("ef-")) || el.tagName.toLowerCase().startsWith("ef-")).length;
  const appOnlyElements = [...root.querySelectorAll("[class]")].filter((el) => el.classList.length && ![...el.classList].some((c) => c.startsWith("ef-")) && !el.tagName.toLowerCase().startsWith("ef-")).length;
  const animating = all.filter((el) => {
    const cs = getComputedStyle(el);
    const secs = (v) => Math.max(0, ...v.split(",").map((x) => parseFloat(x) * (x.trim().endsWith("ms") ? 0.001 : 1)));
    return (cs.animationName !== "none" && secs(cs.animationDuration) > 0.01) || secs(cs.transitionDuration) > 0.01;
  }).length;
  const forcedInvisible = all.filter((el) => el.matches("input:not([type=hidden]), select, textarea, button") && (() => { const cs = getComputedStyle(el); return cs.borderTopStyle === "none" && cs.outlineStyle === "none"; })()).length;
  const rect = root.getBoundingClientRect();
  return {
    pageOverflowX: document.documentElement.scrollWidth - vw,
    beyondViewport: beyond.length, beyondSamples: beyond.slice(0, 8).map(describe),
    clipped: clipped.length, clippedSamples: clipped.slice(0, 8).map(describe),
    controlHeights: { n: heights.length, min: heights.length ? Math.min(...heights) : null, max: heights.length ? Math.max(...heights) : null, distinct: [...new Set(heights)].length },
    peers: { pairs: peerPairs, misalignedOver2px: misalignedPairs, maxTopDelta: Math.round(maxMisalign * 100) / 100 },
    efClasses: [...classes.keys()].filter((c) => c.startsWith("ef-")).sort(),
    appClasses: [...classes.keys()].filter((c) => !c.startsWith("ef-")).sort(),
    customElements: [...tags].sort(),
    efElements, appOnlyElements,
    animatingOver10ms: animating,
    controlsWithoutBorderOrOutline: forcedInvisible,
    targetSize: { width: Math.round(rect.width), height: Math.round(rect.height) },
    fontSizeRoot: getComputedStyle(document.documentElement).fontSize
  };
};

const started = Date.now();
for (const width of VIEWPORTS) for (const variant of VARIANTS) {
  const { context, page } = await contextFor(width, variant);
  for (const state of ACTIVE) {
    const key = `${state.name}__${width}__${variant}`;
    try {
      await settle(page, state);
      const m = await page.evaluate(measure, state.target);
      const shot = join(out, "shots", key + ".png");
      // The footer prints the arm's own build SHA; it is masked so arms differ only by treatment.
      const mask = [page.locator("[data-build]")];
      // WebKit cannot capture images taller than 32767px; such pages are clipped to that height
      // (same rule in every arm) and the clip is recorded. Geometry is measured on the whole page.
      const MAX = 32000;
      const pageHeight = await page.evaluate(() => document.documentElement.scrollHeight);
      const clippedShot = !state.target && pageHeight > MAX;
      let screenshotError = null;
      try {
        if (state.target) await page.locator(state.target).first().screenshot({ path: shot, animations: "disabled", caret: "hide", mask, maskColor: "#ff00ff" });
        else await page.screenshot({ path: shot, fullPage: true, animations: "disabled", caret: "hide", mask, maskColor: "#ff00ff", ...(clippedShot ? { clip: { x: 0, y: 0, width, height: MAX } } : {}) });
      } catch (error) {
        screenshotError = String(error).split("\n")[0].slice(0, 200);
      }
      log("captures.jsonl", { browser: browserName, state: state.name, width, variant, ok: true, pageHeight, screenshotClippedTo: clippedShot ? MAX : null, screenshotError, ...m });
      if (variant === "default" && AXE_VIEWPORTS.includes(width)) {
        const axe = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
        log("axe.jsonl", { browser: browserName, state: state.name, width, violations: axe.violations.map((v) => ({ id: v.id, impact: v.impact, nodes: v.nodes.length, targets: v.nodes.slice(0, 5).map((n) => n.target.join(" ")) })), passes: axe.passes.length, incomplete: axe.incomplete.length });
      }
    } catch (error) {
      log("captures.jsonl", { browser: browserName, state: state.name, width, variant, ok: false, error: String(error).slice(0, 400) });
    }
  }
  if (variant === "default" && KEYBOARD_VIEWPORTS.includes(width) && !ONLY) {
    for (const state of [STATES[0], STATES[1]]) {
      await settle(page, state);
      await page.evaluate(() => window.scrollTo(0, 0));
      const stops = [];
      for (let i = 0; i < 80; i++) {
        await page.keyboard.press("Tab");
        const s = await page.evaluate(() => {
          const el = document.activeElement;
          if (!el || el === document.body) return { tag: "body" };
          const cs = getComputedStyle(el);
          const r = el.getBoundingClientRect();
          const indicator = (cs.outlineStyle !== "none" && parseFloat(cs.outlineWidth) > 0) || cs.boxShadow !== "none";
          return { tag: el.tagName.toLowerCase(), name: (el.getAttribute("aria-label") || el.textContent || el.getAttribute("name") || "").trim().slice(0, 40), indicator, inViewport: r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth, focusVisible: el.matches(":focus-visible") };
        });
        stops.push(s);
      }
      const sequence = stops.map((s) => `${s.tag}:${s.name ?? ""}`);
      const firstRepeat = sequence.findIndex((s, i) => i > 0 && s === sequence[0]);
      log("keyboard.jsonl", {
        browser: browserName, state: state.name, width,
        stops: stops.length, distinct: new Set(sequence).size,
        withoutIndicator: stops.filter((s) => s.tag !== "body" && !s.indicator).length,
        offscreen: stops.filter((s) => s.tag !== "body" && !s.inViewport).length,
        cycleLength: firstRepeat > 0 ? firstRepeat : null,
        sample: stops.slice(0, 12)
      });
    }
  }
  await context.close();
}
await browser.close();
writeFileSync(join(out, "timing.json"), JSON.stringify({ browser: browserName, seconds: (Date.now() - started) / 1000 }));
console.log(`capture ${browserName} done in ${(Date.now() - started) / 1000}s`);
