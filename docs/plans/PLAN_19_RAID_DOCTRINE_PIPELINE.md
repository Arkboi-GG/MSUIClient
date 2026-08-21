# PLAN 19 — The Raid Doctrine Pipeline

**Document type:** architecture explanation and implementation plan.

**Status (2026-08-20):** the authoring surface, typed data model, and the FIRST
executor (the Encounter Lab simulator) are implemented and regression-tested
(167 checks in `tools/encounter-lab-check`), including the class-gated rules
(Fear Ward maintain-aura chains, add-control/Blizzard jobs, opt-in boss
threat-lite). The deployment pipe is BUILT end-to-end: M-A (raid-plan document +
export/import + round-trip test), M-B (MangosSuperUI RaidPlanService/Controller,
builds clean), M-C (`RaidPlanLaw.h/.cpp` on the box, formation law verified
against the client fixtures by a unit main, `LOAD_RAID_PLAN` wired into
`AiBotAIBridge.cpp`, `mangosd` links — NOT deployed; deployment is the owner's).
Multi-select assignment (part of M-E) is live in the Game Plan header.
**M-D v1 is BUILT (same day):** the core now ACTS on `m_raidPlan` —
`AiBotAIRaidPlan.cpp` (a 500ms act sub-tick in UpdateAI: formation stations via
the motion master's own `MoveChase(dist, angle)` — MT on the nose, melee fanned
across the flank band, healer/ranged rings; maintained auras cast on the boss's
REAL current victim through `CanTryToCastSpell`/`DoCastSpell`) and
`AiBotDoctrineEncounter.cpp` (the `EncounterPlay` doctrine: TeamAuto COMPOSED
with add-duty `MaintainTarget` — selected on TeamAuto's conditions +
`HasRaidPlan()`, ranked below PlayerParty). The web push now resolves formation
meta per bot (derived flank, slot index/count, MT flag — one bot only knows
itself; the pusher sees the roster) and bakes bucket assignments into
`b_targets` by job ordinal. `mangosd` links; the law TU's unit main passes
against the client fixtures. **NOT deployed** — the running server still runs
the old binary; deploying is the owner's action.

M-D v1 boundaries (labelled in the code): formation uses conservative DEFAULT
arcs (45° front half / 60° rear half / 15 yd) — deriving real arcs from
`spell_cone` per boss is a refinement; add duty is phase-independent (the core
does not yet track encounter phase); telegraph dodge / spread-from-targeted are
not yet core-side; two same-class bots can race one aura refresh (wasted
cooldown, never wrong state); wards hold until a boss victim exists (in-combat).
Incidental fix that belongs to the owner's WIP: `InvalidateRouteAtlasJourney`
was declared and called but defined NOWHERE (stale build objects masked it);
defined in `AiBotAIMovement.cpp` from the members' documented semantics.

Also pending from M-E: override-lease telemetry and the sim's lease-semantics
alignment.

**Builds on:** `docs/plans/DYNAMIC_COMBAT_RULES_AND_ENCOUNTER_INTELLIGENCE.md`
(the binding contract for control states, precedence, and production authority).
This plan does not repeat that contract; it implements its doctrine/positioning
half and cites it where the rules already exist. Where that document says
"design only," the client-side pieces described below are now real.

**Repositories in scope:**

1. `MSUIClient` — authoring (Encounter Lab / Game Plan), the deterministic
   preview executor (`EncounterSim`), plan persistence, RTS override surface.
2. `SuperUI-Core` (`~/vmangos`, `src/game/SuperUiContent/SuiBots/`) — the
   authoritative executor: doctrine dispatch, rotations, movement, the brain
   bridge.
3. `MangosSuperUI` — the web surface: plan store/import/export, assignment UX,
   and the read-only encounter knowledge service.

**Owner/runtime boundary:** implementation agents edit source and run the
builds/tests explicitly requested. Nico alone deploys server artifacts, changes
a live database, or starts/stops/restarts live services. Every live-fleet step
below is an owner-operated action.

---

## 1. The idea in one paragraph

The encounter definition already contains the answer to most raid questions:
cone tables say where not to stand, combat reach says how close melee must be,
cast times say what can be dodged, summon steps say when adds come. So raid
behaviour is **derived, not authored**: the owner decides only the genuinely
human things — the macro-group split, special jobs, doctrine toggles, and
moment-to-moment overrides — and a small set of pure laws computes the rest.
That intent is **typed data**, and typed data can be executed twice: once by the
client's deterministic simulator (instant preview, rewindable, honest about its
gaps) and once by the C++ core against the real server (authoritative facts:
real threat, real auras, real casts, real pathfinding). Same plan, two
executors, diffable.

