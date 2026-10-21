---
id: RQ-IIR-2026-0910
title: "R9.10 Route inventory, codec and sharing"
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
      at: 2026-10-21T16:32:56.681Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Derived from frozen Indy Init release-profile requirements (Indy-init@b4c0994)"
derived_from: [INPUT-dc272c415fadff47]
---

# R9.10 Route inventory, codec and sharing

Source: `kemiller2002/Indy-init@b4c0994:docs/REQUIREMENTS.md#L165` (text sha256 `8a18e9bc166be7dc…`). Inventory id `IIR-R9.10`. Scope: `scoped`.

Verbatim requirement text (frozen; do not edit):

> The application publishes `.echelon/routes.json` (`echelon.routes/v1`). Pure `parse` and `format` functions own every route, and generated tests prove `format (parse url)` is the canonical URL. The application uses Limen's routing module once Limen 0.9.0 is installed. Screens that people discuss or hand off offer a Copy link action.
