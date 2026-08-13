# REAL_PORTALS — true mage-portal windows and seamless walk-through travel

**Status:** first environmental-window vertical slice built 2026-08-13;
client-detected crossing, prepared-scene promotion, and destination exit
clipping are built. Server-verified movement crossing and remote actors remain
pending. See `docs/systems/SYSTEM_REAL_PORTALS.md`.

**Written:** 2026-08-12.  
**Scope:** summoned Mage portal GameObjects in the standalone MSUI client and the
custom VMaNGOS/SuperUI core.  
**Evidence boundary:** the client and the checked-in `.reference-vmangos-core`
were read directly. The live customized SuperUI core was reconciled on
2026-08-13: opcodes 844-847 and the four fixed packet layouts are now shared by
both implementations. The core currently provides descriptor/readiness leases;
the verified movement-crossing hook described below is not built yet.

---

## 0. Read this first — “portal” means four different things here

This document is about **summoned Mage portals**: networked, temporary,
type-22 `GAMEOBJECT_TYPE_SPELLCASTER` GameObjects which currently require
`CMSG_GAMEOBJ_USE` and cast a city teleport spell.

It is not any of these existing systems:

- **Instance entrance visuals** — `InstancePortal.m2`, its model-space particle
  disc, translucent film, fill light, and glow. That visual machinery is useful
  here, but it currently decorates static dungeon entrances. See
  `docs/systems/SYSTEM_INSTANCE_PORTALS.md`.
- **Area-trigger travel** — static `AreaTrigger.dbc` volumes and
  `CMSG_AREATRIGGER`, owned by `Program.Instances.cs`. Mage portals are summoned
  GameObjects and cannot be represented by static DBC trigger IDs.
- **WMO portal culling** — MOPT/MOPR polygons used to decide which building
  groups are visible. See `docs/systems/SYSTEM_WMO_PORTALS.md`.
- **A generic click-able GameObject** — the existing client interaction path is
  retained as an accessibility and stock-client fallback, but clicking is not
  the intended REAL_PORTALS interaction.

When this document says **source portal**, it means the summoned GameObject and
its oriented aperture. When it says **destination exit**, it means the virtual
frame at the teleport destination through which the preview camera looks. Mage
teleports do not normally create a physical return portal at that destination.

---

## 1. Outcome

The target is not “make the portal model bigger.” It is a temporary dual-world
client:

1. The source world remains active and playable.
2. Approaching a Mage portal begins a **per-player**, cancellable preload of a
   lightweight destination scene.
3. That scene is rendered through a tall, wide, nearly zero-depth aperture with
   correct parallax.
4. A paper-thin local membrane prevents this player crossing until the preview,
   destination collision, landing support, GPU work, and server readiness lease
   are all ready.
5. Walking through the plane, rather than clicking, is the activation gesture.
6. The server verifies the actual movement segment and all ordinary portal-use
   rules, then invokes the existing GameObject spell and teleport machinery.
7. When the vanilla teleport packet arrives, the prepared destination scene is
   promoted instead of tearing down and rebuilding the world.
8. The player sees no loading curtain on a prepared transfer. An unexpected or
   failed preparation always falls back to the ordinary authoritative transfer.

The central architecture is therefore:

```text
                               SharedWorldAssets
                            models / textures / shaders
                                       |
       +-------------------------------+-------------------------------+
       |                                                               |
 ActiveWorld                                                     PortalCandidate
 source WorldScene                                               destination WorldScene
 authoritative EntityStore                                      optional PreviewEntityStore
 movement collision                                              readiness-only collision
 source renderer state                                           portal render target
       |                                                               |
       +-------------------- WorldSceneHost ----------------------------+
                                      |
                         PortalTransitCoordinator
                    descriptor / READY / crossing / promotion
                                      |
                            normal VMaNGOS teleport
```

Only the active world can drive movement, combat, targeting, interaction,
minimap state, ordinary entities, or audio. A candidate world is visual and
readiness state until an authoritative teleport promotes it.

---

## 2. Class

**Addition**, measured against the written interaction and continuity contract
in this document.

The underlying data parsers, GameObject semantics, movement validation, spell
effects, and teleport handshakes remain **emulation-core**. REAL_PORTALS may add
a better interaction and presentation, but it must not weaken or replace the
server rules that make a vanilla Mage portal valid.

The intended look is not claimed to be original-client parity. Vanilla 1.12 did
not render a remote zone through Mage portals.

---

## 3. Player-facing target

### 3.1 Appearance

- A Mage portal is approximately a tall doorway, not a small clickable disc.
- The opening is a configurable rounded rectangle or ellipse with independent
  width and height and visually negligible depth.
- Its rim, motes, frost, glow, and local illumination remain visible from spawn.
- While this player is loading the destination, the opening is an opaque or
  translucent rippling film. It does not display a black, checkerboard, or stale
  frame.
- Once the destination has rendered one complete frame, the film clears into an
  actual perspective view. Moving sideways changes the view with correct
  parallax; it must not read like a texture pasted onto a quad.
- Source-world walls, players, and props can naturally occlude the opening.
- The destination preview and the portal particles are styled by the same final
  glow/painterly passes as the source frame.

### 3.2 Interaction

- There is no required click.
- Before this player is ready, the portal behaves like a very thin magical
  membrane. Forward motion is clamped/slid along the plane; source gameplay
  otherwise continues normally.
- When ready, a swept crossing from either geometric face inside the aperture
  activates it. `ONE_WAY` describes source-to-destination topology, not a
  restriction to one face of the source disc.
- A player may still click/use the GameObject as a fallback. Stock clients keep
  their existing behavior unchanged.
- Readiness belongs to `(session, portal GUID, spawn generation, descriptor
  revision)`. One player loading slowly never locks or disables the shared
  portal for anyone else.

### 3.3 Travel

- Cross-map and same-map portals both use their existing VMaNGOS teleport paths.
- On a prepared transfer the last destination-preview frame and the first active
  destination frame are visually continuous.
- The promotion frame performs no MPQ parsing, synchronous task wait, GPU upload,
  or bulk allocation.
- If the prepared scene is absent, stale, mismatched, or fails during transfer,
  the ordinary loading curtain completes the authoritative teleport.

### 3.4 Honest definition of “true window”

Version 1 renders real destination terrain, WMOs, doodads, liquid, sky, lighting,
and weather from local game data. That is a genuine second world render.

The normal server object stream only observes the player’s active `Map*`.
Therefore live destination players and NPCs are **not** part of version 1. They
require a later, separate, read-only portal snapshot stream. Until that phase,
the correct claim is:

> A live environmental window, with remote population deferred.

---

## 4. Ground truth from the current code

### 4.1 Mage portals are type-22 spellcaster GameObjects

The client already decodes GameObject query responses into a template containing
`Type`, `DisplayId`, and 24 integer data fields:

- `MSUIClient/Program.GameObjects.cs:14-24`
- `MSUIClient/Program.GameObjects.cs:50-72`

VMaNGOS defines type 22 as:

```text
data0 = spellId
data1 = charges
data2 = partyOnly
data3 = allowMounted
data4 = large
data5 = conditionID1
```

Reference: `.reference-vmangos-core/src/game/Objects/GameObjectDefines.h:427-436`.

The stock use handler validates that the object exists, is spawned, is
interactable, is within interaction distance, and passes `PlayerCanUse`, then
calls `GameObject::Use`:

- `.reference-vmangos-core/src/game/Handlers/SpellHandler.cpp:230-259`

The type-22 branch performs its owner/group/raid `partyOnly` checks, obtains
`spellcaster.spellId`, increments use count, and reaches the common spell-cast
tail:

- `.reference-vmangos-core/src/game/Objects/GameObject.cpp:1798-1834`
- `.reference-vmangos-core/src/game/Objects/GameObject.cpp:1987-2027`

**Consequence:** descriptor lookup and READY must never call `Use`. `Use` has
shared side effects, can lock/consume the GameObject, and belongs only to the
committed crossing.

Type 22 has no stock rectangular interaction primitive. Its ordinary use path
ultimately falls back to the core interaction-distance law when display bounds
do not provide a closer result:

- `.reference-vmangos-core/src/game/Objects/GameObject.cpp:2516-2537`
- `.reference-vmangos-core/src/game/Objects/GameObject.cpp:2588-2612`
- `.reference-vmangos-core/src/game/Objects/GameObjectDefines.h:759-785`

That is sufficient for the development spike near the center, but a player can
visually cross the edge of a wide portal while being too far from the GO origin
for stock click distance. Production crossing therefore validates distance to
the server-owned oriented rectangle instead of enlarging global interaction
range.

### 4.2 Portal creation and size

