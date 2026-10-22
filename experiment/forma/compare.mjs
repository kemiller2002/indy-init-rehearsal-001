// Pixel comparison of two arms' captures (same browser, state, width, variant).
// usage: node compare.mjs <armDirFrom> <armDirTo> <label> <outDir>
// armDir = directory containing <browser>/shots/*.png
import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import pixelmatch from "pixelmatch";
import { PNG } from "pngjs";

const [from, to, label, outDir] = process.argv.slice(2);
mkdirSync(join(outDir, "diffs", label), { recursive: true });
const rows = [];
for (const browser of ["chromium", "webkit"]) {
  const dir = join(from, browser, "shots");
  if (!existsSync(dir)) continue;
  for (const file of readdirSync(dir).filter((f) => f.endsWith(".png")).sort()) {
    const other = join(to, browser, "shots", file);
    const [state, width, variant] = file.replace(".png", "").split("__");
    if (!existsSync(other)) { rows.push({ browser, state, width: Number(width), variant, status: "missing-in-target" }); continue; }
    const a = PNG.sync.read(readFileSync(join(dir, file)));
    const b = PNG.sync.read(readFileSync(other));
    const sameSize = a.width === b.width && a.height === b.height;
    // Compare the shared top-left region; a size change is reported separately.
    const w = Math.min(a.width, b.width), h = Math.min(a.height, b.height);
    const crop = (img) => { const out = new PNG({ width: w, height: h }); PNG.bitblt(img, out, 0, 0, w, h, 0, 0); return out; };
    const ca = crop(a), cb = crop(b), diff = new PNG({ width: w, height: h });
    const changed = pixelmatch(ca.data, cb.data, diff.data, w, h, { threshold: 0.1 });
    const ratio = changed / (w * h);
    if (changed > 0 && variant === "default") writeFileSync(join(outDir, "diffs", label, `${browser}__${file}`), PNG.sync.write(diff));
    rows.push({ browser, state, width: Number(width), variant, from: [a.width, a.height], to: [b.width, b.height], sameSize, changedPixels: changed, changedRatio: Math.round(ratio * 1e6) / 1e6 });
  }
}
writeFileSync(join(outDir, `visual-${label}.json`), JSON.stringify(rows, null, 1));
const changed = rows.filter((r) => r.changedPixels > 0 || r.sameSize === false);
console.log(JSON.stringify({ label, compared: rows.length, identical: rows.filter((r) => r.changedPixels === 0 && r.sameSize).length, changed: changed.length, missing: rows.filter((r) => r.status).length }));
