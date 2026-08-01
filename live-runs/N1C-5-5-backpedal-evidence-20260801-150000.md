# NIGHT_01 5-5 — Backpedal animation evidence — 2026-08-01 15:00 local

Status: `CLOSED-FINDING`

## Increment and live batch

Added a reusable labeled frame-contact-sheet tool and an unattended six-frame backpedal capture.
On dedicated TEST with GM OFF, the protocol passed 24/24 and recorded 365 renderer/movement ticks
from input onset through release, with delivered `.gps` controls before and after.

The fresh trace mechanically passes both current and vanilla bands: max speed 4.5, stop distance
0, stall windows 0, hard cuts 0, substituted events 0. The contact sheet labels idle, onset,
three separated stride phases, and settle. Mechanical results are accepted; visual gait/pose
quality is queued as Q20 and is not claimed here.

## Evidence

- `live-runs/N1C-5-5-backpedal-trace-20260801-142250.csv`
- `live-runs/N1C-5-5-backpedal-verdicts-20260801-142250.csv`
- `live-runs/N1C-5-5-backpedal-contact-sheet-20260801-142250.png`
- `live-runs/N1C-5-5-backpedal-contact-sheet-20260801-142250.txt`
- `live-runs/runner-20260801-142250.csv`
- `scenarios/world/backpedal-evidence-live.txt`

