# NIGHT_03 tier-1 doc 1 — kill the silence, then build the roster

Parent: `TIER0_MASTER.md` item 0-1. All Tier-0 rules apply. Attempt floor:
each item needs a committed increment + one real live output before any shelf.

## Why this is first (R3)

`F-SILENT-INTERACT` gates roughly every hostile leg of the spell matrix. It is
NOT universal: NIGHT_01C proved Drain Life's channel and Fortitude's aura got
real server responses, while a GM-off rank-1 Battle Shout flushed the exact
correct body `11 1A 00 00 00 00`, received no spell response, and applied no
aura — with the adjacent `.gps` control delivered. The silence is
path-specific. Find the path.

- **1-1 Server discrimination (item 1-1 reopened, unblocked).** The gdb
  entry-symbol plan from `NIGHT_01/1B` §7 was shelved for ONE reason: it
  needed Nico present at run start for sudo. The account in the launch
  directive is full GM/admin. Re-attempt it. No line table required:
  auto-continue `dprintf` at the function ENTRY of each dispatch-chain
  candidate (WorldSocket read/queue handoff, WorldSession parse/queue,
  `AllowPacket`, `HandleAttackSwingOpcode` logging its GUID argument,
  `Unit::Attack`), plus a return-value capture on `Unit::Attack` (bool) via a
  finish-style catchpoint. Outcome classes: (a) no handler entry ⇒
  pre-handler discard proven, bracketed by the last entry that DID fire;
  (b) handler entry + `Unit::Attack` returns false ⇒ silent-predicate class
  proven, predicate NAMED ONLY IF register/argument evidence at entry
  supports it, otherwise report the class honestly; (c) success path.
  Hygiene unchanged: ≤5 min, `dprintf`-only, `sudo -K` after, health probe
  before and after.
- **1-2 Spell-path discrimination.** Repeat the same instrument for the SPELL
  dispatch chain (`CMSG_CAST_SPELL` handler entry, cast-precondition
  predicate, `Spell::prepare` / `Spell::cast` entry). NIGHT_01C's evidence
  says some spell paths answer and some do not; determine which predicate
  separates Drain Life (answered) from Battle Shout (silent). This is the
  single highest-value diagnostic in the run.
- **1-3 Verdict + fix or file.** If the cause is client-side (malformed
  field, wrong precondition, missing session state), FIX IT — that is work,
  not a blocker. If it is server-side, file ONE consolidated queue entry with
  the proven class and bracketing evidence, keep `BLOCKED-BY:F-SILENT-INTERACT`
  as the single registered finding (Amendment 10), and proceed. Do not
  re-diagnose it per spell later.

## Roster (Amendment 11 + R4)

- **1-4 Deletion fence, implemented before any delete.** Write the fence as
  CODE or a scripted guard, not as an intention: a delete is refused unless
  the target name matches the `NB*` prefix AND appears in the run's roster
  CSV as agent-created. Commit the guard and one refused-delete test showing
  it blocks a non-`NB` name. **A pre-existing character is never deleted,
  renamed, stripped, or logged in to.** This is the one action that ends
  the run.
- **1-5 Roster provisioning.** Create the class representatives the matrix
  needs (doc 3 order), working within the account's character cap by
  creating, testing, and deleting its own characters in waves. Record every
  wave in an append-only roster CSV: name, race, class, created-at,
  deleted-at, wave, and the exact GM commands used. Naming `NB<class><race>`.
- **1-6 Self-provisioning verification.** For each character: level to 60,
  learn the full untalented spellbook (R5), money, reagents, ammo, and
  equipment via `.additem`, `.cooldown` clears between cells, `.tele` for
  placement. **Verify each GM command exists via `.help` or read-only source
  before use and record the ACTUAL syntax** — NIGHT_02 proved VMaNGOS syntax
  differs from assumption. GM mode OFF for any leg whose acceptance depends
  on GM-off (per the accepted X1/Z0 pattern); record the on/off state as a
  column on every verdict row.
- **1-7 Refusal handling.** If a needed GM command is refused at this
  account's GM level, record the exact refusal once, file ONE queue entry
  listing every command needing a higher grant, and work around it. Do not
  stop.
