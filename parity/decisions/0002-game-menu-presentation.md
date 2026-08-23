# 0002 — Preserve MSUI Game Menu presentation

- Registry entry: ui/gamemenuframe
- Date: 2026-08-23
- Decided by: Nico

## Benilla behavior
Benilla presents its Era Game Menu ladder and temporarily tints the bag bar while the modal owns input.

## MSUI behavior (preserved)
MSUI keeps its complete existing Game Menu composition and existing bag-bar appearance.

## Why
Nico explicitly designated the Game Menu and its independent menu scaling as no-touch MSUI presentation.

## Boundaries
Escape ordering, modal ownership, pushed micro-menu state, blocked underlying input, logout actions, sounds, and every reachable behavior still match Benilla. This decision does not authorize changing gameplay UI scaling or Escape/menu scaling.
