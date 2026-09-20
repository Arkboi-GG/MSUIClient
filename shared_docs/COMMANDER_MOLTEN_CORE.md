# Molten Core content and execution review

Current status, acceptance rule, hashes and next step live in `COMMANDER_RAID_STATE.md`; the design is in `COMMANDER_RAID_ARCHITECTURE.md`.

Generated from the configured Core checkout, world database and DBC files. **Research only: no Molten Core pull or clear has been verified.**

This file describes the intended behavior and the remaining executor work. It is not a deployable boss plan.

Content build 5875, patch ceiling 10; Core fb207e67b6f4f34b4e27f9ecc48e0806bc7c057c. Snapshot 2026-09-07T13:35:24.084215+00:00.

## Progression

- Use the same Testwar GUID 787 and 39 bots. Complete source/capability checks and safe room surveys before each normal-game Apply/Arm. No administrative boss kills count.
- Suggested travel order: Lucifron, Magmadar, Gehennas, Garr, Baron Geddon, Shazzrah, Sulfuron, Golemagg, Majordomo, Ragnaros. Confirm patrols, paths and live instance state; this is a proposed route, not verified navigation.
- Seven boss-linked runes are 176951 Sulfuron, 176952 Geddon, 176953 Shazzrah, 176954 Golemagg, 176955 Garr, 176956 Magmadar and 176957 Gehennas. Lucifron has no rune.
- GOHello_go_rune_MC requires its associated boss DONE and records rune DONE. All seven rune states DONE summon Majordomo at the scripted location. Loaded completed rune states remove their fire via the instance update; this is not evidence that fresh runes douse automatically.
- Verify the normal item/GO interaction requirements and proximity using the acting body. Do not replace rune progression with instance-state writes or an administrative summon.
- Majordomo surrenders when eight adds are defeated, then relocates and offers the Ragnaros event. The normal gossip/script event summons Ragnaros; his scripted killing of Majordomo is part of progression, not a player boss kill.
- Ragnaros has a two-hour summoned lifetime for this patch. Preserve the same instance and prior progression. The authorized temporary checkpoint may reset the current encounter throughout its frozen ten-pull batch. After an accepted8/10 result, normal loot requires the current actual reviewed corpse; there is no three-win streak requirement. Do not restore a whole database/worldstate.

## Shared positioning and role readiness

- Resolve the current spell_template through spell_mod and spell_effect_mod before building trigger chains or geometry. Preserve removed effects and changed child spells; raw DBC/template records alone are not the loaded spell.
- Derive exclusion regions from every active effect, both implicit target columns, cone direction, trigger children and C++ overrides. Spell range is not effect radius, and boss combat reach is not the model bounding radius.
- Intersect safe regions with measured walkable floor, complete paths, line of sight, healer range, caster minimum/maximum range and room boundaries. Optimize travel time and angular/radial margin, then re-evaluate when the boss moves or turns.
- Choose melee flank stations inside verified attack reach but outside front/rear cones. Wall support reduces displacement only where collision and actual knockback trajectory have been verified; direct-victim knockbacks remain dangerous.
- For a timed hazard, require path length / measured movement speed plus latency and reaction margin to fit the remaining warning. Apply slows, cast interruption, fear and collisions to that budget. A safe destination with an unsafe path is insufficient.
- Survey each room before arming. Keep Hold and pets passive outside scripted aggro range until every assigned body has a reachable station. Do not copy Onyxia coordinates into another room.

**Protection warriors**: Defensive Stance, shield, level-appropriate weapon/defense skill, Shield Slam, Revenge, Sunder Armor, Shield Block and defensives must be usable from actual learned spells, equipment, rage and cooldowns. Build a threat lead before damage begins; maintain assigned targets and facing. Do not repeatedly taunt an immune boss. After a knockback or reset, reacquire with ordinary threat actions and a designated backup while damage and pets wait.

**Holy priests**: Use efficient heal ranks against predicted incoming damage; reserve mana for assigned tanks and time-critical dispels. Coordinate rather than cast six redundant heals on the same deficit. Dwarf Fear Ward and Desperate Prayer were verified for the existing six priests. Fear Ward needs an assigned tank, coverage check and backup; it is not a generic raid-wide fear solution.

