# Plan 12 — Authored water colours (LightIntBand 13-16 + LightParams alphas)

Status: **specified, then built behind an A/B switch whose OFF state is
bit-identical to today's look.** Written before the code.

## 1. Problem

`LiquidRenderer`'s colours are hand-tuned constants and the authored answer has
been sitting resolved, blended and on screen since 2026-07-25 without being used.

From `water.frag`, the procedural path, in full:

```glsl
if (ocean) { shallowCol=vec3(0.06,0.20,0.28); deepCol=vec3(0.02,0.09,0.16); baseAlpha=0.90; }
else       { shallowCol=vec3(0.10,0.26,0.26); deepCol=vec3(0.05,0.15,0.16); baseAlpha=0.85; }
vec3 body = mix(shallowCol, deepCol, depthFade);
```

Six invented numbers per type, blended by depth. `LightIntBand` bands **13 ocean
close, 14 ocean far, 15 river close, 16 river far** are exactly that pair of
colours per type, and `LightParams` carries exactly that alpha pair —
`OceanShallowAlpha` / `OceanDeepAlpha` / `WaterShallowAlpha` / `WaterDeepAlpha`.
All eight are parsed, resolved per zone and per time of day, blended across
contributing zones, and printed by the light probe. **`LiquidRenderer` has never
been told.**

The textured path — the shipped one — is worse off, not better: it has a single
global `uTexTint` with no type routing and no depth ramp at all, and a single
`uOpacity`/`uShoreFade` pair for ocean, river and lake alike.

**From a vantage:** at `looking at green river water`, open the light probe. The
`river close` / `river far` line and the water on screen are two different
answers to the same question, and the one on screen is the one somebody guessed.

## 2. Class

**Emulation-core.** The yardstick is the real 1.12 client, and for once the
comparison does not need a screenshot to start: the authored values are numbers
we already have, and the current values are numbers in a shader. The photographic
check is still owed (§8) but the numeric one can run today.

This is the third time this move has been made. `GroundEffectTexture` beat the
alpha-derived guess for foliage; `Light.dbc` beat the by-eye ambient retune for
exterior lighting — and §5.3 of SYSTEM_EXTERIOR_LIGHTING records that the retune
it replaced *was not careless, it was just guessing*. Same shape here.

## 3. Target

- Ocean, river and lake take their close/far colours and shallow/deep alphas
  from the resolved light data, per zone and per time of day, on both the
  textured and the procedural path.
- Slime and magma are **untouched**. They are self-luminous, they have no
  authored bands, and their path returns before any of this.
- One switch, `Use authored water colours`, whose OFF state is **bit-identical**
  to the current look — see H3.

## 4. Key design decisions

**H1 — the transport is `WorldAtmosphere`, not a second path from the probe.**
`SetAuthored` already carries the resolved sky and fog from `UpdateExteriorLighting`
into the one object every renderer reads. Water colours ride the same call and
the same `UseAuthoredData` gate. A second wire from `ExteriorLighting` straight to
`LiquidRenderer` would be a second source of truth for "what does the data say
here", and would skip the gate.
*Falsifiable:* turn `Use authored lighting data` off; the water must fall back
with the sky, not independently.

**H2 — the textured path MODULATES, it does not replace.**
SYSTEM_WATER.md Draft 2's reversal is the governing fact: 1.12 water is a dark,
near-opaque **textured** surface and the motion is the texture scrolling. The
authored colour is a tint on that texture, exactly where `uTexTint` already
multiplies, plus a depth ramp that `uTexTint` never had. **If this change makes
the animated shimmer disappear, it has been implemented as a replacement and is
wrong.**

**H3 — OFF must be bit-identical, not "close".**
The switch feeds the shader as `uAuthoredWater` 0 or 1 and every new term is
`mix(<today's expression>, <authored expression>, uAuthoredWater)`. At 0 the
arithmetic reduces to what ships today. That is what makes it safe to land
without being able to run it, and it is what makes the A/B trustworthy rather
than approximately trustworthy.

**H4 — the river triplet looks wrong and must be checked BEFORE it is believed.**
Measured for Azeroth's map default at noon (SYSTEM_WATER.md §5):

| band | value | reads as |
|---|---|---|
| 13 ocean close | `0.380 0.510 0.718` | light sky-blue — **plausible shallow ocean** |
| 14 ocean far | `0.067 0.294 0.349` | dark teal — **plausible deep ocean** |
| 15 river close | `0.000 0.114 0.161` | near-black blue — dark for *shallow* |
| 16 river far | `0.310 0.365 0.078` | olive-yellow-green — **bright for *deep*** |

Ocean behaves the way water behaves: light shallow, dark deep. **River is
inverted and the far colour is yellow-green.** Three candidate explanations, and
they are distinguishable:

1. It is real. Vanilla's Elwynn river is a murky green and a bright olive deep
   band is not impossible once the texture multiplies it down.
2. `SwapRedBlue` is right for one and wrong for the other — impossible, it is one
   global flag over one table, but the *check* it encodes (sky top at noon must
   be blue) never exercised bands 13-16. Swapped, river far becomes
   `0.078 0.365 0.310`, a green-teal, which is **more** plausible; ocean close
   becomes `0.718 0.510 0.380`, an orange, which is much **less**. So a global
   swap cannot fix river without breaking ocean.
3. Close/far are transposed for river in our band mapping, or these particular
   Azeroth-default rows are unauthored and we are reading a neighbour.

