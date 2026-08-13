# Empirical checks — questions only a running client can answer

2026-07-25. Answer inline and hand it back; each answer changes what gets built
next, and I've said which. **Skip anything that's a hassle** — partial is fine,
and the ones marked ★ are worth more than the rest.

---

## A. Does it still build and run? ★

The whole session is uncompiled — no .NET SDK in my sandbox.

**A1.** Does it build? If not, paste the first 3 errors verbatim.
> **ANSWERED 2026-07-25 — yes.** It built and ran: the Instances panel produced
> a complete 44-map dump and the hitch recorder produced four records. That
> clears `ImGuiFontConfig` and `AddImageQuad`, the two riskiest calls of the
> session, plus everything in PLAN_13 stage 1.

**A2.** On startup, paste the four lines:
```
[ui-skin] ...
[ui-font] ...
[settings] window ...
[light] coordinate convention detected: ...
```
> 

*Why: `[ui-skin] 16/16` and `[ui-font] ... from fonts.MPQ` are pass conditions.
The two riskiest API calls of the session are `ImGuiFontConfig` and
`AddImageQuad` — if the build broke, it is probably one of those two lines.*

---

## B. Water — PLAN_12, the thing just built ★

**B1 — the one that decides everything.** Stand at vantage
`looking at green river water`, open the DevTools HUD → **Light probe**, and
paste the four water band rows plus the `LightParams` line:
```
13 ocean close   ...
14 ocean far     ...
15 river close   ...
16 river far     ...
water alpha  ... / ...   ocean ... / ...
```
Note whether any say `(unauthored)`.
> 

*Why: the measured river pair is inverted — near-black shallow, bright
olive-green deep — where ocean behaves normally. If those bands say
`(unauthored)`, we are reading a neighbour's row and the band mapping is the bug,
not the shader. This is PLAN_12 §4 H4 and it gates everything else in section B.*

**B2 — bit-identity.** Video Options → Water → toggle **Authored water colours**
off, then on, then off. Does the water return to exactly what it looked like
before this session?
> 

*Why: every new shader term is `mix(old, new, uAuthoredWater)`. If OFF is not
identical, the mix is not reducing and I got the arithmetic wrong.*

**B3 — the shimmer.** With it ON, does the river still look like a *moving,
textured* surface, or has it gone flat and solid-coloured?
> 

*Why: SYSTEM_WATER Draft 2's finding is that 1.12 water IS the scrolling texture.
The authored colour is meant to tint it. Flat = I implemented replacement instead
of modulation, and it gets reverted.*

**B4 — ocean.** Fly west to open ocean. Better or worse than before?
> 

*Why: ocean is the type whose authored values look right, so it is where the
improvement should show. If ocean improves and river worsens, that is H4
confirmed and the fix is in the band mapping.*

**B5.** Cycle time of day with it on. Does the water colour move with the sky?
> 

*Why: the bands are time-interpolated. Water that stays put while the sky changes
means the resolve is not reaching the shader.*

**B6.** SYSTEM_WATER §5 has said "deep water still reads a little dark" for a
while. With authored alphas on — still true?
> 

*Why: if the authored alphas fix it, that entry gets deleted rather than tuned.*

---

## C. Frame pacing — the last streaming unknown ★

§5A.18 read `threadMCyclesPerMs` at **0.30–0.43** on three hitches, which says the
thread was **blocked, not spinning**. But those were caught during startup
streaming with the GPU at 8–14 ms, not on the controlled crossing.

**C1.** Walk `[32,48] → [32,49]` from a standing start, once, and paste one whole
`[hitch]` block — all six lines.
> **ANSWERED 2026-07-25, differently and better.** Four blocks arrived from a
> *stationary* camera at `[30,48]` in Stormwind rather than from a crossing.
> That eliminates streaming outright — resid/preload/discover/demand/adopt and
> every upload counter read 0.0 — and still shows 30-31 ms frames at
> 0.35-0.55 M/ms. The thread-blocked verdict now holds without §5A.18's
> startup caveat. **The fork resolved sideways: present tracks the GPU, WMO is
> 72-86% of GPU time, and the next lever is PLAN_10 portal culling, not the
> swap chain and not scheduling.** Full reading in SYSTEM_STREAMING.md §5A.20.

