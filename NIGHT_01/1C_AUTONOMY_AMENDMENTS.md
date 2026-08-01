# NIGHT_01C — autonomy amendments (binding; supplements TIER0_MASTER.md and 1B_RESUME_ORDER.md)

Ruling basis: Nico, 2026-08-01. Where this file conflicts with any earlier
NIGHT doc, this file wins. Standing laws (SPEC_TOOLKIT_00_ORDERS.md,
PILOT_PROTOCOL 1–12) remain in force except as explicitly widened by
Amendment 11.

## Amendment 9 — continuous execution (stopping is the violation)

Prior sessions self-terminated after 20–40 minutes despite demonstrated
2–4 hour capacity. Rejected. Rules:

1. Mid-run status goes ONLY to `PROGRESS.md` / `REPORT.md` / evidence
   packets. Producing a chat checkpoint before the end-of-run packet is a
   protocol violation unless it reports a true external blocker
   (Amendment 1 definition in 1B: server down, privilege denied after an
   actual attempt, inputs only Nico holds).
2. Closing an item never ends execution: the anti-lost loop's last step —
   take the first OPEN item — happens immediately, same working turn,
   until the list is exhausted or the session dies externally.
3. There is NOTHING to wait for. Item 1-1 is closed-gate by Nico's
   ruling; no future item may pause for a password, an approval, or a
   ruling — that is what SHELVED-RULING queue entries are for.
4. After an external relaunch, a user message of exactly `continue`
   means: read `PROGRESS.md` tail, resume at the first OPEN item, no
   summary, no re-review.

## Amendment 10 — cross-opcode server silence is ONE finding

Register `F-SILENT-INTERACT` once in REPORT.md and the rulings queue:
CMSG_ATTACKSWING (proven on-wire + ACKed, SPEC-24), CMSG_GOSSIP_HELLO and
CMSG_LIST_INVENTORY (flushed, silent) receive no server response while
chat, teleport, spawn, and death flow normally. Candidates per the frozen
X3/Z3 table; the silent `!IsInWorld()` STATUS_LOGGEDIN skip and
`AllowPacket` are the common-cause candidates consistent with all three.
Any later live leg that gets no server response is recorded as
`BLOCKED-BY:F-SILENT-INTERACT` — one verdict row, no per-item
re-diagnosis — and the item still closes on its client build increment
plus byte-checked wire evidence. Positive-control law stands: every live
leg carries a delivered `.gps` control so "opcode silent" stays
distinguishable from "session dead".

## Amendment 11 — self-provisioning mandate (the autonomy widening)

The agent provisions EVERYTHING it needs itself, on the dedicated GM test
account, and never queues a provisioning ask:

1. Characters: create every race/class combination the item under test
   requires (character-create flow or GM command, whichever is scripted
   faster; record which). Naming: `NB<class><race>` style, recorded in a
   roster CSV committed with the run.
2. GM self-provisioning is AUTHORIZED on the test account's own
   characters: level to 60 (`.character level` / `.levelup`), learn full
   class spellbooks and talents (`.learn all_myclass`, `.learn
   all_myspells`, `.learn all_mytalents` — verify each command exists via
   `.help`/read-only source before use and record the actual syntax),
   money, reagents, equipment and ammo via `.additem`, stat/speed resets
   via `.modify` where a test requires it, `.cooldown` clears between
   sweep cells, `.tele` for placement. GM mode OFF during any leg whose
   acceptance depends on GM-off (combat/attackable checks), per the
   accepted X1/Z0 pattern.
3. Widened server-state class, explicitly: game-mediated state scoped to
   the test account's own characters and disposable spawned creatures is
   allowed and does not require cleanup beyond despawning spawned mobs.
   STILL FORBIDDEN: direct DB writes (DB stays read-only), server
   code/config/restarts, other accounts' state, world/persistent
   settings, F3–F6.
4. If a needed GM command is refused for the test account's GM level,
   record the exact refusal once, file ONE queue entry listing every
   command that needs a higher grant, and work around it where possible
   (e.g., quest-based or trainer-based acquisition) instead of stopping.

## Amendment 12 — spell validation is the flagship of this run

Item 3-x acceptance, per class on the self-provisioned roster:

- Full-spellbook sweep, batch CSV, STRING verdict columns (spell id,
  name, school, cast type, result enum, animation state, effect check).
- Per spell: cast standing (animation-state rows from the renderer's own
  mixer state + batched screenshots); cast while moving (instant = must
  cast, cast-time = must interrupt/refuse per rule matrix — verdict per
  cell); channeled start/tick/stop; GCD, cooldown, resource deltas vs
  server packets.
- "Does what it says": mechanical effect verification against read-only
  DBC/DB expectations — aura applied (descriptor delta), damage/heal
  landed (health delta on self or spawned target), summon/pet appeared
  (object create), teleport moved (position delta). Hostile-target legs
  that hit server silence are BLOCKED-BY:F-SILENT-INTERACT rows;
  self/friendly legs remain fully testable and are the priority.
- Contact sheets per school/class batched for Nico's morning perceptual
  pass; no perceptual claim is the agent's.

## Priorities for the fresh session

1. Spells (doc 3, all items, under Amendment 12) — flagship.
2. Remaining interface families (doc 2 order from 1B) — build + wire
   evidence; live legs marked BLOCKED-BY where silent.
3. World interactions (doc 4), then housekeeping remainder (doc 5).
Item 1-1 and all SHELVED-RULING items stay shelved. End-of-run packet per
Tier 0 when the list is exhausted — that packet is the ONLY chat message.
