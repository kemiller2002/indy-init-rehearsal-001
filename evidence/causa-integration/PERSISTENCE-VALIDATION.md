# CAUSA-REHEARSAL-INTEGRATION-001 — Persistence validation (Phase 5)

## Design

| layer | what | where |
|---|---|---|
| Frozen data | the five frozen investigations, verified in the browser on every load; **read-only** (no action offered, every command refused with `DEMO.READ_ONLY`) | `data/causa-storage-v1.0.0` → `dist/data/…bundle.json` → `CausaData.loadDataset` |
| Working copy (device) | one Causa v1 file set per investigation, in one `localStorage` value (`rci:inv:<id>`, format `causa.fileset/1.0.0`), so a save is atomic; built and re-verified by `Store.build` before every write | `Shell.persistAfterChange` → `onCheckBeforeSave` |
| Concurrent windows | before every write the device copy is read again. It is replaced only as a **fast-forward**, when its `events.ndjson` is a byte prefix of this window's. Otherwise the write is refused: this window keeps its changes in memory, writes them to a separate `causa:conflict:<id>:<version>` copy, and says so | `onCheckBeforeSave` |
| GitHub (remote) | new provider `CausaGitHub.fs`, Git Data API only: one atomic commit per publish (3 blobs → tree on `base_tree` → path-prefix guard → commit with one parent → durable step-7 marker → uncached head re-read → `PATCH force:false` → confirmation re-read **by commit SHA**) | PROVIDER-GITHUB.md CAUSA-PROV-05…24 |
| Provider record | adapter-private, never contains the token: confirmed copy (3 files + commit/tree), status (the closed set pending/sending/unknown/confirmed/conflicted/refused/failed), in-flight files, publish marker, status log | `causa:github:<id>` (CAUSA-PROV-25 shape, batched) |

The previous adapter, a Contents-API `PUT` of `investigations/<id>/events.json`, was non-conforming
(PROVIDER-GITHUB.md §11.1). It was removed. "Use the existing GitHub adapter where possible" was followed for
everything that still applies:
- settings and token handling: the token is held in memory only and never stored, and a legacy stored token is
  erased;
- the workspace binding of statuses;
- the offline and online triggers;
- the status UI.

## Provider gate (PROVIDER-GITHUB.md §10.2)

Evidence comes from three places:
- `GitHubProviderTests` (29 cases): provider programs against a fake GitHub with a real content-addressed object
  graph (§10.1);
- `ShellTests` (9 GitHub cases): the real shell driven through Limen effect JSON against the same fake;
- the browser suite `sync.spec.js`: the real page against a Node fake Git Data API.

