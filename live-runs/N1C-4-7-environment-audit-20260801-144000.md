# NIGHT_01 4-7 — Environment audit sweeps — 2026-08-01 14:40 local

Status: `CLOSED-PASS`

## Additive audit batch

Added an unattended, read-only current-build environment audit that joins the shipped
`AreaTrigger.dbc` volumes to the read-only exported teleport catalog, validates every destination
map and finite target, inventories every instance WDT and its entrance/arrival plan, and samples
the resident liquid renderer at the player plus four adjacent points. It changes no movement,
portal, instance, liquid, or excluded F3–F6 behavior.

## Results

- Dual-band move audit: all 8 current baseline traces and 8 expected verdict sets passed.
- Portal traversal set: 115/115 joined portal definitions passed volume, target-map, and finite-
  destination checks.
- Instance entry set: 33/33 instance rows passed terrain arrival planning or were explicitly
  catalogued by their supported global-WMO/zero-tile representation; no missing WDT or refused
  planned arrival was found.
- Water set: five live current-map samples exercised dry/surface classification while recording
  resident tile count, wake texture, and authored-color state.
- Live protocol: 8/8, bracketed by delivered `.gps` controls; environment summary `PASS` with
  `portalErrors=0` and `instanceErrors=0`.

## Evidence

- `live-runs/runner-20260801-141637.csv`
- `live-runs/verdicts-20260801-141637.txt`
- `live-runs/N1C-4-7-environment-ui-20260801-141637.png`
- `live-runs/N1C-4-7-move-audit-20260801-144000.txt`
- `scenarios/world/environment-audit-live.txt`

