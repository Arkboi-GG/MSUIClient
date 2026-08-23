# System — Gameobject Rendering (server-spawned models)

**How a server gameobject — a Stormwind shop sign, a mailbox, a chest, an ore
vein — gets a model on screen.** One of the per-system docs the handbook indexes
(see PROJECT_HANDBOOK.md §1.2). Read SYSTEM_STREAMING.md for the doodad
renderer this system rides on, and SYSTEM_NETWORKING.md for the
SMSG_UPDATE_OBJECT flow that delivers the entities.

Version: Draft 7 — 2026-08-23. GAMEOBJECT_STATE now owns per-GUID held poses,
one-window door/lid transitions, open-lock and loot-release chest prediction,
and immediate state-gated door/button collision. Type-15 MO_TRANSPORT vessels consume their
TaxiPathNode timetable and exact client-period clock, including path heading,
dock dwell, cross-map hiding, observed rider composition, dynamic WMO hulls,
and live set-0 MODD props. Type-11 TRANSPORT elevators consume their server
path-clock anchor and authored TransportAnimation.dbc keyframes; dynamic M2
render, pick/cull, and owner-aware collision hulls move in place. Draft 2 added mouse-over;
Draft 1 made shop signs and mailboxes visible.

Owner files: `GameObjectDisplayTable` in `Formats/DbcReader.cs`,
`GAMEOBJECT_ROTATION` + `GameObjectRotation` in `Net/ObjectFields.cs`,
`WorldEntity.GameObjectFacing` in `Net/Entities.cs`, the
`AddDynamic`/`RemoveDynamic`/`HasDynamic`/`TryPickDynamic`/
`TryRaycastDynamicCollision` API and
`HighlightedDynamicKey` in `World/Doodads/DoodadRenderer.cs`, the per-frame
sync + `PickGameObject` in `GameLoop/Scene/GameLoop.GameObjectRender.cs` (called from
`Program.cs` Update, next to `QueueVisibleDoodadDemand`), the hover/click
integration in `GameLoop/Combat/GameLoop.Targeting.cs`, and the tooltip adapter in
`GameLoop/Hud/GameLoop.GameTooltip.WorldGameObject.cs`.
State animation policy is owned by `Net/GameObjectAnimationLaw.cs`; open-lock
and loot-release writers live in `GameLoop/Combat/GameLoop.Casting.cs` and
`GameLoop/Panels/GameLoop.Loot.cs`, and live door collision joins static world
collision in `Player/CharacterController.cs`.
Transport motion is owned by `Formats/TransportAnimationCatalog.cs`,
`Formats/TaxiTransportCatalog.cs`, `Net/TransportRiderLaw.cs`, and
`GameLoop/Scene/GameLoop.Transports.cs`.

---

## 0. The bar

Stormwind's shop signs (`WORLD\GENERIC\HUMAN\PASSIVE DOODADS\SIGNS\*.MDL`) and
every PostBox mailbox appear in Stormwind.wmo's MODN **name table** but have
**zero MODD placements** — in vanilla they are server gameobjects, spawned by
the world server and streamed as entities. The client had the network half
(GAMEOBJECT_DISPLAYID decoded, mailbox proximity, minimap blips) and no visual
half: `CreatureRenderer` filters `IsUnit`, so a gameobject was invisible by
construction.

**The invariant: every gameobject entity with a resolvable display model is
drawn where the server says it stands, and disappears when the server despawns
it.**

## 1. What is implemented

- **displayId → model**: `GameObjectDisplayInfo.dbc`
  (`GameObjectDisplayTable`, lazy-loaded from the MPQs on first use like
  `LockCatalog`). Verified against build 5875: 1,638 rows, 12 fields, 48-byte
  records; field 0 = id, field 1 = model path string. The `.mdl`/`.mdx` spelling
  is swapped to `.m2` by `DoodadRenderer.PathCandidates`, same as every MDDF
  placement.
- **Placement**: one `DoodadRenderer.AddDynamic(guid, path, transform)` per
  gameobject entity. Dynamic instances live in the same `_byModel` lists as
  static doodads, so they get the same VAO batching, frustum/distance cull,
  draw, appear-fade, and — critically — participate in BOTH the
  opaque pass and the deferred blended pass (M2 blend modes 2–6), instanced or
  not, with no extra draw path.
