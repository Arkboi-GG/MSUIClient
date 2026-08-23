# 0003 — Preserve MSUI Inspect presentation

- Registry entry: ui/inspectframe
- Date: 2026-08-23
- Decided by: Nico

## Benilla behavior
Benilla uses its authored InspectFrame composition and panel plumbing.

## MSUI behavior (preserved)
MSUI keeps its native Inspect rendering and left-panel integration while retaining the reference-visible information and controls.

## Why
Nico explicitly placed Inspect presentation on today's no-touch list.

## Boundaries
Inspected-unit identity, equipment data, model contents, item effects, tooltips, lifecycle, interaction, and protocol behavior remain must-match.
