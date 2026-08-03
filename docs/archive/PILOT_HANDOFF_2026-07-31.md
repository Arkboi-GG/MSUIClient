# PILOT HANDOFF — 2026-07-31 (session end; Nico moving to travel laptop)

For the FRESH pilot session: read PILOT_PROTOCOL.md first (roles, standing
laws 1–12 — note law 12 autonomy-first and the three-tier law 3), then the
tail of SPEC_TOOLKIT_REPORT_2026-07-30.md. This file is the cross-machine
state snapshot; trust the report tail over it if they disagree.

## Where things stand

CLOSED / ACCEPTED this session:
- 7C variant chain (SPEC 07–12): W2 attachments (3,535), W3 type-6 hair
  (7,677/5,114), W4 Option B pinned (8,889 exact; 689 Tauren type-8 rows =
  frozen forbidden cohort; Option A PARKED — reopen only on live V2b FAIL).
  W5 rebaseline done, hash-anchored (pre-7C NPC verdicts sha256
  cd8723…3eee).
- Movement plane (SPEC 13–14): trace/scripts/dual-band audit live as gate
  4; jump laws integrator-aware (g=19.2911, v0=7.9558 regressed); turn
  rates π / 0.75π, body-heading ±90°, landing 39/187, cross-fade mixer
  instrumented (blendWeight; hardCuts=0), JumpStart 37 handoff landed.
  BENILLA_VS_MSUI_MOVEMENT.md has a dated reconcile addendum. F3–F6
  (speed opcodes, step/slope, swim, capsule) NOT started, by ruling.
- Autonomy (SPEC 15–16 + law 12): GM console, combat verdict/trace/audit,
  protocol runner, disposable live bootstrap; CHECKS migrated (Nico-only =
  perceptual: V1/V2/V2b/M5).
- Real production wins found by the loop: chat language field (was
  Universal), same-map teleport receive/apply/ack (SPEC-19 T0), corrected
  vmangos creature deck, SSH access to 192.168.0.2 (key auth; password
  rotation recommended).

OPEN — the combat acceptance mystery (SPEC 17–21, all honest hard stops):
MSUI sends a byte-correct GM-off CMSG_ATTACKSWING for a proven-valid,
alive, 1.9 yd target (a guard attacked the SAME creature seconds prior) —
and vmangos returns NOTHING: no ATTACKSTART, no swings, no errors, and at
debug logging zero receive/dispatch/handler lines. Excluded so far: GM
mode, identity/GUID (triple-audited), framing, range, facing, mount,
stale combat state, dead/absent target, HOME-motion evade. The permitted
logging cannot distinguish packet loss from an unlogged predicate.
NEXT ORDER = SPEC_TOOLKIT_22_ATTACK_TRANSIT.md: travel-laptop re-bootstrap
(SSH key likely needs re-registration — ask Nico for the possibly-rotated
password at a HARD STOP; never embed it), client socket-flush evidence,
bounded wire capture (tcpdump if sudo / pktmon / built-in packet log),
decision table, HARD STOP. SPEC-21 P3/P4 (combat matrix completion) queue
behind that root cause.

QUEUED (unordered): combat fix queue after transit verdict (target-death
intent drop if proven, attack-error text display per Nico's CB2/CB3
defer-to-server ruling); F3–F6; panes/keybinds slices P and K per
GAMEPLAY_FOUNDATION_PLAN_2.md; 7C-3 CharSections ruling (needs V4);
initial-IntentOff audit-hygiene determination; benilla golden traces
(I14 stage B).

## Pilot craft notes (earned this session)

- Recompute the agent's numbers from committed artifacts before ruling;
  the three biggest catches (wrong revert target 48c16dc, non-
  discriminating W8 evidence, CB4's foreign-swing false PASS) were all
  invisible in the summaries and visible in the raw CSVs.
- The device bridge serves STALE bytes for overwritten paths — run-dated
  filenames + SHA-256 manifests are the standing countermeasure; verify
  via hashes, not restaged content.
- A "passing" metric can define the finding away (phaseResets=0 vs 18
  hard cuts); read the metric's definition before trusting its zero.
- Single-column instruments can't see blending/mixing — "absent" may mean
  "unobservable"; extend the instrument before concluding.
- Wild-mob GUID-sort contaminated two runs of combat evidence; positive
  controls and precondition proofs (in-store, alive, position, GM state
  by server response) are now mandatory in live protocols.
