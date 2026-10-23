# CAUSA-REHEARSAL-INTEGRATION-001 — Contract integration (Phase 2)

## Contract identity

| item | value |
|---|---|
| Release | `kemiller2002/causa-data` tag `causa-storage-v1.0.0` (tag object `fd2991a392e913b741f0ed2bc4daa4bc4be57f6c`, commit `c947c7e354be80766573a782546c8186336774cc`) |
| Schema bundle identity | `0a68af8c93190d2e4d6c27beb2f8cb67c3aebdda586d01beb7122b71af179152` |
| Dataset identity (`fixtures/v1.0.0/manifest.json`) | `f650a2e509a420872b46177b21f57ef6ebd7625ae4c69dd92198d3ca472c2b86` |
| Release lock | `6a7e7802ff2a3ff500ab6065662514a96e09e6c15eb30212ba1377fcc5bf8fc5` |
| Requirements | Indy-init `requirements/causa/data/v1.0.0` (CAUSA-DATA-001; consolidated by CAUSA-REQUIREMENTS-CONSOLIDATION-001) |
| Semantic oracle | this repository's engine at `55f49f6`, the commit named by `causa-data/engine.lock.json`; the integration does not change `Decide`, `Model` or `History.validate` |

The frozen tag and its fixtures were never modified. Every vendored byte is checked against the release's own lock.

## How the data gets in

1. **Vendored bytes.** `data/causa-storage-v1.0.0/` holds 175 files copied byte for byte from the tag: fixtures,
   schemas, golden states, compatibility RELEASE and lock. `src/contract/*.fs` are the seven `Causa.Contract` library
   files, also byte-identical. `.gitattributes` marks both `-text`, so Git never converts line endings.
2. **Build gate.** `scripts/verify-causa-vendor.mjs` is step 0 of `npm run build`. It checks each vendored file
   against `lock.frozenFiles` (SHA-256) and each library file against `VENDOR.json`, and fails the build on any
   difference. Output: `PASS causa-vendor causa-storage-v1.0.0 (176 locked files, 7 library files) release
   6a7e7802ff2a`.
3. **Shipped as bytes.** The build writes `dist/data/causa-storage-v1.0.0.bundle.json` = `{tag, files: {path: base64}}`
   covering schemas, golden states, investigations and the root manifest. Base64 keeps exact bytes: the encoding and
   canonical-form stages need them.
4. **Verified again in the browser.** The F# engine, compiled to WebAssembly, re-verifies everything on every load
   (`src/engine/CausaData.fs`). Nothing is trusted because the build checked it.
   - `loadSchemas`: the bundle manifest's SHA-256 must equal the pinned schema identity, and every schema must match
     its digest.
   - `loadDataset`: the root manifest's SHA-256 must equal the pinned dataset identity. Each listed investigation then
     goes through `Store.ofFileMap` → `Store.validate`, which is all 17 reader stages of CONTRACT §11.1, with stage 15
     admissibility re-decided by this engine's `History.validate`.
   - CAUSA-CLAR-10 dataset checks: every root-manifest field (`eventsSha256`, `manifestSha256`, `snapshotSha256`,
     `expectedStateSha256`, `expectedVersion`, `expectedLifecycle`, `title`) must equal the verified investigation,
     otherwise `CAUSA.FIXTURE_DRIFT`. The replayed state must equal `golden/<key>.state.json`, otherwise
     `CAUSA.GOLDEN_MISMATCH`.
5. **Fail closed per item.** A failing investigation is listed with its contract code and is never shown, not even
   partially. The others still load. A wrong dataset or schema identity refuses the whole dataset.

## Replay results

Each frozen investigation replays to the state digest the release lock pins:

| scenario | title | version | lifecycle | state SHA-256 (= lock) |
|---|---|---|---|---|
| payment-failure | Card payments failing at checkout | 26 | active | `9ce1ec0eb49c83395f3a4f1d07e93075df7ed0814fd902b94dd3054105946f3b` |
| authentication-outage | Users unable to sign in via single sign-on | 59 | resolved | `cd3ea94c8be64f38a8f60d99c6df7b9092488c605401701d22988147f1360c95` |
| conflicting-metrics | Error-rate dashboard contradicts application logs | 28 | active | `f189ac3bf47f37894e0a1bcfe3e17756f2f5badbed7b6a38d68f25029bd481c5` |
| intermittent-outage | Intermittent 502 errors on the order-status API | 37 | awaitingEvidence | `289c26e5525c1b3d48b22b64cc4cf46198ed46130eb29d7fd6d2c3d03e3d7e10` |
| api-latency | p95 latency regression on the search API after release 4.2 | 34 | awaitingVerification | `2ba34140daabd6286229af8b55bb09a72de7f321c2a978926f433871504273e1` |

