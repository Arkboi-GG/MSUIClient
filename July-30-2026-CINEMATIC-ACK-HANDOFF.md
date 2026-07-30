Before: 40-60 s from enter-world to the delayed NPC cohort (the convicted burst was about +38 s after clear); after: 8.65 s and 7.03 s to the first visible NPC in two new-character runs.

# July 30, 2026 — cinematic visibility acknowledgement handoff

## Root cause and fix

Benilla's `net/apply/session.rs:48-56` identifies the server mechanism: while a triggered cinematic
is unacknowledged, vmangos `Player::UpdateCinematic` anchors object visibility to the flying camera
rather than the player's body. A first login's race intro therefore despawns nearby units and streams
remote cohorts until the cinematic ends.

MSUI had `CMSG_COMPLETE_CINEMATIC = 0x00FC` in its enum but no
`SMSG_TRIGGER_CINEMATIC = 0x00FA` entry, no dispatch case and no completion send. It now:

- recognizes the trigger opcode and reads its four-byte cinematic ID;
- sends `CMSG_COMPLETE_CINEMATIC` with an empty body immediately in the same packet pump, matching
  Benilla's unimplemented-cinematic ESC skip;
- records `[net] cinematic <id> triggered - acked (skip)` in the hitch/load event ring;
- includes the bidirectional in-memory wire timeline in load and +60-second creature records.

The active-mover parity check also found MSUI's send in the wrong position. It was emitted only after
`SMSG_LOGIN_VERIFY_WORLD`; it now follows `CMSG_PLAYER_LOGIN` immediately, matching Benilla
`net/io.rs:415-416`. The later duplicate send was removed.

## New-character proof

| Field | Before | New run 1 | New run 2 |
|---|---:|---:|---:|
| Artifact | `load-azeroth-14.json` / original watched run | `load-azeroth-17.json` | `load-azeroth-18.json` |
| Units known at clear | 14-15 in the pre-fix automation | 45 | 45 |
| Cinematic | ignored | cinematic 81 logged and acked | cinematic 81 logged and acked |
| Trigger → empty ack | absent | outbound ack at +19.75 ms from load origin; handler log present | 52 ms (`0.278 s` inbound → `0.330 s` outbound), one pump |
| Nearby packet timing | body-area units despawned; later server waves around +38 s | initial stream / before load origin | 43 unit lifecycle rows received by +3 s |
| First visible NPC from enter/load origin | 40-60 s symptom | 8.646 s | 7.025 s |
| Visible units drawn in +60 s record | none in the pre-fix default-facing automation | 37 units drew; 15 remained currently visible at snapshot | 17 |

Run 2's `wireTimeline` proves the login ordering and acknowledgement exactly:

| Time | Direction | Packet | Size |
|---:|---|---|---:|
| 0.174 s | out | `CMSG_PLAYER_LOGIN` | 8 |
| 0.174 s | out | `CMSG_SET_ACTIVE_MOVER` | 8 |
| 0.278 s | in | `SMSG_TRIGGER_CINEMATIC` | 4 |
| 0.330 s | out | `CMSG_COMPLETE_CINEMATIC` | 0 |

The +38 s visibility waves did not persist after the acknowledgement.

## Existing-character control

`cycle-cinematic-existing-control.json` used the harness's new non-destructive
`--existing-character first` mode. It selected `Test`, did not create or delete it, and verified the
same roster entry after observation.

- No `SMSG_TRIGGER_CINEMATIC` arrived and no completion packet was sent.
- `CMSG_PLAYER_LOGIN` and `CMSG_SET_ACTIVE_MOVER` were adjacent at 0.230 s.
- `unitsKnownAtClear = 41`; all phases exited `condition-met`.
- Six currently visible units drew; first visible NPC was 3.106 s from the load origin.
- No hitch had `dominantPhase: unmeasured`.

The existing parked 60.1 ms `load-network-pump` frame occurred in this control and was not modified,
per this pass's scope.

## Mandatory movement-ack parity audit

| Family | MSUI implementation | Observed inbound in new runs 1/2 or existing control | Decision |
|---|---|---|---|
| Run speed (`SMSG_FORCE_RUN_SPEED_CHANGE` → ack) | Opcode constants only; no handler/writer | No | Parked; no speculative implementation. |
| Run-back speed | Absent | No | Parked. |
| Swim speed | Absent | No | Parked. |
| Walk speed | Absent | No | Parked. |
| Swim-back speed | Absent | No | Parked. |
| Force root / unroot | Absent | No | Parked. |
| Water-walk / land-walk | Absent | No | Parked. |

The audit checked inbound opcodes `0x00E2`, `0x00E4`, `0x00E6`, `0x02DA`, `0x02DC`, `0x00E8`,
`0x00EA`, `0x00DE` and `0x00DF` in `load-azeroth-17.json`, `-18.json` and `-19.json`; count was zero
in every record. No acknowledgement code was invented.

## Verification completed

- Two fresh Human-character cycles completed create → enter → +60 s observation → delete → roster
  verification: `cycle-cinematic-new-1.json` and `cycle-cinematic-new-2.json`.
- One non-destructive existing-character control completed and verified the selected character
  remained present: `cycle-cinematic-existing-control.json`.
- Release solution and cold-cycle tool builds pass. The only solution warning is the pre-existing
  CA2014 at `Engine/UI/GlueAdditive.cs:141`; no new warning was introduced.
- Combat/movement/targeting/wire targeted checks pass.
- Portrait-camera defaults, MPQ priority and HumanMale provenance checks pass.
- `git diff --check` reports no whitespace error (only existing line-ending notices).

## Parked, unchanged

- synchronous `ApplyServerCharacter` enter-world frame;
- object-parse allocation/gen-2 frame;
- swap/present stall;
- delete-modal physical mouse sign-off;
- S7 texture layer.
