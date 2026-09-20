# Commander encounter definitions and execution

Current status, acceptance rule, hashes and next step live in `COMMANDER_RAID_STATE.md`; the design is in `COMMANDER_RAID_ARCHITECTURE.md`.

Updated 2026-09-06. This record describes the implemented architecture and its validation limits. It is not evidence of a raid clear.



## Status and authorization



Nico initially reserved Core build, installation and restart for himself. **Superseded on 2026-09-07:** he explicitly authorized agents to build/install/restart the configured VMaNGOS server throughout this full raid/boss scripting mission; see the mission exception in AGENTS.md and the latest COMMANDER_RAID_HANDOFF.md continuation. Older deployment restrictions in the history below are historical. The generic v3 source patch is now applied to `/home/wowvmangos/vmangos`, on baseline `fb207e67b6f4f34b4e27f9ecc48e0806bc7c057c`. No Core build, server deployment, runtime control or database/worldstate write was performed.



The portable patch is `core-patches/commander-raid.patch`, SHA-256 `37b1714771c51a716222a288713e19f9b68bb5f0c986da5e2a3349ec9891f81b`. It changes 18 files. The actual Onyxia boss script remains unchanged. Both central spell events and ordinary unit state feed the shared controller.



Client Debug/Release builds and **89 planner/profile/wire checks** pass (11 existing warnings in unrelated files). Ten gameplay source contracts verify the normal HUD/button path, the actual main character, fresh server spell facts, target selection and patient editing. Twenty-seven read-only source contracts pass on the patched Core. These checks do not compile C++ or prove a live encounter clear. The standing Core `tools/possess-law-check.sh` is absent from this checkout; that check remains unverified. Client possession, native-widget policy and shared-doc registration checks pass.



The Release presentation probe rendered a 40-person fixture with the human as main tank, 39 assigned bots, a healer whose primary patient is the human, and all three roster pages. Screenshots are `dumps/gameplay-commander-raid-ground-20260906-190721-972.png` and `dumps/gameplay-commander-raid-air-last-page-20260906-190726-978.png`. Both were visually inspected. This fixture runs offline and is not evidence that the networked fight works.



## Answer to “all boss fights?”



The implementation separates **engine functions** from **encounter definitions**. A new fight that uses the supported vocabulary is authored as JSON, loaded by the existing planner, and sent with the accepted plan. It needs no new C++ boss branch, boss-script edit, SQL record or server rebuild.



**The system does not yet clear every boss automatically.** Onyxia is authored; other known bosses can receive an explicitly labelled basic plan from their content facts. Definitions must describe actual mechanics and be tested. An unsupported mechanic requires a new reusable primitive, implemented once and then available to every definition. Unknown action/trigger names are rejected rather than silently skipped.



## The system a boss fight needs



A useful plan describes responsibilities, conditions, actions, and recovery. Each bot needs more than a rotation, and each encounter needs more than positions.



| Layer | Questions it must answer | Conceptual functions |

|---|---|---|

| Encounter identity | Which map, instance, boss, ruleset, difficulty and script revision? Has combat begun, reset or ended? | `ResolveEncounter`, `ObserveLifecycle` |

| Roster and capabilities | Who belongs to this raid? Who may I command? Who is alive, reachable, equipped, skilled, and ready? | `ReadRoster`, `ReadCapabilities`, `ValidateReadiness` |

| Authority | Who edits the plan, applies it, pulls, overrides a bot, pauses, and resumes? Which revision is authoritative? | `Authorize`, `ApplyPlan`, `YieldActor`, `Arm`, `Pause` |

| Roles and duties | What is my persistent role, temporary mechanic duty, target, backup, and eligibility? | `AssignRoles`, `ResolveDuty`, `ReassignVacancy` |

| Teams | Are there wings, heal teams, interrupt rotations, soak teams, or add squads? These are not necessarily raid UI subgroups. | `AssignTeams`, `ValidateCoverage` |

| Space | What defines north/left? Where are the room, safe zones, boss front, rear, hazards, entrances and paths? | `ResolveAnchor`, `FindStation`, `FindSafePath` |

| Time and phases | What state is the boss actually in? What is predicted versus observed? What starts/cancels a mechanic? | `ObservePhase`, `PredictDeadline`, `ExpireMechanic` |

| Targeting | Boss, adds, marked target, lowest-health ally, tank, dispel target? What if it dies or becomes unreachable? | `SelectTarget`, `ValidateTarget`, `ChooseFallback` |

| Rotation | Which learned action is usable, in range, affordable, off cooldown, and permitted by current duty? | `CanExecute`, `ChooseAction`, `ExecuteAction` |

| Cooperation | Who owns the next interrupt, heal, dispel, taunt, battle resurrection or shared cooldown? | `ClaimDuty`, `ReserveAction`, `ReleaseClaim` |

| Priorities | What wins when mechanics conflict, and how long may a lower-priority task wait? | `ArbitrateIntent`, `PreemptAction` |

| Recovery | What happens after death, fear, disconnect, failed path, lost tank, wipe, roster change, or stale command? | `Recover`, `InvalidatePlan`, `StopOwnedActions` |

| Evidence | Was an action requested, accepted, started, completed, or effective? Why is a bot waiting? | `ReportIntent`, `ObserveOutcome`, `ExplainFailure` |



The general execution loop is:



```text

snapshot = ObserveEncounterAndActors()

plan = GetAuthorizedPlan(snapshot.instance, commander, revision)

for actor in plan.assignments:

    if actor is human-controlled or explicitly overridden: yield

    if actor is frozen, dead, unavailable or unauthorized: report and wait

    duties = ResolveDuties(plan, snapshot, actor)

    intent = Arbitrate(duties, hazards, deadlines, resources, shared claims)

    action = FindLegalActionAndPath(intent, actor)

    ExecuteThroughNormalGameRules(action)

    ObserveOutcomeAndReportReason()

```



The engine implements shared roster/control, roles, healing and support reservations, multiple boss objectives, and the primitives below. Hazard routes are sampled for re-entry and estimated arrival time, but real encounter timing and navigation still require combat tests. Special mechanic families outside this vocabulary require reusable extensions.



## What is data, what is code



`encounter-definitions/onyxia.json` contains Onyxia's map/boss identity, chamber bounds, tank/team anchors, coverage requirements, add entries, phase conditions, breath spell/point IDs, fireball spread rule, and Fear Ward rule. None of those identities occur in the Core executor. The bundled default is embedded in the client; the JSON library in the repository root can override it or add definitions without recompilation.



The shared engine owns authority, assignment leases, readiness validation, phase selection, rule priority, normal casts/attacks, movement/path checks, threat gating, healing selection, add selection, lifecycle and status. A bounded JSON parser in Core accepts data only. There are no executable expressions, arbitrary scripts, commands, SQL or file references in a definition.



| Supported vocabulary | Behavior |

|---|---|

| Identity and room | One primary boss entry, up to eight distinct boss objectives and a map; finite bounds up to 400 yards per axis; tank anchor. Each objective receives a tank. Completion requires all objectives to have been observed dead after combat. |

| Teams | One to eight named teams with anchors; add-tank/healer coverage per team. Teams do not change the game's raid subgroups. |

| Roles | Main tank, add tank, healer, melee, ranged. Temporary rule duties override the normal role. |

| Phases | Up to 16, chosen by priority, health interval, observed levitation and optional boss aura. A fallback phase is mandatory. Each phase selects primary/alternate stations, melee/ranged permission and threat ratio. |

| Rule filters | Up to 64 prioritized rules; role bitmask, optional phase and optional feature-toggle bit. |

| Triggers | `always`, `castStart`, `castGo`, `bossAura`, `selfAura`. Cast observations retain the actual explicit target for a bounded duration. |

| Targets | `self`, `boss`, `tank`, `marked`. A marked target requires an observed cast event. |

| `avoidPoints` | Find a reachable endpoint outside the authored spell-target-position footprint. Sample the route at roughly one-yard intervals; after escaping, reject re-entry. Compare path length/current run speed against the remaining event window. Candidate search is bounded. |

| `spread` | Move away from the selected target by the rule's radius; the marked actor holds. |

| `stack` | Move within the selected target's radius and hold. |

| `move` | Move to a data-defined station. |

| `stopDamage` | Stop owned attacks/casts/movement while the rule is active. |

| `cast` | Attempt a specific learned spell through ordinary validation. Optional missing-aura guard supports buffs; hostile spells can express interrupt attempts. |



