// Aggregates one arm's raw evidence into summary.json.
// usage: node summarize.mjs <armOutDir>   (expects chromium/, webkit/, css.json, tests/, timing.json)
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const [dir] = process.argv.slice(2);
const jsonl = (p) => (existsSync(p) ? readFileSync(p, "utf8").split("\n").filter(Boolean).map((l) => JSON.parse(l)) : null);
const json = (p) => (existsSync(p) ? JSON.parse(readFileSync(p, "utf8")) : null);
const UNKNOWN = "UNKNOWN";

const trx = (p) => {
  if (!existsSync(p)) return UNKNOWN;
  const x = readFileSync(p, "utf8");
  const c = x.match(/<Counters[^>]*total="(\d+)"[^>]*executed="(\d+)"[^>]*passed="(\d+)"[^>]*failed="(\d+)"/);
  return c ? { total: +c[1], executed: +c[2], passed: +c[3], failed: +c[4] } : UNKNOWN;
};
const playwright = (p) => {
  const r = json(p);
  if (!r) return UNKNOWN;
  const tests = [];
  const walk = (s) => { (s.specs ?? []).forEach((sp) => sp.tests.forEach((t) => tests.push({ title: sp.title, project: t.projectName, status: t.status }))); (s.suites ?? []).forEach(walk); };
  r.suites.forEach(walk);
  const byStatus = tests.reduce((a, t) => ({ ...a, [t.status]: (a[t.status] ?? 0) + 1 }), {});
  return { total: tests.length, byStatus, failing: tests.filter((t) => t.status === "unexpected").map((t) => `${t.project} › ${t.title}`), stats: r.stats };
};

const perBrowser = (browser) => {
  const caps = jsonl(join(dir, browser, "captures.jsonl"));
  if (!caps) return UNKNOWN;
  const ok = caps.filter((c) => c.ok);
  const byVariant = {};
  for (const c of ok) {
    const v = (byVariant[c.variant] ??= { captures: 0, fullPageCaptures: 0, pagesOverflowing: 0, overflowPxTotal: 0, beyondViewportFullPage: 0, overflowingBoxes: 0, clippedBoxes: 0, peerPairs: 0, misalignedPairs: 0, maxPeerTopDelta: 0, controlHeightSpreadMax: 0, animatingOver10ms: 0, controlsWithoutBorderOrOutline: 0 });
    v.captures++;
    // Page-level measures count each URL x width once (full-page states only).
    if (c.pageLevel) {
      v.fullPageCaptures++;
      if (c.pageOverflowX > 0) v.pagesOverflowing++;
      v.overflowPxTotal += Math.max(0, c.pageOverflowX);
      v.beyondViewportFullPage += c.beyondViewport;
      v.overflowingBoxes += c.overflowingBoxes;
      v.clippedBoxes += c.clippedBoxes;
      v.animatingOver10ms += c.animatingOver10ms;
      v.controlsWithoutBorderOrOutline += c.controlsWithoutBorderOrOutline;
    }
    v.peerPairs += c.peers.pairs;
    v.misalignedPairs += c.peers.misalignedOver2px;
    v.maxPeerTopDelta = Math.max(v.maxPeerTopDelta, c.peers.maxTopDelta);
    if (c.controlHeights.n) v.controlHeightSpreadMax = Math.max(v.controlHeightSpreadMax, Math.round((c.controlHeights.max - c.controlHeights.min) * 100) / 100);
  }
  const sections = ok.filter((c) => !c.pageLevel).map((c) => ({ state: c.state, width: c.width, variant: c.variant, overflowingBoxes: c.overflowingBoxes, clippedBoxes: c.clippedBoxes, beyondViewport: c.beyondViewport }));
  const axe = jsonl(join(dir, browser, "axe.jsonl")) ?? [];
  const impacts = {};
  const rules = {};
  const incomplete = {};
  for (const a of axe) {
    for (const v of a.violations) { impacts[v.impact] = (impacts[v.impact] ?? 0) + v.nodes; rules[v.id] = (rules[v.id] ?? 0) + v.nodes; }
    for (const r of a.incompleteRules ?? []) incomplete[r] = (incomplete[r] ?? 0) + 1;
  }
  const kb = jsonl(join(dir, browser, "keyboard.jsonl")) ?? [];
  return {
    captures: caps.length, failedCaptures: caps.filter((c) => !c.ok).map((c) => `${c.state}/${c.width}/${c.variant}: ${c.error}`),
    screenshotErrors: caps.filter((c) => c.screenshotError).map((c) => `${c.state}/${c.width}/${c.variant}: ${c.screenshotError}`),
    screenshotsClipped: caps.filter((c) => c.screenshotClippedTo).map((c) => `${c.state}/${c.width}/${c.variant}`),
    byVariant,
    overflowing: ok.filter((c) => c.pageLevel && c.pageOverflowX > 0).map((c) => `${c.state}/${c.width}/${c.variant}:+${c.pageOverflowX}px`),
    sections,
    axe: { scans: axe.length, scansByVariant: axe.reduce((m, a) => ({ ...m, [a.variant]: (m[a.variant] ?? 0) + 1 }), {}), violationNodesByImpact: impacts, violationNodesByRule: rules, incompleteRuleScans: incomplete },
    keyboard: kb.map((k) => ({ state: k.state, width: k.width, variant: k.variant, presses: k.presses, cycled: k.cycled, distinct: k.distinct, tabbableVisible: k.tabbableVisible, possibleTrap: k.possibleTrap, withoutIndicator: k.withoutIndicator, offscreen: k.offscreen })),
    elementsByState: ok.filter((c) => c.variant === "default" && c.width === 1440).map((c) => ({ state: c.state, efElements: c.efElements, appOnlyElements: c.appOnlyElements })),
    efClassesRendered: [...new Set(ok.flatMap((c) => c.efClasses))].length,
    appClassesRendered: [...new Set(ok.flatMap((c) => c.appClasses))].length,
    captureSeconds: json(join(dir, browser, "timing.json"))?.seconds ?? UNKNOWN
  };
};

const summary = {
  arm: json(join(dir, "arm.json")),
  functional: {
    engine: trx(join(dir, "tests", "engine.trx")),
    browserSuite: playwright(join(dir, "tests", "playwright.json")),
    typecheck: json(join(dir, "tests", "steps.json"))?.typecheck ?? UNKNOWN,
    build: json(join(dir, "tests", "steps.json"))?.build ?? UNKNOWN
  },
  timing: json(join(dir, "tests", "steps.json"))?.seconds ?? UNKNOWN,
  chromium: perBrowser("chromium"),
  webkit: perBrowser("webkit"),
  css: json(join(dir, "css.json")) ?? UNKNOWN
};
writeFileSync(join(dir, "summary.json"), JSON.stringify(summary, null, 2));
console.log(JSON.stringify({ arm: summary.arm, engine: summary.functional.engine, suite: summary.functional.browserSuite === UNKNOWN ? UNKNOWN : summary.functional.browserSuite.byStatus }));
