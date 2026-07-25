# Plan 10 — WMO portal visibility

**The last major piece of authored WMO data the renderer ignores.** MOPV, MOPT
and MOPR are fully parsed and consumed by nothing (`WmoReader.cs:279-335,
408-414`, and the comment on `WmoRoot.Portals` says so outright: *"Parsed for the
future visibility traversal; no renderer reads these yet."*). Handbook §7.1 item
8 names this as the remaining lever for indoor correctness.

Grounded in the real source: `Formats/WmoReader.cs`, `World/Wmo/WmoRenderer.cs`,
handbook §3.26, §3.34, §3.35.

---

## 1. Problem

Indoor visibility is currently a **distance heuristic pretending to be
occlusion**. Handbook §3.26, in its own words:

> "Until those root chunks are parsed, the renderer uses per-group frustum
> culling and draws ordinary interior groups only within 120 yards of their
> transformed AABB."

They are parsed now. The heuristic remains. Its consequences:

- **Every interior group within 120 yards draws, whether or not it is reachable
  or even visible.** Standing outside Stormwind's cathedral, the rooms behind its
  walls are submitted and rasterised; the walls happen to cover them.
- **Interiors past 120 yards vanish regardless of line of sight.** A long hall
  ends in nothing at its far end.
- It is a **yard number tuned by eye** standing in for a data structure the
  artists authored precisely.

Stated from a vantage: *at the Stormwind courtyard, interior groups draw through
walls and pop at a distance threshold rather than at a doorway.*

> **Paired artifact needed.** `refs/` holds only a README. A real-client capture
> of the same courtyard is what turns "pops at a threshold" into a measured
> difference. This plan does not block on it — §7 is numeric — but §8 does.

## 2. Class

**Emulation-core.** Portal traversal is exactly what the 1.12 client does, from
the same chunks in the same files. There is an external right answer.

## 3. Target

The renderer decides which interior groups to draw by **traversing doorways from
the group the camera is in**, not by distance. Concretely:

- Standing in a room, only that room and what is visible *through its doorways*
  is submitted.
- The 120-yard rule is deleted, not tuned.
- No interior group is ever drawn because it happened to be near.

## 4. Hypotheses / key design decisions

Ranked by how much each buys, and each falsifiable by the dump.

**D1 — "Which group is the camera in?" is the whole problem, and it comes
first.** Traversal starts from the camera's group. Get that wrong and every
subsequent step is wrong in a way that looks like a portal bug. The camera may
also be in **no** group (outdoors), which is the common case and must be fast.
Build and verify this alone before any traversal exists.

**D2 — Traverse breadth-first through portals, clipping the frustum at each
doorway.** The classic algorithm: start at the camera's group with the full
frustum; for each portal out of it, if the portal polygon is inside the current
frustum, intersect the frustum with the portal's screen-space bounds and recurse
into the group on the far side. A group is visible if any traversal reaches it.

**D3 — MOPR's `side` bit decides which way a portal faces, and getting it
backwards silently inverts the world.** Each MOPR entry is
`(portalIndex, groupIndex, side)`. The portal plane in MOPT has an orientation;
`side` says whether this group is on the plane's positive or negative half. Cull
portals facing away from the camera using it. **If interiors vanish when they
should appear and appear when they should vanish, this is the bit.**

**D4 — Outdoor groups are not part of the graph, and must not be dragged into
it.** Exterior groups are drawn by the existing frustum + LOD path (§3.34's shell
swap). Portal traversal governs **interiors only**. Handbook §3.35 warns
explicitly that outdoor portal traversal is experimental and trades popout for
it — so this plan does not attempt it.

**D5 — The ALWAYS_DRAW distance shells are untouchable.** §3.34/§3.35: Stormwind's
approach silhouettes are `ALWAYS_DRAW (0x10000)` groups that are *also* flagged
interior. They are an authored LOD system, not portal cells. Portal traversal
must exclude them by flag before it does anything else, or approaching Stormwind
loses its skyline. **This is the single most likely way to break something that
currently works.**

**D6 — Degrade to the current behaviour, never to nothing.** A WMO with no
portals (`NPortals == 0`), a camera in no group, or a traversal that reaches
nothing must fall back to the existing heuristic. Half of Azeroth's WMOs are
single-group huts with no portals at all; they must keep working.

**D7 — Keep it a toggle for its whole life.** Every visibility change in this
project has shipped with an A/B (`§3.34`'s shell swap, foliage's placement
rules, the SoA cull). A visibility regression is invisible until you stand in the
one spot that shows it, so the ability to flip it off in-game is not optional.

## 5. Resources

**Check these before writing anything** — the handbook records writing from
scratch what already existed, twice.

| Resource | Why |
|---|---|
| `WmoReader.cs:279-335` | `MOPV`/`MOPT`/`MOPR` already parsed: `PortalVertices`, `Portals`, `PortalRefs` |
| `WmoReader.cs:408-414` | `MOGP +0x24/0x26` -> `PortalStart` / `PortalCount`, the run of MOPR entries per group |
| `WmoPortal` (`WmoReader.cs:785+`) | `StartVertex`, `Count`, and the C4Plane |
| `WmoRenderer.cs` | Group draw loop, the 120-yard interior rule, the existing group picker |
| Handbook §3.26 | MOGP offsets; interior/exterior classification; the temporary 120-yard rule this replaces |
| Handbook §3.34, §3.35 | The `ALWAYS_DRAW` interior shells that must NOT be treated as portal cells (D5) |
| `Program.DevTools.cs` `DumpScene` | Where a `portals` block belongs |
| WoWee `src/rendering/wmo_*` | Check whether it implements traversal; if it does not, this is ours to derive |

## 6. Tools / instrument

**The existing instruments are close but not sufficient**, so part of this plan
is instrument work — the same finding PLAN_07 and PLAN_09 both reached.

What exists: the middle-click group picker, `DumpLargeWmoGroups`
(`[wmo-groups]`), per-group reason codes, and the scene dump.

What is missing, and must be built first (D1):

1. **"Which group am I in?"** — a live HUD readout of the camera's containing
   group index and name, or `outdoors`. Everything downstream depends on it and
   nothing currently reports it.
2. **A portal graph dump** — for the current WMO: portal count, and per group its
   `PortalCount` and the group indices it connects to. This is what turns "the
   traversal is wrong" into "portal 12 links 4 -> 7 and it should not".
3. **Traversal result overlay** — which groups the traversal reached this frame,
   and via which portal chain. Without it, a missing room is unattributable
   between D1, D2 and D3.
4. **Portal polygon debug draw** — the doorway quads in world space. A portal
   whose plane or winding is wrong is obvious as a shape and invisible as a
   number.

Items 1 and 2 are cheap and answer most questions. **Build 1 first, alone, and
confirm it against walking through a door before writing any traversal.**

## 7. Test protocol

Written before the change, per the template.

**The instrument:**

1. Walk from Stormwind's courtyard through a door. The containing-group readout
   changes from `outdoors` to a named group **at the doorway**, not before or
   after. Walk back out: it returns to `outdoors`.
2. Dump the portal graph for `stormwind.wmo`. `NPortals` from MOHD equals
   `Portals.Count` (the reader already asserts the relationship in a comment —
   make it a check). Every `PortalRef.GroupIndex` is in range.
3. Stand still and dump twice. Identical graphs; the traversal result is stable
   frame to frame. A flickering result is a frustum-clipping bug, not a data bug.

**The defect:**

4. In a room with one door, the traversal reaches that room plus what the door
   exposes, and **nothing else**. Compare the reached-group list against the
   drawn-group list — they must match.
5. A/B the toggle from the same vantage: the drawn-group count should **fall**
   indoors. If it does not, traversal is not culling anything and D2 is wrong.
6. **Regression, and the one that matters most:** approach Stormwind from
   outside. The skyline silhouettes must be unchanged (D5). Record `[wmo-lod]`
   before and after and diff them.
7. Walk a long hall. Nothing vanishes at 120 yards any more.

## 8. Definition of done

**The instrument:** steps 1-3 pass, and "which group am I in, and what can I see
from it" is answerable in-game without a rebuild.

**The plan's real output:** the 120-yard interior rule is **deleted**; steps 4-7
pass; `refs/stormwind-courtyard.png` exists and is matched; and
`SYSTEM_WMO_PORTALS.md` is extracted per the one-system-one-doc rule, carrying
the traversal algorithm and the `side`-bit convention as ground truth.

Explicitly **not** in scope: outdoor portal traversal (D4), portal-based
occlusion for doodads or liquid, and any change to the `ALWAYS_DRAW` shell swap.

## 9. Fallback

If traversal proves large or unstable, the ordered partial wins are:

1. **The containing-group readout alone** (D1). It is a genuine diagnostic for
   interior lighting and doodad placement too, independent of any culling.
2. **Portal graph dump alone.** Turns future portal questions into data.
3. **Backface portal culling without recursion** — draw the camera's group plus
   every group one portal away. Strictly better than a 120-yard radius, and about
   a tenth of the work of full traversal.

Step 3 is the real fallback: it captures most of the visible benefit indoors and
cannot loop, recurse or stack-overflow.

## 10. Reconciliation

- **Handbook §3.26** loses its "until those root chunks are parsed" caveat and
  its 120-yard rule. Its "eventual portal implementation is:" paragraph becomes
  the description of what shipped.
- **Handbook §3.34 / §3.35** are unaffected by design (D5) — and §7 step 6 exists
  to prove it rather than assume it.
- **Handbook §7.1 item 8** is closed.
- **Plan 02 (scene dump)** gains a `portals` block: containing group, reached
  groups, portal chains.
- **Plan 05 (HUD/TuningState)** gains the portal panel and the traversal toggle
  (D7).
- **`SYSTEM_WMO_INTERIOR_LIGHTING.md`** should note that "which group am I in" is
  now answerable, since its MFOG follow-up (handbook item 10) needs exactly that.
