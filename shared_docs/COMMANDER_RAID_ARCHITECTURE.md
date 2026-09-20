# Commander raid architecture

Owner acceptance rule,2026-09-10: **all encounters are accepted on a complete,
frozen ten-pull batch measuring at least8/10 after evidence review. No separate
3/3 or consecutive-win seal.** Onyxia's verified8/10 batch is accepted and the
mission proceeds to Molten Core. Prior-encounter regression still requires one
reviewed win after a stack change. STATE links the owner receipts.

Owner evening correction,2026-09-10: consumables are the permanent new baseline.
GM preparation restores buffs, vitals, cooldowns and appropriate role elixirs;
combat keeps normal costs, damage, cooldowns and carried potion use. All40 get
fire protection, food and role elixir; tanks also flask/Fortitude. No need to
simulate ordinary preparation. Hazard-refuge is owner-rejected; never resume it.

Retain a change on >=3 additional measured successes OR targeted submetric
(stall count, flight deaths, boss damage rate) improvement with rate not dropping.
A one-win difference alone is noise. Other casualty-family increases do not veto
retention: earliest casualty labels can shift when an earlier hazard is removed.
Report900s timeouts separately; under5% timeouts are near-kills counted in rate,
never actual reviewed kills. Score stall (>=60s flat boss HP
with >half raid alive), phase damage rates, flight deaths and landing HP. Preserve
old scores/reports before rescoring completed batches. The archived protocol4
client's900s deadline stays until protocol5. Acceptance requires>=8/10 in a complete frozen ten-pull batch; no extra streak.

Stable description of the raid system. Status, streaks, hashes and the next step
are NOT here; they live in `COMMANDER_RAID_STATE.md`, which is overwritten every
session. The original design record with its research basis is
`COMMANDER_RAID_PLAN.md`; the per-boss Molten Core review is `COMMANDER_MOLTEN_CORE.md`.

## Goal and the one rule

Clear the whole game with the owner as main tank and 39 bots, with **no
encounter-specific logic**. The rule, precisely:

- **Code** (client planner, Core executor, compiler) never contains a boss entry,
  a spell id, a coordinate or a boss name. A source check enforces this for the
  executor (`tools/commander-raid-source-check.py` on the Core side).
- **Data** is split in three kinds. Only the last one may be written by hand.
  1. *Derived*: everything the compiler produces from the loaded spell data, the
     boss script's own numbers and the world database.
  2. *Global policy*: `tools/encounter-content-audit/plans/compiler-policy.json`.
     One file, identical for every encounter (clearances, priorities, which
     mechanics to counter, which stat buffs a tank gets). Changing it changes every boss.
  3. *Survey*: `plans/surveys/<encounter>.json` with room bounds, tank anchor and
     team anchors, measured live.
  4. *Doctrine and roadmaps* (owner, 2026-09-13; `COMMANDER_RAID_DOCTRINE.md`):
     `plans/raid-doctrine.json` is global raiding practice in archetype terms
     (kill order summoner > healer > caster > ranged > melee, weakest first;
     control what dies last; patrols before the packs they pass; route
     distances). `plans/roadmaps/<map>.json` is per raid but only says the
     boss order, the entrance and what to skip. The compilers derive the rest:
     `archetypes.py` classifies templates from loaded spell data, the pack/boss
     compilers write `requiredAdds` in kill order (executor v28 focuses and
     claims crowd control by that order), `roadmap_compiler.py` derives the
     legs of ordered pull units and the instance gates.
  Nothing else is hand-authored.
- Per-encounter "intent" files are refused by the compiler. A strategy someone
  wrote for one boss, even as JSON, is encounter-specific logic. A roadmap that
  names a pack's tactic is one too; only order, entrance and skips belong there.

A batch counts toward acceptance only when the definition that fought was compiled
from kinds 1 to 3. The first Onyxia 3/3 (2026-09-09, `sep9-clean-assist-evidence`)
was real combat but used a hand-authored definition, so it is evidence that the
executor works, not that the generic system does.

## Objective handling (owner clarification, 2026-09-12)

