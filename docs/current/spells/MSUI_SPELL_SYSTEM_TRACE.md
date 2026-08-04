# MSUI Spell System — Complete Implementation Trace (B)

**Purpose.** This is the standalone, file-and-line-cited description of what the current MSUIClient
actually does across its entire spell system. It is the “B” document intended to be compared later with
`BENILLA_SPELL_SYSTEM_TRACE.md` (“A”). It covers every entry path, spell classification, packet, state
transition, body animation, effect model, mesh, ground effect, particle, ribbon, missile, aura, cooldown,
combat outcome, and spell-facing UI surface that exists in this repository. It records both implemented
behavior and explicit non-behavior; it does not silently fill missing client behavior with assumptions about
the original WoW client.

**Audited snapshot.** Repository root:
`C:\Users\nico\source\repos\MSUIClient`; audit date: **2026-08-02**. Citations such as
`MSUIClient/Program.Casting.cs:48-110` are relative to that root. Section 20 records source hashes so later
changes can be distinguished from the implementation described here.

**Critical truth boundary.** MSUI is a presentation client, not a local spell-gameplay simulator. The server
chooses authoritative hit/miss recipients, computes damage/healing/power, applies and removes auras, changes
health/power/inventory/object descriptors, and reports spell completion/failure. MSUI locally decides only
whether it is willing and able to *send* a limited cast request, then presents server packets and descriptor
changes. Consequently:

- “direct,” “AoE,” “heal,” “buff,” “debuff,” “summon,” “dispel,” “teleport,” and similar gameplay categories
  do not select different local effect algorithms;
- the visual path is principally `spellId → Spell.dbc VisualId → SpellVisual → stage kit → effect M2`;
- an AoE is the server's `SMSG_SPELL_GO` hit/miss GUID lists, not a client radius query;
- aura gameplay is the unit descriptor's 48 aura slots, not a locally evaluated `EffectApplyAuraName` rule;
- combat numbers are server combat-log events, separate from the SpellVisual arrival pipeline.

That boundary is enforced throughout the code: `Program.Casting.cs:80-110` routes GO target lists,
`Net/ObjectFields.cs:239-253` exposes authoritative auras, and `Net/CombatPackets.cs:98-129` turns
authoritative combat-log packets into typed events.

---

## Coordinate and clock conventions

These conventions cross every spell effect and are prerequisites for reproducing MSUI exactly.

| Space/clock | MSUI convention | Direct implementation |
|---|---|---|
| Server/world | right-handed, +Z up, XY ground, yards | entity positions; `Program.Casting.cs:447-453` |
| Parsed M2 local | +Y up; raw `(x,y,z)` becomes `(x,z,-y)` | `Formats/M2Reader.cs:2057-2066`; `World/Units/M2Animator.cs:15-27` |
| M2 scale key | `(x,z,y)` | `World/Units/M2Animator.cs:18-23` |
| M2 quaternion key | `(x,z,-y,w)` | `World/Units/M2Animator.cs:18-23`; parser readers in `Formats/M2Reader.cs` |
| CPU matrices | `System.Numerics`, row-vector composition | `World/Units/M2Animator.cs:33-48` |
| Render positions | camera-relative before projection | mesh `SpellEffectMeshRenderer.cs:125-144`; particle shader `Shaders/particle.vert:31-39` |
| Spell/UI clock | client uptime seconds | `Program.Casting.cs:528`; `MovementInfo.ClientUptimeMs()` callers |
| Effect age | uptime minus instance start; missile age begins at release | `SpellEffectSource.cs:256-319` |
| Particle integration | frame `dt`, clamped to 0.1 s | `World/Spells/SpellParticleSystem.cs:195-200` |
| Ribbon drain | independent wall clock, clamped to 0.1 s | `World/Units/SpellRibbonRenderer.cs:68-77` |

Do not interchange M2-local Y-up values with world Z-up values. Particle gravity uses local Y only for
model-space particles and world Z otherwise (`SpellParticleSystem.cs:218-230`); ribbon sag is world Z
(`SpellRibbonRenderer.cs:113-122`); ground projection rewrites world Z (`SpellEffectMeshRenderer.cs:360-371`).

## System map

| Responsibility | Actual MSUI owner |
|---|---|
| spell metadata, ranges, costs, recipes | `Formats/SpellCatalog.cs` |
| SpellVisual/kit/effect-name chain | `Formats/SpellVisualCatalog.cs` |
| player spell roster/action cooldowns | `Net/PlayerActions.cs` |
| player cast admission and target binding | `Program.ActionBars.cs`, `Net/CastTargetLaw.cs` |
| incoming spell packet layouts | `Net/SpellPackets.cs`, `Program.Net.cs` |
| five-stage routing, cast bar, channel, missile arrival | `Program.Casting.cs` |
| visual-instance ownership and missile motion | `World/Units/SpellEffectSource.cs` |
| body attachment resolution | `World/Units/SpellAttachment.cs` |
| effect M2 parsing and tracks | `Formats/M2Reader.cs` |
| effect skeleton evaluation | `World/Units/M2Animator.cs` |
| effect meshes and ground rings | `World/Units/SpellEffectMeshRenderer.cs` |
| spell-only particles | `World/Spells/SpellParticleSystem.cs`, `Shaders/particle.*` |
| ribbons/trails | `World/Units/SpellRibbonRenderer.cs` |
| player/creature body animation | `CharacterRenderer.cs`, `CreatureRenderer.cs` |
| aura storage, duration, cancellation and UI | `ObjectFields.cs`, `Program.DevTools.Auras.cs`, `Program.UnitFrames.cs` |
| combat outcomes and floating text | `CombatPackets.cs`, `CombatFeedbackLaw.cs`, `Program.CombatFeedback.cs` |
| spellbook, macros, professions, items, pet spells | their corresponding `Program.*.cs` partials |

## Section index

1. Runtime boundary, initialization, update and render order
2. Spell DBC/data layer and consumed-field ledger
3. Spell roster, spellbook, action bars, macros, items and pet entry points
4. Local cast admission, targets, ranges, power and outbound wire
5. Incoming spell network packets and dispatch
6. Cast router: START, GO, impact, failure and the five visual stages
7. Effect-instance ownership, lifetime, attachments and pose lookup
8. M2 asset, animation-track and skeleton implementation
9. Effect meshes, materials, billboard bones and ground/AoE visuals
10. Spell particle parsing, simulation and rendering
11. Ribbon/trail simulation and rendering
12. Missile selection, release, homing, arrival and miss behavior
13. Player/creature body animation, channels and pushed visuals
14. Buffs, debuffs, aura-state FX, tracking, timers and cancellation
15. Cast bar, interruption, auto-repeat, next-swing, GCD and cooldowns
16. Direct, periodic, AoE, heal, power, shield, miss and environmental outcomes
17. Professions, item-created outcomes, hearth, learning and pet spell surfaces
18. Exhaustive spell-class behavior matrix
19. Exact implementation boundaries and known omissions
20. Diagnostics, source inventory, hashes and no-exception audit

---

# 1. Runtime boundary, initialization, update and render order

## 1.1 Initialization and failure domains

Gameplay UI initialization loads `SpellCatalog`, `SpellVisualCatalog`, and `GameplayArt` inside one guarded
block (`Program.ActionBars.cs:33-48`). Spell effect presentation has a separate initialization path:

1. `SpellEffectSource` is created from the mounted game archives.
2. `SpellEffectMeshRenderer` loads its mesh and ground shaders.
3. `SpellRibbonRenderer` loads its shader.
4. `SpellParticleSystem` loads `particle.vert` and `particle.frag`.

Those renderer constructions occur in `Program.Net.cs:119-143`. If any of the three renderer constructions or
shader loads throws, MSUI disposes and nulls **all three renderers**, but the effect source remains. The router
can therefore continue to own/tick visual instances while nothing draws them. DBC loading likewise degrades
to `null`; cast UI and routing test those catalogs at each use rather than treating their absence as fatal.

`SpellEffectSource` caches effect assets by normalized model path, including a permanent cached `null` for a
failed file/read/parse (`SpellEffectSource.cs:432-452`). Each cached asset owns one `M2Animator` and one skin
matrix array, shared by every live instance of that path (`SpellEffectSource.cs:18-26,432-451`). Renderers
evaluate that shared asset state serially while enumerating instances.

## 1.2 Update order

The per-frame gameplay update computes movement intent first. `UpdateCastMovementInput(translating || jump)`
runs before controller movement (`Program.cs:1570-1603`), so the stopped-to-moving cancellation edge is based
on requested forward/strafe movement or jump, not observed displacement; turning alone does not cancel a
cast. Later, after player animation, final camera collision, and target picking, MSUI runs combat-feedback
aging and then `UpdateSpellPresentation` (`Program.cs:1664-1702`).

`UpdateSpellPresentation` has this exact order (`Program.Casting.cs:250-258`):

1. tick every effect instance and missile;
2. reconcile aura-state models from current unit aura descriptors;
3. reconcile remote channel models/holds from `UNIT_CHANNEL_SPELL`;
4. hide completed/failed cast bars whose display deadline elapsed.

This means a missile arrival callback can create an impact during step 1, and that impact is available to the
same frame's render. Aura and remote-channel state is descriptor-polled once per update, after missile ticking.

## 1.3 World render order

The relevant world order is fixed in `Program.cs:2031-2092`:

1. player character (publishes the current player skeleton pose);
2. streamed creatures (publish creature poses);
3. selection ring;
4. spell effect meshes, including queued ground decals;
5. spell ribbons;
6. ordinary doodad/world particles;
7. spell-only particle simulation and rendering;
8. water.

Mesh and ribbon enumeration therefore sees the current frame's unit skeletons. Spell particles are simulated
after those skeletons are published and after opaque units/effect meshes have populated depth. Transparent
spell meshes, ribbons, and particles are not globally depth-sorted with one another; their class order above
is the ordering law.

HUD spell order is separate: floating world combat text, unit frames/aura bar/minimap, center combat text,
casting bar, action bars, other panels, and finally the spellbook (`Program.CombatFeedback.cs:120-171`).

---

# 2. Spell DBC/data layer and consumed-field ledger

## 2.1 `SpellInfo` and derived classifications

`Formats/SpellCatalog.cs:4-25` is the complete in-memory spell row used by MSUI. It stores:

- identity/display: id, localized name, rank, icon path, description;
- attributes: `Attributes`, `AttributesEx2`, `AttributesEx3`;
- casting: normal/channel interrupt flags, cast-time index/value, duration index/value;
- targeting: `Targets`, only the first `EffectImplicitTargetA` as `ImplicitTarget`, all three implicit A/B arrays;
- recovery: spell recovery, category recovery, start-recovery category/time;
- resource: power type, flat mana cost, percentage cost;
- presentation: visual id, projectile speed, range index, school;
- three effect ids, aura ids, effect misc values, effect item types;
- two tools, eight reagent pairs, created item, and required spell focus.

Derived flags are exact bit tests (`SpellCatalog.cs:17-25`):

| Derived property | MSUI formula |
|---|---|
| passive | `Attributes & 0x40 != 0` |
| hidden client-side | `Attributes & 0x80 != 0` |
| ranged | `Attributes & 0x2 != 0` **or** auto-repeat |
| auto-repeat | `AttributesEx2 & 0x20 != 0` |
| on-next-swing | `Attributes & 0x404 != 0` |
| normal cast movement interrupt | `InterruptFlags & 0x08 != 0` |
| channel movement interrupt | `ChannelInterruptFlags & 0x08 != 0` |
| display classification | channel if channel flags are nonzero, else cast-time if `CastTimeMs>0`, else instant |

The classification string is diagnostic/UI metadata; routing is driven by server packets. A spell can receive
START/GO regardless of what the local classification says.

## 2.2 Exact DBC columns

`SpellCatalog.Load` requires `Spell.dbc` with at least 173 fields and `SpellIcon.dbc` with at least two
(`SpellCatalog.cs:58-65`). Icon paths have `.blp` appended when absent (`:67-74`). Cast time and duration use
the first numeric value from their referenced auxiliary rows (`:78-85`); range reads min, max, and flags bit
0 as “melee” (`:87-97`).

| Meaning | `Spell.dbc` column | Load site |
|---|---:|---|
| id / school | 0 / 1 | `SpellCatalog.cs:102,115` |
| Attributes / Ex2 / Ex3 | 6 / 8 / 9 | `:108` |
| Targets / required focus | 13 / 15 | `:110,124` |
| cast-time index | 18 | `:105` |
| recovery / category recovery | 19 / 20 | `:111` |
| interrupt / channel interrupt | 21 / 23 | `:109` |
| duration index | 30 | `:105` |
| power type / flat cost | 31 / 32 | `:112` |
| range index / speed | 36 / 37 | `:114-115` |
| tools | 39..40 | `:125-127` |
| reagent item ids / counts | 41..48 / 49..56 | `:128-135` |
| three effect ids | 61..63 | `:118` |
| implicit A / B | 82..84 / 85..87 | `:110,120-121` |
| three aura ids | 91..93 | `:119` |
| three effect item types | 103..105 | `:123` |
| three effect misc values | 106..108 | `:122` |
| visual id | 115 | `:113` |
| icon id | 117 | `:104` |
| name / rank / description | 120 / 129 / 138 | `:107,114` |
| percent cost | 156 | `:112` |
| start recovery category/time | 157 / 158 | `:113` |

Created-item lookup only recognizes effect id 24 and returns that effect slot's item type
(`SpellCatalog.cs:136-138`). Profession-recipe filtering separately treats effect id 53 as recipe-like
(`Program.Spellbook.cs:110-114`), but the catalog does not derive a created item from effect 53.

`FindKnownByName` considers only the server-known roster, uses case-insensitive exact name equality, and picks
the highest numeric spell id (`SpellCatalog.cs:48-50`). It does not parse rank suffixes or macro conditionals.

## 2.3 SpellVisual chain

`SpellVisualCatalog` parses exactly three DBCs (`Formats/SpellVisualCatalog.cs:174-180`):

- `SpellVisual.dbc`, 16 or more fields;
- `SpellVisualKit.dbc`, 35 or more fields;
- `SpellVisualEffectName.dbc`, 5 or more fields.

All foreign keys fold both `0` and `0xFFFFFFFF` to none (`SpellVisualCatalog.cs:97-99`). Model paths replace
slashes and convert `.mdx`/`.mdl` to `.m2` at the catalog boundary (`:100-112`). An unresolved effect-name row
is silently omitted from a kit; a kit can therefore resolve successfully with zero effect models (`:133-145`).

`SpellVisual.dbc` consumption (`:202-217`):

| field | MSUI meaning |
|---:|---|
| 1 | precast kit |
| 2 | cast kit |
| 3 | impact kit |
| 4 | state/aura kit |
| 5 | channel kit |
| 6 | **not read** (`hasMissile` is not a gate) |
| 7 | missile `SpellVisualEffectName` id |
| 9 | ordinal into the destination attachment table |
| 10 | parsed missile sound id; not played |

Out-of-range destination ordinals default to attachment `0x22`. The ordinal table is
`[0x14,0x22,0x13,0x15,0x16,0x11,0x17,0x18,0x19,0x0F,0x10]`
(`SpellVisualCatalog.cs:81-91`). A nonzero missile effect that cannot resolve later becomes
`Spells\ErrorCube.m2`; a truly absent missile effect takes the ammo/weapon fallback and can remain invisible
(`Program.Casting.cs:86-109`).

