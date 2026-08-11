# CRPG/RTS Mode — WIP (updated 2026-08-11)

> **STATUS: CORE LOOP OWNER-VERIFIED IN PLAY (2026-08-11).** Possession +
> movement, party follow, bot bags + character sheet, free-view collision camera
> all confirmed working in play after Rounds 11–16 (summary below; sections are
> newest-first). Client is at HEAD; the server binary on the box matches the
> build tree and was restarted with everything in it. Owner decisions in here
> are binding — do not redesign them.

## 2026-08-11 session summary (read this first)

The day started with "possession doesn't move the character and nobody follows"
and ended with the core loop verified. One line per round, newest first — each
has a full section below:

| Round | What | State |
|---|---|---|
| 16 | Camera-through-walls in NORMAL play (pre-existing): MinDistance clamp overrode the wall ray → vanilla-style first-person dip | fixed, needs eyes-on |
| 15 | Free-view camera is a floating BODY: collides with walls/ceilings, fly through doors (replaces the cutaway) | owner-verified |
| 14 | Free-view under-map clamp + Divinity cutaway v1 | clamp in; **cutaway REJECTED** (round 15), default OFF |
| 13 | Bot bags empty (FOUR stacked causes; killer: synthetic entities had `Entry = 0`) + stuck cast animation in free view | owner-verified |
| 12 | Cape geoset law, sheet name + stats (SNAPSHOT v2 wire block), walk-toggle clear, free-view cast/melee facing | working; facing needs a repro if still off |
| 11 | **The big one**: stale `SplineDonePending` made the server discard ALL client movement for any AI-walked body | owner-verified |

Open items going into the next session:
1. **Facing while commanded** (round 12): owner "not sure it's properly fixed" —
   needs the specific failing scenario (cast error vs model not turning vs melee
   drift; the last two have known designed limits).
2. **Walk-mode mystery** (round 12): mitigated by clearing the toggle on
   hand-offs; if it recurs, first check whether Shift was held (Shift-while-
   grounded = walk by design and it is in hand during control chords).
3. **Camera diagonal graze** (round 16): thin edges can still slip the single
   centre ray; add near-plane corner rays if seen live.
4. **Cutaway code is parked** behind `Settings.Controls.FreeViewCutaway`
   (default OFF) — delete it or revive it, but don't ship it silently.
5. **Scattered bots**: bots that sat in Solo doctrine get quested far away by
   the brain (pre-round-11 observation: Kaelrunner/Earthadin/Lornkeeper ended up
   across the world). RefreshDoctrine now abandons Solo-era journeys on joining
   a party, but there is no recall/regroup UX for already-scattered bots.
