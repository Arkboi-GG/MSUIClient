# SPEC_TOOLKIT_21 — attack preconditions, GM-state truth, then server tracing (HARD STOP)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md`, PILOT_PROTOCOL standing laws.
Diagnosis only; combat behavior, error display, F3–F6 untouched.

Pilot reframe: the run-17-era build failing TODAY against the archived GUID
proves the WORLD changed, not the client. And one variable flipped between
then and now that no run has isolated: GM commands only started actually
EXECUTING at SPEC-18 G0 — in runs 16/17 the bootstrap's `.gm on` was sent
over the broken chat path and never took effect. Today's sessions may be
the first that truly run in GM mode, and vmangos refuses combat initiation
for GM-mode characters via exactly the kind of silent path H0 catalogued.
Second unisolated variable: the archived control was addressed by stored
GUID bytes blind — nobody proved the character was standing where that
kobold is, or that it is alive and in the client's object store now. Server
tracing (your ask) is authorized here, but only AFTER the cheap layer.

## P0 — precondition truth at send time

At the arena vantage, before any swing, capture as run-dated artifacts:

1. Character position from the movement trace (must be the arena; if the
   session bootstrap leaves the character elsewhere after teleport
   proofing, return first and note it).
2. GM state as the SERVER reports it: `.gm` status response verbatim, and
   both `.gm on`/`.gm off` transitions confirmed by response text. Check
   whether login defaults GM mode ON for this rank-6 account (vmangos
   GM.LoginState config — read the config read-only and cite it).
3. Target truth from the client's OWN object store at send time: is the
   target GUID present, visible, alive (health/deathstate descriptors),
   and what are its UNIT_FLAG/dynamic-flag/faction values (decoded)?
   An absent-from-store target makes a silent server return EXPECTED
   (lookup miss) — that cell teaches nothing about combat law.

## P1 — precondition-enforced matrix

Every cell requires: position proven, GM state proven by response, target
in-store + alive + within measured 3 yd. Cells, one variable each:

- A: GM OFF (confirmed), fresh spawn — the clean case.
- B: GM ON (confirmed), same spawn — isolates the GM-mode effect.
- C: GM OFF, wild creature re-anchored by walking/teleporting to it and
  reading it from the object store (not archived bytes).

Land each cell on an H0 law row with full wire hex. Prediction to test:
A and C produce ATTACKSTART + swings; B is silently refused. If prediction
holds, runs 16–20's entire no-swing history collapses to "GM mode, active
only since chat was fixed, plus blind-GUID targeting" — write that
reconciliation explicitly against each prior run.

## P2 — server-side observation (authorized, bounded)

Only if any precondition-proven cell still returns silence: raise mangosd
logging via its console/config (packet/handler log level), capture the
world log around a repeated swing, and read what the handler saw. Config
changes are documented in SETUP.md and REVERTED after capture; no server
code changes; no DB writes. Cite the log lines against H0's paths.

## P3 — diagnosis completion

With the acceptance cause named: complete SPEC-19 T3 in full (V-A..V-D,
confirmed-death CB6, player-GUID CB4/CB5/CB7 with swing cadence vs weapon
speed, IntentOff hygiene determination). Bootstrap must leave GM mode in
whatever state P1 proved combat requires, documented.

## P4 — HARD STOP

Final packet: acceptance root cause with the P1 matrix; the prior-runs
reconciliation; completed CB findings table; final combat fix-order queue.

One commit per stage; standard four gates; run-dated artifacts + SHA-256
manifests; actual-versus-predicted per stage.