**Holy paladins**: Use Holy Light/Flash of Light according to deficit and cast time. Coordinate Cleanse and blessing assignments. Verify reagents, mana and spell ranks; never classify Recklessness as a defensive cooldown.

**Combat rogues**: Maintain rear-side melee reach without chasing through cones. Use Sinister Strike and finishers with energy/combo points; assign Kick by enemy GUID and reserve energy for deadlines. Stop attacks for crowd control, reflect or threat; Feint/Vanish require working state and cooldowns. Audit weapons and poison applications instead of treating level 60 as complete readiness.

**Frost mages**: Use Frostbolt against fire-immune enemies; maintain range and line of sight. Assign curse patients and Counterspell targets with backups, then resume damage. Polymorph requires an eligible, nonimmune enemy and an exclusive target reservation; stop pets, cleave and incidental damage on controlled targets.

**Destruction warlocks**: Choose Shadow Bolt when fire damage is immune. Coordinate curses, threat, Life Tap and healing load. Assign Banish targets individually, track expiry/resistance and provide recovery when a control breaks. Verify pet spellbook/autocast and shards; a level-60 pet alone is not a readiness certificate.

**Marksmanship hunters**: Verify compatible equipped weapon/ammunition, current stack counts, ranged skill, pet skills, range and line of sight. Reserve Tranquilizing Shot for Magmadar Frenzy with a backup hunter and observed success. Feign Death and pet recall must actually stop threat generation when required; an ordinary ranged damage spell is not a defensive cooldown.

## Lucifron

Entry 12118; completion: boss death plus encounter completion. Status: research only.

Main tank holds Lucifron facing away. Two add tanks acquire the two Flamewaker Protectors 12119; focus one protector at a time with assigned interrupts and controlled-player recovery.

**Mechanics and responses**

- Impending Doom 19702 is Magic with a ten-second duration and a 45-yard effect radius in this database; prioritize dispelling it before expiry.
- Lucifron's Curse 19703 is a Curse with a 45-yard radius. Assign mage decurses, especially tanks and healers, independently of priest/paladin Magic dispels.
- Protector Dominate Mind 20604 is a two-second cast, fifteen-second Magic control. Interrupt the protector or remove the control using a valid hostile/friendly dispel path; do not kill the controlled raid member.

**Formation constraints**

- Separate protector cleaves from the raid; keep add tanks within assigned healer range.
- Do not rely on a 40-yard boundary: both boss raid debuffs extend to 45 in this snapshot.

**Required executor work**: enemy GUID assignments, interrupt reservations, dispel type and urgency, controlled ally handling.

**Acceptance observations**

- All three hostile objectives accounted for by GUID; no protector remains active.
- Doom expiries, dispel latency, interrupted mind controls and deaths recorded; normal boss death verified.

**Source anchors**

- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_lucifron.cpp:18`: `SpellImpendingDoom`.
- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_lucifron.cpp:40`: `m_Events.Reset()`.
- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_lucifron.cpp:46`: `TYPE_LUCIFRON`.

## Magmadar

Entry 11982; completion: boss death plus encounter completion. Status: research only.

Tank Magmadar facing away with fear recovery prepared. Assign hunters in a Tranquilizing Shot rotation after verifying the spell is actually learned and usable.

**Mechanics and responses**

- Database spell list 119820 supplies Frenzy 19451, Panic 19408 and two Lava Bomb variants 19411/20474; there is no dedicated boss C++ file in this checkout.
- Panic is a 45-yard, eight-second fear here. Frenzy lasts eight seconds and also triggers Lava Breath 19272. Tranq success must be verified by aura removal, not a sent cast.
- Lava Bomb target selection differs between list slots. Resolve spawned ground effects and evacuate their actual location; a missing fixed spell destination does not mean there is no hazard.

**Formation constraints**

- Place healers/casters outside Panic only where their actual healing/damage range still reaches their patient/target. Otherwise assign fear mitigation and recovery.
- Reserve bomb escape lanes; turn the boss away from the raid and keep tanks reachable during fear.

**Required executor work**: offensive enrage dispel reservations, targeted ground hazard tracking, fear coverage and recovery.

**Acceptance observations**

- Each Frenzy has a successful Tranq or a documented failure reason.
- Observe Panic recovery, bomb impacts and ground hazard escape; require ordinary death and instance completion.

**Source anchors**

- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/instance_molten_core.cpp:209`: `TYPE_MAGMADAR`.
- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/instance_molten_core.cpp:138`: `RUNE_MAGMADAR`.

## Gehennas

Entry 12259; completion: boss death plus encounter completion. Status: research only.

Main tank holds Gehennas; add tanks separate the two Flamewakers 11661. Kill the adds while curse removal protects the tank healers.

**Mechanics and responses**

- Gehennas' Curse 19716 reduces healing; give cursed tanks priority and keep mage decurse assignments active while dealing damage.
- Rain of Fire 19717 persists six seconds in a ten-yard area around the selected destination. Move immediately using a safe path; do not resume at a still-active impact point.
- Random Shadow Bolt selects a raid victim; healer triage must cover raid damage without abandoning tanks.

**Formation constraints**

- Separate add stun/cleave space and give every group multiple rain escape options.
- Keep each new safe station in healer range and outside the full ten-yard rain radius plus margin.

**Required executor work**: enemy GUID assignments, targeted persistent ground hazards, dispel priority.

**Acceptance observations**

- Rain exposure and relocation latency recorded for every affected member.
- Curse removal restores effective tank healing; both adds and boss accounted for.

**Source anchors**

- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_gehennas.cpp:30`: `SPELL_RAIN_OF_FIRE`.
- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_gehennas.cpp:58`: `TYPE_GEHENNAS`.
- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_gehennas.cpp:82`: `SelectAttackingTarget`.

