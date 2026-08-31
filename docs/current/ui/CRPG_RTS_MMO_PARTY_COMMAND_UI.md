# CRPG/RTS Party Command Interface in the MMO World

**Status:** Design proposal and implementation handoff  
**Date:** 2026-08-24  
**Scope:** Party-scale CRPG/RTS control inside the persistent MMO world. This is
not the separate Tier-2 RTS world or its worldstate/economy interface.

## Related material

- [`CRPG_RTS_WIP.md`](../CRPG_RTS_WIP.md) — current Tier-1 decisions,
  implementation record, and open work.
- [`SYSTEM_CRPG_CONTROL_GROUPS.md`](../../systems/SYSTEM_CRPG_CONTROL_GROUPS.md)
  — current control-group behavior.
- [`DYNAMIC_COMBAT_RULES_AND_ENCOUNTER_INTELLIGENCE.md`](../../plans/DYNAMIC_COMBAT_RULES_AND_ENCOUNTER_INTELLIGENCE.md)
  — server-authoritative combat-rule and rotation direction.
- [`CRPG_RTS_MMO_COMMAND_UI_MOCKUPS.html`](./CRPG_RTS_MMO_COMMAND_UI_MOCKUPS.html)
  — interactive four-screen interface drawing. Open this file in a browser and
  use the tabs across the top.

## Build state (2026-08-25)