## 2. The command loop (the owner's mental model)

```
   ┌────────────────────────────────────────────────────────────────────┐
   │ 1. AUTHOR    in-game customizer (per body or multi-select), or the │
   │              web app. Output: typed plan documents.                │
   │ 2. PREVIEW   EncounterSim runs the whole fight offline. Scrub it,  │
   │              reseed it, watch the formation flow. Fix intent here. │
   │ 3. DEPLOY    plan documents go to the fleet: web app assignment →  │
   │              LOAD_* over the brain bridge → bots adopt doctrine.   │
   │ 4. EXECUTE   bots fight under doctrine on the real server.         │
   │ 5. OVERRIDE  the owner grabs any body or group mid-fight (possess, │
   │              RTS orders). The override is a LEASE: doctrine pauses │
   │              for exactly that body, executes the order, and        │
   │              RESUMES when the lease ends. (Contract §4.3–§4.5.)    │
   │ 6. VERIFY    the Tape records the real fight; diff against the     │
   │              sim's prediction of the same plan. Divergence is a    │
   │              bug in an executor or a lie in the data — find which. │
   └────────────────────────────────────────────────────────────────────┘
```

Step 5 is already legislated: direct control pauses the evaluator; free-view
remote command does not; the six-level precedence ladder (human > safety >
orders/doctrine > engagement gate > rotation > fallback) arbitrates every tick.
This plan adds one rung to that ladder's third level: **derived formation sits
below explicit orders, playbook rows, and assigned positioning scripts, and
above follow/idle** — doctrine fills silence, it never argues with an order.

## 3. The artifacts (what the typed data actually is)

All of these exist today in `MSUIClient/World/Encounters/` and serialize as
versioned JSON. Portability is a hard rule: anything naming a phase key or
ability key is **encounter-local** and lives on the body's scenario rules;
anything portable across fights lives on the reusable records.

| Artifact | Record | Says | Scope |
|---|---|---|---|
| Rotation | `CombatPlan` | what I press: class, ordered ability intents, engagement, enemy/support priorities, resources, fallback | portable; library (`CombatPlanStore`, GUID-ready keys) |
| Positioning script | `PositioningScript` | authored spatial EXCEPTIONS: per-phase spots/paths per role×side | per boss; library |
| Per-phase targets | `PhaseTargetOverride` on `EncounterPlayerRules` | "in p2, adds only" for THIS body | encounter-local |
| Avoidance | `AvoidAbilityKeys` on `EncounterPlayerRules` | per-body override of the doctrine's default avoidance | encounter-local |
| Raid doctrine | `RaidDoctrine` | the raid-wide switches: derive formation, dodge telegraphs, keep clear of cones, spread from targeted casts, group healing, bucket assignments (`PhaseJobAssignment`) | one per scenario |
| Macro groups | `RaidSide` on the actor | Group 1 = left, Group 2 = right; unsided bodies auto-split per bucket | per body |
| Formation law | `RaidFormationLaw` | not data — the pure math that turns hazard arcs + role + group into a station | code, ports to C++ verbatim |

The encounter definition itself (`encounters/*.json`) is the shared fact base
both executors read: phases, transitions, abilities, geometry, and the `GAP-n`
notes that mark what the format cannot yet say.

## 4. The three surfaces