The six stock Mage “Portal:” spells use `SPELL_EFFECT_TRANS_DOOR` to create a
temporary GameObject. `Spell::EffectTransmitted` resolves the GameObject entry,
places it, creates it, records owner/group/spell metadata, applies its duration,
and adds it to the map:

- `.reference-vmangos-core/src/game/Spells/SpellEffects.cpp:5609-5750`

`GameObject::Create` publishes one uniform `OBJECT_FIELD_SCALE_X` from the
template’s `size` field:

- `.reference-vmangos-core/src/game/Objects/GameObject.cpp:184-240`

That scalar cannot express “much wider and taller, still near-zero depth.” The
REAL_PORTALS aperture geometry is therefore server-authored portal metadata and
procedural client geometry. The ordinary M2 is cosmetic; its bounding box does
not define the crossing volume.

The type-22 `large` flag changes visibility distance, not aperture shape:

- `.reference-vmangos-core/src/game/Objects/GameObject.cpp:252-263`

The mounted 1.12 Spell.dbc census found these stock summon-spell → GameObject
entry pairs:

| Mage portal summon spell | GameObject entry |
|---:|---:|
| 10059 | 176296 |
| 11416 | 176497 |
| 11417 | 176499 |
| 11418 | 176501 |
| 11419 | 176498 |
| 11420 | 176500 |

The familiar Mage self-teleport spells `3561`, `3562`, `3563`, `3565`, `3566`,
and `3567` are **not** the spell IDs stored by these GameObjects. The deployed
1.12 world templates contain these authoritative entry -> `data0` use-spell
pairs:

| GameObject entry | spellcaster `data0` use spell |
|---:|---:|
| 176296 | 17334 |
| 176497 | 17607 |
| 176498 | 17608 |
| 176499 | 17609 |
| 176500 | 17610 |
| 176501 | 17611 |

The server resolves the destination from that use spell's
`spell_target_position`. Neither the 356x list nor names are classifier or
destination authority.

### 4.3 The destination is server data

The teleport spell’s destination comes from the world database table
`spell_target_position`, loaded as map/X/Y/Z/orientation:

- `.reference-vmangos-core/src/game/Spells/SpellMgr.cpp:45-117`

`SPELL_EFFECT_TELEPORT_UNITS` selects the transport:

- same current map → `NearTeleportTo`
- another map → `Player::TeleportTo`

Reference:
`.reference-vmangos-core/src/game/Spells/SpellEffects.cpp:1560-1590`.

**Consequence:** the client never supplies a destination. A descriptor may carry
the current resolved destination as a preload hint, but the core resolves it
again at crossing and the vanilla teleport remains authoritative.

### 4.4 Same-map and cross-map are different protocols

Same-map teleport:

```text
server schedules near teleport
server -> MSG_MOVE_TELEPORT_ACK(counter, destination MovementInfo)
client adopts destination and returns MSG_MOVE_TELEPORT_ACK
server validates counter/GUID and executes relocation
```

Reference:
`.reference-vmangos-core/src/game/Handlers/MovementHandler.cpp:220-262`.

Cross-map teleport:

```text
server -> SMSG_TRANSFER_PENDING
server removes player from source map and stores destination
server -> SMSG_NEW_WORLD
client -> CMSG_MOVE_WORLDPORT_ACK
server selects/creates destination Map, relocates, adds player, sends initial state
```

References:

- `.reference-vmangos-core/src/game/Objects/Player.cpp:1992-2117`
- `.reference-vmangos-core/src/game/Handlers/MovementHandler.cpp:41-146`

This is one persistent world connection, not a zone-server socket migration.
The handshake costs at least a server scheduling boundary and network latency;
the visual design hides it but must not bypass it.

### 4.5 The client currently ACKs cross-map travel too early

`NetworkClient` sends `WorldportAck()` from the socket/read thread as soon as it
sees `SMSG_NEW_WORLD`:

- `MSUIClient/Net/NetworkClient.cs:643-649`

The game-thread packet drain separately treats `SMSG_NEW_WORLD` as an ordered
boundary:

- `MSUIClient/Program.Net.cs:460-469`

The immediate ACK allows the server to begin destination-map object delivery
before the render/game thread has adopted the destination. REAL_PORTALS requires
ACK ownership to move entirely to the game-thread transition:

1. Receive and queue `NEW_WORLD`.
2. Promote or load the destination.
3. Apply authoritative map and pose.
4. Send exactly one worldport ACK.
5. Drain destination state.

This correction is required even before a prepared scene is introduced.

### 4.6 The current loader is not reentrant

`BeginWorldLoad` and `StepWorldLoad` are a good bounded algorithm, but all state
belongs to the singleton `GameLoop` and the active renderer instances:

- `MSUIClient/Program.Loading.cs:108-120`
- `MSUIClient/Program.Loading.cs:164-254`
- `MSUIClient/Program.Loading.cs:376-760`

It reads `_config.Start`, `_residentCentre`, and active terrain/WMO/doodad/liquid
fields. Map travel calls `TearDownWorldContent`, which destroys placements,
terrain, liquid, collision, and particle state:

- `MSUIClient/Program.Instances.cs:602-629`
- `MSUIClient/Program.Net.cs:266-359`

`AdtCache`, `TerrainRenderer`, and `LiquidRenderer` key content only by tile
coordinates; the same `(col,row)` means different bytes on different maps:

- `MSUIClient/World/AdtCache.cs:29-44`
- `MSUIClient/World/TerrainRenderer.cs:17-58`
- `MSUIClient/World/LiquidRenderer.cs:86-90`

`TerrainRenderer.UnloadAll` can also wait for speculative preloads. A seamless
promotion cannot call it.

**Consequence:** a second destination cannot be faked by pointing the active
loader at another map and then pointing it back. World state must become an
owned, map-qualified scene.

### 4.7 The current visual is reusable but static

`ParticleRenderer.DrawPortalSurfaces` already builds a zero-depth surface using
the portal emitter’s model-space local Y/Z basis, with local X as its normal:

- `MSUIClient/World/Particles/ParticleRenderer.cs:1035-1107`

It draws the film immediately before additive portal sprites:

- `MSUIClient/World/Particles/ParticleRenderer.cs:836-849`

The complete source render draws source opaque geometry and units first, then
portal/world particles, then water and final post-processing:

- `MSUIClient/Program.cs:2182-2443`

This is the right broad composition order. However, the current particle pools
are supplied by static `DoodadRenderer` placements. Streamed GameObjects are
queried and interactable but not rendered as dynamic world models. Static
`DoodadRenderer.AddPlaced` has placement-based deduplication and no exact
GUID-owned lifetime, so summoned portals need an explicit dynamic registry.

### 4.8 Normal visibility cannot provide remote actors

VMaNGOS requires two objects to share the same `Map*` for normal visibility:

- `.reference-vmangos-core/src/game/Objects/Object.cpp:1705-1708`

Camera visibility visits the current map grid:

- `.reference-vmangos-core/src/game/Camera.cpp:153-164`

Phasing can filter objects already on that map; it cannot expose another map.
Destination player/NPC preview therefore cannot reuse `SMSG_UPDATE_OBJECT` or
the active `EntityStore`.

---

## 5. Locked design decisions

### D1 — two scenes, one authority

The client may hold an active source scene and a speculative destination scene,
but exactly one scene is authoritative. Promotion changes that ownership only
after the server names the destination through the normal teleport packet.

### D2 — promote; never rebuild at the plane

A warm crossing performs an ownership swap. It does not call
`TearDownWorldContent`, change an existing `AdtCache` map name, parse assets, or
wait for workers.

### D3 — share assets, isolate placement and residency

Textures, decoded/prepared models, immutable mesh data, and shaders should be
path-keyed and shared for the game lifetime. ADT residency, terrain heights,
WMO PVS/scratch state, WMO and doodad placements, liquid tile meshes, collision,
and atmosphere are scene-owned.

A prototype may temporarily instantiate a second set of renderers, but that is a
memory-heavy spike rather than the final ownership model.

### D4 — the server owns portal geometry and destination

The client may predict the aperture for presentation, but the production core
owns the source frame, dimensions, crossing direction, generation, expiry, and
destination hint. At crossing it resolves the teleport target again.

### D5 — readiness is per session and is not authorization

READY means “this client reports that its speculative scene passed the required
local gates.” It neither consumes a charge nor grants a teleport. The server
creates a short-lived per-session lease and revalidates everything at crossing.

No READY state is written into `GAMEOBJECT_FLAGS`, world masks, or the shared
GameObject.

### D6 — production crossing rides verified movement

The clean production activation signal is the ordinary player movement segment:

1. Movement packet is decoded and corrected.
2. Anticheat and normal movement validation run.
3. Before source-world relocation/broadcast, the core tests the last accepted
   pose to the verified candidate pose against nearby ready portal rectangles.
