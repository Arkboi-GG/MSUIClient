# Plan 09 — Exterior lighting from the authored data (sky, fog, ambient, sun)

**The last invented system.** Foliage stopped guessing when it followed
`GroundEffectTexture.dbc`; interiors stopped guessing when they followed MOCV and
MODD. Exterior lighting is still entirely hand-authored constants in
`World/WorldAtmosphere.cs`, and vanilla has a complete authored chain for it that
this client has never opened.

Grounded in the real source: `World/WorldAtmosphere.cs` (all of it),
`Engine/ClientWindow.cs:65, 494-497`, `Program.cs:1534`, `Formats/DbcReader.cs`,
and the 1.12 schemas for `Light.dbc`, `LightParams.dbc`, `LightIntBand.dbc`,
`LightFloatBand.dbc`.

---

## 1. Problem

Nico, this session:

> "fog isn't really visible/useful as we have it (yes I can tune it), but it's
> hard to understand because the sky and overall exterior colour seem off, so I
> can't judge correctly."

**That sentence sets the build order and is the most useful thing in this plan.**
Fog is not an independent knob that happens to be mistuned — in vanilla the fog
colour *is a sky band*, and fog's job is to dissolve distant geometry **into the
sky**. Against a sky that is one flat wrong colour, correct fog is
indistinguishable from incorrect fog: both read as a grey wall at a distance.
So fog cannot be judged, let alone tuned, until the sky is right. Any plan that
tunes fog first is tuning against a broken reference.

Three concrete defects, in dependency order:

**1.1 — There is no sky.** `ClientWindow.HandleRender` clears the framebuffer to
a single colour (`ClientWindow.cs:494-497`) and `WorldAtmosphere.SkyColor` is
literally `=> FogColor` (`WorldAtmosphere.cs:56`). No gradient, no horizon band,
no sun disc, no clouds, no skybox. Vanilla's sky is a **five-band vertical
gradient** plus a skybox model.

> The flat sky was a deliberate trade, and the reason is on the line above it:
> *"Sky colour matches the far fog so the visibility boundary disappears into
> aerial perspective instead of ending in a hard silhouette."* That trick is
> exactly what a real sky makes unnecessary — the authored data already gives fog
> (band 7) a colour that sits against the horizon bands. **Do not delete the
> trick before the replacement works, or the far clip becomes a visible edge.**

**1.2 — Every colour is invented.** `DayFog`, `NightFog`, `DayAmbient`,
`NightAmbient`, `NoonSun`, `HorizonSun`, and the intensity curves are constants
with a header comment recording a by-eye tuning pass on 2026-07-23. That pass
fixed a real problem (the world read blue and clipped to white) by taste, because
no yardstick was available. One exists.

**1.3 — Fog distances are invented.** `FogStart = 350`, `FogEnd = 777`, tuned by
hand and used for culling as well as for fog.

## 2. Class

**Emulation-core.** The real 1.12 client renders these zones on this hardware
from data files we already ship, so there is an external right answer and we are
not inventing a target.

Stronger than usual, and this is the plan's biggest advantage: **the yardstick is
numeric, not photographic.** For most emulation-core work the reference is
`refs/<vantage>.png` and the comparison is by eye. Here, the authored answer for
"what colour is the ambient light at this position at this time" is a number in a
DBC we can read. A screenshot is still needed for the sky's *shape* — where the
bands sit on the dome — but every colour and distance is checkable exactly.

> `refs/` currently contains only `README.md`. Real-client captures are needed
> for §7's visual steps and **nothing else in this plan is blocked on them.**

## 3. Target

**Numeric:** at a given position and time, the applied ambient, diffuse, fog
colour, fog start and fog end equal the values the DBC chain resolves to, within
float error. Not "close" — equal, because both sides are the same number.

**Visual:** a real-client capture of Northshire at noon, sunset and midnight,
matched on sky gradient, horizon colour and the distance at which terrain
dissolves into it.

**Felt:** Nico can judge fog, because the thing fog blends into is finally right.

## 4. Key design decisions

Ranked by how much each one buys.

