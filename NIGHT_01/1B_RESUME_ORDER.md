# NIGHT_01B — resume order (amends TIER0_MASTER.md; reopens the shelved items)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md`, PILOT_PROTOCOL standing laws,
and `TIER0_MASTER.md` as amended HERE. Where this file and Tier 0 differ,
this file wins. Ruling basis: Nico's pilot-reviewed rejection of the
NIGHT_01 mass-shelving outcome, 2026-08-01.

## Review verdict being enforced

NIGHT_01's evidence hygiene was clean, but 31 of 38 items were shelved as
BLOCKED, 27 of them in three batch triage passes, and the run ended ~3.5
hours into an 8–10 hour authorization. The dominant shelf reason —
"client protocol/state/UI/runner family absent" — is rejected as a
blocker class. **Absence of a client feature is never SHELVED-BLOCKED.
Building the missing family, instrument-first, is the item's work.** The
following amendments are law for this resume and all future NIGHT runs.

## Amendments (close the loopholes)

1. **Blocker definition (narrow).** SHELVED-BLOCKED is legal ONLY for
   external impossibilities: server unreachable/down, a tool or privilege
   denied after an actual attempt, missing credentials/inputs that only
   Nico holds (record exactly what), or a hard dependency on an item that
   itself ended SHELVED for an external reason. "Not built yet",
   "instrument absent", "runner absent", "gate absent" are WORK, never
   blockers.
2. **No batch shelving.** One item = one dedicated evidence artifact. A
   triage pass that disposes of multiple items in one artifact is void.
3. **Attempt floor.** Before ANY shelf of a buildable item you must have
   committed at least one build increment for it: the instrument/verdict
   channel, or the protocol runner script, or the UI pane skeleton wired
   to real packets — plus one live probe result (even a failing one).
   Shelves without a committed increment are rejected in pilot review.
4. **Time-pressure clause repealed.** The doc-2 triage sentence is
   revoked. Work items in order until the authorized window is actually
   exhausted or the list is done. Do not end the run early: if items
   remain OPEN and the session is healthy, the next item starts.
5. **Interface build order (dependency-corrected).** Build the gossip
   family FIRST (it is the front door to vendor/trainer/quest/taxi/
   innkeeper), then: 2-1 vendor → 2-2 trainer → 2-3 questing → 2-4 loot
   → 2-12 character/inventory cross-check → 2-5 bank → 2-6 mail → 2-7
   auction house → 2-8 professions → 2-9 guild → 2-10 tabard → 2-11
   talents. Each follows the doc-2 interface loop (a)–(e) with the
   attempt floor above; wire-correct acceptance is yours, perceptual
   verdicts to the queue with contact sheets.
6. **Spells reopened the same way.** 3-1's pre-send gate + named-result
   law + roster batch is exactly the kind of instrument NIGHT_01 was
   supposed to build. Build it, then 3-2..3-8 in order under the same
   floor.
7. **Item 1-1 reopened with an amended W1 (Q1 ruled).** The gdb
   entry-symbol plan is authorized and requires NO line table:
   auto-continue dprintf at the function ENTRY of each dispatch-chain
   candidate (WorldSocket read/queue handoff, WorldSession parse/queue,
   AllowPacket, HandleAttackSwingOpcode — log its GUID argument, and
   Unit::Attack), PLUS a return-value capture on Unit::Attack (bool) via
   finish-style catchpoint. Outcome classes: (a) no handler entry ⇒
   pre-handler discard proven, bracketed by the last entry that DID
   fire; (b) handler entry + Unit::Attack returns false ⇒ silent-
   predicate class proven, predicate NAMED ONLY IF register/argument
   evidence at entry supports it — otherwise report the class honestly;
   (c) success path. Same sudo/password law as SPEC-26 (this requires
   Nico present once, at run start); same ≤5 min, dprintf-only, sudo -K,
   health-probe hygiene. If Nico is not present at run start, this item
   alone (1-1) may wait shelved and the run proceeds to the build queue
   — the reverse of NIGHT_01's outcome, where the buildable queue waited
   on nothing.
8. **Combat matrix cells (Q3 interim).** The three Z0-gate-incompatible
   SPEC-21 cells stay shelved pending Nico; the five clean cells stand.
   Do not weaken the gate to fill cells (law 6).

## Statuses

All 31 SHELVED-BLOCKED items revert to OPEN under the new blocker
definition. The 2 SHELVED-RULING items (5-1, 5-2) and Q2/Q4/Q8/Q11/Q13
remain genuinely Nico's and stay shelved. CLOSED items stay closed.

Ledger, evidence, commit, and gate law unchanged: append-only PROGRESS.md
and RULINGS_QUEUE.md, one commit per root cause, four gates per boundary,
run-dated artifacts + SHA-256 manifests, never overwrite an evidence
path, DB read-only, F3–F6 excluded, GM test account only. End-of-run
packet per Tier 0, superseding the NIGHT_01 packet.