4. A valid crossing invokes the existing type-22 GameObject spell exactly once
   and consumes the movement instead of publishing a beyond-plane source pose.

There is no `CMSG_TELEPORT_ME` and the client never submits destination
coordinates.

### D7 — keep stock click use

`CMSG_GAMEOBJ_USE` remains valid for stock clients and as an MSUI accessibility
fallback. The first development spike may automatically send this existing
packet at a local crossing, but production wide-rectangle crossing belongs in
the verified server movement path.

### D8 — no recursive portals in version 1

The destination pass does not render other portal views. One portal view is one
additional world pass. Recursion is disproportionate complexity and cost for
one-way Mage city portals.

### D9 — environmental preview first, remote entities later

Version 1 does not fabricate live inhabitants. A later proxy stream is isolated,
read-only, phase/visibility filtered, rate-limited, and never targetable.

### D10 — unexpected travel always works

Hearthstones, GM `.tele`, instance travel, summons, death recovery, taxi edges,
and transfers from an unprepared portal retain a loading-curtain path. A portal
optimization may never make generic travel dependent on a prepared scene.

### D11 — fixed landing first, geometric continuity second

The safe first version uses the exact `spell_target_position` landing pose.
Preserving source lateral offset, facing delta, or velocity requires a separate
server-authored exit frame and collision validation. It is a later milestone,
not a client-side prediction silently substituted for server coordinates.

### D12 — the portal model is visual, not physical

The procedural aperture defines rendering and collision. M2 particles, model
bounds, display scale, and frost are presentation layers. This permits independent
width/height with effectively zero depth and prevents art changes from changing
gameplay geometry.

---

## 6. Proposed client ownership model

Names below are proposed responsibilities, not mandatory filenames.

```csharp
readonly record struct SceneId(int MapId, string MapName, uint Generation);

readonly record struct PortalKey(
    ulong GameObjectGuid,
    uint SpawnGeneration,
    uint DescriptorRevision,
    ulong Ticket);

[Flags]
enum PortalReadyGate
{
    Descriptor      = 1 << 0,
    CpuGeometry     = 1 << 1,
    GpuGeometry     = 1 << 2,
    Collision       = 1 << 3,
    SpawnSupport    = 1 << 4,
    FirstFrame      = 1 << 5,
    MemoryReserved  = 1 << 6,
}
```

### 6.1 `SharedWorldAssets`

Game-lifetime, immutable or reference-counted state:

- MPQ mount and format catalogs
- shader programs and common VAOs
- path-keyed WMO/M2 prepared CPU data
- path-keyed texture/array objects
- asset-worker and GPU-upload scheduling
- reference counts/deferred GL disposal

The existing `_assetWorkers` and `_uploads` already provide a foundation, but
WMO and doodad renderer model caches are currently private to each renderer and
must be separated from placements.

### 6.2 `WorldScene`

One map-qualified renderable package:

- `SceneId` and cancellation generation
- Map.dbc/WDT identity, including global-WMO placement
- scene-owned `AdtCache`
- terrain residency, heights, holes, and tile GPU instances
- WMO placements plus per-view/PVS scratch state
- doodad placements
- liquid tile meshes
- collision snapshot/BVH
- atmosphere state
- background discovery queues
- load/readiness diagnostics

A `WorldScene` contains no authoritative player or combat state.

### 6.3 `ActiveWorld`

- active `WorldScene`
- authoritative `EntityStore`
- controller collision source
- map-scoped gameplay/UI stores
- minimap/audio/interaction ownership

### 6.4 `PortalCandidate`

- `PortalKey`
- authoritative descriptor
- speculative `WorldScene`
- `PortalReadyGate` mask and timings
- `PortalRenderTarget`
- last complete preview frame
- optional, isolated `PreviewEntityStore`
- expiry, cancellation, memory reservation, and quality tier

### 6.5 `WorldSceneHost`

Owns:

- exactly one `ActiveWorld`
- at most one full-priority crossing candidate
- optionally one lower-priority warm candidate
- deferred-retirement scenes waiting on workers or GL fences

`Promote(candidate, authoritativeDestination)` transfers scene ownership in
constant time. The former active scene stays valid until the last frame fence
which references it completes.

### 6.6 `WorldLoadJob`

Extract the current `BeginWorldLoad` / `StepWorldLoad` algorithm into a reentrant
job taking an explicit scene, destination, and profile.

Profiles:

| Profile | Required content | Purpose |
|---|---|---|
| `ActiveFull` | current full load contract | login and generic travel |
| `PortalVisual` | portal-camera terrain/WMO/near doodads/liquid | first live view |
| `PortalCrossing` | visual set plus arrival geometry, collision, support | READY gate |

Every worker and upload result carries `SceneId.Generation`. A stale or cancelled
result disposes its package and may never publish into a newer scene.

### 6.7 `PortalTransitCoordinator`

Owns descriptor requests, candidate selection, READY leases, membrane state,
crossing latch, transition correlation, transfer timeouts, promotion, and
fallback. It is the only component allowed to connect a portal visual to travel.

### 6.8 `PortalVisualRegistry`

An exact GUID-keyed collection populated from GameObject create/update/destroy:

```text
GUID
source frame
half width / half height
display/model cosmetic
descriptor/readiness
render target handle
visual transition state
```

Do not infer identity by rounded world position and do not repeatedly call
static `DoodadRenderer.AddPlaced`.

---

## 7. Per-player state machine

```text
Absent
  -> Discovered
  -> DescriptorPending
  -> PrefetchCpu
  -> PrefetchGpu
  -> VisualReady
  -> CollisionReady
  -> ReadyPending
  -> ReadyConfirmed
  -> Crossing
  -> TransferPending
  -> PromotePending
  -> AwaitInitialState
  -> Active
```

Terminal/recovery branches:

```text
Denied | Expired | PortalDestroyed | Evicted | LoadFailed
TransferMismatch | TransferAborted | Disconnected | Cancelled
```

### 7.1 State meanings

| State | Meaning |
|---|---|
| `Discovered` | Visible type-22 GO is close enough to consider. Rim can render immediately. |
| `DescriptorPending` | Server is resolving portal geometry, generation, expiry, eligibility, and destination hint. |
| `PrefetchCpu` | ADT/WDT/WMO/M2/liquid/collision source preparation is running off-thread. |
| `PrefetchGpu` | Critical geometry is being adopted/uploaded within budget. |
| `VisualReady` | One complete environmental preview can render. Film begins clearing. Crossing remains sealed. |
| `CollisionReady` | Destination landing support and candidate collision are valid and promotion memory is reserved. |
| `ReadyPending` | Client has reported local readiness; no server lease yet. |
| `ReadyConfirmed` | Short-lived server lease exists. Portal is usable for this player. |
| `Crossing` | A directional aperture crossing has been submitted/observed; local movement is parked. |
| `TransferPending` | Server emitted entering/vanilla transfer state. Source is retained but no longer playable. |
| `PromotePending` | Vanilla destination packet arrived and is being correlated with the candidate. |
| `AwaitInitialState` | Candidate is active and normal ACK sent; wait for destination self/entity state before unpark. |

### 7.2 Local readiness

`LocalReady` requires all of the following:

- descriptor, ticket, generation, revision, and expiry still match;
- destination Map.dbc/WDT identity resolves;
- portal-camera geometry is resident;
- arrival geometry is resident;
- required global WMO/building placements are installed;
- collision BVH is complete;
- a downward support probe succeeds beneath the authoritative landing pose;
- every critical upload fence has signalled;
- one stable portal render-target frame has completed;
- enough memory is reserved to promote without allocating or evicting;
- no fatal asset error belongs to the current scene generation.

Distant doodads, foliage, and outer-ring terrain may continue streaming.
Destination actors are not a version-1 readiness gate.

```text
UsableForThisPlayer = LocalReady
                   && ReadyLease.Valid
                   && PortalStillPresent
                   && DescriptorStillMatches
```

---

## 8. Dynamic Mage portal visual

### 8.1 Discovery

On GameObject create/update:

1. Require the GameObject query template if missing.
2. Identify type 22 and ask the server whether the entry is REAL_PORTALS-enabled.
3. Create/update `PortalVisualInstance` by GUID.
4. Remove it on destroy, out-of-range, despawn, or map boundary.

The client may use type 22 plus the template spell for early cosmetic prediction,
but the server descriptor is the production classification. Not every spellcaster
GameObject is a teleport portal.

### 8.2 Cosmetic asset

Before implementation, probe `GameObjectDisplayInfo.dbc` from the mounted 1.12
archives and record the six stock Mage portal display IDs and model paths. The
client currently has no GameObject display-model resolver.

