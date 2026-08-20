# Encounter Lab — architecture & handbook (built 2026-08-16 · raid sim 2026-08-17 · game-faithful pass 2026-08-17)

> **STATUS (core + raid sim): BUILT & VERIFIED, HEADLESSLY *AND* IN-CLIENT.**
> `tools/encounter-lab-check`: **126/126 offline** (re-run 2026-08-20; live
> world-DB integration remains opt-in). The in-client scripted probe
> (`MSUI_ENCLAB_PROBE`, §11): **11/11** — raid placement, live world, staging,
> GO, roam, Ctrl+F — measured in the real client on the real code paths, with
> screenshots. Owner-verified eyes-on in the lair the same day. No server
> changes, no deploy, no core work — the Lab reads the world DB through
> MangosSuperUI's **existing** CSV export and runs entirely client-side.
>
> **STATUS (later 2026-08-17 game-faithful pass, §8.1–8.5): BUILT, COMPILES
> CLEAN (Debug + Release), NOT YET RE-VERIFIED.** Stand-till-pull, clock-holds,
> SmoothDamp movement, the orientation ring, role colours, boss health bars,
> attack animations and **real spell visuals** (the boss driven through the live
> `ApplySpellGo` pipeline), and the pop-out action panel are all in code. The
> probe's assertions were rewritten to the new behaviour (world *holds* pre-pull
> instead of running live; boss *stands* instead of roaming) but the probe has
> **not been re-run** this pass, and owner eyes-on is pending. Per the
> docs-oversell rule (§11), treat §8.1–8.5 as "written, not yet proven in-client".

**What it is:** press **Ctrl+E** (live mode or creator mode) for a window that
loads an NPC's combat behaviour as a **declarative encounter definition**, runs it
as a **deterministic fixed-step simulation**, and lets you **play, pause,
single-step, scrub, rewind and re-seed** it while drawing every ability's
footprint on the real terrain. Drop a **body capsule** anywhere — with a
trajectory, if you like — and it answers *what can hit this body, when, and why*.
Every fact carries a **fidelity label**, so a shape you cannot trust never looks
like one you can.

**Since 2026-08-17 it is also a raid sim (§8):** a **ten-body raid with jobs**
(2 tank / 2 heal / 3 melee / 3 ranged) is placed at your feet and commanded
RTS-style from the **Ctrl+F** free view: shift-click queues waypoints per body,
the dotted plan draws on the floor, **GO** sends everyone at once, and the fight
**starts by proximity pull** — she stands at spawn until a body crosses her ring,
and the **fight clock holds at zero until then**. Any paused instant accepts a
**teleport what-if** that reflows the whole future while leaving every event
before the edit bit-identical. Nobody dies — bodies count hits, never health.

**And since a later 2026-08-17 pass (§8.1–8.5)** the Lab renders the encounter
the way the game does: the boss plays each ability's **real spell visual** (cast
animation, kit FX, flying fireball, impacts) through the live cast pipeline;
in-reach bodies swing and hit bodies take the real impact reaction; a **health
bar** rides the boss; movement is smoothed so a chase reads as running, not
stutter; waypoints carry a **ground orientation ring** you spin; plan lines are
**role-coloured** (tank gold / healer green / dps cream / boss red); and a pop-out
**action panel** steps through exactly what the selected body is doing in real
time. These are the *extras on top of a faithful game sim* — that framing is the
owner's, and the spec §8.1–8.5 builds to.

---

## 1. The problem this solves, and the decision behind it

Onyxia's behaviour lives in 924 lines of compiled C++ (`boss_onyxia.cpp`). The
naive readings of "make it visualisable" both fail:

| Idea | Why it fails |
|---|---|
| Pause and rewind the live realm | `mangosd` cannot rewind. Its world update is a real-time loop over mutable global state — grids, threat tables, spell queues, aura lists, RNG — with no snapshot. This is not a property of it being *live*; it is a property of its *architecture*. |
| Port the C++ into the client and run it | The script calls `Creature`, `Unit`, `MotionMaster`, `Map`, `ObjectMgr`, `Spell`, grid queries, `ScriptedInstance`. Pulling the script pulls the core. That is not embedding a script, it is embedding mangosd. |

**So the Lab reimplements the one shape that matters** — a fixed-step `UpdateAI`
over a small, snapshottable state — and gets determinism, pause, rewind,
branching and what-if for free.

And the load-bearing decision, the one to defend hardest:

> **An encounter definition is DATA, never code.**

The moment behaviour is expressed in a real programming language it becomes
opaque to the tool meant to visualise it, and you are back to reverse-engineering
compiled logic. Everything in the format is statically analyzable, which is the
only reason the client can answer "what would land here" *without running
anything*. When the format cannot express a behaviour, it does **not** get an
escape hatch into a scripting language — it gets an ability marked
`unknown-unmodeled` and a visible hole in the coverage report. **A visible hole
beats a confident lie.**

This is what makes it repeatable for encounters that do not exist yet: authoring
a new boss as data means the tool understands it for free, forever, with no
per-boss work.

---

## 2. Layers — where logic is allowed to live

Built to `CODE_STRUCTURE_LAW.md`. The one-direction rule holds with no
exceptions: **nothing in `World/Encounters/` or `Net/` references `GameLoop`.**

```
┌─ SIMULATION SUBSYSTEM (pure; no GL, no ImGui, no GameLoop, no I/O) ────────┐
│ World/Encounters/EncounterDefinition.cs  the schema + fidelity registry    │
│ World/Encounters/EncounterGeometry.cs    footprints + capsule hit tests    │
│ World/Encounters/EncounterSim.cs         fixed-step machine, seeded RNG,   │
│                                          snapshot ring                     │
│ World/Encounters/EncounterProbe.cs       trajectories + the "why" report   │
│ World/Encounters/EncounterTranslator.cs  world-DB rows → definition        │
│ World/Encounters/EncounterLibrary.cs     JSON file format (DTO ⇄ model)    │
│ World/Encounters/EncounterSpellFacts.cs  the only file touching Formats/Net│
├─ DATA LAYER (HTTP + parsing + cache; immutable publish, background Task) ──┤
│ Net/EncounterWorldData.cs                row model + published snapshot     │
│ Net/EncounterDataClient.cs               6 tables over the CSV export       │
├─ CONTROL / UI LAYER (GameLoop partials) ──────────────────────────────────┤
│ GameLoop/Dev/GameLoop.EncounterLab.cs          window, transport, sections,│
│                                                staged orders, playbook     │
│ GameLoop/Dev/GameLoop.EncounterLab.Overlays.cs 3-D decals + screen pass    │
│ GameLoop/Dev/GameLoop.EncounterLab.Puppets.cs  rendered raid/boss models   │
│ GameLoop/Dev/GameLoop.EncounterLab.Rts.cs      Ctrl+F select/order bridge  │
│ GameLoop/Dev/GameLoop.EncounterLab.Tape.cs     live recorder + diff        │
│ GameLoop/Dev/GameLoop.EncounterLab.Probe.cs    MSUI_ENCLAB_PROBE self-test │
└───────────────────────────────────────────────────────────────────────────┘
```

**Nothing is shared with the NPC dev window.** Separate data client, separate
settings block, separate files. That window is *spatial and static* (spawns,
routes, aggro radii); this one is *temporal and dynamic*. Neither can break the
other. They share only the creator chrome, `SpellEffectMeshRenderer`, and
`DrawDashedLine`.

