# System — Networking, Login, and the Live Entity Stream

Status date: 2026-07-29 (outbound movement + combat-wire foundation addendum).
Author's honesty clause up front: **this subsystem connects
to a live VMaNGOS realm and streams the world's entities, but almost everything a
player would *see* is placeholder or visibly broken.** It is scaffolding that proves
the pipe works end-to-end, not a finished multiplayer client. Read §1 and §12 before
you believe any screenshot.

The netstack itself (auth, crypto, packet decode) is the solid part and was proven
live: logging in as a real character streamed 218 objects / 57 creatures into
Stormwind. The login UI, character select, creature rendering, and the player's own
body are all first-pass and known-bad. Do not mark any of them "done."

---

## 0. The bar — what a finished version would be

Parity target is benilla + the retail 1.12 glue flow:

1. A textured, laid-out **login screen** (AccountLogin.xml) — the burning gate behind
   real WoW buttons, edit boxes, and fonts, animated.
2. A real **character-select** scene — your roster as posed 3D models on the dais,
   with create/delete, not a text list.
3. In world: **your** character (its saved race / gender / skin / hair / equipment),
   other players as their real models, and **creatures/NPCs textured, animated, and
   moving** along the server's splines, with nameplates and health.
4. Your movement **sent** to the server so you are a real participant, not a ghost
   observer. **Implemented 2026-07-29** for ground locomotion, facing, jump/land,
   and heartbeat state; forced-speed acknowledgements, swimming and transports
   remain follow-up work.

What exists today clears roughly step 0.5 of that. The gap is large and mostly in the
*presentation* layer, not the wire layer.

---

## 1. Status at a glance

| Piece | State | One-line truth |
|---|---|---|
| realmd SRP6 logon | **works** | authenticates against a live realm |
| World handshake + header crypto | **works** | vanilla header cipher, char enum |
| World-host override | **works** | works around the realm's unreachable internal IP |
| Semi-stateless character-select park | **works** | no character owned until you pick |
| Login screen | **placeholder** | an ImGui box over the gate — wrong fonts, colors, layout; no animation |
| Glue scene (UI_MainMenu gate) | **partial** | the gate renders; sky/fire animation and logo/buttons missing |
| Character select | **placeholder** | a list box of names, not the 3D roster; no create/delete |
| Entity stream (UPDATE_OBJECT) | **works** | GUIDs, fields, positions decode; 57 creatures seen live |
| Creature/NPC rendering | **broken (first pass)** | some models resolve; textures mostly fail → grey/black untextured meshes; static; no movement |
| Your character in world | **placeholder** | draws the TEST body (Human male + Battlegear), NOT your logged-in character |
| Other players in world | **not drawn** | skipped entirely (need composited character models) |
| Spawn placement | **improved** | trusts the server Z so you no longer fall under Stormwind |
| Player movement → server | **first pass built** | start/stop forward/back, strafe, turn, jump/land, facing and 500 ms heartbeats carry the post-collision pose |
| Combat / spells / health / nameplates | **wire foundation built** | attack/combat-log packets decode into a bounded typed event stream; presentation and target-driven attack input are next |
| Creature movement (splines) | **not handled** | SMSG_MONSTER_MOVE ignored → creatures never walk |

---

## 2. The connection path

`Net/NetworkClient.cs` runs the whole flow on a background thread and hands the game
loop a thread-safe inbound queue plus a one-shot "entered world" pose. States:
`Idle → ConnectingRealm → Authenticating → ConnectingWorld → CharacterSelect →
EnteringWorld → InWorld`.

### 2.1 realmd logon (SRP6)
`Net/RealmClient.cs` + `Net/Srp6Client.cs`. Standard vanilla SRP6 against realmd (port
3724). Returns the session key and the realm list. The account is upper-cased on the
wire by SRP (so the login box must NOT force caps — that was a bug we removed).

### 2.2 world handshake + header crypto
`Net/WorldSession.cs` + `Net/WorldHeaderCrypto.cs`. Connects mangosd, runs the
SMSG_AUTH_CHALLENGE / CMSG_AUTH_SESSION handshake, installs the vanilla header cipher,
then `CMSG_CHAR_ENUM` → the roster (`Net/Character.cs`, benilla roster field order).