| Piece | State |
|---|---|
| Command voice + chorus | BUILT (client) |
| Free View command shelf: scope label, Regroup/Hold/Focus/Patrol/Line/Circle/Sheathe | SUPERSEDED 2026-08-25 by the WC3 console below (same verbs/orders, new dress) |
| WC3 commander console v2: fixed three-region dock — SQUAD grid (all ten wells, click recall / Shift+click save), INFO panel (scope; portrait unit card with level/class/vitals/abilities for one unit, baked-portrait chips for a group), COMMAND CARD (icon grid; art from the vanilla pet-bar idiom read out of the shipped FrameXML: Focus=Ability_GhoulFrenzy, Regroup=Ability_Tracking, Hold=Spell_Nature_TimeStop; Patrol/Line/Circle/Sheathe archive-verified) | BUILT 2026-08-25 |
| Attack icon law on companion surfaces: console card + party quick slots route through ActionIconLaw, so Attack shows the member's weapon (public visible-item entries), never spell 6603's internal Temp face | FIXED 2026-08-25 (owner caught it in CREATOR MODE) |
| Formations, sheath, wander discipline, conscription | BUILT (core+brain compiled, deploy pending) |
| Free-view number keys: 1-0 recall, Ctrl+1-0 save | BUILT |
| Four quick-action icon slots per party row (truthful; honest unknown wells) | BUILT — display+tooltip only; casting needs Phase B |
| Role medallion (corner-anchored disc, shape+color+letter) | BUILT — client preferred role only |
| Order-state chips (client-tracked last order + enlisted) | BUILT |
| Party inventory browser (`GameLoop.PartyInventory.cs`): owner rail, public equipment for every streamed member, possessed-bot bags, honest possession-boundary notice | BUILT — mutation waits for Phase C |
| Tactics panel (`GameLoop.PartyTactics.cs`): member rail, preferred role, quick-slot AI policy (persisted in botbars.json `BotSlotPolicies`), stances disabled with reason | BUILT — Phase B makes role/stance authoritative |
| Vanilla skinning: UI-Panel-Button shelf verbs, tooltip backdrops on shelf/strips, dialog backdrop + header plaque + close on both panels, autocast gold corners on AI-enabled quick slots | BUILT |
| WC3 free-view console: selection chips (click = solo, Shift+click = drop) in the shelf; order ping (shrinking ring + chevrons) predates this round | BUILT |
| Free View costume change: body chrome (action/multi/stance/pet bars, bag bar, micro menu) hidden; console owns the bottom edge, always present (squads row + hint when empty) with a WC3 unit card (name, vitals, read-only abilities) for single selections | BUILT 2026-08-25 |
| Free View minimap docks to the bottom corner (WC3 console furniture); top-right in normal play | BUILT 2026-08-25 |
| Snapshot RETENTION: possession is the sync gesture — a bot's inventory snapshot now survives release, per-bot with an age stamp (re-possess replaces; session teardown purges) | BUILT 2026-08-25 |
| Party Inventory v2, BG3-style: one column per member — equipment (public, always current) + bags (live for you/the driven body, "synced Xs ago" for retained snapshots, "possess once to sync" otherwise) | BUILT 2026-08-25 |
| Mockup HTML re-skin to the vanilla idiom, reflecting the built UI | BUILT |
| PARTY MEMBER FACTS: server pushes every party/raid AiBot's inventory snapshot + known spells WITHOUT possession (roster-edge push + rate-limited client pull, capability bit 3, opcodes 850/851); client gate lifted, labels retired for party members | BUILT 2026-08-25 (client compiled + guard-checked; core compiled on box, deploy pending) |
| INSTANT PARTY TRADE (Phase C v1): right-click a bag item in Party Inventory → "Give to <member>" — opcodes 852/853, capability bit 4; endpoints = own character or party AiBot, same map, binding not a gate, conjured refused; server re-snapshots both ends to every SUI group member | BUILT 2026-08-25 evening |
| Party Inventory v3: minified CRPG character sheet per column (paper-doll rails around the baked portrait), small bag icons, per-column scroll, resizable panel; B in the free view opens it | BUILT 2026-08-25 evening |
| PARTY QUEST FACTS (PLAN_20 P1): every party member's quest log pushed without possession — opcodes 854/855, capability bit 5, roster-edge push + separately rate-limited pull; new Party Quest Log panel (merged rows × member columns, per-member objective detail), `L` in the free view. Own-guid subjects carry quests held past the twenty update-field slots, ready for the P2 cap removal | BUILT 2026-08-25 late (client compiled + guard-checked; core compiled on box, deploy pending) |
| HELD-QUEST CAP LIFTED (PLAN_20 P2): `Quests.MaxHeld` config (default 100, floor 20); `MAX_QUEST_LOG_SIZE` stays 20 as the field layout; `m_questsHeld` drives all nine credit scans; slot promotion on reward/abandon | BUILT 2026-08-25 late (core compiled on box, deploy pending) |
| PARTY QUEST ACTS (PLAN_20 P3): opcodes 856/857, capability bit 6 — companion rail beside the questgiver frame ("Accept for party (N)"), per-member reward board on turn-in, id-addressed abandon, per-member refusals by name; vanilla Share Quest button lit up with AiBots answering shared quests | BUILT 2026-08-25 late (client compiled + guard-checked; core compiled on box, deploy pending) |
| Party vendoring, numbered `!` markers | PLANNED — `docs/plans/PLAN_20_PARTY_QUESTING_AND_VENDORING.md` P4-P5 |

**Honesty correction (owner pushback, 2026-08-25):** possession already grants
full per-bot inventory + bars, one bot at a time — the owner was right that
"Phase B needed" overstated the gap. What Phase B genuinely adds is
*without-possession* freshness (live spells/cooldowns for never-possessed bots,
real-time updates) and *remote one-shot casts*; Phase C adds mutation. Retention
closes the simultaneity gap client-side: sync each companion once by possessing
it and the browser shows the whole party at once, honestly aged.

**Owner decision (2026-08-25): party = full facts, faction = orders.** Party
and raid members must have bags + skills available to the client WITHOUT
possession — the server pushes their inventory snapshots and known spells to
their party's humans. Non-party faction bots stay command-only. The Tier-2 RTS
world will define its own rules separately. **BUILT 2026-08-25** (same day,
follow-up session): both sides implemented and compile-verified, deploy
pending on the usual gates. Implementation record in
[`CRPG_PARTY_MEMBER_FACTS_NEXT_AGENT_PROMPT.md`](./CRPG_PARTY_MEMBER_FACTS_NEXT_AGENT_PROMPT.md)
(now carrying a build-status header).

