# Plan 03 — Visibility reason codes (the `ClassifyGroup` refactor)

**Foundation build step 3** (`FOUNDATION_PLAN.md` §3.4). Make every WMO group's
draw/skip resolve to exactly one **reason code**, decided by a single shared
predicate, so "why is this building missing?" is a lookup instead of a guess.

Grounded in `World/Wmo/WmoRenderer.cs` (render decision `1603–1690`, build-time
drops `823–845` + `ModelLoadJob` counters `162`, inside-test `686`, picker
`1805–1852`).

> **Build-order note.** The `FOUNDATION_PLAN` numbered this step 3, after the
> dump (step 2). Thinking it through, **this should be built before the dump**:
> the dump's entire value is the reasons, and both the dump and the override DB
> call the predicate this step creates. Recommended order is now
> **01 → 03 → 02 → 04 → 05 → 06** (see the reconciliation note at the end).

---

## 1. Problem

Whether a WMO group draws is decided in `Render()` by a stack of bare `continue`
statements — shell-near, interior-cull, distance, frustum, occlusion — each a
silent skip. Three more decisions happen *elsewhere*: antiportal and no-geometry
groups are dropped at **build** time (`823–845`), kept only as per-root aggregate
counts (`ModelLoadJob.Antiportal/Empty/Unbuilt/MissingFiles`); and a building can
be missing because its **instance was never resident** — it isn't in `_instances`
at all, so the render loop never sees it. When a real exterior WMO doesn't show,
these are indistinguishable from a screenshot, and today they're indistinguishable
from the code too. That is the random walk, at its source.

## 2. Class

Foundation instrumentation. "Done" is mechanical (§8): the reason reported always
equals the reason acted on, for every stage.

## 3. Target

One enum, one shared predicate. Every group — drawn, skipped, dropped, or absent —
can be assigned exactly one `ReasonCode`, queryable **on demand** (by the picker,
the HUD, and the dump), never at per-frame cost. The reason shown and the reason
acted on come from the *same code path*, so they cannot drift.

## 4. Key design decisions

**4.1 The reason set** (precedence order — first match wins):

```
OVERRIDE_HIDE          curated hide (Plan 04) — highest authority
OVERRIDE_SHOW          curated show (Plan 04) — bypasses heuristic culls below
NOT_RESIDENT           placement exists in a resident ADT but no live instance yet
MISSING_FILE           group file absent from MPQs        (build-time)
NOT_BUILT              group failed to build / no VAO      (build-time)
NO_GEOMETRY            group has no drawable batches       (build-time)
ANTIPORTAL_SKIP        MOGP 0x04000000 occlusion-only geom (build-time, §3.33)
INSTANCE_FRUSTUM       whole WMO instance outside frustum
SHELL_NEAR_SUPPRESSED  distance-shell hidden because inside/near (§3.34)
DRAWN_SHELL_FAR        distance-shell drawn as the far impostor
INTERIOR_CULL          interior group, outside, beyond InteriorCullDistance (§3.26)
DISTANCE_CULLED        beyond effective draw distance
FRUSTUM_CULLED         group box outside frustum
OCCLUSION_CULLED       all-blocked per collision BVH (§3.35)
DRAWN                  submitted
```

**4.2 One predicate, two callers — the anti-drift guarantee.** Factor the render
decision stack into a pure method:

```
ReasonCode ClassifyGroup(Instance inst, GroupMesh g, in FrameCullContext ctx)
```

`FrameCullContext` bundles what the loop already computes once per instance
(camera position, `viewProjection`, `effectiveDrawDistance`, `cameraInside`, the
toggle states). `Render()` calls it and draws when the result is `DRAWN` /
`DRAWN_SHELL_FAR`; `ExplainGroup()` calls the *same* method for reporting. The
existing `continue` ladder maps one-to-one onto the enum, so this is a
refactor-in-place, not new logic — the branch conditions move into `ClassifyGroup`
and `Render` switches on its return. The picker already does this for shells
(`IsDistanceOnlyLod` is shared by draw and pick, `1842`); this generalizes it.

**4.3 Build-time drops need a retained record.** Antiportal / empty / unbuilt /
missing groups are discarded during build and survive only as counts. To explain a
*specific* dropped group, add a lightweight list to `Model`:

```
record RejectedGroup(int GroupIndex, uint Flags, int VertexCount, ReasonCode Why);
List<RejectedGroup> Rejected;   // populated where 823–845 currently just count
```

Cheap (a handful per root), and it lets the picker/dump answer "that group exists
in the file but was dropped because ANTIPORTAL_SKIP."

**4.4 NOT_RESIDENT is a placement-level fact, not a group-level one.** The render
loop can't report it because the instance isn't there. Add an enumerator the dump
(Plan 02) will use:

```
IEnumerable<(string Root, uint UniqueId, Vector3 Pos)> EnumerateExpectedWmoPlacements(tiles)
```

built from the resident ADTs' MODF lists (the same `adt.Wmos` the loader walks at
`389–402`). A placement whose root has no entry in `_instances` → `NOT_RESIDENT`.
This is the answer for "the building is just not loaded yet," which no per-group
reason can give.

**4.5 On demand, not per frame.** `ClassifyGroup` for *acting* runs in `Render` as
today (same cost). For *reporting*, `ExplainGroup` is called only for the picked
group and for the dump's targeted instance — never a full sweep every frame. The
per-frame aggregate counters already exist (`VisibleGroupsLastFrame`,
`ShellsHiddenLastFrame`, `OccludedGroupsLastFrame`, `LodGroupsCulledLastFrame`) and
feed the HUD histogram cheaply.

## 5. Files touched

| File | Change |
|---|---|
| `World/Wmo/WmoRenderer.cs` | add `ReasonCode` enum + `FrameCullContext`; extract `ClassifyGroup`; `Render` switches on it; add `ExplainGroup(instance, group)` and an overload for a `RejectedGroup`; add `Model.Rejected` populated at `823–845`; add `EnumerateExpectedWmoPlacements`; extend `GroupHit` with the resolved `ReasonCode` and the **full root path** (Plan 04 needs it) |
| `Program.cs` | show the picked group's `ReasonCode` in the pick readout (`1554`); nothing else yet |

## 6. Resources

- `World/Wmo/WmoRenderer.cs`: render stack `1603–1690`; build drops `823–845` +
  `ModelLoadJob` `151–165`; `CameraInsideInstance` `686`; `GroupMesh` `81–111`;
  `PickGroups`/`GroupHit` `1805–1852`; loader MODF walk `389–402`.
- Handbook §3.26 (interior/portal), §3.33 (antiportal), §3.34 (distance shell),
  §3.35 (occlusion, inside test).
- **WoWee:** not needed to *instrument* — the reasons are our own stack. (If we
  later replace the interior heuristic with portal traversal, that's a separate
  step and would add a `PORTAL_*` reason then.)

## 7. Reconciliation with other plans

- **Feeds Plan 02 (dump):** the dump's WMO block calls `ExplainGroup` and
  `EnumerateExpectedWmoPlacements`. Build 03 first.
- **Feeds Plan 04 (override):** `OVERRIDE_HIDE/SHOW` are the top of the precedence;
  04 fills the hook `ClassifyGroup` leaves for it, and needs the full root path now
  added to `GroupHit`.
- **Changes the build order** in `FOUNDATION_PLAN` §7 to 01 → 03 → 02 → 04 → 05 → 06.

## 8. Test protocol and definition of done

- Middle-click the missing/mysterious group → the HUD pick line shows its
  `ReasonCode`. Toggle the relevant cull (e.g. `Swap distance-only city shells`)
  and the code changes accordingly (`DRAWN_SHELL_FAR` ↔ the detailed group's code).
- Aim at a spot where a building should be but isn't and pick nothing → the dump
  (once 02 lands) lists a `NOT_RESIDENT` placement there; for a dropped group, the
  picker/`Rejected` list reports `ANTIPORTAL_SKIP` / `NO_GEOMETRY`.
- **Done:** for a chosen instance, every group is classifiable, and flipping any
  single cull flips exactly the expected groups' codes — proving report equals act.

## 9. Fallback

If refactoring the hot `Render` loop feels risky, first ship `ExplainGroup` as a
**read-only replay** of the same conditions (duplicated, not shared) to get the
reasons flowing, then unify it with `Render` in a follow-up. Prefer unifying
immediately — duplicated predicates are exactly the drift this step exists to kill
— but the fallback unblocks the dump without touching the draw path.
