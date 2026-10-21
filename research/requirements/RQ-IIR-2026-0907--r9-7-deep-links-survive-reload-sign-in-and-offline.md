---
id: RQ-IIR-2026-0907
title: "R9.7 Deep links survive reload, sign-in and offline"
status: accepted
version: 1.0.0
owners:
  - indy-init-rehearsal-001
created: 2026-10-21
updated: 2026-10-21
priority: high
tags: [indy-init, release-profile, r9]
provenance:
  contributions:
    EXE-20261021T163205535Z-ca660a1d:
      operations: [created]
      at: 2026-10-21T16:32:53.494Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Derived from frozen Indy Init release-profile requirements (Indy-init@b4c0994)"
derived_from: [INPUT-dc272c415fadff47]
---

# R9.7 Deep links survive reload, sign-in and offline

Source: `kemiller2002/Indy-init@b4c0994:docs/REQUIREMENTS.md#L156` (text sha256 `db9b4d705567e93a…`). Inventory id `IIR-R9.7`. Scope: `scoped`.

Verbatim requirement text (frozen; do not edit):

> Routes are hash-based (`index.html#/...`), with relative links and no `<base href>`. A reloaded or pasted deep link therefore never 404s on GitHub Pages (D-203; Limen DF-LIMEN-2026-0006). When GitHub sign-in is needed, the application keeps the target, returns to it afterwards, and never returns outside the application. A deep link to state that is already held locally opens offline, and its persistence state is shown truthfully.