## Executive decision

The interface should feel like the normal MMO HUD growing a tactical party
layer, not like entering a second RTS game.

The always-visible layer is deliberately small:

1. Four customizable one-shot abilities beside each controllable party member.
2. A role medallion half on and half off the lower-right of the portrait.
3. A bottom-middle command shelf with an explicit command scope.
4. Small, persistent feedback showing what each companion is currently doing.

Free View remains the full spatial-command layer. Detailed role behavior,
quick-slot policy, formations, and inventory open in panels instead of occupying
the combat HUD.

## Product goals

- Preserve ordinary MMO targeting, movement, action bars, bags, character
  panels, and loot behavior.
- Make three to four companions individually useful without requiring constant
  possession.
- Make every party-wide action show exactly who will obey it.
- Reuse existing Free View, selection, orders, control groups, action-button
  rendering, and party-frame geometry.
- Keep combat behavior and inventory mutations server-authoritative.
- Remain readable at the established 128x53 party-frame scale.

## Non-goals

- No paused or time-dilated combat.
- No replacement for the MMO target system.
- No magical shared party bag.
- No attack-move button on the primary combat shelf.
- No client JSON deciding an effective combat role or rotation.
- No vanilla item opcode sent as if it acts on a different character.

## The three kinds of focus

These states must be independent and visually distinct:

| State | Meaning | Recommended treatment |
|---|---|---|
| MMO target | Target of spells, attacks, inspection, and normal WoW interaction | Preserve the existing target ring and target frame unchanged |
| Party focus | Companion whose details, quick slots, or inventory are being inspected | Bright portrait frame or row highlight |
| Command selection | Companions that will receive the next RTS order | Separate selection pips/outline plus an explicit selected count |

Changing command selection must not silently change the MMO target. Directly
controlling a companion must not be inferred merely because that companion is
focused or selected.

## Drawing 1: normal MMO party HUD

```text
 PARTY
 ┌────────────────────────────────────────────────────────────┐
 │ [portrait◒T]  Aldric    ████████  ▰▰▰  [1][2][3][4] [HOLD] │
 │ [portrait◒H]  Mirelle   ███████   ▰▰▰  [1][2][3][4] [CAST] │
 │ [portrait◒D]  Kael      ██████    ▰▰   [1][2][3][4] [FOCUS]│
 └────────────────────────────────────────────────────────────┘

                   ┌─ ALL LINKED (3) ─────────────────────────┐
                   │ Regroup  Hold  Focus  Waypoint  Patrol   │
                   │ Link All                         Reset    │
                   └───────────────────────────────────────────┘
                    [normal player action bars remain below]
```

### Four quick actions per member

Four is the recommended permanent count. Three feels too restrictive once an
interrupt and emergency button are included; more than four makes four party
rows visually dominate the normal MMO HUD.

Recommended role templates:

| Role | Slot 1 | Slot 2 | Slot 3 | Slot 4 |
|---|---|---|---|---|
| Tank | Taunt/role action | Major defensive | Interrupt | Engage or mobility |
| Healer | Fast heal | Emergency heal | Dispel | Mana or utility action |
| Damage | Burst action | Interrupt | Crowd control | Defensive or utility |

These are defaults, not hard-coded spell categories. A player can configure
each companion and can optionally copy a layout to all companions of that
class.

Each slot is a **one-shot command**, not a rotation editor:

- Left-click requests the ability now.
- The current MMO target is used when it is a valid target for the ability.
- Otherwise the client enters an explicit targeting cursor. Ground targeting
  uses green/red validity feedback, left-click to commit, and right-click or
  Escape to cancel.
- Right-click opens a policy choice: `Never`, `By tactics`, or `Emergency only`.
  This policy says whether AI may use the ability; it does not rewrite a
  rotation profile.