- **Lifecycle**: `GameLoop/Scene/GameLoop.GameObjectRender.cs` reconciles every frame (the
  entity store has no spawn hooks; `CreatureRenderer` walks it per frame too).
  Signature per GUID = (displayId, position, yaw, scale). A transform-only
  change updates the existing dynamic instance and its pick/cull bounds in
  place; a display change re-adds it. This distinction is load-bearing for
  transports because a per-frame remove/add would discard active M2 animation.
  Despawn/out-of-range (entity gone from the store) removes it. A
  `ResetPlacements()` on a tile crossing wipes dynamic placements with
  everything else; the sync notices `HasDynamic` went false and re-adds.
- **Streaming**: `AddDynamic` returns `Pending` while the M2 streams (it queues
  the preload itself, `"gameobject"` queue) — the sync retries next frame.
  `Unavailable` (missing/unrenderable M2) and unknown display ids are logged
  once per displayId and blacklisted for the session.
- **Transform**: `Scale × RotY(yaw + 90°) × Basis × Translate(position)` — the
  exact `CreatureRenderer` unit convention; the basis equals the linear part of
  `DoodadRenderer.PlacementToWorld`. Scale from `OBJECT_FIELD_SCALE_X`
  (default 1). Skipped when displayId is 0 or the display row is absent.

## 1.1 GAMEOBJECT_STATE animation and collision (2026-08-23)

The reference keeps one client-side state per animated GameObject. MSUI mirrors
the full family-A type census (0, 1, 2, 3, 6, 8, 9, 10, 12, 16–19, 23, 24,
26–30), while models without skeletal sequences remain ordinary static meshes.
An omitted field 14 is the wire zero, `ACTIVE(0)`/open—not unknown and not
`READY(1)`/closed.

First sight snaps silently to the held state pose: 149 Opened, 147 Closed, or
151 Destroyed. A genuine edge plays one authored transition window—148 Open,
146 Close, 150 Destroy, or 152 Rebuild—then explicitly hands back to the
destination rest pose even when the M2 sequence's loop bit is set. Each dynamic
GUID owns this playback state, so two copies of one crate model can hold
different poses; while any owner-local animation exists, the renderer uses its
per-instance CPU-skin draw path instead of sharing one instanced pose. The
reference missing-sequence remap is retained, including frozen frame-zero Open
or Close clips standing in for absent Closed/Opened poses.

Three writers feed the stored state without fighting each other: a genuine
wire field change, an open-lock `SMSG_SPELL_GO` GameObject target (ACTIVE), and
loot release (READY). `LastWire` is separate from the client state, so an
unrelated VALUES delta cannot re-close a chest whose constant wire state never
changed. Custom0–3 and Despawn remain the disjoint one-shot channel; after a
custom window, rendering naturally returns to the retained state pose. Those
one-shots are gated by the same family-A census as the reference. GameObject
event sounds read the exact currently armed sequence and its owner-local clock,
so `$GO0..5`/`$GC0..3` markers cross during a transition or Custom window
instead of scanning the model's unrelated Stand timeline.

Door and button hulls are never baked into the immutable collision BVH. The
controller merges static collision with an owner-local M2 raycast that accepts
those two types only when their wire state is READY. An open or destroyed door
is therefore passable immediately, while a closed door remains solid; chest
collision deliberately does not follow its lid state.

## 1.25 Type-11 elevator/lift motion (2026-08-23)

A type-11 `TRANSPORT` does not receive a streamed position every frame. Its
create movement block supplies one `UPDATE_FLAG_TRANSPORT` path-progress clock;
the client advances that clock locally and evaluates the path.

`TransportAnimationCatalog` reads the seven-field table keyed by the
GameObject's template entry. A valid path has at least two time-sorted frames,
starts at zero, and uses the final frame's cumulative milliseconds as its cycle
period. Each update computes `(serverProgress + elapsed) % period`, brackets the
two frames, linearly interpolates their spawn-local offset, rotates that offset
by `GAMEOBJECT_ROTATION` (movement yaw fallback), and adds the stationary spawn
position. The resulting `WorldEntity.Position` is written before dynamic-doodad
reconciliation, so rendering and mouse picking see the same this-frame car.

A fresh create replaces `WorldEntity`, which is the explicit signal to capture
a new spawn frame and anchor. A movement-only clock re-anchor on the same object
changes only the clock; treating its already-animated world position as a new
spawn would make the elevator walk away by one accumulated path offset.

