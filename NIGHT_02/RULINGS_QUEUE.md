# NIGHT_02 rulings queue (append-only)

No rulings queued as of 2026-08-01 15:35 local.

## Q1 — 2-1 — unit-frame NOT-DRAWN worklist and perceptual review

- Corrected full-reference coverage: player 6/30 with 24 NOT-DRAWN; target 7/35 with 28 NOT-DRAWN. Exact sets are the `coverage` DELTA rows in `live-runs/N2-0-2-9P-instrument-truth-20260801-183000/player-frame-final-diff.csv` and `target-frame-final-diff.csv`.
- Work: implement the NOT-DRAWN chrome/text/state elements; close all remaining draw-derived geometry/anchor/font/color/layer deltas.
- Review: `player-frame-contact-isolated.png` and `target-frame-contact-isolated.png` contain nonblank shipped-art references and clean actual captures.

## Q2 — 2-2 — party-frame NOT-DRAWN worklist and perceptual review

- Corrected coverage: 0/39; 33 NOT-DRAWN; six DRAWN-NOT-INSTRUMENTED. Exact set: `party-frame-corrected-diff.csv` in the pilot packet.
- Work: derive evidence for the six real draws, then implement the 33 absent reference elements.
- Review: `live-runs/N2-0-2-2-party-frames-20260801-160500/party-frame-contact.png`.

## Q3 — 2-3 — action-bar NOT-DRAWN worklist and perceptual review

- Corrected coverage: main 0/21 (7 NOT-DRAWN), button 0/10 (6 NOT-DRAWN), multibar 0/121 (116 NOT-DRAWN). Exact sets: the three `*-corrected-diff.csv` files in the pilot packet.
- Work: replace legacy declarations with draw-derived evidence and implement every NOT-DRAWN element.
- Review: the three contact sheets in `live-runs/N2-0-2-3-action-bars-20260801-162500/`.

## Q4 — 2-4 — cast-bar NOT-DRAWN worklist and perceptual review

- Corrected coverage: 0/6; 2 NOT-DRAWN; four DRAWN-NOT-INSTRUMENTED. Exact set: `cast-bar-corrected-diff.csv`.
- Review: `live-runs/N2-0-2-4-cast-bar-20260801-164000/cast-bar-contact.png`.

## Q5 — 2-5 — buff-frame NOT-DRAWN worklist and blocked reference render

- Corrected coverage: 0/105; 101 NOT-DRAWN; four DRAWN-NOT-INSTRUMENTED. Exact set: `buff-frame-corrected-diff.csv`.
- SHELVED-BLOCKED render only: the harness lacks the runtime-state hook `render --state aura-spell=<id>` needed to join the aura spell to `SpellIcon.dbc` and select its shipped icon. No art is missing and none may be invented. Build that hook before claiming a buff reference contact sheet.

## Q6 — 2-6 — minimap NOT-DRAWN worklist and perceptual review

- Corrected coverage: 0/21; 8 NOT-DRAWN; 13 DRAWN-NOT-INSTRUMENTED. Exact set: `minimap-corrected-diff.csv`.
- Review: `live-runs/N2-0-2-6-minimap-20260801-171500/minimap-contact.png`.

## Q7 — 2-7 — chat-frame NOT-DRAWN worklist and perceptual review

- Corrected coverage: 0/34; 25 NOT-DRAWN; nine DRAWN-NOT-INSTRUMENTED. Exact set: `chat-frame-corrected-diff.csv`.
- Review: `live-runs/N2-0-2-7-chat-frame-20260801-173500/chat-frame-contact.png`.

## Q8 — 2-8 — XP/reputation NOT-DRAWN worklist and perceptual review

- Corrected coverage: reputation 0/12 with zero NOT-DRAWN and 12 DRAWN-NOT-INSTRUMENTED; XP 0/9 with four NOT-DRAWN and five DRAWN-NOT-INSTRUMENTED. Exact sets: `reputation-bar-corrected-diff.csv` and `xp-bar-corrected-diff.csv`.
- Review: both contact sheets in `live-runs/N2-0-2-8-xp-reputation-20260801-175000/`.

## Q9 — 3-1 — character-sheet NOT-DRAWN worklist and perceptual review

- Corrected coverage: CharacterFrame shell 1/13 with 12 NOT-DRAWN; PaperDollFrame 5/127 with 122 NOT-DRAWN. Exact sets: `character-frame-diff.csv` and `paperdoll-diff2.csv`.
- Work: instrument and implement the absent header, slot, stat, model, resistance, and tab elements using the existing functional draw path.
- Review: `live-runs/N2-0-3-1-character-frame-20260801-190000/paperdoll-contact2.png`.
