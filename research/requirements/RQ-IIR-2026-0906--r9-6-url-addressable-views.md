---
id: RQ-IIR-2026-0906
title: "R9.6 URL-addressable views"
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
      at: 2026-10-21T16:32:52.422Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Derived from frozen Indy Init release-profile requirements (Indy-init@b4c0994)"
derived_from: [INPUT-dc272c415fadff47]
---

# R9.6 URL-addressable views

Source: `kemiller2002/Indy-init@b4c0994:docs/REQUIREMENTS.md#L153` (text sha256 `3004257d6143bc88…`). Inventory id `IIR-R9.6`. Scope: `scoped`.

Verbatim requirement text (frozen; do not edit):

> Every screen and every addressable thing has a URL, and pasting that URL into another browser opens the same view. Addressable things are investigations, hypotheses, evidence items, unknowns, assumptions, contradictions, actions and experiments, findings, causes, corrective actions, verifications, counterfactual branches, historical versions and reports. The URL also carries the view's navigable state: the selected version or comparison pair, filters, sort, search, tab and page. This is the portfolio deep-linking standard (Praxis SAF-URL-1..10), and the route table is in [Screen Contracts](SCREEN-CONTRACTS.md#url-contract).