### 2.3 the world-host override (the unreachable-IP fix)
Private realms usually run realmd + mangosd on one box but advertise an internal /
unreachable world IP in the realm DB (yours advertised `10.30.37.30`, which times out).
`ServerConfig.WorldUsesRealmdHost` (default **true**) reconnects the world server on the
same host we already reached for realmd, keeping the advertised port. This is the line
that got us in world. Set it false only if your world is genuinely on a different,
reachable host.

### 2.4 semi-stateless: parked at character select
Faithful to benilla: after char enum the worker **parks** on a `ManualResetEventSlim`
and does not own or log in any character. The app shows the roster and calls
`SelectCharacter(guid)`, which unblocks the worker to send `CMSG_PLAYER_LOGIN`. Only
`SMSG_LOGIN_VERIFY_WORLD` (the server's authoritative map + spawn pose) flips us to
`InWorld` and triggers the world load. Nothing about the world is assumed before that.

---

## 3. The login screen — placeholder (`GameLoop/Scene/GameLoop.Net.cs` `DrawLoginScreen`)

**Known bad, per the maintainer:** wrong formatting, wrong colors, no animation, not
the real text/layout. It is an **ImGui window** (account + password fields, a Log In
button) centered over the glue scene. It is functional — you can type credentials and
connect — but it is a debug panel, not the retail `AccountLogin.xml` UI.

What "right" needs: the real glue widget tree (textured buttons, WoW fonts, the
account/realm edit boxes at their authored positions), driven from the FrameXML/glue
layout, composited over the animated gate — not ImGui. This is a UI-framework effort
(benilla-ui / FrameXML), currently entirely absent.

---

## 4. The glue scene — partial (`Engine/GlueScene.cs`)

Renders `Interface\Glues\Models\UI_MainMenu\UI_MainMenu.m2` (AccountLogin.xml's
ModelFFX — the burning Stormwind gate) fullscreen through the M2's own authored camera
(fov/near/far and the position/target tracks read from the camera chunk), with fog
softened. The gate geometry and textures draw (22 batches).

Missing, and why it looks flat next to retail: the **animated color/alpha tracks** are
not parsed (so no warm moving sky, no pulsing light — M2Reader doesn't read those
tracks yet), the **fire particle emitters** are not wired to the particle renderer
(the gate should be burning), and there is no **WoW logo or buttons** composited on
top. So it reads as a static, cool-toned gate instead of the warm animated hero shot.

---

## 5. Character select — placeholder (`GameLoop/Scene/GameLoop.Net.cs` `DrawCharacterSelect`)

**Known bad, per the maintainer: "just a small box with names."** Correct. It is an
ImGui list of the roster (name, level, race, class, dead flag) with an Enter World
button. It reads the real roster from the server, so picking a character works — but
there is no 3D scene, no character posed on the dais, no create/delete/rename, no
equipment preview, no ordering. The real character-select is its own glue scene with
live character models; none of that exists.

---

## 6. The entity stream — works (`Net/UpdateObject.cs`, `Net/ObjectFields.cs`, `Net/Entities.cs`)

This is the reliable half. `SMSG_UPDATE_OBJECT` / `SMSG_COMPRESSED_UPDATE_OBJECT` are
decoded (packed GUIDs, movement blocks, and the sparse UpdateFields bitmask) into
`EntityStore` — a GUID-keyed, game-thread-owned world model. `SMSG_DESTROY_OBJECT`
removes. Each `WorldEntity` exposes position (raw WoW space), orientation, display id,
level, scale, and a health fraction (health in 1.12 rides UpdateFields, not a packet).

The game loop (`PumpNet`) drains the queue each frame and applies updates. Proven live:
218 objects / 57 creatures around the Stormwind spawn, logged with entry/display/level/
position. **This layer is trustworthy; the breakage downstream is all rendering.**

Not handled (queued but dropped): everything else — `SMSG_MONSTER_MOVE` (creature
locomotion/splines), combat, spells, chat, name/creature-query responses. So creatures
appear where they spawn and never move or acquire names.

---

## 7. The creature renderer — broken first pass (`World/Units/CreatureRenderer.cs`)

New this session. Iterates `EntityStore.Units`, filters to **creatures only** (players
are skipped — see §8), resolves each `displayId` through the DBC chain
(CreatureDisplayInfo → CreatureModelData → `.m2`), loads the M2 once (per-model VAO
cache, 4 loads/frame budget), and draws it with the **exact** `CharacterRenderer`
transform (`Scale · RotationY(heading) · Basis · Translate(pos)`, camera-relative), lit
+ fogged.

**Why it looks broken (the grey/black bits the maintainer reported):**

- **Textures mostly fail to resolve.** The monster-skin slot logic is naïve — for an
  empty (variation) texture slot it guesses `modelDir\<skin>.blp` from
  CreatureDisplayInfo's texture[0]. For many models that path is wrong or the BLP is
  elsewhere, so `LoadTexture` returns null, nothing is bound, and the mesh samples
  whatever texture is left bound / undefined → **untextured grey/black**. This is the
  #1 thing to fix and the most likely single cause of the reported look.
- **No skeletal animation.** Models draw in bind pose (T-pose-ish), not idle/walk. The
  `M2Animator` the character uses is not applied here yet.
- **No movement.** `SMSG_MONSTER_MOVE` is unhandled (§6), so creatures are frozen at
  spawn.
- **Some display ids don't resolve at all** (DBC gaps / unusual model data rows) and
  are silently skipped — those NPCs are simply absent.
- Orientation/scale are best-effort; if a whole class of models faces wrong, the Server
  HUD has live `Creature heading°` / `Creature scale×` sliders to confirm before
  touching code.

Where to start next: dump the resolved `modelPath` + `texture[0]` for a few known
creatures and compare to what the MPQ actually contains; the texture-path rule almost
certainly needs the real variation-texture resolution (and type-0 vs type-11/skin slot
handling), not the current guess.

---

## 8. The player in world — placeholder body + the spawn fix

**Your character is not drawn. The TEST character is.** In live mode we render the
existing offline debug avatar — a **Human male in Battlegear of Might** — at the
controller position, purely so "you" are visible. It ignores the logged-in character's
saved **race, gender, skin, face, hair, and equipment** (all of which the roster
already carries in `Net/Character.cs`). Making it *your* character means loading the
right race/gender model and compositing the saved appearance + the 19 equipment slots —
a real feature, not yet started. Other players are not drawn at all for the same reason
(they need the same composited-character path).

**Spawn placement (fixed this session).** Previously the world-loader's Finish phase
re-teleported you to *terrain* height, which in Stormwind is far below the WMO city
floor the server spawned you on (Z≈93) — so you fell under the city and had to fly up.
Networked spawns are now server-authoritative: the loader trusts the server Z and lets
the controller's own ground resolution settle you onto the WMO floor. You should stand
at street level. (Offline mode still samples terrain, unchanged.)

---

## 9. Ground truth — do not re-derive

**Inbound opcodes actually handled:** `SMSG_UPDATE_OBJECT`,
`SMSG_COMPRESSED_UPDATE_OBJECT`, `SMSG_DESTROY_OBJECT` (→ EntityStore);
`SMSG_LOGIN_VERIFY_WORLD`, `SMSG_NEW_WORLD` (→ enter/worldport pose),
`SMSG_MONSTER_MOVE`, and the attack/combat-log family listed in
`Net/CombatPackets.cs`.

**Outbound actually sent:** session/login/mover/ping/worldport packets plus the
ground `MSG_MOVE_*` transition stream. `LocalMovementSender` diffs each frame's
input flags, sends matching axis starts/stops, jump/fall-land, exact changed
facing, and a 500 ms heartbeat while moving. Clean world clicks now send selection;
right-click and engaged target switches send the attack start/stop latch commands.

**Coordinate + model transform (matches `CharacterRenderer`, the proven path):** M2
verts load raw; `modelMatrix = Scale · RotationY(heading) · Basis · Translate(worldPos)`,
then subtract the camera eye from the translation row (camera-relative), and multiply by
`Camera.RelativeViewProjection` (which looks from the origin, +Z up). `Basis` =
`(0,-1,0; 0,0,1; -1,0,0)` = Y-up model space → WoW axes, byte-identical to
`CharacterRenderer.ModelToWorld`. `heading = orientation + 90°`.

**Creature model chain:** `CreatureDisplayInfo.dbc` (display id → modelId, scale,
texture[3]) → `CreatureModelData.dbc` (modelId → `.m2` path, scale) → the M2 in the MPQ.
`Formats/CreatureDbc.cs` (`CreatureModelResolver.TryResolve`).

**Unit render-scale law:** render units with `OBJECT_FIELD_SCALE_X` alone (plus the explicit HUD
tuning multiplier). The server has already folded the two DBC scales above into that update field.
Multiplying DBC scale again double-applies it; native sub-1 beasts become scale² and visibly tiny.
This was the wolf/critters size bug fixed on 2026-07-29.

**Idle combat facing:** vmangos `SetInFront` is server-side and does not emit a new movement packet.
Each frame, a stationary living creature therefore turns locally toward `UNIT_FIELD_TARGET`, with
the ATTACKSTART victim as a fallback, along the shortest wrapped arc at 8 rad/s. A live spline owns
facing while moving and suppresses this idle correction.

**Death/motion law:** health reaching zero clears the creature spline immediately. The renderer
plays Death (AnimationData id 1) once and holds its final frame; a corpse first streamed dead starts
already settled, while a dead→alive edge queues Rise (id 7). Combat-log damage is never used to
derive death—the descriptor health delta remains authoritative.

**Known world-entry presentation bug (2026-07-29):** when the loading curtain lifts, the local
player currently plays a wound/hit reaction without a newly observed attack. The combat packet and
animation code exists, but this behavior is not correct or verified. Audit queued combat events and
establish descriptor/health baselines before presentation becomes eligible; also clear transient
combat animation state at world-load start/end. Do not infer a real hit from the initial descriptor
population, and do not solve this by merely delaying the visible animation.

**SUI capability trailer and portal cast catalog (2026-08-14):** the fixed
`SMSG_SUI_CONTROL_ACK` prefix may be followed by the historical eight-byte
little-endian trailer `u32 "SUI1" magic, u32 capabilityMask`. Bit 0 advertises
REAL_PORTALS v1. Bit 1 says an all-or-nothing cast-prewarm catalog follows:
`u8 version=1, u8 rowCount=6, u16 rowLength=32`, then six rows of
`u32 summonSpellId, portalEntry, teleportSpellId, previewMapId`, destination
`f32 x/y/z`, and `f32 orientation`. The six rows must be finite, unique, complete,
and use the exact stock summon/entry/use identities; the server authors the map
and destination pose. A malformed bit-1 extension disables only cast-start
prewarm while preserving independently negotiated bit-0 portal behavior. The
catalog never authorizes render, READY, use, or teleport: an own-player
`SMSG_SPELL_START` can only warm assets, and the later object must bind by exact
`GAMEOBJECT_CREATED_BY` plus portal entry before its authoritative descriptor can
adopt that warm.

**RTS R2 state, action and faction-force wire (2026-08-14):** opcodes 838-841
remain the stride-versioned RTS state/action pair. R2 activates their Honor and
hero blocks only when the server's immutable boot module mask says so. The
reserved 842/843 pair is now `CMSG/SMSG_SUI_FORCE_ROSTER`: a 14-byte request
pages active same-faction AiBots by zone and exclusive GUID-low cursor; the reply
has a 16-byte generation/page header and at most 200 fixed 32-byte rows carrying
full GUID, map/zone/xyz, race/class/level and server-derived eligibility flags.
The server scans live authoritative players per page, so the advisory total may
change during one traversal. The client stages correlated pages and publishes
only a complete generation. Faction Control is module bit `0x10`; without it no
roster scan or faction possession bypass occurs. Existing control ACK results 7
and 8 mean outdoor relocation-in-progress and denied foreign-instance transfer.
The byte tables, trust boundaries and paired repository ledger are canonical in
[`SYSTEM_RTS_R2.md`](SYSTEM_RTS_R2.md), not duplicated here.

**Config (`ClientConfig.Net.cs` → `client-config.json`):** `Server.Enabled` (master
opt-in), `Server.AutoConnect`, `Server.RealPortals` (true; matching SuperUI core
only), `Server.Account`, `Server.Password`, `Server.Realm`, `Server.Character`
(dev fast-path; skips select), `Server.WorldPortFallback` (8085),
`Server.WorldUsesRealmdHost` (true), `Server.TimeoutMs`. Realmd host/port are the
existing top-level `RealmdHost` / `RealmdPort`.

---

## 10. Files and responsibilities

Netstack (`Net/`):
- `Opcodes.cs` — 1.12.1 (build 5875) opcode enum, verified vs benilla-protocol.
- `ByteBuffer.cs` — readers/writers incl. packed-spline point decode.
- `Srp6Client.cs` / `RealmClient.cs` — SRP6 logon + realm list.
- `WorldHeaderCrypto.cs` / `WorldSession.cs` — header cipher + world handshake, char
  enum, player login, mover, ping, worldport ack.
- `NetworkClient.cs` — orchestrator + background thread + inbound queue + state machine
  + the world-host override + the character-select park.
- `Character.cs` — roster entry (race/gender/appearance/equipment/pos).
- `MovementInfo.cs` — the MSG_MOVE_* body + movement flags.
- `LocalMovementSender.cs` — local flags → transition/facing/heartbeat send law.
- `CombatPackets.cs` / `CombatState.cs` — typed combat wire + engagement/event seam.
- `CombatFeedbackLaw.cs` — pure ownership, number/word and crit law for outgoing world text.
- `PortalWire.cs` — REAL_PORTALS packets plus the `SUI1` capability trailer and
  strict six-row cast-prewarm catalog codec.
- `RtsWire.cs` -- strict atomic RTS state/action and paged faction-force codecs
  for opcodes 838-843; malformed bodies fail before publication.
- `Formats/FactionTemplate.cs` — faction-template catalog + directional reaction comparator.
- `tools/combat-wire-check` — focused checks for representative combat packets, movement
  jump-tail round trips, faction reaction precedence, and camera-ray alignment.
- `UpdateObject.cs` / `ObjectFields.cs` / `GuidInfo.cs` — UPDATE_OBJECT decode, sparse
  UpdateFields (including exact `GAMEOBJECT_CREATED_BY` GUID), GUID high-part decode.
- `Entities.cs` — `WorldEntity` + `EntityStore` (the client world model).

Client glue + render:
- `GameLoop/Scene/GameLoop.Net.cs` — game-loop integration: `InitNet`, `PumpNet`, the login / connecting
  / character-select / in-world ImGui screens, `DrawGlueScene`, `DrawCreatures`.
- `GameLoop/Combat/GameLoop.Targeting.cs` — occluded unit picking, selection/attack commands, faction
  attackability, and the first target frame.
- `GameLoop/Combat/GameLoop.CombatAnimations.cs` — routes each melee swing packet to attacker and victim one-shots.
- `GameLoop/Combat/GameLoop.CombatFeedback.cs` — floating world text, player health/power frame, and hit flashes.
- `GameLoop/Scene/GameLoop.RealPortals.cs` / `World/Portals/PortalPrewarmHint.cs` — capability
  consumption, spell START/GO/delay/failure lifetime, stock catalog validation,
  and exact spawned-object correlation.
- `Engine/GlueScene.cs` — the UI_MainMenu login gate.
- `World/Units/CreatureRenderer.cs` — the creature/NPC renderer (this doc's §7).
- `ClientConfig.Net.cs` — the `Server` config block.
- `GameLoop/Scene/GameLoop.Loading.cs` — the Finish-phase spawn teleport (server-Z fix, §8).

---

## 11. Tuning / HUD (the Server panel, in world)

`entities: N (creatures C, players P)`, `packets in: N`, `creatures drawn: N`
(0 = renderer off or nothing resolving), a **Draw creatures** toggle, **Creature
heading°** and **Creature scale×** live sliders, a Nearest-units list (distance, npc
entry, display id, level, health%), outbound movement packet/flag/last-opcode
counters, combat event/engagement counters, hover/selection/attack GUIDs, world-text
spawn/active/drop counters, combat-animation trigger/active/current-clip diagnostics, and
Disconnect. Use `creatures drawn` vs
`creatures C` to see how many resolve at all.

---

## 12. Not done — the honest ceiling

In rough priority order for a believable in-world picture:

1. **Creature textures.** Fix the model→texture resolution so NPCs stop rendering as
   grey/black. This is the most visible break (§7). Likely the variation-texture rule.
2. **Your real character.** Load the logged-in character's race/gender model and
   composite its saved skin/hair/face + 19 equipment slots, instead of the test Human
   male. Then render other players the same way. (`CharacterRenderer` +
   `CharacterEquipment` already exist for the offline avatar — the wiring is the work.)
3. **Creature animation breadth.** Idle/walk/run spline locomotion, idle target-facing,
   melee swing/wound/defense, engaged Ready stance, Death hold and Rise are built. Exact
   weapon-subclass picks, upper-body overlays, impact-keyframe timing and spell animation remain.
4. ~~**Send your movement.**~~ **First pass built 2026-07-29.** Remaining movement
   work is forced-speed change/ack, swimming, transports, and live multiplayer
   verification of observer smoothing.
5. **Real login screen.** Replace the ImGui box with the textured AccountLogin.xml glue
   layout (fonts, buttons, edit boxes) over the animated gate.
6. **Real character select.** A 3D roster scene with create/delete, not a name list.
7. **Glue polish.** Animated color/alpha tracks (warm sky), fire particle emitters,
   WoW logo/buttons on the gate.
8. **Combat presentation, spells, nameplates and chat.** Combat wire, local selection,
   right-click auto-attack initiation, reaction comparison, player/target health-power bars,
   hit flashes, outgoing world combat text and first melee swing/reaction animation are built.
   Final portrait chrome, combat log, casting, reputation/PvP reaction, TAB targeting,
   nameplates and final unit frames remain.

The wire and first targeting/auto-attack command path are implemented and build-checked; live realm
play remains the authority for interaction feel and server behaviour.

---

## 13. Build note

Both Debug and Release are verified with the repository's .NET SDK. The first compile of
`CreatureRenderer.cs` needed the repo's standard
`using Shader = MSUIClient.Engine.Shader;` / `using Texture = MSUIClient.Engine.Texture;`
aliases (every renderer carries them, to disambiguate the Silk.NET types); they are in
now. If a later change touches these files, expect the same CS0104 if the aliases are
dropped.

## 14. 2026-07-30 09:00 cast and character-entry corrections

Two client decisions adjacent to networking were tightened in the corrective pass:

- `Net/CastTargetLaw.cs` is the pure pre-send target-shape law. It mirrors Benilla's
  `ui_action/cast_target.rs` separation of self, selected-unit, ground and item targets. A helpful
  spell cannot be serialized against a hostile selected wolf; a self-capable helpful spell falls
  back to the player, while unsupported shapes are refused locally. Cooldown/GCD, pending-cast,
  mounted and known-range gates are also checked before send. Server responses remain authoritative.
- `GameSettings.LastCharacterGuid` persists and restores the selected character GUID. This is an
  explicit product requirement from live review; no Benilla persistence equivalent is claimed.
  Clicking Enter World calls `ArmEnterWorldCurtain` immediately, before asynchronous entry
  verification, so the existing world-ready gate cannot leak the gameplay HUD during transition.

Target-law tests pass and the current solution builds. Live proof is still required for Holy Light
with a hostile selection, logout/return selection restoration and a frame-clean Enter World
transition. The complete handoff is `July-30-20206-9AM-HANDOFF.md`.