`SpellVisualKit.dbc` consumption (`SpellVisualCatalog.cs:191-200`): field 2 is body animation,
fields 3..11 are nine effect-name ids, and field 13 is a parsed but unplayed sound. The nine fixed attachment
ids, in field order, are `0x14,0x22,0x13,0x15,0x16,0x11,0x17,0x18,0x19`
(`:73-79`). All populated/resolved slots fire; MSUI does not select only one slot.

Stage lifetime policy is (`SpellVisualCatalog.cs:114-128`):

| stage | lifetime owner |
|---|---|
| precast | persistent until spell-keyed reap |
| channel | persistent until channel reap |
| state | aura-state until descriptor disappearance |
| cast | self-terminating |
| impact | self-terminating |

## 2.4 Parsed versus operational spell fields

MSUI parses more metadata than it enforces. This distinction is essential for faithful reproduction.

| Field family | Operational use |
|---|---|
| name/rank/icon/description | spellbook, action/macro/pet/profession UI |
| passive/hidden | player cast/spellbook visibility gates |
| ranged/auto-repeat/next-swing | local cast state, sheath, ranged visual fallback |
| interrupt flags | movement cancellation only |
| Targets + first implicit A | local self/unit target binder |
| range | selected-unit local distance gate and hotkey color |
| power/cost/% | local admission and action icon tint |
| recovery/GCD | local approximate cooldowns |
| visual/speed | all effect stages and missiles |
| effect ids | attack icon, recipe filtering/created item only |
| aura ids | stored for diagnostics/catalog completeness; not locally evaluated |
| misc values/implicit B | stored; not used to execute gameplay |
| tools/reagents/focus | profession wrapper only |
| school | diagnostics/combat metadata only; no renderer branch |
| cast time/duration from DBC | diagnostics; server START/channel duration drives the live bar |

Fields not represented include most other `AttributesEx` words, stances/forms, equipped-item requirements,
aura-state prerequisites, caster/target aura requirements, facing, radius, amplitude, chain targets, effect
base points/dice, proc metadata, mechanic/dispel type, damage class, prevention type, totem categories, and
many other Spell.dbc fields. Section 19 records the resulting behavior boundary.

---

# 3. Spell roster, spellbook, action bars, macros, items and pet entry points

## 3.1 Authoritative player roster and 120 action slots

`PlayerActions` owns a 120-element action array, a known-spell set, and spell-id-keyed local cooldowns
(`Net/PlayerActions.cs:13-22`). `SMSG_ACTION_BUTTONS` reads sequential four-byte packed actions; kind is the
top byte and action id the low 24 bits (`PlayerActions.cs:31-43`). Kinds are spell `0x00`, macro `0x40`, and
item `0x80` (`:3-9`).

`SMSG_INITIAL_SPELLS` reads one ignored byte, a `u16` count, then `u16 spell + u16 ignored` per known spell.
It then reads a cooldown count and fourteen-byte entries: `u16 spell`, ignored `u16`, `u16 category`,
`u32 spellMs`, `u32 categoryMs`; MSUI stores `max(spellMs,categoryMs)` when greater than 1 ms
(`PlayerActions.cs:45-70`). Learned, removed, and superseded messages mutate the set and rewrite/clear matching
spell actions (`:73-89`; network dispatch `Program.Net.cs:449-473`).

Trainer/talent/profession learning is not a second spell roster. Those panels observe the same
`SMSG_LEARNED_SPELL` mutation (`Program.Net.cs:452-459`). Trainer buying and talent learning send their own
service requests, then wait for the known-spell delta; they do not locally grant the runtime spell.

## 3.2 Spellbook filtering and activation

The spellbook builds from the known-spell set on every draw (`Program.Spellbook.cs:50-65`). It excludes:

- catalog misses;
- passive and hidden-client-side rows;
- skill recipes whose effects contain 24 or 53;
- status aura ids 2479, 15007, and 26013;
- rows with no skill line, except attack 6603.

Remaining spells group by skill line, sort by name then rank, expose at most eight tabs and twelve spells per
page (`Program.Spellbook.cs:50-78`). A click first tries to open a profession; otherwise it calls the ordinary
`TryCast` route (`:141-170`). Dragging more than six scaled pixels produces a spell payload that can be dropped
onto a main action slot, immediately updating local state and sending `CMSG_SET_ACTION_BUTTON`
(`:92-107`; `Program.ActionBars.cs:500-507`). Tooltips contain name, rank and description only—no cast time,
cost, range, reagent or cooldown detail.

## 3.3 Main action bar

Page-relative main action slots cover six pages of twelve; the secondary bars use fixed wire slots 60, 48,
24 and 36 (`Program.ActionBars.cs:27-30,519-538`). `UseAction` routes spell, item and macro kinds. Spell 6603 is
special: it commits the selection and begins melee attack instead of entering `TryCast`
(`Program.ActionBars.cs:62-80`).

The main twelve buttons implement icon substitution, usability tint, flashing, local cooldown swipe, pressed,
hover, checked, equipped border, range-colored hotkey, and item stack count (`Program.ActionBars.cs:300-455`).
The flash phase is 0.4 seconds on and 0.4 seconds off (`:303-308`). Checked state covers active attack,
auto-repeat, pending ordinary cast, and queued next-swing; flashing covers attack engagement or auto-repeat
(`:986-996`).

Main-button usability is deliberately narrower than `TryCast`: a spell is gray only when the player is dead,
blue-tinted when power is insufficient, and otherwise usable. It does not reflect local cooldown/GCD,
target-shape refusal, mount, pending cast, reagents, equipment, forms or server restrictions
(`Program.ActionBars.cs:932-977`). Range uses the same distance formula as cast admission (`:999-1029`).
Secondary bars only draw an icon and quickslot ring and invoke `UseAction`; they do not draw the main bar's
cooldown, usability, range, flash, checked or drag state (`:541-605`).

Cooldown shading receives **elapsed** fraction. It begins at `-π/2 + elapsed·2π`, uses a radius 72% of button
width, and emits 28 black 60%-alpha triangles covering the not-yet-elapsed angular remainder
(`Program.ActionBars.cs:1083-1097`). Item actions force displayed cooldown to zero (`:388-389`).

Weapon-dependent spell icons are selected by `Engine/UI/ActionIconLaw.cs:23-44`:

- effect 0 == 78 uses the equipped main-hand icon, else `Interface\Buttons\Spell-Reset.blp`;
- ranged+auto-repeat uses the ranged weapon icon unless the equipped subclass is thrown (16);
- everything else uses the SpellIcon path.

## 3.4 Macro entry

Macros scan every nonblank, non-comment line in order (`Program.Macro.cs:76-102`). `/cast NAME` resolves the
highest-id known spell with an exact case-insensitive name and calls `TryCast`; `/use NAME` invokes item use;
`/startattack` uses the melee engagement route. Execution does not stop after one successful cast line, so
later lines run and are usually rejected by the pending/GCD gates. There are no ranks, conditionals, targets,
sequence/reset syntax, stopcasting, or cursor targeting. Macro icons scan for the first recognizable `/cast`
or `/use` line (`Program.Macro.cs:105-123`).

## 3.5 Item-triggered spells

Items bypass `TryCast`. Right-clicking a non-equippable bag item sends `CMSG_USE_ITEM` with the item template's
`UseSpellIndex`; an action-bar item searches backpack then equipped bags and always sends spell slot 0
(`Program.Inventory.cs:480-512,574-600`). The five-byte body is `u8 bag, u8 slot, u8 spellSlot, u16 0`
(`Net/WorldSession.cs:649-654`). There is no local spell target, power, GCD, cooldown, pending-cast, range or
reagent admission on this route. If the server subsequently sends START/GO, the ordinary visual/cast router
handles the resulting spell id exactly like any other server spell.

## 3.6 Pet spells and commands

`SMSG_PET_SPELLS` stores a controlled pet GUID, duration/status bytes, and exactly ten packed actions; variable
spell/cooldown tails are not parsed (`Program.Pet.cs:14-34`). Spell-looking action types `0x81`, `0xC1`, and
`0x01` use the ordinary spell catalog only for their icon (`:111-130`). Clicking sends
`CMSG_PET_ACTION = u64 petGuid + u32 packedAction + u64 targetGuid`; spell actions use the current selection or
the pet itself, command attack uses the selection, and other commands can use zero
(`Program.Pet.cs:74-107`; `Net/WorldSession.cs:271-275`).

Pet spells do **not** pass through player `TryCast`, player power/range/GCD/pending state, or the player
spellbook. Their server-emitted START/GO and combat log packets still enter the shared incoming presentation
paths using the pet/target GUIDs.

---

# 4. Local cast admission, targets, ranges, power and outbound wire

## 4.1 Admission order

The exact `TryCast` sequence is `Program.ActionBars.cs:83-160`:

1. require a network object, catalog row, and non-passive spell;
2. if clicking the already active auto-repeat, send cancel, clear it, sheath state 0, clear body hold, and stop;
3. reject an identical already-queued next-swing spell;
4. reject per-spell local cooldown or global cooldown;
5. for ordinary spells only, reject when another ordinary spell is pending;
6. reject mounted casting unless `Attributes & 0x01000000` is set;
7. resolve target shape and candidate;
8. if the resolved target is the selected nonself unit, apply the range gate;
9. apply the power gate;
10. send the cast;
11. on successful local send, set auto-repeat, next-swing queue, or ordinary pending state;
12. start local GCD/start-recovery state when `StartRecoveryMs>0`.

The local admission does not check known-spell membership when invoked directly; normal UI/macro sources only
offer known spells, but a diagnostic or other internal caller can call `TryCast` with any non-passive catalog
id. It also does not require the player to be alive; death exists only in action-button tint. The server is
expected to refuse unauthorized, dead, silenced, shapeshift-invalid, equipment-invalid or otherwise illegal
casts.

## 4.2 Target-word derivation

`CastTargetLaw.TargetMask` starts with low 16 bits of `Spell.Targets` and consults only
`EffectImplicitTargetA[0]`, exposed as `SpellInfo.ImplicitTarget` (`Net/CastTargetLaw.cs:41-56`):

| implicit target | mutation |
|---:|---|
| 1 | clear explicit-gate bit `0x0400` |
| 5 | clear ally-corpse bit `0x8000` |
| 6, 53 | add enemy bit `0x0080` |
| 21, 45 | add assist bit `0x0100` |
| 23 | add bit `0x0800` |
| 25, 63 | add unit bit `0x0002` |
| 26 | add bit `0x4000` |
| 35 | add party bit `0x0008` |
| 57, 61 | add raid bit `0x0004` |

The supported unit-shaped union is unit, raid, party, enemy, assist, enemy corpse, explicit gate, and ally
corpse (`CastTargetLaw.cs:36-40`). A zero word resolves to implicit self with outbound GUID zero. Any remaining
non-unit-shaped bit refuses as `UnsupportedTargetShape`. Otherwise MSUI tests the current selection, then—by
default—self-cast fallback (`:59-70`).

Candidate satisfaction (`:73-84`) is intentionally simple:

- party/raid bits clear only for self;
- assist clears for self or exactly `Friendly` faction reaction;
- enemy clears for an attackable nonself;
- plain unit clears for any candidate, including dead;
- explicit gate clears for any nonself;
- corpse bits require dead plus friendly/enemy relation.

This is not party/raid membership logic. `Friendly` comes from `ReactionPlayerToward == Friendly`, and hostile
eligibility from `CanAttack` (`Program.ActionBars.cs:190-220`). Neutral/ambiguous reactions do not count as
assist-friendly. There is no local facing or line-of-sight test.

## 4.3 Range formula

Range is evaluated only when the resolved GUID equals the current selection and is not the player
(`Program.ActionBars.cs:136-142`). It uses three-dimensional squared distance between controller position and
target position (`:165-184`). Let `Rs` and `Rt` be server descriptor combat reaches.

- melee row: `min=0`, `max=max(Rs+Rt+1.3333, 5)`;
- nonmelee row with both raw min/max ≤ 0: no check;
- otherwise `max=rawMax+Rs+Rt`; if raw min is nonzero, `min=rawMin+Rs+Rt`;
- `d² < min²` is “too close”; `d² > max²` is “out of range.”

Missing player reach defaults to 1.5 (`Net/ObjectFields.cs:223-227`). Missing entity/range/controller data means
no range refusal. Self-fallback and implicit-self never undergo a range test.

## 4.4 Power formula

`SpellResourceGate` is implemented in the production-compiled diagnostic partial
`Program.DevTools.SpellSweep.cs:37-47` and called by `TryCast`. Available power is the player's current value for
`PowerType`. Cost is:

`flat ManaCost + floor(baseAmount * ManaCostPercent / 100)`

where `baseAmount = BaseMana` for power type 0, otherwise that power type's maximum. Missing player state fails
the gate. Arithmetic is unsigned 32-bit C# arithmetic; there is no explicit overflow saturation. The client
does not locally subtract the cost; the authoritative descriptor update does that.

## 4.5 Outbound cast packet

The ordinary player builder supports exactly two shapes (`Net/WorldSession.cs:299-312`):

| resolved target | body |
|---|---|
| implicit self (`targetGuid=0`) | `u32 spellId + u16 mask 0` |
| self or other unit GUID | `u32 spellId + u16 mask 0x0002 + packedGuid` |

There are no outbound ground destination, source position, game object, item, corpse-specific, trade item,
string or multiple-target builders. Unsupported shape is refused before sending. Cancel-cast, cancel-channel
and cancel-aura each send only `u32 spellId`; cancel-auto-repeat has an empty body
(`WorldSession.cs:314-337`).

`NetworkClient.CastSpell` returns false unless in world with a session and suppresses exceptions; cancel cast
and channel are best-effort void operations, while aura cancel returns success/failure. A locally successful
send means only that the packet reached the session send path, not that the server accepted the spell.

---

# 5. Incoming spell network packets and dispatch

## 5.1 START and GO decoders

`Net/SpellPackets.cs:16-47` decodes build-5875 spell presentation packets.

`SMSG_SPELL_START`:

1. read and discard the first packed GUID;
2. retain the second packed GUID as caster;
3. read `u32 spellId`, `u16 castFlags`, `u32 castTimeMs`;
4. read targets;
5. when flag `0x20` is set, retain ammo display id and discard the following `u32` ammo field.

`SMSG_SPELL_GO`:

1. read/discard first packed GUID, retain second as caster;
2. read `u32 spellId`, `u16 castFlags`;
3. `u8 hitCount`, followed by **raw `u64`** hit GUIDs;
4. `u8 missCount`, followed by raw `u64 guid + u8 reason`; reason 11 consumes one extra byte;
5. targets and optional ammo as above.

Target mask consumption (`SpellPackets.cs:59-67`) retains one packed unit GUID for any of bits
`0x0002|0x0800|0x0200|0x8000`, consumes/discards an item GUID for `0x0010|0x1000`, consumes/discards a source
vector for `0x0020`, retains a destination vector for `0x0040`, and consumes/discards a C string for `0x2000`.
The retained explicit target/destination is diagnostic data; GO effect routing uses the hit/miss arrays.

`SMSG_CAST_RESULT` is `u32 spell + u8 status`, with a reason byte read only when status is 2 and bytes remain
(`SpellPackets.cs:50-55`). Dispatch ignores nonfailure statuses (`Program.Net.cs:643-648`).

## 5.2 Other spell packets

The live dispatcher implements (`Program.Net.cs:637-680`):