## Garr

Entry 12057; completion: boss death plus encounter completion. Status: research only.

Inventory all eight Firesworn 12099 by GUID. Assign the four warlocks distinct Banish targets and the two add tanks the remaining adds; kill uncontrolled adds deliberately before releasing controlled ones.

**Mechanics and responses**

- Antimagic Pulse 19492 strips buffs in 45 yards; do not assume pre-pull buffs remain throughout the fight.
- Magma Shackles 19496 slows movement. Include the slow in eruption escape time and prioritize a dispel when needed.
- Normal Firesworn Eruption 19497 reaches 15 yards; forced Massive Eruption 20483 reaches 30. Garr gains strength as adds die, and the source starts forced eruptions at six minutes.
- Banish reservations must survive misses, breaks and expiry. Do not let ordinary damage, pets or target selection break assignments.
- Loaded spell_effect_mod increases normal Eruption 19497 base points from 1849 to 3500 and die sides from 301 to 800; Massive Eruption 20483 also has modified damage dice. Derive damage from the effective spell and actual bonuses, not the raw template.

**Formation constraints**

- Keep add death points at least the applicable eruption radius plus margin from other groups, while respecting separation anxiety and healer range.
- Spread the eight adds within an explicitly surveyed feasible layout; do not invent eight positions from two team anchors.

**Required executor work**: counted GUID objectives, exclusive crowd control, multiple add tank stations, death and forced eruption hazards.

**Acceptance observations**

- Eight distinct adds tracked from pull to death/control release; no double-assigned Banish.
- Log every eruption hit, buff loss, control gap and tank death; boss death alone cannot hide surviving adds.

**Source anchors**

- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_garr.cpp:21`: `EVENT_MASSIVE_ERUPTION`.
- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_garr.cpp:12`: `SPELL_ERUPTION_TRIGGER`.
- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_garr.cpp:13`: `SPELL_ENRAGE_TRIGGER`.

## Baron Geddon

Entry 12056; completion: boss death plus encounter completion. Status: research only.

Pull the patrol to a cleared, surveyed room. Give every raid member a reachable isolation location and reserve space for the tank to leave Inferno.

**Mechanics and responses**

- Living Bomb 20475 lasts eight seconds; its triggered Explosion 20476 has an 18-yard area. The carrier must leave others with enough path-time margin; repeated spreading around a stationary carrier is not sufficient.
- Inferno uses custom 19698 pulses with a 20-yard radius. C++ increases the damage by tick; DBC base damage alone understates the late ticks.
- Dispel Ignite Mana 19659 promptly on mana users. At under five percent the eight-second Armageddon sequence requires a timed burn or escape; a scripted self-death must be distinguished from an admin kill.
- Loaded effect overrides reorder Armageddon 20479 to area damage plus SCRIPT_EFFECT; the spell_scripts child death remains part of its normal completion path.

**Formation constraints**

- Maintain more than 18 yards between the bomb carrier and other bodies at explosion, with measured travel time from worst-case melee position.
- Plan tank/melee withdrawal beyond 20 yards during Inferno; retain healing line of sight and avoid patrol aggro.

**Required executor work**: marked actor isolation, damage ramp and impact deadlines, mana debuff dispel, scripted completion provenance.

**Acceptance observations**

- No bomb splash hits another raider; record isolation travel and Inferno ticks.
- Normal damage progression and valid completion observed with no combat replenishment.

**Source anchors**

- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_baron_geddon.cpp:31`: `SPELL_LIVINGBOMB`.
- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_baron_geddon.cpp:189`: `CastCustomSpell`.
- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_baron_geddon.cpp:32`: `SPELL_ARMAGEDDOM`.