Before a pack or boss enters acceptance testing, `tools/encounter-content-audit/objective_review.py`
produces its objective review from the frozen snapshot only (script source, instance EventAI,
effective spell data, spawn groups, respawn timers) into `core/commander-raid/objective-reviews/`:

1. **Success and failure as the game defines them** — instance state transitions (`SetData DONE`
   on death, `NOT_STARTED` on evade, EventAI command 37), script death/evade handlers, pack
   membership and same-entry mutual restoration.
2. **Objectives** — required units (the boss plus its aggro-together spawn group; every member of a
   pack), optional threats (script summons), units that must stay alive (friendly-surrender
   bosses), conditional damage restrictions (balance policy, surrender thresholds).
3. **Dependencies and transitions** — which entries stop respawning once an encounter is DONE,
   instance-wide gates (runes, Majordomo/Ragnaros progression), interactions.
4. **Capability coverage** — each hostile cast classified from its effects and mapped to a deployed
   generic mechanism, or marked **unsupported**.

Three things are kept separate and never conflated: *game completion* (what the instance records),
*tactical priorities* (what the raid does to survive; never a completion condition), and *QA
acceptance* (the frozen ten-pull >= 8/10 rule with completion proved from observed state and
provenance: executor state 4 plus native dead transitions of every required objective inside the
fight window; fake death, missing visibility, temporary control and expired tracking prove nothing).
Assumptions the review refuses: that every hostile must die, that the named boss is always the damage
target, that boss death always proves completion. An optional tactic is never turned into a compulsory
encounter sequence.

A known unsupported item that is a demonstrated requirement is resolved before acceptance testing —
by reusing an existing capability first, otherwise by ONE small generic mechanism (shared policy or a
reusable execution mechanism; never a hand-authored strategy, in JSON or in a narrowly tailored compiler
pattern). Attempts run before that are diagnostic and labelled so in their ledger notes. Accepted
evidence and the frozen-batch/regression rules are preserved throughout.

## Layers

| Layer | Where | What it owns |
|---|---|---|
| Client planner | `MSUIClient/Engine/UI/Commander*Law.cs`, `GameLoop/Panels/GameLoop.CommanderRaid.cs`, `GameLoop.PartyTactics.cs` | Roster facts, main role choice, Auto-assign (tank/healer/damage, teams, stations, patients), definition selection by map + boss entry (`CommanderEncounterSelectionLaw`), validation (`CommanderEncounterLaw.Validate`), Apply/Arm/Pause/Clear, guidance display. Gameplay UI uses vanilla primitives only. |
| Wire | `MSUIClient/Net/CommanderRaidWire.cs`, Core `SuiControl.h` | Opcodes 874/875, capability bit 13. Deployed protocol is **4**. The working-tree client is protocol **5** (candidate) and cannot talk to the deployed Core. Pair-deploy. |
| Core executor | `src/game/SuperUiContent/SuiWorld/CRPG/SuiCommanderRaid.cpp` (+ `SuiEncounterDefinition.*`) | Authority and leases, readiness, phase selection, rule priority, movement with navmesh/hazard/forecast certification, threat gating, add pickup, healing triage and support reservations, control dispel, the rule actions. Reads hazards from spell data (target positions, cone target 24 + radius, trigger chains, school masks). Zero boss identities. |
| Class policy | Core `AiBotAISpecCombat.cpp`, `CombatBotBaseAI.cpp` | How a spec performs its role (rotation, blessings, feign recovery). Class-specific, never boss-specific. |
| Definitions | `encounter-definitions/*.json` (schema 1, what the game loads) | See vocabulary below. Loaded from the repo directory at runtime; the client also embeds one default. |
| Compiler | `tools/encounter-content-audit/` | `harvest.py` (SELECT-only snapshot of Core source, DBC, DB) â†’ `definition_compiler.py` â†’ `core/commander-raid/compiled-definitions/*.json` (schema 2, full) â†’ `--promote` writes the schema-1 projection into `encounter-definitions/` after `validate_definition.py` (a line-for-line mirror of the deployed parser). |
| Checkpoint and QA | Core `src/game/Commands/RaidQaCheckpoint.cpp`, `RaidQaCommands.cpp` (canonical copies in `core-patches/`); client `raidqa` protocol (`GameLoop/Dev/GameLoop.CommanderRaidLiveQa.cs`) | `.group qacheckpoint capture\|restore\|restore-raid\|return\|normal\|bless\|feed LABEL`, `.group qarepair`, `.group qaelixirs`. Restores the exact 40 in place at the boss: positions, buffs, vitals, supplies, ammo, pets, repairs, cooldowns; resets the captured boss. |
| Regression runner | `tools/encounter-content-audit/raid_regression.py` | One command from nothing running to sealed fights (below). |