| opcode | consumed layout | action |
|---|---|---|
| `SMSG_SPELL_START` | parser above | `ApplySpellStart` |
| `SMSG_SPELL_GO` | parser above | `ApplySpellGo` |
| `SMSG_CAST_RESULT` status 2 | spell/status/reason | server error + failure path |
| `SMSG_SPELL_FAILED_OTHER` | raw `u64 caster + u32 spell` | interrupted failure |
| `SMSG_SPELL_DELAYED` | discard `u64`, read `u32 delay` | push ordinary cast bar |
| `MSG_CHANNEL_START` | `u32 spell + u32 duration` | starts **local player's** channel |
| `MSG_CHANNEL_UPDATE` | `u32 remaining` | updates/stops local channel |
| `SMSG_PLAY_SPELL_VISUAL` | raw `u64 unit + u32 kit` | pushed kit/body one-shot |
| `SMSG_CANCEL_AUTO_REPEAT` | no parsed payload | clears local repeat/sheath/hold |

`MSG_CHANNEL_START/UPDATE` contain no caster in this handler; MSUI treats them as the local player's channel.
Remote channels are not built from these messages—they are observed from each remote unit's channel-spell
descriptor in Section 13.

Aura duration has its own dispatcher (`Program.Net.cs:443-447`). Object updates are wrapped by a before/after
aura snapshot (`Program.Net.cs:773-792`). Out-of-range update packets can remove multiple GUIDs while
`ObserveAuraObjectUpdate` is invoked only with the update object's `u.Guid`; individual removed units are not
emitted as aura-remove verdicts, although the later aura-state visual scan reaps absent state.

## 5.3 Separation from combat-log packets

START/GO describes spell presentation and target resolution. Damage, healing, power, periodic ticks, shield
damage and misses also arrive through combat-log opcodes, decoded independently
(`Program.Net.cs:698-713`). MSUI does not join a GO target to a later damage log to decide impact visuals, and
does not delay world combat text until a visual missile arrives. Each stream acts when received.

---

# 6. Cast router: START, GO, impact, failure and the five visual stages

## 6.1 START / precast

`ApplySpellStart` is `Program.Casting.cs:24-46`:

1. mark diagnostic sequence stage PRECAST;
2. look up `SpellInfo` and select an effective visual (including ranged-weapon fallback);
3. resolve the visual's precast kit;
4. create every resolved effect model as `StageLife.Persistent` on the caster;
5. begin the kit's body animation as a hold on player or creature;
6. for the local player, sheath ranged spells with state 2;
7. for the local player only, start a cast bar when server `CastTimeMs>0` **and the spell is not ranged**.

Instant START packets therefore still create/release visual stages and body animation, but have no cast bar.
A ranged spell with a nonzero cast time also has no MSUI cast bar because `Ranged` is an explicit suppression.
START target data is not used to position a precast kit; the kit attaches to the caster.

## 6.2 GO / cast stage

`ApplySpellGo` is `Program.Casting.cs:48-110`:

1. resolve effective visual and cast kit;
2. reap the caster/spell's persistent instances (precast/channel lifetime family);
3. spawn all cast-kit effects as self-terminating on the caster;
4. release the kit body animation into a one-shot;
5. for the local player, clear matching pending ordinary and queued next-swing state, complete the cast bar,
   notify profession/hearth observers, and start `max(RecoveryMs,CategoryRecoveryMs)` on this spell id;
6. log target data;
7. derive missile model/ammo fallback;
8. process every hit followed by every miss.

If `SpellInfo.Speed<=0`, or the effect source is unavailable, every target receives immediate arrival handling
in GO list order. If speed is positive, one missile instance is queued per hit/miss GUID. If both arrays are
empty, no impact is generated even when the packet's explicit target or destination is populated.

## 6.3 Impact and state-arrival body animation

On a successful arrival, `ApplySpellImpact` spawns the impact kit as self-terminating on the target
(`Program.Casting.cs:113-130`). It then plays two body animation ids in order: impact kit, then state kit
(`:132-157`). State **effect models** are not spawned here; they belong to the aura descriptor watcher. Only
the state kit's authored body animation is an arrival one-shot.

Body animation rules:

- absent/zero animation: nothing;
- animation 8, 9 or 10: route through the wound-reaction helper;
- any other animation: player `TriggerOneShot`, creature exact authored release action.

There is no synthesized wound when an impact kit has no body animation. On a missed arrival, no impact kit is
spawned. Only miss reasons 3 and 5 generate a dodge/block body reaction; every other miss reason has no
SpellVisual body response (`Program.Casting.cs:113-121`). Combat-log miss text remains independent.

## 6.4 State and channel stages

- **State models:** descriptor-polled per `(unit,spell)` and live until that aura id disappears; Section 14.
- **Local channel:** begins from `MSG_CHANNEL_START`, holds the channel body animation and persistent channel
  kit; Section 13.
- **Remote channel:** descriptor-polled from each nonself unit's `ChannelSpell`; Section 13.

## 6.5 Failure

`ApplySpellFailure` (`Program.Casting.cs:175-187`) clears matching local pending/queued state, resets the entire
local GCD deadline to zero, clears the player's spell hold, paints a failed cast bar, and reaps persistent
instances for that caster/spell. A remote failure clears that creature's hold and the persistent kit.
Self-terminating cast/impact instances already emitted are not reaped by this function.

Known `SMSG_CAST_RESULT` reason names/text are a small table
(`Net/SpellCastResultNames.cs:5-37`): dead 0x13, don't-report 0x17, interrupted 0x23/0x24, LOS 0x2F,
not-mounted 0x39 (named but generic text), not-ready 0x3C, no-power 0x4D, range 0x59, tools 0x5C,
spell-in-progress 0x61, reagents 0x78. Unknown values show “Spell failed.” Failure text is pushed through the
red center-combat-text channel and recorded as a spell-error verdict (`Program.DevTools.SpellErrors.cs:9-31`).

## 6.6 Effective ranged visual

`EffectiveSpellVisual` keeps the spell's own VisualId whenever that row exists, and also keeps it for every
non-ranged spell even if missing (`Program.Casting.cs:514-525`). Only a ranged spell whose visual row is absent
falls back to equipment:

- local player: last equipped piece whose equipment slot is 17 or inventory type 15/25/26;
- remote caster: virtual item display slot 2;
- item display field `SpellVisualId` becomes the effective visual.

This fallback affects all resolved stages and the projectile chain. It is separate from action-icon equipment
substitution.

---

# 7. Effect-instance ownership, lifetime, attachments and pose lookup

## 7.1 Unified instance model

`SpellEffectSource` is the runtime owner for kit effects, aura-state effects, channels, cast flashes, impacts,
missiles and diagnostic pushed visuals (`World/Units/SpellEffectSource.cs:6-12`). An instance records asset,
unit, spell, lifetime class, requested attachment, start/end, stage label, and—when a missile—target,
destination attachment, position/direction, release/deadline, release marker and arrival callback (`:18-56`).

`SpawnKit` iterates every resolved kit effect (`SpellEffectSource.cs:101-128`). Before spawning a persistent or
aura-state kit it removes all existing instances with the same unit, spell and lifetime class. It does not
replace self-terminating instances. Each effect path is independently loaded; missing assets and assets with no
visible mesh/particle/ribbon content are skipped. Self-terminating lifetime is the effect M2's first sequence
duration, or 1.0 second when there is no usable sequence (`SpellAttachment.cs:147-165`). A manually supplied
lifetime overrides that rule only on the legacy/diagnostic overload.

`Reap(unit,spell,life?)` removes only non-self-terminating instances and, when specified, only that lifetime
class (`SpellEffectSource.cs:209-211`). This makes cast GO able to remove precast without deleting an aura-state
instance of the same spell. `Tick` removes expired self-terminating effects and advances missiles
(`:213-253`).

## 7.2 Pose source

`Program.Casting.cs:435-444` supplies the exact pose:

- local player: `CharacterRenderer.SpellPose` built from current unit state;
- loaded creature: `CreatureRenderer.TryGetSpellPose`;
- any other known entity: position/orientation and translation only, with no model/skeleton;
- missing entity: `SpellUnitPose.Missing`.

`SpellUnitPose` stores absolute unit transform, source M2 and the most recently rendered skin matrices.
`BoneMatrix(i)` re-adds the bone pivot because animator skin matrices already contain `T(-pivot)`
(`SpellAttachment.cs:6-29`). Effects therefore ride the same body pose used for that unit's current render.

## 7.3 Attachment resolution

`SpellAttachment.Resolve` tries the requested semantic attachment id, then `0x0F`, then `0x13`
(`SpellAttachment.cs:63-91`). For each id it first consults `AttachmentLookup[id]`, then linearly scans the M2
attachment records by id (`:93-109`). If all fail, the caller uses the unit base transform.

For a resolved point, current world transform is (`SpellAttachment.cs:124-145`):

`T(attachmentLocal - bonePivot) · posedBoneFrame · unitTransform`

where `posedBoneFrame = T(bonePivot) · skin[bone]`. This simplifies to the model-space attachment point through
the raw skin and unit transform. With no live pose, the fallback is `T(attachmentLocal)·unitTransform`.

Nonmissile `TryTransform` re-resolves this attachment every enumeration/frame. If a resolved point is more than
12 yards from the unit base, MSUI overwrites translation with `unitPosition + (0,0,1.5)`
(`SpellEffectSource.cs:323-363`). This plausibility clamp also applies to missile destinations but not to the
initial source attachment helper.

Ground anchoring is based on the **requested** attachment being `0x13`, not on the resolved fallback id
(`SpellEffectSource.cs:294-305`). Thus an effect authored for another attachment that falls back to `0x13`
does not enter the ground-decal renderer.

## 7.4 Effect animation/time feeds

The same instances feed three enumerators (`SpellEffectSource.cs:256-320`):

- emitters: path includes unique instance id (`spell:<assetPath>#<id>`), transform, emitter definition,
  direct emitter texture, animation time/id, and evaluated local origin/rotation;
- meshes: id/path/model/transform/age/animation id/ground flag/custom texture;
- ribbons: id/path/model/transform/age/animation id.

Missiles request animation id 144. Other effect M2s request the first sequence's animation id, or zero. Age is
`now-started`, except missile age is `now-max(started,releaseAt)`. Before yielding emitters, the asset animator
evaluates the chosen clip (falling back to its first clip), derives emitter origin from the skin matrix, and
decomposes `T(pivot)·skin` to obtain local rotation (`SpellEffectSource.cs:263-289`).

Because the asset animator/skin belongs to the path cache rather than each instance, multiple same-path
instances share temporary evaluation storage. Enumeration is synchronous, so each yielded tuple contains
values calculated for that iteration; consumers must not retain a reference to the shared skin as instance
state.

---

# 8. M2 asset, animation-track and skeleton implementation

## 8.1 Accepted model and parsed content

`M2Reader.Parse` accepts `MD20`, rejects versions 264 and newer, and returns `null` on any exception
(`Formats/M2Reader.cs:1194-1212,1281-1288`). Vanilla view 0 is parsed inline. Particle-only models may have no
vertices/view and still load; the final requirement is any valid mesh, particle emitter or ribbon
(`M2Reader.cs:94-98,1239-1252,1283`).

The spell path can consume vertices/indices/submeshes/batches/textures/texture lookup, bones, attachments and
lookup, render flags, particles, ribbons, events, colors, transparency tracks/lookup, sequences and global
sequence durations (`M2Reader.cs:11-76`). It does not use collision geometry from an effect M2 for spell
collision; ground height comes from the world's collision/terrain service.

Coordinate conversion is applied while parsing. M2 render vertices, normals, pivots, attachments, emitter
positions, event positions and translation keys use `(x,z,-y)`; scale uses `(x,z,y)` and quaternions
`(x,z,-y,w)`. The runtime animation code assumes every model-local quantity already shares this Y-up basis
(`M2Animator.cs:15-27`).

## 8.2 Sequences and generic tracks

An M2 sequence supplies animation id, variation, absolute start/end timestamps, move speed, flags and blend
time (`M2Reader.cs:303-327`). `TryFindSequenceIndexByAnimationId` prefers variation 0, then the first other
variation (`M2Reader.cs:117-142`).

Each vanilla animation block is 28 bytes: `u16 interpolation`, `i16 globalSequence`, range array, timestamp
array and key array (`M2Reader.cs:329-370,1640-1705`). MSUI treats `AnimationRange.End` as an **inclusive** last
key index and validates the range endpoints against the sequence's absolute timestamp window; when a repeated
whole-track sentinel range is uninformative, it scans the sequence window instead
(`M2Reader.cs:383-430`).

Renderer-side `M2TrackSampling` (`M2Reader.cs:489-567`) has these laws:

- only interpolation type 1 is linear; all other types are step;
- global tracks use `seconds·1000 mod globalDuration` and ignore the current clip;
- ordinary tracks loop when `(sequence.Flags & 1)==0`, otherwise clamp;
- a valid range is narrowed again to timestamps inside sequence start/end;
- scalar/vector/short values interpolate only for type 1;
- fixed16 alpha is divided by 32767 and clamped to [0,1].

`M2Animator` has a separate global bone-track evaluator: interpolation type 0 is step and every nonzero type
is linear/spherical-linear (`M2Animator.cs:847-927`). Thus Hermite/Bezier are approximated differently in
effect material/emitter sampling (step) and skeletal global-track sampling (linear).

## 8.3 Skeleton bake/evaluation

The animator supports at most 160 bones (`M2Animator.cs:70-100`). It bakes variation-0 sequence tracks into
per-bone translation/rotation/scale arrays, using sequence-relative seconds and normalized quaternions
(`M2Animator.cs:547-615`). Static clips are rejected for normal unit animation but retained when effect
renderers request `includeStaticSequences:true`. A zero declared duration becomes the longest authored key
time; still-degenerate clips are dropped (`:617-635`).

The body/effect skeletal transform is row-vector order (`M2Animator.cs:33-48,672-760`):

- local animated bone = `Scale · Rotation · Translation(rest + animation)`;
- global = `local · parentGlobal`;
- skin = `T(-pivot) · global`.

Cross-fades blend translation/scale linearly and rotation spherically before composing matrices
(`M2Animator.cs:650-717`). Global sequence values are sampled after this blend and override/add their
corresponding component (`:719-731`). Packed GPU bones are three `vec4` rows per matrix
(`M2Animator.cs:820-827`).

The animator does **not** trust the sequence loop flag for chosen body clips. It hardcodes one-shot animation
ids `[1,7,9,16,17,18,19,20,21,22,23,24,30,37,39,85,87,88,117,187]`; all others loop
(`M2Animator.cs:113-135,552-564`). Effect material/emitter track clocks still use sequence flag bit 0 as noted
above.

## 8.4 Particle emitter record

The parser uses a 504-byte emitter stride and ten 28-byte scalar tracks beginning at +52
(`M2Reader.cs:2031-2044`). Operational fields are parsed in `M2Reader.cs:2053-2158`:

| record data | MSUI storage/use |
|---|---|
| +0/+4 | particle id / flags |
| +8,+12,+16 | converted position `(x,z,-y)` |
| +20/+22 | bone / direct texture index |
| +40 | blending type |
| +0x2A u16 | shape: 2 sphere, 3 spline, else plane |
| +44/+45 | particle type, head-or-tail (parsed; renderer supports head sprites only) |
| +48/+50 | texture rows/columns |
| +52, ten tracks | speed, variation, vertical range, horizontal range, gravity, life, rate, area length, area width, z-source |
| +332 | lifetime ramp midpoint |
| +336 | three inline BGRA color keys |
| +348 | three inline scale keys |
| +0x168..0x172 | two head-cell begin/end/repeat segments |
| +0x180..0x18C | twinkle speed/percent/min/max |
| +0x194 | plain float drag |
| +0x1DC | enabled/on-off track |