- The button reuses normal action-button feedback for cooldown, resources,
  range, line of sight, pressed/queued state, and tooltip.
- A small dot or corner mark indicates that AI use is enabled.

Do not bind these buttons to plain `1` through `4`; those keys belong to the
player action bar. Mouse activation is sufficient for the first release.
Contextual, remappable chords can be considered after action-bar input can
explicitly claim or suppress collisions.

### Role medallion

The role marker is an 18-20 logical-pixel medallion at the portrait's
lower-right corner, approximately half inside and half outside the portrait:

- Shield plus `T`: tank.
- Cross or leaf plus `H`: healer.
- Crossed blades plus `D`: damage.

Shape, letter, and color are used together for accessibility. Portrait tint is
not used because tint already communicates disconnect, death, ghost, and low
health states.

During ordinary play the medallion is click-through so it cannot steal normal
party-row targeting or context-menu actions. It becomes interactive in an
explicit role/quick-slot edit mode. The role menu contains `Automatic`, `Tank`,
`Healer`, and `Damage`; unsupported choices are disabled with a reason.

The medallion ultimately displays the server-accepted **effective role**. Until
that hook exists, the client-only value must be described as a **preferred
role**, not as authoritative bot behavior.

### Per-member order state

Do not overload the role medallion with current-order information. A separate
small status chip or icon can show:

- Following or regrouping.
- Holding.
- Moving or patrolling.
- Focusing the MMO target.
- Queued manual ability.
- Out of command range or state unknown.

## Drawing 2: Free View and group commands

```text
 ┌─ FREE VIEW ─────────────────────────────────────────────────┐
 │                                                            │
 │       [T selected]  ───── waypoint 1 ───── waypoint 2       │
 │              [H selected]                                  │
 │                                      [D not selected]       │
 │                                ┌─────────────┐               │
 │                                │ enemy target│               │
 │                                └─────────────┘               │
 │                                                            │
 └────────────────────────────────────────────────────────────┘
  Selection: 2 companions       Order preview: queued route (2)

  [Selected 2]  Regroup  Hold  Focus  Waypoint  Patrol  Reset
```

### Existing controls to preserve

- `Ctrl+F`: enter or leave Free View.
- Left-click: select one companion.
- Shift+left-click: add or remove a companion from command selection.
- Drag: marquee-select companions.
- Right-click: contextual move or attack order.
- Shift+right-click: queue waypoints.
- Alt+left-click: take direct control of a party member.
- `Ctrl+Tab`: cycle direct control.
- `Ctrl+1` through `Ctrl+0`: tactical control groups.

This follows familiar Warcraft selection, contextual-order, queuing, and
control-group conventions without displacing normal MMO controls.

### Escape behavior

Escape unwinds temporary interaction state in this order:

1. Cancel spell or ground targeting.
2. Cancel an unfinished route/waypoint draft.
3. Close the command or tactics palette.
4. Leave Free View if nothing more local is active.

### Bottom-middle command shelf

**Status 2026-08-24:** a first slice ships in `GameLoop/Hud/GameLoop.CommandShelf.cs` —
Free View only, current selection, scope count first, with Hold plus the
formation/sheath verbs (orders 8/9/10, see
`SYSTEM_CRPG_CONTROL_GROUPS.md` §5b). The full shelf below remains the target.

The shelf always begins with scope and count:

- `All linked (3)`
- `Selected (2)`
- `Squad 4 (3)`

Recommended primary commands:

| Command | Behavior |
|---|---|
| Regroup | Cancel the tactical route and return to the controlled player/party anchor |
| Hold | Stay anchored and do not chase until released |
| Focus target | Prefer the current MMO target without changing the player's target |
| Waypoint | Enter route-authoring mode |
| Patrol | Patrol the current route or the next two selected points |
| Link all | Put all controllable companions in the linked command scope |
| Reset | Clear transient tactical orders and resume normal doctrine |

`Attack-move` remains in an advanced palette because an accidental pull is much
more expensive in an MMO dungeon than in an ordinary RTS match.

