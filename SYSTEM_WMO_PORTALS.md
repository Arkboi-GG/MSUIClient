# SYSTEM_WMO_PORTALS — portal-traversal interior/exterior visibility

Written 2026-07-27. This is the "what shipped" doc for **PLAN_10** (portal visibility),
extracted per the one-system-one-doc rule now that traversal is built, on by default,
and verified in-game. It ports benilla's `wmo_portal/` PVS. Read PLAN_10 for the *why*
and the design decisions (D1–D7); this doc is the *what* and the ground truth.

**Status: shipped, default ON.** `WmoPortalCulling` is `true` (GameSettings v2 +
migration). The dev **Portals** panel keeps the A/B toggle. Three visible wins:

1. **Stormwind's exterior roof is hidden from inside** — the trade-district canopy you
   see on approach is gone once you're in the streets. This was the headline ask.
2. **The cathedral silhouette holds across the whole approach + the gate**, and drops
   the instant you reach the interior streets — instead of popping out at a yard mark.
3. **The skyline is never lost** — towers, outer walls and approach silhouettes draw
   exactly as before; only geometry you genuinely cannot see is culled.

The frame-pacing you may see at the gate (`present ~28 ms`, thread `<1 M/ms`) is the
pre-existing swap/pacing bug (SYSTEM_STREAMING §5A), **not** this system: WMO render is
~0.7 ms of the frame with culling on.

---

## Files

- `World/Wmo/WmoRenderer.cs` — the whole system:
  - `ComputeReachableGroups(...)` — the flood (BFS through portals, frustum-clipped).
  - `PortalScreenRect(...)` — projects a doorway to an NDC rect (Sutherland–Hodgman).
  - `ClassifyGroup(...)` — the single visibility decision; consults the reachable set.
  - `UpdateCameraCell(...)` — PLAN_10 D1, the camera's containing cell (`CameraGroup`).
  - `FrameCullContext` — carries `ReachableGroups` + `CameraInCell` into ClassifyGroup.
  - `GroupWorldTriangles(...)` — geometry for the dev highlight overlay.
  - `UsePortalCulling` (default `true`), `PortalReachedLastFrame` (diagnostic).
- `Program.Portals.cs` — the dev **Portals** panel: the toggle, the "highlight picked
  group" overlay, the reached-count and camera-cell readouts, portal-polygon draw.
- `Engine/GameSettings.cs` — `Detail.WmoPortalCulling` (default `true`) + the v1→v2
  migration that flips it on for pre-existing `settings.json`.
- `Formats/WmoReader.cs` — MOPV/MOPT/MOPR + per-group `PortalStart/PortalCount` (parsed
  since the reader was written; this is the first consumer).

Nothing here touches lighting, particles, or the teleport ("instance") portals.

---

## The rule, stated once

> A group is drawn if the flood reaches it from the camera's cell through a chain of
> on-screen doorways. Pure-exterior (`0x08`) shell groups are exempt from being
> *culled* — they are force-seeded (outdoors) or restored by the deferred-exterior
> pass (indoors, when a doorway to the sky is in view). Distance-LOD shells are on
> their own swap path and are never touched.

This is benilla's `visible[]` (`wmo_portal/mod.rs`), which gates **every** group; the
only exemptions are `0x08` (via seeding/deferral) and portal-less models (drawn whole).

---

## Ground truth — the group flags that drive everything

The MOGP group flags decide a group's role. Verified in-game with the picker:

| flag | name | role in this system |
|---|---|---|
| `0x0008` | EXTERIOR | The outer shell: towers, outer walls, the **entrance keep `thief01`**. Seeds the flood from outdoors; restored by the deferred pass indoors; **never reachability-culled once reached**. This is the skyline guard (D5). |
| `0x0040` | EXTERIOR_LIT | The city **streets/districts** — "indoors for visibility, sunlit for lighting". Most of a city WMO. Gated by the flood like an interior. Stormwind: ~115 of 306 groups. |
| `0x2000` | INTERIOR | True interior rooms. Gated by the flood. |
| `0x10000`(+low verts / name / override) | ALWAYS_DRAW shell | The distance impostors (the floating **cathedral silhouette `holy01`**, district facades). Classified `IsDistanceLod`; handled by the §3.34 shell swap, **excluded from portal logic entirely** (D5). |