The parser additionally reconstructs an emitter bone spin/chain (`M2Reader.cs:776-940,2155-2157`). In the
normal spell-effect path, `SpellEffectSource` supplies origin/rotation from the full effect animator. If it
cannot, `SpellParticleSystem` falls back to `emitter.SampleBonePosition` (`SpellParticleSystem.cs:128-137`).

## 8.5 Ramp and cell formulas

At particle lifetime fraction `t`, midpoint is clamped to `[.001,.999]`; choose segment 0→1 or 1→2, compute
segment fraction, then inset it as `f=.99f+.005`. Size and BGRA-converted RGBA interpolate across that segment
(`M2Reader.cs:996-1021`).

Head flipbook sampling (`M2Reader.cs:1023-1071`): split at midpoint; inset the segment fraction; when repeat is
not 1 use `fract(segmentT*repeat)`; forward cell span is `end-begin+1`, reverse span is `end-begin-1` with base
`begin+1`; result is `floor(base+span*t)&0xFF`. Column is `index%cols`, row is
`(index/cols)%rows`; returned UV is cell offset and `1/cols,1/rows` scale.

---

# 9. Effect meshes, materials, billboard bones and ground/AoE visuals

## 9.1 Mesh upload and animation

`SpellEffectMeshRenderer` caches a GPU mesh by effect path (`World/Units/SpellEffectMeshRenderer.cs:210-279`).
Each vertex carries position, normal, UV, four normalized weights and four bone indices (16 floats). A
zero-total weight becomes weight 1 on bone 0; indices beyond 159 become 0 (`:214-234`). Batches come from view
0 submeshes/materials; invalid/empty submeshes are skipped. If no batch survives, the full index array is one
two-sided untextured batch (`:257-279`).

Each frame the mesh chooses requested animation id or the first baked effect clip, evaluates at instance age,
applies billboard-bone rewrites, and uploads up to 160 bones (`SpellEffectMeshRenderer.cs:125-146`). There is no
per-instance effect-animation state beyond age; looping/clamping follows Section 8.

## 9.2 Material behavior

A batch is transparent when blend mode ≥2, NoZWrite, or any referenced color/transparency key is below full
alpha (`SpellEffectMeshRenderer.cs:25-37,440-450`). Rendering is two unsorted passes: opaque then transparent
(`:117-204`). Transparent depth writes are off; two-sided materials toggle face culling. Texture resolution is
`batch.TextureIndex → TextureLookup → Textures`, loaded with mipmaps and repeat disabled (`:282-303`). A
missile's custom ammo texture is used only when the batch's normal texture is missing (`:154-161`).

MSUI samples batch color RGB/alpha and transparency lookup/track at effect age
(`SpellEffectMeshRenderer.cs:162-181`). It does not evaluate texture animations/transforms, multiple texture
units, material layers, shader id, priority plane, environment mapping or other M2 combiner behavior. Alpha
key blend 1 uses cutoff 0.25. Render flags consumed are Unlit `0x1`, TwoSided `0x4`, and NoZWrite `0x10`
(`Formats/M2Reader.cs:179-190`).

Lighting is fixed white ambient intensity 1 plus white +Z sun intensity .35; fog is black from 10,000 to
20,000 yards and is effectively disabled for normal scenes (`SpellEffectMeshRenderer.cs:104-115,618-637`).
Blend modes are: 3/4 `SrcAlpha,One`; 5 `DstColor,Zero`; 6 `DstColor,SrcColor`; all others
`SrcAlpha,OneMinusSrcAlpha` (`:453-458`). There is no transparent instance/batch depth sort.

## 9.3 Billboard bones

After normal skeletal evaluation, `ApplyBillboardBones` transforms camera axes into model space and walks the
bone palette, propagating parent replacements to children (`SpellEffectMeshRenderer.cs:461-505`). Flag 0x04
removes parent rotation. Billboard mask 0x78 selects:

- 0x08 spherical facing;
- 0x40 preserve authored WoW Z axis;
- 0x10 preserve authored WoW X axis;
- otherwise the 0x20-style preserve WoW Y axis.

It constructs a facing basis accounting for the `(x,z,-y)` conversion and writes a replacement skin matrix
(`SpellEffectMeshRenderer.cs:506-555`). This billboard handling applies to effect **meshes**, not the dedicated
particle sprite renderer (which always camera-faces its head quads).

## 9.4 Ground effect recognition

Ground rendering is an effect-model presentation rule, not gameplay AoE logic. An instance is eligible only
when its requested kit attachment is `0x13` (`SpellEffectSource.cs:294-305`). Within a batch,
`FindGroundQuad` requires exactly four unique vertices, local Y approximately zero, first weight ≥254, no
other weights, and the same first bone (`SpellEffectMeshRenderer.cs:306-327`). Anything more complex is drawn
as an ordinary mesh, not projected.

## 9.5 Ground tessellation/projection

The four corners are bilinearly subdivided into a 10×10 grid. Every grid vertex transforms to world, calls
`SpellGroundHeight(x,y,authoredZ)`, and when a height exists becomes `height+0.015`; camera position is then
subtracted. Each cell emits two triangles (`SpellEffectMeshRenderer.cs:330-405`).

`SpellGroundHeight` casts downward from authored Z+3 for six yards. A collision hit within three yards of the
authored Z wins; otherwise terrain height is returned (`Program.Casting.cs:447-453`). There is no projection
onto individual terrain/WMO triangles, no clipping to surfaces, and no slope-normal reorientation—only a
height field sampled per grid vertex.

Ground draws occur after normal mesh passes with blend enabled, depth writes off, culling off, and polygon
offset `(-1,-1)` (`SpellEffectMeshRenderer.cs:407-438`). The ground fragment shader multiplies texture RGB by
tint, texture alpha by opacity, and discards alpha ≤.001 (`:640-654`). A ground ring can visually represent
Frost Nova/Arcane Explosion/etc., but it does not select targets or define their radius.

---

# 10. Spell particle parsing, simulation and rendering

## 10.1 Isolation and pools

Spell effects use `World/Spells/SpellParticleSystem.cs`, not the doodad/portal `ParticleRenderer`
(`SpellParticleSystem.cs:11-22`; render routing `Program.cs:2066-2092`). Pools are keyed by the effect path plus
unique instance id and emitter index, so two live effect instances do not share particles
(`SpellParticleSystem.cs:31-35,81-105`; source path construction `SpellEffectSource.cs:288-289`).

Each pool stores definition, current transform/origin, texture, animation clock/id, model-space flag, attachment
flag, scale, emission accumulator/latch, xorshift seed and particles. Maximum is 1024 particles per pool;
simulation distance is a hard 250-yard origin cutoff (`SpellParticleSystem.cs:39-45`). An emitter beyond the
cutoff is not touched; its prior pool then enters orphan drain for that frame (`:118-193`).

The instance is classified as attached when its generated path does **not** contain the substring “Missile,”
case-insensitive (`SpellParticleSystem.cs:152-163`). This string heuristic—not instance metadata—decides whether
world-space particles ride a moving attachment or freeze in world space.

## 10.2 Emitter sampling

Per touched emitter, MSUI derives origin from supplied evaluated local origin or the emitter bone-chain
fallback, transforms it to world, samples all ten scalar tracks at the instance animation time/id, and derives
instance scale from transform X-axis length (`SpellParticleSystem.cs:128-177`). Invalid/nonpositive scale
becomes 1. Textures are the emitter's direct texture array index and use repeat disabled
(`SpellEffectSource.cs:444-449`; `SpellParticleSystem.cs:480-491`).

## 10.3 Integration

`Advance` clamps `dt` to 0.1 seconds (`SpellParticleSystem.cs:195-200`). For every existing particle:

1. add clamped dt to age; remove when `age>=life`;
2. clear the fresh flag (no follow-delta behavior is otherwise implemented);
3. `position += velocity·dt`;
4. gravity half-step: `position.up -= .5·g·dt²`, `velocity.up -= g·dt`;
5. drag: `velocity -= min(dt·drag,1)·velocity`;
6. for sphere+flag 0x80, remove when `dot(pre-gravity velocity, position-killOrigin)>0`.

The up component is Y for model-space particles and Z for all world-space particles
(`SpellParticleSystem.cs:201-237`). For model-space or attached world particles the kill origin is local zero;
for frozen missile particles it is the recorded world origin.

## 10.4 Emission timing and LOD

No new particles emit when life scalar ≤0. Enabled track gates rate; disabled resets continuous accumulation.
Distance LOD is `clamp(1-(distance-50)*.02, .25, 1)`, so full through 50 yards and at the 25% floor from
87.5 yards (`SpellParticleSystem.cs:239-275`).

- flag 0x8000: on a false→true rate gate, spawn `int(rate·distanceLOD)` once;
- otherwise: accumulator adds `rate·distanceLOD·dt`, spawns its integer part, then subtracts that part.

Pool capacity truncates excess births. There is no global particle budget, stochastic LOD thinning, emission
density setting, inherited unit/missile velocity, or multiple-birth compensation when a frame is longer than
the 0.1-second integration clamp.

## 10.5 Birth geometry

Birth kernel is `SpellParticleSystem.cs:280-354`.

**Sphere:**

- radius = `areaLength + rand·max(0,areaWidth-areaLength)`;
- latitude = symmetric random × vertical range;
- longitude = symmetric random × horizontal range;
- shell = `(cos(lat)cos(lon), cos(lat)sin(lon), sin(lat))`;
- position = radius×shell;
- direction aims from `(0,0,zSource)` when zSource !=0, otherwise +Z for flag 0x100, otherwise radial shell.

**Every non-sphere shape, including parsed Spline:**

- planar position = `(areaWidth/2·sym, areaLength/2·sym, 0)`;
- zSource direction as above, otherwise cone direction from random vertical/horizontal angles.

Speed is `emissionSpeed·(1+speedVariation·sym)` and is not clamped; negative speed reverses direction. Position
and direction then undergo `Rot90Z` and `Swap(x,y,z)=(x,z,-y)`.

Model-space particles retain local position/velocity and are transformed at draw. World-space particles bake
rotation/scale at birth; attached effects store only offset and re-add current origin when drawn, while
missile-classified particles store absolute world position. World velocity is normalized then multiplied by
speed and instance scale (`SpellParticleSystem.cs:325-353`). There are no Spline-specific curves, tail kernel,
tumble/spin, wind, child emitters, ground collision, or particle-to-particle interaction.

## 10.6 Lifetime ramp, twinkle and sprite selection

At draw, each particle samples the three-key color/size ramp and head flipbook using its own `age/life`.
Twinkle noise is a deterministic 128-value xorshift LUT indexed by
`(floor(clamp(twinkleSpeed·age,0,255))+phase)&0x7F`; a noise value above authored `TwinklePercent` suppresses the
quad, otherwise size is multiplied by the authored twinkle range (`SpellParticleSystem.cs:420-466,494-509`).
Particles with size≤0 or alpha≤.002 are not submitted.

For model-space particles, stored local position is additionally rotated by the current supplied bone
rotation, transformed by the current transform's rotation/scale and added to current origin. Their displayed
size is ramp scale × instance scale. World particles use absolute/re-anchored position and the same size rule.

## 10.7 Render law

Only camera-facing **head sprite quads** are rendered. Pools group by `(texturePath,raw blend byte)` and submit
GPU-instanced triangle strips (`SpellParticleSystem.cs:358-418`). Camera right is
`normalize(cross(forward,+Z))` with flat-right fallback; up is `normalize(cross(right,forward))`. The vertex
shader expands center by `(right·corner.x + up·corner.y)·size`, uses camera-relative position, and applies the
per-particle cell rectangle (`Shaders/particle.vert:13-39`). Fragment output is texture×ramp color and discards
alpha≤.003 (`Shaders/particle.frag:18-30`).

Depth testing is on, depth writes off, culling state is not explicitly changed here. Blend modes are 3/4
`SrcAlpha,One`, 5 `DstColor,Zero`, 6 `DstColor,SrcColor`, else ordinary alpha
(`SpellParticleSystem.cs:388-417,468-477`). Notably mode 3 uses source alpha rather than `One,One`. There are no
tail/streak quads, XY/model-facing quads, per-particle rotation, particle mesh types, lighting, fog, soft-depth
fade, or transparent depth sort.

---

# 11. Ribbon/trail simulation and rendering

## 11.1 Trail ownership and live sampling

Trails are keyed by `(effect instance id, ribbon index)`, so they are per visual instance
(`World/Units/SpellRibbonRenderer.cs:14-47`). For each live ribbon, MSUI finds requested animation sequence,
evaluates an effect animator at age, and step-samples the visibility track; hidden emitters do not enter the
seen set that frame (`SpellRibbonRenderer.cs:87-104`).

The joint is `T(bonePivot)·skin[bone]`, followed by instance transform. Ribbon head is authored position through
that world matrix. Ribbon axis is transformed local +Y, normalized with +Z fallback; above/below heights are
track-sampled and clamped nonnegative (`SpellRibbonRenderer.cs:125-136`).

## 11.2 Edge production and aging

Live-ribbon dt is `clamp(instanceAge-lastAge,0,.1)`. Existing edges expire after authored lifetime, whose parser
minimum is 0.25 seconds (`Formats/M2Reader.cs:1970-2008`). Gravity changes both top and bottom Z by
`2·gravity·dt` each frame (`SpellRibbonRenderer.cs:107-123`), an incremental linear sag—not a closed-form
`½gt²` calculation.

Accumulator adds `max(0,edgesPerSecond)·dt`. If it reaches one and the trail has fewer than 512 edges, MSUI
subtracts `floor(accumulator)` but appends **at most one edge** that frame
(`SpellRibbonRenderer.cs:138-143`). Large dt or high rates therefore discard multiple due edges.

## 11.3 Geometry, color and UV

With at least one saved edge and a texture, the renderer samples current RGB and alpha once and applies them
uniformly to the whole trail (`SpellRibbonRenderer.cs:144-161`). It emits the live top/bottom pair at U=0,
then saved edges newest-to-oldest with `U=(currentAge-born)/lifetime`, V=0/1. Positions are made
camera-relative on CPU (`:226-237`). A triangle strip draws the wall.

The renderer uses the first parsed direct ribbon texture and material. It ignores the animated `TextureSlot`,
texture rows/columns and atlas selection despite parsing them. Texture repeat is enabled
(`SpellRibbonRenderer.cs:247-261`).

## 11.4 Drain and blend

When an owning effect disappears, no live head is added, but edges drain using a separate wall-clock dt
clamped to .1 (`SpellRibbonRenderer.cs:180-219`). Draining geometry draws only at two or more edges. A trail
with exactly one edge remains stored but invisible until that edge expires; it is removed only at zero.

Rendering enables blending, disables depth writes and culling, and restores them afterward
(`SpellRibbonRenderer.cs:79-85,220-223`). Blend 3/4 is `SrcAlpha,One`; every other mode is ordinary alpha, so
render modes 5 and 6 are not reproduced for ribbons (`:164-174,208-216`). There is no global trail depth sort,
lighting, fog, spline smoothing, authored segment-length constraint, tail fading beyond the single current
alpha, or sound.

