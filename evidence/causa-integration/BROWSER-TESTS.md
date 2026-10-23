# CAUSA-REHEARSAL-INTEGRATION-001 — Tests (Phase 6)

Every count below comes from a run on a recorded commit. No test was skipped, disabled or quarantined, and a project
that executes no test now fails CI (`scripts/browser-project-counts.py`, review finding T7).

## Release candidate: PR #8 head `8c62945` (merged to `main` as `2d1fb29`)

CI run "Build and test" [39937438604](https://github.com/kemiller2002/indy-init-rehearsal-001/actions/runs/39937438604),
job 120360549912: **success**.

| suite | result |
|---|---|
| Engine tests (`tests/App.Engine.Tests`, .NET 10) | **188 / 188 passed**, 0 skipped. This is 154 at the baseline, − 14 legacy Contents-API adapter tests (removed with the adapter; replaced by the AC-PROV rows), + 48 new |
| Browser suite (Playwright 1.63, 133 tests, 0 flaky, 0 skipped) | **133 / 133 passed**: |
| · chromium-desktop | 39 / 39 |
| · chromium-ipad-portrait (Chromium emulation, not Safari) | 8 / 8 (`@ipad` subset) |
| · chromium-ipad-landscape (Chromium emulation, not Safari) | 8 / 8 (`@ipad` subset) |
| · **webkit-ipad** (WebKit, the closest engine to iPad Safari; not Safari) | **39 / 39** |
| · **firefox-desktop** (new) | **39 / 39** |
| Requirement traceability (CI) | 321 test results, 321 passed |
| Echelon foundations, Praxis validation | success |
| Live GitHub persistence ("Causa live GitHub persistence") | **1 / 1 passed** on `fe5d347` (the code at `8c62945`; later commits changed only evidence), run [39936822179](https://github.com/kemiller2002/indy-init-rehearsal-001/actions/runs/39936822179); also on `a0ce5ab`. See PERSISTENCE-VALIDATION.md |

Local runs, Chromium only (WebKit and Firefox are not installed in the development container):
- engine 188/188;
- browser 55/55 across the three Chromium projects.

## New integration tests

### Browser (`tests/browser/causa.spec.js`, real WebAssembly engine, real vendored bytes)

| requirement | test |
|---|---|
| five investigations load | all five frozen investigations load, verify and are listed. Counts are checked against the frozen **golden states** (not against the app's own output); no rejection; no console error |
| schema identity, replay, golden compatibility, detail rendering | each frozen investigation opens read-only with its title and version; its hypothesis, evidence and overview counts equal the golden state; the contract line shows the lock's state digest |
| invalid fixture / corrupted stream | a corrupted `events.ndjson`, rewritten in the served bundle by Playwright, gives `CAUSA.EVENTS_HASH_MISMATCH`; that investigation is refused and absent, the other four load. A non-pinned dataset is refused as a whole (`CAUSA.FIXTURE_DRIFT`, the incompatible state) |
| read-only protection | no form and no state-changing button on any frozen view |
| working copy, persistence and reload | create a working copy, add a hypothesis, reload. The change persists; the stored file set's events start with the frozen bytes; the frozen original is unchanged |
| conflict | two windows on one device: the stale write is refused, its changes are kept in the window and as a conflict copy, and the other window's save is intact |
| keyboard | the list and a frozen investigation are reached, opened and copied with the keyboard alone |
| 200% reflow, mobile, tablet | no horizontal scrolling on the list and a detail page at 200% text (1280×800), phone 390×844, tablet 834×1194 and 1194×834 |

### Browser (`tests/browser/sync.spec.js`)

A Node-side fake Git Data API with a real object graph is used. Covered:
- confirmed only after the SHA re-read;
- offline changes stay pending and publish when back online;
- the second commit's events extend the first's byte for byte;
- the Contents API is never used;
- a foreign change in the directory is a conflict that is never overwritten;
- the token appears only in `Authorization` and is never stored.

### Engine

| file | what |
|---|---|
| `CausaDataTests.fs` | schemas; five replays equal to the lock; frozen titles; corrupted history; dataset drift; all five × three files round-trip byte for byte; append keeps a byte prefix; stale append refused |
| `GitHubProviderTests.fs` | 31 provider-gate cases against the fake GitHub of PROVIDER-GITHUB §10.1 (AC-PROV-01, 02, 03-partial, 06–14, 15-partial, 17 ×6, 18–22, 24, own-late-write, rewritten history) |
| `ShellTests.fs` | the real shell, driven through Limen effect JSON, against the fake GitHub: publish and confirm, lost PATCH, offline, concurrent append, adopt (also with a full device), token redaction (mutation-checked), workspace-bound records, one program in flight, restart recovery, working-copy publish, recovery on a fresh device, never overwriting unreadable copies, the 401 halt and requeue, offline changes across a reload |

## Not run, stated plainly

- **Real devices.** Real iPad Safari, real iPhone and screen readers were not tested. The iPad projects are Chromium
  emulation, and WebKit is not Safari.
- **True zoom.** Browser zoom was not tested; the 200% check uses the root font-size proxy.
- **CI retries.** The suite retries once in CI (R001 configuration). This run had **0 flaky** tests, so no test
  passed only on a retry.
