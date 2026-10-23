# CAUSA-REHEARSAL-INTEGRATION-001 — Baseline

Recorded 2026-10-23, 08:20 UTC, before any application change. Praxis work item `CAUSA-REHEARSAL-INTEGRATION-001`,
execution `EXE-20261023T081832699Z-2539c4b3`. `./praxis sync check --start`: 0 ahead, 0 behind `origin/main`.

## Identity

| item | value | how verified |
|---|---|---|
| `main` head | `55f49f61c939a3a780cee592f36f732a1328f981` ("Praxis close-out: SLICE-SECURITY-HARDENING and COND-MISSION-001 complete") | `git rev-parse origin/main` |
| Deployed commit | `55f49f61c939a3a780cee592f36f732a1328f981`, `builtAt` `2026-10-21T21:01:33.788Z` | live `https://kevinmmiller.us/indy-init-rehearsal-001/build-info.json` |
| Deploying run | Deploy to GitHub Pages #8, run `39675035959`, `push` on `main`, conclusion **success** | GitHub Actions API |
| R001 accepted release (preserved) | `dffaf082ca6b475231d7b21198876bf0c43fc3df` (deploy run #7 `39672376807`, success); `55f49f6` is its Praxis close-out | Actions history; `evidence/MISSION-RESULT.md` |
| Main equals deployment | **yes**: `main` = deployed = `55f49f6` | both sources above |

## Deployment workflow

`.github/workflows/deploy-pages.yml` runs on `push` to `main` and on `workflow_dispatch`. It has four steps:
1. **verify:** `dotnet build`, then engine tests on the exact commit.
2. **build:** `npm run build`, which stamps `dist/build-info.json` with `GITHUB_SHA`.
3. **deploy:** `actions/deploy-pages`.
4. **accept:** checks that the served `build-info.json` equals the commit, then runs the end-to-end suite against the
   live URL.

Other workflows: `build-and-test.yml`, `echelon-foundations.yml` and `praxis-validation.yml`.

## Versions

| component | version | source |
|---|---|---|
| F# engine | `IndyInitRootCauseInvestigator.Engine` at `55f49f6` (net10.0, Aegis.Core 1.0.0). This is the same commit as the Causa storage contract's semantic oracle (`causa-data/engine.lock.json`). | `src/engine/App.Engine.fsproj` |
| Forma (`@echelon-foundry/design-system`) | **0.5.0** | `package.json`, `.echelon/foundations.json` |
| Limen (`@echelon-foundry/limen`) | **0.9.0** | same |
| Folio (`@echelon-foundry/print-components`) | **0.3.0** (release tarball) | same |
| Ordo / Praxis / Aegis | 1.5.0 / 3.16.0 / 1.0.0 | `.echelon/foundations.json` |
| .NET SDK used here | 10.0.402 (CI: `setup-dotnet` 10.0.x) | local install |

## Application structure

- The **F# engine** lives in `src/engine/`:
  - `Model`, `Commands`, `Decide` (transitions, policy, evolve, decide, `History.validate`);
  - `Shell.fs`, a pure message handler that produces a projected view document and effects;
  - `Sync.fs`, the GitHub adapter;
  - `Routes`, `Report`, `Codec`.
- It is hosted as **.NET WebAssembly** (`src/wasm/Program.cs`, a single `[JSExport] Dispatch`). Entropy and time are
  stamped at that boundary with `DateTimeOffset.UtcNow`, which has 100 ns precision; Causa v1 requires
  millisecond-truncated UTC at the command boundary (CONTRACT §2.9).
- The **Limen kernel** (`src/kernel/main.js`) binds `index.html` templates (`data-text`, `data-each`, `data-if`) to the
  projected document and performs effects: storage, HTTP, navigation, clipboard and connectivity.
- **Routes** (`Shell.ScreenRoute`):
  - `#/` home (investigation list and start form);
  - `#/i/{id}` workspace;
  - `#/i/{id}/report` report;
  - `#/i/{id}/{h|e|u|x|t|c|a|k|p}/{entity}` entity focus;
  - `#/settings` GitHub settings;
  - `#/factory` "How this app was built";
  - missing and refused-link screens.
- **Branding:** "Root-Cause Investigator" in the title, header and meta description.
- **Content at first open:** no investigations ("No investigations yet. Start one above."). There is **no
  demonstration data** and no Causa dataset.

## Persistence (before)

- **Local:** browser storage. One key per investigation holds the rehearsal's own event codec (`rci.events/v1`,
  7-digit fractional timestamps), plus index, settings and sync documents.
- **Remote:** `Sync.fs` writes a single file `investigations/<id>/events.json` with the **Contents API** `PUT`, using a
  blob-SHA concurrency token. 409/422 → conflict; the token is held in memory only.
  - Per Indy-init `requirements/causa/data/v1.0.0/PROVIDER-GITHUB.md` §11 this adapter is **non-conforming** to Causa
    storage v1.0.0: no atomic three-file commit, wrong layout and wire format, no `unknown`/`refused`/`failed` states.
- **Causa v1.0.0 storage:** not implemented.

## Tests (before)

| suite | result | where |
|---|---|---|
| Engine tests (`tests/App.Engine.Tests`) | **154/154 passed** | local, .NET 10.0.402 |
| Browser suite (Playwright 1.63.0, `tests/browser`) | **41/41 passed** across 3 projects (chromium-desktop, chromium-ipad-portrait, chromium-ipad-landscape; Chromium emulation, not Safari) | local, `npm run build` then `npx playwright test` |
| WebKit project | **not run locally**: Playwright WebKit is not installed in this environment. CI installs it. R001 recorded 72/72 across 4 projects including WebKit. | `playwright.config.js` |
| Firefox | **no project configured** | `playwright.config.js` |

## Accessibility (before)

- `AC-14` (axe-core 4.13.0 via `@axe-core/playwright`): **no serious or critical violations** on home, workspace and
  report. It passed in all 3 local projects.
- The EX-FORMA-INDY-2026-001 measurements for this exact baseline (arm A, Forma 0.5.0) at 200% text (root font-size
  proxy):
  - 6 of 20 pages overflow horizontally (1,172 px Chromium);
  - 153 elements extend beyond the viewport;
  - axe violations: 0.
- Keyboard: R001 suite walk, no trap.

## Console and network (live site, before)

- One 404 per cold load: `favicon.ico`. The site declares no icon, so the browser requests one. There are no other
  console errors in desktop, tablet (834×1194, 1194×834) or mobile (390×844) loads.

## Screenshots (before)

`screenshots/before/before-desktop.png` (1440×900), `before-tablet.png` (834×1194), `before-tablet-landscape.png`
(1194×834), `before-mobile.png` (390×844). All are of the live site at `55f49f6`.

## R001 evidence to preserve

`evidence/MISSION-RESULT.md`, `evidence/bootstrap/`, `evidence/slices/`, `evidence/requirements-inventory.json` and
`evidence/traceability-local.json`, plus the R001 release commit `dffaf08` and its deploy run. This mission adds only
`evidence/causa-integration/` and does not edit those files.
