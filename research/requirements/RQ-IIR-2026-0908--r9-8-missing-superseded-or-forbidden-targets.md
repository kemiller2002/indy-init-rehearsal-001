---
id: RQ-IIR-2026-0908
title: "R9.8 Missing, superseded or forbidden targets"
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
      at: 2026-10-21T16:32:54.572Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Derived from frozen Indy Init release-profile requirements (Indy-init@b4c0994)"
derived_from: [INPUT-dc272c415fadff47]
---

# R9.8 Missing, superseded or forbidden targets

Source: `kemiller2002/Indy-init@b4c0994:docs/REQUIREMENTS.md#L159` (text sha256 `165ffbd5b874bc3f…`). Inventory id `IIR-R9.8`. Scope: `scoped`.

Verbatim requirement text (frozen; do not edit):

> A URL naming an entity that does not exist, or a repository the user cannot read, shows a not-found or not-permitted state with a way back to the investigation or home. It never shows a blank page and never shows a different investigation or record. A superseded or rejected entity is still shown as such, because history is never deleted (R2.4).
