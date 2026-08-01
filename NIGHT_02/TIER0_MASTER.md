# NIGHT_02 TIER 0 — vanilla 1.12 UI parity (autonomous long-run work order)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md`; all PILOT_PROTOCOL standing laws
(1–12); and the NIGHT autonomy amendments, which are RE-INHERITED here in
full: NIGHT_01/1B_RESUME_ORDER.md (blocker definition, attempt floor, no
batch shelving, build-don't-shelve) and NIGHT_01/1C_AUTONOMY_AMENDMENTS.md
(Amendment 9 continuous execution / stopping-is-the-violation, Amendment 11
self-provisioning, Amendment 12 sweep discipline). Read those two files
before starting; they govern this run unchanged except where narrowed below.

## Mission

Bring every gameplay UI built in NIGHT_01 to VANILLA 1.12 (client build
5875) visual parity. NIGHT_01 accepted panels on wire-correctness and
queued appearance to Nico. That was deliberate but incomplete: 1.12 parity
is largely MECHANICAL, not perceptual, because Blizzard ships the exact
spec and art. This run does the mechanical 95% and queues only true feel
to Nico.

## Why parity is mechanical (the method that makes this autonomous)

The reference 1.12 UI is fully specified in shipped data:
- **FrameXML** (`.toc`/`.xml`/`.lua` under the reference MPQs) defines every
  frame's size, anchor points, backdrop, texture path, font object, and
  layer for the default UI.
- **DBC + MPQ art** hold the actual textures, fonts (FONT files), and
  colors the reference client draws.

So "match the look" decomposes into: (a) EXTRACT the reference spec for a
panel — geometry, anchors, texture paths, font objects, colors — into a
committed reference table (STRING columns, law 4); (b) RENDER the MSUI
panel using the ORIGINAL shipped assets at those coordinates; (c) DIFF
mechanically — geometry/anchor/texture-path/font deltas as verdict rows;
(d) produce a side-by-side contact sheet (reference render vs MSUI render)
for the ONLY residual that is perceptual: final feel. Acceptance is the
mechanical diff; the contact sheet is queued, not blocking.

## Asset-sourcing law (READ THIS — do not fabricate art)

- Use ONLY assets already present in the repo / shipped MPQs / the deployed
  reference data the project already relies on. Cite the source path
  (MPQ:file) for every texture/font used, in the evidence packet.
- If a required reference asset (a FrameXML file, a texture, a font) is NOT
  locatable in available data, that sub-item is `SHELVED-BLOCKED` with the
  exact missing path and ONE consolidated queue entry listing everything
  missing — never invent, redraw, approximate, or download art. This is the
  one hard blocker class for this run.
- Read-only for reference data; MSUI rendering is additive client work
  within fix authority (NIGHT_01 fix-authority table applies).

## Perceptual vs mechanical split (narrows Amendment 12 for UI)

MECHANICAL (agent accepts autonomously): frame dimensions, anchor points
and offsets, texture path identity, font object identity, font size, color
values (hex compare), layer/strata order, element presence/count, nine-slice
backdrop insets. Express as CSV verdict rows vs the extracted reference
table.
PERCEPTUAL (queued to Nico, contact sheet only): final "does it feel like
1.12" gestalt, animation timing feel, anything where the mechanical diff is
green but you cannot self-verify pixel fidelity. Do NOT block on these.

## Numbering, navigation, ledger, statuses

Identical to NIGHT_01 Tier 0: `PROGRESS.md` and `RULINGS_QUEUE.md` are
append-only and live in THIS folder (NIGHT_02/); per-item evidence is
run-dated + SHA-256 manifest under `live-runs/`; per-item sections append to
`NIGHT_02/REPORT.md`. Anti-lost loop after every item: append status,
re-read this file, re-read PROGRESS tail, take first OPEN item. Author
`SUB_<id>_*.md` from `../NIGHT_01/TEMPLATE_SUBDOC.md` when an item needs
tier-3 decomposition. Status vocabulary unchanged (CLOSED-PASS,
CLOSED-FINDING, SHELVED-RULING, SHELVED-BLOCKED).

**Manifest scope (Q21 pre-ruled for this run):** hash ONLY immutable
run-dated artifacts, never mutable source files or append-only ledgers. This
fixes the NIGHT_01 stale-manifest defect from the start.

## Continuous execution (Amendment 9, restated because it matters most)

Prior runs self-stopped after 20–40 min despite multi-hour capacity.
Mid-run status is FILE APPENDS ONLY. Closing an item immediately starts the
next OPEN item, same turn. Nothing in this run waits for Nico — rulings go
to the queue and you keep going. Your next and ONLY chat message is the
end-of-run packet. A relaunch message of `continue` = resume at first OPEN
item, no summary.

## TIER 0 ITEMS (execute in order)

- **0-1 → `1_PARITY_HARNESS.md`** — build the reusable parity harness FIRST:
  FrameXML/asset reference-spec extractor, the original-asset render path,
  the mechanical geometry/texture/font/color differ, and the side-by-side
  contact-sheet generator. Prove it on ONE simple existing panel end to end
  before fanning out. Everything downstream depends on this; if the harness
  cannot render with original assets at all, that is the run's one legitimate
  early hard finding — record it and shelve dependents, don't fake renders.
- **0-2 → `2_CORE_HUD.md`** — unit frames (player/target/party), action
  bars, cast bar, buff/debuff frames, minimap, chat frame, XP/rep bars,
  bags/backpack — the always-on HUD.
- **0-3 → `3_WINDOWS.md`** — the NIGHT_01 interface panels brought to
  parity: character sheet, spellbook, talent frame, quest log, merchant,
  trainer, bank, mail, auction house, loot, guild, gossip, taxi, trade.
- **0-4 → `4_SYSTEM_UI.md`** — game menu, options, keybindings, macro UI,
  tooltips (GameTooltip geometry/backdrop), error/UI-message text styling,
  cursor and dialog/popup frames.
- **0-5 → `5_POLISH.md`** — font-object audit across all frames, color
  constants audit (class colors, quality colors, reaction colors vs
  reference), strata/layer audit, and a final full-UI contact-sheet batch
  for Nico's single perceptual pass.

## End-of-run packet (mandatory, last commit)

Append to `NIGHT_02/REPORT.md`: full status table of every item; count of
mechanical-parity PASS vs FINDING; the rulings-queue count; the
missing-asset list (if any); gates summary; and the three most important
parity gaps found. Add a pointer line to the main SPEC_TOOLKIT report.
Leave the tree clean.