The clinical fixture pins dwell, interpolation, wrap and quaternion rotation;
the real-data assertions pin Thunder Bluff entries 4170/4171 (30.033/30.000 s,
61.244-yard travel) and Undercity 152614. This slice moves the visible/pickable
  car. Armed lift cars mark their retained M2 placement as live collision, so
  the static BVH omits only those moving instances. The controller raycasts the
  current owner-keyed collision hull, attaches to the lift GUID, and reuses the
  same local-frame carry and outbound `ON_TRANSPORT` path as WMO vessels.

## 1.35 Type-15 boat/zeppelin motion and observed riders (2026-08-23)

A type-15 `MO_TRANSPORT` template supplies `data[0..2]` as TaxiPathNode path id,
move speed, and acceleration. `TaxiPathNodeCatalog` loads the real nine-field
build-5875 table and groups rows by path. `MoTransportTimetable` mirrors the
vanilla/vmangos map-change and teleport skip dance, splits the surviving frames
into spline legs, samples Catmull-Rom arc length with 20 chords, derives the
trapezoidal acceleration/deceleration windows, and self-pins the cycle to the
exact build-5875 client period. The real-data gate covers all nine live vessel
paths, including the 1,208,014 ms Naxxramas transport.

Each frame samples `(serverProgress + localElapsed) % exactPeriod`, writes the
vessel position and a dynamic path-heading override before GameObject
reconciliation, and reports dock/depart state from the timetable window. If
the sample belongs to a different map, reconciliation removes any retained
local placement so a cross-continent vessel cannot remain frozen at its last
dock. When the timetable returns to the current map, ordinary reconciliation
publishes it again.

Observed passengers already retain the wire's transport-local position and
yaw. `TransportRiderLaw` composes those values through the armed lift/vessel
matrix each frame. Both WMO vessels and M2 lift cars expose owner-aware walking
support from their live hull transform. The controlled mover attaches from that support,
is rigidly carried before input (position, facing, body, and camera), retains the
platform frame through a jump, and streams `MOVEFLAG_ONTRANSPORT` with the exact
boat-local pose. World ground, swimming, flight, free view, or a vanished vessel
detaches it.

Stock vessel displays end in `.wmo`, so they use an owner-keyed dynamic lane in
`WmoRenderer` rather than the M2-only placement lane. The hull transform is
updated in place each frame, including its draw, cull, liquid, and pick bounds.
Its set-0 MODD sails, rotors, and furniture are enumerated from the live WMO
instance and published as stable `(host GameObject GUID, prop index)` dynamic
M2 instances. Those props retain independent animation/particle identities,
follow the hull transform without remove/add churn, and are excluded from
static WMO residency enumeration. Off-map legs, display swaps, despawns, and
residency resets remove or rebuild the hull and all owned props together.

The real build-5875 fixture pins display 3015 to `transportship.wmo`, its one
doodad set, and all 134 set-0 props. Moving hull collision is never baked into
the immutable terrain/WMO BVH: the controller raycasts the live transform and
retains the host GUID, preventing a ghost deck at an old timetable pose.

At a continent seam, `SMSG_TRANSFER_PENDING`'s transport entry survives until
`SMSG_NEW_WORLD`. The matching ridden vessel is spared across the entity purge,
re-armed against the destination map, and the packet's boat-local position and
orientation are recomposed before world residency and the ACK.

## 1.5 Mouse-over: pick, highlight, tooltip, click (2026-08-12, second pass)

Vanilla brightens a world object under the cursor and shows its name in the
GameTooltip; right-click uses it. All four now exist, each reusing the unit
idiom it mirrors:

- **Picking** — `DoodadRenderer.TryPickDynamic(origin, direction, maxDistance,
  out guid, out distance)`: nearest ray-vs-AABB over the dynamic placements'
  per-instance world cull bounds (the same bounds the frustum cull reads).
  Deliberately loose the way unit picking's vertical cylinders are; static
  doodads are never tested. `PickGameObject` in `GameLoop/Scene/GameLoop.GameObjectRender.cs`
  wraps it with the `PickUnit` conventions: same camera ray, same 200-yd
  `TargetPickDistance`, same "static world collision strictly nearer blocks the
  hit" rule (a GO's own hull can never block its own pick — the ray enters the
  AABB first). **Units win ties**: `PickUnit` gained an out-distance overload,
  and the GO pick's max distance is the unit hit — a GO hovers only when
  STRICTLY nearer, so the two hovers (`_hoveredGuid` /
  `_hoveredGameObjectGuid`, `GameLoop/Combat/GameLoop.Targeting.cs`) are exclusive by
  construction. A nameplate rect hit reports distance 0 and beats everything.
