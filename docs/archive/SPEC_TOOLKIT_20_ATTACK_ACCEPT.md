# SPEC_TOOLKIT_20 — attack-acceptance diagnosis: GUID identity vs framing (HARD STOP)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md`, PILOT_PROTOCOL standing laws.
Diagnosis + instrument/runner fixes only; combat behavior, error-text
display, and F3–F6 remain untouched.

Pilot framing: CMSG_ATTACKSWING framing is the WEAK hypothesis — the same
send code received SMSG_ATTACKSTART against wild creatures in runs 16/17.
The change since is the target: response-derived spawn identity. A server
that finds no victim for the sent GUID returns silently; a server that
rejects a valid-but-unattackable victim sends an error we now capture.
Discriminate identity → validity → framing, in that order, with a wild-mob
positive control anchoring every run.

## H0 — server-side silent paths (read-only)

Read vmangos `HandleAttackSwingOpcode` and `HandleSetSelectionOpcode`
(cite file:line, official source as staged/on-disk): enumerate every path
that returns WITHOUT sending anything to the client, and every path that
sends each error. This is the decision table's law column.

## H1 — GUID identity audit

Spawn one creature via the corrected deck. Record three identities:
(a) the server's command-response identity, (b) the GUID the client's own
object store assigned it from SMSG_UPDATE_OBJECT when it appeared,
(c) the GUID bytes actually sent in CMSG_SETSELECTION and CMSG_ATTACKSWING
(full hex). Compare all three; decode the GUID fields (high/entry/low) for
each. Any mismatch is a finding with the exact bytes.

## H2 — three-way attack matrix (one variable per cell)

1. POSITIVE CONTROL: attack a wild pre-existing creature (GUID from the
   client object store, the run-16 method). Expected: SMSG_ATTACKSTART as
   in runs 16/17. If this now fails too, the regression is client-side
   since run 17 — suspect the T0 teleport change's touch on movement/net
   state; bisect and report, do not fix in this order.
2. Spawned creature, attacked via the OBJECT-STORE GUID.
3. Spawned creature, attacked via the RESPONSE-DERIVED GUID (only if it
   differs from 2).

Decision table: control PASS + object-store PASS + response FAIL ⇒ runner
identity defect (fix authorized in H3). Control PASS + both spawn cells
FAIL ⇒ spawn validity issue: read the spawn's faction/flags from client
descriptors and the creature template read-only (DB or source), match
against H0's reject paths. Control FAIL ⇒ client regression path above.
All cells must include full wire hex and the H0 law row they land on.

## H3 — fix + diagnosis completion

Fix the identified runner/instrument defect (identity construction, or
whatever H2 named — production combat code stays untouched unless the
finding IS a production defect, in which case HARD STOP with the evidence
and wait for an explicit order). Then re-run SPEC-19 T3 in full: V-A..V-D
matrix, confirmed-death CB6, player-GUID-only CB4/CB5/CB7 with swing
cadence, IntentOff hygiene determination.

## H4 — HARD STOP

Final packet: identity/acceptance root cause with bytes; completed CB
findings table; final combat fix-order queue. No fixes beyond H3's
authorized scope.

One commit per stage; standard four gates; run-dated artifacts + SHA-256
manifests; actual-versus-predicted blocks per stage.
