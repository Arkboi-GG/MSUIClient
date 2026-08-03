# Port session 2026-07-27 — loading-screen art, appear-fade, WMO portal culling

Three benilla systems ported into MSUIClient this session. **Nothing here has been
compiled** — there is no .NET SDK in the assistant sandbox (same constraint every
prior stop-point notes). **Build first.** Every change was read against the current
source and cross-reviewed for compile/render correctness, but the build is the proof.

Also: the device write-bridge dropped partway through this session, so these files
were delivered into the chat (downloadable) rather than written to the repo. Drop
each into the path listed in the manifest at the end, then `dotnet build`.

Scope honoured from your brief: **did not touch** instance (teleport) portals,
lighting, particles, or the Escape-menu look. The portal work here is WMO
interior/exterior *visibility culling* (PLAN_10) — a different system from the
teleport "instance portals" you said to leave alone.

---

## 1. Loading-screen art  (SYSTEM_LOAD.md extension) — default ON

The curtain from SYSTEM_LOAD.md now draws the map's **real WoW loading-screen BLP**
behind the progress bar, instead of the flat dark quad. Resolution is the verified
1.12 chain, ported from benilla (`benilla-formats/{maps,loading_screen}.rs`):

    Map.dbc field 38 (LoadingScreenID)  ->  LoadingScreens.dbc field 2 (BLP path)
    e.g. map 0 -> 4 -> Interface\Glues\LoadingScreens\LoadScreenEasternKingdom.blp

- `Formats/DbcReader.cs` — `MapRow.LoadingScreenId` (field 38, guarded by FieldCount);
  new `LoadingScreenTable` (id -> BLP path).
- `Engine/LoadingScreen.cs` — a second inline textured-quad shader + `SetBackground`;
  draws the art full-screen (stretched to the window), the dark quad only as fallback.
- `Program.Loading.cs` — `TryLoadLoadingArt(gl, mapId)` resolves + decodes + uploads
  the BLP (cached by path, disposed at shutdown), called from `BeginWorldLoad`. Reads
  Map.dbc directly (EnsureInstanceData runs too late, in the Finish phase).
- Best-effort: any miss (no MPQ, no FK, missing BLP, decode failure) silently leaves
  the dark curtain. Kill switch: `Render.LoadingScreenArt=false` in client-config.json.

**Test:** enter world; the curtain should show the Eastern Kingdoms art (Elwynn/SW is
map 0). Console prints `[load] loading-screen art <path> (WxH) for map 0`. `.tele`
across a map boundary that re-raises the curtain shows that map's art.

**Debt:** art is stretched to the window, not letterboxed 4:3 as benilla does. Fine for
a splash; if you want pillarboxing, pass the window aspect into `LoadingScreen.Render`
and inset the textured quad.

---

## 2. Per-object appear-fade  (SYSTEM_LOAD.md "Still to do") — default ON

benilla `model_fade.rs`: a streamed-in model eases in over 2 s (alpha = t^3) instead
of popping. Ported for both doodads and WMO buildings. **Only the output alpha is
touched — lighting is untouched** (verified: RGB/lambert/baked/fog math unchanged in
both shaders).

- Doodads: per-instance `AppearStart` rides the instance VBO as a new vertex attribute
  (location 8; stride 20->21 floats; `doodad.vert`/`doodad.frag`). Straight-alpha blend
  is enabled around the doodad pass; at alpha 1 it composites bit-identically to opaque.
- WMO: per-instance `uAppearAlpha` uniform (`wmo.frag`), always set (so a building can
  never vanish from a default-0 uniform); blend is enabled for the opaque groups only
  while a building is actually fading, depth-write kept on (benilla wow_model.wgsl).
