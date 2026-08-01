# NIGHT_02 — vanilla 1.12 UI parity report

## 2026-08-01 15:35 local — item 0-1 parity harness

Status: `CLOSED-PASS`

Built the reusable FrameXML reference extractor, original-MPQ-asset reference renderer,
actual client draw-path capture, STRING-column mechanical differ, actual-frame cropper,
and side-by-side contact-sheet generator. The end-to-end GameMenuFrame proof found and
fixed a stale 195x226/seven-row transcription against the winning build-5875 definition
(195x246/eight rows), then passed 77/77 geometry, anchor, texture-path, font, color,
layer, and presence verdicts. DIALOG ordering now prevents foreground unit/combat text
or developer windows from crossing the modal. Evidence:
`live-runs/N2-0-1-parity-harness-20260801-153500/N2-0-1-parity-harness-20260801-153500.md`.

All five stage-boundary gates pass; the solution build retains only the standing CA2014 warning.
No reference asset is missing. The contact sheet is queued for perceptual review and did not block.

Main toolkit report: `../SPEC_TOOLKIT_REPORT_2026-07-30.md`.

## 2026-08-01 15:55 local — item 2-1 player / target unit frames

Status: `CLOSED-FINDING`

Added actual-draw captures for the unit-frame roots, backgrounds, and portraits,
fixed logical-scale measurement, and corrected target buffs/debuffs to the separate
build-5875 grids. All instrumented geometry/anchor/layer rows pass. The unchanged
reference cohorts retain 9 player and 28 target presence deltas, chiefly conditional
template rows plus unenumerated existing frame/text draws, so the item is not claimed
as parity PASS. Evidence:
`live-runs/N2-0-2-1-unit-frames-20260801-155500/N2-0-2-1-unit-frames-20260801-155500.md`.

## 2026-08-01 16:05 local — item 2-2 party frames

Status: `CLOSED-FINDING`

Implemented the missing build-5875 `SMSG_GROUP_LIST` consumer and a party-member
renderer at the shipped 128x53 / 83px-stack geometry. The shipped include chain
is now resolved mechanically by the harness. A real TEST-character draw capture
passes all 42 representative geometry/anchor/texture/layer verdicts. The complete
FrameXML inventory retains 33 presence deltas for conditional debuff, pet, leader,
disconnect, and anonymous wrapper rows; pet-member stats wire support remains the
material functional gap. Evidence:
`live-runs/N2-0-2-2-party-frames-20260801-160500/N2-0-2-2-party-frames-20260801-160500.md`.

## 2026-08-01 16:25 local — item 2-3 action bars

Status: `CLOSED-FINDING`

Corrected the main quickslot ring's FrameXML Y conversion and hotkey font box,
then added functional bottom-left, bottom-right, right, and left multi-action
bars using the shipped action-slot mappings and 42px pitch. Real TEST captures
pass 98/98 main-bar, 28/28 action-button, and 35/35 representative multi-bar
verdicts. Full inventories retain 7, 6, and 116 presence deltas respectively
for conditional controls and the unenumerated remaining repeated buttons.
Evidence:
`live-runs/N2-0-2-3-action-bars-20260801-162500/N2-0-2-3-action-bars-20260801-162500.md`.

## 2026-08-01 16:40 local — item 2-4 cast bar

Status: `CLOSED-FINDING`

Mechanical extraction found the cast bar 49 logical pixels too high and its
label 14 pixels too high. Both were corrected to the shipped bottom-55 root and
TOP+5 label geometry; the spark is now half-pixel exact. A real active-cast
capture passes 28/28 representative verdicts. The full six-row inventory retains
two conditional presence deltas (background color quad and success flash).
Evidence:
`live-runs/N2-0-2-4-cast-bar-20260801-164000/N2-0-2-4-cast-bar-20260801-164000.md`.

## 2-5 Buff/debuff frame — CLOSED-FINDING

The player aura family now uses the build-5875 top-right anchor, 30px icons,
8-column/35px wrapping, a dedicated harmful third row, and the original shipped
debuff overlay crop. TEST self-provision applied and removed Arcane Intellect
through verified VMaNGOS GM syntax. The populated representative cohort passes
28/28 mechanical verdicts; the complete conditional/template inventory retains
101 presence findings. Evidence:
`live-runs/N2-0-2-5-buff-frame-20260801-165500/N2-0-2-5-buff-frame-20260801-165500.md`.

## 2-6 Minimap — CLOSED-FINDING

The previously absent minimap is now a functional build-5875 frame backed by
the shipped translated minimap tile, original border/button art, player-position
UV selection, zoom controls, zone label, and tracking-aura presentation. TEST
Find Herbs exercised the tracking path. The visible normal state passes 91/91
mechanical verdicts; the complete state inventory retains eight conditional
button-texture presence findings. Evidence:
`live-runs/N2-0-2-6-minimap-20260801-171500/N2-0-2-6-minimap-20260801-171500.md`.
