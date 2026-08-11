# CRPG/RTS Mode — WIP (2026-08-10)

> **STATUS: WORK IN PROGRESS.** Large feature round landed today across client and
> server; several pieces are **implemented but not yet play-tested** because the
> session ended before a joint deploy. Read this whole file before touching the
> feature. Owner decisions in here are binding — do not redesign them.

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

Server access: `ssh -i ~/.ssh/id_ed25519_msui_vmangos_travel_20260731 wowvmangos@192.168.0.2`.
Build: `cd ~/vmangos/build && cmake --build . -j$(nproc)`. Deploy (owner runs it):
`cd ~/vmangos/build && make install && sudo systemctl restart mangosd`.

## ⚠ HARD RULE: client and server deploy TOGETHER on wire changes

Today's "Ctrl+F logs me out" bug was exactly this: the client sent the new
`CMSG_SUI_CAM` (835/0x343) to a server built before that opcode existed → the
unknown-opcode path **kicks the session**. If you add/repurpose opcodes, the
server one-liner above must run before the client is tested.

## SUI order codes (CMSG_SUI_ORDER)

| Code | Meaning | Notes |
|---|---|---|
| 0 | move | clears any waypoint chain |
| 1 | attack | targetGuid |
| 2 | stop / hold | clears chain + patrol |
| 3 | queue waypoint | Ctrl+RightClick chain; arrival chains next leg (in-callback rule: `MoveToDestination(..., false)`) |
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
- **Chain links are Divinity semantics**: linked members follow *whoever is being
  driven*; an unlinked member stands its ground. Not "x follows y".
- **A real character must NEVER be adoptable by the bot fleet** (see incident).

## Landed today — server (committed on `development`, compiled, deploy pending)

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

## Landed today — client (**UNCOMMITTED in this repo's working tree**)

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
  bake via `CreatureRenderer.RenderPortrait` (inspect machinery), appearance-hash
  invalidation, one bake/frame, circular mask; TemporaryPortrait art is now only the
  out-of-range fallback.
- Fixes: player-frame name + overhead name follow possession identity; name tag no
  longer rides the freecam rig; plain-F fly toggle ignores Ctrl.

## NOT yet verified in play (test after the joint deploy)

Everything above dated today. Specifically: freecam entity streaming (eye), waypoint
chains + patrol, links, click-to-possess, party portraits, bot bars end-to-end,
freecam hold (brain must NOT quest the own char — verified in code, not live).

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
7. Client work is uncommitted — commit after live verification.

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
