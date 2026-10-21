---
id: RQ-IIR-2026-1104
title: "R11.4 Prompt/data boundaries"
status: accepted
version: 1.0.0
owners:
  - indy-init-rehearsal-001
created: 2026-10-21
updated: 2026-10-21
priority: high
tags: [indy-init, release-profile, r11]
provenance:
  contributions:
    EXE-20261021T163205535Z-ca660a1d:
      operations: [created]
      at: 2026-10-21T16:33:07.961Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Derived from frozen Indy Init release-profile requirements (Indy-init@b4c0994)"
derived_from: [INPUT-dc272c415fadff47]
---

# R11.4 Prompt/data boundaries

Source: `kemiller2002/Indy-init@b4c0994:docs/REQUIREMENTS.md#L203` (text sha256 `5bf6145b444d3cbc…`). Inventory id `IIR-R11.4`. Scope: `scoped`.

Verbatim requirement text (frozen; do not edit):

> External evidence must be treated as untrusted input. It must not be able to silently redefine system instructions or engineering policy.
