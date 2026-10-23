# Adversarial review: indy-init-rehearsal-001, 55f49f6..HEAD (Causa storage v1.0.0 + GitHub provider)

Reviewed at HEAD 3a46d7d (4e2251a + an evidence-only commit). `dotnet test tests/App.Engine.Tests`: 180/180 pass.
Reproductions were written as extra tests in a scratch copy of the test project (scratchpad/rt/ShellTests.fs, tests ADV1–ADV3; they reference the built engine DLL). Nothing in the repository was modified.

I found no critical defect. The frozen release can't be written: it is shipped read-only in the bundle, pinned in the engine, and every command on a frozen id without a working copy is refused. The GitHub path guard holds. Nothing is reported as "Saved to GitHub" without a SHA-addressed byte comparison. The defects below are in conflict handling, the local record store and concurrency.

## MAJOR

**M1. A HISTORY_REWRITTEN conflict offers "Use GitHub's version". After adoption the app appends to the rewritten investigation.** CONFIRMED (ADV2)
- Code: Shell.fs:2068 (`canAdoptRemote` = any `Conflicted` with `Remote.IsSome`) and Shell.fs:1704-1711.
- The remote copy is attached for the rewrite cause at CausaGitHub.fs:603 (changedAt) and :727 (resolve).
- Scenario:
  1. Publish v1.
  2. Someone force-replaces `P` with a different valid history for the same id.
  3. A local edit gives `conflicted`, `CAUSA.HISTORY_REWRITTEN`, `canAdopt=true`.
  4. The user clicks adopt. The record becomes `confirmed` on the rewritten copy.
  5. The next edit PATCHes onto the rewritten investigation (PATCH count 1→2; the remote extends the rewritten bytes).
- Spec: this violates CAUSA-PROV-29 §5, which allows only discard, export or a new run, and says "MUST NOT write to the rewritten or invalid investigation". It also goes against CAUSA-PROV-07 §2.

**M2. Provider records are keyed by investigation id only. A run or workspace change overwrites the previous workspace's record, including `unknown` and `conflicted` ones.** CONFIRMED (ADV1)
- Code: CausaGitHub.fs:794 (`Record.key iid = "causa:github:"+iid`), Shell.fs:1071 (saveRecord), and Shell.fs:1295 (syncNext creates a fresh record when `recordOf` filters out the old workspace).
- Scenario:
  1. The PATCH lands but its answer is lost, and the resolution reads fail. The stored record is `SyncUnknown` (run A, with InFlight and the step-7 marker).
  2. The user clicks "Start a new run" (`sync.newRun`, which is the remedy the UI offers for conflicts) or changes the repository.
  3. The stored record becomes `SyncPending` for run B with `Confirmed=None`. Run A's unknown outcome and confirmed copy are gone from the device.
  4. Switching back to run A later republishes as a create, gets `refused`/`VERSION_STALE`, and has no way out.
- Spec: violates CAUSA-PROV-01 §3 ("MUST NOT move, copy or delete queued or confirmed data"), CAUSA-PROV-30 §6 and CAUSA-PROV-24.
- The shell test that claims this property is vacuous (T1 below).

**M3. "Use GitHub's version" can permanently lose this device's version. Conflict copies are write-only.** CONFIRMED by trace (quota failure not reproduced)
- adoptConfirmed (Shell.fs:1161-1189) replaces `st.Investigations` and the device key with GitHub's copy. The previous version survives only as a `causa:conflict:<id>:<n>` StorageSet.
- The result of that write is ignored (Shell.fs:1874 `SaveConflictCopy, _ -> st, []`), yet the notice always says "Your previous version was kept as a separate conflict copy" (Shell.fs:1711).
- With localStorage full (records duplicate every file set up to 3×, and conflict copies are never pruned), the copy fails silently and the user's version exists nowhere.
- The keys collide: adopt uses the line count (Shell.fs:1177) and the save conflict uses the aggregate version (Shell.fs:1865), so later copies can overwrite earlier ones.
- No UI or export lists, opens or restores conflict copies, against CAUSA-PROV-25 §5 (export before discard).

**M4. More than one provider program can be in flight at once.** CONFIRMED (ADV3) for the trigger; consequences PLAUSIBLE
- Code: Shell.fs:1246 (`abandon`), Shell.fs:1340 (saveSettings), Shell.fs:2943-2957 (identity check).
- *Trigger 1, same workspace:* re-saving settings (for example re-entering the token) while a publish is in flight. abandon sets `Busy=false` and moves Sending to Pending/Unknown in memory. The workspace identity is unchanged, so the old program's replies are still adopted, and `syncNext` starts a second program. ADV3 shows 2 outstanding RemoteSteps. The old program keeps sending the old token after it was replaced.
- *Trigger 2, workspace changed:* a late reply from the old program calls `abandon` again. That clears `Busy` and rewrites the in-memory status of the new program's record while the new program is still running, so the next trigger starts yet another program.
- *Consequence:* a resolve can race an in-flight PATCH and return the record to `pending`. A third publish built on the old base then meets the first program's own landed commit. If the user edited in between, it reports a false `VERSION_STALE` "someone else appended". The only offered remedy, adopt, moves the user's newest edits into an invisible conflict copy (M3).
- Spec: violates CAUSA-PROV-20 §7 (one attempt in flight per client).

