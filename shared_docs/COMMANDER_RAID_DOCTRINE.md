# Commander raid doctrine and roadmaps

Added 2026-09-13 (owner request: "while we don't want encounter-specific logic, introduce basic high-level
instruction/routing from decades of knowledge, so that there's a roadmap on how to take packs, which order to
do a raid, etc."). This is the fourth kind of data next to derived data, global policy and surveys
(`COMMANDER_RAID_ARCHITECTURE.md`): **doctrine** (global, archetype-level raiding practice) and **roadmaps**
(per raid: boss order, entrance, skips - never tactics). Code still contains no entry, spell id, coordinate or
boss name; the compilers turn doctrine + database into definition data and route plans, and the executor reads
only the compiled definition.

## Files

| File | Kind | Content |
|---|---|---|
| `tools/encounter-content-audit/plans/raid-doctrine.json` | hand data, global | kill order by archetype, control claim order, pull rules, route derivation distances, pack/boss completion rules |
| `tools/encounter-content-audit/plans/roadmaps/<map>.json` | hand data, per raid | `order` (encounter ids of `plans/<raid>.json`), `entrance` (measured), `skip` (unit id + reason), notes |
| `tools/encounter-content-audit/archetypes.py` | code | classifies a creature template from its loaded spell list / template slots / EventAI casts / registered script casts and unit class into summoner, healer, caster, ranged, melee; `kill_order()` sorts entries by doctrine rank, then weakest first |
| `tools/encounter-content-audit/roadmap_compiler.py`, `compile_roadmap.py` | code | boss order -> legs of ordered pull units (spine over floor evidence, corridor / neighbour / footprint selection, patrols first), gates from the instance script, progress overlay, Markdown |
| `core/commander-raid/compiled-roadmaps/<map>.json|.md|-derivation.json` | derived | the compiled roadmap (input hashes recorded); `<map>-progress.json` is the session-state overlay (bosses/units done that no clear-state file carries) |
| `check_archetypes.py`, `check_roadmap.py` | tests | synthetic fixtures for the classifier, the ordering and the leg derivation |

## What the doctrine says (and what enforces it)

| Rule | Data | Enforced by |
|---|---|---|
| Kill summoners, then healers, then casters, then ranged, then melee; weakest first within a class; the pulled unit is just a member | `killOrder` | pack and boss compilers write `requiredAdds` in that order (`fieldOrigins['/requiredAdds'] = source-or-database-ordered-by-doctrine`); **executor v28** ranks focus candidates by their entry's index in `requiredAdds` before health/distance |
| Control what dies last, never the current kill target | `control.claimOrder` | **executor v28**: native crowd control claims from the end of the kill order (GUID order within a rank); the boss / last uncontrolled add still stays active |
| One puller, one group; the pack comes to the raid | `pull` | client puller + executor pull discipline (unchanged) |
| Patrols before the packs they walk past; no fight with an idle neighbour inside chainYards | `pull.patrolsBeforeStationary`, `pull.chainYards` | roadmap ordering and neighbour closure; executor patrol hold / route lanes (v10, v27) and idle-hostile hazards (v21/v25) at run time |
| Packs: two consecutive kills = done, done packs stay done; bosses: frozen 10-pull >= 8/10 | `route.packKillsToFinish`, `route.bossAcceptance` | `clear_route.py` / `raid_regression.py` (owner rules 2026-09-13); the roadmap prints them per unit / leg; a unit whose entries stop respawning once a fallen encounter is DONE is finished by one kill |
| Which packs stand between two bosses, in what order | `route.linkYards/linkSlope/corridorHalfWidthYards/footprintYards` | `roadmap_compiler.py` (see below) |

Advisory (in the doctrine notes, not mechanised): line-of-sight pulls for caster packs, "casters get controlled, melee get tanked" beyond what the claim order gives, pull-method pinning (body vs ranged pull - the client decides today, STATE item 97).

## How archetypes are derived

Per creature template, from the frozen snapshot only:

- roots = the template's `spell_list_id` list (else its four template slots) + EventAI cast actions (command 15) + the casts of its registered `ScriptedAI` (`DoCastSpellIfCan` / `CastSpell` arguments resolved through the file's enums - the Firewalker's list carries only the self "casting" aura 19636 whose trigger effect the server removed; its script casts the 2.8k Fire Blossom 19637 on random members);
- `healer`: heal effect (10/67/75) or periodic heal aura (8) on a friendly target other than self;
- `summoner`: a summon effect (28/41/42/56/73/74/87-90/93/97/112) or an EventAI temp-summon action (command 10);
- `caster`: magic-class direct/periodic damage on an enemy target at >= `archetypes.rangedYards` (20), including through a self periodic-trigger aura's child; a mage-class template counts as caster evidence;
- `ranged`: physical-class ranged damage at >= 20 yd; charges are melee openers, not ranged attacks;
- `melee`: everything else. Tie-break within a class: lower `health_multiplier` first, then entry.

Molten Core result: Firelord = summoner (Summon Lava Spawn / Split), Flamewaker Priest = healer (Dark Mending), Firewalker / Firesworn / Flamewaker Healer / Flamewaker Elite / Lava Spawn = caster, everything else melee. Every accepted boss definition is byte-identical (one add entry each); the promoted packs whose `requiredAdds` order changed are the Firelord + Lava Annihilator pairs (56720; 56784/56800 unsurveyed). The behavioural change is executor v28: a Firewalker pack is now burned Firewalker (caster) -> Flameguard -> Lava Elementals / Reaver instead of "whatever is already lowest" (the Firewalker used to die last, STATE items 115-118), and the warlocks' Banish goes on the Lava Elementals first.

## How a roadmap is compiled

1. Pull units = the pack compiler's units (spawn groups, lone spawns, assistance-radius merges; boss units excluded).
2. Floor graph = every spawn and patrol waypoint on the map plus the declared entrance and scripted summon points; two nodes within `linkYards` (60) whose grade is under `linkSlope` (0.5) are linked; consecutive waypoints of one patroller always are; a declared point nobody reaches is joined to its nearest node.
3. Leg spine = the shortest path from the previous fight to this boss (surveyed tank anchor, else spawn home, else the script's `SummonCreature` position). Disconnected = straight line, flagged.
4. Units on the leg: `corridor` (a member or route point within `corridorHalfWidthYards` (35) of the spine and within 12 yd of its height), `footprint` (within `footprintYards` (30) of the boss fight: survey anchors, home, and for a surveyed patrolling boss the part of its lane within the policy's `patrolEngageYards` of the anchor), `neighbour` (within `chainYards` (35) of a selected unit, transitively, bounded by twice the corridor width). First leg wins; the roadmap's `skip` list marks a unit but keeps it visible.
5. Order = distance along the spine; a patroller is placed `chainYards` earlier than a stationary unit at the same distance (it is met where its lane first touches the corridor).
6. Gates = rune objects whose `GOHello` requires an encounter DONE (all seven for the Majordomo summon), and a boss summoned by another encounter's script (Ragnaros by Majordomo). Rune positions come from the object table.
7. Progress = `--progress` file + `--clear-state` files: units done, bosses done, pending lists per leg.

Molten Core compiled 2026-09-13 (`core/commander-raid/compiled-roadmaps/molten-core.md`): 76 route units in 10 legs, 37 pending after the sep12/13 sessions, 12 off-route. Leg 5 (Baron Geddon) derives exactly the order the live sessions converged on: Ancient Core Hound patrol 56856 -> Lava Surger 56848 -> 91286 -> 91261 -> 91277 -> Geddon; leg 6 = 91290 + the Ancient Core Hound 56860 -> Shazzrah. Known differences from practice: the compiler keeps only the two hound packs on Lucifron's corridor before him and the other three before Magmadar (the sessions cleared all five first - both are valid); the nine entrance packs of leg 1 were never fought because the raid was staged inside by checkpoint (GM staging is allowed), so they show as pending.

## Commands

```bash
python tools/encounter-content-audit/compile_roadmap.py core/commander-raid/evidence/content-snapshot-sep12.json tools/encounter-content-audit/plans/molten-core.json tools/encounter-content-audit/plans/roadmaps/molten-core.json core/commander-raid/compiled-roadmaps --progress core/commander-raid/compiled-roadmaps/molten-core-progress.json --clear-state scratch/onyxia-live/sep13-*-evidence/clear-state.json
```

```bash
python tools/encounter-content-audit/check_archetypes.py && python tools/encounter-content-audit/check_roadmap.py
```

The pack and boss compilers pick the doctrine up automatically (`compile_packs.py`, `compile_raid.py`); `--doctrine` overrides the path on `compile_roadmap.py`.

## Rules for editing

- Doctrine changes change every raid; they are hand data of the same standing as `compiler-policy.json` (owner decision, measured batch).
- A roadmap may carry the boss order, the entrance, skips with reasons and notes. Anything that reads like "pull X here" or names a pack's tactic belongs nowhere: fix the doctrine or the survey.
- Kill-order/claim-order evidence is in `core/commander-raid/pack-definition-derivation.json` (`packs.<id>.killOrder`) and `encounter-definition-derivation.json` (`encounters.<id>.killOrder`).

## Addendum 2026-09-13 20:30Z: formation stand-off coverage

`definition_compiler.py` native coverage `formation-standoff` (pass `--native-coverage ... formation-standoff`): a
spells-only cast footprint prepared in at most `STANDOFF_MAX_CAST_MS` (1500 ms) - an instant or near-instant burst
around the caster that nobody already inside can leave - is covered by the survey geometry when every team anchor
stands at least the burst radius from the tank anchor. Healers and ranged never take it; the melee absorb it under
healing triage (the same claim the objective review makes for every caster-centred area effect). The projection
verifies the distances and refuses a survey whose teams stand inside the radius. First use: Shazzrah's Arcane
Explosion (19712, 30 yd, 0.5 s) with the tank anchor (612,-786) and teams (640,-770)/(650,-790) at 32/38 yd. This is
raiding doctrine ("ranged stay out of the boss's AoE range") expressed as a survey rule; no executor change.
