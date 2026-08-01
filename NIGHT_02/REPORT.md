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