| row | status | evidence / reason |
|---|---|---|
| AC-PROV-01 create | **met** | one commit on the old head adding exactly P; request order per CAUSA-PROV-13; confirmed only after the SHA re-read (provider, shell, browser, live) |
| AC-PROV-02 append | **met** | the old events are a byte prefix; the result passes 17-stage verification (provider, shell, live) |
| AC-PROV-03 concurrent append, equal re-decision | **partial** | detected: no PATCH is sent from the stale base, the record is `conflicted` (`CAUSA.VERSION_STALE`), nothing is overwritten. **Automatic re-decision (CAUSA-PROV-28) is not implemented.** The person chooses: adopt GitHub's verified copy (theirs is kept as a conflict copy) or continue in a new run |
| AC-PROV-04 changed re-decision | **not met** | no re-decision, so no `redecided` lines; nothing is written without the person's choice |
| AC-PROV-05 refused re-decision | **not met** | no re-decision |
| AC-PROV-06 other path moved | **met** | rebase with the same blobs, 2 commit attempts, 1 PATCH, the other change preserved |
| AC-PROV-07 PATCH applied, answer lost | **met** | `unknown` → resolution by SHA → `confirmed`, no second commit (provider, shell) |
| AC-PROV-08 PATCH lost, not applied | **met** | `unknown` → resolution finds nothing landed → `pending` → retry lands one commit |
| AC-PROV-09 restart after the marker | **met** | the record survives the reload as `unknown` (T20); without a token nothing is claimed; with it, resolution confirms with no new commit (shell test) |
| AC-PROV-10 failure before the PATCH | **met** | head unchanged, `pending`, retry confirms |
| AC-PROV-11 guard | **met** | a tree touching `fixtures/v1.0.0/` → `CAUSA.PATH_FROZEN`, no PATCH, head unchanged |
| AC-PROV-12 invalid runId or id | **met** | `CAUSA.PATH_INVALID`, zero requests (4 cases) |
| AC-PROV-13 token scan | **met** | fake, shell, browser and live: the token appears only in `Authorization` to `https://api.github.com` |
| AC-PROV-14 create over an existing directory | **met** | `refused`, `CAUSA.VERSION_STALE`, existing bytes unchanged, no PATCH |
| AC-PROV-15 working copy of all 5 | **partial** | every copied file equals the frozen bytes and `fixtures/` is unchanged, but it is **one commit per investigation**, not one commit for all five |
| AC-PROV-16 two queued commands across a restart | **partial** | two offline changes survive a reload and are published in order, byte for byte (shell test `[AC-PROV-16 partial]`). The queue is per investigation, not per command, and stores no command inputs |
| AC-PROV-17 PATCH status mapping | **met** | 401→failed, 403 rate-limit→pending, 429→pending, 422 "Reference does not exist"→failed, 409 "empty"→failed, 502→unknown. A 401 halts publishing until a new token is entered, which returns authentication failures to `pending` (shell test `[CAUSA-PROV-19] [T18]`) |
| AC-PROV-18 rewind before the PATCH | **met** | the step-8 re-read sees the move; lost confirmed events → `CAUSA.HISTORY_REWRITTEN`, no PATCH |
| AC-PROV-19 truncated listings | **met** | only non-recursive walks; `truncated:true` is never taken as complete (→ pending) |
| AC-PROV-20 byte exactness | **met (base64)** | non-ASCII, U+2028, a control escape and a 4-byte character round-trip; a server that stores different bytes → `failed` (byte mismatch) before the PATCH. The `utf-8` blob encoding (a MAY) is not used |
| AC-PROV-21 no Contents API or raw reads | **met** | none in any log; every ref read is a distinct uncached URL |
| AC-PROV-22 listing with invalid entries | **met** | `Not-A-Uuid` → PATH_INVALID; a 4th file → PATH_INVALID; a bad digest → EVENTS_HASH_MISMATCH; zero writes |
| AC-PROV-23 dataset read anonymously from GitHub | **not applicable / not met** | the app does not read the dataset from GitHub. It ships the vendored, lock-verified bytes and verifies them in the browser against the same pins (CONTRACT-INTEGRATION.md) |
| AC-PROV-24 actorId | **met** | persisted `actorId` is `local-user`, never the GitHub login |

Under PROVIDER-GITHUB.md §0.3, a build with a GitHub adapter that lacks evidence for every AC-PROV row is not a
provider-gate PASS. **This build's provider gate is therefore incomplete.**

## Other deviations (stated, not hidden)

- **Storage.** `localStorage`, not IndexedDB (CAUSA-PROV-25 §2). `navigator.storage.persist()` is not requested
  (CAUSA-PROV-35 §4).
- **Timers.** There are no timer-driven retries (CAUSA-PROV-20 §5): Limen offers no timer effect. Sync runs on
  triggers:
  - a change;
  - coming back online;
  - saving settings;
  - "Check GitHub again";
  - load.

  An ambiguous PATCH is resolved immediately once.
- **Fetch options.** The kernel cannot set `fetch` `cache`, `redirect` or `referrerPolicy` (CAUSA-PROV-06 §2,
  -31 §3). Ref reads therefore use a per-request nonce URL so they are never served from cache, and GitHub calls use
  `credentials: "omit"`.
- **Redirects.** A redirect is followed by the browser. GitHub's own redirects (a renamed repository) stay on
  `api.github.com`, so the token is re-sent to the same origin. A moved repository is therefore followed instead of
  failing as a configuration error (review m11). Any response that is not the expected JSON still fails closed.
- **Batching.** A publish may carry several local changes of one investigation as one commit, as CAUSA-PROV-18 §1
  allows.
- **Local concurrency.** The local fast-forward check is a read followed by a write. `localStorage` has no
  compare-and-set, so two windows can still race between the read and the write (review m3). An investigation whose
  local save was refused is not published.
- **Run ids.** A new run id is not checked for absence on GitHub (CAUSA-PROV-02 §4). A collision would show as
  `refused` per investigation, never as an overwrite.
