# Plan 04 — Visibility override database (curated truth)

**Foundation build step 4** (`FOUNDATION_PLAN.md` §3.5). A hand-authored show/hide
database, built by clicking, that the renderer consults **first** — so we can make
any frame look right *today*, before we understand the heuristic, and so cases the
heuristics will never get right have a permanent home.

Grounded in the existing middle-click picker (`PickGroups` → `_lastPick`,
`Program.cs:839, 1020, 1551`) and the `ClassifyGroup` predicate from Plan 03.

> **Build-order note.** After Plan 03 (it fills `ClassifyGroup`'s override hook and
> needs the full root path added to `GroupHit`) and ideally after Plan 02 (see the
> problem before overriding it). Order: 01 → 03 → 02 → **04** → 05 → 06.

---

## 1. Problem

Two problems, one tool. First, some of these visibility calls may never have a clean
heuristic — Blizzard hand-authored the LOD/interior behaviour and a rule that's
right for Stormwind is wrong for Ironforge. Second, and bigger: **we need a way to
always make the frame look right, even before we understand why it's wrong.** That
guarantee is what de-risks the whole visibility area — worst case, we hand-annotate
and it's correct.

## 2. Class

Foundation tooling *and* the safety net under Pillar A. Mechanically done when a
click reliably shows/hides a group and it persists.

## 3. Target

Middle-click a group (already works), press "Hide picked" / "Show picked" → an entry
is appended to `visibility_overrides.json`, applied live, and surviving restart. The
renderer honours overrides above every heuristic. `ClassifyGroup` returns
`OVERRIDE_HIDE` / `OVERRIDE_SHOW` so the dump and HUD show exactly what's curated.

## 4. Key design decisions

**4.1 Identity — `(rootPath, groupIndex)`, with a region escape hatch.** The picker
already yields the group index and (after Plan 03) the full root path. That pair is
the key. Caveat worth stating plainly: it applies to **every instance of that root**.
For unique city WMOs — Stormwind, the building example — there's one instance, so
it's exact. For a house model reused 200 times it would hide the group in all of
them. Two escape hatches:

- **Region box** — a world-space AABB with a rule, for "this placement here, not the
  model everywhere." Matched by the instance's world position.
- *(Later, if needed)* **MODF `UniqueId`** — the precise per-placement key. The MODF
  list carries it (`EnumerateExpectedWmoPlacements` already surfaces it, Plan 03
  §4.4), but the renderer doesn't retain it per instance yet. Add that only if
  repeated-model overrides become real; region covers v1.

**4.2 Authoring is one click.** `_lastPick[0]` is the nearest picked group and holds
everything needed. Add HUD buttons "Hide picked" / "Show picked" (and optionally a
hotkey) in the Visibility section that append an entry and apply it immediately — no
reload, because `ClassifyGroup` reads the in-memory override set each frame. The DB
grows by pointing at problems, which is the whole idea.

**4.3 Precedence and what `OVERRIDE_SHOW` bypasses.** In `ClassifyGroup`, overrides
are checked first:

- `OVERRIDE_HIDE` → skip unconditionally.
- `OVERRIDE_SHOW` → bypass the **heuristic** culls (shell-near, interior,
  distance, occlusion) but still respect **residency** (can't draw what isn't
  loaded) and **frustum** (drawing off-screen is wasted). So "force show" means
  "ignore the guesses," not "draw impossible."

Rationale: curated truth beats heuristics (the point), but residency and frustum
aren't heuristics — they're correctness/perf invariants.

**4.4 Persistence.** `visibility_overrides.json` at the repo root, same JSON
conventions as `vantages.json`, **git-committed** (unlike dumps): the annotations are
shared truth we build over time. Loaded at startup into a `VisibilityOverrides`
lookup; each entry carries `{ root, groupIndex | regionBox, rule, note, vantage }`
so future-us knows the context.

**4.5 It's also training data.** Patterns in what we hand-hide/show are evidence for
the missing heuristic. `OVERRIDE_*` counts in the dump make that visible over time —
e.g. "we keep hiding low-vert exterior groups at range" suggests a classifier tweak.

## 5. Files touched

| File | Change |
|---|---|
| `Engine/VisibilityOverrides.cs` *(new)* | entry record + store (load/save `visibility_overrides.json`, lookup by `(root,groupIndex)` and by region) |
| `World/Wmo/WmoRenderer.cs` | hold the override set; `ClassifyGroup` checks it first (the hook Plan 03 left); needs the full root path already on `GroupHit`/instance |
| `Program.cs` | "Hide picked" / "Show picked" buttons in the Visibility HUD section, operating on `_lastPick[0]`; load the store at startup |
| `visibility_overrides.json` *(new, repo root, committed)* | the annotations |

## 6. Resources

- `World/Wmo/WmoRenderer.cs`: `PickGroups`/`GroupHit` `1805–1852`; `ClassifyGroup`
  (Plan 03); `Instance.Path`/`Transform` for region matching.
- `Program.cs`: pick wiring `839`, `1020`, `1551`; the `_lastPick` HUD list `1554`.
- Handbook §5.3 (the picker as the identification tool), §3.34/§3.35 (what the
  heuristics do that we're overriding).

## 7. Reconciliation with other plans

- **Fills Plan 03's** `OVERRIDE_*` hook; **requires** the full root path Plan 03 adds
  to `GroupHit`.
- **Surfaced by Plan 02:** the dump reports `OVERRIDE_HIDE/SHOW` and their counts.
- **Presented by Plan 05:** the Hide/Show-picked controls live in the reorganized
  Visibility section beside the reason readout.

## 8. Test protocol and definition of done

- Middle-click the `thief01` entrance keep that stays visible across the open
  courtyard → "Hide picked" → it vanishes, the dump shows `OVERRIDE_HIDE`, and it's
  still hidden after a restart.
- Find a real exterior building the heuristics wrongly drop → "Show picked" → it
  appears (in frustum, resident), dump shows `OVERRIDE_SHOW`.
- Edit `visibility_overrides.json` by hand, relaunch → the change takes effect.
- **Done:** a click reliably flips a group and persists; the frame can be made to
  look right by hand regardless of heuristic state.

## 9. Fallback

Ship `(rootPath, groupIndex)` matching only for v1 — it fully covers unique city
WMOs, which is the building example and the hardest cases. Region and `UniqueId`
matching for repeated models come only if a real repeated-model case demands them.