## Definition vocabulary (schema 1, deployed)

Identity and room: one boss entry, up to eight objectives, map, bounds â‰¤ 400 yd per
axis, tank anchor, 1..8 teams with anchors, add entries, required add counts, add policy.
Phases (â‰¤16): health interval, airborne flag, boss aura, priority, melee/ranged
permission, threat ratio; one unconditional fallback phase is mandatory.
Rules (â‰¤64): `action` âˆˆ avoidCones, avoidPoints, isolate, spread, stack, move,
stopDamage, cast; `trigger` âˆˆ always, castStart, castGo, bossAura, selfAura,
memberAura, bossNear; `target` âˆˆ self, boss, tank, marked, tankHealer; role mask,
phase, radius, duration, spell, point spells, station, toggle, reserveCasters.
Unknown words are rejected, never ignored. Schema 2 (candidate) adds `mechanics`
(crowd control, hazards, damage policies, interactions, completion) and `tuning`.

## What the compiler derives (all encounters, same code)

| Derived rule | From |
|---|---|
| Phases | Script `GetHealthPercent() < N` thresholds; `SetLevitate(true)` selects the airborne model (ground, transition, landed, airborne). |
| Cones | Boss spells with implicit target 24 and a radius â†’ `avoidCones` in every ground phase. |
| Point lanes | Boss spells whose trigger chain walks `spell_target_position` rows on the map â†’ `avoidPoints` on source-proven normal castStart; triggered/unproven casts use castGo (persistent area auras also use castGo with the loaded duration). |
| Flight stations | The script's move table pairing hover points with lane spells â†’ `bossNear` forecast rules. |
| Landing intercept | Fly-mode-only `MovePoint` destinations; the one nearest the surveyed tank anchor, offset toward it â†’ main-tank `move` in the airborne phase. |
| Airborne spread | Any airborne phase â†’ `spread` for all roles. |
| Targeted splash | Boss spell landing an enemy area effect at its victim â†’ `isolate` the marked target at the effect radius; the triggered aura is the impact aura. |
| Carried splash | An aura on a raid member that periodically triggers an area effect around them â†’ `memberAura isolate`. |
| Immunity support | Boss mechanics in the policy's counterable list (fear) â†’ `cast` rules for every learnable friendly immunity aura (tank with reserved casters, tank healer, healer self). |
| Tank buffs | Highest learnable friendly buff per policy stat (stamina) â†’ `cast` on the tank. |
| Adds | Spawn-group membership of the boss's spawns plus script `SummonCreature` arrays â†’ add entries and required counts. |

Triggered/dynamic source calls must not borrow a DBC cast-start warning. The compiler records call expressions, flags, lines and conservative file-scoped ambiguity in `laneCastTiming`; only a positively resolved normal cast can grant that warning. The deployed executor finds no forecast warning for a castGo-only lane.

The derivation report (`core/commander-raid/evidence/definition-derivation.json`)
classifies every leaf of every definition as source-or-database, policy-margin,
survey or schema default, and lists unresolved items. Ten Molten Core definitions
compile with `surveyRequired: true` and are not runnable until surveyed.

## Candidate hazard observations (protocol5)

The candidate uses actual object GUIDs and lifecycle for summoned traps and
persistent DynamicObjects, including loaded school, radius and remaining duration.
A spells-only hazard with followSource=true observes a real preparing cast at the
caster; cancellation or completion removes it. An entry plus aura observes the
unit while that aura exists. Loaded damaging periodic children provide their
actual next-tick deadline. A source-linked custom damage loop can provide the
aura footprint without inventing a deadline from an unrelated dummy aura.