- **Highlight** — `DoodadRenderer.HighlightedDynamicKey` (set per frame by
  `UpdateTargeting`, exactly like `CreatureRenderer.HoveredGuid`). The matching
  instance draws with the same additive 64/255 brighten the creature/player
  shaders use, applied after the unlit clamp so glow batches brighten too. The
  boost rides the per-instance VBO as a 22nd float (attribute 9, `aHighlight`),
  so the instanced path, the non-instanced path (`uHighlight` uniform), and
  BOTH deferred blended flavours inherit it with no separate highlight draw.
  Disabled-attribute default 0 keeps every static doodad unaffected.
- **Name tooltip** — `GameLoop/Hud/GameLoop.GameTooltip.WorldGameObject.cs` is the
  world-unit adapter's shape applied to the GO hover: it feeds the
  already-present `TryShowWorldGameObjectGameTooltip` responder (which was
  waiting on exactly this picker verdict), publishes the gold name line at the
  frozen default bottom-right anchor, and fades over the same half-second on
  leave. The name comes from the `_gameObjectTemplates` cache
  (`CMSG_GAMEOBJECT_QUERY` via `RequireGameObjectTemplate`, one query per
  entry); while the query is in flight NO tooltip shows — vanilla's cache-miss
  behaviour. It runs right after the unit adapter in `DrawCombatHud` so an
  owner handoff between the two always lands on the newest hover.
- **Right-click use** — a right world-click whose GO pick is strictly nearer
  than any unit hit routes to the existing `UseGameObject` entry point
  (`GameLoop/Scene/GameLoop.GameObjects.cs`), which already gates range (6 yd), type routing
  (mailbox → mail panel, chest → `CMSG_GAMEOBJ_USE` / open-lock spell) and
  telemetry. Left-click on a GO deliberately stays "empty world" — vanilla
  does not select gameobjects.
- **Cursor icons** — NOT implemented: no per-interactable hardware/software
  cursor mechanism exists in the client yet (only capture modes and ImGui drag
  payloads), and this pass does not build one. When one exists, the hover
  verdict plus `GAMEOBJECT_TYPE_ID` is everything it needs.

Out of scope for hover, deliberately: GO dyn-flag gating (sparkle/activate
states), lock/skill requirement lines in the tooltip (the law's
`GameTooltipGameObjectLine` channel is ready for them), and any hover on
static scenery doodads.

## 2. The field layout used (build 5875, verified against vmangos UpdateFields_1_12_1.h)

| Field | Index | Notes |
|---|---|---|
| `OBJECT_FIELD_SCALE_X` | 4 | f32; already surfaced as `ObjectFields.Scale` |
| `GAMEOBJECT_DISPLAYID` | 8 | already decoded pre-existing |
| `GAMEOBJECT_ROTATION` | 10–13 | f32 ×4 quaternion — NEW this pass |
| `GAMEOBJECT_STATE` | 14 | 0 ACTIVE/open, 1 READY/closed, 2 ALT/destroyed; absent = 0 |
| `GAMEOBJECT_TYPE_ID` | 21 | already decoded pre-existing |

Facing: vmangos writes the rotation quaternion as `z = sin(yaw/2)`,
`w = cos(yaw/2)`, `x = y = 0` for static spawns. `WorldEntity.GameObjectFacing`
prefers `2·atan2(z, w)` when the quaternion is non-zero, else falls back to the
movement block's `Orientation` (position always comes from the movement block,
which vmangos sends for every gameobject create).

## 3. What is NOT implemented (deliberately, this pass)

- **Partial spawn progress / reversal**: `GAMEOBJECT_ANIMPROGRESS` does not yet
  seek a streamed-in transition, and reversing a door midway through a swing
  starts the opposite clip rather than blending from the current intermediate pose.
- **Model-less traps**: invisible traps with empty display paths remain
  correctly skipped; no visual can be synthesized for an absent model.
- **Interior light**: dynamic GOs always use the exterior default light
  (`Light.W = 1`), so a mailbox inside a dark inn is lit like the outdoors —
  same limitation creatures currently have.
- **Collision outside transports and doors/buttons**: ordinary dynamic M2
  GameObjects retain the existing static-snapshot behavior. Armed lift cars and
  stateful doors/buttons are the live owner-aware exceptions.