---

# 12. Missile selection, release, homing, arrival and miss behavior

## 12.1 Spawn gate and target multiplicity

The only gameplay spawn gate is a catalog row with `Speed>0`; `SpellVisual` field 6 is never read
(`SpellVisualCatalog.cs:13-20,161-172`; `Program.Casting.cs:95-109`). MSUI creates one missile for each GO hit,
then each GO miss. It does not create a missile for an explicit target/destination omitted from those arrays.
It does not client-compute chains, cones, radii or area recipients.

When speed≤0, each GO target runs immediate impact/miss arrival. When speed>0, a missile exists even if its
asset is `null`; an invisible projectile still moves on its deadline and invokes arrival
(`SpellEffectSource.cs:150-186,213-253`).

## 12.2 Model and texture choice

GO chooses (`Program.Casting.cs:86-109`):

1. resolved `SpellVisual` missile effect model;
2. if the visual names a nonzero effect but lookup/path fails, `Spells\ErrorCube.m2`;
3. if no missile model was named, `ItemDisplayInfo` ammo model from the packet's ammo display;
4. if that also fails, no asset (invisible missile).

Ammo model uses ItemDisplay `ModelName1` in `Item\ObjectComponents\Weapon`, else `ModelName2` in `...\Ammo`,
with filename stem and `.m2`; texture uses the parallel model texture and `.blp`
(`SpellEffectSource.cs:72-92`). The ammo custom texture only fills an effect mesh batch whose authored texture
is missing (`SpellEffectMeshRenderer.cs:154-161`).

## 12.3 Initial points, travel time and deadline

At GO, source point resolves caster attachment `0x15`; target point resolves the visual's destination
attachment and applies the 12-yard plausibility clamp to chest fallback (`SpellEffectSource.cs:150-160,365-390`).
Travel duration is `distance(source,target)/speed`. The **deadline is fixed at GO time + this travel duration**,
while visible launch waits for a release event (`:161-186`). This means release delay consumes part or all of
the travel window; MSUI does not shift the deadline after launch.

Out-of-range destination-attachment ordinals use `0x22`. If the target pose is missing,
`SpellUnitPose.Missing.Position` is zero; no special destruction or last-known-position rule exists.

## 12.4 Authored release event

`FindReleasePoint` searches the caster model's selected cast animation sequence and event identifiers in fixed
priority order `$CSL`, `$CSR`, `$CST`, `$BWR` (`SpellEffectSource.cs:392-417`). For each identifier it chooses
the earliest event within inclusive sequence timestamps and returns immediately. Thus identifier priority wins
over global chronological order. Without a marker, delay is the full sequence duration, or .25 seconds for a
degenerate/missing sequence. If model or animation id is absent, the default release delay is zero.

Before release the instance is not yielded to mesh/particle/ribbon renderers. At release, marker-based launch
uses `T(eventPosition-bonePivot)·posedBone·unit`; fallback re-resolves caster attachment `0x15`
(`SpellEffectSource.cs:225-234,419-429`).

## 12.5 Homing integration and orientation

Each tick after launch resolves the target's live destination attachment. Remaining time is
`deadline-now`; at or below zero, position snaps to destination, the instance is removed, and arrival callback
runs. Otherwise with `dt=clamp(now-lastMotion,0,.1)`:

`position += (destination-position) · clamp(dt/remaining,0,1)`

Direction becomes the pre-step gap (`SpellEffectSource.cs:236-252`). This interpolation reaches a moving
target exactly at the fixed deadline without preserving constant instantaneous speed. There is no ballistic
arc, collision, obstruction test, acceleration, turn-rate limit or early proximity arrival.

Render transform is
`RotY(atan2(direction.Y,direction.X)+π/2) · MissileBasis · T(position)`
(`SpellEffectSource.cs:323-333`). Missile effect age/animation begins at release and requests animation 144.

## 12.6 Arrival and miss

Hit arrival spawns impact and body animations as Section 6. Miss arrival spawns no impact. Only reasons 3 and
5 trigger a body dodge/block response (`Program.Casting.cs:113-121`). Other miss categories—resist, parry,
evade, immune, deflect, absorb, reflect and generic miss—are visible only if a separate combat-log miss event
produces text. Missile sound id is parsed but neither started nor stopped.

---

# 13. Player/creature body animation, channels and pushed visuals

## 13.1 Player precast/channel hold and cast/impact action

`CharacterRenderer.BeginSpellVisual` requires a nonzero authored animation and resolves it exactly with
on-demand baking on the spell-hold track; no fallback id is supplied (`World/Units/CharacterRenderer.cs:2464-2469`).
`ReleaseSpellVisual` clears the hold, resolves the authored id exactly/on demand as a combat action and restarts
that action (`:2471-2480`). Cancel only clears the hold; it does not clear an already released body action
(`:2483`).

Player clip precedence is combat action before spell hold, then locomotion/airborne/etc.
(`CharacterRenderer.cs:2877-2883`). Therefore a melee/reaction/cast action can visually override an active
channel/precast hold until the action finishes. Combat actions clear only after the chosen clip has actually
been current and its duration elapsed (`CharacterRenderer.cs:2382-2388`). Clip transitions use authored blend
time, with the general body mixer's fallback policy; spell actions are not a hardcoded separate pose blender
(`CharacterRenderer.cs:2525-2573`).

`TriggerOneShot` used for non-wound impact/state animations resolves the requested id without on-demand bake,
with animation 0 as fallback (`CharacterRenderer.cs:2485-2492`). This differs from cast/precast authored exact
resolution.

## 13.2 Creature holds/actions

Creature `BeginSpellVisual` stores a nonzero animation id by GUID and resets that creature animation time.
Release removes the hold and installs an authored-exact combat action with a four-second safety expiry
(`World/Units/CreatureRenderer.cs:157-175`). At render, dead animation wins, then a resolvable combat action,
then a resolvable spell hold, then ordinary creature animation (`CreatureRenderer.cs:414-454`). The action is
removed when its actual clip duration is reached; the four-second stored expiry is a separate safety value in
the action record/path.

Creature wound reactions map victim state 2/8→30, 3→20, 5→24, otherwise landed hit→9 and install up to a
three-second action record (`CreatureRenderer.cs:140-154`). Player wound fallback mapping is parallel
(`CharacterRenderer.cs:2445-2461`). Spell damage combat-log events do not call these reaction methods; only
SpellVisual arrival and melee swing animation do.

## 13.3 Local channel lifecycle

`MSG_CHANNEL_START` calls `BeginCastBar(spell,duration,channel:true)`, emits channel diagnostics, resolves the
spell's **own** visual id (not ranged equipment fallback here), begins its channel body hold, and spawns the
channel kit persistent on the player (`Program.Casting.cs:325-337`). The bar counts down using the server
duration.

`MSG_CHANNEL_UPDATE(remaining>0)` changes only `_castBarEnds = now+remaining`; it does not reset the original
start (`Program.Casting.cs:303-323`). `remaining=0` marks success, schedules the normal success display,
clears the player hold and reaps the player's persistent channel kit. Channel ticks do not control this state;
periodic/damage combat packets are only observed for diagnostic tick verdicts
(`Program.DevTools.SpellChannel.cs:42-63`).

## 13.4 Remote channel lifecycle

Every spell-presentation update scans nonself units' descriptor `ChannelSpell` (`Program.Casting.cs:482-511`).
For a new/replaced value it reaps the old persistent kit, resolves effective visual (including ranged weapon
fallback), spawns channel kit and begins creature hold. It records the new spell even if catalog/visual/kit is
missing, so it will not retry until the descriptor changes. When the unit/spell disappears from the scan it
reaps, cancels hold and removes tracking state.

There is no remote channel cast bar, duration or tick display. Unit disappearance is reconciled by the scan;
there is no dedicated remote `MSG_CHANNEL_UPDATE` handling.

## 13.5 Pushed visual kits

`SMSG_PLAY_SPELL_VISUAL` directly resolves a kit id, spawns it on the supplied unit as a self-terminating
effect with synthetic spell id 0/stage `PUSHED`, and releases the kit animation as a body action
(`Program.Casting.cs:339-347`). It does not consult a Spell.dbc row, stage lifetime map, sound, target, power,
or aura. Multiple pushed self-terminating instances can coexist because self-terminating spawn does not
replace prior spell-0 instances.

## 13.6 Sound

There is no spell audio implementation. `SpellVisualKitInfo.Sound` and `SpellVisualStages.MissileSound` are
parsed (`Formats/SpellVisualCatalog.cs:53-56,191-217`) but no cast/start/impact/state/channel/pushed/missile
path invokes a sound player. M2 event markers are used only for missile release, not sound events. A faithful
reproduction of current MSUI must remain silent for all spell stages.

---

# 14. Buffs, debuffs, aura-state FX, tracking, timers and cancellation

## 14.1 Authoritative 48-slot storage

`ObjectFields.Auras()` walks raw slots 0 through 47 in ascending order (`Net/ObjectFields.cs:239-253`). For a
slot, it extracts a four-bit flag nibble. The slot is considered live only when `flags&0x0E !=0` and its aura
spell field is nonzero. Level is a byte from `UNIT_AURALEVELS`; displayed stacks are the stored application
byte plus one, capped at 255.

MSUI classifies slots below 32 as helpful and 32–47 as harmful. This is a slot convention, not a read of
Spell.dbc dispel/mechanic/positivity metadata (`Program.DevTools.Auras.cs:14-18`). The descriptor is
authoritative: MSUI does not locally add, remove, stack, tick or expire an aura when a spell casts.

## 14.2 Before/after aura events

For one object update, MSUI snapshots current `(slot,spell,flags,stacks)`, applies the entity update, then diffs
that unit (`Program.Net.cs:773-792`; `Program.DevTools.Auras.cs:20-42`). For every old slot whose complete
snapshot changed, it emits REMOVE. It then emits APPLY for a new/replaced spell or STACK when the spell is the
same and stacks differ.

Consequences of the exact two loops:

- a stack change emits REMOVE first and STACK second because the full old/current snapshots differ;
- a flag-only change emits REMOVE but neither APPLY nor STACK;
- replacing a spell in a slot emits REMOVE then APPLY;
- events are diagnostic observations only; UI and state FX read the final descriptor directly.

For the local player, a duration entry is removed only when the final snapshot has no aura at that slot
(`Program.DevTools.Auras.cs:28-34`). A direct spell replacement in the same slot can therefore inherit the old
timer.

## 14.3 Aura duration packet

`SMSG_UPDATE_AURA_DURATION` is `u8 slot + u32 durationMs`. MSUI always stores
`(duration, expires=now+duration/1000)` keyed **only by slot**, then emits a duration verdict only if an aura is
currently present there (`Program.DevTools.Auras.cs:44-54`). A duration packet before a descriptor apply is
retained for the later UI; there is no spell id, receive-generation, freshness or join validation.

The player aura UI computes remaining time from this dictionary and displays `ceil(remaining/60)m` at 60
seconds or more, else `ceil(remaining)s` (`Program.UnitFrames.cs:233-240`). It does not remove the aura or timer
when remaining reaches zero; only a descriptor change does. Target auras do not display duration.

## 14.4 Aura-state world effects

Every update, MSUI walks every known unit and the distinct spell ids in its live aura slots
(`Program.Casting.cs:456-480`). On first sight of `(unit,spell)`, it resolves the spell (including ranged
equipment visual fallback) and spawns the State kit with `StageLife.AuraState`; it then marks the key active
**even if** the spell/catalog/visual/kit/effect asset was missing. Thus a missing/late dependency is not retried
while that aura remains continuously present. When the key disappears, aura-state instances reap and the key
is removed.

This stage is the only persistent world-model representation of buffs/debuffs. It does not alter unit
opacity, scale, material, movement, transform, shapeshift model, mount model, stealth state or gameplay. Any
such changes must arrive through other authoritative object fields and their general renderers.

At successful missile/nonmissile arrival, the State kit's **body animation** may also play once
(`Program.Casting.cs:153-157`), independent of whether the server actually adds an aura. Conversely a later
descriptor aura can spawn state models even if no local GO/impact was seen.

## 14.5 Player aura UI

Player auras remain in raw slot order (`Program.UnitFrames.cs:183-247`):

- helpful: at most 16, eight columns × two rows;
- harmful: at most eight, placed on row 2;
- total hard stop: 24;
- icon: 30×30; harmful icons get a cropped `UI-Debuff-Overlays` image;
- stacks show only above one;
- a catalog miss, blank icon path, or failed texture load hides the aura completely.

There are no tooltips, spell descriptions, caster names, dispel-colored borders, pulsing/expiration animation,
time formatting below seconds, sorting caches, stealable indication, or click-through menus. Every right-click
attempt calls the cancelability gate.

## 14.6 Target aura UI

Target auras also stay in raw slot order (`Program.UnitFrames.cs:156-181`):

- helpful: first five, 21×21, cyan border, one row, 24-pixel step;
- harmful: first sixteen, 17×17, red border, six columns, 20-pixel step;
- stack number only above one;
- no timers, cancellation, tooltip, dispel coloring or ownership filtering.

Counters increment before the cap check, but the result is still the first five/first sixteen renderable
catalog+texture auras.

## 14.7 Cancellation

An aura is cancelable only when slot<32 and flags bit `0x01` is set (`Program.DevTools.Auras.cs:14-18,56-75`).
Player UI right-click sends `CMSG_CANCEL_AURA` with only `u32 spellId` and waits for the server descriptor
change; local state is not removed optimistically (`Net/WorldSession.cs:326-334`). Duplicate spell ids use the
first snapshot found by spell-id cancellation.

Minimap tracking is a distinct, less strict path. It takes the first aura whose name begins “Find ” or
“Track ”, draws its icon and tracking border, and on right-click calls network cancel directly without checking
slot/helpful/flag cancelability (`Program.Minimap.cs:162-183`). There is no tracking selection dropdown or
multiple tracking display.

## 14.8 Buff/debuff gameplay categories

MSUI does not locally interpret `AuraIds`, effect amplitude, periodic interval, proc chance, dispel type,
mechanic, stat modifier, crowd-control family or positivity. All buff/debuff kinds—including stat buffs,
shields, forms, stealth, roots, stuns, fears, diseases, poisons, curses, magic debuffs, weapon imbues, tracking,
mounts and passive procs—share the descriptor/UI/state-kit path above. Periodic numeric ticks are a separate
combat-log path in Section 16.

---

# 15. Cast bar, interruption, auto-repeat, next-swing, GCD and cooldowns

## 15.1 Cast-bar state and server timing

The bar has `Hidden`, `Casting`, `Channel`, `Success`, and `Failed` phases
(`Program.Casting.cs:9-20`). Ordinary START uses the server packet's `CastTimeMs`; channel start uses the server
message's duration. DBC cast/duration values appear only in diagnostics (`Program.DevTools.CastBar.cs:10-20`).

`BeginCastBar` sets start=now, end=now+duration, zeroes accumulated pushback and selects Casting/Channel
(`Program.Casting.cs:260-270`). Ordinary fraction is elapsed; channel fraction is remaining:

- cast: `(now-start)/(end-start)`;
- channel: `(end-now)/(end-start)`;
- success/failure: 1;
- final value clamped [0,1].

The renderer is `Program.Casting.cs:349-425`: 195×13 bar at the lower center, orange while active, green on
success, red on failure, original border/text, and a 32×32 additive spark moving with the fraction. Channel
therefore visually drains right-to-left because its fraction falls.