## Shazzrah

Entry 12264; completion: boss death plus encounter completion. Status: research only.

Use ranged damage and assigned tank recovery stations around a cleared area. Give a priest an offensive dispel duty for Deaden Magic and mages curse-removal duties.

**Mechanics and responses**

- Arcane Explosion 19712 is 30 yards here, with a half-second cast. Melee uptime is subordinate to survival; do not use the commonly assumed smaller radius.
- Curse 19713 increases incoming magic damage; Deaden Magic 19714 is a dispellable boss buff. Friendly and hostile dispels require different target paths.
- The Gate code actually resets threat, teleports to a random player and attacks, despite a stale comment saying teleport is not implemented.
- Counterspell 19715 affects 45 yards; coordinate cast pauses and recovery against observed cadence, retaining emergency healing.

**Formation constraints**

- Keep ranged stations outside the 30-yard explosion while maintaining actual spell range; expect a blink to invalidate the old safe center.
- Stop damage and pets after a blink until a tank re-establishes control; move the new explosion away from the clustered raid.

**Required executor work**: hostile aura dispel, relocation and threat reset handling, boss-centered radius avoidance for all roles, cast pause scheduling.

**Acceptance observations**

- Each blink produces a new center, target and threat recovery record.
- Measure avoidable explosion hits, dispel latency and interrupted healing; verify ordinary completion.

**Source anchors**

- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_shazzrah.cpp:123`: `NearTeleportTo`.
- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_shazzrah.cpp:122`: `DoResetThreat()`.
- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_shazzrah.cpp:29`: `SPELL_ARCANEEXPLOSION`.

## Sulfuron Harbinger

Entry 12098; completion: boss death plus encounter completion. Status: research only.

Assign all four Flamewaker Priests 11662. Separate a kill target where geometry allows, tank the others, and allocate a primary plus backup interrupter per active healer.

**Mechanics and responses**

- Priest Dark Mending 19775 has a two-second cast. Interrupt the actual casting priest GUID; a boss-only interrupt rule cannot do this.
- Inspire 19779 chooses a friendly missing the buff within 45 yards and buffs Sulfuron too. Distance separation must be assessed together with Dark Mending range and healer reach.
- Demoralizing Shout, Knockdown and Flamespear still require tank mitigation and raid triage while the priest kill order progresses.

**Formation constraints**

- Survey pull and priest holding stations; separate by actual buff/heal reach only if paths, leash and healer range permit it.
- Face tanks away from the raid and reserve access paths for interrupters without dragging priests across the raid.

**Required executor work**: enemy GUID assignments, interrupt scheduling with backups, friendly enemy heal-range separation, hostile target priority.

**Acceptance observations**

- Every Dark Mending attempt has caster, interrupt owner, deadline and outcome.
- All four priests defeated before final boss burn; no unobserved priest healing masked as low damage.

**Source anchors**

- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_sulfuron_harbinger.cpp:33`: `#define SPELL_INSPIRE`.
- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_sulfuron_harbinger.cpp:94`: `DoFindFriendlyMissingBuff`.
- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_sulfuron_harbinger.cpp:61`: `TYPE_SULFURON`.

## Golemagg the Incinerator

Entry 11988; completion: boss death plus encounter completion. Status: research only.

Main tank holds Golemagg. Two add tanks hold the two Core Ragers 11672 away from the boss; damage dealers focus Golemagg.

**Mechanics and responses**