Source links are restricted to the creature template's registered AI, a unique
boolean activation after the self-aura cast and its custom damage loop. The
compiler records these source lines and damage constants. Native escape advice
distinguishes a complete path that meets its observed deadline from best effort,
including movement speed, control lock, execution margin and manual advice age.
These candidate capabilities do not constitute a live encounter clear.

A hazard with both onDeath and followSource represents a source-proven terminal
burst: its living source is an exclusion region, and observed death removes it
immediately. Its duration is an observation lease, not an invented corpse damage
duration. Optional aura predicates apply to its lifecycle and melee exclusion.

Damage policies may set whileObjectiveAlive. This is derived from a registered
add AI cancelling incoming damage and restoring full health below a threshold,
with its completion-state guard linked to the primary AI's death callback.
The policy releases only when every declared primary is observed dead. Roles
preserve add-tank threat attacks; class casts, pets and area/chain attacks share
the same conditional guard. Independent aura/school policies still apply.

The original GameObject source of a direct source-area cast is recorded at
cast-go, including loaded school/radius and the actual scoped summoner GUID.
Script-created objects may have zero creation-spell id and zero respawn lifetime.
This observation proves cast emission, not damage received, and adds no future
hazard or post-impact movement for an instantaneous burst.

## Candidate add-distance constraints (protocol5)

Optional mechanics.addDistances identifies declared add subjects and encounter
references, minimum/maximum edge distances, 2D/3D semantics and an optional
reference aura. Zero maximum means no upper bound. Unknown fields, invalid
intervals and undeclared identities are refused by client, native and Python
parsers; a definition using these constraints cannot downgrade to schema1.

The compiler links registered AI distance checks to the primary AI's evade call.
Friendly-heal search distances come from loaded creature spell lists and the
captured native target dispatcher. Positive area damage/haste buffs require a
registered AI self-cast and loaded effect geometry. Global clearance is separate
from source distances; no per-boss stations or identities live in execution code.

The executor binds live GUIDs, actual bounding radii, reference aura and combat
state. Non-objective relation units have separate bounded observations and do
not become required kills. Add tanks reposition only while retaining victim
ownership. Candidate stations allow for uncertain enemy contact position and
require complete tank/NPC paths, hazard/forecast checks and leash compliance.
Each movement step retains present healer coverage. If no station is found,
normal stationary combat continues and a native state transition records the
obstruction. This is a positioning attempt with repeated live observations,
not a guarantee about future enemy movement or a live-clear claim.

## Candidate conditional summon waves (protocol5)

Optional mechanics.waves describes a declared add entry, source summon-attempt
count, primary protection aura and active/recovery countdowns. Zero timer means
unknown. These are source countdowns, not a fixed wall-clock cycle. Registered
AI helper loops over initialized position arrays supply counts; execution code
contains no copied spawn coordinates. Conditional waves never become
unconditional required kills.

Actual aura edges identify successive wave generations. Real GUID observations
retain alive, isolated, explicitly dead and unavailable states. Missing visibility
does not prove death or end an add phase; old GUIDs retain their original
generation. Reservations and phase counts deduplicate observed targets. Aura
removal permits early emergence, including with isolated living adds. Bounded
tracking pauses on overflow. These observations do not claim a live clear.

## Acceptance and evidence

- Iteration is measured by **win rate over a frozen stack**: a batch is 10 reviewed
  normal-combat pulls with no Core, client, definition or policy change between
  pulls, each pull scored (`score.json`: first death time/phase, tank contact-loss
  seconds, healer deaths <300s, terminal boss percent, failure family). At most one
  generic change is made per batch, targeting a supported failure mechanism, and it is
  judged by comparing the next batch's rate. A single loss never justifies a patch.
- A new target is **accepted** after a complete frozen ten-pull batch measures at
  least8/10 and its evidence is verified. There are no extra streak pulls. Every
  previously accepted encounter needs **one** reviewed win on the new configuration
  after any Core, client, definition or policy change.