**The `thief01` lesson.** The Stormwind entrance keep is `0x08` EXTERIOR with a
**10.3 million yd³** bounding box that swallows the whole gate. So "am I standing in a
cell?" is *true* the moment you step under the arch — which is not "inside the city".
The fix is general, not hand-tuned: **being in an exterior (`0x08`) cell does not count
as inside.** Only a `0x40`/`0x2000` cell does. See `CameraInCell` below.

---

## The algorithm

### 1. Seed (`Render`, per instance)

- **Camera in a cell of THIS building** (`CameraGroup.InstancePath == instance.Path`):
  seed the flood from that one group, full-screen. (Includes standing in `thief01`.)
- **Camera outside** (or in a different building): **outdoor seed** — push *every*
  `0x08` EXTERIOR group of this building, each full-screen, and flood through their
  doorways into the interiors. This is what culls the roof from the gate approach while
  keeping the outer shell drawn.
- D6 fallback: no portals (`NPortals==0`), or the flood reaches nothing → the set is
  null and `ClassifyGroup` runs the old 120-yd interior heuristic unchanged.

### 2. Flood (`ComputeReachableGroups`)

A stack DFS carrying a screen rect. Guards, faithful to benilla, are `came`-from (never
re-cross the entry portal) + `DEPTH_CAP=64` + `MAX_ITERS=65536` — **no global visited
set**; the shrinking rect kills cycles (a revisit through a narrower window collapses).
A group is marked reachable when popped. Groups are keyed by **file index** (`Model.Groups`
is compacted, so a `byFile` dictionary maps MOPR/CameraGroup indices → `GroupMesh`).

### 3. The MOPR side bit — the convention that inverts the world if wrong (D3)

Read RAW, identical to benilla (`benilla-formats/.../root.rs`) — no negation, no
swizzle. The eye is transformed into the WMO's **local** space; the portal plane is the
raw `(NormalX,Y,Z, PlaneDistance)`:

```
d = P.NormalX*eyeLocal.X + P.NormalY*eyeLocal.Y + P.NormalZ*eyeLocal.Z + P.PlaneDistance;
if (r.Side < 0) d = -d;      // Side (±1) orients the plane
if (d < 0f) continue;        // enter only from the FRONT (threshold exactly 0)
```

**If interiors vanish where you stand and appear where you don't, this sign is flipped.**

### 4. Frustum-through-portal clip (`PortalScreenRect`)

