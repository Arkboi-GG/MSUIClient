# SPEC_TOOLKIT_15 — combat plane instruments (report-only, HARD STOP)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md`. Plan authority
`GAMEPLAY_FOUNDATION_PLAN_2.md` doctrine applied to the combat plane. Nico's
ruling (2026-07-31): combat instrumentation before F3–F6; F3–F6 remain
untouched. INSTRUMENTS ONLY: zero behavior change to combat logic, the swing
state machine, animation, or the wire beyond the explicitly authorized C0
console. Live combat weirdness (attack, move-while-attacking, cancel,
re-attack) is the target: this order makes it observable; fixes come after
Nico's live protocol run.

Standing practice, new as of this order: every sweep/trace artifact gets a
RUN-DATED filename (`<name>-<yyyymmdd-HHmm>.csv`), and every stage's commit
message or report append lists SHA-256 of new artifacts — the pilot's device
bridge serves stale bytes for overwritten paths, so unique names + hashes
are the verification channel.

## C0 — GM send capability

Nico's vmangos GM account works server-side; it is unknown whether MSUI can
SEND chat commands. Determine whether the client has any CMSG_MESSAGECHAT
send path. If yes: expose it. If no: add a DevTools "GM console" — a text
input that sends a typed line as CMSG_MESSAGECHAT (say), with the last N
commands recallable and every send echoed to the verdict ring. Additive,
DevTools-gated, no other wire changes. This unblocks the scenario deck for
combat and later F3 (`.speed`).

Gate: live-checkable item written into Session 3 (type `.gps`, paste the
server response); build + standard gates.

## C1 — combat trace + [verdict:combat] channel

Event-driven channel (ring + console, click-to-copy) and a toggleable
run-dated CSV `dumps/combattrace-<name>-<stamp>.csv`. Events sampled from
the REAL combat/animation/net state (report=act):

- auto-attack intent: on/off transitions with CAUSE
  (user-start | user-cancel | target-change | target-death | server-stop)
- wire: CMSG_ATTACKSWING / CMSG_ATTACKSTOP sends;
  SMSG_ATTACKSTART / ATTACKSTOP / ATTACKERSTATEUPDATE receives (opcode,
  target guid, timestamp)
- swing state: each swing-timer arm/fire/reset with the timer value and
  reset cause; weapon speed in use
- eligibility per tick while attacking: in-range flag, in-arc/facing flag,
  and what the client DID about ineligibility (hold swing, spam, nothing)
- animation: AnimChoice entries for attack clips (which clip, one-shot
  return), overlap with locomotion clips (the mixer state from SPEC-14)
- target: selection changes mid-combat with guids

The F10 dump gains a `combat` block: current intent, swing timer, last ~50
combat events.

## C2 — scenario deck + Session 3 combat protocol

`scenarios/combat/` GM scripts (vmangos syntax; cite SETUP.md): `dummy.txt`
spawns a low-damage melee target near the movement-arena vantage plus a
second one for target-switching; `reset.txt` despawns/cleans. Then extend
CHECKS_GAMEPLAY Session 3 with protocol items, each with paste slots:

- CB1 attack a stationary target; stand still through 3+ swings
- CB2 attack, then orbit the target while attacking (range stays, facing
  changes) — do swings continue? paste eligibility transitions
- CB3 attack, walk out of range mid-swing, walk back — what does the
  client send/do at each edge?
- CB4 attack, cancel (Esc / stop key), verify ATTACKSTOP sent once; attack
  again — clean re-arm, no double-swing, no dead timer
- CB5 switch to the second target mid-swing — timer behavior + wire
- CB6 kill the target mid-swing — intent OFF with cause target-death,
  no lingering swings
- CB7 attack while moving the whole time (chase) — locomotion + attack
  anim overlap observations

Each item names the verdict lines that answer it — never prose pixels.

## C3 — combat-audit

`tools/combat-audit <trace>` mechanical checks over any recorded session:
swing cadence matches weapon speed ±1 tick while eligible; exactly one
CMSG_ATTACKSWING per intent-start and one ATTACKSTOP per cancel (spam
detector); no swings while intent off; no illegal state transitions
(enumerate the machine in the tool; unknown transition = FAIL row); every
SMSG_ATTACKERSTATEUPDATE within an intent-on window; attack one-shots return
to the movement-driven base state (the SPEC-14 mixer law). Output verdicts
CSV, run-dated. No committed baseline yet — the first baseline is cut from
Nico's live protocol session AFTER the weirdness is diagnosed and fixed.

## C4 — HARD STOP

Append: C0 capability finding, instrument inventory, and the Session 3
combat protocol reference. No fixes, no baselines. The next order is
written from Nico's pasted CB1–CB7 results.

Standard four gates every boundary; one commit per stage; SHA-256 manifest
per stage; report appends with actual-versus-predicted where applicable.
