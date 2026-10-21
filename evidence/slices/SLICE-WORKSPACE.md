# SLICE-WORKSPACE — completion evidence (EX-INDY-INIT-2026-R001)

Commit: `6556d39f249f88b703ce4718558078b1fd522219` (local run). CI re-executes the same suites on every PR head and on deploy; only CI results count as Verified.

| Requirement | Status (local tests) | Citing tests |
|---|---|---|
| R9.1 | untested | 0 |
| R9.2 | tested | 1 |
| R9.3 | tested | 5 |
| R9.4 | untested | 0 |
| R9.5 | untested | 0 |
| R9.6 | tested | 3 |
| R9.7 | untested | 0 |
| R9.8 | tested | 4 |
| R9.9 | tested | 5 |
| R9.10 | tested | 4 |

Totals at this commit: 190 tests, 190 passed.

**Recorded deviation:** R9.1 real iPad Safari NOT tested (no device); Chromium iPad emulation and Playwright WebKit (CI) only. R9.7 offline/other-browser deep-link open not implemented (needs service worker or GitHub load-by-id).

Adversarial reviews: research-documents `indy-init-r001/run/adversarial-review-rc1.md`, `-rc2.md`.