Eventually, Stop and Hold should be distinct:

- **Stop:** cancel the current action, then permit normal reactions.
- **Hold:** remain anchored and suppress chasing until explicitly released.

Before adding Stop, verify that the existing order named Hold truly suppresses
chasing rather than only cancelling movement.

## Drawing 3: roles, stances, and quick-slot editor

```text
 ┌─ ALDRIC — TACTICS ──────────────────────────────────────────┐
 │ Role       [Auto] [TANK✓] [Healer] [Damage]                 │
 │ Stance     [Guard] [Defensive✓] [Passive]                   │
 │ Protect    [Party leader ▼]    Formation [Standard ▼]       │
 │                                                            │
 │ Quick actions                                               │
 │ [Taunt]       [Shield Wall]   [Shield Bash]   [Charge]      │
 │ By tactics    Emergency only  Never           By tactics    │
 │                                                            │
 │ [Copy to warriors…]                     [Apply] [Cancel]    │
 └────────────────────────────────────────────────────────────┘
```

Role is the broad job the server and AI use when choosing behavior. Stance is a
smaller engagement rule. A useful initial stance set is:

- **Guard:** protect the configured anchor and react normally.
- **Defensive:** do not initiate a new engagement, but defend the party.
- **Passive:** execute explicit manual commands and safety behavior only.

Formation is a soft preference, not a rigid pathing promise:

- Compact.
- Standard.
- Spread.

Formation yields to doors, terrain, encounter rules, explicit orders, role
range, and safety. The UI must not imply exact synchronized positions when the
server cannot achieve them.

## Drawing 4: party inventory

```text
 ┌─ PARTY INVENTORY ───────────────────────────────────────────┐
 │ Owners: [You] [Aldric T] [Mirelle H✓] [Kael D]             │
 │ Search all party items: [_____________________________]     │
 ├──────────────────────────────┬───────────────────────────────┤
 │ Mirelle's bags               │ Mirelle's equipment           │
 │ [item][item][    ][item]     │ Head       [item]              │
 │ [    ][item][item][    ]     │ Chest      [item]              │
 │ [item][    ][item][item]     │ Main hand [item]              │
 │                              │                               │
 │ Owner glyph appears on every aggregate-search result         │
 ├──────────────────────────────┴───────────────────────────────┤
 │ Read-only snapshot                         [Give to… later]  │
 └──────────────────────────────────────────────────────────────┘
```

### Ownership model

Party Inventory is an ownership-aware browser, not a shared bag:

- A portrait rail chooses the owner.
- The selected owner's bags, equipment, and paper doll are shown.
- An optional aggregate search displays the owner on every result.
- Normal `B`, `Shift+B`, character-panel, and player-loot behavior remains
  session-character behavior.
- The first implementation is read-only.

The current snapshot system can display possessed-bot bags and equipment, but
the current vanilla mutation opcodes do not contain an acting companion GUID.
The client correctly fences those mutations to the session character. Never
work around that fence by sending an ordinary item opcode while another
character is displayed.

### Future transfer and equipment transaction

`Give to...`, stack splitting, equipping, and unequipping require a dedicated
server transaction containing:

- Request ID and protocol version.
- Full source-owner and destination-owner GUIDs.
- Full item GUID, source slot, destination slot, and count.
- Expected source and destination inventory revisions.

The server revalidates distance, combat state, life state, party authority,
binding, quest-item and currency restrictions, bag family, bag space,
class/level requirements, and unique-item rules. It then returns the
authoritative result plus a refreshed snapshot or delta.

The client dims and locks only the exact `(owner GUID, item GUID, slot)` pending
operation. Rejections explain why and restore the server snapshot. Loot remains
ordinary MMO loot; this interface never bypasses loot ownership, Need/Greed, or
binding rules.

## Command feedback and precedence

Every manual command needs an acknowledgement. Useful states are:

- Accepted.
- Queued.
- Started.
- Rejected.
- Interrupted.
- Expired.