**Threading contract** (same as the NPC dev window's, and just as binding):
`EncounterDataClient` fetches and parses on a background `Task` and publishes ONE
immutable `EncounterWorldData` through a volatile field. Nothing background-side
touches `EntityStore`, `Settings`, ImGui or GL.

---

## 3. The encounter definition format

`encounters/*.json`, schema version 1. A future `schemaVersion` is **rejected with
an error**, never parsed into nonsense. One bad document never takes the library
down.

```
EncounterDefinition
├── key, name, primaryEntry, memberEntries[], mapIds[]
├── provenance   { source, coreBuildHash, dbRevision, contentPatch }
├── coverage     source flags — NEVER a completeness percentage
├── actors[]     boss / add / friendly — radius, reach, job, speeds,
│                idle movement, timed move orders (see 3.3)
├── phases[]
│   ├── casterFlying, meleeEnabled
│   ├── onEnter[]      steps
│   └── transitions[]  { toPhase, trigger, fidelity, steps[] }   ← choreography
└── abilities[]
    ├── trigger   Timer | HealthBelow | OnAggro | OnPhaseEnter | OnMovementDone | …
    ├── timing    initial/repeat min+max in ms — a RANGE, because urand() is a range
    ├── target    CurrentVictim | RandomHostile | Self | DatabaseLocation | …
    ├── geometry  Circle | Cone | Line | PointChain | Projectile | None
    ├── fidelity  the registry below
    ├── steps[]   optional choreography
    └── sources[] where to go and check
```

### 3.1 The fidelity registry

Taken **verbatim** from `docs/plans/DYNAMIC_COMBAT_RULES_AND_ENCOUNTER_INTELLIGENCE.md`
§11.3 rather than inventing parallel vocabulary:

| Label | Meaning |
|---|---|
| `exact-db` | Straight out of a world-DB table. Reproducible exactly. |
| `declared-cpp-manifest` | Transcribed from compiled C++ by a reviewed manifest. Stale the moment the core commit moves — which is why definitions carry `coreBuildHash`. |
| `derived-dbc` | Derived from spell data: radii, cones, cast times, missile speed. |
| `heuristic` | An informed guess; scaffolded, not reviewed. |
| `unknown-unmodeled` | Known to exist, deliberately not modeled. Draws as a hole. |

Fidelity is **never averaged**. An ability whose timing is exact but whose target
mapping needs a threat table is `heuristic` overall, because the weakest fact
governs. `WorstFidelity()` reports the weakest fact in the whole document and the
window prints it. Footprint **colour is fidelity** in every overlay.

### 3.2 Choreography steps

A deliberate subset of the core's own `SCRIPT_COMMAND_*` vocabulary — `Wait`,
`MoveTo`, `Cast`, `SetFlying`, `SetSpeed`, `Say`, `Summon`, `SetPhase`,
`DespawnSummons`, `Unmodeled` — so a sequence authored here stays translatable
into DB script rows later instead of becoming a private dialect.

Transitions own choreography because that is exactly what EventAI cannot express
today, and it is what makes a boss a boss: Onyxia's takeoff is nine ordered beats,
not one action. **A running sequence blocks ability timers**, reproducing the
real script's `m_bTransition` early-return — the difference between a believable
transition and a boss that cleaves mid-takeoff.

### 3.3 Actors, jobs, idle movement, and orders (added 2026-08-17)

All additive; the DTO layer is tolerant, so schema stays **version 1** and every
older document parses with defaults.

| Actor field | Meaning |
|---|---|
| `job` | `Tank` \| `Healer` \| `Melee` \| `Ranged` \| `None`. Drives the playbook and the melee-reach dps gate — never invents numbers. |
| `runSpeedYdPerSec` / `walkSpeedYdPerSec` | The template's real speeds (`speed_run × 7`, `speed_walk × 2.5`). Onyxia: **9.0 / 2.5**. 0 = engine defaults. |
| `detectionRangeYards` | `creature_template.detection_range`, display-only honesty beside the pull-ring slider (the core adds a level delta). Onyxia: **20**. |
| `idleMovement` | What the spawn's DB row says she does out of combat: `Stationary` (an ANSWER, not an absence — Onyxia guid 47572: `movement_type 0`, `wander_distance 0`), `Wander` (+`wanderYards`), or `Waypoints` (+`points[{position, waitMs}]`, the `creature_movement` replay — looped, exact pauses, walk speed). A declared route always plays; when the answer is "stationary" the Lab roams her anyway as an explicitly-labelled sandbox behaviour (§8). |
| `dps` | Owner-set damage per second — an INPUT to the plan, never a simulated outcome. |

| Move order (`moves[]` entry) | Meaning |
|---|---|
| `anchor: AtTime` | The original verb: at `timeMs`, start running there. Travel is paid at run speed — a late order visibly costs the trip. |
| `anchor: AfterPrevious` | Chain leg: fires when the previous entry's run arrives. Shift-click chains author these. |
| `anchor: OnPhaseEnter` + `phaseKey` | Fires the instant the fight enters that phase — "when she lifts off, go here". Dispatched from `EnterPhase`, no step lag. |
| `arrivalFacing` | Radians; the body pivots to it on arrival (the tank's back to the wall). Null/NaN = keep the run's facing. |
| `teleport: true` | The paused what-if verb: the body IS there at that instant, no travel. History before the edit stays identical; the future reflows. |

The list keeps **authored order**, not time order — chains and phase orders have
no meaningful timestamp to sort by. Firing any order retires every unfired
`AfterPrevious` leg before it, so a fresh order cancels the stale remainder of the
route it replaces instead of that route resuming underneath it.

---

## 4. The simulator

Two decisions carry the whole thing:

1. **Fixed step.** `Advance()` never reads a clock. Same inputs, same output.
   `UpdateEncounterLab(dt)` is the *only* place wall clock appears, and it decides
   only *how many* steps to take, never how big. A scrub at 200 fps and one at
   20 fps give the same fight.
2. **Seeded RNG.** Every `urand()` the real script would roll becomes a draw from
   a seeded xorshift128 stream. **A seed names a fight, forever.** Deliberately
   *not* `System.Random`, whose sequence is an implementation detail that has
   changed between .NET versions.

State is small enough to snapshot **every single step** — a boss is a phase, a
dozen timers and a handful of actors — so **rewind is a list index, not a
re-simulation**. Onyxia's full 166-second fight is 1 666 snapshots and 267 events.

Health is driven by a labelled `raid dps` dial (a fraction of max health per
second) **plus each body's owner-set dps**, purely so health-gated phases are
reachable, or pinned directly. It is never presented as a damage model. With the
**melee-reach gate** on (default), tank/melee body dps counts only beside a
grounded boss — an air phase honestly stalls her health gates.

**Movement & aggro (2026-08-17).** The pull is **authored, never assumed**: she
idles until a Friendly body crosses `PullRangeYards` of her live position; an
empty scenario idles forever (the old fight-at-t=0 shortcut for empty scenarios
made a fixtureless boss run her script against nobody, and died for it). Aggro is
an **owner-assigned timed plan** (`TimedAggro`), never inferred — she faces the
holder continuously, and post-pull, grounded and unscripted, she **chases** the
holder to melee reach at her real run speed: a tank at the back wall drags her,
and every cone comes along. Friendlies run their ordered moves (§3.3), and a
**playbook** of per-(phase × job) standing orders — `Hold` / `ChaseBoss` /
`MoveToSpot` — answers "what does melee do when she lifts off" as data. An
explicit order takes a body off autopilot until the next phase turn re-applies
its directive. Pre-pull the boss plays her declared idle movement, or the sandbox
roam — which draws from its **own seeded stream**, so however long the roam runs,
the fight's ability rolls at the pull are untouched.

Each Friendly can also carry serialized `EncounterPlayerRules` with a reusable
`CombatPlan` (§8.7). Its facing doctrine is applied after both the player and
primary target move on every fixed step. A tank can therefore run through an
encounter target toward the back wall while keeping its pose aimed at the live
target instead of snapping to its travel direction. Legacy `AlwaysFaceBoss`
remains the no-plan compatibility input.

**What the simulator does not have, and says so:** no threat MODEL (aggro is
owner-assigned; the fallback victim is "closest friendly", and any ability
needing threat order drops to `heuristic`), no aura system, no GCD, no resists,
no friendly health/healing model (friendly bodies count `HitsTaken` and never die —
by design), no line-of-sight or pathfinding for actor movement.

---

## 5. Geometry — a position is a body, not a point

Whether something lands depends on the body's **width and height**, the caster's
**facing**, and where the body is **at impact** rather than at cast. So every test
takes a `BodyCapsule` (feet, radius, height) and footprints are evaluated at their
impact moment, with travelling effects re-resolved against the world as it is when
they land.

| Kind | Test |
|---|---|
| `Circle` | ground distance ≤ radius + body radius, with a vertical band |
| `Cone` | distance **and** arc, where the body's width buys angular slack that grows as it closes |
| `Line` | swept capsule: point-to-segment distance ≤ half-width + body radius |
| `PointChain` | covered by any sphere in the chain |
| `Projectile` | circle at the impact point |

**The sign convention is load-bearing.** `spell_cone.cone_degrees` stores rear
arcs as **negative** — Tail Sweep is `-120`. The renderer flips the arc's centre
line to the caster's back for a negative cone, because "do not stand behind her"
is the entire lesson of that ability and a front-facing wedge would teach the
opposite.

Verticality is not skipped: Onyxia hovers ~22 yd above the floor in phase 2, and a
body far above or below an effect plane returns `WrongHeight`, not a hit.

The probe reports **why not** as well as why — `outside the arc by 8 deg`,
`3.2 yd outside the radius` — because a report that only lists hits never teaches
you where the safe line is.

`StructuralThreats()` answers the different, better question: *can this ever reach
me here*, ignoring timing. One seeded run is one roll of the dice.

---

## 6. Where behaviour actually comes from

Three tiers on this world DB, and the Lab reads all three:

| Tier | Count | Handling |
|---|---|---|
| `creature_spells` | 2 595 spell lists | **exact-db.** Slots → timed abilities. |
| `creature_ai_events` + `creature_ai_scripts` | 3 470 creatures / 6 201 scripts | **exact-db** triggers; EventAI's inverse phase mask is modelled faithfully. |
| compiled C++ (`script_name`) | 725 creatures | **A declared hole.** `"Scripted behaviour exists in compiled C++; this encounter is not fully modeled."` |

`EncounterTranslator` is written once and covers ~6 000 creatures. That ratio is
the whole argument for authoring new encounters as data.

### 6.1 Traps, all of them verified against the live DB

1. **THE APOSTROPHE TRAP (found by the live test, and it bites elsewhere).**
   MangosSuperUI's CSV export writes negative numbers with a **leading apostrophe**
   — `'-215.238`, `'-120` — the Excel "treat as text" escape. A plain
   `float.TryParse` rejects it and falls back to the default, so **every negative
   coordinate and every rear arc silently reads as ZERO**. Onyxia's breath lanes
   collapse onto the origin; Tail Sweep's rear arc becomes nothing. `CsvRow.Raw`
   strips it before parsing. Before the fix the north-south lane measured 20.7 yd;
   after, 89.6 yd — the real chamber.
2. **THE UNITS TRAP.** `creature_spells` delays are stored in **SECONDS** and
   multiplied by `IN_MILLISECONDS` in `ObjectMgr::LoadCreatureSpells`, while
   `creature_ai_events` params are **already milliseconds**. Read raw, every
   DB-driven creature appears to cast 1000× too fast. Everything published from
   `Net/EncounterWorldData.cs` is normalised to ms.
3. **Server truth ≠ client truth.** Cone arcs (`spell_cone`) and literal landing
   coordinates (`spell_target_position`) exist **only** server-side; no Spell.dbc
   field carries them. `EncounterSpellFacts` consults both sources, DB winning
   where they overlap.
4. **`spell_template` is multi-build** (4222 / 4449 / 5302 / 5875) and
   `spell_target_position` rows are build-ranged. The parser filters to build 5875.
   This is a *different* trap from `creature_template.patch`.
5. **`urand` is a range.** Timings are stored and displayed as bands, never as
   fake-precise single beats.

---

## 7. Onyxia — the spec fuzz, and the gap list

`encounters/onyxia.json` is a full transcription of `boss_onyxia.cpp`: three
phases, the nine-beat takeoff and landing choreography, all five phase-1/3
abilities, phase-2 flight, whelp waves, and **all eight breath lanes resolved from
`spell_target_position`** (84 points, verified against the live DB).

Its more important job was to **enumerate what the format cannot say**. Each gap
below is a named, additive change — to the format, the simulator, or eventually
the core:

| Gap | What the C++ does | Why the format cannot say it |
|---|---|---|
| **GAP-1** | Deep Breath picks its lane from the flight waypoint she is parked on; whelps pick one of two egg pits with `urand(0,1)` | No **variant selection by state**. Modelled with a representative lane + all 8 catalogued as `Manual`. |
| **GAP-2** | Wing Buffet / Knock Away only fire `if CanReachWithMeleeAutoAttack(victim)`, and do not reset their timer otherwise | No **predicate gating** on an ability. |
| **GAP-3** | `DelayCastEvents(2000)` — one cast pushes every *other* timer out | No **cross-ability timer coupling**. The sim bunches casts the real fight spaces out. |
| **GAP-4** | Knock Away −25% threat, Fireball −100%, `DoResetThreat()` on landing | **No threat model at all.** |
| **GAP-5** | The eight-waypoint ring walk: 35% clockwise, 35% counter-clockwise, 30% opposite-with-breath | No **stateful graph traversal**; `MoveTo` is a literal point. |
| **GAP-6** | Whelp waves: two per second from two pits until 16, then `5 + urand(0,2)` after 30 s | `Summon` is a one-shot, not a **state machine**. |
| **GAP-7** | The landing pad branches on `GetPositionX() < -40` | No **conditional step**. |
| **GAP-8** | Evade at `x < -95`; yank an out-of-reach victim to the chamber centre | No **zone predicates**. |
| **GAP-9** | `SetData(DATA_ONYXIA_EVENT, …)` | No **instance/encounter-wide state**. |
| **GAP-10** | `isOnyxiaFlying()` reads `HasAura(SPELL_HOVER)` | No **aura state**; flying is a phase flag, which happens to be enough here. |

That list is the deliverable. Whatever an encounter-authoring format eventually
becomes, these ten are what it has to answer for — and **GAP-1, GAP-2 and GAP-7
(variant selection, predicate gating, conditional steps) are the three that would
unlock the most legacy bosses**.

---

## 8. The raid sim — a commandable, rewindable raid (2026-08-17)

The owner's workflow, verbatim, is the spec: *flag her on and she paths like the
game; a 10-man spawns at my feet up the walkway; Ctrl+F into RTS view; queue
every bot's waypoints; GO sends them; the pull starts the fight; then pause,
rewind, reposition a body and watch where her damage lands instead.* Everything
below exists to make that sentence literally true, and all of it rides the same
two §4 decisions — fixed step + seeded RNG. **Every edit is a full deterministic
re-run** (a couple thousand snapshots, one frame), with the view restored to the
same millisecond: history before the edit is bit-identical *by construction*
(probe-verified: 42 = 42 events before a mid-fight teleport), and the future
honestly reflows.

**The live room.** Loading a document presses Play, but **the clock holds until
the pull** (§8.1). With no body in her ring there is no fight, so playback does
not advance and she **stands at spawn** — the exact-db truth (movement_type 0).
Pre-pull is a frozen setup, not a running timer: order a body across her ring and
the walk-in plays, the pull stamps the clock's zero, and it counts from there.
(An earlier build auto-ran a pre-pull roam; the owner's rule is that the timer
must not move before the pull, so the room now holds and the sandbox roam is an
explicit opt-in — §8.1.) Pause and the scrub bar still do what they always did.
Documents' Friendly fixture actors are **not** loaded (they stood inside her
ring and pulled her the instant the world went live); the only raid is the one
the owner places.

**The raid.** *Place raid (10)* forms 2 tanks / 2 healers / 3 melee / 3 ranged
**at the player** (creator sandbox included — `DevPlayerPosition` is
offline-aware), facing the boss, tank 1 holding aggro, each body's Z from a
**collision-world raycast** (WMO floors; bare terrain sampling sank half a raid
through the lair floor). Bodies render as real character models (puppets) with
per-look weapons; puppet guids are **stable per body key across rebuilds** so a
selection survives the very edit it just ordered; puppets follow sim positions
with a **critically-damped SmoothDamp** (§8.1) — an exponential lerp hid the
0.25 yd roam steps but not a chase's 0.9 yd/step, which stuttered; SmoothDamp
carries velocity so a chase reads as running.

**Staged orders — queue, read, GO.** Pre-pull, every non-teleport click on a
selected body QUEUES. Nothing moves. Set every bot's waypoints, read the dotted
cyan plan on the floor (numbered legs, facing ticks), then **GO** — a green
button that appears with the staged count (Play does the same) — and the whole
raid moves at once: first legs fire at the current instant, chains run
`AfterPrevious`. Once the fight is engaged at the view, clicks become
**immediate** timed moves — mid-fight you are editing the timeline, not queueing
a plan — and `Ctrl+RClick` teleport is always immediate: that is the paused
what-if ("if he stood HERE at this exact moment"), the question the whole
machine exists to answer.

**The command deck (Ctrl+F).** Works offline — in the creator sandbox the free
view is purely a camera decision (seat the rig, exact pose restore on exit; the
live path's server release handshake is not consulted). Raid puppets are picked
and marquee-selected like any RTS unit; a selection carrying puppets routes to
the sim, never to `SuiOrder`, so server bots are untouched.

| Gesture (free view) | Effect |
|---|---|
| Left-click a body / drag a box | Select / multi-select (never take-command — there is no character behind a puppet) |
| **Shift+Click** ground (left or right) | **Stage a waypoint** for the selection; repeat to chain legs; multi-selections fan around the point |
| **Ctrl+Z** or **Undo** in Transport | Remove the last staged waypoint gesture; a waypoint placed for a multi-selection is undone for the whole selection |
| **GO** (Transport) or Play | Commit the whole staged plan; everyone moves at once |
| Plain RClick ground *(engaged only)* | Immediate walk order at the scrub instant — travel paid |
| **Ctrl+RClick** ground | **Teleport what-if** at the scrub instant — always immediate |
| **Alt+RClick** | Arrival facing for the last order (staged leg first): "…and then face this way" |
| **Shift+RClick a waypoint DOT** | **Grab it to orient** (§8.2) — then move the mouse and its ground facing ring spins freely to any angle, click to set. On empty ground Shift+RClick still stages/chains; only a dot hit orients. |
| Ctrl+F again | Land exactly where you detached |

**Two design rules, learned the expensive way.** *One:* the default is whatever
the owner asked to SEE, and a knob may not silently override it. This cut both
ways in one day. First: an early roam on/off toggle whose persisted `false`
pinned her motionless cost three rounds of "still broken", so for a while roam
had no switch and was forced on. Then the owner decided the opposite — she must
**stand** until pulled — so the forced roam became the *bug*, stationary became
the default, and the sandbox roam a clearly-labelled opt-in checkbox (§8.1). The
invariant is not "always roam" or "never roam"; it is "the default is what the
owner wants on screen, changeable but never silently overridden". *Two:* claims
about GameLoop behaviour are proven by the probe (§11), not narrated — the
headless suite proves sim math, and every genuinely lost hour of 2026-08-17 lived
in the gap between the two.

---

## 8.1 The game-faithful pass — movement & timing (2026-08-17, pending eyes-on)

The owner's framing: *"this must SIMULATE the game environment + adds all the
extras we have on top."* Everything in §8.1–8.5 serves that — a faithful combat
picture with the analysis overlay riding it, not a schematic with labels.

- **Selected-unit paths persist off-screen.** `Camera.TryWorldToScreen` returned
  false for any point outside the viewport rect even though the pixel was valid,
  and every overlay path loop treated false as "cut the line" — so flying the free
  camera made a unit's dotted plan vanish the instant a waypoint left the frame.
  New `Camera.TryProjectToScreen(world, size, out pixel, out onScreen)` succeeds
  for **any point in front of the camera**; `TryWorldToScreen` now delegates to it
  (`&& onScreen`) so single marks/labels/selection are byte-identical. The overlay
  path loops use the new call: dashed lines ride through off-screen (a Liang–Barsky
  `ClipSegmentToRect` bounds the dash walk so an off-screen endpoint can't iterate
  tens of thousands of pixels), dots/labels/rings draw only where on-screen.

- **The fight clock holds until the pull.** The engine clock legitimately ran from
  load through the pre-pull roam; the pull only stamped `EngagedAtMs` mid-timeline,
  so the timer *looked* like it was counting before the fight. Now
  `UpdateEncounterLab` bails `if (sim.EngagedAtMs < 0) return;` — with no pull
  scheduled anywhere in the pre-simulated timeline, playback **does not advance**;
  a scheduled pull (a plan that walks a body across the ring) lets the walk-in
  play and the clock starts *at* the pull. Display: `EncounterFightClock` reads
  "pre-pull" until `_encounterViewMs >= EngagedAtMs`, then `(view − EngagedAtMs)`
  seconds; the top-HUD ▶ shows only while time is actually advancing.

- **She stands until engaged.** `RebuildEncounterSim` had hard-coded
  `InventPrePullRoam = true`, overriding Onyxia's exact-db `Stationary`. It is now
  `InventPrePullRoam = settings.SandboxRoam` (new `EncounterLab.SandboxRoam`,
  **default false**); the old always-on "sandbox roam yd" slider became a
  "sandbox roam (what-if)" **checkbox** (default off) plus a radius slider shown
  only when checked. A document-declared `Wander`/`Waypoints` idle still plays its
  own exact route regardless.

- **Chase no longer stutters.** `ChaseAggroHolder` feeds the puppet 0.9 yd crumbs
  at the 100 ms sim cadence. The old exponential lerp (τ = 100 ms) decelerated into
  each crumb then re-launched, pumping the run clip's playback rate at 10 Hz — fine
  for a 0.25 yd roam, a visible stutter at 0.9 yd/step. Replaced with a
  critically-damped **SmoothDamp** (`Puppets.cs`, per-actor `_encounterPuppetVel`,
  `EncounterPuppetSmoothTime = 0.13 s`) that carries velocity between crumbs, so a
  steadily-advancing chase renders as steady running. The 20 yd scrub-teleport snap
  is kept (it zeroes the velocity). Trade-off: ~1 yd follow-lag behind the sim dot.

## 8.2 Waypoint orientation — grab, spin, set (2026-08-17)

Each waypoint carries a decoupled arrival facing (`ArrivalFacing` on the staged
leg and the committed `TimedMove`). The owner sets it in the free view with a
free, continuous spin — reached after two rejected attempts (a drag-to-aim "Orient
mode" toggle: *"I don't understand the orientation"*; and Ctrl+Left assign /
Alt+Left spin: *"ctrl+left doesn't work, it's tied to the left-button go-down"* —
the marquee/selection path ate the click). Two lessons: **left-button gestures in
the free view get eaten by marquee/selection**, so dot-picking verbs go on the
right button; and **right-DRAG is hard-wired to camera mouselook**
(`ClientWindow.CameraLookRequested` returns `rightDown` unconditionally — a
documented invariant), so a spin cannot hold the button.

The result (`HandleEncounterWaypointOrient` in `Rts.cs`, run first in the free-view
RIGHT-click branch): **Shift+Right-click a waypoint dot to GRAB it**, then **move
the mouse** — the arrow tracks the cursor's ground point live, continuous radians —
and **click to SET it** (`_encounterOrientSpinning`; `UpdateEncounterOrientSpin`
runs each frame; any click commits via a top guard in `HandleFreeCamWorldClick`).
Because the button is not held during the spin, camera mouselook never engages.
Staged legs update live (cheap); committed moves ride a preview arrow and write +
rebuild once on commit (no per-frame sim rebuild). The visual is **ground-projected**
(`DrawWaypointOrientation`): a ring sampled on the terrain plane (2.2 yd) with an
arrow built from world offsets past it — it lies flat on the floor and scales with
distance, not a fixed badge on the camera plane. Default grab facing = face the
boss's spawn. (No clear-facing binding yet.)

## 8.3 Legibility — role colours & health (2026-08-17)

- **Plan lines/dots coloured and weighted by role** (`EncounterRoleStyle(role,
  job)` in `Overlays.cs`): **tank gold** (thickest, 4.5), **healer green** (3.5),
  **dps cream** (2.5, ≈ prior weight), **boss red** (3.5), else neutral cyan.
  Applied to body marks, the dashed staged + committed lines (`DrawDashedLine` /
  `DrawDashedLineClipped` gained a `thickness` param), waypoint dots, the
  orientation ring, the boss facing tick and her authored flight route. Teleport
  what-ifs stay orange; the "just hit" flash moved off red (now bright white-gold)
  since the boss owns red.
- **Boss health bar** (was %-text only): a red→amber→gold `ImGui.ProgressBar` in
  the Transport section, and a filled bar floating above her world marker in
  `DrawEncounterActors`. Health is what drives her phase gates, so it reads at a
  glance now.

## 8.4 The real fight — animations & spell visuals (2026-08-17)

This is the load-bearing "simulate the game" piece. The whole **live** spell-cast
pipeline already runs offline (ticked from `Program.cs` `UpdateSpellPresentation`,
drawn by the particle / mesh / ribbon renderers each frame; the DBC catalogs load
at world-init), and Onyxia's abilities carry **real DBC spell ids**
(`flame_breath` 18435, `cleave` 19983, `wing_buffet` 18500, `knock_away` 19633,
`tail_sweep` 15847, `fireball` 18392, `deep_breath` 17086, `bellowing_roar`
18431…). So the Lab reproduces the encounter's actual art rather than placeholders.

`UpdateEncounterCombatAnimations` (called from `UpdateEncounterLab` with the
`(fromMs, toMs)` span just played) drives, keyed off puppet guids:

- **Boss ability casts** (`PlayEncounterSpellCast` on each `CastStart`): builds a
  `SpellGoPacket{ Caster = bossPuppetGuid, Hits = [targetGuid] }` and calls
  **`ApplySpellGo`** — the same handler live combat uses. A puppet guid ≠
  `ControlledGuid`, so it takes the creature branches: the real **cast animation**
  (`_creatures.ReleaseSpellVisual`), the **attached kit FX** (`_spellEffects.SpawnKit`
  — her breath, her roar), and for a **Speed > 0** spell (fireball) a live
  guid→guid **missile** whose arrival plays the impact. Cones/instants (Speed 0)
  pass empty `Hits` → caster cast visual only.
- **Impacts on bodies** (`PlayEncounterSpellImpact` on each `ActorHit`):
  `ApplySpellImpact(victimGuid, spellId)` — the real impact kit + authored wound
  reaction, at the sim's land time. Projectiles are skipped here (their impact is
  played by the missile arrival) to avoid doubling.
- **Melee** (synthesized — no sim event marks a melee autoattack): tank/melee
  bodies in reach of a grounded boss swing every ~2 s of sim time
  (`_encounterSwingAccumMs`, phase-offset per key) via `TriggerCombatSwing`.

Resolvers: `_spellCatalog.TryGet(spellId, out SpellInfo{VisualId, Speed})`, then
`_spellVisualCatalog` (SpellVisual / Kit / EffectName DBC). The reusable
lower-level template is `PresentSpellEffect(spellId, stage, onGuid)` in
`DevTools.SpellAnimation.cs`.

**Not yet done (§12):** ranged/healer bodies do not cast (the sim models their
damage as a number, not spell events — casts would be synthesized); DynamicObject
/ area ground-fire visuals (Deep Breath lanes) are not spawned as real particles,
only the overlay footprints show them (`TryGetAreaVisual` / `SpawnAreaVisual`
would add them); and there is no precast **hold** — `ApplySpellGo` fires the cast
animation immediately rather than holding a precast pose for the cast duration
then releasing (a long cast like Deep Breath's 5 s does not read as a channel).

## 8.5 The action panel — a live step-through (2026-08-17)

A separate pop-out chrome window (`GameLoop.EncounterLab.ActionPanel.cs`,
`_encounterActionPanelOpen`, drawn from `Program.cs` after the Lab window, opened
by a **"Pop out ▸ live action panel"** button in the Timeline section). It
**follows the selected body** (`EncounterFollowedActorKey`: a `_freecamSelection`
/ `_selectionGuid` guid mapped back to a sim `actor.Key` through `_encounterPuppets`,
else the boss) and shows what it is stepping through in real time: name + role,
the clock + phase, a boss health bar, a **NOW** line, and a scrolling list of
`sim.Events` filtered to `ActorKey == key || TargetKey == key`, ±20 s around the
scrub head, the current step marked ▶ and future rows carrying a `+Xs` lead time.
All of it reads the pre-simulated `sim.Events` — no new sim instrumentation.

## 8.6 Reading a paused fight — ability visualizer & the orbit sweep (2026-08-18, pending eyes-on)

Refinements for reading a HELD instant, all client-side overlay / what-if work.

- **Cones and lines are real ground decals.** Directional sectors and rectangular lanes
  now use `SpellEffectMeshRenderer`'s terrain/WMO projector, alongside the existing discs.
  A white sector/strip mask is clipped onto the exact gathered floor triangles and tinted
  by fidelity, so Flame Breath, Cleave, Knock Away, and Tail Sweep follow the lair floor
  instead of appearing as one flat origin-Z sheet with a camera-dependent perspective.
  The same depth-tested pass lets Onyxia and raid bodies occlude the paint naturally.

- **Dashed plan / route / probe lines no longer vanish on a camera pivot.** A separate,
  worse case of the same family: `TryProjectToScreen` returns false for any point BEHIND
  the camera (`clip.W ≤ 0` — no pixel exists), and every dashed-path loop dropped the
  whole segment on that (`from = null; continue`). A raid ordered in one direction shares
  its far move-targets, so a small pivot pushed them all behind the camera plane at once
  and **every plan line disappeared together**. §8.1's screen-space clip cannot help —
  a behind-camera point has no screen coordinate to clip. New
  `Camera.TryProjectSegmentToScreen` clips the segment to the camera plane in homogeneous
  clip space FIRST (the standard near-plane cut), so a leg straddling the plane keeps its
  visible half; the path loops (committed moves, staged plan, probe trajectory, flight
  route, chain spine) now thread WORLD points through `DrawDashedWorldLine` instead of
  screen points.

- **Completed raid-route legs retire on arrival.** `SimActorState` snapshots now carry
  the fired authored-move mask plus `ActiveOrderedMoveIndex`. The committed route renderer
  starts at the body's live position and draws only its active leg plus unfired future legs;
  the instant a body reaches a waypoint, that leg and dot disappear instead of remaining as
  historical floor noise. Because the state lives in every snapshot, scrubbing backward
  restores the appropriate leg rather than showing the final route state at every instant.

- **Visualize every current-phase ability from a dedicated pop-out.** The non-blocking
  **Ability Visualizer** is opened beside the live phase readout, from Timeline, or from
  the Action Timeline. It recomputes `definition.AbilitiesIn(sim.PhaseKey)` every frame
  and renders one clean row per available mechanic: ability name on the left, a circled
  **?** for hover-only geometry/provenance detail, and a compact game-asset **Show**
  or **Hide** button on the right. Technical labels such as `declared-cpp-manifest` remain
  available to authors without occupying the list. The selected-key set
  `_encounterVisualizedAbilities` is also filtered by phase at render time, so an old attack
  cannot linger for one misleading frame after a phase turn. Spatial abilities force-draw
  their landing zones at full strength, ignoring cast timing and the global overlay toggles;
  `ResolveVisualizedFootprint` anchors cones to the boss's live holder-facing and aims lines,
  bolts, and circles at the live aggro holder. Non-spatial abilities are not omitted: authored
  summon steps paint and label their spawn locations, while a mechanic with no spatial facts
  gets an explicit boss-anchored "no spatial shape modeled" callout. **Hide** turns that
  mechanic off, and **Clear phase** removes the
  current phase's selections. The summary row is permanently reserved (including `0 visible`)
  so toggling cannot move the list. The pop-out now activates its own persisted layout tune
  before calculating scale and spacing, so Text Size, Widget Size, Button Size, Row Spacing,
  and Reset Sizes visibly affect this window. All Encounter Lab action controls use the same
  Blizzard `UIPanelButton` assets, with compact sizing for dense inline rows.
  Radius-less targeted cones such as Cleave and Knock Away would otherwise resolve to the
  geometry law's 0.5 yd data-hole minimum under Onyxia's model; their visual preview alone is
  extended through the live holder with a readable melee-reach minimum. Simulation geometry
  remains unchanged.

- **Orbit-sweep a body around her (`Rts.cs`).** Select a raid body, **Shift+Left-click
  it** to GRAB, then move the mouse and it arcs around the boss at the radius it was on
  (LOCKED — rotate on the ring, not in/out), sweeping through the visualized footprints with
  a live **clear / IN <ability>** readout (`EncounterGeometryLaw.Test` against the swept
  body). **Left-click sets** it as a teleport what-if at the scrub instant (one rebuild,
  the Ctrl+RClick verb — the fight reflows, history before it bit-identical);
  **right-click cancels**. No per-frame rebuild: the model is overridden live in
  `SyncEncounterPuppets` (snap, zero velocity) and the commit is the single rebuild — the
  same no-rebuild-while-dragging lesson the orient spin learned (§8.2). Shift+Left-click
  on empty GROUND still stages a waypoint; the grab fires only on a hit body. Playback
  pauses on grab so the sweep is a stable paused read. **When the dragged body is the
  aggro holder, the boss TRACKS it live** — `EffectiveBossFacing` snaps her facing to the
  swept position (facing is instantaneous in the fight), so her model turns to follow it
  and every visualized cone sweeps along; her position does not chase (that takes sim time —
  the commit's what-if reflows it). Dragging a non-holder body leaves her cones fixed and
  sweeps that body through them, which is the other half of the read.

## 8.7 Character Customizer — reusable Combat Plans (2026-08-20)

A plain click on a raid puppet in **Ctrl+F** selects it for orders without opening
or interrupting anything. A compact, game-native **Character Customizer** button
slides onto the right edge for that single selected body; only clicking it opens
the **Player Setup** modal. Multi-selection stays an order group and does not get
an ambiguous single-body button. The same modal remains reachable from
the body's **rules** button in Scenario.

That modal is now a full per-character **Combat Plan**, not a spell-list editor.
It starts with plain-language intent and progressively exposes five views:

- **Quick Plan** — an editable role template, movement doctrine (independent,
  hold, or follow a semantic ally), follow-distance band, facing, and permission
  to initiate an engagement.
- **Priorities** — ordered protection thresholds and ordered hostile buckets.
  Rows can be enabled, moved, or removed; first applicable wins. Subjects such as
  `tank 1`, `tank 2`, `lowest-health ally`, `active adds`, and `primary encounter
  target` are resolved from the current roster instead of naming a boss.
- **Responsibilities** — interrupt/dispel/cleanse/CC/resurrection ownership,
  resource reserve, emergency threshold, cooldown policy, and fallback.
- **Encounter Context** — the current encounter, phase, playbook directive, and
  explicit body orders, shown as an overlay that is deliberately **not saved in
  the character plan**.
- **Test & Explain** — the resolved follow/protect/enemy actor at the current
  snapshot, precedence information, validation warnings, and an explicit account
  of what the Lab can and cannot execute yet.

Edits are a draft with **Undo**, **Redo**, **Revert**, and explicit **Save &
apply**. Saving writes a versioned, per-user `combat-plans.json` profile keyed by
the stable character slot; placing that character against a different encounter
hydrates the same plan. Applying also puts the plan on the current actor inside
the JSON-round-trippable `EncounterPlayerRules`, and scrub snapshots capture its
resolved intent. The older `AlwaysFaceBoss` field remains readable when no plan
exists; once a plan exists its facing rule is authoritative, so legacy state
cannot silently override the modal.

**Portability invariant.** `CombatPlan` contains no encounter key, phase key,
creature entry, boss name, or group-size switch. The same plan is valid in a
dungeon or raid. Direct control and legality win first, followed by explicit
waypoint/RTS orders, encounter-local playbook choreography, reusable movement
doctrine, ordered ability intent, then fallback. This keeps “what this character
always tries to do” separate from “what this encounter requires right now.”

**Honesty boundary.** The current simulator resolves follow, protection, and
hostile intent; it executes the follow-distance doctrine and routes each body's
owner-authored DPS to the resolved hostile. “Adds before primary target” therefore
changes add and boss health, includes melee reach against the chosen target,
retires dead adds, and reroutes deterministically. The global raid-DPS fraction
remains the explicitly labelled boss-health dial. The Lab does not fabricate
friendly damage, mana, GCDs, healing amounts, dispels, interrupts, or cooldown
use. Those remaining typed intentions are ready for the later combat evaluator,
but the modal does not claim those casts have happened.

## 8.8 Persistent flight presentation (2026-08-20)

Flying is durable actor state now, not merely a property of an active movement
spline. Encounter puppets copy it on spawn and every simulation frame. A flying
body that has reached a point therefore selects the model's authored **Hover**
animation (falling back to Fly, Fall, then Stand) and never enters the ground
contact-shadow pass. Onyxia can pause between air-phase attacks without looking
as if she sat on an invisible floor; grounded and travelling animation selection
is unchanged.

**Status: BUILT, COMPILES CLEAN (Debug + Release), NOT YET EYES-ON.** The pins and the
sweep only read on a staged-raid-pulled-then-paused fight, which the probe does not
reach — so the proof is the owner's sweep-and-watch pass (per the §11.3 rule, treat
§8.6 as "written, not yet proven in-client").

---

## 9. Using it

**Ctrl+E** opens the window. Works with no server connected — the simulator needs
nothing but the client.

| Section | What it does |
|---|---|
| **Encounter** | Pick an authored document, or **Load selected** to derive one live from the world DB for whatever creature you clicked. Shows source, coverage flags, weakest fidelity, core build hash. |
| **Overlays** | Footprints-at-this-instant · **structural** (everything that could ever land, ignoring timing) · authored route · actors + probe · labels · linger. Includes the fidelity colour key. |
| **Transport** | **GO (staged count)** when a plan is queued · Play / Pause / Step / Back / Reset, a scrub slider reading the **combat clock** (pre-pull until the pull, then counting), a **boss health bar** (§8.3), playback speed, **seed**, step size, and the labelled raid-dps dial. |
| **Scenario** | **Place raid (10)** at the player · place the boss, add dummies, place any actor by clicking the world · pull-ring slider with the exact-db detection_range line · pre-pull line (declared idle) with a **sandbox-roam opt-in** checkbox (default off — she stands, §8.1) · melee-reach dps gate · the **role playbook** table (phase × job → hold / chase boss / to spot) · per-body **rules** modal, job, dps, aggro @ scrub, and anchor-aware moves. |
| **Timeline** | A ±12 s window around the scrub head, coloured by fidelity; unmodeled beats show in red. **Pop out ▸** a live action panel (§8.5) that follows the selected body and steps through its casts / phase turns / hits in real time. |
| **Position probe** | Place a body, or **Add waypoint** repeatedly to give it a trajectory. Reports hits with times and near-misses with clearance. Warns when "safe" rests on unmodeled mechanics. |
| **Abilities** | Every ability: trigger, timing band, target rule, resolved shape, chance, phases, notes, and `← source` lines pointing at the exact table/column or `file:symbol`. |
| **Coverage & holes** | Source flags and every declared hole, verbatim. |
| **Tape** | Record live `SMSG_SPELL_GO` traffic and diff **predicted vs observed** per spell. |

An armed placement owns the world click completely, so dropping a probe never also
issues an RTS order.

---

## 10. The tape — and why it matters most

`SMSG_SPELL_GO` already carries the caster, the spell, the destination and the
full hit/miss lists; `SMSG_MONSTER_MOVE` already carries splines. Both are parsed
by the client **today**. Recording them cost a few hundred lines and **no server
work whatsoever**.

The diff names, per spell: what the server cast that the model does not know about
(`MISSING FROM MODEL`), and what the model invented. Deliberately coarse —
per-spell counts, not per-instant alignment — because timing drifts legitimately
(threat, movement, resists) while a missing or invented spell is *always* a real
modelling defect.

**A simulator with no tape drifts into fiction.** This is what stops it, and it
works on all 725 C++ bosses the simulator will never model.

Recording is **off by default** and both taps no-op unless the window is open AND
recording is armed — the instrumentation-hazard rule, same as the NPC dev
window's observed-path tap.

---

## 11. Verification

Three layers, each catching what the others cannot.

**1. Headless suite — 126/126 offline, `tools/encounter-lab-check` (re-run
2026-08-20; nothing regressed):**

```bash
dotnet run --project tools/encounter-lab-check
```

Covers: rear/front cone arcs and body-width slack, vertical bands, chains, swept
lines, near-miss clearance; determinism (same seed ⇒ identical event stream),
different seeds diverging, exact rewind, `Reset` reproducibility, phase gating,
casts blocked during choreography; probe trajectories tested at impact; the
translator's unit conversion, C++-hole declaration and EventAI mapping; JSON round
trip and stale-schema rejection; Combat Plan subject/priority/fallback resolution,
follow bands, movement precedence, facing migration, per-target DPS, deterministic
add death/rerouting, despawn invalidation, rewind, and durable profile-store
recovery; and the whole Onyxia document loading, simulating, reaching all three
phases and declaring its holes.

Add `--live [baseUrl]` for the world-DB integration pass (read-only):

```bash
dotnet run --project tools/encounter-lab-check -- . --live http://192.168.0.2:5000
```

That pass is what found the apostrophe trap. It proves the cone sign, the
seconds→ms conversion, the exact lane coordinates, the missing `17096` row, all
eight lanes resolving, and a real EventAI creature translating and simulating.

**2. In-client scripted probe — the REAL client on the REAL code paths:**

```bash
MSUI_ENCLAB_PROBE=1 dotnet run --project MSUIClient/MSUIClient.csproj -- MSUIClient/client-config.json
```

Boots the creator world at the persisted location, loads onyxia, and MEASURES the
owner's whole flow: document loads · *Place raid (10)* forms 10 jobbed bodies
within ~9 yd of the controller · staged ~75+ yd outside her ring · **on the
collision floor** (worst Z offset under 1 yd) · never pulled until ordered ·
**shift-click stages (queued, nothing moves)** · **GO commits and the body walks**
· Ctrl+F raises the free view offline. Dumps screenshots to
`dumps/gameplay-enclab-probe-*.png` and prints PASS/FAIL per claim. The Encounter
Lab toolbar shows the binary's **build stamp** — read it before debugging any
"not working" report; sim math passing headlessly proves nothing about GameLoop
wiring, and the probe exists because that gap ate a day.

> **⚠ Two assertions were rewritten for the §8.1 behaviour and NOT re-run this
> pass.** Stage 2 flipped from *"world runs LIVE after load (she moves on her
> own)"* to **"clock HOLDS before the pull"** (view < 200 ms && `EngagedAtMs` < 0);
> the roam check flipped from *"Onyxia ROAMS by default"* to **"Onyxia STANDS at
> spawn pre-pull"** (drift < 1 yd). The probe needs a fresh run to re-earn its
> tally; until then §8.1–8.5 are compile-verified only.

(Probe-writing note: puppets spawn one frame after a sim rebuild — a probe stage
that touches fresh puppets needs a settle stage or it races the spawn.)

**3. Owner eyes-on:** the **core + raid sim (2026-08-17): done** — raid at the
feet on the walkway, staging + GO, confirmed in-session in the lair. The
**game-faithful pass (§8.1–8.5): pending** — built and compiling, not yet
eyeballed; spell visuals only fire during a played, engaged fight (which the
probe does not reach), so the real proof is a stage-raid-pull-watch pass.

---

## 12. Known limitations

1. **No threat model.** Aggro is an owner-assigned timed plan (the tank holds
   because you said so); the fallback victim is "closest friendly". Every ability
   depending on real threat order is labelled `heuristic`. This is the single
   largest source of divergence from a real pull — and also the point: "who is
   she on" is an input to the plan being tested, not a thing to guess.
2. **Actor movement is straight-line.** `MmapNavLoader` parses mmaps into
   triangles for the X-ray but there is **no Detour query** in the client, so
   `MOVE_PATHFINDING` cannot be reproduced. Flight paths are fine (they are
   straight anyway); ground pathing around geometry is not — raid walks, her
   chase and the roam all go as the crow flies. Placement Z uses the collision
   world; movement Z is linear between points.
3. **Ground decals require a projectable floor.** Circles, chains, projectiles, cones,
   and lines follow gathered terrain or walkable WMO collision. In a data hole with neither,
   the renderer deliberately falls back to a flat shape at the footprint origin's Z rather
   than hiding the mechanic entirely.
4. **No aura, GCD, resist, immunity or interrupt model.**
5. **The raid-dps dial is not damage.** It exists to make health gates reachable.
6. **Instance scripts are not read.** `instance_onyxia_lair.cpp`-style coordination
   — encounter state, door and trap control, cross-NPC signalling — has no
   representation. Only the creature's own `script_name` is surfaced, so an
   encounter driven mostly by its instance script will look emptier than it is.
7. **EventAI `condition_id` is not evaluated** — gated events drop to `heuristic`.
8. **`imgui.ini` persists the window rect** (`###encounter-lab`); a bad saved rect
   survives restarts. Reset = delete that ini block.
9. **The staged plan and the playbook are session-state, not document-state.**
   Committed move orders live on the scenario and would round-trip through the
   DTO layer, but nothing currently saves a raid plan back to a `.json`; a
   restart forgets it. First-class "save this plan" is future work.
10. **The sandbox roam is an opt-in fiction (§8.1).** Onyxia's DB truth (guid
    47572) is stationary and she now **stands until pulled** by default; the
    "sandbox roam (what-if)" checkbox re-enables an invented wander within a
    radius. A creature with a real `Wander`/`Waypoints` declaration plays the
    truth regardless of the checkbox.