## 15.2 Completion/failure display timing

GO completion sets Success and displays through
`now + 1/6 + 1/1.5 = now + 0.833333…s` (`Program.Casting.cs:272-280`). The additive flash alpha grows as
`clamp((now-finished)*6,0,1)` and stays full until the whole bar is hidden (`:404-411`).

Failure sets Failed and displays through `now + 1 + 1/1.5 = now + 1.666666…s`
(`Program.Casting.cs:283-291`). The guard is exact: when the requested spell differs **and** the existing phase
is not Hidden, it returns. A failure can replace a hidden bar or the same spell's visible bar.

Channel update zero uses the same 0.833333-second success deadline (`Program.Casting.cs:303-318`). The periodic
`UpdateSpellPresentation` hides success/failure at deadline (`:250-258`).

## 15.3 Pushback

`SMSG_SPELL_DELAYED` discards its caster GUID and supplies a delay. Only a Casting bar responds. MSUI shifts
**both** start and end forward by delay and accumulates the diagnostic pushback total
(`Program.Casting.cs:294-301`). Duration `(end-start)` therefore remains constant, while elapsed numerator
`now-start` decreases, visibly pushing the bar backward. It does not verify the packet caster/spell.

## 15.4 Escape and movement cancellation

Escape order (`Program.Casting.cs:190-213`):

1. if auto-repeat is active, send cancel-auto-repeat, clear repeat/sheath/hold and consume Escape;
2. otherwise find pending ordinary spell or active Casting bar, send cancel-cast and immediately apply local
   interrupted failure;
3. an active Channel alone is not an Escape target.

Movement uses one stopped→moving edge and movement intent = translation or jump, not turning
(`Program.cs:1570-1598`; `Program.Casting.cs:215-241`). For channel, MSUI sends cancel-channel when catalog is
missing or `ChannelInterruptFlags&0x08 !=0`; it emits cancellation diagnostics but leaves bar/hold/FX until a
server channel update. That channel branch consumes the edge. For ordinary cast, catalog missing means cancel;
a known spell cancels only when `InterruptFlags&0x08 !=0`. Ordinary movement cancel immediately runs local
failure in addition to sending.

There is no damage-based local cancellation; only server delayed/failure packets alter cast progress. There is
no local silence/stun/death cancellation hook in this spell state beyond server results.

## 15.5 Auto-repeat

Auto-repeat is a local spell-id latch set immediately after successful send (`Program.ActionBars.cs:149-159`).
Clicking it again or Escape sends empty `CMSG_CANCEL_AUTO_REPEAT_SPELL` and clears local state. A server
`SMSG_CANCEL_AUTO_REPEAT` also clears repeat, sheath and body hold (`Program.Casting.cs:243-248`).

START/GO for each server shot uses the ordinary visual/missile router. Auto-repeat does not occupy
`_pendingCastSpell`, so other ordinary local casts are not blocked by the pending gate; GCD/cooldown can still
block them. Its action button is checked and flashes with the 0.4/0.4 cadence.

## 15.6 On-next-swing

On-next-swing is a single local spell-id latch set after send. Clicking the identical queued spell is refused;
another next-swing spell is not blocked by the ordinary pending gate and overwrites the latch after sending
(`Program.ActionBars.cs:100-115,149-154`). Matching GO or failure clears it
(`Program.Casting.cs:60-76,175-187`). The action button is checked but does not use the attack/auto-repeat
flashing predicate (`Program.ActionBars.cs:986-996`).

Spell 6603 is not this mechanism. It is intercepted as melee attack engagement and emits no
`CMSG_CAST_SPELL` (`Program.ActionBars.cs:62-80`). Server melee swing packets drive its body actions and white
damage text.

## 15.7 GCD and recovery cooldowns

On successful local cast send, `StartRecoveryMs>0` does two things: sets one global deadline and also stores a
cooldown on the clicked spell id with the start-recovery category (`Program.ActionBars.cs:155-159`). It does not
apply the category to every spell sharing that category. Because the action button separately queries only its
own spell id, only the clicked spell draws the start-recovery swipe even though every spell is blocked by the
global deadline.

On local GO, MSUI starts `max(RecoveryMs,CategoryRecoveryMs)` on that spell id with category 0
(`Program.Casting.cs:72-76`). This replaces any existing cooldown entry for that spell. Failure resets the
entire global deadline to zero but does **not** remove a per-spell start-recovery entry
(`Program.Casting.cs:175-187`).

`PlayerActions.StartCooldown` is spell-id keyed and simply overwrites (`Net/PlayerActions.cs:96-100`).
`CooldownFraction` returns elapsed/duration, deletes expired entries and drives the reversed shade geometry;
remaining time is duration-elapsed (`:102-119`). Category is stored but never used for category fan-out.

Only `SMSG_INITIAL_SPELLS` supplies server cooldown data in the implementation. There is no live opcode handler
that refreshes cooldowns after login. Item actions suppress cooldown display, even when their triggered spell
later starts a spell-id cooldown that has no item-action association.

---

# 16. Direct, periodic, AoE, heal, power, shield, miss and environmental outcomes

## 16.1 Typed combat events

`Net/CombatPackets.cs:8-96` defines the complete outcome model: attack started/stopped, melee swing, spell
damage, periodic aura with one or more typed ticks, heal, energize, damage shield, environmental damage, spell
miss, and XP gain. `CombatPacketParser.Parse` accepts only its explicit opcode set and rejects any trailing
bytes (`CombatPackets.cs:98-129`). An unknown periodic aura type also throws rather than skipping the tick
(`:183-208`).

These parsers do not mutate authoritative health/power/aura/inventory. `CombatState.Apply` updates combat
engagement bookkeeping; object descriptors remain the state source. The events feed diagnostics, channel tick
observation, body melee animation and combat feedback (`Program.Net.cs:698-713`).

## 16.2 Direct spell damage

`SMSG_SPELLNONMELEEDAMAGELOG` reads target packed GUID, attacker packed GUID, spell id, damage, school, absorb,
resist, periodic byte, unused byte, blocked, hit info, and an extended-data byte that is assumed zero
(`CombatPackets.cs:166-180`). World text appears only when attacker is the local player and target is not self;
it is yellow and critical when damage>0 and hitInfo bit 0x2 (`CombatFeedbackLaw.cs:35-38`). Zero damage selects
Absorb, then Resist, else Miss (`:136-141`).

Incoming direct spell damage to the player creates red center text, negative amount or the same zero-damage
word, and the same crit bit (`CombatFeedbackLaw.cs:88-91`). Spell damage to other players from other casters
creates neither world nor center text, though feedback victim flashing can still affect the selected unit.

Direct spell damage combat logs do not cause body wound animations (`Program.CombatAnimations.cs:11-28`).
Visual impact does so independently if the kit authors an animation.

## 16.3 Periodic aura ticks

`SMSG_PERIODICAURALOG` contains target packed GUID, caster packed GUID, spell id, `u32 count`, then typed tick
records (`CombatPackets.cs:183-208`):

| aura type | parsed tick |
|---:|---|
| 3 or 89 | damage: amount, school, absorb, resist |
| 8 or 20 | heal: amount |
| 21 or 24 | energize: amount, power type |
| 64 | mana leech: amount, power type, float multiplier |

Local outgoing periodic **damage** to a nonself target produces yellow world text. Outgoing periodic heal,
energize and leech produce no world text (`CombatFeedbackLaw.cs:40-45`). Any periodic tick targeting the local
player produces red damage, green heal, or blue power center text (`:92-103`). Periodic ticks are not locally
scheduled from aura duration/amplitude; every displayed tick is a packet record.

For an active local channel, matching periodic aura ticks from the local caster produce diagnostic channel
TICK rows. A matching spell-damage packet with its periodic bit set is also a channel damage tick
(`Program.DevTools.SpellChannel.cs:42-63`). Neither path advances/stops the channel bar.

## 16.4 Direct heal and energize

`SMSG_SPELLHEALLOG` reads target, healer, spell id, amount and critical byte; `SMSG_SPELLENERGIZELOG` reads
target, caster, spell id, power type and amount (`CombatPackets.cs:211-223`). Only events targeting the local
player create center text: green `+amount` with heal critical sizing, or blue `+amount` for power
(`CombatFeedbackLaw.cs:105-110`). Heals/energizes to the current target do not create world numbers.

## 16.5 Damage shields

`SMSG_SPELLDAMAGESHIELD` is two raw GUIDs then damage and school. The first GUID is the shield bearer (`Victim`)
and the second is the unit receiving reflected damage (`Attacker`) (`CombatPackets.cs:71-76,225-226`). A shield
on the local player damaging another unit creates yellow world text over that attacker; reflected damage to
the local player creates red center text (`CombatFeedbackLaw.cs:48-51,111-113`). The event has no spell id and
does not create SpellVisual impact/state effects.

## 16.6 Spell misses

`SMSG_SPELLLOGMISS` reads `u32 spellId`, raw `u64 caster`, extended flag, `u32 count`, then raw
`u64 target + u8 missInfo`; extended entries add two floats (`CombatPackets.cs:231-245`). Local outgoing misses
create yellow world words; incoming misses targeting the player create red center words
(`CombatFeedbackLaw.cs:53-57,117-120`). Mapping is:

| code | word |
|---:|---|
| 2 | Resist |
| 3 | Dodge |
| 4 | Parry |
| 5 | Block |
| 6 | Evade |
| 7,8 | Immune |
| 9 | Deflect |
| 10 | Absorb |
| 11 | Reflect |
| other | Miss |

This combat-log miss list is separate from GO's miss list. GO misses control projectile/impact behavior; log
misses control text. The client does not deduplicate them.

## 16.7 AoE and multi-target outcomes

There is no local AoE query. A direct multi-target/AoE GO creates one immediate arrival or projectile per
server hit/miss GUID (`Program.Casting.cs:95-110`). A multi-target combat-log packet creates cues for each record
its event model contains (multiple periodic ticks or miss targets). If the server emits one direct-damage
packet per victim, each packet is handled independently.

Ground ring/decal models are purely the visual behavior in Section 9. A spell with no ground effect can still
hit many server targets; a ring can draw even if the server hit list is empty. MSUI does not use Spell.dbc
radius, implicit target enumeration, chain count or destination to compute or validate victims.

## 16.8 Environmental damage and melee boundary

Environmental damage is `u64 victim + u8 type + damage + absorb + resist` and creates red center text only for
the local player (`CombatPackets.cs:228-229`; `CombatFeedbackLaw.cs:114-116`). It has no spell id or VFX.

Melee swings carry a wire spell-id field that the parser deliberately discards (`CombatPackets.cs:142-164`).
They drive melee body swing/reaction and white world damage, not the spell visual router
(`Program.CombatAnimations.cs:7-28`). Thus Heroic Strike-like authoritative melee outcomes can be presented as
melee even when the queued action began through an on-next-swing spell.

## 16.9 Feedback animation/timing

Any feedback victim equal to player or selection flashes that unit frame for 0.35 seconds
(`Program.CombatFeedback.cs:57-65`). World text is capped at four simultaneous rows per target, lasts 1.5
seconds, anchors at `unitPos + Z·max(1.5,2.2·scale)`, and rises 1.2 yards/s (`:10-22,67-88,213-255`). Alpha
ramps in over .15 s, stays full through .76 s, then fades. Base font size is display diagonal×.018333; critical
starts at 2× and settles to 1.5× by .30 s. Lanes move horizontally.

Center text is capped at 20, lasts 1.9 seconds, stays full through 1.3 then fades over .6. Noncrit text rises
225 scaled pixels; crit grows 30→60 over .05 s then shrinks to 30 over .15 and does not rise
(`Program.CombatFeedback.cs:90-117,258-293`). Damage is red, heal green, power blue.

---

# 17. Professions, item-created outcomes, hearth, learning and pet spell surfaces

## 17.1 Skill lines and recipes

`SkillLineCatalog` maps the first observed spell to a skill line and parses recipe minimum/trivial values from
`SkillLineAbility.dbc` (`Formats/SkillLineCatalog.cs:1-66`). The spellbook hides rows considered recipes, while
clicking a profession-opening spell can open the profession panel instead of casting.

Supported craft line ids are 40, 129, 164, 165, 171, 185, 186, 197, 202 and 333
(`Program.Professions.cs:120-128`). A profession opener must belong to one of those lines, create no item
itself, and have at least one known recipe with a created item or reagent list
(`Program.Professions.cs:36-44`). Recipes collect product, reagents, tools, required focus and skill thresholds
from the catalogs and sort by required skill, reagent total, then name (`Program.Professions.cs:90-116`).

## 17.2 Profession-only admission

Before calling ordinary `TryCast`, the profession wrapper requires enough carried reagents, every tool, and a
nearby required focus (`Program.Professions.cs:132-177`). This is the only production caller that enforces
tools/reagents/focus locally. Failure is logged as “missing-reagents” even when the missing dependency was a
tool or focus.

Carried counts scan 16 backpack slots and up to 36 slots in each of four equipped bags; equipped gear is not
counted as carried reagent/tool. A spell focus is a streamed game object whose template type is 8, data[0]
matches the focus id, and 3D distance is within `clamp(data[1],0,10)`
(`Program.GameObjects.cs:83-108`). Required focus 0 always succeeds; a template-authored distance of zero makes
nonzero focus effectively require exact coincidence.

After `TryCast`, the profession panel calls the craft accepted only if `_pendingCastSpell` equals the recipe
id (`Program.Professions.cs:150-177`). Instant ordinary sends still set pending until GO, but auto-repeat or
next-swing recipe-shaped rows would not satisfy this acceptance check.

## 17.3 Product and batch observation

GO notifies `ObserveProfessionSpellGo`, which marks craft success and begins skill/product observation
(`Program.Professions.cs:237-260`). Product proof comes from inventory descriptor count changes or an
authoritative created `SMSG_ITEM_PUSH_RESULT`; the latter can replace the Spell.dbc-declared product with the
actual server result (`Program.Professions.cs:282-329`). Skill increase waits for the corresponding skill
descriptor; unchanged is reported after five seconds. Product unchanged is reported after eight seconds.

Create All computes the minimum floor of carried reagent counts, caps at 100, and begins the next craft only
after the previous product evidence arrives. Any spell failure stops the batch. Closing/canceling an active
batch can send cancel-cast (`Program.Professions.cs:430-510`). None of this creates items locally.

The UI displays at most eight reagent icons for the selected recipe and separately lists focus/tools, with
availability coloring. Server reagent consumption/inventory changes remain authoritative.

## 17.4 Hearthstone

The hearth surface finds item entry 6948 and sends item-use from backpack with spell slot 0. Spell id 8690's
server START/GO uses the ordinary cast/visual path; GO is observed as hearth completion and its local recovery
cooldown is shown (`Program.Hearth.cs:7-72`). Diagnostic replay can seed a one-hour spell-id cooldown, but the
normal client does not locally teleport—the server world-transfer/position packets do
(`Program.Hearth.cs:74-103`).

## 17.5 Learning/removal/superseding

Learning a spell adds the roster id and notifies trainer/profession observers; it does not create an action
slot or cast anything. Removal deletes matching spell actions. Supersede swaps every action referencing the old
spell to the new id (`Net/PlayerActions.cs:73-89`; `Program.Net.cs:452-473`). Passive learned spells remain in
the roster but are hidden from the spellbook and refused by `TryCast`.