Higher-priority active rules run before normal role behavior. Cast rules that cannot execute allow lower-priority work. Mandatory movement rules report failure and hold rather than teleporting. Rule priorities cannot override possession, explicit commander orders, freeze, death or missing authority.



For example, the fireball definition says: on spell-go for spell 18392, retain its target for five seconds, apply `spread` at radius 12 to eligible roles, with priority 90. The engine knows how to spread from an observed target; it does not know the word “fireball.” Those radius/duration values still need live validation.



## Commander workflow in the actual game



The existing Commander rotation/tactics button opens **Rotations & Raid Plan** through the ordinary gameplay HUD. Neither this button nor its panel requires Creator mode or developer tools. The offline probe is a separate caller of the same panel and cannot insert its synthetic roster into party state or network packets.



1. Enter Command View with your actual group and open the rotation button.

2. Choose **My role**: main tank, add tank, healer, melee or ranged. This is mandatory before Auto-assign. Your character remains manual; its role counts toward raid coverage and makes it a valid primary healing patient.

3. Select the boss and click **Use target**, or use **Encounter** to select a loaded definition. Onyxia loads the authored definition. An unmapped known boss gets a conspicuously labelled basic plan around its live position. That plan uses known immunities but does not invent special mechanics.

4. Click **Auto-assign**. The planner uses your actual character plus the full raid roster, not only your subgroup. It waits for this session's server-reported learned spells when facts are missing, reports incomplete facts after ten seconds, and never grants abilities from the catalog.

5. Review roles, named teams, primary/alternate stations, learned heals/attacks, and each healer's editable **Primary** patient. Auto-assigned support capabilities are shown for non-healers. Changing the main role clears the old assignment and requires Auto-assign again.

6. **Apply to raid** validates locally and on Core. **Arm plan** is enabled only after Core acknowledges the exact current draft. Arming permits nearby legal healing and authored missing-aura self/tank buffs; it does not move through the boss's aggro radius or initiate attacks. The human pulls.

7. During combat the server reports each bot's current duty. **Pause** releases the plan. An explicit command or directly driven bot yields that member until deliberate rearm. **Clear live plan** discards an inactive plan.



**Who decides:** the human chooses their own role, selects the encounter and approves the generated assignment. Only the actual group leader in Command View can Apply/Arm/Pause/Clear. Core rechecks command rights, learned spells, map/instance, room coordinates, team coverage, primary patients and revisions. The raid planner decides bot duties; ordinary game rules decide whether each cast, attack or move is legal. Explicit human control wins over every mechanic.



**How roles are filled:** keep the human's chosen role; fill one main tank per objective; fill each team's add-tank requirement; fill minimum healer coverage and the encounter's total healer requirement; then assign the remaining damage roles. Tank candidates use class/form eligibility plus observed health, armor, level and learned taunt capability. Healers require an actual learned direct heal. Ties use GUID order, making assignments stable under roster arrival ordering. This is capability-based allocation, not a proof of talent/gear optimization or sufficient raid throughput.



**How healing works:** distribute primary patients across main and same-team add tanks, with extra weight for main tanks. A healer first reacts to critical allies, then legal support duties, then normal healing. Selection accounts for the primary patient, tank role, team, actual health, estimated incoming assigned heals, range and line of sight. A critical heal can stop ordinary station movement; hazard rules still outrank it. Critical healing prefers quicker learned heals. Normal healing starts with the selected spell and can fall back to learned alternatives/ranks when normal casting rejects it or mana is insufficient. Shared reservations expire or release when the caster dies or stops casting. They estimate base healing and do not assert that a heal landed, crit, or included all equipment modifiers. A human healer is counted and shown a primary patient but never auto-casts or contributes a fictional incoming-heal reservation.



**How support works:** learned effect data selects interrupts, friendly dispels, taunts and self damage-reduction abilities. Core validates those categories again, uses ordinary spell/immunity/cooldown rules, checks actual dispellable auras, and briefly reserves successful attempts to avoid same-tick duplication. Tank loss allows an available add tank to become the backup. Add tanks enumerate matching adds and prioritize loose enemies over adds already held by another tank. The temporary role passed to the existing melee/tank class rotation is restored afterward.



Encounter teams are logical duties and positions; they do not rewrite the game's raid subgroups. The map is a schematic, not a collision mesh. Clicking changes the selected member's station. Ground melee uses a boss-relative flank during execution. Save writes ignored `commander-plans/<definition-id>.json`, including the complete definition and actual GUID assignments. **Reload fights** reloads the JSON library and clears the selected definition's draft assignments. Save desired manual edits first.



The external bot brain remains connected, but competing mutation messages receive `ENCOUNTER_DROP`; explicit commander-injected orders yield the encounter lease first. The executor never teleports followers and never acquires the human's controls, including on Pause, Clear, death or reset.



## Actual content audit



The read-only audit covered 217 boss-tagged creature templates, 113 boss script files, 154 matching spawns, 22,413 spell records, 307 spell target positions, 2,596 creature spell-list records and 7,381 generic AI script records. The content selection uses client build 5875 and creature patch ceiling 10 from this development database. These are dataset counts, not numbers of fully understood or supported fights. Registration extraction matched 128 script registrations; several registrations can occur in one file.



`tools/encounter-content-audit/extract.py` generates the tracked, embedded `MSUIClient/Data/encounter-boss-catalog.json`. It records boss identities, immunities, script spell IDs, source paths, feature flags, spawn positions, baseline and input hash. The runtime **Use target** path consumes the resulting facts. Raw source/SQL exports remain in ignored scratch. Only world-content SELECTs were used; no account/character information, credentials, database writes or save swaps are included in the artifact.



A concrete data correction: Onyxia has fire-school immunity. Auto-assign excludes fire damage even when the learned fire spell has a higher level. Holy Light is a script effect (77), not direct-heal effect 10, in this database; both client and Core recognize that spell family by its actual spell name. Hunter Auto Shot uses effect 58 plus auto-repeat attributes and is accepted as a learned ranged fallback. Those cases have regression checks.



## Observation and protocol



`Spell::SendSpellStart` / `SendSpellGo` report casts from watched boss objective entries. `Unit::Update`, after its freeze guard, reports ordinary state and advances event expiry on the game's clock. A small atomic watch list avoids locking the plan store for every unrelated creature. Observations bind map, instance and boss GUID. The controller uses levitation/health/aura predicates, not private boss-script phase fields.



For Onyxia, a health interval covers takeoff; a higher-priority observed-airborne predicate keeps bots in alternate stations when health drops before landing. This is a data definition inferred from the exact local script, not an assertion that health alone is always sufficient.



Protocol version **3** uses opcodes 874/875 and capability bit 13. Request header: `u8 version, u32 request, u32 revision, u8 operation, u32 bossEntry, u8 toggles, u8 actorCount, u32 mapId, u32 definitionBytes` (24 bytes). Apply then carries 71-byte actor records and UTF-8 JSON, maximum 32 KiB. Other operations carry zero actors and zero definition bytes. Reply layout remains 21-byte header plus 17 bytes per actor, with v3 and arbitrary valid phase IDs. Exact lengths and bounded identities are checked before applying state. The v3 source upgrade supersedes the previously applied v2. Each actor record now includes a manual-control byte, primary-patient GUID, objective entry, and four support spell IDs in addition to role/team, stations and selected heal/damage. An older wire version is rejected.



The server checks the definition's spell identities and required spell target positions against its loaded data. A missing hazard point rejects Apply; a missing runtime path reports failure. Applying a new definition does not edit the world database or the encounter script.



## Validation limits and broader encounter coverage



- Onyxia's authored phases, breath geometry/timing, fireball spread, Fear Ward, whelp pickups and landing threat must still be exercised in the actual raid. No live boss kill has been observed with this patch. Successful client builds and source checks cannot substitute for an actual Core build and combat testing.

- Multiple boss objectives now have distinct tanks and an all-observed-dead condition. Sequential objectives, separate rooms, respawning objectives and bespoke victory conditions need additional definition semantics and tests. There are still only two station sets.

- Cast observations cover boss objectives and their explicit cast targets. Add-origin casts, multi-target snapshots, dynamic area triggers and event cancellation need broader observation primitives. The sampled route/time check uses the definition's remaining window and current speed; it cannot establish the true impact deadline or survival after a movement slow without live evidence.

- Gameobject interactions, soaks, transports, mind-control duties, line-of-sight puzzles, dedicated battle resurrection and arbitrary phase formations are not represented by the current six actions. They must be added as reusable primitives rather than boss-name branches. An in-window rule editor is not implemented; advanced rules are JSON data.