**D1 — Build the light probe before changing a single rendered pixel.**
The template's first hard rule: if you cannot fill §7, the instrument is the real
task. We cannot currently answer "what does the data say here, right now" at all.
Everything else in this plan is unfalsifiable until we can. See §6.

**D2 — The sky is geometry, and it comes before fog.**
Per §1, fog is unjudgeable against a flat sky. Build the five-band gradient
(`LightIntBand` 2-6: top, middle, band1, band2, smog) as an actual dome or
fullscreen gradient drawn before the world, then let fog (band 7) blend into it.
Doing these in the other order wastes the fog work.

**D3 — Blend zone lights by falloff, do not snap to the nearest.**
`Light.dbc` rows carry a position and `falloffStart`/`falloffEnd`. Inside
`falloffStart` the zone's light applies fully; between start and end it blends
toward whatever is underneath; a row at position `0,0,0` with radius `0` is the
**map-wide default**. Elwynn, Stormwind and the default all overlap around
Northshire, so snapping would pop at zone edges — the same class of bug as
`ResetPlacements` popping at tile edges.

**D4 — Interpolate the bands over time, and wrap at midnight.**
Each band holds up to **16 `(time, value)` keys**, `numEntries` says how many are
real, and time is **half-minutes from midnight, 0-2880**. Interpolate linearly
between adjacent keys and wrap from the last key back to the first across
midnight. A band with `numEntries == 0` has no authored answer and must fall back
explicitly rather than silently reading as black.

**D5 — Units: positions and fog distances are stored x36.**
`Light.dbc` position and `falloffStart`/`falloffEnd`, and `LightFloatBand` band 0
(fog end), are **yards x 36**. Getting this wrong yields either a fog wall in
your face or fog past the far plane, and both look like "the data is wrong"
rather than like a unit bug. Convert once, at the reader boundary, and say so in
the type.

**D6 — Fog start is a multiplier, not a distance.**
`LightFloatBand` band 1 is a 0-0.999 scaler: `fogStart = fogEnd * multiplier`.
Our current model has two independent yard values. Keep the derived form so the
authored relationship survives.

**D7 — Expect the world's scale to change, and expect it to cost.**
`VisibilityDistance => CullAtFogEnd ? Max(100, FogEnd)` (`WorldAtmosphere.cs:62`)
feeds doodad draw distance and the residency radius. **If the authored fog end is
materially larger than 777, draw distance grows and streaming cost grows with
it** — `SYSTEM_STREAMING.md` §4 records the residency radius as
`draw + half tile diagonal + 50 = 727 yd` at 300 yd draw. This plan can therefore
move performance, and that must be measured, not discovered. See §10.

**D8 — Out of scope, explicitly, so this plan can finish.**
Skybox M2 models (`lightSkyboxID` -> `LightSkybox.dbc`), clouds
(`LightIntBand` 9-12 + cloud density), `highlightSky`, `glow`, weather variants
(`ParamsStorm`, `ParamsClearWat`) and the death-zone params. Read them, record
them in the probe, apply none of them. Each is a follow-up with its own visual
target.

**D9 — Water colours are in this table but not in this plan.**
`LightIntBand` 13-16 are ocean and river close/far colours, which
`SYSTEM_WATER.md` currently invents. That is a real finding and a real follow-up.
Surface them in the probe so the size of the discrepancy is visible, change
nothing in `LiquidRenderer`.

## 5. Resources

**Check these before writing anything** — the handbook records writing from
scratch what already existed, twice.

