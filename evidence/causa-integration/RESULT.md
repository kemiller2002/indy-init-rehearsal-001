# RESULT — CAUSA-REHEARSAL-INTEGRATION-001

## Classification: **PARTIAL**

**What works, and how it was shown.**
- **Frozen data.** The rehearsal app loads the frozen Causa storage release v1.0.0 and verifies every byte in the
  browser. It replays all five investigations to the digests the release lock pins, refuses corrupted or non-pinned
  data with the contract's codes, and shows the investigations under the Causa brand with an overview and the
  engine's next permitted actions.
- **Working copies and GitHub.** Working copies are saved as Causa v1 file sets. They are published to GitHub through
  a new adapter written to the Git Data API requirements, one atomic commit per change.
- **Tests.** Everything passed in every configured browser:
  - engine tests;
  - the full Playwright suite;
  - the new integration tests, including Firefox and WebKit;
  - a live run against the real `api.github.com` (twice).
- **Deployment.** The existing Pages workflow deployed it, and the live site was checked independently.
- **Review.** An independent adversarial review found no critical defect. All 5 major findings were fixed and
  regression-tested.

**Why not PASS.**
- **Provider gate incomplete.** PROVIDER-GITHUB.md §0.3 makes a build with a GitHub adapter a provider-gate PASS
  only with evidence for every `AC-PROV-*` row. This build's provider has **no automatic re-decision**
  (CAUSA-PROV-28):
  - AC-PROV-03 is partial; AC-PROV-04 and -05 are not met.
  - Instead, a conflict is never overwritten, and the person chooses between GitHub's verified copy and a new run.
- **Other partial or inapplicable rows:**
  - AC-PROV-15: one commit per working copy instead of one for all five.
  - AC-PROV-16: a per-investigation queue without command inputs.
  - AC-PROV-23: not applicable, because the dataset is vendored rather than read from GitHub.
- **Unmeasured:** real iPad Safari and screen readers.

**Why not FAIL or BLOCKED.**
- Every functional acceptance item of the mission was met and verified.
- Live GitHub persistence was **actually verified**, not assumed. It ran on the real API, with each claim checked
  by an independent read by SHA.

## Identities

| item | value |
|---|---|
| Repository | `kemiller2002/indy-init-rehearsal-001` (updated in place; no new experiment or repository; R002 not started) |
| Baseline (= R001 close-out, deployed before) | `55f49f61c939a3a780cee592f36f732a1328f981`; R001 accepted release `dffaf082ca6b475231d7b21198876bf0c43fc3df` preserved |
| Integration release | PR #8, head `8c629453aa78c1d9543a56d5a79235b23ee8275e`, merged as **`2d1fb29078aaceb297cef35c50f15168ad0a154d`** |
| Follow-up | PR #9 (frozen-view save status hidden; evidence), merged as **`5d5f123a5d82572fd8f9faa9163e837f51e25afa`** |
| Deployments | Pages run #9 [39939703764](https://github.com/kemiller2002/indy-init-rehearsal-001/actions/runs/39939703764) (`2d1fb29`); run #10 [39944028563](https://github.com/kemiller2002/indy-init-rehearsal-001/actions/runs/39944028563) (`5d5f123`) |
| Praxis | work item `CAUSA-REHEARSAL-INTEGRATION-001`, execution `EXE-20261023T081832699Z-2539c4b3` |

### Versions

| component | baseline | now |
|---|---|---|
| Forma (`@echelon-foundry/design-system`) | 0.5.0 | **0.9.0** (pinned in `.echelon/foundations.json`) |
| Limen | 0.9.0 | 0.9.0 |
| Folio | 0.3.0 | 0.3.0 |
| Ordo / Praxis / Aegis | 1.5.0 / 3.16.0 / 1.0.0 | unchanged |
| F# engine semantics (`Decide`, `Model`, `Commands`, `Primitives`) | `55f49f6` | **unchanged** (the contract's semantic oracle) |
| .NET | 10 | 10 |

### Contract identity

| item | value |
|---|---|
| Release | `kemiller2002/causa-data` tag `causa-storage-v1.0.0` (tag object `fd2991a3…`, commit `c947c7e354be80766573a782546c8186336774cc`) |
| Schema bundle | `0a68af8c93190d2e4d6c27beb2f8cb67c3aebdda586d01beb7122b71af179152` |
| Dataset identity | `f650a2e509a420872b46177b21f57ef6ebd7625ae4c69dd92198d3ca472c2b86` |
| Release lock | `6a7e7802ff2a3ff500ab6065662514a96e09e6c15eb30212ba1377fcc5bf8fc5` |
| Requirements | Indy-init `requirements/causa/data/v1.0.0` (CAUSA-DATA-001, PROVIDER-GITHUB.md) |

The frozen tag and its fixtures were never modified. The vendored copy is checked against the lock on every build
(176 files) and again in the browser.

## Replay results

All five replay to the lock's state digests in .NET, in the browser (WebAssembly) and on the live site:

| scenario | version | lifecycle | state SHA-256 |
|---|---|---|---|
| payment-failure | 26 | active | `9ce1ec0e…` |
| authentication-outage | 59 | resolved | `cd3ea94c…` |
| conflicting-metrics | 28 | active | `f189ac3b…` |
| intermittent-outage | 37 | awaitingEvidence | `289c26e5…` |
| api-latency | 34 | awaitingVerification | `2ba34140…` |

- **Corrupted stream** (`aggregateVersion` 3→4 in one line): `CAUSA.EVENTS_HASH_MISMATCH`. That investigation is
  refused, and the other four load (.NET and the real page).
- **Non-pinned dataset:** `CAUSA.FIXTURE_DRIFT`; the whole dataset is refused.

## Test counts

