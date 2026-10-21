---
id: RQ-IIR-2026-0803
title: "R8.3 Aegis integration"
status: accepted
version: 1.0.0
owners:
  - indy-init-rehearsal-001
created: 2026-10-21
updated: 2026-10-21
priority: high
tags: [indy-init, release-profile, r8]
provenance:
  contributions:
    EXE-20261021T163205535Z-ca660a1d:
      operations: [created]
      at: 2026-10-21T16:32:45.074Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Derived from frozen Indy Init release-profile requirements (Indy-init@b4c0994)"
derived_from: [INPUT-dc272c415fadff47]
---

# R8.3 Aegis integration

Source: `kemiller2002/Indy-init@b4c0994:docs/REQUIREMENTS.md#L130` (text sha256 `3687c046842036be…`). Inventory id `IIR-R8.3`. Scope: `scoped`.

Verbatim requirement text (frozen; do not edit):

> Invalid transitions and violated invariants must be represented through Aegis-compatible faults rather than hidden exceptions or generic error text.