| Resource | Why |
|---|---|
| `Formats/DbcReader.cs` `DbcFile` | WDBC parsing, `GetUInt`/`GetInt`/`GetString` already exist. The new tables are row structs, not a new reader |
| `Formats/DbcReader.cs` `GroundEffectTextureTable` | The pattern for a typed table with a `MpqPath` const and an id lookup. Copy its shape |
| `World/FoliageRenderer.cs` DBC load path | How a system loads its tables off the MPQ mount at startup and degrades when they are missing |
| `World/WorldAtmosphere.cs` | Becomes a consumer of resolved data instead of the source of constants. `Evaluate()` keeps its shape and its HUD sliders keep working as multipliers |
| `Engine/ClientWindow.cs:65, 494-497` | The clear-colour sky and the comment explaining why it matches fog (§1.1) |
| `Program.cs:1534` | `_window.SkyColor = _atmosphere.SkyColor` — the one wire to replace |
| `SYSTEM_FOLIAGE.md` §0, §1.1-1.3 | The precedent: authored data beats a plausible-looking derivation, and the road test that proved it |
| `SYSTEM_WATER.md` | D9. Its colours have an authored answer in the same table |
| `SYSTEM_STREAMING.md` §4 | D7. Draw distance feeds the residency radius; fog end feeds draw distance |
| wowdev.wiki `DB/Light`, `DB/LightParams`, `DB/LightIntBand`, `DB/LightFloatBand` | Schemas, verified 2026-07-25. Recorded in §11 so they are not re-fetched |

## 6. Tools / instrument

**None of the existing instruments can isolate this, so the instrument is task
one.** The scene dump records what we *applied*; nothing records what the data
*says*, so "the sky is off" cannot currently be stated as a discrepancy.

**The light probe** — a HUD panel and a `dumps/` block that answers, for the
player's current position and time:

1. **Which `Light.dbc` rows are in range**, with each row's id, distance,
   `falloffStart`/`falloffEnd` in yards, and its computed blend weight. The
   map-wide default listed explicitly as the base.
2. **The resolved `LightParams`** id and its fields, per contributing light.
3. **All 18 int bands and all 6 float bands**, evaluated at the current time,
   per light and after blending — named, not numbered.
4. **What we are actually using**, in a column beside it.

Point 4 is the instrument. Two columns, `data` and `applied`, on one line each,
so every question in this plan becomes a subtraction instead of an opinion.

A time scrubber and the existing `SetDay`/`SetSunset`/`SetNight` buttons make the
whole 24-hour curve inspectable without waiting for it.

> Building the probe first also de-risks D5 and D4 completely: a unit error or a
> wrapping error shows up as an absurd number in a readout, rather than as a
> rendering result somebody has to interpret.

## 7. Test protocol

Written before any code, per the template.

**The instrument (must pass before it is trusted):**

1. Stand in Northshire. The probe names a specific `Light.dbc` row for Elwynn,
   **not** the map default, and reports a fog end in plausible yards after the
   x36 conversion. An instrument that reports the default everywhere has a
   position or unit bug, not a finding.
