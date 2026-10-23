# CAUSA-REHEARSAL-INTEGRATION-001 — Forma upgrade (Phase 4)

Forma (`@echelon-foundry/design-system`) goes from **0.5.0 to 0.9.0**, following the recommendation of
EX-FORMA-INDY-2026-001 (research-documents `research/experiments/EX-FORMA-INDY-2026-001/RESULT.md`, classification
PARTIAL). The experiment ran in isolated arms. This mission ports its verified end state into the rehearsal app. Only
application paths were taken from the experiment's commits, not its `.ros` Praxis state.

## What was adopted

| commit here | from experiment | change | why |
|---|---|---|---|
| `89a4bb4` | arm B (`fb30278`) | `package.json` / lock: Forma 0.5.0 → 0.9.0, version only | newest verified release (signature, SLSA provenance, tag = npm `gitHead`) |
| `c2f83b1` | arm B-patched (`4a37eaf`) | `.echelon/foundations.json` pins Forma 0.9.0 | version-only arm B fails the Echelon foundations gate; the one-line pin fixes it with identical runtime results |
| `d922572` | C1 (`47b0789`) | report fault codes and snapshot digest use `ef-fault-reference` | removed the report overflow at 320 px |
| `bc85f31` | C3 (`9101c51`) | workspace panels use Forma `ef-surface` instead of app-drawn surfaces | less custom CSS, same rendering contract |
| `65100d9` | C5 (`50c78fb`) | rely on Forma for focus ring, reduced motion and control target size | removed re-implemented contracts |

**Not adopted:** C4 (`ef-form-row`). The experiment found it fixed no measured deficit, since peer alignment was
already 0. It also added 7 wrapper elements and moved submit buttons below their fields (RESULT.md answer 5). The
experiment's failed or deviating parts (DV-03, DV-04) are not reproduced.

## Measured effect (experiment, Chromium / WebKit)

These are the experiment's measurements for 20 pages at 200% text, taken from RESULT.md:

| | A (0.5.0) | C (0.9.0 + C1/C3/C4/C5) |
|---|---|---|
| Pages overflowing | 6 / 6 | 1 / 1 (+1 px) |
| Overflow px | 1,172 / 1,180 | 1 / 1 |
| Elements beyond the viewport | 153 / 112 | 1 / 1 |
| axe violations | 0 | 0 |

This build is C without C4, so the experiment's C numbers are an approximation, not a measurement of this build.
This build's own checks are below.

## This build's checks

- `npm run check`, the build and the Echelon foundations workflow pass on every push (CI).
- R001 browser suite plus the new Causa tests: all pass locally (BROWSER-TESTS.md). That includes AC-14 axe (no
  serious or critical violations) on home, workspace and report, in all Chromium projects.
- **New:** `causa.spec.js` "no horizontal scrolling…" asserts `scrollWidth ≤ clientWidth` on the home list and on a
  frozen investigation in four configurations:
  - 200% root text at 1280×800;
  - phone 390×844;
  - tablet 834×1194;
  - tablet 1194×834.

  It passes.
- `app.css`:
  - 6,465 bytes at the baseline;
  - 5,817 bytes after C1/C3/C5;
  - 6,766 bytes now. That is +949 bytes over the C5 size: the Causa list, overview and sync styles, which use only
    Forma tokens.
- One contrast regression was introduced and fixed during this mission. The first version of the Causa list used a
  muted text token (4:1); it now uses the app's established `--ef-color-text-primary`. axe caught it (AC-14).

## Limitations

- The 20-page overflow matrix of the experiment was not re-run for this build. The integration test covers the two
  new screens.
- Real iPad Safari, screen readers and true user-agent zoom were not measured. The 200% check uses the root
  font-size proxy, as the experiment did.
