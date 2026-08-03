# SPEC_TOOLKIT_17 — combat diagnosis round 2: instrument fixes + root-cause re-run (HARD STOP)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md`, PILOT_PROTOCOL standing laws
(incl. 12). Ruling record (2026-07-31, Nico): **CB2/CB3 law = vanilla
defer-to-server.** The client never gates swings on range/facing; it sends
intent, displays the server's error events, and keeps the attack intent
latched through ineligibility. The error-text DISPLAY is a later order; this
order is diagnosis + instrument fixes only. Combat behavior fixes and F3–F6
remain untouched.

Pilot findings folded in (recomputed from run 20260731-141730 artifacts):

- CB4's swing confirmation is VOID: runner step 38's `waitfor SwingReceive`
  was almost certainly satisfied by the foreign NPC swing at t=41.459
  (attacker 0x…E7F9 — a wild-world mob, not a spawn). The CB4 wire pairing
  (2 starts / 2 stops, clean edges) STANDS; the swing wait does not. CB4's
  machine-verified status is downgraded to partial pending re-run.
- Zero attack-error opcodes appear anywhere in the verdict log — the wire
  instrument does not capture the attack-error SMSG family, so a
  silently-holding server is currently indistinguishable from a rejecting
  one. This blind spot must close before any CB1 conclusion is drawable.
- `.gm on` was active for the entire run (runner step 1) — a prime
  root-cause candidate for the player never swinging.

## D0 — instrument fixes (no combat behavior change)

1. combat-audit + runner `waitfor`: scope SwingReceive (and every player-
   swing assertion) to attacker == player GUID. Foreign swings get their own
   event name (ForeignSwingReceive).
2. Wire capture: add the complete 1.12 attack-error/State SMSG family to the
   combat channel — at minimum ATTACKSWING_NOTINRANGE, ATTACKSWING_BADFACING,
   ATTACKSWING_CANTATTACK, ATTACKSWING_DEADTARGET, ATTACKSWING_NOTSTANDING,
   plus ATTACKERSTATEUPDATE hit-result decode (verify each opcode
   name/value against the project's authoritative 1.12 opcode table; cite
   in the report — symbol-verification law applies).
3. Runner primitives from the A4 missing list: GM chat-response capture
   (server SMSG_MESSAGECHAT echoed into the runner log per step), spawn
   identity taken from the GM response rather than GUID sort, targeted
   kill/death confirmation (watch victim health/death descriptor fields).
4. Combat trace gains per-tick player→target distance and facing-delta
   columns (computed from the same state the renderer uses; report=act).

## D1 — CB1 root-cause matrix

One protocol, four variants, run-dated artifacts each; ONLY the named
variable changes between variants:

- V-A: exact repeat of CB1 (`.gm on`, spawn at feet) — reproduce baseline.
- V-B: `.gm off` before attacking (GM mode is the prime suspect: vmangos
  may suppress GM-initiated melee).
- V-C: `.gm off`, deliberately start 10 yd out, walk in while intent on —
  with error opcodes now captured, a holding server becomes visible.
- V-D: `.gm off`, in range, face 180° away, then turn in — same for facing.

Decision table (write results against it): player SwingReceive appears in
V-B ⇒ root cause = GM mode; error opcodes appear in V-C/V-D and swings
resume on eligibility ⇒ server behaves per vanilla and the client's only
gap is the un-displayed error events (already ruled: display is a later
order); no swings in any variant ⇒ escalate with the full wire log and stop.

## D2 — CB6 re-run with death confirmation

With chat-response capture and death-descriptor watching: `.gm off` variant,
attack, `.die`, confirm the victim actually died server-side (response +
health/death fields), then judge the client: intent must drop with cause
target-death. If the victim died and intent survived ⇒ confirmed client
defect (state the exact descriptor/flag the client missed). If `.die`
failed ⇒ record the server response verbatim and retry with the corrected
selection primitive.

## D3 — CB4 re-verification + CB7 re-audit

Re-run CB4 and CB7 with the D0 scope fix. CB4's machine-verified claim is
restored only if a PLAYER swing confirms the re-arm. Re-run combat-audit
over the archived 20260731-141730 traces as well — recount every finding
with correct scoping and report which prior rows change.

## D4 — HARD STOP

Packet: root-cause verdicts for CB1 and CB6 with the decision-table row
that proves each; re-scoped CB4/CB7 results; the full re-audited findings
table; and the queue of fix orders this implies (candidate list: intent
drop on target death; error-event display per the CB2/CB3 ruling; anything
the matrix exposes). No fixes in this order.

Standard four gates every boundary; one commit per stage; run-dated
artifacts + SHA-256 manifests; report appends with actual-versus-predicted.
