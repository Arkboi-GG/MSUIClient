# NIGHT_02 vanilla 1.12 UI parity — implementation report 2026-08-02

Build status: BUILT+GATES-PASS

## Stages completed

| Stage | Status | Commit/summary |
|---|---|---|
| 0-1 parity harness | CLOSED-PASS | reusable extraction/render/diff/contact pipeline |
| 0-2 core HUD | CLOSED-FINDING | nine items exhausted; exact conditional gaps retained |
| 0-3 windows | CLOSED-FINDING | fourteen original-art window increments |
| 0-4 system UI | CLOSED-FINDING | seven system-UI increments |
| 0-5 polish | CLOSED-FINDING | four audits, 34-panel contact batch, hygiene |

## Files touched

| Area | New/Edit | What |
|---|---|---|
| `MSUIClient/` | Edit/New | original-art panels, draw instrumentation, popup/tooltip/error specimens, exact power colors |
| `tools/ui-parity/` | Edit/New | MPQ FrameXML extraction, original-asset rendering, full-reference mechanical diff, crop/contact generation |
| `NIGHT_02/` | Edit/New | progress, report, rulings, full-reference rule |
| `live-runs/N2-*` | New | immutable references, actuals, verdicts, contacts, audits, manifests |

## Symbol verification

| Cited symbol | Found as | Note |
|---|---|---|
| `CollectUiParityDraw` | real draw-variable collector | legacy metadata cannot earn mechanical acceptance |
| `WowSkin.Dialog` / `WowSkin.Tooltip` | shipped nine-slice renderers | MPQ assets only |
| `DrawCarriedItem` | real inventory cursor attach | original item icon follows pointer |
| `PowerColor` | gameplay HUD constant mapper | corrected to exact vanilla values |

## Deviations

No missing-asset blocker occurred. Mechanical gaps were closed as FINDING with
exact CSV worklists; contact sheets were queued and never treated as acceptance.
Replay-labelled populated fixtures remain explicitly labelled where the live
server could not authoritatively provide the expanded state.

## Findings (bugs noticed, NOT fixed)

Large conditional element inventories remain NOT-DRAWN; draw evidence needs
named font/backdrop/layer fields; some dynamic windows still lack complete
authoritative wire/state paths. Exact rows are in `NIGHT_02/RULINGS_QUEUE.md`.

## Console evidence

Final solution build: PASS, 0 errors. Combat-wire, interface-wire,
portrait-camera, and move-audit: PASS. Manifest validation: 44/44 prior run
directories PASS. Raw-art audit: zero stray dumps.

## Live checks for Nico

Perform the single perceptual pass over
`live-runs/N2-0-5-5-final-contact-batch-20260802-011500/contact-batch-page-01.png`
through `contact-batch-page-05.png`, using `contact-index.csv` to open any of
the 34 full-resolution source sheets. Mechanical CSV verdicts remain the
authoritative acceptance record.

Full item table and counts: `NIGHT_02/REPORT.md`.