Useful machine-readable rejection reasons include dead, not controllable, out
of range, line of sight, cooldown, global cooldown, insufficient resource,
invalid target, no path, lease lost, and superseded by a newer request.

The recommended server decision priority is:

1. Direct human control.
2. Safety and hard encounter constraints.
3. Explicit orders, doctrine, and a short-lived manual-ability intent.
4. Engagement selection.
5. Rotation.
6. Fallback behavior.

Only one current manual intent should exist per companion. New intent replaces
stale intent; requests have a short TTL and identical spam is coalesced.

## Command voice feedback (BUILT 2026-08-24, client-only)

Warcraft III's primary command feedback is the unit's voice, and vanilla already
ships a complete acknowledgement vocabulary: `EmotesTextSound.dbc` maps
(text-emote, race, sex) to the player vocal kits, and the StandardNPC pissed
kits cover the click-me-again lines. The client speaks them for companion
commands — no new asset, no wire change, no server work:

| Gesture | Speaker | Line |
|---|---|---|
| Plain select / marquee / group-card recall | first selected companion | Hello (emote 55) |
| Move, hold, waypoint, patrol order | first ordered companion | Yes (emote 67) |
| Attack order | first ordered companion | Charge (305) melee, Open fire (327) ranged |
| Chain-link a member | the driven body | Follow me (324) |
| Unchain a member | that member | Yes (67) |
| Server refuses control of a bot | the refusing bot | No (66) |
| Selecting the same companion once too often | that companion | Its race's StandardNPC pissed kit |

Implementation: `Net/CompanionVoiceLaw.cs` (pure gesture-to-vocal law),
`GameLoop/Scene/GameLoop.CompanionVoice.cs` (state, throttling, playback), call
sites beside every `SuiOrder` dispatch. The selection cycle reuses
`NpcGreetingLaw.SelectLine` — five hello takes, then the pissed variants, then
wrap — which is the vanilla NPC click law applied to the party. Lines are flat
interface voice at the effects volume (never distance-culled), one mouth at a
time, minimum 1.2 s spacing, dropped not queued. Gated by
`Settings.Controls.CompanionVoice` (default on, Options → Interface → CRPG/RTS).

Group orders answer as a **chorus** (owner request 2026-08-24): four or more
subjects get two voices, ten or more get three, cascading 0.4 s apart with
randomized speakers and distinct race/gender voices preferred — an army sounds
like an army, never like an echo.
`tools/companion-voice-clinical-check` proves every race/gender resolves every
line against the live archives (321 voice files).

