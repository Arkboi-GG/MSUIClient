# NIGHT_01 TIER 0 — master list (autonomous long-run work order)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md` and every PILOT_PROTOCOL standing
law (1–12). This folder is a self-contained multi-item work order. Execute
items in order until the list is exhausted. This order is designed so that
after item 1-1's front-loaded interactive gates, NOTHING blocks: every
would-be hard stop that is not a safety fence becomes SHELVE-AND-CONTINUE.

## Numbering and navigation law

- Tier 0 = this file. Tier-1 docs are `N_<NAME>.md` in this folder; item
  `n` of tier-1 doc `1` is `1-n`; item `m` under it is `1-n-m`.
- You MAY author deeper-tier docs yourself using `TEMPLATE_SUBDOC.md`,
  named `SUB_<id>_<slug>.md` (e.g. `SUB_2-3_auction-house.md`), committed
  in this folder. You may NOT edit Tier-0 or Tier-1 item lists — findings
  about the lists themselves go to `RULINGS_QUEUE.md`.
- ANTI-LOST LOOP (mandatory): after closing or shelving any item, (1)
  append its status line to `PROGRESS.md`, (2) re-read this file top to
  bottom, (3) re-read the `PROGRESS.md` tail, (4) take the FIRST item that
  is not yet recorded as CLOSED or SHELVED. Never re-open a CLOSED or
  SHELVED item without new committed evidence.

## Status vocabulary (use exactly these)

- `CLOSED-PASS` — item done, acceptance met, evidence committed.
- `CLOSED-FINDING` — item done; result is a defect/finding, recorded with
  evidence; any fix beyond your authority shelved to the queue.
- `SHELVED-RULING` — needs Nico's approval; queue entry written with the
  decision needed, the evidence, and your recommendation; MOVE ON.
- `SHELVED-BLOCKED` — mechanically impossible right now (missing symbol,
  server down, tool limit); exact blocker recorded; MOVE ON.

## Ledger law

- `PROGRESS.md` is APPEND-ONLY (stale-bytes countermeasure). One line per
  event: `<UTC-ish local time> | <item id> | <status> | <one-line result> |
  <evidence path or commit>`.
- `RULINGS_QUEUE.md` is APPEND-ONLY. One numbered entry per shelved
  decision: what is needed from Nico, the evidence paths, your
  recommendation. Nico reads this file in the morning — it IS the
  deliverable for shelved work.
- Per-item evidence: run-dated artifacts + SHA-256 manifests under
  `live-runs/` as always; per-item report sections appended to
  `NIGHT_01/REPORT.md` (append-only), with a single pointer line added to
  `SPEC_TOOLKIT_REPORT_2026-07-30.md` at the end of the run.

## Fix-authority table (replaces per-item hard stops)

You MAY implement autonomously, without queueing, when ALL hold:
- client-side only; additive or narrowly-scoped per law; and
- an instrument YOU built (or an existing one) mechanically verifies the
  fix (before/after verdict rows, sweep diff, or live-protocol PASS); and
- no change to laws, gate lists, cohorts, baselines, frozen key files,
  or accepted evidence; and
- one commit per root cause, four gates green at the boundary.

You MUST shelve (SHELVED-RULING) — never attempt:
- anything server-side (code, config, DB writes, sysctl, packages,
  restarts), beyond what SPEC-26 explicitly authorizes in item 1-1;
- combat behavior changes (the root-cause fix itself is Nico's ruling);
- F3–F6 (speed opcodes, step/slope, swim, capsule) — excluded by standing
  ruling; do not start them under any tier item;
- baseline/cohort regeneration, expected-list edits, law edits;
- anything perceptual (art, feel, framing) — collect contact sheets /
  screenshots as evidence and shelve the judgment to the queue;
- new credentials, elevation beyond the established relay, or network
  scope beyond 192.168.0.2.

## Standing-law recap (all still binding)

Instrument before fix. Report must equal act. Batch beats anecdote.
Additive-only. One commit per root cause; stage-boundary commits; four
gates (build, combat-wire, portrait-camera 1224/1289/56, move-audit) at
every boundary. Shown ⇒ copyable. Scope fences hard. vmangos DB READ-ONLY.
Autonomy-first: Nico is the evidence path of last resort; everything here
is agent-runnable except what the fix-authority table shelves. Run-dated
filenames; never overwrite an existing evidence path. The dedicated GM
test account only. Positive controls and precondition proofs mandatory in
every live combat/spell protocol (the Z0 mechanical gate pattern is now
the house standard for any send-precondition claim).

## Interactive gates — front-loaded, item 1-1 ONLY

The sudo password ask (SPEC-26 W0b) and the Administrator PowerShell
capture relay are the run's only interactive moments and happen first,
while Nico is present. After item 1-1 concludes (or shelves), Nico may
leave; if any later item believes it needs him, it is wrong — shelve it.
The elevated relay MAY be kept available for the whole run for bounded
pktmon/netsh capture ONLY (same fence as SPEC-23/24); if the relay dies
mid-run, capture-dependent sub-items go SHELVED-BLOCKED, everything else
continues.

## TIER 0 ITEMS (execute in order)

- **0-1 → `1_COMBAT_ROOTCAUSE.md`** — SPEC-26 execution (interactive
  gates), W1–W3 discrimination, then SPEC-21 P3/P4 combat matrix.
- **0-2 → `2_INTERFACES.md`** — gameplay interface build-out and
  validation: vendor, trainer, questing, loot, bank, mail, auction house,
  crafting/professions, guild, tabard, talents, character/inventory.
- **0-3 → `3_SPELLS.md`** — spell casting correctness (wire, GCD,
  cooldowns, errors, interrupts), cast animations standing/moving,
  channeled spells, auras/buffs, visual-effect presence sweeps.
- **0-4 → `4_WORLD_INTERACTIONS.md`** — gossip, game objects, resting/XP,
  death/rez flow completion, environment interaction audits (excluding
  F3–F6).
- **0-5 → `5_HOUSEKEEPING.md`** — queued non-perceptual backlog: panes/
  keybinds slices P and K, initial-IntentOff audit hygiene, benilla golden
  traces I14 stage B, backpedal animation evidence, deferred-list triage.

## End-of-run packet (mandatory, last commit)

Append to `NIGHT_01/REPORT.md`: a single status table of EVERY tier-1 and
tier-2 item id with its final status; the rulings-queue count; total
commits; gates summary; and the three most important findings. Add the
pointer line to the main SPEC_TOOLKIT report. Leave the tree clean.