- **Branch protection.** Protection of the data branch (CAUSA-PROV-04 §1) cannot be configured from this session.
  The residual rewind window between step 8 and step 9 (CAUSA-PROV-16 §4) is therefore open on `causa-workspace`.

## Local persistence results (open, copy, change, persist, reload, recover)

| check | result | evidence |
|---|---|---|
| open a frozen investigation read-only | pass | `causa.spec.js` (5 investigations, no form or action offered) |
| copy: the working copy keeps the frozen history byte for byte | pass | `causa.spec.js`, `ShellTests` |
| change, then persist as a v1 file set that verifies | pass | `causa.spec.js`, R15.4 shell test |
| reload: the change survives; the frozen original is unchanged | pass | `causa.spec.js` |
| recover: after a reload a stale or unknown publish resolves; a fresh device restores a run from GitHub | pass | `ShellTests` (AC-PROV-09, CAUSA-PROV-08 recover), live (below) |
| concurrent window: stale write refused, nothing lost | pass | `causa.spec.js` (two pages, same storage) |

## Live GitHub persistence

**Result: verified live**, twice, against the real `api.github.com`.

The workflow `.github/workflows/causa-live-persistence.yml` runs on GitHub Actions:
- The real app is built from the commit and served locally, then driven in Chromium.
- It publishes with the job's short-lived `GITHUB_TOKEN` (contents: write on this repository only), to the dedicated
  data branch `causa-workspace`. That branch is an orphan holding only `.gitattributes` `* -text`
  (CAUSA-PROV-04 §3).
- Traces, screenshots and videos are off, so the typed token is never recorded.
- Every claim is checked **independently** by the test process, which reads GitHub's Git Data API by SHA. Each blob's
  git id is recomputed from its bytes.

| run | commit | result | artifact |
|---|---|---|---|
| [39933658628](https://github.com/kemiller2002/indy-init-rehearsal-001/actions/runs/39933658628) | `a0ce5ab` (before the review fixes) | **pass**, 1/1 | `causa-live-persistence` 12493445406 |
| [39936822179](https://github.com/kemiller2002/indy-init-rehearsal-001/actions/runs/39936822179) | `fe5d347` (review fixes; the code that is deployed) | **pass**, 1/1 | `causa-live-persistence` 12494656190 |

An earlier run, [39932899507](https://github.com/kemiller2002/indy-init-rehearsal-001/actions/runs/39932899507) on
`4e2251a`, **failed**. Its first five checks passed live. It then failed because the test did not re-enter the
memory-only token after the reload: the app correctly showed "enter your access token". The test was fixed
(`a0ce5ab`); the app was not changed.

Steps of run 39936822179 (`fe5d347`), from the run's own evidence file (it contains no token):

| step | evidence |
|---|---|
| configure | run `demo-20261023-100923-f80d80`, P = `workspaces/demo/demo-20261023-100923-f80d80/investigations/d588748e-8df9-5f27-859e-5d36d9a2b53d/`, head before `32305dd5` |
| working copy (CAUSA-PROV-11) | commit `87c113de`, one parent `32305dd5`; exactly 3 blobs `100644`; `events.ndjson` SHA-256 `a85cec5e…` = the frozen `eventsSha256`; message `causa: … v0 -> v34 (34 events), working copy of causa-demo-v1` |
| append (AC-PROV-02) | commit `fa6d1fe0`, one parent `87c113de`; GitHub's three files equal the device file set byte for byte; the events start with the frozen bytes; `v34 -> v35 (1 events)` |
| reload | confirmed state survives; the token is not stored on the device and is asked for again |
| recovery on a second device (fresh storage) | "Recover this run" restores the investigation, verified; its events are byte-identical (19,415 bytes) |
| concurrent write (CAUSA-PROV-27) | device B appends and is confirmed. Device A, on the older version, appends: **conflict** `CAUSA.VERSION_STALE`, no PATCH from the stale base, GitHub head stays at B's commit `ee86a895`, B's bytes untouched |
| adopt on request (CAUSA-PROV-29) | A adopts GitHub's version: A's events equal B's, A's previous version is kept as 1 conflict copy, and the status is confirmed |
| token | not in either page's content and not in device storage (boolean checks only); GitHub Actions masks the secret in logs |

Each live run leaves its commits on `causa-workspace` under its own run id. They are the auditable trail: one commit
per change, the author `causa-adapter`, and no domain text in commit messages.

