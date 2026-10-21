---
id: RQ-IIR-2026-1004
title: "R10.4 Conflict detection"
status: accepted
version: 1.0.0
owners:
  - indy-init-rehearsal-001
created: 2026-10-21
updated: 2026-10-21
priority: high
tags: [indy-init, release-profile, r10]
provenance:
  contributions:
    EXE-20261021T163205535Z-ca660a1d:
      operations: [created]
      at: 2026-10-21T16:33:00.736Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Derived from frozen Indy Init release-profile requirements (Indy-init@b4c0994)"
derived_from: [INPUT-dc272c415fadff47]
---

# R10.4 Conflict detection

Source: `kemiller2002/Indy-init@b4c0994:docs/REQUIREMENTS.md#L180` (text sha256 `4428d952a82cb6fa…`). Inventory id `IIR-R10.4`. Scope: `scoped`.

Verbatim requirement text (frozen; do not edit):

> The application must detect stale writes or conflicting remote changes and must not silently overwrite them.
