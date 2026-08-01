# NIGHT_02 tier-1 doc 1 — parity harness (build first)

Parent: `TIER0_MASTER.md` item 0-1. All Tier-0 rules apply. Attempt floor:
each item needs a committed increment + one real output before any shelf.

- **1-1 Reference-spec extractor.** Parse the reference 1.12 FrameXML for a
  panel: emit a committed reference table (CSV, STRING columns) of frame
  name, size, anchor point/relativeTo/relativePoint/offset, backdrop
  texture path + insets, layer/strata, font object + size, color hex, per
  element. Cite MPQ:file source. Prove on one panel.
- **1-2 Original-asset render path.** Render an MSUI panel using the shipped
  textures/fonts at the extracted coordinates (report=act: the same draw
  path the client uses, not a mock). Cite every asset's MPQ:file.
- **1-3 Mechanical differ.** Compare MSUI render state to the reference
  table: geometry/anchor/texture-path/font/color/layer deltas as verdict
  rows; PASS = zero deltas in mechanical columns. This is the acceptance
  instrument for the whole run.
- **1-4 Contact-sheet generator.** Side-by-side reference-vs-MSUI image per
  panel for the perceptual queue; labeled, run-dated.
- **1-5 End-to-end proof.** Run 1-1..1-4 on one simple existing panel (e.g.
  the bag/backpack or minimap) and commit the full artifact set. If 1-2
  cannot draw with original assets, record the exact blocker (missing render
  hook or asset), file the queue entry, and shelve HUD/window dependents —
  do not fabricate renders to keep moving.
