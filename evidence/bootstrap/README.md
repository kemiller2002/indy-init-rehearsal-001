# SLICE-BOOTSTRAP evidence (EX-INDY-INIT-2026-R001)

- Starting state: empty repository (no refs), recorded in research-documents `indy-init-r001/baseline/baseline-manifest.json`.
- Bootstrap: `conditor init --preset indy-init` (Conditor 0.10.8 @902277d) → `kickoff-init.out`; `conditor verify` → `kickoff-verify.out` (exit 0); lock `.conditor/lock.json`; first commit 9799ed8 (factory-generated only).
- Requirements: `conditor requirements import` plan sha256:5d65d10e… (23 slice items, 992 traced) → `req-import.out`; Praxis inbox intake INPUT-dc272c415fadff47 → 82 canonical records `research/requirements/RQ-IIR-2026-*`.
- Build/test at kernel commit: `tests.txt`.
- No pre-existing application code: the only application sources are those committed after 9799ed8 during the timed window.