6. **Client TODO (v1.1)**: implement `CMSG_MOVE_SPLINE_DONE` — outside the SUI
   flows a server spline on the own character (charge-type effects) can still
   wedge the movement-blocking flag (round 11's clears cover only SUI paths).

## What this is

CRPG/RTS hybrid on top of the SuperUI possession stack: possess any party bot
(Ctrl+Tab / Alt+click portrait / **click a toon in the free view**), Ctrl+F for a
free RTS camera with marquee selection and right-click orders, Divinity-style
chain links on the party portraits, layered per-class/per-bot skillbars.

## The three codebases (all three matter — none is optional)

| Piece | Where | Role |
|---|---|---|
| Client | this repo (`MSUIClient/Program.Control.cs`, `Program.BotBars.cs`, `Program.PartyFrames.cs`, `Program.Portraits.cs`) | all UI/UX, SUI wire opcodes 0x33C–0x343 |
| Server C++ | `wowvmangos@192.168.0.2:~/vmangos`, branch `development` (no feature branches — owner rule: work directly on it) | possession, orders, follow/formation, streaming eye |
| Brain C# | `repos/MangosSuperUI` (deployed at `/opt/mangossuperui` on the box) | fleet goals; STANDS DOWN whenever `pparty=1` |

Server access (from this machine): `ssh -i MSUIClient/local-credentials/vmangos_ed25519
wowvmangos@192.168.0.2` (key lives IN THIS REPO, one level deeper than you'd guess;
`~/.ssh/id_ed25519_msui_vmangos_travel_20260731` is the travel laptop's key).
Build: `cd ~/vmangos/build && cmake --build . -j$(nproc)`. Deploy (owner runs it):
`make install`, then restart the **screen session** — mangosd is NOT under systemd:
`/usr/bin/SCREEN -DmS mangosd ~/vmangos/run/bin/mangosd`. Server logs:
`~/vmangos/run/bin/Server.log` (+ Movement.log etc. beside it); grep `[SUI]`,
`[AIBOT-DOCTRINE]`, `[SUI-FOLLOW]`, `[cutaway]` (client console) for this feature.

## ⚠ HARD RULE: client and server deploy TOGETHER on wire changes

Today's "Ctrl+F logs me out" bug was exactly this: the client sent the new
`CMSG_SUI_CAM` (835/0x343) to a server built before that opcode existed → the
unknown-opcode path **kicks the session**. If you add/repurpose opcodes, the
server one-liner above must run before the client is tested.

**Still true as of 2026-08-10 22:0x** — verified on the box: `mangosd` has been
running since **09:37**, but `6ed7716a6` (CMSG_SUI_CAM) landed at **19:32** and
`build/src/mangosd/mangosd` (20:23, matching HEAD `33e15c1f6`) was never
installed. The live binary's `NUM_MSG_TYPES` is 835, so opcode 835 trips
`IsDefinitelyBogusOpcode` and the socket closes — the free view sends its first
cam heartbeat within a frame of entering, hence "Ctrl+F boots me". **`make
install` + restart is the entire fix; nothing is wrong in the client here.**

## SUI order codes (CMSG_SUI_ORDER)

| Code | Meaning | Notes |
|---|---|---|
| 0 | move | clears any waypoint chain |
| 1 | attack | targetGuid |
| 2 | stop / hold | clears chain + patrol |
| 3 | queue waypoint | **Shift**+RightClick chain; arrival chains next leg (in-callback rule: `MoveToDestination(..., false)`) |
| 4 | patrol | converts chain to a loop (arrival re-queues popped point) |
| 5 | follow | injects `SET_ESCORT` (legacy; superseded by links for the portrait UX) |
| 6 | link/unlink | `x >= 0.5` links; unlink → `m_suiUnlinked`, `DoPartyFollow` early-returns |

Subjects list empty = whole party. `orderBot` gate is "AI-attached && !possessed"
so the **unattended own character obeys orders too**.

## Owner decisions (binding)

- **Free view: the personal party is NEVER autonomous** unless a future explicit
  command makes it so. Enforced by `pparty=1` for the enrolled real character
  (AiBotAIBridge.cpp STATE else-branch) — the brain holds Idle.
- **Clicking a party toon in the free view IS taking control of it** (same as
  Ctrl+Tab), bars live and castable. Not read-only inspection.
- **…and it does NOT leave the free view** (2026-08-10, revised). Possession is a
  CONTROL decision, the free view is a CAMERA decision, and they are independent:
  clicking a toon from the sky hands you its bars/bags/spells while the camera
  stays up. **Ctrl+F is the only thing that lands you.** Implemented as
  `_freeView`, a field separate from `ControlState`; the single enforcement point
  is `SeatControllerOnControlled`, which no-ops on the camera while `_freeView` is
  set, so every possess/release path inherits the rule.
- **Shift, not Ctrl, chains waypoints.** Ctrl is the control-chord modifier
  (Ctrl+F, Ctrl+Tab), so entering the free view with Ctrl still down turned the
  first right-click into a waypoint instead of a move. Shift is also the universal
  RTS queue-order binding.
- **Chain links are Divinity semantics**: linked members follow *whoever is being
  driven*; an unlinked member stands its ground. Not "x follows y".
- **A real character must NEVER be adoptable by the bot fleet** (see incident).

## Landed 2026-08-10 — server (committed on `development`; deployed + verified 08-11)

`6ef0ba6ae` FindEscortBoss possessed-first pre-pass (fixes party-follows-body)
`3059145e8` free-view pparty hold + orders reach unattended own char
`d91c9b2d2` **theft walls** (see incident below)
`6d26b5ecf` ORDER_MOVE_QUEUE waypoint chains
`6ed7716a6` CMSG_SUI_CAM + freecam streaming eye (World Trigger 15384 active-object
            summon rides the player Camera; `s_freecamEyes` in SuiPossess.cpp;
            torn down on possess/release/logout)
`5a17f83c9` ORDER_PATROL loop
`035562171` ORDER_FOLLOW via SET_ESCORT
`33e15c1f6` ORDER_LINK + DoPartyFollow gate

## Landed 2026-08-10 — client (committed as `0169804` "CRPG & RTS Work + Painterly")

- Free-view marquee (window `FreeSelectMode`: left = select, right = look),
  depth-tested ground-decal selection rings + move markers (procedural textures in
  `SpellEffectMeshRenderer`), `GroupSelectedGuids` highlight, waypoint-chain dots +
  dashed route, cam heartbeat (>5 yd or 2 s → `SuiCam`).
- Click-toon-to-possess from free view (`RequestPossess` accepts FreeCam origin;
  denial/watchdog fall back INTO the free view).
- **Layered bot bars** (`Program.BotBars.cs` + `botbars.json` repo root): generated
  baseline (active spells, best rank, wire slots 0-11 then 48-59) ← ClassSlots ←
  BotSlots (explicit 0 masks). Banner toggle picks save layer while possessing.
  `BotSpells`/`BotClasses` cache feeds free-view inspection; `BarsGuid`/`BarsReadOnly`
  swap bars for marquee-single selection (read-only, `UseAction` gated).
- **RTS strips** (`Settings.Controls.RtsCommands`, checkbox in Settings→Controls):
  role cycle T/H/D (client-persisted `BotRoles` — rotations hook is FUTURE WORK),
  Hold, Patrol, link chip.
- **Chain links UI**: permanent bead-chain down the party frames
  (`DrawPartyChainLinks`), broken stub when unlinked; drag portrait onto another/
  player frame = link, drag away >60 px into the open = unlink; `BotLinks` map.
- **Real party portraits** (`UpdatePartyPortraits`, Program.Portraits.cs): per-member
  bake, appearance-hash invalidation, one bake/frame; TemporaryPortrait art is now only
  the out-of-range fallback. **Reworked after the first look (uncommitted):** the bake
  now goes through `TryBakeCreaturePortrait` — the same booth as the target frame
  (authored M2 portrait camera → bounds fallback → blank retry) with the own-character
  portrait's `player:race-gender` tuning — instead of a hand-rolled fov-30 camera; it
  styles-then-`UpdateCircularCopy` like every other bake and hands the party frame the
  round handle. **The party frame was also drawing the bake with flat-art UVs, i.e.
  upside down** (`Vector2.Zero/One` instead of `(0,1)/(1,0)`) — that was the "those
  aren't the bots" symptom.
- Fixes: player-frame name + overhead name follow possession identity; name tag no
  longer rides the freecam rig; plain-F fly toggle ignores Ctrl.

## Round 3 — client, uncommitted (2026-08-10, after the first live free-view session)

1. **Ctrl no longer double-duties.** Waypoint chaining moved to Shift+RightClick
   (`HandleFreeCamWorldClick`). Residual overlap to watch: Shift is also the fly-rig
   `Boost`, so chaining while flying moves the camera faster.
2. **RTS edge scroll** (`UpdateFreeCamEdgePan`): pointer within 14 px of a screen edge
   slides the rig camera-relative, speed scaled by altitude above ground
   (`FlySpeed × clamp(alt/12, 0.45, 3.0)`). Suppressed during a marquee drag (the
   rectangle is screen-anchored), while the right button holds camera look, and while
   ImGui owns the pointer — so the bottom UI strip stays usable.
3. **Free view survives possession** — see the owner decision above. Touches
   `RenderSelfGuid`, `BarsGuid`, both ACK branches, `ToggleFreeView` (now lands on the
   commanded toon with no server round trip), `SwitchControlTo`, the click router in
   `Program.Targeting.cs`, the nameplate anchor, and the RTS overlays; all keyed on
   `_freeView` instead of `ControlState.FreeCam`. `UpdateFreeCamSelection` re-asserts
   `Flying`/`_character.Enabled` every frame because `ApplyControlledCharacter`
   re-enables the first-person body on every possess. New `ResetSuiControl()` drops both
   modes on socket loss, where no forced-release ACK can ever arrive.
4. **Selection rings tuck behind the model** (`SpellEffectMeshRenderer`): `RenderGroundQuads`
   never enabled depth TESTING (it inherited whatever the previous pass left) and applied the
   full `GROUND_FX_DEPTH_BIAS` of -8192 units, which at RTS camera range pulls a decal several
   yards toward the eye — so the ring's far arc drew straight through the body standing in it.
   Depth test is now explicit (write still off) and rings use `UnitAwareDepthBias` (-64): enough
   to clear the coplanar terrain, not enough to beat a unit. Spell decals keep the coarse bias.
   **-64 is a tuned guess** — raise it if rings z-fight the ground at distance, lower it if the
   model still fails to occlude.
5. **Party frames follow the driven unit** (`PartyFrameMembers`): the possessed bot leaves
   the party list (it holds the player frame) and the abandoned own character joins it via
   a synthesised row. Before this the bot appeared twice and the real character vanished.
   `UpdatePartyPortraits` bakes the frame set rather than the wire roster. Strict no-op
   while `ControlledGuid == LocalPlayerGuid`, so UI-parity captures are unaffected.

## Round 4 — client, uncommitted (2026-08-10, second free-view session)

All four were consequences of commanding-from-the-sky, which round 3 made reachable:

1. **Spell FX came out of the camera, not the caster.** `SpellEffectUnitPose` served the
   first-person body's pose for `ControlledGuid`, and that pose is the CONTROLLER's — the fly
   rig, i.e. screen centre. Now falls through to the streamed pose while `_freeView`.
2. **Player frame showed the commanded bot's name over the old face.** Round 3 hid the
   first-person body with `_character.Enabled = false`, but `CharacterRenderer.Render`
   early-returns on `!Enabled` and the portrait booth calls that same method — so the bake
   silently froze. The body is now suppressed at its world-pass call site
   (`Program.cs`, gated on `_freeView`) and `Enabled` is left alone.
3. **Ordered units would not move.** Two distinct causes:
   - Releasing while staying in the free view sent mode 0. `SuiPossess::DoRelease` answers
     mode 0 with `DetachUnattendedAI` + `RemoveFreecamEye` — so the abandoned own character
     had no AI left to obey, and the streaming eye died. `RequestControlRelease` now sends
     mode 1 whenever `_freeView`, whatever the caller asked for.
   - The commanded bot itself is unorderable by design (`orderBot` bails on
     `ai->IsPossessed()` — possession makes the CLIENT its mover). It is no longer
     auto-selected on the click that commands it, and it is filtered out of any subject list
     with a real message instead of a false "move to".
4. **Waypoint route outlived the walk.** `UpdateRtsWaypointProgress` retires the leading leg
   once any ordered subject stands within 3.5 yd of it (horizontal only — stairs), mirroring
   the server's own arrival chaining, and drops a chain that has made no progress in 45 s.

## Round 5 — client, uncommitted (2026-08-10, third free-view session)

1. **The commanded toon cast without animating.** `ApplySpellStart` / `ApplySpellGo` /
   `ApplyChannelStart` / `PlayBodyAnimation` all sent the CONTROLLED unit's body animation to
   `_character`. In the free view that body is not drawn — the driven unit streams in like any
   other player and `CreatureRenderer` owns its skeleton — so the animation played on nothing.
   New `ControlledBodyIsStreamed` picks the renderer; the HUD side-effects (cast bar,
   cooldowns, server-result emits) still key on `ControlledGuid` as before.
2. **Ctrl+F would not leave the free view.** Round 3's `staysInFreeView` rule ("a solicited
   release inside the free view is a control change only") also caught the release that Ctrl+F
   sends to LEAVE — both arrive as reason 16, so the code cannot tell them apart. Only the
   client knows which it sent: new `_freeViewExitRequested`, set in `ToggleFreeView`'s FreeCam
   case and cleared on entry, on local exit, and on session reset.

### ATTACK orders are dead on arrival (open, server-side — **pre-existing, not CRPG**)

Proven from `Server.log`, 21 occurrences during the 2026-08-10 session:

```
[AIBOT-BRIDGE] Tesfff:     ATTACK_TARGET creature guid 79945 not found on map
[AIBOT-BRIDGE] Kaelrunner: ATTACK_TARGET creature guid 79945 not found on map
[AIBOT-BRIDGE] Earthadin:  ATTACK_TARGET creature guid 79945 not found on map
```

The wire, the subject list and `orderBot` are all fine — the order reaches every member.
`AiBotAI::BridgeHandleAttackTarget` then rebuilds the target as
`ObjectGuid(HIGHGUID_UNIT, uint32(guidLow))`, i.e. **counter only, entry dropped**. A vmangos
creature guid embeds its entry, so `Map::GetCreature` can never match and the handler logs and
returns. The comment there ("Try with full GUID construction / Search nearby creatures as
fallback") describes a fallback that was never written.

**This is not a CRPG bug** — the same handler serves brain-issued `ATTACK_TARGET`, so fleet
attack commands down this path have always failed the same way. The entry is lost at the JSON
hop (`SuiPossess` ORDER_ATTACK sends `targetGuid.GetCounter()`), so the fix is either to carry
the entry in the payload or to have `SuiPossess` set the attack target directly from the full
`targetGuid` it already holds.

### The abandoned character runs off, and the party chases IT (open, server-side)

Reported 2026-08-10: possess any bot and the real character bolts, with the remaining bots
following the runaway body instead of the possessed toon.

Verified from source + the live `Server.log`:

- Possession does attach the unattended AI (`TryBegin` → `AttachUnattendedAI`); the matching
  `[SUI] Tesfff back under manual control` on every reason-16 release proves it was attached.
- **That AI has no brain.** The theft wall `d91c9b2d2` gates `UpdateBridgeTick` on
  `m_ownedDummyEntry`, so an AI on a real character never opens the bridge socket — by
  design, and it must stay that way. Its commit message assumes "doctrine plus
  CMSG_SUI_ORDER injections are the whole control surface".
- **The non-autonomy decree is not enforced anywhere that AI can see it.** `pparty=1` is
  computed inside the STATE producer in `AiBotAIBridge.cpp` — a message *to the brain*. For a
  real character that message is never sent and the brain would ignore it anyway. So nothing
  holds the body; it runs whatever the C++ doctrine picks by default. **This is the likely
  root cause of "booking it" and it needs a local hold, not an echo.**
- `FindEscortBoss`'s possessed-first pre-pass reads correctly and IS in the deployed build
  (`6ef0ba6ae` 09:02 < build 20:23), so "other bots follow the body" is NOT explained by
  static reading. Either it is downstream of the above (the body bolts, boss resolution
  transiently falls through to the only real session) or the pre-pass is not on the path that
  actually drives fleet-bot follow. **Needs a live diagnostic, not more code reading.**

## Round 16 — camera-through-walls (PRE-EXISTING, not CRPG; owner-reported 2026-08-11)

Pressing the orbit camera against a wall in NORMAL play let you see through it.
`ResolveCameraCollision` pulls the boom in correctly (full collision-world ray +
0.35 clearance) but then clamped `allowed` up to `Camera.MinDistance` (1.5 yd) —
so any wall closer than that behind the character's head simply won: the camera
parked inside the wall. Vanilla's answer is the first-person dip, now
implemented: the collision pass may collapse the boom to `FirstPersonDip`
(0.25 yd; the zoom wheel still stops at MinDistance), and the first-person body
render is suppressed below `FirstPersonBodyHide` (0.6 yd) so the camera doesn't
sit inside the model's head. Watch-for: a diagonal graze past a thin pillar edge
can still slip the single centre ray — add near-plane corner rays if seen live.

## Round 15 — cutaway REJECTED; free camera is a floating body instead (2026-08-11)

Owner saw the Round 14 cutaway live: the open-face dollhouse look is **"no good"**
— `FreeViewCutaway` now defaults OFF (kept as an experiment toggle; code intact
behind it). The clarified intent: in the free view the camera should behave like
a **floating body** — walls and ceilings stop it, so while the party is in room A
you naturally see room A and nothing impossible; to see another room you fly
through the door.

Implemented (client only): `CharacterController.FlyCollide` +
`FlyMove(delta)` — full-3D swept move with slide against the collision world
(like MoveHorizontal minus walkable-slope pass-through and step-up). WASD flight
AND the edge pan route through it; plain F fly stays a ghost. Gated by
`Settings.Controls.FreeViewCameraCollision` (default ON, checkbox in Settings →
Controls → CRPG/RTS).

Watch-fors: a rig that somehow starts INSIDE solid geometry may wedge (untick
the checkbox to escape); single-ray + radius sweep can slip through thin gaps
at extreme speed; the ORBITING camera around the rig has its own collision
(ResolveCameraCollision) which was not touched.

## Round 14 — free-view floor clamp + Divinity cutaway v1 (2026-08-11, client only; cutaway REJECTED — see Round 15)

1. **Fly rig can no longer sink beneath the map** in the free view:
   `CharacterController.FlyFloorClearance` (null = classic unclamped fly; the
   free view sets 2 yd, plain F fly stays a go-anywhere debug tool). Terrain
   only — WMO floors (city streets, bridges) are not sampled.
2. **Divinity-style building cutaway, v1** (owner picked trigger (a): commanding
   an indoor toon; hard hide, fade is future polish). Rides the PLAN_10 portal
   machinery: `WmoRenderer.SetCutawaySubject(worldPos)` resolves the commanded
   toon's cell exactly like the camera's (refusing pure-EXTERIOR 0x08 seed cells
   — "at the gate" must not cut a city), and for that ONE instance the
   authoritative ReachableGroups gate is fed by `ComputeCutawayGroups`: a
   view-independent portal BFS from the toon's cell that never traverses INTO a
   0x08 group. Shell + roof stay unreached → cull; the room and its
   portal-connected interior stay. **Deliberately removable**: one Settings
   checkbox (Controls → "Cut buildings away in the free view",
   `Settings.Controls.FreeViewCutaway`), and in code three marked blocks
   (property + seed resolution + flood override in WmoRenderer,
   `FreeViewCutawaySubject()` in Program.Control.cs, one feed line in
   Program.cs before UpdateCameraCell).
   Watch-fors on first live test: WMO-owned doodads (furniture) may still draw
   inside hidden groups; multi-storey buildings show every floor portal-reachable
   from the toon's (a "floors above" cut is a future refinement); the cut is
   per-instance, so neighbouring buildings are untouched.

## Round 13 — bot bags + stuck cast animation (2026-08-11, second play report)

**Bags (FOUR stacked causes, all fixed — the fourth found after the third's
deploy still showed empty slots):**
0. Client, the big one: the snapshot's synthetic `WorldEntity`s never set the
   plain `Entry` FIELD (only OBJECT_ENTRY inside `Fields`), and Entry is what
   every template consumer keys on — `Require(0, …)` is a silent no-op, so
   names/icons/tooltips/bag portraits all read "no item" while stack counts
   (field-driven) rendered fine, and nothing ever logged a failure. Owner
   verified sizes fixed but items invisible; `Entry = entry` closed it.
1. Client: `ApplySuiSnapshot` never set `CONTAINER_NUM_SLOTS` on the synthetic
   container entities, and the bag UI both sizes windows and enumerates contents
   from it (`Math.Min(numSlots, 36)` → 0 iterations) — the items were filed into
   CONTAINER_SLOT fields nobody ever read. Now set from the snapshot's bagSlots
   byte.
2. Server: vmangos' anti-datamining gate (`Item.PreventDataMining = 1` +
   `ItemPrototype::Discovered`) answers template queries for undiscovered entries
   with the not-found tombstone. Fabricated bots' gear can be undiscovered after a
   restart, so every name/icon/tooltip/bag-size for bot items came back refused.
   `AppendSnapshotItem` now marks each snapshot item's prototype Discovered.
3. Client: `Items.Apply` caches a tombstone as a permanent null template
   (Require never retries) — one refusal is forever. Kept the cache (query-storm
   guard: DiscoverItemTemplates re-Requires every frame) but it now logs
   `[items] template query for entry N answered EMPTY` so a refusal is visible.

**Stuck cast animation in the free view (client, fixed):** every spell-hold
CANCEL path (`ApplySpellFailure`, escape cancel, auto-repeat cancel, channel
stop) called `_character?.CancelSpellVisual()` — the first-person body — even
when the controlled unit is streamed (`ControlledBodyIsStreamed`). Start/Go
branched correctly (Round 5) but the cancels didn't, so an interrupted cast left
the streamed body looping its cast-state until the next cast overwrote the hold.
New `CancelControlledSpellVisual()` helper picks the renderer; all four sites
converted.

**Facing (Round 12) is deployed** (binary md5-verified running since 10:29) but
owner reports "not sure it's properly fixed" — needs a specific failing scenario:
cast-time facing errors should be gone; melee only faces at swing START (drift
mid-fight relies on the bot's own combat AI); mid-cast target movement is not
re-faced (vanilla behaviour).

## Round 12 — post-fix polish (2026-08-11, after Round 11 verified in play)

Owner confirmed movement is fixed. Four follow-ups from the first real play session:

1. **Cape length changed with the renderer** (client, fixed):
   `CharacterEquipment.ApplyGeosets` hard-coded cloak geoset variant 2 (the long
   1502 cloth) for any textured cloak; the streamed body correctly uses
   `1501 + GeosetGroup[0]` (CharacterGeosets.cs law). The first-person body now
   applies the same law — driving a toon no longer promotes its cape to long.
2. **Character sheet identity + stats** (client + server, fixed — WIRE ADDITION):
   the sheet header always printed the SESSION character's name (`_net.PlayerName`);
   now resolves the driven unit. Stats/armor/AP/damage are owner-only UNIT_FIELDs
   the vanilla wire never streams for another player, so the bot sheet was zeros:
   `SMSG_SUI_SNAPSHOT` now appends a raw stat block (5 stats, 7 resistances,
   AP+mods, RAP+mods, 3 attack times, 6 damage floats) and the client injects it
   verbatim into the bot's fields. Optional trailing bytes — old/new client+server
   mixes degrade gracefully (no new opcode, no kick risk).
3. **"My character goes into walk mode"** (client, mitigated): the Slash walk
   toggle (`_walkToggled`) survived control hand-offs invisibly; every hand-off
   now seats you running. NOTE: holding Shift while grounded is also walk by
   design (`Walking = _walkToggled || shift`), and Shift is in hand during
   control chords (reverse cycle, fly boost) — if walk still appears, check
   whether Shift was held; may deserve its own rethink.
4. **Free-view casts fail facing** (server, fixed): with the camera parked no
   orientation packets ever turn the acting unit, so frontal-arc checks failed
   and the commanded toon could not fight. `HandleCastSpellOpcode` and
   `HandleAttackSwingOpcode` now face the actor at the target when
   `SuiPossess::IsFreeViewUp(_player)` (new query: session player holds a live
   freecam eye — covers both the commanded bot AND the unattended own character
   acting through the bars). The facing spline this launches is safe because
   Round 11's arrival auto-clear covers bot-session players.

**Bot bags** were still broken at the end of this round — diagnosed and fixed in
Round 13 (four stacked causes).

## Round 11 — SplineDonePending: the REAL "I drive but nothing happens" (server, 2026-08-11)

Round 10 was necessary but not sufficient. The definitive signature (owner-reported):
the client predicts movement freely, but Ctrl+F reveals the body never left the
pickup spot — the server discarded every movement packet.

Root cause: `MoveSplineInit::Launch` sets `SetSplineDonePending(true)` for EVERY
spline on a Player, and the flag clears only when the controlling client sends
`CMSG_MOVE_SPLINE_DONE` — the arrival auto-clear in `Unit::UpdateSplineMovement`
explicitly skips Players. Fleet bots are Players with fake sessions that never send
it, so **any bot the AI has ever spline-walked carries the flag for life**. It costs
the fleet nothing (bots send no client movement) — it only bites when a human takes
the body: `HandleMovementOpcodes` drops every packet while the flag is set. Same for
the own character after the unattended AI walked it during the free view. This is why
possession worked right after the 23:48 restart (bots un-splined since boot; the brain
holds pparty bots) and failed for freshly created bots minutes later (spline-quested
through onboarding at creation) and for everything the morning after.

Fixed on the box (`development`, uncommitted; DEPLOYED 2026-08-11 morning and
owner-verified — "seems fixed"):

1. `Unit.cpp UpdateSplineMovement` — arrival auto-clear now also applies to Players
   whose own session is a bot session (a fake client can never confirm).
2. `SuiPossess.cpp TryBegin` — `SetSplineDonePending(false)` AFTER the stop sequence
   (StopMoving itself launches a stop spline that re-sets the flag), plus
   `SuiAbandonJourney()` (new AiBotAI helper: task + stored path + RTS waypoints)
   so the brain-era journey dies when a human takes the body.
3. `SuiPossess.cpp DetachUnattendedAI` — clear the flag on the own character when
   the human gets their body back (the AI era splined it).
4. `SuiPossess.cpp HandleCam(active=false)` — landing while commanding: remove the
   eye FIRST (kills the commanded waiver), then stop the controlled bot and clear
   its flag.
5. `AiBotAIMain.cpp MovementInform(TASK_DEST)` — no journey chaining while
   `m_possessed && !IsCommandedFromFreeView` (Finalize fires this callback from
   TryBegin's own stop; the next chunk would re-open a movespline that outranks the
   human's packets).
6. `AiBotAIMain.cpp RefreshDoctrine` — entering PlayerParty from Solo calls
   `SuiAbandonJourney()`: a Solo-era errand must not keep walking a bot that just
   joined a human's party (live TASK_MOVE_TO also gates DoPartyFollow — the
   "new member marches away instead of forming up" bug).

Client TODO (v1.1): implement `CMSG_MOVE_SPLINE_DONE`. Until then any server spline
on the own character OUTSIDE the SUI flows (charge-type effects) still wedges the
flag with nothing to clear it.

Also deployed in the same binary: the `[SUI-FOLLOW]` per-bot follow diagnostic
(task/unlinked/commanded/boss/dist every 5 s in PlayerParty), which the previous
session had built at 2026-08-11 00:16 but never installed.

## Round 10 — possession must STOP the bot first (server + client)

The "I drive but nothing happens" bug. `HandleMovementOpcodes` drops **every** client movement
packet while the mover has an unfinalized movespline:

```cpp
if (!pMover->movespline->Finalized())
    return;
```

`TryBegin` granted possession without stopping the bot. A bot is almost always mid-follow when
you take it, so its spline kept running: the client's input was discarded wholesale, the bot
walked on to wherever the AI had already sent it, and the human drove a body that ignored them.
Every reported symptom follows — no attacks (you are not where you think), nobody follows you,
and dropping to the free view reveals the character back with the party, which is simply where
the spline finished. `TryBegin` now does `StopMoving()` + `MotionMaster::Clear/MoveIdle` at the
grant, mirroring what `DetachUnattendedAI` already does on the way out.

**Client, same round:** `SyncDrivenEntityToController()` now refuses to publish while the
movement stream is parked or flying. Round 9 published unconditionally, which painted the
client's predicted position over the server's real one — the selection ring tracked you
perfectly while the character had not moved at all, hiding exactly this desync. If the stream
is silent the server's position is the truth and the client must show it.

## Round 9 — the driven unit's entity is published EVERY FRAME (client only)

`SyncDrivenEntityToController()` moved from the control hand-offs into the frame loop, right
after `_controller.Update`. Hand-off-only was treating a symptom: the driven unit is
client-authoritative, so its entity is the one thing the server never updates for us, and it
stays frozen at the pickup spot for the whole time you drive. Everything reading the ENTITY
rather than the controller renders it there — the visible one is `DrawSelectionRing`
(`Program.Targeting.cs`, `target.Position`), which leaves the blue player selection circle
sitting on the ground behind you as you run off. The teleport-on-hand-off was the same bug
caught at one instant; this is the general form.

## Round 8 — the free view has to be a SERVER fact (uncommitted, compiled, NOT deployed)

⚠ **WIRE CHANGE — client and server deploy together** (see the hard rule above).

Round 7's waiver keys on "possessor holds a freecam eye". But **landing was purely client-side**:
Ctrl+F while commanding just set `_freeView = false` locally and re-seated the camera. The
server never heard, so the eye stayed up, `IsCommandedFromFreeView` stayed true, and the bot's
own AI kept driving it. The human drove locally at the same time — movement was prediction
only, swings never landed, nobody followed, and the release ACK snapped the character back to
wherever the server actually had it. Round 7 traded one broken direction for the other.

- `CMSG_SUI_CAM` gains a trailing **ACTIVE** byte. Read as optional (`rpos() < size()`), so a
  sender predating it still reads as "up", which is what it meant.
- Client: every `_freeView` transition now goes through `SetFreeView(bool)`, which sends one
  `active: 0` cam packet on the way down and forces an immediate heartbeat on the way up. Five
  assignment sites collapsed into it — the bug was possible because they were scattered.
- `HandleCam(..., bool active)`: `!active` → `RemoveFreecamEye` and return.
- `RemoveFreecamEye` no longer blind-`ResetView()`s. Landing while STILL possessing must hand
  the camera back to the **bot** — a reset would point it at the abandoned body and stop
  streaming the world around the character being driven.

## Round 7 — "commanded == driven", the rest of it (uncommitted, compiled, NOT deployed)

Round 6 relaxed `orderBot`'s `IsPossessed()` bail and called the job done. It was not: that is
the OUTERMOST of **three** gates, and the order still died one level below it. All three are
now waived by one predicate, `SuiPossess::IsCommandedFromFreeView(bot)` — possessed AND the
possessor holds a live freecam eye:

1. `SuiPossess::orderBot` — the order reaches the bot (round 6).
2. **`AiBotAI::BridgeProcessLine`** (AiBotAIBridge.cpp) — dropped EVERY command but PING for a
   possessed bot, so the `MOVE_TO` was thrown away with a `POSSESSED_DROP` event. **This is
   where the move actually died.**
3. **`AiBotAI::UpdateAI`** (AiBotAIMain.cpp) — returned after `UpdateBridgeTick()` for a
   possessed bot, so even a queued task had no tick to walk it.
4. `DoPartyFollow` now early-returns for a commanded bot, so it holds the ground you sent it to
   instead of formation-walking back to the party the moment the leg finishes.

The resulting doctrine is PlayerParty (the possessor's own body resolves as boss): no grind, no
patrol, no wander, combat assist live, task machinery reachable. **Design note / risk:** the
server now drives a bot whose client is nominally its mover (`SetMover` + `SetClientControl`
are still in force). That is safe only because the free view parks the client's movement
stream — the controller is the camera and sends nothing. Needs live verification; if it
desyncs or trips anticheat, the fix is to drop client control for the duration of a free-view
command rather than to re-close these gates.

Client, same round: `SyncLocalPlayerEntityToController()` publishes the controller position into
the local player's ENTITY at both hand-off points (free-view entry, possess grant). Local
movement is client-authoritative, so the own character's entity is only the last SERVER
snapshot — often the spawn point. It never showed while the controller drew the body; the
moment `RenderSelfGuid` stops covering you, your body draws from that stale entity and appears
back where you entered the world until the unattended AI's first move corrects it.

## Round 6 — server, UNCOMMITTED on `development`, compiled clean, NOT deployed

Four fixes, `git diff` on the box to review (61 insertions across 4 files):

1. **`AiBotDoctrine.cpp` + `AiBotAIMain.h` — the runaway body.** Root cause found in the live
   log: `[AIBOT-DOCTRINE] Tesfff: (none) -> Solo` fires the moment the human enters the free
   view. With nobody possessed, `FindPartyBoss` finds no OTHER real member in a group of bots,
   so the ladder fell through to **Solo** — grind, patrol, wander. The non-autonomy decree was
   never enforceable there: `pparty=1` is an echo *to the brain*, and the theft wall keeps this
   AI off the bridge, so the echo has no reader. `ResolveDoctrine` now returns `PlayerParty`
   for any AI with `IsUnattendedRealCharacter()`. That invents no behaviour: formation on
   whoever is driven when someone is, `DoPartyFollow`'s no-boss early return (stand still) when
   nobody is, combat assist either way, and the branch stands the whole task machinery down.
   *This is also the likely fix for "the other bots follow the body" — they were escorting a
   legitimately-resolved boss that had gone grinding.*
2. **`SuiPossess.cpp` `HandleCam` — the streaming eye.** Now `EnsureFreecamEye` rather than
   only teleporting an existing one, so the eye tracks the client's real camera mode instead of
   its possession state. Client companion: `SeatControllerOnControlled` zeroes
   `_freecamCamSentAt` in the free view, forcing the heartbeat out next frame instead of
   waiting up to 2 s.
3. **`SuiPossess.cpp` `orderBot` — the commanded toon is orderable from the sky.** The
   `IsPossessed()` bail now applies only when the possessor has no freecam eye. The conflict
   it guards against (server MOVE_TO fighting the client's movement stream) cannot arise in
   the free view: the client's controller is the camera and its stream is parked.
   **→ the client-side subject filter in `HandleFreeCamWorldClick` has been deleted** (it was
   still refusing the order after the server started accepting it, and it was the thing putting
   "You are commanding X — Ctrl+F to land and drive it" on screen). `BarsGuid` also stops the
   marquee stealing the commanded toon's live bars: **commanding a character from the sky gives
   you the same character you would have driving it directly** — that is the rule, and any gate
   that makes it a spectator is a bug.
4. **`SuiPossess.cpp` + `AiBotAIBridge.cpp` — ATTACK_TARGET.** ORDER_ATTACK now sends
   `entry` alongside `guid`, and `BridgeHandleAttackTarget` rebuilds
   `ObjectGuid(HIGHGUID_UNIT, entry, counter)` — the form every correct caller in
   `AiBotAIMain` already uses. The entry-less path is kept for producers that omit it.

**Still broken, same bug, NOT touched** (out of scope, brain paths, no report against them):
`BridgeHandleInteractNpc` (AiBotAIBridge.cpp:1678) and the lookup at :2559 both use the same
entry-less `ObjectGuid(HIGHGUID_UNIT, counter)` and cannot resolve a creature either.

### Server-side, NOT done — needs a decision

- **The streaming eye dies when you command a toon from the sky.**
  `SuiPossess::HandleRequest` calls `RemoveFreecamEye(requester)` ("possess overrides the
  free-camera view"), which was true before round 3 and is not any more. `HandleCam` only
  teleports an eye that already exists (`if (Creature* eye = FreecamEyeOf(player))`), so it
  cannot bring it back. Suggested fix: have `HandleCam` call `EnsureFreecamEye(player)` —
  the client only sends `CMSG_SUI_CAM` while the free view is up, so the eye then tracks the
  client's real camera mode. Until this lands, flying far while commanding a toon will fly
  into unstreamed world.
- **Ordering the toon you are commanding** is refused by design (`orderBot` bails on
  `ai->IsPossessed()`). Owner-confirmed symptom: with a group marquee-selected, everyone moves
  EXCEPT the commanded one — and if the commanded one is the real character (unattended, not
  possessed) the whole group moves. If this should work, the server has to allow orders to a
  possessed bot while its possessor is in the free view. **When that lands, delete the
  matching client-side filter in `HandleFreeCamWorldClick`** (it drops `_controlTargetGuid`
  from the subject list so the chat line stops promising a move the server threw away).
- **`BridgeHandleAttackTarget` guid reconstruction** — see the section above.

## Verification status (2026-08-11 update — this section was "NOT yet verified")

The 2026-08-11 joint deploy happened; core loop is owner-verified (see the top
summary). The "uncommitted / NOT deployed" stamps on Rounds 6–8 below are
HISTORICAL — that code is compiled into the running server binary. Not yet
individually exercised in play: waypoint chains + patrol, links, party
portraits end-to-end, bot bars end-to-end.

## Known gaps / next steps

1. **Roles → rotations**: `BotRoles` is client-side only; wire into the brain/server
   rotation selection.
2. **Free-view bars for never-possessed bots**: spell cache only fills on first
   possession. Server could stream spell lists with the roster instead.
3. **Multi-human control authority** (who may possess whose bots) — undesigned.
4. **Patrol UX**: route is invisible after issuing; consider persisting/showing it.
5. **PlayerRenderer** exists but is unwired; players draw via CreatureRenderer
   (highlight lives there).
6. Streaming eye edge cases: freecam + teleport (no force-release when not
   possessing), cross-map camera not handled.
7. The party-portrait rework above is uncommitted — commit after live verification.

## Incident log (2026-08-10): character theft — DO NOT reintroduce

The enrolled unattended real character HELLO'ed the brain like a fleet bot; the
brain auto-registered it into `characters.playerbot`; the next mangosd restart
spawned it as a fabricated bot on a synthetic account (`SaveToDB` stamped the
account over NICO's) — the character vanished from the owner's list and quested
as a bot. Walls now in place: real chars never bridge-connect (`UpdateBridgeTick`
gates on `m_ownedDummyEntry`), the spawn path refuses guids whose account exists
in realmd (`OnSessionLoaded`), and the brain refuses to register real-account
characters (MangosSuperUI `main` `ac3a63c` — needs a brain deploy eventually).
Theft detector query: `SELECT ... FROM characters WHERE account NOT IN (SELECT id
FROM realmd.account)` — fleet bots are all on synthetic 10xxx accounts, real
characters must never be.
