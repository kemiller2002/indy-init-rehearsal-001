# SLICE-FACTORY-PROOF — completion evidence (EX-INDY-INIT-2026-R001)

- Factory/proof view `#/factory` (S20/S21) reads `factory.json` written at build time: scoped requirements, slice per requirement, Praxis work-item states, build commit, and CI engine-test traceability for the deployed commit (deploy workflow writes `test-results/traceability.json` before `npm run build`). It states that its totals cover engine tests only.
- Requirement traceability: `scripts/trace-requirements.py` (TRX + Playwright JSON → `verification/traceability.json` per CI run; deploy acceptance → `traceability-deployed.json`).
- Deploy identity: deploy workflow `accept` job checks served `build-info.json` equals the commit, then runs the end-to-end suite against the live URL.
- Praxis→Dokimos verified completion of SLICE-SEMANTIC-KERNEL at 0b00087 (Dokimos 0.7.0; unattested).
- Dokimos code-metric snapshot of src/engine (research-documents run/dokimos-snapshot-engine-0b00087.json).
- Requirements covered: R14.1, R14.2, R14.3 (see factory.spec.js, ShellTests factory cases).

**Recorded deviations:** the factory view shows build-time state only, not live telemetry. R13.7 churn metrics are reported in the experiment report, not in the app. The browser end-to-end totals are not embedded in factory.json, because the site is built before the browser suite runs.