The aperture remains procedural regardless of which model is used. The existing
`InstancePortal.m2` particle treatment, frost film, center hollowing, glow, and
fill light may be reused or adapted as the rim layer. Dynamic emitter instances
must be fed to `ParticleRenderer` by GUID alongside static doodad emitters.

### 8.3 Geometry

Represent the source aperture as:

```text
center
right       horizontal unit vector
up          world +Z for ordinary Mage portals
through     unit normal, front -> destination
halfWidth
halfHeight
epsilon
```

The initial tuning values are intent, not Blizzard data, and belong in
server-owned portal metadata. A reasonable starting lab configuration is a
6-yard-wide by 8-yard-tall opening (`halfWidth=3`, `halfHeight=4`) with the
bottom approximately 0.1 yard above support. They must remain live-tunable until
paired in-game captures settle the feel.

The visual quad uses independent `right * halfWidth` and `up * halfHeight`.
Its fragment shader applies an ellipse or rounded-rectangle signed-distance mask,
edge feather, frost/ripple overlay, and readiness cross-fade.

---

## 9. Destination rendering

### 9.1 Pass order

Add `PortalViewManager.RenderViews` after atmosphere/update setup and before the
source sky pass in `GameLoop.Render` (`MSUIClient/Program.cs:2216-2229`).

For the scheduled portal:

1. Bind its offscreen target and clear color/depth.
2. Apply the candidate scene’s atmosphere.
3. Render destination sky.
4. Render destination terrain.
5. Render destination WMO.
6. Render destination doodads.
7. Render destination liquid.
8. Restore all GL and source-atmosphere state.
9. Render the source world normally.
10. Composite the destination texture at the existing portal-surface point.
11. Draw the portal rim/particles over it.
12. Let existing glow/painterly processing style the complete source frame.

Do not render destination HUD, debug overlays, active entity stores, portal
recursion, or a second whole-scene post-processing chain.

### 9.2 Render target

Create a pooled `PortalRenderTarget` with:

- single-sample RGBA8 color
- Depth24
- the same aspect ratio as the main framebuffer
- a persistent last-complete texture across resize/tier changes

`MSUIClient/Engine/PortraitRenderTarget.cs:58-90` is the allocation precedent.
Use the more complete state-guard behavior in `PainterlyPass` as the restoration
standard. At minimum preserve:

- draw/read framebuffer
- viewport and scissor
- shader program and VAO
- active texture bindings
- clear color and color/depth masks
- depth function
- blend, cull, depth, scissor, sRGB, and `ClipDistance0` enable state

No default-framebuffer stencil dependency is introduced in version 1.

### 9.3 Portal camera transform

Represent source and destination as orthonormal frames:

```text
S = (centerS, rightS, upS, throughS)
D = (centerD, rightD, upD, throughD)
```

Transform a vector by preserving its coefficients in the source frame:

```text
T(v) = rightD   * dot(v, rightS)
     + upD      * dot(v, upS)
     + throughD * dot(v, throughS)
```

Then:

```text
eyeD     = centerD + T(eyeS - centerS)
forwardD = normalize(T(forwardS))
cameraUp = normalize(T(cameraUpS))
targetD  = eyeD + forwardD
```

Preserve source FOV and aspect. Use a portal-specific far plane.

The current `Camera` already has `AuthoredPosition`, `AuthoredTarget`,
`AuthoredUp`, and an authored FOV (`MSUIClient/Engine/Camera.cs:101-108`), but
`Camera.Forward` ignores the authored target (`Camera.cs:139-150`). Correct that
before using an authored portal camera.

For version 1, render a full-viewport-aspect destination texture and sample it in
screen space. That avoids an off-axis projection requirement while retaining
correct parallax.

### 9.4 Screen-space composite

The surface fragment shader samples:

```glsl
vec2 screenUv = gl_FragCoord.xy / uMainFramebufferSize;
vec4 view = texture(uPortalView, screenUv);
```

Do **not** map the ordinary perspective render across portal-local UVs. That
creates a stretched television/painting whose view does not respond correctly to
the source camera.

Extend the current portal-surface shader with at least:

```text
uPortalView
uPortalViewAvailable
uLiveBlend
uMainFramebufferSize
uHalfWidth / uHalfHeight or already-scaled basis vectors
uShape / edge-feather parameters
```

The existing static film remains the fallback. Cross-fade to the first complete
view over roughly 0.2 seconds; never cross-fade to an incomplete target.

### 9.5 Destination clip plane

Geometry behind the virtual destination exit must not leak into the window.
Add optional clip-plane uniforms to terrain, WMO, doodad, and water vertex
shaders:

```glsl
uniform int  uPortalClipEnabled;
uniform vec4 uPortalClipPlaneRel;

gl_ClipDistance[0] = uPortalClipEnabled != 0
    ? dot(vec4(relativeWorldPosition, 1.0), uPortalClipPlaneRel)
    : 1.0;
```

For the world-space keep plane:

```text
dot(n, world - centerD) >= epsilon
```

and camera-relative geometry:

```text
planeRel.xyz = n
planeRel.w   = dot(n, eyeD - centerD) - epsilon
```

Enable `GL_CLIP_DISTANCE0` only during the candidate pass. Do not clip sky.
Relevant world positions already exist in:

- `MSUIClient/Shaders/terrain.vert`
- `MSUIClient/Shaders/wmo.vert`
- `MSUIClient/Shaders/doodad.vert`
- `MSUIClient/Shaders/water.vert`

### 9.6 Transparency and water

The live aperture composites with source depth test and no color-pass depth
write so the portal rim can remain volumetric. Source water currently renders
after portal particles. If water behind the portal incorrectly blends over the
view, add a dedicated aperture-depth seal after rim particles and before source
water:

- color mask off
- depth write on
- plane offset approximately 0.05–0.10 yard through the aperture

Water physically in front of the portal must still overlay it. The offset and
order are tuning points and require a captured validation scene.

### 9.7 Crossing veil

Even a prepared transfer takes a network round trip. At contact the portal
naturally fills most of the camera. Once the server reports `ENTERING`, hold the
destination view across the framebuffer for the brief handoff, park source
movement, and hide the source body if necessary. This is not a loading screen;
it is a continuation of the portal camera already being viewed.

On promotion, replace that texture with the active destination render. If the
authoritative destination differs from the descriptor beyond tolerance, abandon
the veil and use the generic curtain rather than snapping to a false scene.

---

## 10. Client movement and membrane

### 10.1 Integration point

The controller currently advances at `MSUIClient/Program.cs:1774`, publishes the
driven entity at `:1780`, sends movement at `:1790-1795`, then runs existing
instance-portal checks at `:1802-1805`.

REAL_PORTALS motion filtering belongs between controller simulation and entity/
network publication:

```text
previous controller pose
-> CharacterController.Update(proposed pose)
-> PortalTransitCoordinator.ResolveMotion(previous, proposed)
-> SyncDrivenEntityToController
-> LocalMovementSender
```

### 10.2 Local membrane

For every nearby portal, compute signed plane distance:

```text
d(p) = dot(p - center, through)
```

If the player is on the source side and the proposed capsule crosses while the
portal is not `ReadyConfirmed`:

1. Find the segment/plane intersection.
2. Clamp the capsule to `intersection + through * sourceEpsilon`.
3. Remove only the velocity component into the plane.
4. Preserve tangential motion so the player slides along the surface.
5. Keep the film/rim responsive; optionally pulse a restrained loading ripple.

This is a per-client movement filter, not world collision and not a GO flag.

### 10.3 Client crossing detection

Use a swept test, not “currently inside a thick trigger”:

```text
dPrevious > +epsilon
dProposed <= -epsilon
velocity points through the plane
intersection lateral coordinate is inside halfWidth + capsule radius
player capsule vertically overlaps centerZ +/- halfHeight
crossing latch is armed
```

Arm only after the player has been outside the epsilon band. Debounce once per
portal generation so standing on the plane cannot spam activation.

For the production server-crossing path, force the verified crossing movement to
the wire, then park subsequent movement until the server answers. The source
world must never receive a series of accepted positions beyond the plane.

For the no-core development spike, clamp at the source side, flush a movement
stop, and automatically send the existing `CMSG_GAMEOBJ_USE(guid)`. That spike
is limited by the stock center-distance interaction rule and is not the final
wide-portal authority model.

---

## 11. Server authority

The production implementation adds a capability-gated `PortalMgr` to the custom
SuperUI core. Ordinary GameObject, spell, movement, and teleport paths remain the
commit machinery.

### 11.1 Portal registration

Register REAL_PORTALS-enabled type-22 GameObjects when they enter the map and
unregister them on despawn/removal. The stock Mage creation path reaches
`Map::Add(pGameObj)` in `Spell::EffectTransmitted`.