**M5. "Recover this run from GitHub" overwrites device copies that failed verification, with no conflict copy.** CONFIRMED by trace
- recoverProgram/recovered skip only ids in `st.Investigations` (Shell.fs:1205, 1217). An investigation the app reported as "could not be verified … left untouched" (`st.Unreadable`) is not in that map.
- adoptConfirmed then writes `rci:inv:<id>` over it and removes it from `Unreadable` (Shell.fs:1170, 1189). `previous` is None (fileSetOf fails), so no conflict copy is made.
- Scenario: a stored file set that fails a reader stage on this build (for example a newer local format, or a stored doc in "another format") is silently replaced by the run's copy.

## MINOR

**m1. An undecodable provider record is overwritten even though the UI says it is "left untouched".** CONFIRMED by trace
- At Shell.fs:1898 the record is not loaded, so syncNext creates a new record (Shell.fs:1295) and begin' saves it over the same key (Shell.fs:1321).
- Example: a `recordVersion` 2 record from a newer build is destroyed.

**m2. adoptConfirmed writes the device copy without the fast-forward check of onCheckBeforeSave.** PLAUSIBLE
- If another window has extended the device copy, that window's version is overwritten. The conflict copy holds this window's version, not the overwritten one.

**m3. onCheckBeforeSave is a read-then-write across two async round trips (TOCTOU).** PLAUSIBLE
- Two windows can both read the old device copy and both write. The second write drops the first window's events from the device. The first window keeps them in memory and in a conflict copy on its next save.
- The design note's "never overwritten" is stronger than the mechanism.
- Separately, a window whose local save was refused keeps publishing its version to GitHub (persistAfterChange calls syncNext regardless of the save result, Shell.fs:1335).

**m4. A 401 does not halt the workspace, and failed records are not re-queued after re-authentication.**
- After a 401, syncNext moves on and lets every other investigation hit 401 too; each ends `failed`.
- `settings.save` clears `Attempted`, but workFor ignores `SyncFailed` (Shell.fs:1110). retrySync applies T18 only to the investigation on screen (Shell.fs:1356).
- After an expired token, every investigation must be opened and retried by hand.
- The evidence row AC-PROV-17 ("queue halted until re-authentication") is marked met; it is not.

**m5. resolve deviates from CAUSA-PROV-24 in three ways:**
- A remote that fails path checks (PATH_INVALID, an extra file) leaves the record `unknown` forever (CausaGitHub.fs:712) instead of T11 `conflicted` with that code.
- Case (d) does not verify the remote and labels an invalid remote `VERSION_STALE` (CausaGitHub.fs:725-728). Adoption re-validates it, so nothing invalid is shown.
- Case (b) stores the confirmed copy as our files with the token of head H, not `publish.commit` (CausaGitHub.fs:721).

**m6. A 404 on a tree or blob read during phase R maps to `failed` (CausaGitHub.fs:629-630).**
- These SHAs were just read from a commit, so CAUSA-PROV-19 treats a 404 as replication lag: retry, then `pending`. A transient lag needs the user to retry.

**m7. CAUSA-PROV-11 §2: the working copy is never checked against the root-manifest digests before publishing.**
- frozenFilesOf (Shell.fs:1091-1094) rebuilds the bytes with Store.build.
- Today it holds by construction: I verified that all 5 rebuilt events, manifest and snapshot files are byte-identical to the release (scratch rebuild.fsx).
- There is no runtime check, though.

**m8. CAUSA-PROV-02 §4: a new runId is never checked for absence at the head before use** (Shell.fs:1567, 1718, 1880).

**m9. The run publishes before the UI has shown it.**
- `settings.save` with a token immediately publishes every non-legacy investigation on the device. The runId is shown only once the save has happened (CAUSA-PROV-01 §3, "before the first write").

**m10. startInvestigation adds the StartInvestigation events to memory before any representability check** (Shell.fs:1519).
- If the chained ActivateInvestigation is refused (Decide or fileSetOf error), the investigation stays on screen and editable but is never saved or indexed. PLAUSIBLE edge.

**m11. Redirects are followed, so a moved repository is not reported as a configuration failure.**
- The declared rationale ("browsers drop Authorization cross-origin") does not cover GitHub's redirects, which stay on api.github.com.
- So a renamed or transferred repository is followed with the token, instead of `failed (configuration)` (CAUSA-PROV-19 and CAUSA-PROV-31 §3).
- Low impact, but the stated mitigation does not apply.

