# CAUSA-REHEARSAL-INTEGRATION-001 — Deployment verification (Phase 7)

**URL:** https://kevinmmiller.us/indy-init-rehearsal-001/

**Mechanism:** the existing workflow `.github/workflows/deploy-pages.yml`, unchanged. It runs on every push to
`main`:
1. **verify:** build, then engine tests on the exact commit.
2. **build:** `dist/` stamped with the commit in `build-info.json`.
3. **deploy:** `actions/deploy-pages`.
4. **accept:** the served `build-info.json` must name the commit, then the browser suite runs against the live URL.

The integration reached `main` through PR #8, merged with a merge commit after all required checks passed:
build-and-test, foundations and validate.

## Deployment 1: PR #8, application release

| item | value | how verified |
|---|---|---|
| Merged head | `8c629453aa78c1d9543a56d5a79235b23ee8275e` (PR #8) | PR merge API |
| Deployed commit | `2d1fb29078aaceb297cef35c50f15168ad0a154d` (merge commit on `main`) | — |
| Workflow | Deploy to GitHub Pages **#9**, run [39939703764](https://github.com/kemiller2002/indy-init-rehearsal-001/actions/runs/39939703764): verify-and-build ✓, deploy ✓, accept ✓ | Actions API |
| Served identity | live `build-info.json` = `{"commit": "2d1fb29078aaceb297cef35c50f15168ad0a154d", "builtAt": "2026-10-23T10:33:47.299Z"}` | fetched from the live site |
| Accept job (live URL) | **94 / 94 passed**, Chromium projects and WebKit, including all `causa.spec.js` and `sync.spec.js` tests | job 120368905388 log |

### Independent check of the live site

This check was run from the development container with real Chromium, with no mocks, on 2026-10-23 at 10:42 UTC.
The raw report is [deployed-verification.json](deployed-verification.json).

| check | desktop 1440×900 | tablet 834×1194 | tablet 1194×834 | mobile 390×844 |
|---|---|---|---|---|
| Title / brand | "Causa · Root-Cause Investigation" / "Causa Root-Cause Investigation" | same | same | same |
| Demonstration cards | 5 | 5 | 5 | 5 |
| Refused investigations | 0 | 0 | 0 | 0 |
| Horizontal overflow, list / detail (px) | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 |
| Console errors, page errors, failed requests, HTTP ≥ 400 | **none** | none | none | none |

The baseline's one console error (favicon 404) is gone.

Each investigation opens with its real data. The expected values come from the frozen golden states and the
release lock:

| scenario | heading | lifecycle shown | hypotheses (= golden) | evidence (= golden) | pinned state digest shown | read-only (no form) |
|---|---|---|---|---|---|---|
| payment-failure | Card payments failing at checkout | In progress | 4 ✓ | 3 ✓ | `9ce1ec0eb49c…` ✓ | ✓ |
| authentication-outage | Users unable to sign in via single sign-on | Resolved | 2 ✓ | 7 ✓ | `cd3ea94c8be6…` ✓ | ✓ |
| conflicting-metrics | Error-rate dashboard contradicts application logs | In progress | 3 ✓ | 5 ✓ | `f189ac3bf47f…` ✓ | ✓ |
| intermittent-outage | Intermittent 502 errors on the order-status API | Waiting for evidence | 2 ✓ | 4 ✓ | `289c26e5525c…` ✓ | ✓ |
| api-latency | p95 latency regression on the search API after release 4.2 | Waiting for verification | 3 ✓ | 4 ✓ | `2ba34140daab…` ✓ | ✓ |

**Screenshots (after):** `screenshots/after/after-{desktop,tablet,tablet-landscape,mobile}.png` and the matching
`-detail.png` files. The `before` screenshots of the baseline are unchanged in `screenshots/before/`.

**Found during this check and fixed afterwards.** The frozen view showed the save-status block ("Not saved yet",
"Local only — not connected to GitHub"). A frozen original is never saved, so the block is misleading there. It is
now hidden for frozen originals; a browser test asserts it is absent. This fix ships in the follow-up deployment
below.

## R001 evidence and release identity

- No file under `evidence/` outside `evidence/causa-integration/` differs between `55f49f6` (the baseline) and
  `2d1fb29`. The files checked: `MISSION-RESULT.md`, `bootstrap/`, `slices/`, `requirements-inventory.json` and
  `traceability-local.json` (`git diff 55f49f6 2d1fb29 -- evidence` lists only `causa-integration/`).
- The R001 accepted release commit `dffaf082ca6b475231d7b21198876bf0c43fc3df` and its close-out `55f49f6` are
  unchanged ancestors of `main`.

## Deployment 2: follow-up (frozen-view save status)

Not deployed yet when this revision was written. It is recorded here once its own deploy run has finished and has been checked on the live site.