Each runtime portal receives a monotonic spawn generation. A full GUID alone is
not sufficient protection against delayed READY messages after removal/reuse.

Maintain a spatial index by map/cell. Movement crossing may query nearby portals;
it must never scan every portal in the world.

### 11.2 Server-owned template

Add server data keyed by GameObject entry, conceptually:

```sql
real_portal_template
  gameobject_entry       primary key
  local_center_x
  local_center_y
  local_center_z
  yaw_offset
  half_width
  half_height
  plane_epsilon
  preload_radius
  flags
```

`local_center_z` normally lifts the aperture center so its bottom meets the
floor. `yaw_offset` aligns the procedural normal with the visual asset. Template
reload increments a global descriptor revision and revokes stale READY leases.

The teleport destination remains `spellcaster.spellId -> spell_target_position`.

### 11.3 Shared eligibility helper

Today the relevant checks are split between
`WorldSession::HandleGameObjectUseOpcode`, `GameObject::PlayerCanUse`, and the
type-22 branch of `GameObject::Use`. Factor them into side-effect-free validation
plus one side-effecting commit, for example:

```text
CanUseSpellcaster(player, gameObject, useSource)
TryUseSpellcaster(player, gameObject, useSource)
```

Both stock click and portal crossing must share:

- correct map and mover
- GO exists, is spawned, and is not deleted
- visible/in phase
- no `GO_FLAG_NO_INTERACT`
- `PlayerCanUse` and condition checks
- mount policy
- owner/group/raid `partyOnly`
- charges, cooldown, duration, and teleport-pending state
- valid spell and current target metadata

Only distance geometry differs:

- stock click → existing interaction-distance law
- verified crossing → intersection inside the server-owned oriented rectangle

Only `TryUseSpellcaster` invokes `GameObject::Use`/spell preparation and consumes
the use.

### 11.4 READY validation

On client READY, validate in this order:

1. SUI feature/version negotiated and packet length exact.
2. Session is in world and not already teleporting.
3. GUID resolves on the player’s current `Map`.
4. GO is REAL_PORTALS-enabled type 22.
5. spawn generation, descriptor revision, and ticket match.
6. GO is spawned, visible/in phase, and interactable.
7. Player is within the configured preload radius.
8. teleport spell and server target metadata still resolve.
9. shared type-22 eligibility passes without side effects.
10. lifetime/charges/cooldown permit a future use.

Then create/renew a short READY lease keyed by the session and portal key. Rate
limit requests and cap live READY records per player. A client load failure never
creates a lease.

READY remains a quality-of-experience assertion, not security authority.

### 11.5 Verified swept crossing

Hook self-movement after packet correction, movement/anticheat validation, and
before `HandleMoverRelocation` publishes the candidate source-world pose. In the
reference core this seam is around
`.reference-vmangos-core/src/game/Handlers/MovementHandler.cpp:315-347`.

For a nearby portal:

```text
normal = (cos(yaw), sin(yaw), 0)
right  = (-sin(yaw), cos(yaw), 0)
up     = (0, 0, 1)

d0 = dot(previousAccepted - center, normal)
d1 = dot(verifiedCandidate - center, normal)
t  = d0 / (d0 - d1)
hit = lerp(previousAccepted, verifiedCandidate, t)
```

Require:

- either-face motion toward/through the plane with epsilon/hysteresis; a
  one-way portal still leads to the same destination from both source faces;
- active, matching READY lease;
- lateral hit within `halfWidth + playerRadius`;
- player capsule overlaps the aperture’s vertical interval;
- crossing not already consumed for this generation;
- no teleport already pending.

At the crossing instant, re-fetch the live GO and re-run all eligibility,
lifetime, charge, and destination checks. If valid:

1. consume the crossing debounce;
2. send portal state `ENTERING` to the capable client;
3. invoke the shared type-22 use exactly once;
4. return before applying/broadcasting the beyond-plane movement.

If invalid, reject that movement, send `BLOCKED` or `REVOKED`, and correct the
client to the last accepted source-side pose through the ordinary authoritative
movement correction mechanism.

Stock clients do not receive a membrane or movement crossing behavior. They
continue walking through the cosmetic model and can click it normally.

### 11.6 Same-map commit

Retain the stock near-teleport flow. The client receives the destination
`MSG_MOVE_TELEPORT_ACK`, promotes prepared same-map residency first, then returns
the standard ACK. The server validates GUID/counter and relocates through
`HandleMoveTeleportAckOpcode`.

The client does not invent a destination or custom teleport ACK.

### 11.7 Cross-map commit

Retain `Player::ExecuteTeleportFar`:

1. validate map/instance entry and binding;
2. send `SMSG_TRANSFER_PENDING`;
3. remove player from the old map;
4. store authoritative destination;
5. send `SMSG_NEW_WORLD`;
6. wait for standard worldport ACK;
7. create/select destination map, relocate/add player, send initial state.

The preview can suppress the curtain but cannot send ACK before `NEW_WORLD`.
Instance/group rules may make the final destination differ from a preview hint;
the client must detect this and fall back.

---

## 12. Wire protocol

### 12.1 Opcode authority first

The client currently defines custom SUI opcodes `0x033C` through `0x0349` in
`MSUIClient/Net/Opcodes.cs`. The checked-in reference core does not contain that
custom protocol, and stock VMaNGOS treats opcodes outside `NUM_MSG_TYPES` as
malformed.

Therefore:

1. Read the live SuperUI core’s `SUI_WIRE_PROTOCOL.md` and opcode table.
2. Confirm its capability negotiation and `IsSuiCapable` behavior.
3. Reserve symbols there before editing either side.
4. If the current range is exactly the client range, the provisional next block
   is `0x034A` onward; these numbers are **not authoritative until that audit**.
5. Never probe an unmodified server with an unknown out-of-range opcode. When
   REAL_PORTALS support is not negotiated/configured, use ordinary clickable
   portals.

Custom SMSGs are only sent to a session which first identified itself as SUI
capable. Stock clients never see this protocol.

### 12.2 Minimal production messages

Symbolic messages:

```text
CMSG_SUI_PORTAL_PREPARE
SMSG_SUI_PORTAL_DESCRIPTOR
CMSG_SUI_PORTAL_READY
SMSG_SUI_PORTAL_STATE
```

No custom teleport/commit message is required in the chosen design; verified
normal movement is the crossing signal.

All packets are versioned, exact-length, and little-endian through the existing
packet writer/reader conventions.

#### `CMSG_SUI_PORTAL_PREPARE`

```text
u8  version = 1
u8  reserved = 0
u16 requestFlags
u32 requestId
u64 portalGuid
```

The client may send this for a nearby candidate type-22 GO. The server decides
whether it is REAL_PORTALS-enabled and eligible for a descriptor.

#### `SMSG_SUI_PORTAL_DESCRIPTOR`

```text
u8  version = 1
u8  result
u16 flags
u32 requestId
u64 portalGuid
u32 spawnGeneration
u32 descriptorRevision
u64 ticket
u32 portalEntry
u32 teleportSpellId             // informational; server still owns it
u32 remainingLifetimeMs
f32 sourceCenterX
f32 sourceCenterY
f32 sourceCenterZ
f32 sourceYaw
f32 halfWidth
f32 halfHeight
f32 planeEpsilon
u32 previewMapId
f32 previewX
f32 previewY
f32 previewZ
f32 previewOrientation
```

Descriptor flags include, at minimum:

```text
ONE_WAY
PARTY_ONLY
CLICK_FALLBACK
SAME_MAP_HINT
BIDIRECTIONAL        // reserved; stock Mage portals are one-way
```

The destination is a preview hint resolved from current server data. It is not a
promise that bypasses later instance/binding validation.

#### `CMSG_SUI_PORTAL_READY`

```text
u8  version = 1
u8  loadResult                  // READY or FAILED
u16 reserved = 0
u64 portalGuid
u32 spawnGeneration
u32 descriptorRevision
u64 ticket
```

Client asset hashes, if ever added, are telemetry only and never authorization.

#### `SMSG_SUI_PORTAL_STATE`

```text
u8  version = 1
u8  state
u8  reason
u8  reserved = 0
u64 portalGuid
u32 spawnGeneration
u32 descriptorRevision
u64 ticket
u32 leaseOrRetryMs
```

States:

```text
READY
REVOKED
BLOCKED
ENTERING
EXPIRED
FAILED
```

Reasons include stale generation/revision, not visible, not eligible, not ready,
out of preload range, expired, no charge, target changed, transfer pending, and
rate limited.

### 12.3 Lease cleanup

Clear/revoke per-player portal state on:

- GO despawn/removal or generation change
- descriptor configuration reload
- expiry
- loss of visibility/phase/eligibility
- player map/instance change
- logout/disconnect
- another teleport beginning
- death, transport, or taxi state if policy disallows entry
- successful crossing

Malformed/stale READY traffic is logged and rate-limited.

---

## 13. Network transition and scene promotion

### 13.1 Correct ACK ownership

Remove `session.WorldportAck()` from the socket-thread `SMSG_NEW_WORLD` case in
`MSUIClient/Net/NetworkClient.cs:643-649`.

The network reader may update diagnostic status, but it must not acknowledge a
world the game thread has not adopted.

Retain the ordered game-thread boundary in `Program.Net.cs:460-469`; do not parse
destination object updates past it in the same drain.

### 13.2 Do not mutate world state inside `PumpNet`

The beginning of `PumpNet` currently clears stores, tears down content, repoints
the ADT cache, changes `_config.Start`, and teleports the controller
(`Program.Net.cs:266-359`). Replace that with classification and scheduling.

Map/scene adoption belongs in an outside-pump transition. The existing
`PumpWorldEntryTransition` seam at `Program.Net.cs:1145-1195` can be generalized
for this ownership transfer.

### 13.3 `SMSG_TRANSFER_PENDING`

At `Program.Net.cs:423-435`:

- matching `ReadyConfirmed` portal and predicted destination → park movement,
  retain the live portal veil, suppress the opaque curtain;
- anything else → arm the existing curtain.

`TRANSFER_PENDING` is not permission to change map identity or ACK.

### 13.4 Cross-map `SMSG_NEW_WORLD`

For a candidate correlated by `ENTERING` state:

1. Verify ticket/generation/revision are current.
2. Verify destination map and pose are within descriptor tolerance.
3. Verify candidate scene generation and readiness are still valid.
4. Atomically promote the candidate `WorldScene`.
5. Commit active map identity and `_config.Start`.
6. Clear/reset the authoritative entity and map-scoped gameplay stores.
7. Apply authoritative controller, facing, and camera pose.
8. Reset movement state.
9. Send exactly one game-thread `WorldportAck`.
10. Enter `AwaitInitialState` and begin draining destination packets.

Unpark after the destination self-create/authoritative player state arrives, or
after an optional server `COMMITTED` state emitted only after successful
destination `Map::Add`.

If any check fails before ACK, raise the generic curtain and finish through the
ordinary loader. Do not promote a mismatched candidate.

### 13.5 Same-map `MSG_MOVE_TELEPORT_ACK`

At `Program.Net.cs:561-592`, for a matching portal transition:

1. verify candidate/destination;
2. promote its local residency first;
3. apply authoritative controller and camera pose;
4. reset movement;
5. return the standard teleport ACK.

The core does not execute near relocation until this ACK. Unmatched same-map
teleports use the ordinary same-map residency path.

### 13.6 Entity stores

Version 1 clears the authoritative source `EntityStore` on promotion and accepts
fresh destination updates after ACK. A later `PreviewEntityStore` is not promoted
as authority; proxies may be reconciled visually, but targeting/combat state must
come from ordinary destination updates.

### 13.7 Scene retirement

The old source scene moves to a deferred-retirement queue only after promotion.
It remains renderable until the final GPU fence referencing it completes. Worker
results belonging to a retired generation dispose themselves rather than being
published.

Never synchronously wait for speculative source/candidate work during promotion.

---

## 14. Geometric arrival continuity — later milestone

Vanilla `spell_target_position` supplies one fixed landing pose. If three players
cross different parts of a six-yard opening, all three currently collapse to the
same point. The environmental camera can still be convincing, but perfect
body/camera continuity requires a server-owned destination exit frame.

When enabled:

1. Compute source lateral coordinate from the authoritative crossing hit:

   ```text
   lateral = dot(hit - sourceCenter, sourceRight)
   ```

2. Clamp it to the safe authored destination width.
3. Place the destination at:

   ```text
   destinationExitCenter
   + destinationRight * lateral
   + destinationThrough * landingEpsilon
   ```

4. Resolve Z/support on the server. Do not preserve arbitrary vertical offset;
   source and destination floors may differ.
5. Transform facing relative to the two portal frames.
6. Validate collision, map coordinates, instance rules, and safe support.
7. Fall back to the exact stock target when any validation fails.

Version 1 resets velocity as the stock teleport does. Transforming/preserving
horizontal velocity is a separate later decision and must remain server-checked.

---

## 15. Failure and recovery law

| Failure | Required behavior |
|---|---|
| Descriptor denied | Keep rim/fallback film or ordinary GO visual; no candidate and no membrane unless configured as decorative. Click fallback remains. |
| Portal expires/despawns/revises before crossing | Revoke lease, reseal/remove membrane, cancel by generation, retain source play, retire candidate asynchronously. |
| Candidate load/memory failure | Keep sealed film, send READY failed if protocol expects it, back off; never declare usable. |
| READY lease expires near plane | Reseal and clamp at source epsilon; request renewal without consuming a use. |
| Crossing request/response timeout before server ENTERING | Restore source-side controller, unpark, require exit/re-entry before another attempt. |
| Server blocks crossing | Accept authoritative correction, pulse film with restrained denial feedback, clear lease/debounce as directed. |
| `SMSG_TRANSFER_ABORTED` before source removal completes | Source remains active; unpark, clear transition, retain/discard candidate by reason. |
| Transfer begins but candidate becomes invalid | Core may already have removed player from source map. Freeze, raise generic curtain, finish authoritative transfer; do not resume source play. |
| `NEW_WORLD` destination mismatch | Never promote candidate. Use generic loader and delayed game-thread ACK. |
| Promotion fails before ACK | Leave ACK unsent, raise curtain, run generic loader. |
| Failure after ACK | Follow server recovery packets. Never locally roll back to retired source authority. |
| Disconnect/context loss | Invalidate leases/tickets, cancel candidates, dispose through deferred cleanup. |
| More portals than budget | Nearest/projected-largest candidate wins; others retain animated static film. |

The fallback is intentionally asymmetric around ACK. Before ACK, the client can
still choose a safe loader. After ACK, the server owns recovery completely.

---

## 16. Scheduling, quality, and budgets

### 16.1 Priority order

```text
1. Active-world crossing/residency safety
2. Portal candidate crossing-critical geometry/collision
3. Active-world ordinary near streaming
4. Portal preview cosmetics
5. Distant background discovery
```

CPU workers and GPU uploads need explicit scene generation, priority, and
cancellation. Portal work may never starve active gameplay.

### 16.2 Quality tiers

| Tier | Trigger | Target size | Cadence | Candidate content |
|---|---|---:|---:|---|
| `Static` | hidden, unsupported, failed, or over budget | none | none | current frost/rim only |
| `Distant` | visible and under roughly 5% of screen | 0.25× viewport | 10 Hz | radius-1 visual set, short far plane |
| `Near` | 5–25% of screen or approximately under 12 yd | 0.5× viewport | 30 Hz | visual/arrival set and water |
| `Crossing` | over 25% of screen or approximately under 4 yd | 1.0×, capped | every frame | full readiness set |

Thresholds are initial tuning values. Use hysteresis, preserve the previous
texture during tier changes, update at most one expensive portal view per frame,
and keep at most two candidate scenes warm.

### 16.3 Initial budgets at 1080p/60

- portal GPU EWMA ≤ 2.5 ms; crossing soft ceiling 4 ms;
- render-thread portal submission ≤ 1 ms;
- speculative adoption/upload ≤ 0.5 ms per frame;
- no synchronous MPQ parsing or upload wait;
- warm candidate memory target ≤ 256 MB;
- no promotion frame over the project’s 40 ms hitch ceiling;
- no recursive destination views.

When over budget, degrade in this order:

1. update cadence;
2. target resolution;
3. far plane and noncritical doodads/liquid detail;
4. evict secondary candidate;
5. retain static film.

Do not degrade crossing collision/support readiness.

---

## 17. Instrumentation — build before claiming smoothness

### 17.1 Portal lab overlay

Add a DevTools overlay which can draw and report:

- portal GUID/entry/type/display/spell
- source center/right/up/normal and dimensions
- player signed distance and aperture-local coordinates
- previous/proposed/corrected motion segment
- crossing latch/debounce
- client state and server lease time
- descriptor destination and actual teleport destination
- candidate map/generation/tile set
- each readiness gate
- FBO tier/size/cadence/age
- portal CPU/GPU time and memory
- last denial/fallback reason

Render the source rectangle, normal arrow, destination exit plane, and capsule
intersection in distinct colors.

### 17.2 Portal timeline dump

Write `dumps/real-portal-<timestamp>.json` with monotonic timestamps for:

```text
GO discovered
descriptor request/response
CPU first/complete
GPU first/critical complete
VisualReady
collision complete
spawn support pass
first stable FBO frame
READY send/ACK
local crossing
server ENTERING
TRANSFER_PENDING or near-teleport packet
NEW_WORLD
promotion begin/end
standard ACK send
self-create/initial state
movement unpark
source scene retired
```

Also capture active/candidate queue depths, allocation deltas, scene generations,
frame hitches, transfer correlation, and fallback reason.

### 17.3 Visual captures

Provide a one-key paired capture containing:

- source framebuffer with portal visible;
- raw destination FBO;
- aperture mask/composite debug;
- one frame immediately before promotion;
- first active destination frame.

Parallax validation uses two deterministic source camera positions one yard apart.

### 17.4 Network trace

Record ordered portal custom packets, vanilla teleport packets, ACK send thread,
map IDs, movement counter, portal key, and state transitions. The trace must make
duplicate/early ACK impossible to hide.

---

## 18. Test protocol

Run every protocol case with instrumentation enabled, then repeat the core paths
with DevTools disabled.

### T0 — archive and server-data census

Before rendering work:

1. Extract the six stock portal-spell → GO-entry rows from Spell.dbc.
2. Extract their GameObject display IDs/model paths from the installed data/core
   database.
3. Record the type-22 teleport spell and `spell_target_position` for each.
4. Determine the visual model’s forward/up basis empirically.
5. Commit the census to the doc or a checked-in probe artifact.

Pass: no production behavior depends on an unverified model-path or normal-axis
guess.

### T1 — ACK ownership regression

Exercise login, hearth, GM `.tele`, same-map teleport, cross-map teleport,
instance entry/exit, summon, and transfer abort.

Pass:

- socket thread sends zero worldport ACKs;
- exactly one game-thread ACK follows each accepted `NEW_WORLD`;
- ACK occurs after destination scene/map/pose adoption;
- no destination object update is applied to source scene identity.

### T2 — candidate isolation

Stand still in Azeroth and repeatedly create/cancel a destination candidate for a
different map and a far-away same-map city.

Pass:

- active terrain, placements, collision, entities, controller, map identity,
  audio, and minimap never change;
- cancelled generations never publish;
- active streaming latency does not regress beyond budget;
- memory returns to its bounded steady state after retirement.

### T3 — window perspective

At a fixed portal, capture the camera at the center and one yard right.

Pass:

- destination parallax changes correctly;
- verticals remain upright;
- destination forward matches authoritative arrival orientation;
- image is contained by the aperture and source objects occlude it;
- no geometry behind the destination exit leaks through;
- no black/checker/incomplete frame appears.

### T4 — unready membrane

Throttle candidate loading and walk/run/strafe/jump into both center and edges of
the aperture.

Pass:

- no click/use/charge is consumed;
- capsule never crosses locally or on server;
- tangent motion slides rather than sticks;
- other players are unaffected;
- when readiness arrives, the membrane releases without requiring a click.

### T5 — no-core activation spike

For each of the six stock Mage portals, automatically send existing GO use on a
valid local plane crossing while inside stock interaction range.

Pass:

- normal party/charge/lifetime rules still decide success;
- same-map and cross-map teleport packets are observed as expected;
- exactly one use occurs per crossing;
- exiting/re-entering rearms the latch.

This proves interaction only; it is not production sign-off.

### T6 — production verified crossing

With server READY lease enabled, cross center, both edges, just outside each
edge, backward, while standing in the epsilon band, and with deliberately stale
generation/revision/ticket.

Pass:

- only inside-aperture segments moving into the plane from an armed face commit;
- anticheat runs first;
- server never publishes a source-map beyond-plane pose;
- use/charge occurs exactly once;
- invalid movement is corrected to source side;
- stock click behavior remains unchanged.

### T7 — warm cross-map promotion

Approach cold, wait for READY, walk through, and return/repeat warm.

Pass:

- no opaque loading curtain;
- last preview and first active destination frame align within captured tolerance;
- promotion performs no parse/upload/wait/allocation spike;
- ACK is sent after promotion;
- initial destination state arrives afterward;
- movement resumes only after authoritative destination state.

### T8 — warm same-map promotion

Use city pairs on the same continent map.

Pass: candidate residency is promoted before near-teleport ACK, with the same
visual and movement invariants as T7.

### T9 — denial and lifetime races

Test non-group user, group change after READY, portal last charge, expiry during
load, despawn at the plane, target metadata reload, loss of phase/visibility, and
transfer already pending.

Pass: server revalidation decides every result; no stale READY grants travel; no
source session is stranded; no shared GO readiness flag changes.

### T10 — mismatch and fallback

Force descriptor/actual map mismatch, evict the candidate after ENTERING, inject
a candidate load failure, and trigger an unrelated teleport while a portal is
warm.

Pass: wrong candidate is never promoted; generic curtain finishes the correct
authoritative transfer; source authority is not resurrected after ACK.

### T11 — performance and pressure

Observe one near portal, two visible portals, rapid approach/retreat, repeated
summon/despawn, low memory budget, delayed uploads, and 100–250 ms artificial
network latency.

Pass:

- budgets in §16 hold or quality degrades in the prescribed order;
- active streaming remains prioritized;
- no unbounded candidate/cache growth;
- no frame waits on cancelled speculative work;
- network delay extends only the already-covered crossing veil.

### T12 — compatibility

Connect an ordinary 1.12 client and an MSUI client with REAL_PORTALS disabled.

Pass: no custom packets are sent to stock sessions, portals remain clickable,
and normal core behavior is byte/behavior compatible.

---

## 19. Build order

Each milestone has its own proof and must leave generic travel working.

### M0 — evidence and protocol authority

- Run T0 archive/database census.
- Read the live custom core and `SUI_WIRE_PROTOCOL.md`.
- Reserve/confirm opcodes and feature negotiation.
- Add portal timeline/geometry diagnostics before optimization claims.

Exit: visual basis, six stock identities, protocol range, and actual custom-core
hooks are recorded rather than assumed.

### M1 — ACK ownership

- Remove socket-thread worldport ACK.
- Preserve ordered `NEW_WORLD` boundary.
- Move generic worldport ACK to the safe game-thread transition after generic
  destination readiness.
- Keep login and near teleport semantics distinct.

Exit: T1 passes before any dual-scene work.

### M2 — stock activation spike

- Add GUID-owned portal visual instances.
- Add configurable tall/wide procedural aperture and debug rectangle.
- Add local signed-plane/capsule crossing and latch.
- Automatically issue existing `CMSG_GAMEOBJ_USE` inside stock range.

Exit: T5 passes for all six portals. Existing loading behavior is acceptable in
this spike.

### M3 — single-scene ownership refactor

- Introduce `SharedWorldAssets`, `WorldScene`, `ActiveWorld`, and
  `WorldSceneHost`.
- Run all existing gameplay with exactly one scene.
- Move active loader inputs out of `_config.Start` globals into explicit scene/job
  state.
- Replace synchronous speculative teardown waits with generation cancellation
  and deferred disposal.

Exit: no visual/gameplay regression and T1 still passes.

### M4 — isolated candidate loading

- Add reentrant `WorldLoadJob` profiles.
- Load/cancel a second static destination without touching active state.
- Add active/candidate scheduler priorities and memory reservation.
- Validate collision and landing support.

Exit: T2 passes.

### M5 — true environmental window

- Add `PortalRenderTarget` and GL state guard.
- Add portal camera transform and authored-camera correction.
- Add clip plane to terrain/WMO/doodad/water.
- Composite through the procedural aperture and cross-fade from film.
- Add quality tiers and portal GPU timing.

Exit: T3 and §21 visual criteria pass.

### M6 — scene promotion

- Correlate the stock-use spike’s vanilla transfer with the candidate.
- Promote on both `NEW_WORLD` and same-map teleport paths before standard ACK.
- Add crossing veil, movement parking, initial-state unpark, and deferred source
  retirement.
- Keep generic fallback for every mismatch/failure.

Exit: T7, T8, T10, and T11 pass with stock `CMSG_GAMEOBJ_USE` activation.

### M7 — production per-player protocol and core crossing

- Add descriptor, READY lease, state/revoke packets.
- Add server portal template/generation/spatial registry.
- Factor shared type-22 eligibility.
- Add verified swept crossing before source relocation.
- Make membrane usability follow server READY confirmation.
- Retain stock click fallback.

Exit: T4, T6, T9, and T12 pass. This is the REAL_PORTALS version-1 shipping
boundary.

### M8 — geometric arrival continuity

- Add server-authored destination exit frame.
- Preserve lateral offset and facing delta with collision/support fallback.
- Tune camera/body continuity against promotion captures.

Exit: three distinct lateral crossings remain distinct and safe at destination;
fixed stock landing remains the fallback.

