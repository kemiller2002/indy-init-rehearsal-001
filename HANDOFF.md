# Indy Init Rehearsal 001 handoff

## Objective

Bootstrap Indy Init Rehearsal 001 as a greenfield Praxis pilot.

## Current state

- Praxis 3.16.0 greenfield profile installed on 2026-10-21; Praxis 3.25.0 since
  WI-0004 (2026-11-25).
- echelon-current 1.42.0 since WI-0004 (Conditor 0.10.29 `upgrade --current`):
  Praxis 3.25.0, Ordo 1.20.0, Limen 0.9.1, Forma 1.0.1, Folio 0.4.1, Aegis
  2.0.2, Percepta 0.1.0 and the optional Echelon theme 1.1.0 (loaded after
  Forma `all.css`: `echelon.css`, `echelon-fonts.css`, `echelon-app.css`;
  `scripts/build-site.mjs` copies the theme's `dist/`, fonts included).
  - `conditor.json` declared Forma 0.5.0 while `package.json` pinned 0.9.0
    since the first commit; the declaration now says 0.9.0 and `conditor init`
    re-locked it before the upgrade. Folio's foundations capability lost its
    `releaseArtifact` key, which Conditor cannot prove for 0.4.1 and later
    Praxis foundations schemas forbid.
  - The Forma 1.0 namespace break was migrated with Forma's NamespaceMigrator
    (`src/kernel`, `tests/browser`). Folio 0.4.1 still names its elements
    `ef-print-*`, so those stay as they are.
- Project charter is a draft.
- No first vertical slice, evidence record, hypothesis, or experiment has been
  accepted.
- The operating system is under evaluation.

## Validation

Run:

```bash
./praxis registry check
./praxis validate
```

## Continuity

An executor session is disposable. Before handing off, commit and push the
work, record `./praxis work checkpoint --id ID --occurred-at NOW --summary ...
--next-action ...`, and push the `.ros/` state. A successor reads
`./praxis work context ID --text` and takes over with `./praxis work continue`.

## Unresolved questions

1. What concrete communication problem and user should the first slice serve?
2. What baseline workflow will be used for comparison?
3. What data, privacy, safety, and accessibility constraints apply?
4. Which outcome would distinguish useful engineering from additional process?

## Next action

Complete `PROJECT-CHARTER.md`, choose the first bounded outcome, and record its
baseline and acceptance criteria in `context/CURRENT-STATE.md`.
