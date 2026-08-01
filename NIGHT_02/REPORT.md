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