- Core Ragers heal instead of being killed below half health while Golemagg is alive. Do not waste raid damage trying to kill them first.
- Golemagg's Trust buffs nearby ragers within 30 yards. A rager more than 100 yards from the boss can trigger a reset; placement must satisfy both bounds.
- Magma Splash retaliates against melee. Watch stacks and survival; keep ranged pressure steady.
- Below ten percent the source schedules Earthquake (18-yard effect radius). Boss death explicitly kills the linked ragers.

**Formation constraints**

- Keep ragers beyond 30 yards with margin but within the 100-yard leash, and ensure each off-tank has healing line of sight.
- Measure melee feasibility under Magma Splash and the final Earthquake; move out when survival requires it.

**Required executor work**: independent add tank targets and stations, damage target exclusion, leash and aura-distance constraints.

**Acceptance observations**

- Ragers remain controlled, outside Trust, inside leash; no attempted rager burn before boss death.
- Capture ten-percent transition and verify boss death plus scripted add cleanup.

**Source anchors**

- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_golemagg.cpp:113`: `KillAdds(false)`.
- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_golemagg.cpp:140`: `GetHealthPercent() < 10.f`.
- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_golemagg.cpp:66`: `NPC_CORE_RAGER`.

## Majordomo Executus

Entry 12018; completion: eight adds defeated and Majordomo surrender/instance DONE; boss stays alive. Status: research only.

Tank Majordomo and the four elites 11664, and assign four mage Polymorph reservations to the four healers 11663 while verifying eligibility. Prepare enough tank/control capacity before starting.

**Mechanics and responses**

- Eight add deaths trigger surrender. Damaging Majordomo is not the win condition, and he must remain alive for progression.
- Magic Reflection 20619 and Damage Shield 21075 rotate on living adds every thirty seconds for ten seconds. Stop the affected damage type and choose targets by aura, including pets.
- At four adds remaining the script applies Immunity 21087 to all remaining adds. Prepare tanks and interrupt owners before crossing that threshold; do not assume Polymorph continues.
- Each death encourages survivors; the last add becomes Champion. Teleporting a player also resets Majordomo threat and requires immediate tank recovery.

**Formation constraints**

- Use the eight scripted spawn points as initial observations, then survey holding/control positions within separation-anxiety limits and healer range.
- Keep control targets outside incidental cleave/AoE and reserve tank paths for the four-add transition.

**Required executor work**: counted GUID objectives, friendly surrender completion, exclusive crowd control, per-target reflect damage stop, threshold reassignment, threat reset recovery.

**Acceptance observations**

- Exactly eight distinct summoned adds defeated; four-add immunity transition handled without loose adds.
- Majordomo alive, surrender state observed, encounter complete, and normal gossip progression available.

**Source anchors**

- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_majordomo_executus.cpp:202`: `AddCount == 0`.
- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_majordomo_executus.cpp:169`: `AddCount <= 4`.
- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_majordomo_executus.cpp:40`: `SPELL_MAGIC_REFLECTION`.

## Ragnaros

Entry 11502; completion: boss death plus encounter completion. Status: research only.

Place main and backup tanks in verified melee contact with knockback recovery routes. Spread the raid in surveyed reachable positions and assign Son of Flame tank/control duties before the summon dialogue finishes.

**Mechanics and responses**

- Wrath of Ragnaros 20566 has a 25-yard knockback area. A safe wall-assisted station must be proven from live collision and actual trajectories; never assume all melee can avoid it by standing behind.
- If the current victim is unreachable, the script seeks another player in melee and changes threat; with no reachable player it uses Magma Blast. Tank contact is a continuous invariant.
- Might of Ragnaros 21154 summons a trigger; Intense Heat 21155 reaches 20 yards. Track the spawned hazard, not the parent spell one-yard summon radius.
- Submerge occurs at three minutes, normally lasts ninety seconds, and spawns Sons of Flame 12143. The source emerges early when every living Son is banished; keeping them all controlled does not guarantee a ninety-second respite.
- Sons have Lava Shield 21857 triggering mana damage. Keep mana users away from their actual aura reach while killing/controlling them; reposition tanks before emerge.

**Formation constraints**

- Keep at least one assigned tank reachable by normal melee and line of sight throughout surface phases.
- Use valid ground around the lava and reserve separate son tank routes and ranged escape regions outside actual triggered radii.

**Required executor work**: continuous melee contact and backup tank, summoned hazard tracking, timed and aura phases, counted add tracking and control, early emerge recovery.