- A session directory `scratch/onyxia-live/<session>-evidence/` holds one
  `streak-ledger.json` (linked to the previous ledger by path and hash, with the
  Core binary hash and the hash of every configuration file) and one directory per
  attempt: server checkpoint receipt, `normal-rules-before-pull.json` (native
  invincibility/cheat/priest/pet gate), readiness, `encounter.json` (the exact
  definition sent), `events.jsonl`, `battle.jsonl`, movement trace hash,
  `runner-audit.json`, `runner-damage.json`, the native server log through review,
  `review-candidate.json` and, if sealed, `reviewed-result.json` with the reviewer.
- A win requires the source-derived completion predicate with executor state4:
  physical death at0HP, or a separately proved living friendly surrender with all
  required add deaths. Surrender is never labeled a physical kill. The native
  normal-rules receipt must exist, the audit must have no errors, server commands
  during combat must be read-only normal checks, and all evidence/configuration
  hashes must match the ledger. Under5% timeout measurements remain separate from
  these actual reviewed outcomes.

## Startup and retry (temporary pre-fight setup, allowed by the owner 2026-09-09)

`raid_regression.py run` does, in order: launch the isolated QA client as Testwar;
teleport the owner into the saved instance; reconnect the exact39 through the web
app; native `return` + `restore` of the checkpoint (revive, full, repairs,
cooldowns, buffs, supplies, pets, boss reset); pet inspection, Hold, Auto-assign,
Apply, Arm, drive; fresh normal-rules gate; pull; watch; copy the native log;
build the review packet; seal; restore again for the next attempt. Two temporary
rules in the checkpoint command make any saved checkpoint reusable indefinitely:
cosmetic slots (shirt, tabard) do not decide gear identity, and every restored
buff is refreshed to its full duration. Both are setup-only and can be removed once
the mission is over.

The executor selects backup tanks from the complete eligible roster, with contact grace scoped to objective GUID and reset across non-melee phases, noncombat and re-arm. Selection does not itself prove threat recovery. Ordinary manual-tank hold guidance requires valid room bounds, actual melee reach and line of sight.

## Measurement tooling

Use `raid_regression.py run ... --attempts 10 --continue-on-failure --measure`
for rate measurement. Measure mode requires continue-on-failure and runs all
requested pulls even after three consecutive wins. It preserves ordinary win
review and frozen-stack checks. `batch-summary.json` records won/lost/rate,
score hashes, normal-combat verification and acceptance eligibility. Streaks are
descriptive and do not control acceptance.

`score_pull.py` reuses the loss/damage readers and writes `score.json` per pull.
It records first death time/phase, main-tank contact-loss seconds, healer deaths
before 300s, terminal boss percent, survivors and one failure family. Spell
families derive from compiled rules and loaded trigger chains. Stall labels identify sustained objective stagnation; earliest casualty context
is retained separately and does not prove the cause of a wipe.
Contact uses snapshot geometry because protocol4 lacks an authoritative contact/
LOS field; dead-main time, air phases and long sampling gaps are excluded.
Terminal survivor count is null when unavailable; last-sample count stays separate.

`report_batch.py` renders a completed batch and requires one evidenced proposal
for a largest observed failure family. Compare the next complete batch against
that baseline using the owner retention rule above; restore rejected changes.
`finish_measurement_session.py` recovers the exact raid, closes the recorded QA
client and logs out the exact39, requiring native0p+0b and saving its receipts.
A measurement batch is not finished operationally until this clean boundary and
the report/STATE/resume are saved.

## Known gaps

- Protocol5/schema2 integration is under way in the merged candidate linked from
  STATE. It retains accepted Onyxia behavior and integrates native support, CC,
  object/area/cast hazards and non-death completion. Remaining MC requirements,
  full pair deployment and live validation are incomplete. The older canonical
  core/commander-raid executor must not overwrite that merged candidate.
- Tuning:53 named fields exist; the literal inventory remains incomplete. Future
  schema2 ledgers freeze the manifest and effective values. tuning_evidence.py
  compares the loaded client values and this attempt's native Apply log, and the
  review seals both observations. Historical ledgers are never backfilled.
- The regression matrix is one runner invocation per encounter, not yet an
  automatic fan-out over every completed checkpoint after a change.
- Old `check_core_*.py` source-contains checks still exist beside the behavioral
  geometry/primitive suites and should be retired as those suites cover them.
