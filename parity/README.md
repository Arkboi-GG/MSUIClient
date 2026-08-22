# parity/ — MSUI ⇄ Benilla implementation tracking

This directory answers one question, continuously: **what does Benilla have that MSUIClient
still needs, and what has MSUI deliberately chosen to do differently?** Both codebases keep
evolving; this tracks the delta *logically* (by behavior and wire contract), not by eyeballing
screenshots.

## Ground rules

1. **MSUI is behind Benilla.** The default direction is: Benilla behavior gets implemented
   into MSUIClient.
2. **Deliberate deviations are allowed only for UI/graphics.** Where MSUI's existing look or
   presentation is preferred, that is recorded as a decision — never silently. Entries carry
   `deviationPolicy`: `ui-allowed` (a preservation decision is permissible) or `must-match`
   (gameplay/protocol — MSUI must match Benilla, no exceptions).
3. **Nothing is "done" by assertion.** Implemented work stays `implemented-unverified` until
   verified against a live session.

## Layout

| Path | What it is |
|---|---|
| `registry/` | **The source of truth.** One JSON per logical thing: `ui/<surface>`, `systems/<module>`, `protocol/<module>`, plus `engine/internals` (Benilla-engine inventory, never backlog). |
| `backlog.md` | Generated view of what's left, in priority order. Regenerate with `tools/rebuild_backlog.py`; never edit by hand. |
| `decisions/` | Numbered records of Nico's preserved-MSUI rulings (UI/graphics only). |
| `notes/` | Free-form per-packet/per-entry notes for agent handoff (see its README). |
| `claims/`, `traces/` | Behavioral evidence ledger (`current.jsonl`) — claims/traces reference hash-pinned facts. |
| `snapshots/current/` | The latest Benilla + MSUI snapshot pair: fact indexes with per-file SHA-256, source zips. |
| `packets/pair-*/` | The current snapshot generation's audit packets (evidence provenance for claims). |
| `tools/` | `seed_registry.py` (one-time seeding), `rebuild_backlog.py`, `check_drift.py`. |
| `archive/` | Everything historical: 11 superseded snapshot generations, old snapshots, one-shot promotion scripts. Reference only. |

## Registry entry semantics

```
status:        unreviewed | support-only | missing | partial | implemented-unverified
               | verified | preserved-msui | not-applicable
openItems[]:   kind = gap (implement it) | adjudicate (Nico decides: port or preserve)
               | verify (blocked on live session) — with resolved flag
benillaSources[]: path + fileSha256 + snapshotId  ← drift detection anchor
msuiAnchors[]: MSUIClient files implementing this thing
decisions[]:   ids of decisions/ records that constrain this entry
```

## The ongoing loop (both codebases keep moving)

1. **New Benilla snapshot lands** → replace `snapshots/current/benilla.facts.json` (and zip),
   run `tools/check_drift.py`. Every CHANGED/REMOVED/UNCOVERED line is a registry entry to
   re-review (or a new entry to add). Update the entry's `benillaSources` hashes only after
   re-reviewing the delta.
2. **MSUI changes** → if the change touches an entry's `msuiAnchors`, re-verify that entry's
   claims before trusting its status.
3. **Working an entry** → read its registry JSON and its notes first. Unresolved `adjudicate`
   items need Nico; `gap` items are implementable now; `verify` items need a live session.
4. **Recording a preference** → add `decisions/NNNN-<slug>.md`, set the entry's status to
   `preserved-msui`, link the decision id, and resolve the adjudicate item. Refuse to do this
   for `must-match` entries.
5. After any registry edit → `python tools/rebuild_backlog.py`.

After replacing the current snapshot, `python tools/refresh_engine_inventory.py` refreshes only
the non-behavioral engine inventory. It deliberately leaves new UI/system/protocol files uncovered
for review instead of hiding them inside `engine/internals`.

ParityDeck (`C:\Users\nico\source\repos\ParityDeck`) is the dashboard over all of this.