- Arming (benilla `arm_appear_fade` / `focus_resident`): a `_worldShown` gate. During
  the initial build behind the curtain, every placement is stamped **opaque** (the
  curtain's own fade covers the reveal); only models streamed in *after* the curtain
  lifts ease in — which is exactly the walk-time pop-in this closes.
- Re-placement safety (the trap the analysis flagged): a tile crossing rebuilds every
  resident placement, so a per-key `_appearStartByKey` (surviving ResetPlacements)
  keeps re-placed objects opaque and only fades genuinely new keys. Capped so a long
  roam can't grow it unbounded.

**Test:** walk into an unloaded area; new trees/props/buildings should fade in over ~2 s
rather than snapping. Cross a tile boundary back and forth — the resident world must
**not** re-fade each crossing (if it does, the key persistence regressed). Toggle off:
`Detail.AppearFade=false` in settings.json → hard pop-in returns (A/B).

---

## 3. WMO portal culling — hides Stormwind's roof from inside  (PLAN_10) — default OFF

The flagship ask. benilla `wmo_portal/` (the analysis first pointed at `interior.rs`,
which is actually entity-lighting; the real PVS is `wmo_portal/mod.rs`+`seed.rs`).
Implements PLAN_10 D1–D7. When on and the camera is inside a WMO cell, it floods the
portal graph from that cell, clipping the view frustum at each doorway, and draws
interior groups **only when reached** — so the exterior roof/ceiling group that is
unreachable from your cell stops drawing. The 120-yd interior heuristic is replaced
(not tuned) when traversal is active.

- `World/Wmo/WmoRenderer.cs`: `UsePortalCulling` toggle; `ComputeReachableGroups`
  (stack flood, `came`-from + depth-64 + 65536-iter guards, no visited set — the
  shrinking screen-rect kills cycles, exactly as benilla); `PortalScreenRect`
  (Sutherland–Hodgman clip vs the 4 side planes, w-clamp, NDC rect); the MOPR `side`
  bit front-face test in WMO-local space; `FrameCullContext.ReachableGroups`;
  `ClassifyGroup` interior branch consults it. Seeded from the existing D1
  `CameraGroup` (already computed every frame in Update).
- Honours: **D4** exterior groups untouched (only the interior branch changes);
  **D5** distance-LOD shells excluded (the shell swap is a separate branch that
  returns first — skyline unchanged); **D6** falls back to the current heuristic when
  it can't seed or reaches nothing (worst case = today's picture, never an empty
  building); **D7** toggle, **default OFF**.

**Why default off:** it is an unverified culling change to the hottest render path and
I could not run it. Off means zero behaviour change until you opt in.

**Enable + test:** open the dev HUD → **Portals (PLAN_10)** panel → tick
**"Portal culling (hide unreachable interiors)"**. Then, from PLAN_10 §7:
1. Walk into Stormwind. The roof/ceiling visible on the way in should disappear once
   you're inside, and **no walls/floors you should see may be missing**. The panel's
   "reached N interior group(s)" should be > 0 inside.
2. If interiors you're standing in vanish and far ones appear — the **MOPR side bit is
   inverted** (PLAN_10 D3). Flip the `if (r.Side < 0) d = -d;` sense in
   `ComputeReachableGroups` and retest. This is the single most likely failure.
3. Approach from outside: the skyline silhouettes (Cathedral shells) must be unchanged
   (D5). If they change, traversal wrongly touched a shell.
4. A/B: drawn-group count should fall indoors with it on.

**v1 scope (deliberately smaller than full PLAN_10):** traversal runs only for the
instance the camera is *inside*. The "stand outside and see a room through an open
door" case (PLAN_10 §3 table) is **not** done — outside a building its interiors use
the old 120-yd rule, which is never worse than today. Add outdoor/exterior seeding
(benilla `mod.rs:365-371` + the deferred-exterior shell gate) as a follow-up if you
want the door-peek case. When it ships, extract `SYSTEM_WMO_PORTALS.md` per PLAN_10 §8.

---

## Settings summary

client-config.json:  `Render.LoadingScreenArt` (true)
settings.json:       `Detail.AppearFade` (true), `Detail.AppearFadeSeconds` (2),
                     `Detail.WmoPortalCulling` (false)
No rows were added to the Escape-menu Video Options page (your menu look is untouched);
the portal toggle lives in the dev **Portals** HUD panel, appear-fade via settings.json.

---

## Build checklist

1. `dotnet build` at the repo root. Expect 0 errors. Watch for: a shader that fails to
   compile prints its full source (Engine/Shader.cs) — the four new uniforms are
   optional (Location tolerates -1) so a name typo logs, it does not crash.
2. If buildings render invisible → `uAppearAlpha` isn't reaching wmo.frag; confirm the
   WMO frag was updated and the renderer sets it every instance.
3. If doodads render as garbage → the instance VBO stride/attribute (DoodadRenderer
   BuildModel) disagrees with `InstanceData`; both must be 21 floats.

## What was NOT ported (the honest roadmap)

These are real multi-session efforts, most needing a live server + iterative debugging,
so they were out of scope for a one-shot uncompilable pass:

- **Networking / the "http connection"** — benilla-protocol (129 files) + net.rs + SRP6
  auth. A large, well-bounded port, but it only *works* against your realm with live
  packet debugging, so it can't land blind.
- **Combat, creatures/NPCs as entities, nameplates, chat bubbles, cast bars** — all
  depend on the network entity stream above.
- **The full UI framework** (benilla-ui, ui_* : quest log, loot, trade, merchant, mail,
  talents, spellbook, …) — also server-driven.
- **WDL distant-horizon mesh** (benilla wdl.rs) — self-contained and a good *next*
  local-only visual port if you want one without touching the network.
- **Painterly pass** — your own stated long-term goal; a shader variant.

If the device bridge is back next session I can write directly to the repo and we can
take the WDL horizon or start scaffolding the netstack.