Evidence:
- `CausaDataTests`, all five equal to `lock.expectedStates` (.NET);
- `causa.spec.js`, where each detail page shows the first 12 hex digits of the pinned digest (real page, WebAssembly);
- live deployment (DEPLOYMENT-VERIFICATION.md).

## Version awareness, refusal and immutability

- **Corrupted history.** `aggregateVersion` 3 → 4 in one line of `api-latency/events.ndjson` gives
  `CAUSA.EVENTS_HASH_MISMATCH`. That investigation is refused and the other four load. This is tested in .NET
  (`CausaDataTests`) and in the browser, where Playwright rewrites the served bundle, so the WebAssembly engine
  refuses it.
- **Wrong dataset.** `causa-demo-v1` → `causa-demo-v2` in the root manifest gives `CAUSA.FIXTURE_DRIFT`. The whole
  dataset is refused and the UI shows the incompatible state.
- **Frozen is read-only.** `#/demo/{id}` always shows the frozen original.
  - The engine offers no action on it: no form, and no transition button. `workspaceKeys` gates every action on
    `editable`.
  - Any command against it is refused with `DEMO.READ_ONLY` before `Decide` runs.
- **Working copy.** "Create working copy" copies the frozen event list into a device investigation with the **same
  investigation id**, as CONTRACT §3.2 and CAUSA-PROV-11 require. It is stored as a Causa v1 file set
  (`causa.fileset/1.0.0`: base64 `manifest.json`, `events.ndjson`, `snapshot.json` in one storage value, so a save is
  atomic). The file set is built and re-verified with `Store.build`, which runs all 17 stages before anything is
  written (CONTRACT §9.2). Its `events.ndjson` starts with the frozen bytes, byte for byte, as the tests show.
- **Append-only.** Every later file set's events are a byte prefix extension of the previous one (R15.4 test). A
  local save is a fast-forward only: a device copy that is not a byte prefix of this window's history is never
  overwritten (see PERSISTENCE-VALIDATION.md).
- **No silent transformation.** Investigations stored by R001 in the rehearsal's own codec (`rci.events/v1`) are
  read and shown **read-only**. They are never migrated or rewritten into the old format or the new one
  (`STORAGE.LEGACY_READ_ONLY`). Commands are timestamped at the boundary with millisecond-truncated UTC
  (CONTRACT §2.9).
- **Provider metadata stays outside domain data.** GitHub commit, tree and blob SHAs, branch, run id and sync
  states live only in the adapter-private record `causa:github:<id>` (CAUSA-PROV-25/-34). No contract file contains
  them, and commit authorship never becomes `actorId` (AC-PROV-24 test).
- **No hand-written summaries.** List cards and the overview (lifecycle, counts, cause, verification, last
  activity) are computed from the replayed state. The next permitted actions come from the engine's policy
  (`Outstanding work`) and from `Decide.probe`, which shows a button only when the kernel would accept that exact
  command.

## Engine changes (application, not the semantic oracle)

- `CaseNames.fs` was split out of `Codec.fs`, unchanged, because the contract library needs it and must compile
  before `Codec.fs`, whose internal `Json` module would otherwise shadow the contract's.
- `CausaData.fs` is the new dataset and file-set adapter.
- `CausaGitHub.fs` is the new provider (PERSISTENCE-VALIDATION.md).
- `Shell.fs` adds the demo route, file-set persistence and the provider plumbing.
- `Sync.fs` is reduced to configuration types: the non-conforming Contents-API adapter
  (PROVIDER-GITHUB.md §11.1) was removed together with its unit tests (`SyncTests.fs`, 14 test functions). The AC-PROV rows
  replace them.
- `Decide.fs`, `Model.fs`, `Commands.fs` and `Primitives.fs` are unchanged since `55f49f6`. The semantic oracle is
  untouched.
