// Static CSS / design-system metrics for one arm.
// usage: node css-metrics.mjs <appDir> <capturesJsonl...> > metrics.json
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import postcss from "postcss";

const [appDir, ...captureFiles] = process.argv.slice(2);
const read = (p) => readFileSync(p, "utf8");
const sha256 = (s) => createHash("sha256").update(s).digest("hex");

const cssStats = (css) => {
  const root = postcss.parse(css);
  let rules = 0, declarations = 0, important = 0, hardColors = 0, customPropsDefined = 0, mediaQueries = 0;
  const pairs = new Map();
  const efVars = new Set(), appVars = new Set();
  root.walkAtRules("media", () => { mediaQueries++; });
  root.walkRules(() => { rules++; });
  root.walkDecls((d) => {
    declarations++;
    if (d.important) important++;
    if (d.prop.startsWith("--")) customPropsDefined++;
    const valueNoVar = d.value.replace(/var\([^()]*(\([^()]*\))?[^()]*\)/g, "");
    hardColors += (valueNoVar.match(/#[0-9a-fA-F]{3,8}\b|\b(rgb|rgba|hsl|hsla|oklch|oklab|lab|lch)\(/g) ?? []).length;
    for (const m of d.value.matchAll(/var\(\s*(--[\w-]+)/g)) (m[1].startsWith("--ef-") ? efVars : appVars).add(m[1]);
    const key = `${d.prop}:${d.value}`;
    pairs.set(key, (pairs.get(key) ?? 0) + 1);
  });
  const duplicateDeclarations = [...pairs.values()].filter((n) => n > 1).reduce((a, n) => a + n - 1, 0);
  return { bytes: Buffer.byteLength(css), rules, declarations, important, hardCodedColors: hardColors, customPropsDefined, mediaQueries, duplicateDeclarations, efVarRefs: [...efVars].sort(), appVarRefs: [...appVars].sort() };
};

const pkgDir = join(appDir, "node_modules", "@echelon-foundry", "design-system");
const formaPkg = JSON.parse(read(join(pkgDir, "package.json")));
const lock = JSON.parse(read(join(appDir, "package-lock.json")));
const lockEntry = lock.packages["node_modules/@echelon-foundry/design-system"];
const formaCss = read(join(pkgDir, "dist", "all.css"));
const formaClasses = new Set([...formaCss.matchAll(/\.(ef-[\w-]+)/g)].map((m) => m[1]));
const formaCustomElements = new Set([...formaCss.matchAll(/(?:^|[\s,>+~(])(ef-[a-z][\w-]*)(?=[\s,.:{>+~[)#]|$)/gm)].map((m) => m[1]));
const formaVarsDefined = new Set([...formaCss.matchAll(/(--ef-[\w-]+)\s*:/g)].map((m) => m[1]));

const appCssPath = join(appDir, "src", "kernel", "app.css");
const appCss = read(appCssPath);
const indexHtml = read(join(appDir, "src", "kernel", "index.html"));
const staticClasses = new Set([...indexHtml.matchAll(/class="([^"]*)"/g)].flatMap((m) => m[1].split(/\s+/)).filter(Boolean));
const staticCustomElements = new Set([...indexHtml.matchAll(/<(ef-[a-z][\w-]*)/g)].map((m) => m[1]));

const rendered = { ef: new Set(), app: new Set(), ce: new Set() };
for (const f of captureFiles) for (const line of read(f).split("\n").filter(Boolean)) {
  const r = JSON.parse(line);
  if (!r.ok) continue;
  r.efClasses.forEach((c) => rendered.ef.add(c));
  r.appClasses.forEach((c) => rendered.app.add(c));
  r.customElements.forEach((c) => rendered.ce.add(c));
}
const efUsed = new Set([...rendered.ef, ...[...staticClasses].filter((c) => c.startsWith("ef-"))]);
const undefinedEf = [...efUsed].filter((c) => !formaClasses.has(c)).sort();
const efCustomUsed = new Set([...rendered.ce, ...staticCustomElements].filter((t) => t.startsWith("ef-")));
const app = cssStats(appCss);
const undefinedEfVars = app.efVarRefs.filter((v) => !formaVarsDefined.has(v));

// Forma 0.9.0 color-contract CLI (harness-installed instrument, same for all arms).
const instrumentDir = join(dirname(new URL(import.meta.url).pathname), "node_modules", "@echelon-foundry", "design-system");
const instrumentVersion = JSON.parse(read(join(instrumentDir, "package.json"))).version;
let colorContract;
try {
  const outText = execFileSync(process.execPath, [join(instrumentDir, "tools", "color-contract", "cli.mjs"), "validate", appCssPath], { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
  colorContract = { exit: 0, output: outText.trim().split("\n").slice(0, 60) };
} catch (e) {
  colorContract = { exit: e.status, output: `${e.stdout ?? ""}${e.stderr ?? ""}`.trim().split("\n").slice(0, 60) };
}
const unsupportedTokenFindings = colorContract.output.filter((l) => /--ef-|unknown|reserved|unsupported/i.test(l)).length;

console.log(JSON.stringify({
  forma: { version: formaPkg.version, lockVersion: lockEntry?.version, resolved: lockEntry?.resolved, integrity: lockEntry?.integrity, allCssBytes: Buffer.byteLength(formaCss), allCssSha256: sha256(formaCss), classesDefined: formaClasses.size },
  appCss: { sha256: sha256(appCss), ...app, efVarRefsUndefinedInInstalledForma: undefinedEfVars },
  indexHtml: { bytes: Buffer.byteLength(indexHtml), sha256: sha256(indexHtml), staticClassTokens: staticClasses.size, staticEfClasses: [...staticClasses].filter((c) => c.startsWith("ef-")).length },
  designSystem: {
    efClassesUsed: [...efUsed].sort(), efClassesUsedCount: efUsed.size,
    efClassesUndefinedInInstalledForma: undefinedEf,
    efCustomElementsUsed: [...efCustomUsed].sort(),
    appClassesRendered: [...rendered.app].sort(), appClassesRenderedCount: rendered.app.size
  },
  colorContract: { instrument: `@echelon-foundry/design-system@${instrumentVersion} forma-color validate`, unsupportedTokenFindings, ...colorContract }
}, null, 2));