**m12. Dead state and misleading provenance.**
- `StoredVersion` is written but never read.
- A recovered or adopted investigation whose id is a frozen fixture id is labelled a working copy of `causa-storage-v1.0.0/<key>` even if its history does not descend from the frozen one (Shell.fs:1194, 1206).

## Tests that do not test what they claim

**T1. `[CAUSA-PROV-01] records stay bound to their workspace … the old record is still on the device, untouched`** (ShellTests.fs ~451)
- The effects of `configureOn "other-repository"` and `settings.clear` are never pumped into `world`.
- The final assertion checks a stale snapshot of the device. It passes although the shell overwrites that key (M2).

**T2. `[AC-PROV-24]`** (GitHubProviderTests.fs:394) is tautological.
- The helper builds events with `Actor.human "local-user"` itself, then asserts that "local-user" is present. The shell's actor is never exercised.

**T3. `[AC-PROV-13]` (provider) scans weak inputs, and the live claim is unasserted.**
- The provider test scans the test's own stub Keep payload (not `attemptOf`'s real record) and a hand-built `Record` with `LastError = Some "x"`. Faults and real log entries are not scanned.
- The live spec comment "Every request the app made went to api.github.com only" (github-live.spec.js:183) has no request listener behind it. Only `page.content()` is checked.

**T4. `[AC-PROV-09] [AC-PROV-16]` (provider) only round-trips `Record.recover`.**
- No test anywhere queues two offline changes, restarts, and publishes them in order. The evidence nevertheless cites shell and browser tests for AC-PROV-16.

**T5. CausaDataTests "a working copy round-trips … byte for byte"** checks only `events.ndjson` of one of the five investigations.

**T6. The sync.spec.js conflict test's name doesn't match what it tests.**
- The name says "GitHub's version is used only on request", but it never exercises adoption.
- Its stimulus (an extra file in `P`) yields PATH_INVALID with no remote copy, not a concurrent append.

**T7. Silent zero-test projects and retries.**
- The Firefox and WebKit projects select zero tests (`grep /$^/`) when the browser is absent, which reports as a pass.
- CI `retries: 1` lets a flaky provider test pass as "flaky". (The pattern predates this change; Firefox is new in 4e2251a.)

**T8. The live spec asserts with the real token as the expected value.**
- Examples: `expect(stored).not.toContain(token)` and `expect(page.content()).not.toContain(token)`. On failure Playwright prints the token. Only GitHub Actions secret masking prevents disclosure.
- Separately, the operator step puts the token in a git URL (`x-access-token:${TOKEN}@`). That is outside the adapter, but CAUSA-PROV-31 §2 forbids it for the adapter itself.

## Checked and found sound (no defect)

- **Request order** (CAUSA-PROV-13): ref, commit, walk, 3 blobs, tree, guard, commit, marker, ref, PATCH `force:false`, commit by SHA, walk, blobs. Creating a commit checks for exactly one parent `[Tc]`. The PATCH response must name `C`.
- **Path guard** (CAUSA-PROV-15): a level-by-level tree walk with siblings compared by SHA, and exactly 3 `100644` blobs at `P`. `checkTarget` validates before any request.
- **Kernel mapping:** a fetch throw on PATCH becomes `OutcomeUnknown` → `Unanswered` → `unknown`. Non-2xx responses arrive as Success with status and headers, so rate-limit 403 and 429 are classified correctly.
- **No false success:** every `confirmed` comes from a byte comparison of a SHA-addressed read (confirm, resolve (a)/(b), changedAt equal bytes, verified load).
- **Token:** never projected (no bind on `set-token`), never stored, and read-back settings ignore a legacy token. Commit messages hold ids and versions only.
- **XSS and CSP:** all data uses `data-text`, and every `href` is built from ids. CSP `connect-src` is `'self' https://api.github.com`.
- **Frozen data:** pinned in-engine (schema bundle and dataset identity match release-pin.json), all 17 stages plus golden digests, rejected per investigation, a not-pinned dataset refused as a whole. The vendor script fails the build on drift.

## Known deviations I agree are honest limitations, not defects

These are listed in PERSISTENCE-VALIDATION.md:
- No CAUSA-PROV-28 re-decision: AC-PROV-03 is partial; AC-PROV-04 and -05 are not met.
- Working copies are published one commit per investigation (AC-PROV-15 partial).
- The queue is per investigation, without command inputs (AC-PROV-16 partial).
- localStorage instead of IndexedDB, and no `navigator.storage.persist()`.
- No timer-driven retries or backoff (Limen has no timer effect).
- Fetch `cache`, `redirect` and `referrerPolicy` can't be set; ref reads use a nonce URL instead (but see m11 on redirects).
- Several local changes may be batched into one commit (CAUSA-PROV-18 §1 allows it).
- The data branch is unprotected, so the window between step 8 and step 9 is open.
- AC-PROV-23 is not applicable: the dataset is vendored, not read from GitHub.
- The `utf-8` blob encoding is unused.
- A truncated non-recursive listing goes to `pending` rather than a fallback.
- The provider gate is correctly declared incomplete.