**Acceptance observations**

- Log surface/submerge/early-emerge transitions and all Son GUIDs; no unexplained loss of tank contact.
- Ragnaros starts alive, takes ordinary raid damage, dies with health zero and confirms completion; preserve full attempt provenance.

**Source anchors**

- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_ragnaros.cpp:355`: `UNIT_STATE_ISOLATED`.
- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_ragnaros.cpp:414`: `m_uiSubmergeTimer = 3 * MINUTE`.
- `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_ragnaros.cpp:46`: `SPELL_MAGMA_BLAST`.

## Trash and travel

- Use database spawn GUIDs, creature_groups, linking rows and patrol positions to identify pulls. A nearby entry match does not establish pack membership; avoid aggro bridges to neighboring groups.
- Core Hounds 11671 fake death at one health and can revive after ten seconds if an in-combat hound within 100 yards remains above one health. Balance damage across the pull, then finish together; one corpse-looking unit is not pack completion.
- Ancient Core Hounds 11673 face away, carry a random ability selection, and change respawn behavior after Magmadar. Learn the actual selection from live casts/auras before treating it as a fixed debuff.
- Firelords 11668 and Lava Spawns 12265 require spawn suppression and immediate assigned damage; avoid fire spells on fire-immune targets. The source caps lava spawns but does not remove the need to control growth.
- Lava Surgers 12101 and Annihilators 11665 need displacement/random-target recovery and healer protection. Treat a changed target as an observed mechanic rather than automatically blaming the main tank.
- Firewalkers 11666, Flameguards 11667, Lava Elementals 12076 and mixed lava packs require facing, interrupts/control where legal, and school-aware target selection. Use their database AI and source evidence, not a single generic trash rotation.
- Track consumables, ammunition, deaths, durability, mana, patrol respawns and cooldowns between pulls. Never replenish a battle and still label it an ordinary clear.

## Extraction limits

- File-scoped spell references can belong to adds or shared scripts; not all are boss casts.
- DB spell/effect overrides are applied before trigger closure; remaining hardcoded loader adjustments are flagged for review.
- Conditional C++ execution, arbitrary ScriptEffects, AI conditions and movement require review.
- Template reach is not live reach. Every station needs live floor, LOS and complete-path verification.
- Source revision does not prove the running server contains the changes.
- No encounter in this dossier has a verified normal-game clear.

## 2026-09-07: staging survey and QA readiness groundwork

Read-only Detour survey: scratch/raid-mc-staging/lucifron-navmesh.csv; nearby
spawn inventory: lucifron-nearby-spawns.csv in the same directory. The installed
map409 has7tiles. Survey started at actual Lucifron spawn1024.41,-973.309,-181.505,
bounded880..1120 by-1120..-880, step4, floor tolerance15. Candidates near
908,-920,-183.726 and912,-928,-189.166 have complete paths to the spawn and
approximately55yards from the nearest static spawn at similar height. They are
NOT live aggro/LOS certification: Lucifron patrols and hound groups wander.
His two protectors56606/56607 belong to group leader56605. Nearby five-hound
groups include leaders56629,56634 and56639; their fake-death/revival behavior
requires actual grouped combat completion, not simply seeing one-health bodies.

raid_session.py now verifies readiness against the attempt's saved encounter.json,
including map, boss entry and sealed plan identity. Wrong-map/wrong-boss tests pass;
the unchanged real Onyxia readiness also passes. Stage/recovery templates remain
Onyxia-specific pending a surveyed MC protocol. This is groundwork, not a MC pull.

## 2026-09-07 23:28 EDT implementation continuation

Onyxia is cleared, but no MC pull or clear has occurred. Shared counted-GUID add
completion and split/focus/balance/hold policies are built/deployed; actualdeath
hook distinguishes hound feigneddeath from final scripted death. Normalhoundpack
plan mc-core-hounds-west.json is BASIC/UNTESTED, and its stagingprofile remains
liveVerified:false. Solo survey failed at full40 assignment because other39 were
held outside and nonresident; no live geometry/normalaggro certification occurred.
Read the latest COMMANDER_RAID_HANDOFF.md continuation before resuming. All boss
plans in this dossier remain research; rune progression and Majordomo surrender
still need executor and real-game validation. Owner requested sessionpause.
