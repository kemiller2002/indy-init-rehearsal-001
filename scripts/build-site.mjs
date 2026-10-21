// Builds the deployable static site into dist/ (GitHub Pages, any sub-path).
//
//   dist/index.html            the page (build sha stamped into the footer)
//   dist/main.js, app.css      composition root and layout
//   dist/vendor/limen/         Limen kernel (.js only, from node_modules)
//   dist/vendor/forma/all.css  Forma
//   dist/vendor/folio/         Folio components + print.css
//   dist/wasm/_framework/      the .NET WebAssembly engine (dotnet publish)
//   dist/.nojekyll             Pages must not run Jekyll (it drops _framework)
//   dist/build-info.json       { commit, builtAt }
//   dist/factory.json          requirements, slices, work items at build time
//
// Every URL in the output is relative; nothing points into node_modules.
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { cpSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, relative } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const dist = join(root, "dist");
const kernel = join(root, "src", "kernel");
const modules = join(root, "node_modules", "@echelon-foundry");

const run = (command, args) => execFileSync(command, args, { cwd: root, stdio: "inherit" });

const commit = (() => {
  if (process.env.GITHUB_SHA) return process.env.GITHUB_SHA;
  try {
    return execFileSync("git", ["rev-parse", "HEAD"], { cwd: root }).toString().trim();
  } catch {
    return "unknown";
  }
})();

/** Copy a tree, keeping only files the filter accepts. */
const copyTree = (from, to, accept) => {
  for (const name of readdirSync(from)) {
    const source = join(from, name);
    const target = join(to, name);
    if (statSync(source).isDirectory()) copyTree(source, target, accept);
    else if (accept(name)) {
      mkdirSync(dirname(target), { recursive: true });
      cpSync(source, target);
    }
  }
};

rmSync(dist, { recursive: true, force: true });
mkdirSync(dist, { recursive: true });

// 1. The engine: dotnet publish (Release) of the WebAssembly host.
const publishDir = join(root, "src", "wasm", "bin", "site-publish");
rmSync(publishDir, { recursive: true, force: true });
const dotnet = process.env.DOTNET ?? (existsSync("/root/.dotnet/dotnet") && !process.env.CI ? "/root/.dotnet/dotnet" : "dotnet");
run(dotnet, ["publish", join(root, "src", "wasm", "App.Wasm.csproj"), "-c", "Release", "-o", publishDir, "--nologo"]);
const framework = join(publishDir, "wwwroot", "_framework");
if (!existsSync(join(framework, "dotnet.js"))) throw new Error(`dotnet publish produced no ${relative(root, framework)}/dotnet.js`);
// Pages serves no content negotiation, so the pre-compressed copies are dead weight.
copyTree(framework, join(dist, "wasm", "_framework"), (name) => !name.endsWith(".br") && !name.endsWith(".gz"));

// 2. Vendor copies.
copyTree(join(modules, "limen", "dist"), join(dist, "vendor", "limen"), (name) => name.endsWith(".js"));
// Forma is consumed by its canonical package name (the foundations verifier looks for it).
const formaPackage = "@echelon-foundry/design-system";
cpSync(join(root, "node_modules", formaPackage, "dist", "all.css"), join(dist, "vendor", "forma", "all.css"));
copyTree(join(modules, "print-components", "src", "components"), join(dist, "vendor", "folio", "components"), (name) => name.endsWith(".js"));
cpSync(join(modules, "print-components", "src", "styles", "print.css"), join(dist, "vendor", "folio", "print.css"));