*Why: §5A.19 states the fork exactly. A frame with GPU back under 1 ms that
**also** reads <1 M/ms means §5A.16's zero-work stall is the same blocked wait and
the swap chain is the suspect. If it reads 4–5 M/ms, there are two bugs and
§5A.18 only killed one. **This decides whether the next streaming work is swap
chain or scheduling** — and the handbook says not to write streaming code before
reading it.*

**C2.** What is your monitor's refresh rate?
> **ANSWERED 2026-07-26 by inference — 60 Hz.** §5A.21: this scene's baseline
> frame is **p50 17 ms, p95 18 ms** — one refresh interval, hit consistently —
> and the hitches are 29-33 ms, which is two. Vsync is on and normally making
> its deadline. A direct confirmation from the display settings is still welcome
> but no longer blocking.
>
> *Superseded reasoning below.* §5A.20
> leaves exactly twelve milliseconds of `present` unexplained by GPU time. At
> 60 Hz with VSync on (the default) that is a textbook double-buffer miss —
> render 4.4 + GPU 13.3 = 17.7 overshoots 16.67, so a whole extra refresh is
> paid. At 144 Hz it means the double-buffer reading has been wrong since
> §5A.19. **One number decides it.**

*Why: 31–34 ms is exactly two intervals at 60 Hz. At 144 Hz it means something
completely different and my double-buffer reading is wrong.*

**C3.** PLAN_08 §7 step 3 is still owed: at one spot, back to back, toggle
**Flat cull bounds** off and on and paste the `doodad cull:` line for each.
> 

*Why: §5A.15 records honestly that the 55.8 ms → 0.3 ms win is **not** proven to
be the SoA change — model count fell at the same time. If the toggle makes no
difference at equal model count, the change gets backed out.*

---

## D. Settings — does the persistence actually work? ★

**D1.** Change something visible (view distance), Okay, **quit**, relaunch. Did it
come back?
> 

*Why: this is the entire point of PLAN_11. If it fails nothing else matters.*

**D2.** Set `"devTools": false` in client-config.json, relaunch. Does Escape open
the menu, and is the HUD gone?
> 

*Why: PLAN_11 H1 — the modal must work in a shipping build. Also the first real
test of today's `UpdateExteriorLighting` fix: with DevTools off, authored lighting
must still apply. **If the world looks different with DevTools off, that fix
failed.***

**D3.** Video Options → Ground clutter. Raise **Clutter distance** to 100. What
does the readout under it say, and does the grass actually reach that far?
> 

*Why: closes out the 40-yard cap. The readout prints the effective fade window,
so its number vs what you see is the answer.*

---

## E. `refs/` — five systems are photographically unverified ★★

`refs/` holds a README and nothing else. Water, WMO interior lighting, doodad
lighting, foliage and exterior lighting are all measured numerically and checked
against nothing. **This is the highest-value thing in the document and the only
one I cannot do any part of.**

**E1.** From the **real 1.12 client**, at roughly the two saved vantages, capture:
`refs/looking-at-the-visible-castle.png`, `refs/green-river-water.png`, plus a
plain daytime sky with a clear horizon. Then the same three from MSUI.
> 

*Why: the three sky band heights and the sun direction are the only invented
quantities left in exterior lighting, and SYSTEM_EXTERIOR_LIGHTING §4 says
nothing but a capture can settle them. The same shots also settle whether water,
foliage and interior lighting are right.*

**E2.** If E1 is too much, just one: **a real-client screenshot of the 1.12 Video
Options frame** at a known UI scale.
> 

*Why: cheapest possible, and it settles whether our frame proportions and the
font size are right — the one thing in SYSTEM_SETTINGS_UI still marked unverified.*

---

## F. Cheap eyeball tests already written down

Each is one line in an existing doc, none has been run.

**F1.** SYSTEM_FOLIAGE §0 — walk the Northshire road. Does grass creep onto the
cobblestone?
> 

**F2.** SYSTEM_DOODAD_LIGHTING — stand in a tavern. Does a barrel match the floor
it stands on, and is "N with baked interior light" non-zero indoors?
> 

**F3.** Approach Stormwind from outside: is its silhouette there? Inside Trade
District: are the Cathedral/entrance shells gone? Paste
`LOD shells hidden nearby`.
> 

*Why: F1 and F2 are the one-line tests those two systems were signed off against
and neither has been re-run since. F3 is handbook §7.1 item 7.*

---

## H. WMO liquid — PLAN_15, the thing just built (2026-07-26) ★

