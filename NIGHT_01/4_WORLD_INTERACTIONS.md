# NIGHT_01 tier-1 doc 4 — world interactions

Parent: `TIER0_MASTER.md` item 0-4. All Tier-0 rules apply. F3–F6 (speed
opcodes, step/slope, swim, capsule) remain EXCLUDED by standing ruling —
if an item here seems to need them, shelve that sub-item, don't start them.

- **4-1 Gossip.** Gossip menu decode for every NPC flag class in the
  corrected creature deck (vendor/trainer/quest/flightmaster/innkeeper/
  banker/auctioneer), option routing to the right interface (cross-links
  to doc 2 items), text display vs DB read-only.
- **4-2 Game objects.** Interact flows: chests (loot), doors/levers
  (use), herbs/veins IF the profession prerequisite from 2-8 landed —
  else shelve those two sub-items; spell-focus objects presence check.
- **4-3 Resting/XP.** Rest state enter/leave (inn/city), rested bonus
  accumulation display, XP gain verification extended from kill XP (1-2 /
  2-3) to quest XP; level-up flow (stats/talent point/plate updates).
- **4-4 Death/rez completion.** Extend the accepted 13/13 death proof:
  corpse run, spirit healer rez (sickness/durability display), corpse
  reclaim timing, resurrect-request dialog (GM or cross-char if
  feasible — record which path was testable).
- **4-5 Innkeeper/hearthstone.** Bind flow, confirmation, hearth cast
  (ties to 3-1/3-2), cooldown display.
- **4-6 Flightmaster.** Taxi map display, node discovery state, flight
  purchase and the flight path itself (client control lockout during
  taxi, arrival handoff). Movement-adjacent but NOT F3–F6; if any part
  crosses into F3–F6 territory, shelve that part explicitly.
- **4-7 Environment audit sweeps.** Batch re-run of the existing
  movement/portal/instance/water audit instruments over the current
  build as a regression pass (no new movement features): dual-band
  audit, portal traversal set, instance entry set. Deviations =
  CLOSED-FINDING + queue.