When Phase B adds real rejection reasons, the per-reason spoken error lines in
`Sound\Character\<Race>\<Sex>ErrorMessages\` (out of range, no target, cooldown,
invalid attack target) are the natural escalation past the generic "No".

## Design language (owner-agreed direction, 2026-08-24)

The HTML mockup is a wireframe, not a visual spec. The shipped look follows:

- **Skin from vanilla WoW, command grammar from Warcraft III, keyed by mode.**
  The normal HUD must read as the 1.12 UI growing a tactical layer; Free View
  (`Ctrl+F`) is the costume change and may go full RTS console.
- **The pet bar is the native idiom** for commanding an AI companion:
  Attack/Follow/Stay map to Focus/Regroup/Hold, its stances map to
  Guard/Defensive/Passive, and quick-slot AI policy uses the autocast
  treatment (gold corner overlay for "may use", animated shine for "on") —
  not a colored dot. Verify exact assets via mpqpeek before building.
- **Role markers are corner-anchored icons fully on the frame** (leader-crown
  precedent), not half-off medallions; shape + letter, never color alone.
- **Order state is an icon with a tooltip**, not inline text.
- **Party inventory extends the vanilla Inspect frame** precedent; the command
  shelf stays bottom-center styled as a pet/shapeshift-bar sibling.
- **WC3 borrowings for Free View:** command-card styling, order-confirmation
  ping on the move marker, selection shown as mini portrait chips, and the
  voice channel above.

## Multiplayer, lifecycle, and edge cases

- Human party members may display informational roles but never show companion
  command buttons or receive companion orders.
- Party leadership/ownership must arbitrate conflicts when multiple humans can
  see the same bot. The UI shows when the local player lacks authority.
- Pets inherit their owner's command selection instead of adding extra party
  rows.
- Death, release, zoning, party removal, lease loss, and reconnect clear
  transient orders and pending casts. UI layout preferences remain.
- An out-of-range member's cooldown should be shown as unknown rather than as a
  false ready state unless the server has supplied a current snapshot.
- Role icons must remain distinguishable without color.
- The command shelf and four quick buttons join the existing UI layout manager
  and scaling rules; they must not float through menus or other panels.

## What the Windows client already provides

The client has most of the interaction shell:

- [`GameLoop.Control.cs`](../../../MSUIClient/GameLoop/Scene/GameLoop.Control.cs)
  distinguishes the session character, controlled unit, displayed action-bar
  unit, Free View, command selection, possession, contextual orders, marquee
  selection, and queued waypoints.
- [`GameLoop.RtsControlGroups.cs`](../../../MSUIClient/GameLoop/Hud/GameLoop.RtsControlGroups.cs)
  supplies ten session-local control groups, group cards, Hold, Patrol,
  auto-group, linking, and per-member Control.
- [`GameLoop.BotBars.cs`](../../../MSUIClient/GameLoop/Hud/GameLoop.BotBars.cs)
  already has layered per-bot/class action layouts and a text-heavy per-member
  RTS strip. That strip is the natural replacement seam for four icon buttons.
- [`GameLoop.ActionBars.cs`](../../../MSUIClient/GameLoop/Hud/GameLoop.ActionBars.cs)
  already renders icons, cooldowns, usability, range, pressed state, and
  tooltips. The party quick bar should reuse that renderer rather than create a
  second spell-button language.
- [`GameLoop.PartyFrames.cs`](../../../MSUIClient/GameLoop/Hud/GameLoop.PartyFrames.cs)
  provides the established 128x53 rows and portrait geometry. The portrait's
  lower-right corner is the role-medallion seam.
- [`GameLoop.Inventory.cs`](../../../MSUIClient/GameLoop/Panels/GameLoop.Inventory.cs)
  follows the controlled character for display while keeping mutation gated to
  the session character.

Existing group orders cover move, attack, hold, waypoint, patrol, link/unlink,
and auto-group. The first version of the command shelf can therefore use the
current order path. No new C++ is needed merely to rearrange those buttons.

Known client limitation: a never-possessed companion may not have a populated
known-spell/cooldown cache. A server member-facts snapshot is needed before the
four-button bar can be fully truthful for every party member.

## Required SuperUI-Core hooks

There is no authoritative C++ checkout in this Windows repository. The only
development checkout is the homeserver repository at `~/vmangos`. The names
below describe contracts and likely seams; they are not opcode assignments.

### 1. Companion member-facts snapshot

A versioned, capability-gated server message should expose, for authorized
party/faction members:

- Full member GUID and revision.
- Class and role capabilities.
- Server-accepted effective role and stance.
- Known active abilities or spell-chain roots.
- Cooldown/resource facts needed by the four-button bars.
- Current order and manual-intent state.
- Whether each category is current or unknown.

This can generalize the existing possession snapshot without pretending that
the client possesses every inspected member.

### 2. Companion command request and result

Use a typed request/result pair containing:

- Protocol version and request ID.
- Requesting actor GUID.
- One or more full subject GUIDs.
- Operation: query facts, set role, set stance, request one-shot ability,
  formation, or reset.
- Target kind and full target GUID or world position.
- Spell ID/chain root when relevant.
- Expected roster, role, and member-facts revisions.

Do not overload unused fields in the existing order packet. In particular, do
not hide new semantics in XYZ floats.

### 3. Server-authoritative role and manual intent

Likely server seams include:

- `src/game/SuperUiBots/SuiPossess.{h,cpp}` for shared authorization,
  capability negotiation, snapshots, and request routing.
- `AiBotAIMain.cpp` and `AiBotAIBridge.cpp` for effective role, stance, and the
  short-lived manual-cast intent.
- A typed packet definition near `Server/Packets/SuiControl.h` or its successor.
- `Protocol/Opcodes_1_12_1.h`, `Opcodes.cpp`, and `WorldSession` only after the
  canonical opcode registry is reconciled.

The shared command-subject predicate should validate party/faction membership,
live-eye visibility, lease/ownership, controllability, and lifecycle state.
Role changes increment a role revision and invalidate any incompatible cached
rotation binding.

### 4. GUID-scoped inventory protocol

Inventory mutation should be a separate, more restrictive request/result pair.
It needs full actor/item GUIDs, revisions, strict parsers, rate limiting, and
server calls into normal `Player` inventory validation rather than duplicated
client rules.

### 5. Capability and opcode safety

- Advertise every optional feature before the client sends its custom opcode.
- Keep capability bits append-only.
- Register exact packet lengths/parsers and reject trailing or malformed data.
- Reconcile opcode numbers with the dynamic-combat work. Numbers around 848/849
  are already provisionally discussed there and must not be independently
  claimed by this feature.
- Rate-limit roster/facts queries and inventory actions separately from
  high-frequency movement.

## Recommended implementation sequence

### Phase A: client-only comprehension shell

- Replace the text-heavy per-member strip with four icon slots.
- Add lower-right preferred-role medallions.
- Add the scoped bottom command shelf using existing orders.
- Add visible order-state chips.
- Add a read-only Party Inventory browser where snapshot data exists.

This phase proves layout and comprehension. It must label unavailable facts and
client-only preferred roles honestly.

### Phase B: authoritative companion tactics

- Add capability negotiation and member-facts snapshots.
- Populate spells/cooldowns for never-possessed companions.
- Add effective role and stance acknowledgement.
- Add one-shot ability requests, lifecycle results, and visible rejection
  reasons.

### Phase C: inventory mutation

- Add revisioned, GUID-scoped inventory snapshots.
- Implement transfer, split, equip, and unequip as server transactions.
- Reuse normal server inventory validation and return authoritative deltas.

### Phase D: deeper tactics

- Add soft formations and protect/focus policies.
- Integrate role/stance with server rotation and raid doctrine.
- Add remappable contextual quick-slot chords if they can coexist cleanly with
  player action bars.

## Acceptance criteria for the first playable slice

- A player can understand who will receive a party-wide command before clicking
  it.
- Normal MMO targeting and player action-bar keys behave exactly as before.
- Each controllable companion shows four configurable, truthful ability slots or
  a clear unknown/unavailable state.
- Role and current order are readable at a glance and are not encoded only by
  color.
- Free View selection does not silently alter the MMO target.
- Hold, regroup, focus, waypoint, patrol, and reset provide visible acceptance or
  rejection feedback.
- Human party members are never accidentally commandable.
- The inventory browser always shows item ownership.
- No bot inventory mutation is attempted with a vanilla session-character item
  opcode.
- Transient commands clear safely across death, zoning, party changes, and lease
  loss.

## External control-language references

- Blizzard, [Warcraft III controls for beginners](https://news.blizzard.com/en-us/article/23229495/finding-the-fun-real-time-strategy-games-for-beginners)
  — selection, marquee selection, contextual right-click orders, and control
  groups.
- Blizzard, [StarCraft II simplified controls](https://news.blizzard.com/en-us/article/6640645/game-guide-simplified-controls)
  — move, focus fire, hold, patrol, follow, queuing, and groups.
- Blizzard, [StarCraft II basic unit controls](https://news.blizzard.com/en-us/article/4552956/game-guide-basic-unit-controls)
  — the semantic distinction between Stop and Hold Position.