`ColorBandAuthored(paramsId, band)` already answers (3) directly and the probe
already shows it. **§7 step 1 is to read it before anything else.**

**H5 — depth drives both the colour ramp and the alpha ramp, from the same term.**
`water.frag` already computes `tdepthFade = 1 - exp(-vDepth * uDepthRate)` on the
textured path and `depthFade` on the procedural one. Reuse it for both rather
than introducing a second notion of depth; two depth curves that disagree is a
bug waiting for a vantage.

**H6 — the existing tuning sliders stay, as multipliers.**
Exactly what PLAN_09 did to the atmosphere sliders. `uTexBright`, `uOpacity`,
`uDepthDarken` and friends keep working on top of the authored base, so a
by-eye session is still possible and `Adopt live` still captures it. What changes
is what they multiply: a datum instead of a guess.

## 5. Resources

| Source | Gives |
|---|---|
| `Formats/DbcReader.cs:859-867` | `LightIntBandTable.BandNames` — the 18-band mapping, 13-16 named |
| `Formats/DbcReader.cs:649-652, 687-690` | `LightParams` water/ocean shallow/deep alphas, already parsed |
| `World/ExteriorLighting.cs:221-247` | `Sample.Colors[]` — bands are already blended across contributing zones |
| `World/WorldAtmosphere.cs` `SetAuthored` | The transport, and the `HasAuthored` / `UseAuthoredData` gate to extend |
| `Shaders/water.frag` | `uTexTint` (the modulation point), `tdepthFade`, and the six invented constants in the fallback |
| `Program.LightProbe.cs:~305` | The probe rows that already print all four bands and both alpha pairs |
| `SYSTEM_WATER.md` §5 | The measured values and the standing warning: *push those sliders no further until they have been checked against the data* |
| `SYSTEM_EXTERIOR_LIGHTING.md` §2.3, §7 | How the bands resolve; the standing "water colours" gap |
| `PLAN_09` D9 | Where this was deliberately scoped out, and why |

## 6. Tools / instrument

Already built, nothing new needed:

- **Light probe** — prints all four bands with swatches, both alpha pairs, and
  `(unauthored)` per band. It is the whole of §7 step 1.
- **Water page, Video Options** — every knob live, plus the new switch.
- **Vantages** — `looking at green river water` exists and is the river case.
  There is **no saved ocean vantage**; §7 step 4 says to make one.
- **Scene dump** — records the toggle state, so an A/B pair is two dumps.

## 7. Test protocol

1. **Settle H4 first, before looking at any water.** At `looking at green river
   water`, open the probe. Read the four band rows and whether each says
   `(unauthored)`. Paste the block. If river close/far are unauthored, we are
   reading a neighbour's row and the mapping is what needs fixing, not the shader.
2. **Bit-identity of OFF.** Switch `Use authored water colours` off, dump, on,
   dump, off, dump. Dumps 1 and 3 must be identical. If they are not, H3 failed
   and the mix() is not reducing.
3. **The river case.** Same vantage, toggle on. The water must stay a *textured,
   animated, near-opaque* surface (H2). If the shimmer goes flat, stop — it has
   been implemented as a replacement.
4. **The ocean case.** Fly west from Westfall to open ocean, save a vantage
   `looking at open ocean`, and repeat 3. Ocean is the type whose authored values
   look right, so it is the one that should visibly improve.
5. **Time of day.** Cycle noon → sunset → night with the switch on. The water
   colour must move with the sky, because the bands are time-interpolated. Water
   that stays put while the sky changes means the resolve is not reaching it.
6. **The deep-water complaint.** SYSTEM_WATER.md §5 opens with *"deep water still
   reads a little dark"* and warns not to push the sliders until the alphas are
   checked. With authored alphas on, re-judge it. If it is fixed, that entry gets
   deleted rather than tuned.

## 8. Definition of done

- Steps 1-5 pass and step 1's probe block is pasted into SYSTEM_WATER.md.
- H4 is resolved one way or the other **in writing**. "It looked fine" is not a
  resolution for a value that reads inverted.
- SYSTEM_WATER.md §5's two water-colour entries and
  SYSTEM_EXTERIOR_LIGHTING.md §7's are struck, or the reason they cannot be is
  recorded.
- **Not done until a `refs/` ocean and river capture exists.** Numeric agreement
  with the DBC is not photographic agreement with the client. Same honesty
  SYSTEM_EXTERIOR_LIGHTING.md applies to its band heights.

## 9. Fallback

The switch. OFF is today's look exactly (H3), so the worst case is one unticked
box and a note in this file. Nothing else in the client reads the new fields.

## 10. Reconciliation

- **PLAN_09 D9** is discharged by this plan. Its "surface, change nothing"
  instruction was right at the time and stays as the record of a deliberate
  deferral.
- **SYSTEM_WATER.md** gains a §4 subsection for the authored path and loses two
  §5 entries. Draft 2's texture-is-the-look finding is **not** superseded — it is
  the constraint this plan is built around (H2).
- **SYSTEM_EXTERIOR_LIGHTING.md §7** loses its water-colours bullet.
- **`LiquidType.dbc` colours/materials**, still listed in SYSTEM_WATER.md §5,
  are a *different* table and stay open. Do not confuse them: `LightIntBand` is
  per zone and per time, `LiquidType` is per liquid. If they ever disagree, they
  are answering different questions.
- No overlap with the streaming or portal fronts.
