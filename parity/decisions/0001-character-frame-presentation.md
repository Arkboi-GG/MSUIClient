# 0001 — Preserve MSUI Character presentation

- Registry entry: ui/characterframe
- Date: 2026-08-23
- Decided by: Nico

## Benilla behavior
Benilla uses its `CharacterFrame.xml` composition, statistics layout, and model-facing/rotation constants.

## MSUI behavior (preserved)
MSUI keeps its established native Character page composition, broad statistics and resistance panels, labels and damage formatting, plus its zero-facing model and 0.12-radian rotation tap.

## Why
This is a complete, intentional MSUI presentation that Nico explicitly placed on today's no-touch list.

## Boundaries
Only presentation and model-pane tuning are preserved. Equipment fields, stats, tooltips, sounds, rotation lifecycle, protocol state, and newly missing behavior must still match Benilla. This does not protect the distinct Pet Paper Doll.