## 17.6 Pet and summoned spell outcomes

The pet action surface is described in Section 3. A summon spell itself has no special local execution: its
server GO can play normal visuals, and the summoned unit appears through object descriptors. A pet's own
spell START/GO, aura state, channel descriptor, projectiles and impacts use the same GUID-based presentation
owners as any remote creature. Pet power/cooldown tails from `SMSG_PET_SPELLS` are not parsed, and pet action
buttons have no spell cooldown swipe/usability/range/auto-cast animation beyond active command/reaction border
(`Program.Pet.cs:14-34,74-107`).

---

# 18. Exhaustive spell-class behavior matrix

This matrix is the no-exception classification ledger. “Same ordinary route” means `TryCast` when player
initiated, then server START/GO stage routing; it does **not** mean MSUI locally implements the gameplay effect.

| Spell/effect class | Player entry/admission | Server/presentation behavior | MSUI-specific limits |
|---|---|---|---|
| instant spell | ordinary `TryCast`; pending set after send | START if sent still precasts; GO casts/arrives immediately or missiles | no bar when START time 0 |
| cast-time spell | ordinary route | START precast/hold/bar; GO cast/release/complete | bar uses server time; ranged suppresses bar |
| channeled spell | ordinary cast send, then channel messages | local MSG channel bar/hold/kit; remote descriptor channel | ticks diagnostic only; Escape does not cancel |
| passive spell | refused by `TryCast`, hidden from spellbook | server auras/descriptors may still present state/UI | no local passive evaluation |
| hidden-client-side spell | castable if internal caller invokes and nonpassive; hidden only in spellbook | normal incoming route | action bar could still contain/display it |
| auto-repeat ranged | toggled local repeat latch | each START/GO is ordinary stage/missile; server cancel clears | no repeat cadence/timer simulated locally |
| on-next-swing | queued local id | matching GO/failure clears; melee outcome via swing packet | second different queued id can overwrite |
| basic attack 6603 | intercepted to attack engagement | melee packets/animations/text | no cast packet or SpellVisual route |
| self spell | target word zero or self fallback | kit attaches to caster/GO server target | outbound implicit self mask 0 or explicit unit self |
| friendly unit spell | faction-friendly candidate | server hit list drives arrivals | no party/raid membership; self fallback default |
| hostile unit spell | attackable candidate | server hit/miss list | no local facing/LOS |
| corpse-target spell | dead+relation candidate | normal server route | only supported corpse-shaped unit bits; packet still unit mask 2 |
| game-object target | target law refuses unsupported bit shape | incoming server packets can still render | no outbound GO binder |
| item/trade-item target spell | target law refuses ordinary cast shape | item-use route can independently trigger server spell | no outbound ordinary item target |
| ground/destination spell | target law refuses ordinary cast shape | incoming destination retained but hit/miss GUIDs drive impact | no cursor/ground cast builder; destination does not spawn impact alone |
| string/source-location target | unsupported outbound | parser consumes data (destination retained, source/string dropped) | not used for presentation placement |
| direct nonprojectile damage | ordinary visual GO with speed≤0 | immediate impact per GO target; damage text on combat log | no local damage calculation |
| direct projectile damage | Speed>0 | one homing missile per GO hit/miss, fixed deadline | no collision/ballistic/sound |
| direct heal | same ordinary route | same visual stages; green center number only if local player healed | no world heal number for other targets |
| direct energize | same ordinary route | same VFX; blue center number only if local target | no world power number |
| periodic damage (DoT) | same ordinary/aura route | aura descriptor/state kit; each packet tick text | no local tick schedule |
| periodic heal (HoT) | same ordinary/aura route | aura descriptor/state kit; local incoming green ticks | outgoing target ticks not shown |
| periodic energize/leech | same ordinary/aura route | descriptor + blue local incoming tick text | no local resource mutation/schedule |
| buff | same ordinary route | descriptor helpful slot, icon/timer, optional State kit | helpful inferred solely slot<32 |
| debuff | same ordinary route | descriptor harmful slot, icon, optional State kit | harmful inferred solely slot≥32 |
| stackable aura | same | descriptor application byte +1; UI stack number | diff emits REMOVE+STACK |
| timed aura | same | slot-keyed duration UI | timer can survive slot replacement; does not expire aura |
| cancelable buff | right-click if helpful+flag bit 0 value `0x01` | sends cancel by spell id, waits descriptor | minimap tracking bypasses this gate |
| tracking aura | ordinary aura | first `Find `/`Track ` name gets minimap icon | no chooser/multiple tracking display |
| shapeshift/form | ordinary/aura route | optional state VFX; general descriptors/model changes only if separately handled | no spell-form requirements or local model rule in spell system |
| stealth/invisibility | ordinary/aura route | icon/state kit/descriptors | no spell-driven opacity/material rule |
| mount | ordinary/aura/server descriptors | mount descriptor drives general character presentation; aura UI/state kit possible | local gate only blocks other spells unless attr `0x01000000` |
| crowd control/root/stun/fear/silence | ordinary debuff route | aura/UI/state VFX and server movement/state | no local CC mechanic or cast-prevention check |
| dispel/cure/purge | ordinary route | server removes target aura descriptors | no local dispel-type selection/animation beyond kits |
| shield/reflect | ordinary buff route | aura/state; damage-shield combat packet produces numbers | shield log lacks spell id; no local absorption |
| proc/reactive aura | server aura and combat packets | optional state VFX/periodic/direct outcomes | no proc evaluation |
| taunt/threat/interrupt | ordinary route | server state/failure/combat outcomes; normal visuals | no local threat/interrupt effect execution |
| summon/pet creation | ordinary route | normal kits; object update creates unit | no local spawn |
| resurrect | ordinary spell can show normal kits; server resurrect request UI is separate | descriptor/world state authoritative | no spell-id join to rez request in spell router |
| teleport/hearth | item-use/ordinary server spell | normal stages; later world transfer authoritative | no local teleport; hearth observer only |
| create/conjure item | ordinary route; profession wrapper when recipe | GO plus inventory/push result | no local item creation |
| craft/profession recipe | profession validates reagent/tool/focus then `TryCast` | normal cast visuals; product/skill observed | only wrapper enforces requirements; max 8 displayed reagents |
| enchant/weapon imbue | ordinary/profession route | server item/aura descriptor changes; normal VFX | effect misc/item fields not executed locally |
| learned skill/proficiency | learned roster packet | spellbook/action availability | no local effect execution |
| multi-target/AoE direct | same ordinary route if target shape supported | one arrival/missile per server GO GUID | no client radius/chain/cone computation |
| ground AoE ring | often ordinary/unsupported outbound depending target word | effect M2 four-vertex ground mesh tessellated 10×10 | visual only; no target area |
| chain spell | ordinary initial target | server GO target list yields one per recipient | no chain order/arc logic; all missiles independently home |
| area aura | descriptor per affected unit | each unit gets its own state-kit key and aura icon if framed | no local area propagation |
| miss/resist/immune/etc. | server decides | GO miss controls impact; spell-log miss controls text | only GO dodge/block animates body |
| spell critical | server hitInfo/heal critical | text sizing only | no special impact kit selected by crit |
| spell pushback | server delayed packet | shifts casting bar start+end | caster/spell not verified by handler |
| spell failure | local gate or server result | error text, failed bar, persistent reap | known reason text table is partial |
| item-use spell | item packet, bypass ordinary gates | later START/GO shared | item action shows no cooldown |
| pet spell | pet-action packet, bypass player gates | pet GUID START/GO/shared effects | pet cooldown/power tails ignored |
| remote NPC spell | no local admission | START/GO body/effects, descriptor channel/aura | no remote cast bar |
| pushed visual kit | no Spell.dbc spell needed | direct kit self-term + body action | spell id 0; sound ignored |
| effect with mesh | same stage router | skinned/material mesh renderer | partial M2 material support |
| effect with particles only | same | isolated spell particle renderer | head billboards only |
| effect with ribbons | same | per-instance ribbon trail | partial blend/atlas support |
| effect with no visible asset | same | instance skipped or invisible missile timing retained | stage may still body-animate |
| environment damage | not a spell entry | red center number | no spell id/VFX |

Every Spell.dbc effect opcode not explicitly named in the small operational ledger in Section 2 falls into
“server-authoritative gameplay; ordinary visual/descriptor/log presentation.” MSUI never switches over all
spell effect ids to execute them.

---

# 19. Exact implementation boundaries and known omissions

This section is not a comparison with another client. It is the explicit boundary of **current MSUI** so a
future A-vs-B diff does not mistake an unimplemented behavior for undocumented behavior.

## 19.1 Admission/targeting/gameplay

- No local spell effect execution: no damage/heal/power/aura/summon/teleport/dispel/threat/item mutation.
- Ordinary outbound casting supports only implicit self and one unit GUID.
- No ground cursor, game-object, item, trade-item, source position, destination, string or multi-target builder.
- Only first implicit target A affects local targeting; implicit B and other effect slots do not.
- Party/raid target bits are satisfied only by self, not actual group membership.
- No local LOS, facing, indoors/outdoors, silence, stun, death, stance/form, stealth, equipment, aura-state,
  combo-point, ammo, totem, reagent, focus or tool check for ordinary casts.
- Range is 3D reach-expanded distance only and runs only for selected nonself targets.
- No local spell queue window, latency prediction, batch sequence reconciliation or cast-time prediction.
- A successful local send is treated as pending before server acceptance.

## 19.2 Cast/channel/cooldown state

- Ranged START never shows a cast bar even with nonzero server cast time.
- Local channel messages are assumed self because the handler reads no caster.
- Remote channels have no bars/durations and are descriptor-polled.
- Escape cancels repeat or ordinary casting, not channels.
- Channel movement cancel waits for server update; ordinary movement cancel fails immediately.
- Any matching local failure clears the entire GCD deadline, but leaves the spell-id cooldown entry.
- Cooldowns are per spell; category fan-out is absent.
- Live server cooldown updates after initial login are absent.
- Action usability tint does not reflect most admission gates or cooldown; secondary bars omit state overlays.

## 19.3 START/GO/miss/missile routing

- The first packed GUID in START/GO is discarded and cannot identify a casting item separately.
- START targets are parsed but do not place precast effects.
- GO explicit unit/destination does not create arrival without a hit/miss list member.
- GO target arrays alone define presentation multiplicity; no local AoE/chain computation.
- Missile deadline is computed at GO and not shifted by release delay.
- Release identifier priority is fixed-string order, not globally earliest event.
- Missing target pose homes toward zero; no last-known target or destroy-on-missing rule.
- Miss impact is entirely suppressed; only dodge/block get body reactions.
- No projectile collision, ballistic path, obstruction, proximity arrival or in-flight sound.

## 19.4 Aura behavior

- Helpful/harmful is slot number, not positivity/dispel metadata.
- A stack change produces diagnostic REMOVE+STACK.
- Duration is keyed by slot and can attach to a replacement aura.
- Duration reaching zero does not remove an aura.
- Aura-state keys are marked active even if resolution/spawn failed, preventing retry until removal.
- Aura UI hides catalog/icon/texture misses; no tooltip/caster/dispel/sort/expiration animation.
- Minimap tracking cancel bypasses the normal cancelability test.

## 19.5 Effect assets/animation

- Failed model/path loads are negatively cached.
- One animator/skin array is shared per effect asset path.
- Effect clip uses missile animation 144 or first sequence; no stage-specific effect animation selector.
- Body stage animation is exact and can be missing; player impact `TriggerOneShot` alone permits fallback 0.
- Player combat action overrides an active spell hold.
- M2 versions 264+ and external skin/anim files are unsupported.
- Hermite/Bezier are approximated as step for generic effect tracks and linear for global bone tracks.
- Static/looping policy differs between body hardcoded one-shot ids and generic sequence flag sampling.
- No spell-stage audio or general M2 event sound playback.

## 19.6 Mesh/material/ground

- One texture unit; no texture animation/transform, combiners, layers, shader id or priority-plane behavior.
- Fixed lighting/fog rather than world lighting.
- No transparent depth sorting.
- Custom ammo texture is fallback-only.
- Ground recognition only accepts one four-vertex, single-bone flat batch.
- Ground projection is a 10×10 height snap, not surface-triangle clipping/projection.
- Ground eligibility is requested attachment 0x13 only.

## 19.7 Particles

- Hard 250-yard simulation cutoff and 1024-particle per-pool cap.
- Spline emitters use the plane kernel.
- Only head camera-facing sprite quads; no tails, streaks, XY quads, spin/tumble or model particles.
- No inherited velocity, follow-delta integration, wind, child emitters, collision, ground snap or soft particles.
- `path.Contains("Missile")` decides attached vs world-frozen behavior.
- Continuous emission uses clamped dt; no compensation for long frames.
- Textures do not repeat even though cell sampling wraps in-atlas.
- No lighting, fog, gamma policy or transparent sort.

## 19.8 Ribbons

- At most one new edge per frame, even if rate accumulator crosses multiple integers.
- Gravity sag is incremental `2gdt`, not physical displacement integration.
- Current color/alpha is applied uniformly to old edges.
- TextureSlot and texture atlas rows/columns are ignored.
- Blend 5/6 collapse to ordinary alpha.
- No depth sorting or spline smoothing.

## 19.9 Outcomes/UI

- Combat-log packets and visual arrivals are independent and not temporally joined/deduplicated.
- Direct spell logs do not trigger body wounds; only visual impact authored animation does.
- Outgoing heals/energizes and most other-player combat have no world numbers.
- Feedback is capped and silently drops excess world rows after recording a counter.
- Known cast-error text is partial; unknown reasons collapse to “Spell failed.”
- Pet spell cooldown/power tails and pet spell usability are not implemented.

---

# 20. Diagnostics, evidence provenance and no-exception audit

## 20.1 Built-in spell evidence channels

The diagnostic code below observes or drives the production paths described earlier. It does not replace
those paths. In particular, a diagnostic verdict proves that instrumentation emitted a row; it does not by
itself prove visual or mechanical parity with any reference client.

| verdict channel | implementation | what it records |
|---|---|---|
| `spell-sweep` | `MSUIClient/Program.DevTools.SpellSweep.cs:9-148` | known-spell inventory, class, school, cast type, resource availability/cost, target resolution, local/pre-send/server result, aura presence and visual-model-chain checks |
| `cast-bar` | `MSUIClient/Program.DevTools.CastBar.cs:10-21` | bar event, spell, classification, server and DBC duration, phase, source and pushback count |
| `spell-animation` | `MSUIClient/Program.DevTools.SpellAnimation.cs:10-101` | requested stage, resolved kit, authored animation, effect paths and presentation/sample result |
| `spell-animation-sequence` | `MSUIClient/Program.DevTools.SpellAnimationSequence.cs:18-233` | per-frame body animation request/playback/resolution, crossfade and motion; active effect assets; particle/mesh/ribbon draw state; stage visual status; screenshots; health, power, aura, inventory, unit-count and position deltas |
| `spell-channel` | `MSUIClient/Program.DevTools.SpellChannel.cs:14-63` | channel start/update/tick/cancel/stop, remaining time and periodic combat evidence |
| `spell-aura` | `MSUIClient/Program.DevTools.Auras.cs:14-120` | production aura apply/remove/stack/duration/cancel behavior plus explicit synthetic-wire replay |
| `spell-error` | `MSUIClient/Program.DevTools.SpellErrors.cs:9-31` | local/server failure reason, rendered text, display status and source |

