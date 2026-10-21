---
id: RQ-IIR-2026-0909
title: "R9.9 No secrets or evidence content in URLs"
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
      at: 2026-10-21T16:32:55.582Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Derived from frozen Indy Init release-profile requirements (Indy-init@b4c0994)"
derived_from: [INPUT-dc272c415fadff47]
---

# R9.9 No secrets or evidence content in URLs

Source: `kemiller2002/Indy-init@b4c0994:docs/REQUIREMENTS.md#L162` (text sha256 `ceb7f06460ac90f5…`). Inventory id `IIR-R9.9`. Scope: `scoped`.

Verbatim requirement text (frozen; do not edit):

> URLs never contain a GitHub token, an OAuth code, an AI provider key, evidence text or attachment content, or personal data typed about anyone. Entities are addressed by their GUID identifiers. Limen's reserved parameter names (`token`, `code`, `key`, `session` and the rest) are refused.