2. Scrub time 0 -> 24 h. Every band moves smoothly, and **crossing midnight
   produces no discontinuity** (D4's wrap).
3. Walk Elwynn -> Stormwind. Blend weights cross over monotonically within
   `falloffStart..falloffEnd`, summing to 1 at every step. No snap.
4. Set a band's `numEntries` case deliberately (find a light with an empty band):
   the probe says "no authored value", not `(0,0,0)`.

**The defect:**

5. At noon in Northshire, `applied` ambient and diffuse equal `data` bands 1 and
   0. Any difference is either a bug or a deliberate multiplier, and the HUD
   sliders make that distinction visible.
6. Capture `refs/northshire-noon.png` from the real client at the same spot and
   time. Compare sky gradient, horizon colour, and the distance at which terrain
   dissolves. Repeat at sunset and midnight — **sunset is the hard case**,
   because that is where the invented `HorizonSun` lerp diverges most from the
   authored curve.
7. Re-measure streaming after the fog distances change (D7): one crossing at
   `[32,48] -> [32,49]` with the hitch recorder at 25 ms, diffed against the
   current numbers in `SYSTEM_STREAMING.md`.

## 8. Definition of done

**The instrument:** steps 1-4 pass, and the probe can be pointed at any spot to
say what vanilla intends there.

**The plan's real output:** sky, ambient, diffuse, fog colour and fog distances
all sourced from the chain; steps 5-6 pass; and `SYSTEM_EXTERIOR_LIGHTING.md`
extracted with the measured before/after, following the one-system-one-doc rule.

Explicitly **not** in scope: D8's list, and D9's water colours. If the sunset
comparison shows the gradient needs a skybox model to match, that is Plan 10 and
it gets its own template.

## 9. Fallback

If the rendering work proves large, **the probe alone is already a win**: it
converts "the sky seems off" into "authored ambient at noon in Elwynn is X, we
are applying Y." That single readout tells us how wrong the constants are and
whether the remaining work is a tune or a rewrite — and it is perhaps a day of
work with no rendering risk at all.

Next smallest win after that is **fog distances only** (D5/D6, two floats),
because it needs no new geometry and is immediately visible. Do **not** stop
there and call fog done — per §1, fog cannot be judged until the sky lands.

If the five-band gradient proves fiddly, a **two-band** horizon-to-zenith lerp
using bands 2 and 6 is still enormously better than a flat clear colour, and it
proves the pipeline end to end.

## 10. Reconciliation

- **`WorldAtmosphere`** stops being the source of truth and becomes the
  evaluator. Its constants survive only as the explicit fallback for missing
  data, and the HUD `SunStrength`/`AmbientStrength` sliders become multipliers on
  authored values rather than on invented ones.
- **`ClientWindow.SkyColor` and the clear-colour trick** retire once D2 lands —
  but only then, per §1.1's warning about the far-clip silhouette.
- **`SYSTEM_STREAMING.md` §4** gains a dependency note: draw distance derives
  from fog end, which now comes from data. If the authored value is much larger
  than 777, the 727 yd residency radius grows with it and PLAN_08's unbuilt D2
  (budgeted resumable adoption) becomes more urgent, not less. **This plan can
  regress streaming performance; §7 step 7 exists to catch that.**
- **`SYSTEM_WATER.md`** gains a note that bands 13-16 are the authored answer for
  its invented ocean and river colours (D9).
- **`PROJECT_HANDBOOK.md` §1.2** gains `SYSTEM_EXTERIOR_LIGHTING.md` in the
  per-system index.
- **Plan 02 (scene dump)** gains the probe block, so a dump taken anywhere
  carries the authored lighting answer for that spot.

## 11. Schemas — verified 2026-07-25, do not re-derive

```
Light.dbc
  0 id   1 mapId   2-4 position XYZ (x36)   5 falloffStart (x36)   6 falloffEnd (x36)
  7 ParamsClear   8 ParamsClearWat   9 ParamsStorm   10 ParamsStormWat   11 ParamsDeath
  (position 0,0,0 with radius 0 = map-wide default)

LightParams.dbc
  0 id   1 highlightSky   2 lightSkyboxID   3 glow
  4 waterShallowAlpha   5 waterDeepAlpha   6 oceanShallowAlpha   7 oceanDeepAlpha   8 flags

LightIntBand.dbc    rows for a LightParams id: id*18-17 .. +17
  0 id   1 numEntries (0-16)   2-17 times (half-minutes, 0-2880)   18-33 colours

  band  0 global diffuse      1 global ambient
        2 sky top             3 sky middle      4 sky band 1
        5 sky band 2          6 sky smog        7 fog / background mountains
        8 sun                 9 cloud sun      10 cloud emissive
       11 cloud L1 ambient   12 cloud L2 ambient
       13 ocean close        14 ocean far      15 river close   16 river far
       17 shadow opacity

LightFloatBand.dbc  rows for a LightParams id: id*6-5 .. +5
  0 id   1 numEntries (0-16)   2-17 times   18-33 floats

  band  0 fog end (x36)      1 fog start multiplier (0-0.999; start = end * mult)
        2 celestial glow through   3 cloud density   4-5 unknown
```

Sources: wowdev.wiki [DB/Light](https://wowdev.wiki/DB/Light),
[DB/LightParams](https://wowdev.wiki/DB/LightParams),
[DB/LightIntBand](https://wowdev.wiki/DB/LightIntBand),
[DB/LightFloatBand](https://wowdev.wiki/DB/LightFloatBand).

**Field indices above are from the wiki, not from our data.** Step 1 of §7 is
also the check that they are right for 1.12.1.5875 specifically — a shifted
column reads as plausible-but-wrong colours, which is the hardest kind of bug to
see. `DbcFile.RecordCount`/`FieldCount`/`RecordSize` are printed by the probe on
load precisely so a schema mismatch is caught at the door rather than inferred
from a strange sunset.
