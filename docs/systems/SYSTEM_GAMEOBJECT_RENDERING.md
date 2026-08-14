# System — Gameobject Rendering (server-spawned models)

**How a server gameobject — a Stormwind shop sign, a mailbox, a chest, an ore
vein — gets a model on screen.** One of the per-system docs the handbook indexes
(see PROJECT_HANDBOOK.md §1.2). Read SYSTEM_STREAMING.md for the doodad
renderer this system rides on, and SYSTEM_NETWORKING.md for the
SMSG_UPDATE_OBJECT flow that delivers the entities.

Version: Draft 2 — 2026-08-12. Draft 1 made shop signs and mailboxes visible
at all; the same day's second pass added mouse-over: pick, highlight tint,
name tooltip, right-click use.

Owner files: `GameObjectDisplayTable` in `Formats/DbcReader.cs`,
`GAMEOBJECT_ROTATION` + `GameObjectRotation` in `Net/ObjectFields.cs`,
`WorldEntity.GameObjectFacing` in `Net/Entities.cs`, the
`AddDynamic`/`RemoveDynamic`/`HasDynamic`/`TryPickDynamic` API and
`HighlightedDynamicKey` in `World/Doodads/DoodadRenderer.cs`, the per-frame
sync + `PickGameObject` in `GameLoop/Scene/GameLoop.GameObjectRender.cs` (called from
`Program.cs` Update, next to `QueueVisibleDoodadDemand`), the hover/click
integration in `GameLoop/Combat/GameLoop.Targeting.cs`, and the tooltip adapter in
`GameLoop/Hud/GameLoop.GameTooltip.WorldGameObject.cs`.

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
  collision-free draw, appear-fade, and — critically — participate in BOTH the
  opaque pass and the deferred blended pass (M2 blend modes 2–6), instanced or
  not, with no extra draw path.
- **Lifecycle**: `GameLoop/Scene/GameLoop.GameObjectRender.cs` reconciles every frame (the
  entity store has no spawn hooks; `CreatureRenderer` walks it per frame too).
  Signature per GUID = (displayId, position, yaw, scale); any change re-adds
  the placement, so a `GAMEOBJECT_DISPLAYID` values-delta updates the model.
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
| `GAMEOBJECT_TYPE_ID` | 21 | already decoded pre-existing |

Facing: vmangos writes the rotation quaternion as `z = sin(yaw/2)`,
`w = cos(yaw/2)`, `x = y = 0` for static spawns. `WorldEntity.GameObjectFacing`
prefers `2·atan2(z, w)` when the quaternion is non-zero, else falls back to the
movement block's `Orientation` (position always comes from the movement block,
which vmangos sends for every gameobject create).

## 3. What is NOT implemented (deliberately, this pass)

- **Doors / GO state visuals**: `GAMEOBJECT_STATE` (field 14) is ignored — a
  door renders in its authored pose whether open or closed.
- **Animation**: no M2 animation on gameobjects (flags waving, fishing bobber
  bobbing); models render in bind pose like static doodads.
- **Traps**: invisible traps have display rows with empty model paths and are
  correctly skipped, but no armed/triggered visuals exist.
- **Transports**: boats/zeppelins are gameobjects with continuous movement;
  static placement would draw them frozen at the spawn point. They stream like
  any other GO today and will simply sit still.
- **Interior light**: dynamic GOs always use the exterior default light
  (`Light.W = 1`), so a mailbox inside a dark inn is lit like the outdoors —
  same limitation creatures currently have.
- **Collision**: dynamic instances are visible to the doodad collision
  snapshot only when a rebuild happens to run while they are placed; no
  per-spawn collision update.