- Friendly dispel/interrupt selection follows generic legality. Fight-specific “do not dispel yet” policies and long cooldown schedules need explicit mechanic data. Pets, ammunition, consumables, spec-dependent resources and sustained healing/damage throughput need live class testing. The patch does not conjure consumables or bypass cast costs/cooldowns.

- Readiness checks current membership, abilities, role coverage, life state, coordinates and authority. It does not certify resist gear, raid buffs, consumable stock or sustained throughput. Logical teams do not guarantee subgroup-limited effects.

- Logout, leader changes, wipe, freeze/resume, dead tanks, failed paths and existing brain interactions require runtime tests. Global plan locking is not proof of world-object lifetime/thread correctness. Status describes current intent, not a combat replay or confirmed effect.



## Files and verification



Client data lives in `World/Encounters/CommanderEncounterDefinition.cs`, `CommanderRaidPlan.cs` and `CommanderBossCatalog.cs`; pure validation/layout/phase selection lives in `Engine/UI/CommanderEncounterLaw.cs`, `CommanderRaidPlanLaw.cs`, `CommanderRaidCapabilityLaw.cs` and `CommanderEncounterSelectionLaw.cs`. The panel and wire remain in their existing feature files. The standalone `tools/commander-raid-check` links these pure classes and exercises the second encounter plus malformed definitions and packets.



Core adds `SuiEncounterDefinition.{h,cpp}` for the bounded parser and `SuiCommanderRaid.{h,cpp}` for shared execution. Central observations, AI ownership, packet registration and capability advertisement are included in the same patch. `tools/commander-raid-source-check.py` is read-only and labels its results as source contracts, not execution tests.



```text

dotnet build MSUIClient/MSUIClient.csproj -c Debug

dotnet build MSUIClient/MSUIClient.csproj -c Release

dotnet run --project tools/commander-raid-check

python tools/commander-raid-check/gameplay-source-check.py

dotnet run --project tools/interface-wire-check -- --possess-law-only

dotnet run --project tools/interface-wire-check -- --imgui-policy-only

dotnet run --project tools/interface-wire-check -- --shared-docs-only

python3 tools/commander-raid-source-check.py   # in Core; no build/runtime action

```



The authorized offline client probe uses `MSUI_COMMANDER_RAID_PROBE=1`, a network-disabled config, and a synthetic presentation roster. It captures primary/alternate maps and the final roster page, then exits itself. It cannot prove live click routing, spell behavior, or a raid clear.



After the Core build/install/restart (agent-authorized for this mission), verify the current capability/replies, Apply/Arm/Pause/Clear, ownership overrides and freeze with his existing test character/39-bot raid. Then validate each mechanic and phase, fix failures, and run full attempts through an actual Onyxia death under normal game rules. Build logs and live observations are required before calling the task complete.



## Research basis



The exact local VMaNGOS source supplies spell, movement, threat, bot, possession and encounter behavior. Onyxia content was extracted from that script and loaded spell-position identities; the script itself is unmodified by this revision. For the editing model, Obsidian's [Deadfire update 49](https://eternity.obsidian.net/news/pillars-of-eternity-ii-deadfire-update-49---patch-11) documents AI conditional organization and save workflows. Shared assignment authority and mechanic coordination are additions required by this raid system.





The separation between observed state and reusable decisions also follows the model described in Epic's [Behavior Tree overview](https://dev.epicgames.com/documentation/en-us/unreal-engine/behavior-tree-in-unreal-engine---overview). As another MMO comparison, [FFXIV BossMod](https://github.com/awgil/ffxiv_bossmod) combines encounter mechanics, rotation/cooldown planning and AI. Those projects inform the separation of responsibilities; they do not establish that an arbitrary fight can be solved without encounter knowledge.





## Live verification — 2026-09-06: Core crash before pull



The deployed v3 capability was observed on the real server. With explicit owner authorization, the web app created exactly 39 bots (job `0c0ce052`): two protection warriors, six holy dwarf priests, four holy paladins, eight combat rogues, twelve frost mages, four destruction warlocks and three marksmanship hunters. All were made level 60. The queued second protection profile subsequently converged to its intended role with 51 talent points. Testwar is player GUID 787. Server responses confirmed the actual pre-raid BiS templates for all forty characters after correcting four selection/command timing misses; persisted inventory inspection found equipped items on all forty.



The normal client logged into the real server with isolated QA settings. Its actual network roster contained Testwar and 39 bots, with `creator=false`. Production Auto-assign produced a valid plan with Testwar manually controlled as main tank and observed abilities on every member. Native UI automation could not initialize because of the desktop helper ACL error, so button clicks remain unverified. The opt-in `raidqa` protocol calls production operations against the actual server, not a synthetic roster.



Apply outside map 249 did not succeed. Background Inspect responses obscured the refusal; the client now preserves failed mutation messages across successful Inspect responses and logs operation/result/state. A later relog transferred leadership to a bot; the existing in-game claim-lead request succeeded and restored Testwar as leader.



The first instance transfer requested (-65, -215, -88), below supported client collision. The loading screen stayed up. At **21:28:55 EDT**, VMaNGOS crashed before bot staging, Apply/Arm inside the instance, or any boss pull. No live combat mechanic or raid clear has been verified. The unrelated CMaNGOS server was left untouched.



Offline inspection of coredump PID 2516164 showed `WorldSession::~WorldSession -> LogoutPlayer -> HandleMoveWorldportAck -> DungeonMap::Add -> Map::Add`. The faulting instruction at `Map::Add+847` writes through a null broadcaster. `WorldSession::Update` calls `DeletePacketBroadcaster` on socket closure, before logout finishes pending far teleports. `Map::Add` dereferenced that pointer unconditionally. The supplemental **core-patches/commander-raid-live-fix.patch** guards this access while preserving map entry and logout cleanup. It is applied to the Core source checkout only; it must accompany the original raid patch on a fresh checkout. No Core build, install, restart, service control or database restore was performed. Core's 27 source contracts passed; the separate `tools/possess-law-check.sh` is absent and was not run. The crash correction has not been built or verified live.



The prepared next staging coordinate uses the script's south-room XY (-65.8444, -213.809) with Z -83, above the script's ground -84.298462; it still needs live verification. The owner explicitly approved instance teleports and disabling Testwar's GM/combat cheats, and that authorization persists. The prepared battle driver requires a real forty-member raid, successful Apply/Arm, ordinary Testwar control and confirmed GM mode off. It uses normal movement, attack and learned warrior spells and requires observed Onyxia death plus server completion. It has not reached the pull.



Evidence is ignored under `scratch/onyxia-live/`: `prepare-evidence/prepared-roster.json`, `assign-evidence/live-assignment.json`, `equipment-verification.json`, `crash-backtrace.txt`, and the `battle-2` log/bootstrap artifact. The real-game assembly screenshot is `dumps/gameplay-onyxia-prepared-raid-20260906-211928-476.png`. The QA configuration contains existing login credentials and must not be published.



**Next owner step:** build/install/restart the corrected Core. Continue this same raid after inspecting its saved position/state following the crash; then verify instance staging, Apply/Arm and the full encounter. Live acceptance remains incomplete.





### Owner reboot follow-up, 2026-09-06



The owner reported the corrected Core rebooted; read-only inspection found VMaNGOS startup at 21:36:41 EDT and the null guard in the source checkout. Live run `battle-3` entered map 249 at the corrected staging point and logged out without a repeat server crash. This verifies normal instance entry at that point, not the exact pending-worldport disconnect regression. The raid still has all 39 bot members, but the web app reports all 39 disconnected after reboot. The protocol could not Auto-assign without live member facts and stopped before Apply/Arm or combat.



The owner explicitly approved reconnecting those same bots using `.bot add NAME`. Automatic approval review nevertheless rejected executing that web command even after the explicit approval, citing the owner-only runtime rule. No workaround was executed. `scratch/onyxia-live/Reconnect-OnyxiaRaid.ps1` is prepared for the owner to run; it names only these 39 existing characters. Once their logins complete, resume the prepared battle protocol. No boss pull or kill has occurred.





### Reconnected raid: live Apply refusal and recovery handoff



The owner reconnected the same 39 bots. Read-only bridge inspection confirmed all 39 online, level 60 and alive; run `battle-4` brought all 39 into map 249. Production Auto-assign completed, but Core rejected Apply with result 5. The actual assignments exposed a classification mismatch: client defensive detection paired any negative base point with any damage-taken aura, selecting Recklessness, while Core requires a negative value on the same effect index. Client classification now matches the effect index. Damage candidates must also support at least 20-yard range (or real ranged auto-repeat); category recovery is included in the cooldown limit. A direct taunt is preferred over temporary forced-target attacks. Regression checks reproduce Recklessness, Wing Clip and Mocking Blow choices.



