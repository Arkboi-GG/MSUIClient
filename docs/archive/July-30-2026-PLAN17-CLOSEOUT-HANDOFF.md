# July 30, 2026 — PLAN_17 closeout attempt handoff

## Scope and result

This pass addressed only the two named `load-azeroth-15.json` frames and then ran the PLAN_17
sign-off gate. The code changes and diagnostics are retained because they moved named fields, but
PLAN_17 is **not closed**: the first complete create/enter/observe/delete gate still fails the
40 ms ceiling, zero-Gen2 requirement, and per-VISIBLE-unit 2 s lifecycle limit. Two additional
cycles and the PLAN_08 crossing were not run after the first cycle failed their hard prerequisite.

## What landed

### Fix 1 — player avatar handoff

- Enter-world work is split from the network pump into curtain-covered stages.
- A matching character-select renderer can be transferred to the world instead of destroyed.
- Auto-login materializes that selected booth renderer under the already-opaque curtain before
  `BeginWorldLoad`, even when character select has not rendered yet.
- Race/sex-stable appearance changes prepare on `AssetWorkerPool`, upload on `GpuUploadWorker`, and
  leave the old complete avatar visible until the replacement is ready.
- Fade waits for `AppearanceReady`, so it cannot reveal a missing or partly equipped player.
- The load timeline now captures the complete BeginWorldLoad-through-clear-plus-60-second frame,
  allocation, and collection window used by the gate.

The final gate logs `adopted cached character-select avatar` followed by `player model reused` and
does not queue or finalize an appearance rebuild during the measured load.

### Fix 2 — object-update parse storage

- Compressed-update inflate scratch comes from `ArrayPool<byte>` and is returned after parsing.
- Update masks come from `ArrayPool<uint>` and field dictionaries start at measured capacity.
- Parsed updates use reusable 4,096-reference chunks instead of a growing contiguous LOH array.
- The parser tests cover both ordinary and compressed packets through the reusable destination.

### Supporting sign-off instrumentation

- The cold-start harness uses an isolated stock-settings file and removes it afterward.
- The retained post-clear artifact records max frame, over-40/unmeasured counts, total and worst
  allocation, worst post-clear allocation, GC generations, and exception frames.
- Temporary allocation-probe logging and the rejected worker-count experiment were removed.

## A/B evidence

| Fix | Before — `load-azeroth-15.json` | After | Result |
|---|---|---|---|
| Player avatar | frame 17: 43.27 ms; 39.78 ms `load-network-pump`, bracketed by synchronous `ApplyServerCharacter` | `load-azeroth-25.json`: booth avatar adopted and reused; no appearance rebuild/finalize; largest pump bucket 14.12 ms | Named synchronous pump work moved; unrelated terrain/GC frames still fail the global ceiling |
| Parse buffers | frame 18: 41.59 ms; 16.13 ms Gen2; 20.27 MB allocated by off-thread parse | `creature-load-azeroth-25.json`: worst post-clear allocation 12.58 MB; three Gen2 collections in the full window | Post-clear allocation moved 38%, zero-Gen2 field fails |

Useful intermediate evidence is retained in `load-azeroth-21.json` (booth-adoption A/B: adopted and
reused, pump max 10.52 ms) and rejected `load-azeroth-22.json` through `load-azeroth-24.json`
allocation/scheduling probes. The terrain-buffer and priority experiments were backed out because
they did not move the hard fields.

## Full gate 1 — authoritative evidence

Artifacts: `cycle-plan17-final-gate-1.json`, `load-azeroth-25.json`, and
`creature-load-azeroth-25.json`.

- Harness created `Coldfdujzgfp`, observed for 60 seconds, and deleted it successfully.
- All nine phases exited `condition-met`; no watchdog fired.
- Terrain, WMO, outdoor-doodad, and interior-doodad first-ten dequeue distances are monotone.
- `mpqArchiveOpensDuringLoad = 0` and the log has exactly one
  `[shader] attached compiled and linked` line.
- 45 units were known at clear. First creature draw was already present at clear; first visible draw
  occurred at 5.255 s, which is single-digit.
- The complete window max is 75.7697 ms. It has two frames above 40 ms and one over-40
  `unmeasured` frame.
- The window has 119 Gen0, 31 Gen1, and 3 Gen2 collections. Total allocation is 2.269 GB;
  worst post-clear allocation is 12,578,976 bytes.
- There are 16 VISIBLE lifecycle rows. Their worst `firstDrawMs - spawnPacketMs` is 5,640.67 ms,
  so the required 2,000 ms limit fails.

## PLAN_17 §8 definition-of-done table

| Item | Status | Proof or required check |
|---|---|---|
| 1. Every phase condition-met | PASS for gate 1 only | `load-azeroth-25.json` |
| 2. Creature ≤2 s after clear; units >0 | AUTOMATED PASS for gate 1; live view still required | `load-azeroth-25.json`: first draw 0 ms, units 45; visually confirm the first NPC |
| 3. No frame >40 ms and no over-40 unmeasured frame through clear +60 s | FAIL | `creature-load-azeroth-25.json`: max 75.7697 ms, two over 40, one unmeasured |
| 4. First-ten distances monotone | PASS for gate 1 only | `load-azeroth-25.json` dequeue rows |
| 5. PLAN_08 Elwynn/Stormwind crossing stays ≤40 ms | NOT RUN | Repeat the saved `[32,48] → [32,49] → Stormwind` `[stream]` protocol and field-diff it against the prior handoff |
| 6. Curtain/live reveal check | HUMAN-ONLY | Confirm immediate cover, no dialog/HUD leak, and a populated fully equipped first reveal |

Three consecutive sign-off runs remain required after items 3 and the added zero-Gen2/lifecycle
gates are fixed. Gate 1 cannot be counted as one of those three.

## Verification

- Release build passes with only the known `Engine/UI/GlueAdditive.cs:141` CA2014 warning.
- Combat/movement/targeting/wire checks pass, including reusable compressed object parsing.
- Portrait-camera and MPQ ordering checks pass.
- `git diff --check` passes.

## Explicitly parked work

- The previously measured 57.8 ms `Present`/swap stall remains parked in the
  `swap-and-events` family with its existing evidence; it was not reclassified as this fix.
- Delete-modal physical mouse click-through remains a human-only check.
- S7's process-wide texture layer remains a separate plan as specified by PLAN_17 §7. This pass did
  not turn the transient parse-pooling fix into that broader cache project.

## Remaining sign-off work

1. Bring the 75.77 ms terrain/GC frame and 42.67 ms post-clear update frame below 40 ms and
   eliminate all Gen2 collections without moving into S7 scope. The current evidence points to
   retained terrain/WMO preparation pressure rather than the compressed scratch buffer or player.
2. Make every VISIBLE lifecycle row reach first draw within 2,000 ms of its spawn packet.
3. Run three consecutive full create/delete cycles and retain all six artifacts.
4. Perform the PLAN_08 crossing comparison and the two human curtain/delete-modal checks.
5. Only after those pass, change this handoff and PLAN_17 reconciliation from open to done.