Each doorway's polygon is transformed local → world → camera-relative → clip
(`RelativeViewProjection`, matching `Camera.BoxInFrustum`'s row-vector convention), then
Sutherland–Hodgman-clipped against the **four side planes only** (`w+x, w-x, w+y, w-y`)
— **no near plane**, so a doorway you're standing in explodes to full-screen instead of
collapsing. A `w`-clamp (`|w| < 0.001 → +1e-5`) keeps a straddling vertex sane. The
survivors' perspective-divided NDC min/max is the doorway's screen rect. The child rect
is `intersect(currentRect, doorwayRect)`; if either extent `< RECT_EPS (0.001)` the
branch dies — **that collapse is the cull.**

### 5. Deferred exterior (the roof cull) — `ComputeReachableGroups` tail

```
// If the flood reached ANY 0x08 group, the whole exterior shell is visible
// (you can see the sky through that doorway). If it reached none, exterior
// groups stay unreached and get culled -> roof and outer shell disappear.
if (any reachable group has flag 0x08)
    add every 0x08 group to the reachable set;
```

Outdoors this is a no-op (the seed already pushed every `0x08` group). Indoors it is the
whole trick: deep inside with no line to the outside, no `0x08` group is reached, so the
roof/outer shell is culled; near a doorway to the sky, one is reached and the shell
snaps back. Faithful to benilla `mod.rs:453-459`.

### 6. The visibility decision (`ClassifyGroup`)

In order: curated overrides (PLAN_04) win; then **distance-LOD shells** (`IsDistanceLod`)
take the §3.34 swap path and `return` before any portal logic (D5 — skyline safe); then
**every other group** is gated:

```
if (ReachableGroups is not null)                 // culling active
    if (!ReachableGroups.Contains(group.GroupIndex))
        return InteriorCull;                     // reached => draw, else cull
else if (group.IsInterior && !CameraInside && dist > InteriorCullDistance)
    return InteriorCull;                         // D6 legacy heuristic (culling off/unseedable)
```

With culling **off**, only the interior-only heuristic runs, so exterior groups always
draw and behaviour is byte-identical to before this system.

### 7. Distance-shell suppression uses a cell signal, not a yard mark

The cathedral silhouette is a distance shell (`holy01`, `IsDistanceLod`). Its
suppression was purely `ShellNearGuard` yards, which trips out on the bridge. With
culling on it now keys on **`CameraInCell`** = *the camera is in a real interior/street
cell of this building* (`CameraGroup` in this instance AND **not** a `0x08` exterior
cell). So the silhouette holds across the bridge and the gatehouse (`thief01`, `0x08`)
and drops the moment you reach a `0x40` street. Culling **off** keeps the tuned
`CameraInside || within ShellNearGuard` behaviour exactly.

---

## The dev instrument (Program.Portals.cs)

- **Portal culling** checkbox — the A/B (writes the live renderer + settings).
  `reached N interior group(s)` shows the flood size this frame.
- **`in: [idx] 'name'`** — the camera's containing cell (or `outdoors`), with flags.
  PLAN_10 D1: it must flip AT the doorway walking in and back to `outdoors` walking out.
- **Highlight picked group (solid, through walls)** — middle-click any geometry and it
  glows through everything, and the panel prints its exact index + flags + draw reason.
  Built specifically so "which triangle is the roof" is never ambiguous again.
- Portal-polygon draw + `Dump portal graph` + `Print camera cell`.

---

## Test protocol (PLAN_10 §7) — all passing

1. Walk from the bridge through the gate: the roof over the trade district is present on
   approach and **gone once you're in the streets**; no walls you should see go missing.
2. Approach Stormwind: the **cathedral silhouette holds** across the bridge and the gate
   and drops when you reach the interior streets. Towers/outer walls unchanged (D5).
3. A/B the toggle from one spot: drawn-group count falls with it on; the picture with it
   off is the old picture exactly.
4. Long hall: nothing vanishes at 120 yд any more (the heuristic is retired when active).

---

## Known limits / debt

- **Meshless bridge cells dead-end the flood.** `PrepareWmo` drops empty/antiportal
  groups, so a group that carries portal refs but built no mesh stops traversal
  (`byFile` miss). benilla keeps a complete per-absolute-index `group_nav`. In practice
  those are never habitable rooms, so it hasn't bitten; the faithful fix is a per-file
  `PortalNav[]` (flags + PortalStart/Count) retained for *every* parsed group. **Symptom
  if it ever bites: interior geometry you can plainly see through a door goes missing.**
- **Outdoor seed floods every portaled WMO not containing the camera.** Bounded by the
  `Portals.Count>0` guard (huts have none), so in practice it's just the city in view.
- **Per-frame allocation** (a `byFile` dict + the flood's lists) when culling is on.
  Fine today; cache on `Model` if a dense city ever shows it.
- **`CameraInside` (CameraInsideInstance) is still fragile** (§3.35) and is only used now
  for the culling-off shell path and the interior heuristic; the culling-on path uses the
  more precise `CameraGroup`/`CameraInCell`.

## Reconciliation

- Handbook **§3.26**'s 120-yd interior rule is **retired** whenever culling is active
  (it survives only as the D6 fallback).
- Handbook **§3.34/§3.35** (the distance-shell swap) is unchanged with culling off; with
  it on, the shell's *near-suppression* keys on the portal cell signal instead of the
  yard guard (§7 above). The shell classification itself (`IsDistanceOnlyLod`) is
  untouched.
- Handbook **§7.1 item 9** (WMO portal visibility) and **PLAN_10** are **done**; PLAN_10
  §9's one-hop fallback was not needed.