After the rejected unarmed plan and logout, all 39 bots were observed dead. No armed executor battle was performed. A subsequent readiness attempt showed ghosts can retain positive health; the live roster now explicitly rejects `PlayerIsGhost`. A production Hold command was added to the opt-in QA protocol so ordinary AI does not wander during staging. Hold uses normal SUI order 2 on exactly the 39 actual bot GUIDs; the human is excluded.



The owner gave explicit permission to do anything needed with Testwar and these 39 bots, including recovery, revival and repairs. Automatic approval review nevertheless blocked execution of the recovery protocol, claiming AGENTS.md could not be overridden. The recovery and battle protocol is prepared but was not executed by the agent. The owner can launch `scratch/onyxia-live/Run-OnyxiaLiveTest.ps1`; it uses the existing credential-bearing QA config without printing credentials, writes `owner-battle.stdout.log` and `owner-battle-evidence`, and controls only the local client. It does not build/deploy/restart a server or restore a database. Recovery runs outside combat before staging and normal-input combat. No live kill is verified.


## Role competence and fast iteration — 2026-09-06 late-session revision

The encounter definition chooses objectives, teams, stations, mechanic duties and damage/threat windows. A reusable class/spec policy must decide how to perform the assigned role with the character's actual learned ranks, talents, equipment, resources and cooldowns. A single preferred spell is a readiness/fallback hint, not a rotation. Progress through the encounter cannot validate a role that is missing its basic kit.

The following is the required behavior contract. Guide advice is cross-checked against this server's actual spell data and `AiBotAISpecCombat.cpp`; contemporary Classic guides do not override the local build 5875 rules or grant talents the character does not have.

| Role/spec in this raid | Required ordinary play | Evidence to inspect |
| --- | --- | --- |
| Protection warrior | Defensive Stance; establish melee threat before moving the boss; Shield Block while receiving melee hits; Revenge and Shield Slam, Sunder maintenance; Heroic Strike only with surplus rage. Use survival cooldowns before lethal damage, retain shield and face the boss. Taunt is a recovery tool only where the target permits it. | Defense/weapon skill, shield, rage, successful casts/misses, boss victim, threat lead and incoming melee. |
| Holy priest | Assigned tank/patient coverage, coordinated incoming heals, choose ranks for deficit and urgency, conserve mana, Fade when healing attracts the enemy. Dwarf Fear Ward must actually be learned and rotated by the encounter's support rule. Emergency self-healing must not replace the tank's coverage indefinitely. | Heal start/landing/overheal, reserved incoming healing, mana, Fade and Fear Ward application/cooldown. |
| Holy paladin | Flash of Light for efficient sustained work; Holy Light for larger/urgent deficits; choose rank with actual healing bonuses. Coordinate blessings, keep Salvation off tanks, avoid immunity spells that make an active tank lose the enemy. | Flash of Light eligibility, useful healing per mana, blessing coverage and tank targeting. |
| Combat rogue | Melee flank/rear, maintain Slice and Dice, generate with Sinister Strike and spend finishers appropriately. Use legal cooldowns and poisons; reduce threat with Feint/Vanish as needed. Respect encounter movement and target assignments. | White attacks, energy/combo points, Slice and Dice uptime, poisons, finishers and threat pauses. |
| Frost mage | Frostbolt and learned frost talents; respect immunity and threat limits, use mana recovery during a safe window, coordinate debuffs and defensive cooldowns. | Cast throughput, spell immunity/refusals, mana, threat and interrupted casts during holds. |
| Destruction warlock | Use Shadow Bolt when fire is immune; coordinate useful curses and debuff slots, manage a real pet and resources, use Life Tap only with adequate health/healing headroom. The actual talent profile determines pet/sacrifice choices. | Curse coverage, legal non-immune casts, mana/health, pet contribution and resource recovery. |
| Marksmanship hunter | Auto Shot plus Aimed Shot and suitable other shots, correct ranged distance and facing, actual ammunition, pet control and threat reduction. Account for Auto Shot timing instead of treating Arcane Shot as the rotation. | Auto Shot events, Aimed Shot, ammo consumption, dead-zone refusals, pet target and Feign Death behavior. |