The verdict panel can filter, pause, snapshot and copy these channels while the wire logger records packets
(`MSUIClient/Program.DevTools.Verdicts.cs:9-163`). A live-run completion writes the complete verdict text and
seven dedicated CSVs, with explicit column schemas, at
`MSUIClient/Program.LiveRun.cs:909-1047`. The animation-sequence cell becomes `MEASURED` only after at least
14 samples; before that it is `NOT-INSTRUMENTED` (`MSUIClient/Program.DevTools.SpellAnimationSequence.cs:92-104`).

These diagnostics deliberately distinguish:

- `ABSENT`: the DBC stage has no authored asset;
- `ASSET-MISSING`: a referenced MPQ file could not be supplied;
- `RESOLVED-NOT-DRAWN`: the asset resolved but no applicable renderer drew it;
- `PRESENT`: the expected visual was active and drawn;
- `ANIM-EXACT`, `ANIM-FALLBACK`, `ANIM-STATIC` and `ANIM-MISSING`: actual body-clip resolution;
- `NOT-INSTRUMENTED`: the evidence window was insufficient, not a pass.

The exact classification logic is at
`MSUIClient/Program.DevTools.SpellAnimationSequence.cs:67-147` and asset-stage inspection is at
`MSUIClient/Program.DevTools.SpellAnimationSequence.cs:169-233`.

## 20.2 External spell audit tools

These are test/evidence producers. They are not linked into the normal client loop and therefore are not
runtime spell features.

| tool | scope and exact boundary |
|---|---|
| `tools/spell-visual-diagnose/Program.cs:7-102` | resolves every expected model through the configured MPQ chain; records supplier, bytes, M2 parse status and drawable mesh/particle/ribbon/event/release/color/alpha/batch/texture counts; also runs pure attachment/track/visual-chain self-tests |
| `tools/spell-animation-reference/Program.cs:9-90` | converts a live known-spell sweep into the per-class expected catalog: rank, cast/duration/speed, all three effect/aura/implicit-A/implicit-B/misc/item fields, every visual stage kit/animation/model and missile model |
| `tools/spell-contact-sheet/Program.cs:1-44` | composes seven-school precast/cast capture sheets; presentation artifact only |
| `tools/spell-matrix-scenario/Program.cs:6-121` | generates standing and moving protocols for every expected roster spell, resetting target/state/resources and sampling the animation sequence around a real cast |
| `tools/spell-matrix-aggregate/Program.cs:6-160` | joins sequence, sweep, cast-bar, aura, channel and error CSVs; derives cast, movement, mechanical-signal and visual coverage without silently turning missing evidence into a pass |
| `tools/spell-roster/Program.cs:7-226` | provisions or inspects per-class test characters under an authorization fence and writes a hashed roster ledger; test infrastructure only |
| `tools/combat-wire-check/Program.cs:7-260` | pure protocol-layout, targeting, cast-classification, action-state, combat-feedback and verdict-ring checks; not an integration/render test |

The matrix aggregator's mechanical oracle maps selected Spell.dbc effect opcodes to observable state families
(health, aura, inventory, unit count, position and power) at
`tools/spell-matrix-aggregate/Program.cs:55-83`. That mapping is diagnostic coverage logic, **not** a hidden
client-side effect executor.

## 20.3 What the evidence can and cannot establish

The source trace in Sections 1-19 is the specification of what MSUI currently does. Evidence can establish
that a path was exercised and expose its selected data, timing and render state. It cannot establish any of
the following without an independent reference capture:

- that a DBC-authored animation, particle, ribbon or material looks like another client;
- that server-side damage, healing, aura selection or target enumeration is semantically correct;
- that an absent effect is intentionally absent rather than missing from the archives;
- that `NOT-INSTRUMENTED` or an unobserved state delta means no behavior occurred;
- that a synthetic wire replay has all the side effects of a live server packet sequence;
- that a contact sheet preserves motion, timing, blend evolution, camera-facing behavior or depth ordering.

Conversely, a runtime boundary documented in Section 19 remains a boundary even when a tool manufactures a
packet or calls a presentation helper directly.

## 20.4 Audited source ledger

This ledger fingerprints the files used for this document so the future comparison can detect source drift.
`SHA-256/12` is the first 12 hexadecimal digits of the file's SHA-256 at the audit time, **2026-08-02**.
Line counts are physical source lines.

### Data, protocol and state

| source | lines | SHA-256/12 |
|---|---:|---|
| `MSUIClient/Formats/SpellCatalog.cs` | 145 | `6031293F602C` |
| `MSUIClient/Formats/SpellVisualCatalog.cs` | 225 | `18AEDC7C1D78` |
| `MSUIClient/Formats/M2Reader.cs` | 2414 | `A58BC3E4182D` |
| `MSUIClient/Formats/SkillLineCatalog.cs` | 77 | `7C1A91332E67` |
| `MSUIClient/Formats/SpellFocusCatalog.cs` | 32 | `CD55587FB8A7` |
| `MSUIClient/Formats/DbcReader.cs` | 1458 | `21D17F0FFB1E` |
| `MSUIClient/Net/SpellPackets.cs` | 69 | `4D6DDE9C0A3F` |
| `MSUIClient/Net/CastTargetLaw.cs` | 86 | `D3B51E80E718` |
| `MSUIClient/Net/SpellCastResultNames.cs` | 38 | `83836BF0F3FF` |
| `MSUIClient/Net/PlayerActions.cs` | 120 | `312DA241DDDE` |
| `MSUIClient/Net/WorldSession.cs` | 784 | `2B52051008F2` |
| `MSUIClient/Net/NetworkClient.cs` | 655 | `3484D1CC43B9` |
| `MSUIClient/Net/ObjectFields.cs` | 337 | `8EAF8B3596F8` |
| `MSUIClient/Net/CombatPackets.cs` | 275 | `B5EBFEB50878` |
| `MSUIClient/Net/CombatFeedbackLaw.cs` | 168 | `366A04225867` |
| `MSUIClient/Net/CombatState.cs` | 73 | `F3E5FDC6B9D5` |
| `MSUIClient/Net/Opcodes.cs` | 329 | `1B32FEA5EF23` |

### Client orchestration, actions and UI

| source | lines | SHA-256/12 |
|---|---:|---|
| `MSUIClient/Program.ActionBars.cs` | 1105 | `BA9CF5AC15FB` |
| `MSUIClient/Program.ActionIcons.cs` | 42 | `43CDF9598336` |
| `MSUIClient/Program.Casting.cs` | 529 | `16E0596D8895` |
| `MSUIClient/Program.Spellbook.cs` | 194 | `A69FC5E6FA46` |
| `MSUIClient/Program.Macro.cs` | 181 | `B98355D6031D` |
| `MSUIClient/Program.Inventory.cs` | 653 | `6EBB3E4E5F2C` |
| `MSUIClient/Program.Pet.cs` | 132 | `DA5954CC2204` |
| `MSUIClient/Program.Professions.cs` | 525 | `595EA8E33A9B` |
| `MSUIClient/Program.GameObjects.cs` | 250 | `44C7BDF408A9` |
| `MSUIClient/Program.Hearth.cs` | 120 | `796BA61FE358` |
| `MSUIClient/Program.UnitFrames.cs` | 307 | `BF5E830A51C1` |
| `MSUIClient/Program.Minimap.cs` | 385 | `00FE042C669A` |
| `MSUIClient/Program.Net.cs` | 1985 | `8B01047906D4` |
| `MSUIClient/Program.cs` | 3244 | `743B47ECD12C` |
| `MSUIClient/Program.CombatFeedback.cs` | 295 | `0FE25346C6FE` |
| `MSUIClient/Program.CombatAnimations.cs` | 29 | `9D01C79EFA06` |
| `MSUIClient/Program.Trainer.cs` | 174 | `08F87A58FEF0` |
| `MSUIClient/Program.Talents.cs` | 362 | `879557D1FD23` |

### Spell presentation and rendering

| source | lines | SHA-256/12 |
|---|---:|---|
| `MSUIClient/World/Units/SpellEffectSource.cs` | 454 | `7360DBB71479` |
| `MSUIClient/World/Units/SpellAttachment.cs` | 180 | `E588BE77097E` |
| `MSUIClient/World/Units/SpellEffectMeshRenderer.cs` | 655 | `CB19993D8E99` |
| `MSUIClient/World/Units/SpellRibbonRenderer.cs` | 287 | `1865755AFCC1` |
| `MSUIClient/World/Spells/SpellParticleSystem.cs` | 590 | `B5C2CFC72F14` |
| `MSUIClient/World/Units/M2Animator.cs` | 1058 | `864A9E5835A8` |
| `MSUIClient/World/Units/CharacterRenderer.cs` | 3416 | `92F80155386B` |
| `MSUIClient/World/Units/CreatureRenderer.cs` | 1768 | `5C44F397CEBE` |
| `MSUIClient/Engine/UI/ActionIconLaw.cs` | 46 | `4A4CF36A041F` |
| `MSUIClient/Shaders/particle.vert` | 40 | `64D61820E907` |
| `MSUIClient/Shaders/particle.frag` | 31 | `49EDAF041C58` |

### Instrumentation and audit executables

| source | lines | SHA-256/12 |
|---|---:|---|
| `MSUIClient/Program.DevTools.Auras.cs` | 121 | `B2A7B522C899` |
| `MSUIClient/Program.DevTools.CastBar.cs` | 22 | `E6A040BAFDE7` |
| `MSUIClient/Program.DevTools.SpellSweep.cs` | 149 | `177C777796A9` |
| `MSUIClient/Program.DevTools.SpellAnimation.cs` | 102 | `4C043260E90F` |
| `MSUIClient/Program.DevTools.SpellAnimationSequence.cs` | 234 | `319A3F11FD25` |
| `MSUIClient/Program.DevTools.SpellChannel.cs` | 64 | `C95EC09BD170` |
| `MSUIClient/Program.DevTools.SpellErrors.cs` | 32 | `133A09F5350D` |
| `MSUIClient/Program.DevTools.Verdicts.cs` | 163 | `770070C97FED` |
| `MSUIClient/Program.LiveRun.cs` | 1062 | `AAB2446B0261` |
| `tools/spell-visual-diagnose/Program.cs` | 102 | `865234AF5251` |
| `tools/spell-animation-reference/Program.cs` | 91 | `DB81F52AA846` |
| `tools/spell-contact-sheet/Program.cs` | 44 | `90C0F4C43A33` |
| `tools/spell-matrix-scenario/Program.cs` | 122 | `0264029AE32F` |
| `tools/spell-matrix-aggregate/Program.cs` | 160 | `9F6A0899AD90` |
| `tools/spell-roster/Program.cs` | 226 | `2F7A3D917708` |
| `tools/combat-wire-check/Program.cs` | 260 | `B5022B0F89FA` |

## 20.5 No-exception coverage checklist

| required subject | where completely traced |
|---|---|
| spell/data discovery, all parsed Spell.dbc fields, visual kits, effects, attachments and ranges | Sections 2-3 |
| learned spells, superseded ranks, passives, trainer/talent learning, spellbook pages and drag/drop | Sections 3 and 17 |
| primary/secondary action bars, macros, item actions, pet actions and pushed visual kits | Sections 3, 4 and 17 |
| all local admission gates, target law, range math, power math and outbound packet shapes | Section 4 |
| all spell/aura/cooldown/learned/pet/combat packet handlers and parsed/ignored fields | Sections 5, 12-17 |
| START, GO, hit/miss arrays, AoE/chain multiplicity, impact and miss suppression | Sections 5-6 and 18 |
| every visual stage: precast, cast/release, missile, impact, state aura, channel and direct pushed kit | Sections 6-13 and 18 |
| effect instance ownership, lifetime, attachment transform and caster/target fallback | Section 7 |
| body M2 and effect M2 parsing, sequence selection, interpolation, crossfade and global tracks | Section 8 |
| mesh skinning/materials/blending/textures/culling/fog/lighting and ground projection | Section 9 |
| particle spawn shapes, rate, seed, life, forces, color/size/alpha, atlas, billboard and shaders | Section 10 |
| ribbon emission, trail ageing, gravity, width/color/alpha, geometry, texture and blend | Section 11 |
| missile model selection, release event, travel clock, interpolation, homing and invisible travel | Section 12 |
| player/creature spell body animation, holds, ranged override, impacts, pushback and sound boundary | Section 13 |
| helpful/harmful aura storage, stacks, duration, cancel, UI, tracking and state/channel effects | Section 14 |
| cast bar, cancel, movement, auto-repeat, next swing, GCD, cooldown data and UI overlays | Section 15 |
| damage/heal/energize/miss/periodic/environment outcomes, world text, reactions and combat log | Section 16 |
| professions, crafting, fishing, skinning, pick lock, tracking, items, hearth, learn/unlearn and pets | Section 17 |
| explicit matrix for instant/cast/channel/auto/next/AoE/chain/aura/item/pet/remote/pushed/render cases | Section 18 |
| unimplemented and simplified behavior, separated from implemented behavior | Section 19 |
| diagnostic channels, external tools, evidence limits and source fingerprints | Section 20 |

The checklist covers spell **classes and execution surfaces**, not a frozen list of spell IDs. Known spell IDs
are server/account/character data and can change. The runtime applies the same classified paths to each known
row; the `spell-animation-reference` roster export is the mechanism for producing a particular character's
complete ID-by-ID snapshot.

## 20.6 Contract for the later A-to-B gap comparison

To compare the Benilla trace (A) to this MSUI trace (B) without false equivalence, compare each row on the
following independent axes:

1. **Admission:** who may request it, local gates, target shapes and failure timing.
2. **Authority:** which side computes targets and mechanics, and which packets establish truth.
3. **Clock:** request, START, GO, release, travel, impact, aura/channel expiry, GCD and cooldown clocks.
4. **Multiplicity:** self/single/AoE/chain hit and miss instances and deduplication rules.
5. **Body animation:** requested animation, exact/fallback/static/missing result, hold and interruption.
6. **Stage assets:** precast, cast, missile, impact, state and channel kits, attachments and lifetimes.
7. **Rendering:** mesh/material/texture, particles, ribbons, billboard basis, ground projection and ordering.
8. **Audio:** cue selection, position, launch/loop/impact timing and stop behavior.
9. **Persistent state:** aura classification/stack/duration/cancel, channel updates and tracking.
10. **Feedback/UI:** cast bar, action state, errors, combat text/log, spellbook and cooldown display.
11. **Special surfaces:** item, profession, pet, macro, auto-repeat, next-swing and pushed-kit behavior.
12. **Evidence quality:** directly implemented, server-observed, synthetic, inferred, absent or not instrumented.

Coordinate axes, angle units, milliseconds/seconds and packet-vs-render timestamps must be normalized before
numeric comparison. An authored-but-missing asset is different from no authored asset; an invisible timed
missile is different from an instant impact; server mechanics are different from client presentation; and a
documented omission is a real B-side gap, not missing documentation.

## 20.7 Audit conclusion

For the audited source snapshot, the MSUI spell system is completely accounted for from input and learned
spell discovery through network admission, server lifecycle, animation, effects, particles, ribbons,
missiles, auras, channels, outcomes, cooldown/UI feedback and special item/profession/pet paths. Sections
18-19 make every observed spell class and every known simplification explicit. No undocumented local spell
mechanics, audio layer, ground-target subsystem or alternate renderer was found in the audited source ledger.