1. **In-game customizer** (Encounter Lab → Game Plan tab). Today: single-body
   authoring; the Game Plan is encounter-first (phase columns generated from
   the definition, hazards auto-listed, per-phase target presets, avoid
   toggles). Planned: multi-select assignment — apply a rotation/doctrine to
   the current RTS selection in one action, role-filtered ("assign to selected
   warriors").
2. **The web app** (MangosSuperUI). Today: the rotation prototype
   (`RotationController`: `GET /api/rotations`, `POST /api/rotations/assign`,
   `/clear`; `RotationService` stores JSON + `assignments.json` and pushes
   `LOAD_ROTATION` over the loopback bridge). Per the contract (§3.3) this is
   proof and import format, not production authority — but it is the correct
   SHAPE: the web app is where plans are browsed, imported/exported, and
   assigned to fleet members out of game.
3. **The RTS/possession layer** (free view, `CMSG_SUI_ORDER` 0–6, possession).
   This is the override surface — it does not author doctrine, it leases
   bodies away from it.

## 5. The three channels (what rides which wire)

| Channel | Transport | Carries | Cadence |
|---|---|---|---|
| SUI game wire | `CMSG_SUI_*` opcodes in the authenticated WorldSession | possession, imperative orders, RTS state, zone intel | live, per action |
| Web/HTTP | MangosSuperUI REST (`SuiBaseUrl`, today `http://192.168.0.2:5000`) | plan documents, assignments, encounter knowledge, audit | on change |
| Brain bridge | C# ⇄ C++ TCP JSON (`AiBotAIBridge.cpp`) | `LOAD_ROTATION` today; `LOAD_RAID_PLAN` (this plan); `COMBAT_DIRECTIVE`, `MOVE_TO`, state/events | on assignment + telemetry |

Production-authority note (inherited from the contract): the anonymous
name-keyed HTTP path and the unauthenticated bridge are acceptable for the LAN
prototype loop but are not the end state. Durable gameplay assignment
ultimately belongs to SuperUI-Core persistence behind authenticated sessions;
the web app remains the editor/browser over that store.

## 6. Executor #1 — the Encounter Lab simulator (built)

`MSUIClient/World/Encounters/EncounterSim.cs`. A deterministic, fixed-step,
seeded, snapshot-every-step machine — deliberately the same shape as a core
UpdateAI loop, so per-tick decisions transplant.

What it executes today (all regression-tested):

- **Derived formation** — arc-safe stations per role/group, recomputed every
  tick against the live boss; air-phase spread ring; waypointed bodies exempt.
- **Avoidance** — telegraphed casts dodged (run out during the cast, walk back
  after, station and facing restored); instant cones held clear of by
  continuous tangential sidestep; aggro holder exempt from arcs that point at
  him.
- **Spread-from-targeted** — neighbours step off a marked body; the mark holds
  and soaks alone.
- **Targeting** — per-phase overrides and doctrine bucket assignments over the
  plan's portable buckets (adds / current / primary).
- **Adds** — threat-lite victim selection (add-duty tank magnet → direct
  attacker → nearest), chase, dialled damage, friendly deaths.
- **Healing** — derived assignments (tank healers one-to-one, group healers on
  their flank's lowest health) executed as dialled throughput.

What it deliberately does NOT model (the honesty boundary): mana, GCDs, spell
damage, boss threat (her victim is owner-assigned), fear on players, tracked
projectiles being outrun. Every such gap is labelled in the UI and in the
encounter file (`GAP-n`), because each one names exactly a place where executor
#2 will behave better and the Tape diff will show it.

## 7. Executor #2 — SuperUI-Core (the point of this plan)

The core is in-process with the world. Every fact the sim emulates, the core
has authoritatively — this is the entire translation story:

| Sim emulates | Core has |
|---|---|
| phase edges from my health model | the real boss: health %, fly flag, cast events (same observables the JSON transcribed from `boss_onyxia.cpp`) |
| scheduled `_pending` impacts | the actual cast: `GetCurrentSpell`, real timers, `spell_target_position` — GAP-1 (which breath lane) disappears, the cast row IS the lane |
| no boss threat (GAP-4) | `ThreatManager` — the P3-landing "nearest warrior shields up and rides sunders until tanks catch up" becomes a real query + real casts |
| cosmetic rotation | Layer R casts real spells (LOAD_ROTATION pipe exists) |
| `HealerHps` dial | real heal spells; doctrine picks the target, the rotation casts |
| "needs an aura model" (fear ward chain) | `HasAura()` — the chain rule is trivial |
| straight-line walks | mmaps pathfinding (`PathFinder`, `AiBotMovementGenerators`) |
| `RaidFormationLaw` inputs exported from DB | the core's own `spell_cone` / reach — the law ports verbatim |

Where each piece lands in the existing architecture
(`src/game/SuperUiContent/SuiBots/`):

- **Targeting** → a new doctrine TU implementing `IEngagementDoctrine`
  (`AcquireTarget`/`HoldPull`/`MaintainTarget` — the same three decisions the
  sim's resolver makes). It is also the natural activator for the
  present-but-dark `Directed` posture and the M2 conduct substrate.
- **Formation** → a movement generator beside the existing AiBot generators,
  fed by the C++ port of `RaidFormationLaw` (a pure-function TU, unit-testable
  in that tree).
- **Reactions** (dodge / sidestep / spread) → a per-tick hazard scan in the
  bot update reading real casts, positions, and facings — the same laws, better
  facts.
- **Ingestion** → `LOAD_RAID_PLAN` in `AiBotAIBridge.cpp`, same idiom as
  `LOAD_ROTATION` but: validate-before-clear, per-rule diagnostics back to the
  sender, and group-scoped stamping (a raid plan is a group artifact, not N
  copies).

Boss observation stays observational: script internals (`m_uiPhase`) are
private to the boss AI. Derive phase from observables exactly like the sim
does; a read-only observer hook on `CreatureAI` is the fallback if observables
ever prove insufficient, never script-state leakage into bot advantage (the
contract's "no hidden future encounter state" rule).

## 8. Override semantics (the "go back" question, answered precisely)

Inherited verbatim from the contract (§4.3–§4.5) and matched by the sim:

- **Human directly driving a body** → that body's evaluator pauses. Everyone
  else fights on.
- **Free-view RTS command** → still autonomous control: the evaluator keeps
  its cadence; the ORDER outranks doctrine movement for that body until it
  completes or is cleared.
- **Order completes / control released** → the doctrine resolver re-selects on
  the next tick and the body flows back to its derived station. Nothing to
  "un-assign" — the plan never left; it was outranked.
- In the sim, the same ladder holds: explicit orders and authored waypoints >
  assigned positioning script > playbook row > derived formation > follow >
  idle. One nuance to reconcile in M-C: the sim currently treats a body with
  ANY authored waypoint list as permanently formation-exempt; the core should
  treat completed orders as expired leases (revert to doctrine), and the sim
  will be aligned to lease semantics then.

## 9. Verification

1. **`tools/encounter-lab-check`** — 159 deterministic checks today; the
   doctrine laws are specified BY these tests (formation arcs, dodge/return,
   spread, magnet, healing derivation, bucket ordinals). The C++ port inherits
   them as fixture expectations.
2. **The Tape** (`GameLoop.EncounterLab.Tape`) — records live `SPELL_GO` /
   `MONSTER_MOVE` from the real server. The closing loop: run the SAME plan in
   the sim and on the fleet, diff the tape against the sim timeline. Deltas are
   either an executor bug or a data lie (a wrong `GAP-n` claim), and both are
   exactly what we want surfaced.
3. **Live protocol** — owner-operated: small fleet, Onyxia, staged phases;
   never partybots (the fleet — AiBotAI + brain — is the real system).

## 10. Milestones

- **M-A (client)** — raid-plan serializer: one JSON document bundling
  `RaidDoctrine` + assignments + per-body encounter rules + rotation refs.
  Export/import from the Lab. Acceptance: a sim scenario round-trips through
  the document byte-stable.
- **M-B (web)** — MangosSuperUI stores/serves raid-plan documents beside the
  rotation prototype; assignment UX (pick plan → pick group). Acceptance: the
  document the Lab exported is browsable and assignable.
- **M-C (core)** — `RaidPlanLaw` TU (formation + reactions, pure functions) +
  `LOAD_RAID_PLAN` bridge ingestion with validation-before-adopt. Acceptance:
  law unit tests mirror the client check-suite fixtures; a loaded plan echoes
  back a per-rule diagnostic set.
- **M-D (core)** — the EncounterPlay doctrine TU + formation movement
  generator + hazard scan; bots stand at computed flanks on the real server,
  dodge a real Deep Breath, and pick up real whelps by bucket assignment.
  Acceptance: owner-run Onyxia session; Tape diff against the sim shows the
  known-gap deltas and nothing else.
- **M-E (loop close)** — multi-select assignment in the RTS layer; override
  lease telemetry (who outranked doctrine, when, why) surfaced in the Lab's
  action panel; sim aligned to lease semantics.

## 11. Non-goals

- No autonomous fight acquisition by rotations or doctrine (contract §4.5:
  engagement authority stays with doctrine/orders/aggro).
- No boss-script state leakage into bot decisions.
- No client-side puppeteering of the fleet at combat cadence: the client
  authors and overrides; the core executes. Latency-bound remote micro is a
  possession feature, not a doctrine feature.
- The web app does not become gameplay authority; it edits and assigns over
  the core's store (contract §3.3).