### M9 — optional live remote population

- Add ticket-scoped destination snapshot/delta packets at approximately 5–10 Hz.
- Use isolated proxy IDs/store and non-interactable renderers.
- Respect phase, stealth, GM invisibility, privacy, and visibility distance.
- Reconcile proxies with ordinary destination entities after promotion.

Exit: remote actors appear without becoming targetable or leaking hidden state,
and disappear/reconcile without a visible population pop.

---

## 20. Definition of done

REAL_PORTALS version 1 is done only when all of these are true:

### Authority

- The server owns destination, aperture, expiry, generation, and crossing verdict.
- Movement/anticheat validation precedes crossing.
- READY never consumes a use and never replaces crossing-time eligibility.
- Stock click behavior and stock clients remain supported.
- No destination coordinate supplied by the client affects teleport authority.

### Scene correctness

- Candidate work never mutates active map, geometry, collision, entities, or
  controller.
- Every async publication matches scene generation/cancellation.
- Promotion is a bounded ownership swap with no load/upload/wait.
- A mismatched vanilla teleport never promotes a candidate.
- Source scene remains valid until successful promotion and fence retirement.

### Network correctness

- Socket thread sends zero worldport ACKs.
- Exactly one ACK is sent per accepted `NEW_WORLD`, after scene/map/pose adoption.
- Same-map promotion occurs before the near-teleport ACK.
- Destination updates are applied only against destination active identity.
- Every unexpected teleport still completes through generic loading.

### Interaction

- An unready player cannot cross, but can slide along the membrane.
- A ready player walks through without clicking.
- At most one use is emitted per directional crossing/generation.
- Another player’s readiness never changes this player’s or the shared GO state.
- Server denial cleanly returns/corrects the player to the source side.

### Visual

- A one-yard source-camera translation creates correct destination parallax.
- The destination orientation and up vector are correct.
- The window has no black/checker/incomplete frame.
- Destination rear-plane geometry does not leak.
- Source occlusion and foreground water remain correct.
- Last preview and first active frame show no objectionable camera/map pop.
- Rim, film, glow, and painterly style remain coherent across readiness.

### Performance

- One near portal meets §16 budgets on the target machine.
- Two visible portals remain stable through cached/alternating updates.
- Promotion stays under the project’s 40 ms hitch ceiling.
- Candidate memory and retired-scene memory are bounded.
- Active streaming wins every scheduling conflict.

Live destination actors are explicitly not required for version-1 done.

---

## 21. Visual acceptance checklist

- [ ] Portal reads as a doorway-sized opening, not an enlarged circular button.
- [ ] Width and height tune independently; apparent depth remains negligible.
- [ ] Sideways camera movement produces perspective parallax.
- [ ] Destination verticals stay vertical and forward matches arrival orientation.
- [ ] Source arch/walls/players naturally occlude the window.
- [ ] Preview remains completely inside the rounded aperture.
- [ ] Frost hides every incomplete or stale render target.
- [ ] Readiness cross-fade does not reset or pop the particle swirl.
- [ ] Clip plane removes geometry behind the virtual destination exit.
- [ ] Source water behind the portal does not wash over the preview.
- [ ] Source water physically in front still blends correctly.
- [ ] Quality-tier changes do not flash or visibly reallocate.
- [ ] Portal is full-rate before physical contact.
- [ ] Crossing veil feels like passing through the surface, not a disguised loading screen.
- [ ] First active destination frame is continuous with the last preview.

---

## 22. Planned client touchpoints

Likely new files:

```text
MSUIClient/World/Scenes/WorldScene.cs
MSUIClient/World/Scenes/WorldSceneHost.cs
MSUIClient/World/Scenes/WorldLoadJob.cs
MSUIClient/World/Scenes/SharedWorldAssets.cs
MSUIClient/World/Portals/PortalDescriptor.cs
MSUIClient/World/Portals/PortalTransitCoordinator.cs
MSUIClient/World/Portals/PortalVisualRegistry.cs
MSUIClient/World/Portals/PortalRenderTarget.cs
MSUIClient/World/Portals/PortalViewManager.cs
MSUIClient/Net/PortalWire.cs
MSUIClient/Program.RealPortals.cs
MSUIClient/Shaders/portal_composite.vert
MSUIClient/Shaders/portal_composite.frag
```

Expected modifications:

| File/system | Change |
|---|---|
| `Program.GameObjects.cs` | expose type-22 spell metadata; maintain GUID portal discovery/lifetime |
| `Program.cs` | movement-filter seam; destination pass; active scene access |
| `Program.Loading.cs` | extract explicit reentrant `WorldLoadJob` |
| `Program.Net.cs` | transition correlation, promotion, same-map/far ACK ownership |
| `Net/NetworkClient.cs` | remove socket-thread worldport ACK |
| `Net/WorldSession.cs` | portal packets and explicit game-thread ACK use |
| `Net/Opcodes.cs` | audited SUI portal opcode symbols |
| `Engine/Camera.cs` | authored forward/up correctness and portal-camera support |
| `TerrainRenderer` | scene ownership/shared assets; optional portal clip plane |
| `WmoRenderer` | split asset cache from placements/PVS state; clip plane |
| `DoodadRenderer` | split asset cache from placements; dynamic emitter lane; clip plane |
| `LiquidRenderer` | scene-owned tiles/shared textures; clip plane |
| `ParticleRenderer` | explicit dynamic portal instances and live-view composite integration |
| shaders | portal clip uniforms and procedural aperture composite |

The live core will require corresponding protocol, portal manager/template,
eligibility factoring, movement crossing, and cleanup changes. The checked-in
`.reference-vmangos-core` is evidence/reference only unless explicitly promoted
to the actual server worktree.

---

## 23. Fallback ladder

Each partial state remains useful and safe:

1. **Ordinary stock portal** — click, normal loading screen.
2. **Tall dynamic portal** — improved visual, click or automatic stock use,
   normal loading screen.
3. **Prepared static film** — per-player preload but no live window; warm promotion.
4. **Environmental window** — correct destination view, stock GO use commits.
5. **REAL_PORTALS v1** — server READY lease and verified walk-through crossing.
6. **Geometric continuity** — lateral/facing preservation.
7. **Populated window** — isolated remote actor proxy stream.

At every rung, failure moves downward to a known-safe behavior rather than
blocking travel.

---

## 24. Reconciliation with existing systems

### `SYSTEM_INSTANCE_PORTALS.md`

Remains authority for the existing `InstancePortal.m2` particle/film/glow look.
REAL_PORTALS reuses that presentation law but replaces rounded-position static
surface discovery with explicit dynamic portal instances and a destination
texture. Once built and validated, extract a dedicated `SYSTEM_REAL_PORTALS.md`;
do not overload the instance-portal system doc.

### `SYSTEM_INSTANCES.md` / `Program.Instances.cs`

Static area-trigger dungeon travel remains separate. It benefits from the
scene/ACK refactor and may later preload an instance entrance, but it does not
become the Mage portal crossing implementation.

### `SYSTEM_LOAD.md` / `PLAN_17_COLD_START.md`

The existing bounded phases become `WorldLoadJob`; active first load and generic
travel retain their gates. REAL_PORTALS adds explicit profiles, cancellation,
generation stamping, and promotion readiness. It does not relax spawn-support
or collision correctness.

### `SYSTEM_STREAMING.md`

Active streaming retains priority. Portal timing/memory/queue fields join the
existing hitch evidence rather than inventing an unmeasured scheduler.

### `SYSTEM_NETWORKING.md`

Worldport ACK moves to the game-thread ownership transition. Custom portal
packets follow the established SUI capability rule and never replace vanilla
teleport packets.

### `SYSTEM_WMO_PORTALS.md`

No relationship beyond shared word choice. Destination WMO rendering uses its
own scene/PVS state and may use normal WMO portal culling inside the preview.

---

## 25. Open evidence tasks, not open architecture

The architecture above is decided. These facts must still be measured before
their corresponding implementation step:

1. The live custom core’s actual free opcode range and feature negotiation.
2. The six stock Mage portal display IDs/model paths and exact visual forward
   basis in Nico’s mounted data/core DB.
3. The source-frame yaw offset which aligns procedural `through` with each
   visual model.
4. Final portal width/height and readiness ripple tuning.
5. Actual target-machine scene memory and portal render cost, which may revise
   §16 thresholds but not the degradation order.
6. Whether an aperture depth seal is necessary after captured water ordering.
7. Whether live remote population is worth its visibility/privacy complexity
   after the environmental version is played.

None of these justify changing the core decisions: dual scenes, shared assets,
per-player readiness, verified server movement crossing, stock teleport commit,
and promotion-before-ACK.