11. **Ranged/healer bodies do not cast (§8.4).** The sim models raid damage as a
    number, not per-cast events, so only the boss's real abilities and synthesized
    tank/melee swings animate. Ranged shots and heals would have to be synthesized.
12. **Area ground-fire is overlay-only (§8.4).** DynamicObject / persistent-area
    visuals (Deep Breath's ground lanes) show as footprint decals, not real
    particle fire; `SpawnAreaVisual` would add the art.
13. **Boss casts fire immediately, with no precast hold (§8.4).** `ApplySpellGo`
    plays the cast animation at cast start rather than holding a precast pose for
    the cast duration then releasing, so a long cast (Deep Breath, 5 s) does not
    read as a channel.
14. **SmoothDamp adds ~1 yd of follow-lag (§8.1).** The rendered model trails its
    sim marker slightly at run speed — the cost of ironing the chase stutter flat;
    tunable via `EncounterPuppetSmoothTime`.

---

## 13. Found in passing — NOT fixed (in-flight file)

**The NPC dev window's CSV fallback has the apostrophe bug.**
`Net/DevDataClient.cs` parses coordinates with a plain `float.TryParse`, and the
`creature` export escapes negatives exactly the same way:

```
"47572","10184",…,"249","'-4.8689","'-217.171","'-86.7104","3.14159",…
```

So on the CSV path **every negative spawn coordinate reads as 0** — which is most
of Azeroth. It has not shown up because the deployed `/NpcDev/Snapshot` JSON
endpoint is the primary transport and JSON numbers are unescaped; the bug only
bites when the fallback ladder (`NPC_DEV_WINDOW.md` §7c) actually falls back.

Left alone deliberately — `DevDataClient.cs` belongs to the in-flight NPC dev
work. The one-line fix is the same as `EncounterDataClient.CsvRow.Raw`: strip a
leading `'` before parsing.

---

## 14. Extension recipes

**Author a new encounter** — drop a `.json` in `encounters/`, hit *Reload
documents*. Start from `onyxia.json`; the fields are in §3. Set `fidelity`
honestly and use `Unmodeled` steps for beats you cannot express — they surface in
the timeline and the coverage report rather than quietly disappearing.

**Add a geometry kind** — extend `FootprintKind`, add a `Resolve` case and a
`Test` case in `EncounterGeometryLaw`, then a draw case in
`GameLoop.EncounterLab.Overlays.cs`. Add its decal primitive to
`AddFootprintGroundShapes` and it inherits terrain/WMO projection.

**Add a trigger or step kind** — extend the enum, handle it in
`EncounterSim.TriggerFires` / `AdvanceSequence`, and map the matching
`EVENT_T_*` / `SCRIPT_COMMAND_*` in `EncounterTranslator`. Keep the vocabulary a
subset of the core's, so authored encounters stay translatable to DB rows.

**Add a behaviour table** — add it to `EncounterDataClient.Tables`, write a
header-name-based parser (**strip the apostrophe**), extend `EncounterWorldData`.

**Close a gap from §7** — the highest-value three are GAP-1 (variant selection),
GAP-2 (predicate gating) and GAP-7 (conditional steps). Each is additive to the
format and to `EncounterSim`; none requires a core change to *simulate*, only to
eventually *execute* server-side.

**Give a patrolling creature its exact route** — dump its `creature` row and
`creature_movement` rows through the web API
(`http://192.168.0.2:5000/Database/Export/mangos/<table>`; direct MySQL is
refused, and negative numbers wear the apostrophe escape), then author
`idleMovement: {kind: Waypoints, points: [{position, waitMs}, …]}` on the actor
with a source ref. The sim loops it at true walk speed with exact pauses —
built, tested, waiting for its first real patrol.

**Add a playbook directive kind** — extend `RaidDirectiveKind`, handle it in
`EncounterSim.ApplyPlaybook`, add the combo label in `DrawEncounterPlaybook`.
Keep directives dumb verbs; anything with branching belongs in the format's gap
list, not in an escape hatch.

**Extend the probe** — every new Lab claim gets a check in
`GameLoop.EncounterLab.Probe.cs` stating what the owner's eyes would check, in
yards and booleans. If a stage touches puppets after a rebuild, give it a settle
stage (§11).

---

## 15. Files

**New — this feature owns them**

| File | Responsibility |
|---|---|
| `MSUIClient/World/Encounters/EncounterDefinition.cs` | Schema, fidelity registry, coverage flags, `Holes()`, `WorstFidelity()` |
| `MSUIClient/World/Encounters/EncounterGeometry.cs` | `BodyCapsule`, `Footprint`, `IEncounterSpellFacts`, resolve + hit tests |
| `MSUIClient/World/Encounters/EncounterSim.cs` | `SeededRng`, actors, events, snapshot ring, the step; **ordered-move fired/active state captured for scrub-correct route retirement**; deterministic per-character **Combat Plan intent + follow doctrine**, captured for rewind (§8.7) |
| `MSUIClient/World/Encounters/EncounterProbe.cs` | `ProbeTrajectory`, `ProbeReport`, structural scan |
| `MSUIClient/World/Encounters/EncounterTranslator.cs` | world-DB rows → definition |
| `MSUIClient/World/Encounters/EncounterLibrary.cs` | JSON DTOs ⇄ model, load/save |
| `MSUIClient/World/Encounters/CombatPlanStore.cs` | Versioned per-character `combat-plans.json`: tolerant load, atomic explicit save/remove, shared plan DTO mapping (§8.7) |
| `MSUIClient/World/Encounters/EncounterSpellFacts.cs` | Spell.dbc + world DB bridge |
| `MSUIClient/Net/EncounterWorldData.cs` | Row model + immutable snapshot |
| `MSUIClient/Net/EncounterDataClient.cs` | 5 tables, CSV + 12 h disk cache |
| `MSUIClient/GameLoop/Dev/GameLoop.EncounterLab.cs` | Window, transport (GO) + **combat clock (holds till pull) + boss health bar**, sections, click intercept, raid preset, staged orders, playbook UI, collision-ground placement, **sandbox-roam opt-in**, **attack animations + real spell visuals** (`ApplySpellGo`/`ApplySpellImpact` on puppet guids, §8.4) |
| `MSUIClient/GameLoop/Dev/GameLoop.EncounterLab.Overlays.cs` | 3-D decals + screen pass; committed and staged plans, anchor labels; **completed committed legs retire on arrival**, **role-coloured/weighted lines & dots**, **ground-projected orientation ring**, **boss health bar over the marker**, off-screen-safe path projection (§8.1–8.3); **terrain/WMO-projected cone + line visualizations, phase-filtered ability visualization + `ResolveVisualizedFootprint`, summon markers, orbit-sweep preview** (§8.6) |
| `MSUIClient/GameLoop/Dev/GameLoop.EncounterLab.Puppets.cs` | Rendered raid/boss models: synthetic entities, per-look weapons, stable guid reserve, **SmoothDamp motion** (velocity-carrying follow, §8.1) + shortest-arc facing; **live orbit-drag position override** (§8.6); durable flying state for stationary hover (§8.8) |
| `MSUIClient/GameLoop/Dev/GameLoop.EncounterLab.Rts.cs` | Ctrl+F bridge: non-blocking puppet selection (§8.7), marquee, order routing (stage / immediate / teleport / facing), **Shift+Right-click waypoint orientation grab-spin-set** (§8.2), **Shift+Left-click body orbit-sweep grab→teleport-what-if** (§8.6), never touches `SuiOrder` |
| `MSUIClient/GameLoop/Dev/GameLoop.EncounterLab.ActionPanel.cs` | **Live action step-through pop-out (§8.5)** — follows the selection, streams `sim.Events` for the target, current step ▶ + `+Xs` lead; dedicated phase-aware **Ability Visualizer** pop-out with a hover-help affordance and **Show/Hide** button per mechanic (§8.6) |
| `MSUIClient/GameLoop/Dev/GameLoop.EncounterLab.PlayerSetup.cs` | Right-edge game-native **Character Customizer** affordance + five-view **Combat Plan workspace** (§8.7): templates, ordered doctrine, context overlay, live explanation, validation, draft undo/redo/revert/apply |
| `MSUIClient/GameLoop/Dev/GameLoop.EncounterLab.Probe.cs` | `MSUI_ENCLAB_PROBE` — 11 in-client checks + screenshots (§11); two assertions rewritten for §8.1 (clock-holds, stands) and pending a re-run |
| `MSUIClient/GameLoop/Dev/GameLoop.EncounterLab.Tape.cs` | Recorder + predicted-vs-observed diff |
| `encounters/onyxia.json` | The authored encounter + spec fuzz — boss at the exact DB spawn (guid 47572), real speeds, `detectionRangeYards`, declared `Stationary` idle with full provenance note |
| `tools/encounter-lab-check/` | deterministic headless assertions, including Combat Plan resolution, precedence, rewind, follow bands, and JSON round-trip; `--live` integration mode |

**Touched — thin hooks only**

| File · location | Hook |
|---|---|
| `Program.cs` BuildGui, beside `DrawDevWindow()` | `DrawEncounterLab(); DrawEncounterActionPanel(); DrawEncounterLabOverlay();` |
| `Program.cs` 3-D pass, after `RenderDevOverlays3D()` | `RenderEncounterLab3D();` |
| `Program.cs` `Update`, before `ObserveUiPanelOwnership()` | `UpdateEncounterLab(dt);` |
| `GameLoop/Scene/GameLoop.Control.cs` | `UpdateEncounterLabInput(typing);` · free-view hooks: puppet select, shift-click stage, order routing, puppet marquee, **Shift+Right-click waypoint-orient grab + any-click spin commit** (§8.2) · **creator-sandbox Ctrl+F** (offline free view, exact pose restore) · shared `DrawDashedLine` gained a `thickness` param (§8.3) |
| `GameLoop/Combat/GameLoop.Targeting.cs` click drain | `if (HandleEncounterLabClick(click)) continue;` |
| `GameLoop/Combat/GameLoop.Casting.cs` spell-go handler | `RecordEncounterTapeCast(packet);` (unchanged). The Lab now also **calls into** this file — `ApplySpellGo` / `ApplySpellImpact` with a puppet-guid caster — to play the real spell visuals (§8.4); no edit needed there (the caster ≠ `ControlledGuid` branches already do the right thing offline). |
| `Engine/Camera.cs` | `TryProjectToScreen(world, size, out pixel, out onScreen)` — succeeds for any point in front of the camera; `TryWorldToScreen` delegates (`&& onScreen`). Lets overlay paths persist off-screen (§8.1). **`TryProjectSegmentToScreen(a, b, …)`** near-plane-clips a world segment so a dashed line survives one end passing behind the camera (§8.6). |
| `GameLoop/Dev/GameLoop.DevWindow.Overlays.cs` | `DevPlayerPosition()` is offline-aware (controller / free-view return pose) — every "at the player" affordance depends on it |
| `Engine/ClientWindow.cs` | `WorldMouseClick` carries Ctrl/Alt; `BuildStamp` (assembly write time, shown in the Lab toolbar) |
| `Engine/GameSettings.cs` | `EncounterLabSettings` + `GameSettings.EncounterLab` (melee-reach gate, pull range, roam radius, and the new **`SandboxRoam`** opt-in — default off, §8.1) |
| `Program.cs` Update | `UpdateEncounterLabProbe();` |
| `.gitignore` | `/encounter-tapes/` (recordings). `/encounters/` deliberately tracked. |

**Not touched:** `Net/DevDataClient.cs`, `Net/DevWorldData.cs`,
`GameLoop/Dev/GameLoop.DevWindow*.cs` — the in-flight NPC dev work.
