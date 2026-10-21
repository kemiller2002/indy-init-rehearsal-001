# SLICE-SECURITY-HARDENING — completion evidence (EX-INDY-INIT-2026-R001)

- Stored or remote histories are validated by replaying every CommandId group through `Decide` (`History.validate`). A history that fails is not adopted (RC3 class fix 39272f2; eight forgery shapes rejected; mutants killed).
- RC4 review F1 is fixed: the resolution gate's current conclusion is the latest by event sequence, not by the caller-supplied timestamp (3c0a0a9).
- RC4 review L1 is fixed: an envelope naming a different investigation from the loaded state is refused with ENVELOPE.MISMATCH (3c0a0a9).
- Agent authority boundary D-004: AGENT.PROPOSAL_ONLY.
- GitHub token held in memory only (R11.3).
- CSP, and evidence shown verbatim as data (R11.4).
- Verified in CI on 385ba15: 226/226 tests (engine 154, Playwright 72 including WebKit iPad).
- Accepted on the deployed site at dffaf08: deploy run 39672376807, accept 72/72.

**Recorded deviations:**

- R11.1 (GitHub App/OAuth) and R11.2 (least privilege) are not implemented, because no App or OAuth infrastructure exists.
- Pages serves the custom domain over plain http without redirect (Enforce HTTPS is off). This is a human-owned setting and has not been changed.
