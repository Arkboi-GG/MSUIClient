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

## 2-7 Chat frame — CLOSED-FINDING

The absent gameplay chat frame now receives server chat/notifications into a
bounded message display and draws the build-5875 main frame, zero-alpha default
background state, scroll controls, General tab, and edit presentation from
shipped art. A real TEST `.gps` response populated the capture. The visible main
cohort passes 63/63 mechanical verdicts; the full main inventory retains 25
resize/alternate-state presence findings. Evidence:
`live-runs/N2-0-2-7-chat-frame-20260801-173500/N2-0-2-7-chat-frame-20260801-173500.md`.

## 2-8 XP / reputation bars — CLOSED-FINDING

The live XP path was recaptured and the missing conditional reputation-watch
bar was added with original status, reputation, and dwarf-border assets. The
complete reputation inventory passes 84/84 mechanical verdicts; the visible XP
cohort passes 35/35 while its full inventory retains four conditional presence
findings. Evidence:
`live-runs/N2-0-2-8-xp-reputation-20260801-175000/N2-0-2-8-xp-reputation-20260801-175000.md`.

## 2-9 Bags / backpack — CLOSED-PASS

The runtime container layout was corrected to the shipped bottom-right anchor
chain, with the original backpack background, quickslot rings, close button,
and 12px title. The runtime cohort passes 56/56 mechanical verdicts. Evidence:
`live-runs/N2-0-2-9-backpack-20260801-181000/N2-0-2-9-backpack-20260801-181000.md`.

## Binding pilot correction — instrument truth before doc 3

The earlier PASS counts are withdrawn. They compared call-site declarations
against the reference and used post-hoc subsets. The corrected differ requires
a precommitted `scope=all-reference-elements` rule and reports coverage before
verdicts. Legacy rows are DRAWN-NOT-INSTRUMENTED, never PASS.

- 2-1: player before 3/30, after 6/30 with 24 NOT-DRAWN; target before 3/35,
  after 7/35 with 28 NOT-DRAWN. Draw-derived rows expose 68 and 80 deltas.
- 2-2: before 6/39; after 0/39 with 33 NOT-DRAWN and six instrument-debt rows.
- 2-3: main before 14/21, after 0/21 with 7 NOT-DRAWN; button before 4/10,
  after 0/10 with 6 NOT-DRAWN; multibar before 5/121, after 0/121 with 116 NOT-DRAWN.
- 2-4: before 4/6; after 0/6 with 2 NOT-DRAWN.
- 2-5: before 4/105; after 0/105 with 101 NOT-DRAWN. Reference rendering is
  SHELVED-BLOCKED on the exact missing aura-spell-to-SpellIcon runtime-state hook.
- 2-6: before 13/21; after 0/21 with 8 NOT-DRAWN.
- 2-7: before 9/34; after 0/34 with 25 NOT-DRAWN.
- 2-8: reputation before 12/12, after 0/12 with zero NOT-DRAWN; XP before
  5/9, after 0/9 with 4 NOT-DRAWN.

Every NOT-DRAWN set and every claimed contact sheet now has an append-only
entry in `NIGHT_02/RULINGS_QUEUE.md`. Player/target reference rendering now
resolves implicit fill regions and mirrored texcoords, producing nonblank
shipped-art contact sheets. Evidence:
`live-runs/N2-0-2-9P-instrument-truth-20260801-183000/N2-0-2-9P-instrument-truth-20260801-183000.md`.

## 3-1 Character sheet — CLOSED-FINDING

Coverage: CharacterFrame 1/13 with 12 NOT-DRAWN; PaperDollFrame 5/127 with
122 NOT-DRAWN. The full shipped-art reference and real populated paperdoll now
render side by side; all exact gaps are queued. Evidence:
`live-runs/N2-0-3-1-character-frame-20260801-190000/N2-0-3-1-character-frame-20260801-190000.md`.