// 3. The page.
const shortSha = commit.slice(0, 7);
const source = readFileSync(join(kernel, "index.html"), "utf8");
// CSP: the inline import map is allowed by its exact sha256 hash, nothing else inline.
const importMap = source.match(/<script type="importmap">([\s\S]*?)<\/script>/);
if (importMap === null) throw new Error("index.html has no import map");
const importMapHash = "sha256-" + createHash("sha256").update(importMap[1], "utf8").digest("base64");
const html = source.replaceAll("__BUILD_SHA__", shortSha).replaceAll("__IMPORTMAP_HASH__", importMapHash);
writeFileSync(join(dist, "index.html"), html);
cpSync(join(kernel, "main.js"), join(dist, "main.js"));
cpSync(join(kernel, "app.css"), join(dist, "app.css"));
writeFileSync(join(dist, ".nojekyll"), "");
// The route inventory (echelon.routes/v1) is published with the site.
mkdirSync(join(dist, ".echelon"), { recursive: true });
cpSync(join(root, ".echelon", "routes.json"), join(dist, ".echelon", "routes.json"));
const builtAt = new Date().toISOString();
writeFileSync(join(dist, "build-info.json"), JSON.stringify({ commit, builtAt }, null, 2) + "\n");

// 3b. Factory proof (S20/S21, R14.3, R16.5): the factory's state AT BUILD TIME.
// Requirements in scope come from the frozen inventory, their slice from the
// Conditor trace, work-item states from the Praxis queue, and test counts from
// test-results/traceability.json only when it already exists (the deploy
// workflow writes it before the build; elsewhere it is usually absent).
const readJson = (path) => (existsSync(join(root, path)) ? JSON.parse(readFileSync(join(root, path), "utf8")) : null);
const inventory = readJson("evidence/requirements-inventory.json");
const trace = readJson(".ros/work/attachments/COND-MISSION-001/1-requirements-trace.json");
const queue = readJson(".ros/work/queue.json");
const traceability = readJson("test-results/traceability.json");
const sliceOf = new Map((trace?.requirements ?? []).map((r) => [r.id, r.workItem]));
// test-results/traceability.json (scripts/trace-requirements.py): totals and
// per-requirement coverage, when the workflow wrote it before this build.
const coverageOf = new Map((traceability?.requirements ?? []).map((r) => [r.sourceId, r.status]));
const testsOf = (t) => {
  if (t === null || typeof t !== "object" || t.totals === undefined) return null;
  const total = Number(t.totals.tests ?? 0);
  const passed = Number(t.totals.passed ?? 0);
  const summary = t.summary ?? {};
  return {
    commit: typeof t.commit === "string" ? t.commit : "",
    tests: total,
    passed,
    failed: total - passed,
    tested: Number(summary.tested ?? 0),
    untested: Number(summary.untested ?? 0),
    failing: Number(summary.failing ?? 0)
  };
};
const tests = testsOf(traceability);
const factory = {
  schema: "rci.factory/v1",
  label: "Factory state at build time",
  commit,
  builtAt,
  experiment: inventory?.experiment ?? "",
  requirements: (inventory?.requirements ?? [])
    .filter((r) => r.scope === "scoped" || r.scope === "scoped-factory")
    .map((r) => ({ id: r.id, sourceId: r.sourceId, title: r.title, scope: r.scope, slice: sliceOf.get(r.sourceId) ?? "", coverage: coverageOf.get(r.sourceId) ?? "" })),
  workItems: (queue?.items ?? []).map((i) => ({ id: i.id, title: i.title, status: i.status })),
  tests,
  // Build status as far as the build can know it: the recorded test run, if any.
  buildStatus: tests === null ? "unknown" : tests.failed === 0 ? "passed" : "failing"
};
writeFileSync(join(dist, "factory.json"), JSON.stringify(factory, null, 2) + "\n");

// 4. Guard: the site must not reach outside itself.
const offenders = [];
for (const file of ["index.html", "main.js"]) {
  const text = readFileSync(join(dist, file), "utf8");
  if (text.includes("node_modules")) offenders.push(file);
}
if (offenders.length > 0) throw new Error(`dist references node_modules: ${offenders.join(", ")}`);
console.log(`Site built in ${relative(root, dist)}/ (build ${shortSha}).`);