| suite | result | where |
|---|---|---|
| Engine | **188 / 188** (154 baseline − 14 legacy adapter tests removed with the adapter + 48 new) | CI and local |
| Browser, release candidate `8c62945` | **133 / 133**, 0 flaky, 0 skipped. Chromium desktop 39, Chromium iPad-emulation portrait 8, landscape 8, **WebKit 39**, **Firefox 39** | CI run 39937438604 |
| Browser against the live URL (accept, `2d1fb29`) | **94 / 94** | Pages run #9 |
| Browser against the live URL (accept, `5d5f123`) | **93 passed, 1 flaky** (an R001 journey test timed out while the page booted on its first attempt and passed on retry; recorded as flaky, cause not established), 0 failed | Pages run #10 |
| Live GitHub persistence | **pass** on `a0ce5ab`, `fe5d347` and `f5b9f12` (PR #9 head) | runs 39933658628, 39936822179, 39941861418 |
| Provider gate (fake GitHub) | 31 provider cases + 15 shell GitHub cases; AC-PROV statuses in PERSISTENCE-VALIDATION.md | engine suite |

No test was skipped or disabled. A browser project that executes no test now fails CI.

## Live persistence results

Run 39936822179 (`fe5d347`) published to the real `api.github.com`, branch `causa-workspace`, run
`demo-20261023-100923-f80d80`. Everything was verified by independent SHA reads:

| step | result |
|---|---|
| working copy | commit `87c113de`; frozen bytes exactly (events SHA-256 `a85cec5e…`) |
| append | commit `fa6d1fe0`, one parent; GitHub equals the device byte for byte; author `causa-adapter` |
| reload | still confirmed; the token was not stored and was asked for again |
| second device | recovered the run; bytes identical |
| concurrent write | stale write refused (`CAUSA.VERSION_STALE`); GitHub head unchanged at the other device's commit `ee86a895`; nothing overwritten |
| adopt on request | GitHub's version adopted; the replaced version kept as a conflict copy |

## Accessibility

- **axe:** no serious or critical violations (AC-14) on home, workspace, report and factory, in Chromium, WebKit and
  Firefox. One contrast regression from this mission was caught by axe and fixed.
- **Keyboard:** an investigation is created, and the demonstration list and a frozen investigation are used, by
  keyboard alone.
- **Reflow:** no horizontal scrolling on the list and a detail page at 200% text, phone 390×844, tablet 834×1194
  and 1194×834 (test). On the live site: 0 px overflow in four viewports.
- **Forma 0.9.0 with C1/C3/C5:** experiment arm C, without C4, measured 1/20 pages overflowing at 200%, against
  6/20 at the baseline.

## Deployment evidence

- Pages run #9 deployed `2d1fb29`; the live `build-info.json` names it; accept passed 94/94.
- Independent check of the live site:
  - title and brand "Causa · Root-Cause Investigation";
  - 5 cards and 0 refusals;
  - each investigation opens with its golden-matching data and pinned digest, read-only;
  - **no** console errors, failed requests or HTTP ≥ 400 (the baseline favicon 404 is gone).
- Follow-up run #10: Pages run #10 deployed `5d5f123`; `build-info.json` names it; accept 93 passed + 1 flaky. The independent check was repeated: all clean, and the frozen view no longer shows a save status.
- The R001 evidence and release identity are unchanged.

## Screenshots

- `screenshots/before/`: the baseline, live `55f49f6`.
- `screenshots/after/`: the live release, desktop, tablet (portrait and landscape) and mobile, list and detail.

## Limitations

1. **Provider gate incomplete.**
   - There is no automatic re-decision (AC-PROV-03 partial, -04 and -05 not met).
   - Working copies are one commit per investigation (AC-PROV-15 partial).
   - The queue is per investigation and has no command inputs (AC-PROV-16 partial).
   - AC-PROV-23 is not applicable.
2. **Browser platform constraints.**
   - Storage is `localStorage`, not IndexedDB, and there is no `persist()`.
   - There are no timer-driven retries.
   - The fetch `cache`, `redirect` and `referrerPolicy` options cannot be set from Limen. Ref reads use nonce URLs,
     and same-origin redirects are followed.
3. **Local concurrency.** The fast-forward check is a read followed by a write, and two windows can still race in
   between: `localStorage` has no compare-and-set.
4. **Unprotected data branch.** `causa-workspace` has no force-push protection, which cannot be configured from this
   session. The rewind window of CAUSA-PROV-16 §4 is open.
5. **Run id.** A new run id is not checked for absence at the head. A collision would be `refused`, never an
   overwrite.
6. **Not measured.**
   - Real iPad Safari: the iPad projects are Chromium emulation, and WebKit is not Safari.
   - Screen readers.
   - True browser zoom: the 200% check is a root-font proxy.
7. **Conflict copies cannot be listed.** They are stored on the device but no UI lists, exports or restores them.
8. **One flaky test in the final accept run.** R001's `[R15.2][R15.5]` journey test did not reach
   `data-kernel = running` within 15 s on its first attempt against the live site (Pages run #10). It passed on
   retry, and it passed first time everywhere else. The cause is not established.
9. **Live persistence was proven with the job's `GITHUB_TOKEN`.** A person using the deployed site supplies their own
   fine-grained token at run time, held in memory only.

## Evidence index

- `BASELINE.md`
- `CONTRACT-INTEGRATION.md`
- `FORMA-UPGRADE.md`
- `PERSISTENCE-VALIDATION.md`
- `BROWSER-TESTS.md`
- `DEPLOYMENT-VERIFICATION.md`, with `deployed-verification.json`
- `ADVERSARIAL-REVIEW.md`, with `ADVERSARIAL-REVIEW-REPORT.md`
- `screenshots/`

## Link

https://kevinmmiller.us/indy-init-rehearsal-001/
