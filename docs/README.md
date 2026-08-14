# MSUI documentation

The repository root intentionally keeps only `PROJECT_HANDBOOK.md`,
`CODE_STRUCTURE_LAW.md`, and `SETUP.md`. Detailed project knowledge lives here so
it remains available without crowding the root.

## Where to look

- `current/spells/` — active spell-system traces, implementation records,
  semantic audits, the current session record, and the next-agent prompt.
- `current/ui/` — the active interface-proportions and functionality handoff.
- `current/project-context/` — gameplay checks, gameplay-UI port context, and
  the character creation specification.
- `current/research/` — Benilla/MSUI comparisons and empirical check records
  still used to explain current implementation choices.
- `systems/` — one-system-per-file ground truth. These documents preserve
  empirical rules, known traps, verification boundaries, and open debt. Read the
  relevant system document before changing that subsystem.
- `plans/` — foundation and numbered implementation plans. Completed plans still
  contain derivations and evidence that are intentionally not duplicated in the
  handbook.
- `archive/` — local-only superseded handoffs and completed investigation
  reports. This directory is ignored by Git; it remains available on this
  machine without entering future commits.

## Authority order

Start with `../PROJECT_HANDBOOK.md`, then read the relevant document under
`systems/` or `current/`. Treat plans and `current/research/` as derivation
records; where a system document explicitly supersedes a plan, the system
document is current.

**Before adding, moving, or renaming any source file, read
`../CODE_STRUCTURE_LAW.md`** — it is the binding authority on *where code lives and
how it is named* (the `GameLoop` god-class and its `GameLoop/<bucket>/` layout, the
subsystem folders, the one-direction layering rule). System docs remain the
authority on how a system *behaves*.

Nothing under this directory is generated build output. Do not bulk-delete it
as a file-count cleanup.