> **2026-08-12 update.** The build this checklist was written for was reverted;
> PLAN_15 was **rebuilt today as a DRAW-ONLY pass** (SYSTEM_WATER.md §7). The
> checks below still apply with three amendments: the log line is now
> `[liquid] WMO liquid vN: X surface(s) meshed, Y fully hidden` (H2's `escape`
> diagnostic lives in `WmoRenderer.LiquidEscapeCheck()`, not the load path);
> **H5 is NOT APPLICABLE** — submersion for WMO liquid is deliberately not
> wired, `TryGetSurface` is untouched and the overlay must not fire in a canal;
> and H7's depth slider does not exist — depth is a baked constant 3.0.

Six files changed and **none of it has been compiled.** H1 and H2 need no
screenshot and catch the two ways this can be wrong.

**H1 — does it build?** If not, paste the first 3 errors verbatim. The riskiest
edit of the session is `Model.Liquids` changing from a tuple carrying a group
index to one carrying a `GroupMesh`; if something broke, look there first.
> 

**H2 — the numeric gate.** Paste the three load lines:
```
[wmo-liquid] N surface(s), T tile(s) drawn, H hidden, X triangles
[wmo-liquid] types ...
[wmo-liquid] escape total ... yd over N surface(s), worst ... yd
```
> 

*Why: `escape` recomputes at runtime the exact metric that settled the MLIQ
coordinate convention offline against 235 groups. It should reproduce those
figures. **A wildly larger number means the instance transform is wrong, not the
convention** — the convention is settled and is not the thing to doubt.*

**H3 — the substance check, and it is the one that matters.** Video Options →
Water, with `Draw WMO liquid` on. Read the `types ...` line **in Stormwind**,
then **in Ironforge**.
> Stormwind: 
> Ironforge: 

*Why: Stormwind must be `water` only, Ironforge `magma` only. MLIQ's type codes
are NOT the codes `water.frag` routes on, and **three of the six agree by
coincidence** — so a test in Stormwind passes whether or not the translation is
right. Ironforge is the discriminating case. PLAN_15 §4.5.*

**H4 — the canal.** Stand on the Trade District canal bridge. Is there water in
the canal, at the height of the canal walls? Screenshot if it looks wrong.
> 

**H5 — submersion.** Walk down into the canal until the eye goes under. Does the
underwater overlay appear, and clear on the way out? Then stand on the bridge
**above** it — the overlay must NOT fire.
> 

*Why: `TryGetSurface` changed shape this session — it now takes the eye Z and
returns the lowest surface above it instead of the first hit it happens to find.
H5 is the test of both halves.*

**H6 — the silent one.** Walk out of Stormwind and back in. Is the canal still
there?
> 

*Why: WMOs are placed several frames before their groups are adopted, so a
rebuild fired at the tile crossing produces a permanently dry canal with no
exception and no log line. It is guarded by a version counter; H6 is the only
thing that proves the guard works.*

**H7 — depth, the known stand-in.** Does the canal edge look wrong where it meets
the wall? The `WMO liquid depth` slider is right there — what value looks best?
> 

*Why: WMO pools get one constant depth because there is no terrain under them to
subtract (PLAN_15 D3). If a value clearly reads better than 3.0, bake it. If the
edge looks bad at every value, that is the signal to spend the raycast.*

**H8 — A/B.** Toggle `Draw WMO liquid` off. Is everything else exactly as before?
> 

---

## G. Direction

**G1.** After water, what do you want next? My read, in order:

1. **PLAN_10 portal traversal** — the biggest remaining lever for indoor
   correctness and the Stormwind courtyard keep. D1's instrument is built,
   traversal is not. Experimental; §3.35 warns it trades popout. **C1 upgraded
   this**: §5A.20 found WMO is 72-86% of GPU time and named portal culling, not
   the swap chain, as the next lever — so this is now the performance item too.
2. ~~**WMO liquid (MLIQ)**~~ — **BUILT 2026-08-12, PLAN_15, draw-only** (the
   2026-07-26 build was reverted; the rebuild draws but does not wire
   submersion). See section H and SYSTEM_WATER.md §7.
3. **Streaming**, if C1 says something decisive.
4. **P2 networking** — the actual project goal, and nothing above is a
   prerequisite. Everything above is polish on a world that already renders.
> 

**G2.** Anything that annoys you daily that I have not mentioned?
> 
