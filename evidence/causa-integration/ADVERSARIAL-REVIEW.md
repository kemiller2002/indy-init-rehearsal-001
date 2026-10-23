# CAUSA-REHEARSAL-INTEGRATION-001 — Independent adversarial review (Phase 8)

**Reviewer.** An independent agent with read-only access. It was given the code and the specs (CONTRACT, PROVIDER-GITHUB,
CLARIFICATIONS, INVARIANTS), not the author's conclusions. It reviewed `55f49f6..3a46d7d`. It reproduced three
findings with its own scratch tests (ADV1–ADV3), written outside the repository. Its unedited report is in
[ADVERSARIAL-REVIEW-REPORT.md](ADVERSARIAL-REVIEW-REPORT.md).

**Verdict of the review:** no critical defect.
- The frozen release cannot be written.
- Nothing is reported as "Saved to GitHub" without a byte comparison against a commit re-read by SHA.

The review found:

| severity | count |
|---|---|
| major | 5 |
| minor | 12 |
| tests that do not test their claim | 8 |

**Disposition:** every major finding is fixed in commit `fe5d347` and has a regression test. That commit is the one
deployed (see DEPLOYMENT-VERIFICATION.md).

## Major findings

| id | finding | disposition | regression test |
|---|---|---|---|
| M1 | after a `CAUSA.HISTORY_REWRITTEN` conflict, "Use GitHub's version" was offered; adopting it made the next edit PATCH the rewritten investigation (CAUSA-PROV-29 §5) | **fixed**: a rewritten or invalid remote carries no adoptable copy, in publish and in resolution | provider `[CAUSA-PROV-29 §5] a rewritten history is conflicted without an adoptable copy` |
| M2 | the sync record was keyed by investigation only; changing the run or workspace overwrote the old workspace's record, losing an `unknown` outcome and its confirmed copy (CAUSA-PROV-01 §3, -30 §6) | **fixed**: one stored book per investigation holds a record per workspace; only the configured workspace's record is used, and the others are kept untouched | shell `[CAUSA-PROV-01] records stay bound…` (rewritten: it now pumps the device and checks all three workspaces' records, closing T1) |
| M3 | adopting replaced the device copy even if the conflict-copy write failed, and still said the version "was kept"; conflict keys could collide | **fixed**: adoption reads the device copy, writes every replaced version (the device's and this window's) as a uniquely keyed conflict copy, and replaces only after all are stored. A failed copy replaces nothing and says so. Save-conflict copies report their own result | shell `[CAUSA-PROV-29] adopting GitHub's version replaces nothing when this device's version cannot be kept` (full device, then retry) |
| M4 | re-saving settings for the same workspace mid-publish started a second program; late answers could clear the busy flag of a newer program (CAUSA-PROV-20 §7) | **fixed**: programs carry a generation, and answers for any other generation are dropped. A token-only change keeps the running program; a workspace change abandons it (T19/T20 in memory). GitHub's copy that is a verified byte prefix of the attempt (an earlier write of ours that landed late, CAUSA-PROV-10 §4 / -24 (c)) is now built on instead of reported as a conflict | shell `[CAUSA-PROV-20 §7] re-entering the token mid-publish never starts a second program`; provider `[CAUSA-PROV-10 §4] [CAUSA-PROV-24 (c)] this client's own earlier write…` |
| M5 | "Recover this run" overwrote device copies the app had reported as unreadable | **fixed**: recovery skips every investigation the device holds in any form (loaded, deferred, unreadable or indexed) | shell `[CAUSA-PROV-08] recovery never writes over a stored investigation this device could not read` |

## Minor findings

| id | finding | disposition |
|---|---|---|
| m1 | an undecodable sync record was overwritten | **fixed**: it is left untouched, and its investigation is not published (the notice says so) |
| m2 | adopting wrote the device copy without a fast-forward check | **fixed** by M3: the device copy is read and kept as a conflict copy first |
| m3 | the local read-then-write check can race between two windows; a window whose local save was refused still published | **partly fixed**: an investigation with a refused local save is no longer published. The read/write race remains, because `localStorage` has no compare-and-set. **Limitation** |
| m4 | a 401 did not halt the workspace, and failed records were not requeued after re-authentication | **fixed**: a 401 halts publishing; a new token lifts the halt and returns authentication failures to `pending` (T18). Test: shell `[CAUSA-PROV-19] [T18]…` |
| m5 | resolution: an invalid path stayed `unknown` forever; an invalid remote was not verified in case (d); case (b) stored the head commit | **fixed** for the first two: they are now conflicts. The (b) bookkeeping (head instead of the published commit) is **not changed**: the next publish compares blob SHAs, not commits, so it has no effect on decisions |
| m6 | a 404 on a SHA just read mapped to `failed` | **fixed**: it is lag → `pending` (CAUSA-PROV-06 §3) |
| m7 | working-copy bytes were not checked against the root-manifest digests before publishing | **fixed**: the events SHA-256 and the manifest and snapshot SHA-256 (without the final LF) must equal the verified root-manifest values, or nothing is published |
| m8 | a new runId is not checked for absence at the head (CAUSA-PROV-02 §4) | **not fixed: limitation**. Run ids carry a timestamp and 24 random bits. A collision would surface as `refused` (VERSION_STALE) per investigation, never as an overwrite |
| m9 | saving settings with a token publishes at once, as the run id is first shown | **not fixed: limitation**. The run id and path are shown in the notice and on the settings page on save |
| m10 | if activation after StartInvestigation is refused, the new investigation is kept in memory but not saved | **not fixed: limitation** (an edge case inherited from R001's start flow) |
| m11 | redirects are followed (GitHub's stay on api.github.com, so the token is re-sent to the same origin) | **not fixed: limitation**. Limen cannot set `redirect: "manual"`. A response that is not the expected JSON still fails closed. PERSISTENCE-VALIDATION.md is corrected |
| m12 | `StoredVersion` is unused; recovered or adopted copies with a frozen id were labelled "working copy of" without descending from it | **fixed** for the label: the origin is shown only when the history starts with the frozen bytes. `StoredVersion` remains a harmless display field |

## Tests that did not test their claim

| id | disposition |
|---|---|
| T1 | the workspace-binding test now pumps the settings change into the device and checks every workspace's record |
| T2 | the actorId is checked through the shell: the device and GitHub copies of the app's own events carry `local-user`, never the GitHub login |
| T3 | new shell test: GitHub errors that echo the token (401/403/422/502) leave no trace of it in the view or anywhere in device storage. **Mutation-checked**: with redaction disabled, the test fails |
| T4 | new shell test: two offline changes, a reload, then back online; both are published in order, byte for byte |
| T5 | all five frozen investigations, all three files, round-trip byte for byte |
| T6 | the misleading browser test name was corrected (its stimulus is a foreign file in the directory, giving PATH_INVALID) |
| T7 | CI runs `scripts/browser-project-counts.py`, which fails when any browser project executes no test. Flaky tests are reported separately, not as passes |
| T8 | the live spec only uses booleans for token checks; the operator step sends the token in a masked header, not in a URL |

## Checked and found sound by the reviewer

- request order;
- `force:false`;
- the parent check before the PATCH;
- confirmation by SHA;
- the path guard;
- PATCH network errors → `unknown`;
- rate-limit classification;
- the token is never shown, never stored and never in commit messages;
- no XSS (text binding only);
- CSP `connect-src`;
- frozen-data pins, all 17 stages and the golden digests;
- per-item and whole-dataset refusal;
- the vendor gate.

## Agreed limitations (already declared)

- no automatic re-decision (AC-PROV-03 partial, -04/-05 not met);
- one commit per working copy (AC-PROV-15 partial);
- a per-investigation queue without command inputs (AC-PROV-16 partial);
- `localStorage` instead of IndexedDB, and no `persist()`;
- no timers;
- no fetch cache, redirect or referrer options;
- batched commits;
- an unprotected data branch;
- AC-PROV-23 not applicable;
- `utf-8` blob encoding unused;
- truncated listing → `pending`;
- **provider gate incomplete**.