Research: [warrior tank rotation](https://www.wowhead.com/classic/guide/classes/warrior/tank-rotation-cooldowns-abilities-pve), [Classic threat](https://www.wowhead.com/classic/guide/threat-overview-classic-wow), [priest healing](https://www.wowhead.com/classic/guide/classes/priest/healer-rotation-cooldowns-abilities-pve), [healing downranking](https://www.wowhead.com/classic/guide/spell-down-ranking-classic), [holy paladin](https://www.icy-veins.com/wow-classic/holy-paladin-healer-pve-rotation-cooldowns-abilities), [combat rogue](https://www.icy-veins.com/wow-classic/rogue-dps-pve-rotation-cooldowns-abilities), [mage](https://www.icy-veins.com/wow-classic/mage-dps-pve-rotation-cooldowns-abilities), [warlock](https://www.icy-veins.com/wow-classic/warlock-dps-pve-rotation-cooldowns-abilities), [hunter](https://www.icy-veins.com/wow-classic/hunter-dps-pve-rotation-cooldowns-abilities).

### Readiness findings and completed preparation

Testwar's saved Defense was **90/300** and Swords **176/300** before the audit. All 39 bots already had capped level-scaled skills. Normal `.maxskill` gameplay commands were applied to exactly the authorized forty characters; live Testwar fields and read-only saved-state inspection now show Defense and Swords 300/300, and no below-cap level-scaled skill rows remain in the raid. This is combat skill preparation, not a claim about profession completion, defense from gear, hit caps or perfect itemization.

All six dwarf priests lacked **Fear Ward and Desperate Prayer**. They were taught 6346 and 19243 through ordinary selected-character GM commands. A subsequent authoritative roster refresh proves both learned on GUIDs 117–122. The complete reviewed role-spell checklist passes for all forty using actual live spell facts. Saved `character_spell` alone is insufficient: default spells and lower ranks may be reconstructed on login; the hunters already knew Auto Shot live. Rogues had the later Eviscerate/Feint ranks. All 39 runtime talent profiles report usable with their intended spec; the earlier preparation recorded 51 talent points each. The seven saved hunter/warlock pets are level 60. Pet abilities/autocast, poison application, buffs, consumable stock and sustained throughput still need explicit acceptance observations; do not call this a complete class-readiness certification.

### Source fixes awaiting the owner's Core build

`core-patches/commander-raid-role-policy.patch` is applied to the existing Core source checkout only. The raid now delegates ranged attacks to ordinary class/spec AI after movement/mechanic/threat arbitration. That preserves learned rank, cost and immunity checks. The temporary encounter role is visible to those policies. Generic add CC selection is suppressed while the encounter owns assignments; the hunter spec path cannot fabricate ammunition in an owned raid.

Both client and Core now recognize script-effect **Flash of Light**, alongside Holy Light. Healing selection scores actual remaining deficit, affordability and ordinary caster/patient healing bonuses, prefers adequate fast heals for emergencies and efficient useful healing otherwise, and reserves only useful incoming healing. The amount is a conservative noncritical estimate, not a prediction of future damage or exact healing. Priest Fade and rogue Feint can respond to the relevant threat situation. Damage holds interrupt ongoing non-melee spells and pet casts as well as stopping attacks.

This integration is still incomplete: coordinated curse/buff policies, hunter shot timing and robust Feign Death reset, pet spell settings, poison stock/application, healer damage forecasting and all spec rotations need further verification. The existing policies are reused, not certified as optimal. No new boss-name branches were introduced. A separate `commander-raid-roster-batch.patch` batches repeated full-roster broadcasts from group link/follow orders, fixing a send-queue overflow seen during recovery. The currently running Core has neither supplemental change until Nico builds/deploys/restarts it.

### Retry workflow and evidence

Keep one persistent real client logged in with `persistent.protocol` (`raidqa await`). `Submit-OnyxiaAttempt.ps1` queues Stage, Fight, Full or Report into that session. Stage explicitly resets Testwar outside the instance, recovers the same bots, clears stale combat references on these characters, revives and replenishes the caller separately (group commands skip the caller), repairs, Holds/unlinks, pauses/clears stale plans, stages, Auto-assigns and applies/arms. It then disables QA cheats and stops before pulling. Fight continues the same staged attempt. Full combines them. Failure retains the login and waits for the next protocol; it does not continue to a misleading completion report. New attempt labels create separate report/battle/failure directories.

The temporary client workaround paces individual unlink orders at 0.6 seconds until the Core broadcast batching is deployed. Preparation is outside combat; no admin damage, revival, replenishment or boss modifications are used during an acceptance pull. The human tank driver now uses ordinary Shield Block/Revenge/Shield Slam/Sunder, health-based defenses, Bloodrage and surplus-rage Heroic Strike, while retaining melee threat before backing toward the station. Read-only `.list threat` samples complement health, victim, rage, aura and position evidence.

Debug and Release client builds pass with 11 existing warnings. The planner/profile/wire suite passes 95 checks, gameplay source checks pass 12, and Core source checks pass 33. These are not Core compilation or combat proof. Several legitimate armed attempts reached only 98–99% before wiping, prior to the skill/racial corrections; no normal-rules raid clear has been observed.



## Positioning verification — 2026-09-07



The owner rebuilt the role-policy and roster-batch Core changes at 23:59 on September 6; they are now deployed. Subsequent ordinary combat reached approximately 83% before a wipe. This remains incomplete live acceptance.



Read-only current world `spell_cone`, versioned `spell_template` rows (latest <=5875), the server's SpellRadius.dbc, and `Spell::SetTargetMap` / `SpellNotifierCreatureAndPlayer` establish the actual ground hazards:



| Spell | Direction and size | Placement consequence |

| --- | --- | --- |

| Flame Breath 18435 | Front 120 degrees, radius 45 yd | Only the tank occupies the forward sector. |

| Wing Buffet 18500 | Front 120 degrees, radius 20 yd | Flank melee avoid it; large-target melee reach also permits forward attacks beyond this radius. |

| Tail Sweep 15847 | Rear 120 degrees, radius 30 yd | Stay in the side sectors, including healers and add tanks. |

| Knock Away 19633 | Direct victim, not a cone | Wall backing limits displacement; the script also removes 25% victim threat. |

| Fireball 18392 | Impact AoE radius 8 yd | Air spread and travel timing still require verification. |



The complementary safe ground sectors each span 60 degrees, centred at +/-90 degrees relative to the boss facing. Source radius checks use centre distances against players; Onyxia receives no player movement leeway on her cone radius. Cleave is a chained victim-target attack in this Core, so spacing from the tank also matters; do not model it as another guessed cone.



The live `raidqa geometry` probe measured Onyxia combat reach 26 and bounding radius 2. Stationary player/boss autoattack reach is approximately 28.8 yards using the shared melee reach law. The room floor rises gently toward the north wall, with the steep wall near X=36–37 at Y=-215. The authored tank point is now (34,-215,-84); the expected settled boss X~5–10 must be checked in combat. The support anchors are (15,-191,-88) and (15,-239,-88). Healer rows use three-yard spacing to retain patient coverage; add-tank rows sit five yards behind the team anchor. Other ground roles keep the wider grid. Existing server melee placement remains relative to current boss facing, at the centres of the flank sectors.



Client regressions verify full 3D primary-patient distance <=40 and support clearance of at least five degrees from both ground cones across the expected settled boss X=5–10. These are planned-layout checks, not proof of safe paths or a successful pull. The manual QA tank now returns to its authored station and lets the boss approach, uses actual melee reach for Shield Block, and no longer forces a five-yard chase. No new boss-specific Core branch or wire change was added. The next ordinary battle must verify station reachability, boss facing, knockback recovery, support arrival paths and cast hit lists.



The retry driver keeps one login and isolated evidence per attempt. Recovery explicitly selects each of the same forty GUID/name pairs and clears cooldowns once outside combat; it does not enable a persistent cooldown cheat. Debug/Release and 97 raid regressions pass. Geometry evidence is under `scratch/onyxia-live/sep7-session-3-evidence/geometry-stage/`; the next staged attempt is `sep7-session-4-evidence/measured-wall-01`. No raid clear is claimed.





### Result of the measured-position pull and required correction



The new attempt reached airborne phase with all forty alive (54% at 115 seconds), then lost eighteen actors to the first Deep Breath and ultimately reset after reaching 47%. The boss moved close to the wall after Knock Away, invalidating the predicted settled X=5–10 placement. Initial support cone regressions therefore cover only that initial hypothesis; they do not certify the later geometry. A generic Core radial fallback now searches seven closer flank endpoints with floor/range/LOS/path checks. Dynamic support cone avoidance and complete safe approach paths remain work.



The decisive breath defects are corrected in source/data: triggered casts skipped the old `SendSpellStart` observer, and the authored radius was 19 despite actual 30-yard effects. The observer now runs during successful preparation for both triggered and ordinary casts, all eight data footprints are 31, and Apply rejects undersized point-hazard radii using loaded spell data. The new `commander-raid-geometry.patch` is applied to Core source but requires Nico's build/install/restart. It passes 37 source checks; final client Debug/Release, 98 raid regressions, 12 gameplay contracts and PossessLaw pass. No normal-rules clear has occurred. All forty were recovered outside, plan cleared, and the local QA client closed. See the final handoff section for exact evidence and remaining limitations.



## 2026-09-07 continuation: Molten Core understanding and stricter retry evidence

Nico expanded the requested scope to all Molten Core, using the successes and failures of this Onyxia session. **Onyxia is still not cleared; Molten Core has not been attempted.** The pending geometry/trigger-observer Core patch remains the next prerequisite for a meaningful Onyxia retry. Read-only inspection still finds the running binary timestamp at 2026-09-06 23:59:40, before that source patch. No server build, deployment, restart or database/worldstate restore was performed.

`shared_docs/COMMANDER_MOLTEN_CORE.md` now contains a generated review of all ten encounters, role duties, positioning constraints, trash and rune/summon progression. Reviewed intent lives in `tools/encounter-content-audit/plans/molten-core.json`. These are research plans, deliberately not loaded as executable encounter definitions. Required generic capabilities remain explicit: GUID-based add/control/interrupt reservations, typed and prioritized dispels including hostile buffs, persistent targeted and summoned hazards, counted objectives, friendly surrender, reflect handling, and continuous tank contact/recovery. The existing kill-only Onyxia human driver is not a full Molten Core driver.

The new SELECT-only `harvest.py` and `compile_raid.py` snapshot C++ plus actual creature templates, AI events/action scripts, spell lists, conditions, groups/linking, gameobjects, DBC geometry and spell trigger chains. The corrected source parser recognizes CamelCase enums, #defines and direct casts, strips comments and reports unresolved dispatch. The existing 217-boss client catalog was regenerated with it. Fresh ignored evidence: `scratch/raid-audit/snapshot.json` and `molten-core-review.json` (ten encounters, 32 creatures, 139 spell records). The generated review retains source hashes/anchors. Four dynamic/overloaded source calls remain explicitly unresolved across the source inventory; the compiler is not a C++ interpreter.

Local data matters: Lucifron Doom/Curse and Magmadar Panic reach 45 yards; Shazzrah Arcane Explosion reaches 30; Geddon bomb's child Explosion reaches 18 and his custom Inferno pulses reach 20. Garr's normal eruption reaches 15 but forced eruption reaches 30. Majordomo surrenders alive after eight add deaths; Ragnaros can emerge early when all surviving Sons are banished. Core Hounds fake death at one health and can resurrect ten seconds later. Do not replace these details with generic guide values or kill-only completion assumptions.

The real client QA runner now uses explicit Recovery -> Preparation -> Ready -> PullPending -> Fighting -> Finished/Failed stages. `raidqa prepare` is inserted before instance entry in the recovery templates. `raidqa prepull` seals exact-roster/fresh-fact/full-health evidence. `raidqa begin` rechecks and records the boss identity/full health BEFORE sending GM-off, then waits for actual acknowledgment; this correctly includes natural aggro immediately after GM-off. `fight-only.protocol` and `resume-battle.protocol` now invoke begin instead of manually issuing GM-off. The driver checks the same boss GUID. `SendGmCommand` refuses further setup commands during accidental preparation combat or a sealed battle; explicit recovery remains authorized. Failure from this guard returns to the persistent inbox without accidentally advancing past it.

Every new labeled attempt writes `events.jsonl`, including readiness, command requests actually sent, the pull boundary and outcome. `audit_attempt.py` replays that plus battle samples; even a consistent death journal only yields a candidate requiring actual combat/server-log review. It cannot exclude outside RA interference. The legacy best Onyxia attempt replays as `failed-reset`, minimum 47%, first sampled casualty 120.256s, reset starting 240.456s. No synthetic fixture or replay output is combat acceptance.

Validation: client Debug and Release builds pass with the same 11 existing warnings; 113 planner/profile/wire/retry checks, 15 gameplay wiring contracts, eight Python behavioral fixtures plus actual-snapshot integration checks, and PossessLaw/SharedDocs/GameplayImguiPolicy pass. New readiness journaling and begin/GM-off sequencing still need live verification after the Core build. The separate Core possess-law shell script was previously found absent; this round does not claim it ran. ParticleRenderer.cs remains untouched by this work.

Character state was not changed in this continuation. Last actual live report remains all forty alive/out of combat at safe Northshire, plan Clear, Testwar in GM preparation mode; no local MSUIClient was running at the latest process inspection. Reinspect when resuming. First finish the pending Onyxia validation with the same roster; then implement/validate the listed reusable MC primitives and author live-tested encounter definitions, rather than treating this content review as a completed Molten Core clear.


## 2026-09-07: shared hazard guidance and durable battle observation

The deployed geometry correction improved the next real Onyxia attempt to 43%, with all forty alive until Testwar died near 246 seconds. His fixed air station remained inside a breath footprint; the old driver then incorrectly stopped when the boss left client visibility. Subsequent bot deaths mean this was not a clear. All forty were recovered outside and the client closed normally.

Protocol v4 now shares a generic complete-path escape solver between bot execution and read-only manual guidance. Actual cast time is distinct from effect persistence, and an expired warning does not force an actor to hold inside damage. The normal planner map displays the waypoint; only the explicitly enabled QA driver follows it automatically. Authoritative status includes boss health/life/combat and member life/combat so main death and stale bridge feeds cannot silently invalidate battle observation. Server source is already patched; Nico's build/install/restart is required before live v4 validation. The portable supplemental diff is `core-patches/commander-raid-guidance.patch`.

Debug/Release, 122 raid checks, 18 gameplay wiring contracts, eight Python behavioral checks plus actual snapshot integration, client possession/UI/docs laws, and 43 Core source contracts pass. Source checks do not compile Core or prove escape behavior. Exact evidence, recovered state and remaining generic mechanics are in the latest handoff. Onyxia remains incomplete and Molten Core is still a source/DB review, not a cleared raid.


## 2026-09-07: effective spell data and landing threat acquisition

Protocol-v4 guidance was deployed by Nico and passed real Apply/Arm. The next failure exposed the missing loaded spell overrides: Fireball adds Engulfing Flames through spell_effect_mod, and the raw-template audit missed it. The reusable pipeline now captures and applies spell/effect modifiers before trigger closure, resolves creature spell-list scripts in their own table, rejects incomplete snapshots and preserves override evidence. Twenty MC spell records change under those overrides; the generated MC review is refreshed from snapshot-v2.json.

Changing the authored spread trigger to castStart kept 36 actors alive through most of air and reached landing at 38.72%. Testwar had died; the backup tank remained at the hold anchor without acquiring the boss, and damage roles waited on threat. The encounter stopped and the raid eventually wiped. The new generic tank-acquisition source patch uses ordinary chase/rotation until contact and threat are established, then returns to the hold anchor. It is applied to source and requires Nico's build. The QA driver now stops acceptance immediately when Core stops the encounter, even after main death. All forty were recovered outside and the client closed. See the latest handoff for exact evidence and remaining spatial/role limitations. No Onyxia or MC clear is claimed.


## 2026-09-07: tank acquisition verified; reusable cone positioning awaits owner build

**No Onyxia clear and no Molten Core pull yet.** Nico deployed tank acquisition at 09:48:52 EDT (server process 2556345 started 09:48:55). Real attempt `scratch/onyxia-live/sep7-acquire-live-evidence/acquire-01` passed full-roster Apply/Arm and reached **34.43%**, then all forty died. Testwar was dead by the 20.171-second sample; backup 116 acquired the boss during phase one. The raid reached landing at 290.825 seconds, but casualties accelerated before the tank regained the boss late in phase three. The sealed audit is `scratch/raid-audit/acquire-01-review.json`. This verifies improved acquisition, not adequate landing survival; five-second samples do not prove a particular killing spell.

Recovery `sep7-acquire-live-evidence/recovery-acquire-01/recovered-outside.json` records all forty alive outside at map 0, plan Clear, with 38 observed full health. The local QA client PID 20368 closed normally. Testwar remains in GM preparation mode. Refresh live state, replenish, repair and recheck hunter ammunition before the next stage. Do not infer current full health from this older recovery report. Reuse the exact existing roster and persistent inbox workflow.

`core-patches/commander-raid-cone-positioning.patch` is **already applied to Core SOURCE**, and reverse-apply checking plus 52 source contracts pass. Nico must build/install/restart; the agent did none of those operations. Generic `avoidCones` rules use loaded target-family 24 radii and signed cone angles, current boss position/facing, bounded clearance and a shared complete-path escape search. Ordinary station movement cannot send a safe actor back into a forbidden sector. The current boss victim is exempt to avoid rotating the hazard across the raid while a tank acquires it. Safe actors resume their ordinary role. The Onyxia definition supplies spell IDs and ground phases; the executor contains no Onyxia identity/spell branches. Manual guidance remains read-only in normal gameplay. Client rule ordering now matches Core's stable priority indices.

Remaining limits: this is not a complete combined constraint solver; add chase and some spread paths still need integration, cached escape routes are not continuously recomputed at every facing change, and the ground cone rules currently cover healer/melee/ranged roles rather than add tanks. Wall geometry does not prevent the directly targeted threat-reducing Knock Away. Core compilation and these new live behaviors remain unverified. The client definition now includes `avoidCones`, so do not Apply it to the older running Core.

Typed received damage, periodic damage/heals, healing, reflection and environmental amounts are now logged by the client. `tools/encounter-content-audit/summarize_damage.py` scopes its report to the sealed attempt, reports incoming damage and boss damage by actor, healing by caster, and preceding damage around observed deaths. It explicitly distinguishes partial visibility, overheal and legacy missing amounts; it cannot certify a kill or prove a killing blow. The acquire-01 run predates these typed logs. The next built client includes them.

Validation for this source/client round: Debug and Release succeed with 11 existing warnings; 129 raid regressions, 19 gameplay source contracts, 12 Python behavioral fixtures plus actual snapshot-v2 modifier/trigger integration, and PossessLaw/SharedDocs/GameplayImguiPolicy pass. The separate Core possess-law shell script remains absent; it was not run. ParticleRenderer.cs retains SHA256 `3B203AAB6519D27DFC4521DE5E2B5A81ECA297D885FA58AC269F3E8B16B0E72A`. No commits, branches, server deployment or database restores were performed.

After Nico deploys the cone patch, reconnect the same 39 bots if needed, stage under Hold, inspect exact forty-character readiness, run a fresh sealed ordinary fight, and use the typed damage report alongside authoritative Core state and source/DB geometry. Continue toward Onyxia, then implement the remaining reusable MC capabilities and validate normal progression; the generated ten-boss MC review is not an executable or completed raid.


## 2026-09-07: cone patch deployed; live opening exposed human QA contact defect

Nico built/deployed cone positioning: binary 10:22:28 EDT, process 2561016 started 10:22:30. `sep7-cone-live-evidence/cone-01` passed actual Apply/Arm and forty fresh/full-health readiness. It failed at 98.75%: Testwar stayed at X34 while the boss attacked healers around X-35, and damage roles waited on tank threat. All forty died. This is not evidence that cone behavior solves landing. Audit and typed damage summaries are `scratch/raid-audit/cone-01-review.json` and `cone-01-damage.json`; 507 typed amount packets, zero legacy missing-amount packets, but no received death markers. Recorded incoming damage was dominated by Cleave 19983 (91006); cone attacks also hit actors. Partial packet totals do not prove individual killing blows.

The opted-in human test driver incorrectly always walked to TankAnchor, even when outside melee reach and without boss threat. Its movement now uses the tested `CommanderRaidAttemptLaw.TankMovementGoal`: approach actual melee contact, hold that contact until ordinary threat is acquired, then back to the authored station; knockback first triggers contact recovery. This changes the explicit QA driver, not normal human input. A session-local `abort-attempt.txt` now ends an observed fight as Failed and returns to the persistent inbox; it sends no administrative command. Queue explicit recovery afterward. This enables rapid abandonment of a diagnosed failed attempt without waiting fifteen minutes. No abort can award a clear.

Client Debug/Release, 133 raid checks including four contact regressions, 21 gameplay source contracts and possession/UI checks pass. Particle hash remains unchanged. Recovery `sep7-cone-live-evidence/recovery-cone-01/recovered-outside.json` verifies all forty alive/full/out of combat at map0, plan Clear. Client23952 closed normally. New corrected client30492 is staging `sep7-contact-live-evidence/contact-01`; inspect current files before proceeding. Core requires no additional build for this client-only correction.

Reusable content finding: loaded Cleave19983 is target6, damage class melee, chain target count10, no radius-index override. `Spell::SetTargetMap` derives radius from spell max range when radius index is zero. TARGET_UNIT_ENEMY's chain branch searches visible enemies around its target (unless CHAIN_FROM_CASTER), orders by proximity, enforces jump distance and LOS, and has no facing check. Treat this target family separately from target24 cone geometry; do not assume lateral positioning alone protects a group clustered around its victim. Full chain-spacing execution remains incomplete.


## 2026-09-07: confirmed account interruption and healer range recovery patch

`sep7-contact-live-evidence/contact-01` stopped during preparation when the combat guard rejected a summon. A later report showed no combat and a full-health boss, so the transient cause is unresolved; no pull was accepted. A separate full recovery/stage `contact-02` passed forty fresh/full readiness and actual Apply/Arm. Testwar acquired initial contact but died around 14 seconds; the 39 bots kept fighting under backup tank116 and reached air. The last authoritative sample was 105.306 seconds (about 61.14% boss health, 39 alive). The client then lost its socket and exited, with `bootstrap-20260907-104219.json` reporting NETWORK_FAILED. Nico confirmed that he logged into the same account at 10:42 EDT. Treat this as an interrupted attempt, not a decoder defect or a clear. Its old journal has no terminal entry, and tools that require a sealed terminal correctly refuse to certify it. Bots subsequently died after the owner session changed.

Measured opening failure: at ~9 seconds the ten healers were 59–86 yards from Testwar. Received packets show melee/Cleave damage and a 2907 Flame Breath at ~14.141 seconds, with no received heals to Testwar in that interval. This supports an out-of-range healing diagnosis, without treating partial packets as complete raid meters. The authored stations were in healing range, but nearby opportunistic heals could keep healers at distant staging positions rather than moving to their assigned patient.

`core-patches/commander-raid-healing-range.patch` is **ALREADY APPLIED TO CORE SOURCE**, reverse-check and 55 source contracts pass. It adds reusable assigned-patient range recovery before opportunistic healing. A dead/missing assigned patient falls back to the active living tank. Candidate endpoints require ordinary 38-yard distance, LOS and active cone clearance; every approach sample respects cone constraints before entering healing range. Floor projection is followed by another endpoint eligibility check. There is no boss-specific branch or wire change. Nico must build/install/restart; the agent did not compile or deploy Core. Actual rescue timing, landing behavior, simultaneous marked hazards, chain-spacing and mana sustain remain unverified/incomplete.

The client now writes a Failed terminal event if network/bootstrap termination interrupts a sealed fight and records actual combat body/objective GUIDs when the preparation guard blocks a command. Debug/Release pass with the same 11 warnings; 133 raid regressions, 23 gameplay source contracts and PossessLaw/SharedDocs/GameplayImguiPolicy pass. ParticleRenderer.cs hash remains unchanged. These diagnostics do not alter gameplay rules or fabricate a terminal entry in historical evidence.

Final recovery: `scratch/onyxia-live/sep7-contact-recovery-evidence/sep7-contact-recovery/recovered-outside.json` records all forty alive, 39 observed full health, none in combat, map0 and plan Clear0. Local recovery client23928 was closed normally. Recheck/replenish before staging; no local QA client should be running. Bounded read-only watchers21664 and20792 were started for the two real attempts and stop themselves after900 seconds; inspect their actual handles before any cleanup. Exact39 reuse and existing skills/specs/gear remain the authorized roster; pet abilities/autocast, poisons, coordinated buffs/curses and full MC role behavior remain unfinished.

An automatic approval review initially rejected transfer of the healer source diff. Read-only evidence confirmed the documented destination and an exact SHA256 match between the remote original and patch base; the source-only retry was approved and applied. This did not bypass a rejection via another channel and did not authorize any server build/deployment. No approval remains pending for that source edit.

Next: Nico builds/deploys the prepared healer patch. Verify the new binary/process, reconnect only the named bots if needed, use the Release launcher and a new labeled Stage under Hold, confirm exact full/fresh readiness, then run ordinary combat. Keep the account free for the QA client during the attempt. Full objective remains Onyxia then all Molten Core through reusable data-driven logic; best completed observation remains the earlier failed34.43% attempt, with no raid clear.


## Pet readiness continuation - 2026-09-07

The same four warlock and three hunter pets now have the reviewed high-rank abilities and autocast policy, verified from received pet spellbooks after normal disconnect/reconnect. `tools/encounter-content-audit/verify_pets.py` uses `plans/raid-pets.json` and records source hashes. All seven real profiles pass;14 Python behavioral checks plus actual world-snapshot integration pass. Imp Torment was removed; Blood Pact is enabled, trained Firebolt disabled for Onyxia. Hunter Bite/Claw/Dash are enabled, Cower disabled, and Fire Resistance/Great Stamina trained within normal Core checks. QA commands and successful casts alone do not certify persistence; the reconnected reports do. Happiness, feeding, buff coverage and combat pet control remain to verify.

The latest pet QA client changes built Debug/Release,27 gameplay contracts and possession/UI laws passed. Final preparation report has all40 alive/full, map0, out of combat, controlled787; status is null, not a fresh Clear acknowledgement. Client26004 was closed normally. See the latest handoff for exact evidence and limitations. The healer-range patch is still only applied to Core source; the installed binary remained10:22:28 at the11:37 check. Nico's build/install/restart is required before the next meaningful fight. No Onyxia or MC completion is claimed.


## Hunter happiness and retry readiness - 2026-09-07

All three hunter pets were discovered unhappy despite their trained spells. Ordinary Feed Pet6991 on actual carried Roasted Quail8952 raised them above900000 received happiness, verified by `plans/raid-hunter-feed.json` and `scratch/onyxia-live/sep7-hunter-feed-verified.json`. The bounded recovery helper waits without casting during combat and skips feeding when the reserve is already met; both behaviors were exercised live. The local Stage/Full recovery sequences include it before Preparation. Combined recovery/Apply/Arm still needs a fresh live pass after the pending owner healer-range Core deployment. Fifteen Python fixtures, actual world-snapshot integration,28 gameplay source contracts, client Debug/Release and possession/UI laws pass. The latest handoff records exact evidence, final safe40 state and client closure. No clear is claimed; happiness still decays and drops on death.

## 2026-09-07: stationary hazard work and combined positional constraints

Core and client now accept the generic `memberAura` trigger for `spread` with
`target: marked`. It enumerates actual living raid members carrying any named
aura; the carrier is excluded from its own exclusion circle. Onyxia data uses
20019, whose loaded periodic trigger20021 reaches5yards (DBC radius index8).
Carrier exclusion is6yards plus the solver's1yard margin. Initial Fireball uses
8yard splash (index14); the existing cast-warning spread remains12yards.

Active cast spread, observed aura carriers and authored breath footprints feed
one shared complete-path escape search for bots and manual guidance. Paths
cannot enter an initially safe footprint while leaving another. This remains a
conservative implementation, not proof of universal feasible routes or healing
coverage. Safe bodies may resume stationary casts/attacks; ordinary station
return, healer range recovery and add chase stay constrained while positional
hazards are active. These changes are built and deployed to the mission runtime;
the latest handoff identifies the current validation attempt. Neither raid is
cleared. Existing normal-rules evidence remains the acceptance requirement.


2026-09-07 live follow-up: combined footprint/member-aura positioning was tested
normally in position-06; wipe at37.19%, no clear. Priest idle wanding and later
heals were received. Loose whelps still overwhelmed healers. The paired Core
safe-add-protection patch permits complete hazard-safe melee approach paths,
lets ranged damage help encounter adds, and uses ordinary Fade against actual
add attackers.74source contracts and Corebuild pass; installed new runtime
2585449, first add-protection live attempt is pending. Full details/evidence in
COMMANDER_RAID_HANDOFF.md. MC requires authored target policies for encounters
whose adds must remain alive; do not equate this Onyxia add-damage policy with a
finished Molten Core executor.

## 2026-09-07 22:54 EDT: Onyxia live acceptance passed

Normal combat clear verified in consumables-01 under
scratch/onyxia-live/sep7-consumables-2241-evidence:562.522seconds,38survivors,
actualbosshealth0/dead andCoreState4, no sealed-combat setup commands. Separate
verified-clear.json references original journal/samples/audit/damage/terminal hashes.
Shared hazard reactions, normal healing/support and idle healer wanding ran together.
Normal carried healing/mana potions were used. Wand autorepeat continuity refinement
is built but not deployed; see latesthandoff. ContinueMoltenCore; noMCclear yet.

## 2026-09-07 23:28 EDT: paused for session transfer

Onyxia normal clear remains verified. Wand continuity is now deployed with counted
add objectives (Core2619166,b3ac94e2...). Both client configurations are built.
Molten Core has no pulls/clears. Full exactstate, validation, known solo-survey
assignment timeout and resume steps are in the LATEST CONTINUATION of
COMMANDER_RAID_HANDOFF.md. The owner requested a pause; do not treat this as missioncompletion.

### 2026-09-08 continuation: persistent carrier isolation and collision repair

Onyxia still requires three consecutive verified normal wins. The latest
configuration extends the existing marked `isolate` action to observed
`memberAura` triggers in both validators. A burning carrier now continues
separating from living peers after the Fireball cast-event window expires.
The ordinary executor already supports that action/target combination.
`core-patches/member-aura-isolation.patch` is the supplemental parser delta.
Breath forecasts have priority95, below confirmed breath100 and above
splash isolation90/91, based on the main-character deep-breath death in
onyxia-sweep-01. This configuration is deployed but not yet combat-accepted.
CharacterController now sweeps falling movement against steep solid faces
and slides downhill; the prior solver rejected them as standing ground and
could fall through Onyxia's wall. The actual-mesh regression changes four
failures to zero across 25 cases; normal movement clinical checks pass.
See the latest handoff for hashes, attempt journals and the current ledger.

### Shared support capacity reservations

An always support cast with a shared target may specify `reserveCasters` from
0 to8. A stable pool of living, learned, eligible bot casters retains that spell
for the higher-priority rule instead of spending it on lower-priority support.
Cooldowns and crowd control do not shuffle ownership; dead or missing members
are replaced. All ordinary spell guards remain. Both validators reject misuse
and accept older definitions without the optional field. Onyxia requests two
casters for tank Fear Ward following an observed coverage gap and raid-killing
boss turn. This is an encounter-agnostic execution primitive, currently deployed
and awaiting live acceptance. The supplemental portable patch is
core-patches/support-caster-reservations.patch.

### Shared forecast escape certification (2026-09-08)

The supplemental `core-patches/forecast-escape-certification.patch` replaces
blanket forecast exclusion where a conservative evacuation proof is available.
A bossNear footprint resolves its matching castStart footprint by the data's
point set, radius, active phase and toggle. Lead time comes from the effective
loaded spell's cast time on the caster. No boss or spell ID is in this policy.

The shared solver may choose space within a possible future footprint only if
the entire proposed movement plus a complete ordinary evacuation path fits
that lead time, less one second of shared reaction margin and observed/pending
movement-lock duration. Triggered aura effects are traversed with a bounded
cycle guard; confusion, fear, stun and root consume the movement budget. Missing
spells, unresolved script effects, unknown control durations and trigger cycles
retain conservative exclusion. This is a conservative subset of effect parsing,
not a complete expected-damage optimizer or arbitrary-script inference.

Evacuation checks reject incomplete and shortcut paths, sample room/current
hazard safety, and use actual path length and actor movement speed. Work is
bounded to64 extra navigation queries per solve,12 per exit search; failure
retains the existing full-footprint solver and explicitly compromised fallback.
Safe arrival permits authored support to resume. Manual safe hold is reported
as such only when the route result is not compromised. Runtime diagnostics
report certificate success, query count, control budget and elapsed microseconds.
The live acceptance and installed version are recorded in the handoff.

### Shared route integration after the third forecast attempt failed

`core-patches/certified-role-routes.patch` extends the same forecast certificate
to formation, melee/add approach, healing range, damage range, station recovery
and manual air positioning. Complete paths still avoid observed hazards; any
entry into a forecast needs the same bounded evacuation proof. A currently
certified hold precedes optional crowding optimization, letting ordinary healing
and combat resume instead of continuously interrupting casts to refine position.
Guidance also recognizes a certified current hold. The production-function tests
exercise the complete candidate/path search, combat approach, timing/control
rejection, observed hazards and current-safe hold despite cheaper crowding
elsewhere. This supersedes the previous limited integration note. Full live
acceptance remains pending in the new streak; no per-boss definition changed.


### September8: observed burst healing

Shared direct-cast triage reads current hostile explicit-target casts and actual
remaining countdown, estimates direct school damage using loaded values/bonuses,
and prioritizes timely healing before a dangerous hit. Late incoming-heal claims
do not hide the deficit. Ordinary emergency/efficiency ranking remains. No boss
or spell IDs are used. This is partial noncritical/pre-resistance prediction;
scripted/triggered and ground damage are not covered or certified harmless.
See core-patches/observed-burst-healing.patch and actual-function behavioral
fixtures check_core_burst_triage.py. New combat configuration requires a fresh
three-win streak; inspect latest handoff for deployment and live status.

### September 8: protective support priority and priest roster gate

Authored instant control immunities are recognized through loaded spell effect
metadata and attempted before beginning a new critical heal, with normal cast
constraints. A protected tank healer is chosen from assigned healers who can
cast the same protection when available, then by heal cast time; ordinary buff
target selection keeps the previous fastest-healer behavior. Busy states do not
rotate the chosen recipient. Actual-source fixture check_core_protection.py
covers priorities and boundary cases. This remains a heuristic based on authored
support intent, not a complete prediction of incoming attack risk.

Owner requires all six prepared priests to be dwarves and know Fear Ward.
All six already satisfy it; raid_session verifies fresh race and received spell
facts before each pull. These checks do not count as encounter success.


### September8: tank facing correction within melee reach

Wrong-side tank corrections now use bounded tangent steps around the target
instead of crossing through its center. The radius comes from ordinary melee
reach, and the axis from authored room/station data; already aligned contact
holds. The client QA driver and Core bot tank use the same geometry. Actual
repeated-step/rotated-layout tests verify center clearance and contact. This
reduces a source of boss pursuit and healer repositioning; live acceptance is
still pending. It does not add a complete manual path safety certificate.


### September8: one freshness boundary for manual guidance

The QA driver now shares the guidance law's2.5-second freshness boundary. A stale
status or rejected nonzero/unknown actor guidance holds pending fresh advice;
it cannot become permission to resume an ordinary station route. The previous
three-second driver gate left a half-second fallthrough window. The timing
contract checks the shared client boundary against Core's manual advice-age
allowance, and diagnostics record guidance waits. A hold on unknown data is
not a certificate that the current position is safe. Live acceptance pending.


### September8 follow-up: tank arc experiment reverted

The arc did not prevent knockback pursuit and has no accepted clear evidence.
It was narrowly reverted in client and Core for the next comparison. The earlier
legal-contact preservation and compact facing correction are current again;
shared guidance freshness and invalid-advice holds remain. See the latest
handoff for active binary identities and attempt state.


### September8: budget consumables use received classes and assigned roles

The profile generator and pre-pull validation now use actual class and role facts
instead of treating GUID ranges as classes. This corrected eight mages who had
been assigned agility elixirs. Current totals are three armor, ten intellect,
eleven agility and sixteen spell-damage elixirs, exactly one per character.
A mismatched profile or changed received class/role blocks the pull. The mistaken
preparation was not pulled; its eight wrong auras were removed during recovery.


### September8: shared urgent healing and ranged formation restoration

Urgent healing no longer lets a percentage-size cutoff force a slower heal when
a useful faster rank is available. Among spells that can beat an observed hit,
the selector first prefers enough healing to survive that hit with a small
margin; otherwise it favors earlier delivery and the largest useful rank at
that speed. Ordinary nonurgent adequacy and mana efficiency remain intact.
Actual production comparator regressions fail on the prior build and pass on
the new one, including late, timely, insufficient and fast-heal cases.

Ranged damage actors may restore crowded formation between casts while retaining
the current target's spell range, minimum range and line of sight. Every proposed
route retains the shared hazard and forecast certification. Failed optional
spreading permits ordinary damage; it does not create a stationary safety hold.
The production function and existing route/forecast fixtures pass.

These are encounter-agnostic execution changes. No encounter definition or boss
identifier branch was added. The healer-observation logging is retained. Live
acceptance is pending; the new configuration begins at0/3 Onyxia.
