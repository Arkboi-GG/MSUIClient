# Empirical checks — questions only a running client can answer

2026-07-25. Answer inline and hand it back; each answer changes what gets built
next, and I've said which. **Skip anything that's a hassle** — partial is fine,
and the ones marked ★ are worth more than the rest.

---

## A. Does it still build and run? ★

The whole session is uncompiled — no .NET SDK in my sandbox.

**A1.** Does it build? If not, paste the first 3 errors verbatim.
> 

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
> 

*Why: §5A.19 states the fork exactly. A frame with GPU back under 1 ms that
**also** reads <1 M/ms means §5A.16's zero-work stall is the same blocked wait and
the swap chain is the suspect. If it reads 4–5 M/ms, there are two bugs and
§5A.18 only killed one. **This decides whether the next streaming work is swap
chain or scheduling** — and the handbook says not to write streaming code before
reading it.*

**C2.** What is your monitor's refresh rate?
> 

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

## G. Direction

**G1.** After water, what do you want next? My read, in order:

1. **PLAN_10 portal traversal** — the biggest remaining lever for indoor
   correctness and the Stormwind courtyard keep. D1's instrument is built,
   traversal is not. Experimental; §3.35 warns it trades popout.
2. **WMO liquid (MLIQ)** — Stormwind canals, fountains, indoor pools. Parsed,
   drawn nowhere. Very visible, well-bounded.
3. **Streaming**, if C1 says something decisive.
4. **P2 networking** — the actual project goal, and nothing above is a
   prerequisite. Everything above is polish on a world that already renders.
> 

**G2.** Anything that annoys you daily that I have not mentioned?
> 
