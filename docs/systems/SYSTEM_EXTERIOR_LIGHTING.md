# System — Exterior Lighting (sky, fog, ambient, sun)

**Where daylight, sky and fog come from, and why they are no longer invented.**
One of the per-system docs the handbook indexes (see PROJECT_HANDBOOK.md §1.2).
Read this plus the handbook's cross-cutting ground truth (§3.1 coordinates, §11
working agreements) before touching anything atmospheric. You should not need the
rest of the handbook.

Version: Draft 2 — 2026-08-12 (Draft 1 2026-07-25). Written during the session
that built the light probe and adopted `Light.dbc`; revised when the lighting
MODES landed (§4.5) and the band-0 mystery was solved (§2.3). **Every number in
this doc was read out of Nico's own MPQs by the probe** (or, for the 2026-08-12
pass, by `tools/mpqpy` against the same archives), not estimated; the ones still
open are marked as open.

Owner files: `World/ExteriorLighting.cs` (resolve), `World/WorldAtmosphere.cs`
(evaluate + apply), `World/DayNightCycle.cs` (`World\dnc.db`, the vanilla
day/night intensity table), `World/SkyRenderer.cs` + `Shaders/sky.vert|frag`
(draw), `Program.LightProbe.cs` (the instrument), the four Light tables in
`Formats/DbcReader.cs`.

---

## 0. The bar

Vanilla has a **complete authored lighting chain** — which light applies where,
what colour every part of the sky is at every minute of the day, and how far fog
reaches. The bar is to follow it, exactly as foliage follows
`GroundEffectTexture.dbc` (SYSTEM_FOLIAGE.md §0).

This is **emulation-core** by FOUNDATION_PLAN §2, and unusually well-conditioned:
the yardstick is a **number in a file**, not a screenshot. "Is the ambient right"
is a subtraction. Only the sky's *band heights* and the sun's *direction* remain
matters of judgement, and both are called out below.

---

## 1. The chain

```
Light.dbc            which lighting setup applies where, and how it fades
  -> LightParams.dbc       one setting-set per weather state
     -> LightIntBand.dbc     18 colour curves over the day
     -> LightFloatBand.dbc    6 scalar curves over the day
```

Measured shapes in Nico's 1.12.1.5875 data:

```
[dbc] Light:         374 record(s), 12 field(s),  48 bytes; 374 zones, 19 map defaults
[dbc] LightParams:   426 record(s),  9 field(s),  36 bytes
[dbc] LightIntBand: 7668 record(s), 34 field(s), 136 bytes; 6695 bands with keys
[dbc] LightFloatBand:2556 record(s), 34 field(s), 136 bytes; 2424 bands with keys
```

`7668 = 426 x 18` and `2556 = 426 x 6` **exactly**. That arithmetic is the proof
the row mapping is right, and it is cheaper than any other check — verify it
first if anything ever looks wrong.

Band rows for LightParams `P`: int bands `P*18-17 .. P*18`, float bands
`P*6-5 .. P*6`. Looked up **by id**, not by row index; the two coincide today and
relying on that is exactly the assumption that breaks quietly.

---

## 2. Ground truth — do not re-derive

| Fact | Value |
|---|---|
| Positions, falloff radii, fog end | stored **x36**. Undone in `DbcReader` at the boundary |
| Band times | **half-minutes from midnight, 0..2880**. Converted to hours at the boundary |
| Keys per band | up to **16**; `numEntries` says how many are real |
| Curves | **wrap** — last key to first crosses midnight |
| Map default | a `Light` row at position `0,0,0` with radius `0`. Azeroth's is **light 1 -> params 12** |
| Fog start | **not a distance** — `LightFloatBand` band 1 is a 0..0.999 multiplier; `start = end * mult` |
| Colour packing | `0x00RRGGBB`. Confirmed by sky top reading blue at noon |
| dbc -> world | `X = 17066.666 - dbc.Z`, `Y = 17066.666 - dbc.X`, `Z = dbc.Y` |

### 2.1 The coordinate convention, and how it was settled

The DBC stores positions **Y-up and in positive map space**, ours are Z-up and
centred. The zone extent gave it away in one line:

```
80 zone(s) RAW  X 3200..32800   Y -234..436   Z 13208..32800
player at      (-8950, -132, 84)
```

`Y` is the small axis, so `Y` is height. The horizontal range sits inside
`0..34133` = 64 tiles x 533.33, i.e. the map in positive space where ours is
centred on +/-17066.

Six candidate mappings were scored against the player's position by
`ExteriorLighting.ScoreConventions`. The winner was 6.4x clearer than the
runner-up, but the *proof* is better than the margin:

> **Light 77 is at raw `(16488, 0, 25868)`, which maps to world
> `(-8801, 579)`. That is Stormwind City, within ~20 yards of where it actually
> stands.** A wrong convention cannot land a named landmark on itself.

`DetectConvention` runs once on the first frame with a real position, applies the
result and logs its margin. Under 2x it warns that the test did not decide.

### 2.2 The 18 colour bands

| # | Meaning | # | Meaning |
|---|---|---|---|
| 0 | global diffuse | 9 | cloud sun |
| 1 | global ambient | 10 | cloud emissive |
| 2 | sky top (zenith) | 11 | cloud L1 ambient |
| 3 | sky middle | 12 | cloud L2 ambient |
| 4 | sky band 1 | 13 | ocean close |
| 5 | sky band 2 | 14 | ocean far |
| 6 | sky smog (horizon) | 15 | river close |
| 7 | fog / background mountains | 16 | river far |
| 8 | sun | 17 | shadow opacity |

The 6 float bands: **0** fog end (x36), **1** fog start multiplier, **2**
celestial glow-through, **3** cloud density, **4-5** unknown.

> These names are what the code uses (`LightIntBandTable.BandNames`) and slots
> 8–17 are **suspect** since 2026-08-12 — noggit/wowdev name that range
> differently (see the end of §2.3). Slots 0–7 are confirmed.

### 2.3 Azeroth's default light at noon, as measured

```
 0 global diffuse   1.000 0.533 0.000      7 fog             0.302 0.471 0.561
 1 global ambient   0.408 0.510 0.604      8 sun             0.302 0.302 0.302
 2 sky top          0.000 0.122 0.286     13 ocean close     0.380 0.510 0.718
 3 sky middle       0.227 0.635 0.812     14 ocean far       0.067 0.294 0.349
 4 sky band 1       0.600 0.863 0.961     15 river close     0.000 0.114 0.161
 5 sky band 2       0.686 0.855 0.878     16 river far       0.310 0.365 0.078
 6 sky smog         0.706 0.706 0.706     f0 fog end         500.000 yd
                                          f1 fog start mult    0.250  -> 125 yd
```

Ambient keys: `0h, 3h, 6h, 12h, 21h, 22h`. The noon sample lands exactly on the
12h key, which is why it can be checked against the raw key list by eye.

> ~~**Bands 0 and 8 look wrong and probably are not ours to fix.**~~
> **SOLVED, 2026-08-12** — investigated with `tools/mpqpy` against the real
> archives (`Light.dbc` lives in `patch.MPQ`, not `dbc.MPQ`). Three hypotheses
> were tested and all three of the "our decode is broken" ones are FALSE:
>
> 1. **The coordinate convention is right.** All six candidates were scored
>    against Northshire `(-8913,-137)`, Stormwind `(-8913,505)`, the gates
>    `(-8400,600)` and Goldshire `(-9450,60)`. Only the shipped winner
>    (`X=H-z, Y=H-x`) produces ANY containment — light 77 (params 79, reach
>    418..495 yd) lands 134 yd from Stormwind centre, lights 51/52 (params 62)
>    on the gates. Every other convention covers nothing anywhere.
> 2. **The channel order is right.** Sky bands 2–6 decode blue-dominant at noon
>    with `0x00RRGGBB`; so does the coastal params-14 diffuse `0x82b9de`.
> 3. **Band 0 really is the exterior diffuse, and really is `0xFF8800` at noon**
>    for the Azeroth default (keys `6h 0xff6700, 12h 0xff8800, 21h 0xff6d00`,
>    night `0x6182a2`). Stormwind's own params 79 has the SAME noon value.
>
> The missing piece was never in `Light.dbc`: **the vanilla client multiplies
> band 0 by the day/night intensity curve in `World\dnc.db`** (misc.MPQ), the
> classic-only day-night table (removed in TBC). Its 24 hourly rows carry
> `DayIntensity` (0 at night, ramping to **0.8** through 6h–19h),
> `NightIntensity` (1.0 at night), the two light directions, and grey
> day/ambient/fog ramps — the same combination the vanilla-era open renderers
> (wowmapview lineage) used: `diffuse = band0 * dayIntensity`. That is what the
> 1.12 Parity mode reproduces (§4.5); MSUI mode keeps applying band 0 raw.
>
> Band 8's flat grey (`0x4d4d4d` noon) stays open: per noggit/wowdev the
> 18-band layout is `8 = unknown, 9 = sun+halo, 10 = larger sun halo,
> 12 = clouds, 14..17 = ocean light/dark, river light/dark` — which does NOT
> match our §2.2 names (we call 8 "sun" and read water from 13–16). Band 9
> decodes to a plausible warm-white sun `(1.00,0.97,0.87)` at noon, band 13 to
> a blue-grey that noggit calls unknown. **Suspected off-by-one in our water
> bands** — see §7.

---

## 3. What actually applies where

**Zone lights are small.** Measured reaches near Northshire: **495, 250, 90, 85,
76 yards**. These are local ambience spots — Stormwind, buildings, caves — not
zone-wide lighting.

**Elwynn Forest has no dedicated `Light.dbc` row.** At Northshire the probe
resolves the map default and nothing else, and that is **correct, not a bug**.
The map default *is* outdoor Azeroth's lighting; the positioned rows are
exceptions layered on top.

This surprised the first reading and cost a round of investigation, so it is
recorded loudly: *"only the map default applies here"* is the expected answer in
open country. **Re-verified 2026-08-12** offline against all 81 map-0 rows:
under every one of the six candidate conventions, nothing covers Northshire or
Goldshire — the map default is genuinely vanilla's answer there, and the noon
look it produces is explained by the dnc.db curve (§2.3), not by a missed zone
light.

Blending is by falloff, never nearest-wins: full strength inside `falloffStart`,
linear to zero at `falloffEnd`, applied farthest-first over the map default so
the nearest zone lands last. Snapping would pop at zone edges — the same defect
class as rebuilding placements at a tile boundary.

---

## 4. What is applied, and what is still ours

> ### 4.0 ~~Known defect~~ — FIXED, 2026-07-25
>
> Found in the 2026-07-25 doc-sync pass, fixed the same day. Recorded rather than
> deleted, because the shape of it is worth keeping.
>
> `WorldAtmosphere.SetAuthored` had exactly one caller, `UpdateLightProbe`, and
> that method opened with
>
> ```csharp
> if (!_config.DevTools || !_exteriorLight.Ready) return;
> ```
>
> so in a run with `DevTools` off, `SetAuthored` was never called, `HasAuthored`
> stayed `false`, and `WorldAtmosphere`'s `Authored` gate silently routed every
> colour back to the invented constants this whole system exists to replace.
> `DetectConvention` never ran either. **A FOUNDATION_PLAN §12 seam violation,
> inverted:** the rule is *core decides, the dev layer observes*, and here the
> dev layer decided and core followed.
>
> **The fix.** `Program.LightProbe.cs` now has two halves:
>
> - `UpdateExteriorLighting()` — **core, runs in every build.** Detects the
>   convention, resolves at the **world clock**, calls `SetAuthored`.
> - the rest — `_lightSample`, which feeds `DrawLightProbePanel` and
>   `PrintLightProbe` and nothing else — stays behind `_config.DevTools`, which
>   is the half that legitimately belongs there.
>
> Pinning the probe's time still resolves a second, separate sample, so scrubbing
> to inspect a curve cannot relight the scene. Unpinned, the one answer is reused
> instead of resolved twice, which the old code did.
>
> **The switch that decides whether the renderer consumes authored data is
> `WorldAtmosphere.UseAuthoredData`. It is not the DevTools flag and must never
> become it again.** (Since 2026-08-12 it is a transient probe-panel A/B rather
> than a persisted setting — the player-facing switch is now the lighting MODE,
> §4.2, and both modes consume the authored data.)
>
> **What let it survive several readings:** the call site's comment asserted the
> opposite of the truth — *"Read-only: it feeds the probe panel and nothing
> else"* — and `Program.cs`'s note above `InitLightProbe` still said *"nothing
> applies it yet"*, which had been false since PLAN_09 landed. Both now say what
> the code does. A comment that is confidently wrong is worse than no comment,
> because it ends the search.
>
> It took the settings modal to make this bite: its Lighting page offers
> **Use authored lighting data (Light.dbc)** in exactly the build where the
> resolve was not running.
>
> Everything in the table below was measured **with DevTools on**, which is now
> the same code path as with it off.

| Quantity | Source |
|---|---|
| Ambient colour | **data**, band 1 |
| Diffuse / sun colour | **data**, band 0 |
| Fog colour | **data**, band 7 |
| Fog start / end | **data**, float bands 0 and 1 |
| Sky: 5 band colours | **data**, bands 2-6 |
| Sky: band **heights** | **ours** — three HUD sliders |
| Sun **direction** | **ours** — computed from time of day (dnc.db carries the real arc, unwired — §4.2) |
| Sun / ambient **strength** | **ours** — HUD multipliers, 1.0 = use data exactly (Parity mode additionally scales sun by the dnc.db curve — §4.2) |

Two honest gaps, both deliberate:

**`LightIntBand` gives five sky colours and never says what elevation each sits
at.** `SkyRenderer.StopMiddle/StopBand1/StopBand2` are ours, defaulted to
`0.45 / 0.18 / 0.06` so most of the gradient happens near the horizon. They are
sliders, not constants pretending to be data.

**Light.dbc carries no sun position.** `WorldAtmosphere.SunDirectionAt` computes
it from the clock — six sunrise, twelve noon, eighteen sunset. Inventing this is
honest; inventing a colour was not.

### 4.1 The sky is a screen-space pass, not a dome

`SkyRenderer` draws **one fullscreen triangle** generated from `gl_VertexID`,
before the world, with depth writes off. The sky is a function of view
*direction*, not of position, so this is exact at any FOV and any orientation
with no geometry to build, cull, or get wrong at the poles.

`ClientWindow` still clears to the fog colour underneath. That is deliberate:
disabling the sky pass restores exactly the old flat behaviour rather than
exposing a hard far-clip edge. **Do not remove the clear until the sky has been
checked against a real-client capture.**

### 4.2 Lighting modes (2026-08-12)

The old bool (`UseAuthoredData`, data vs constants) became a MODE
(`Lighting.Mode`, settings v6 — SYSTEM_SETTINGS_UI.md §3.6): **both modes
consume the authored data** and the hand-tuned constants survive only as the
no-data fallback. The seam is one multiplier in `WorldAtmosphere.Evaluate`'s
authored branch plus per-mode recommended values pushed by
`LightingSettings.ApplyLightingModeDefaults`:

| | **MSUI Lighting** (`Msui`, the default) | **1.12 Parity** (`Parity112`) |
|---|---|---|
| Colours (bands 0/1/7, sky 2–6) | authored, raw | authored, raw |
| Sun intensity | `SunStrength` only — the pre-v6 look, preserved exactly | `SunStrength ×` **the dnc.db curve** (`DayNightCycle.SunIntensityAt`) |
| Ambient intensity | `AmbientStrength` only | `AmbientStrength` only — nothing else attenuates |
| Fog distances | authored (float bands 0/1: Azeroth default 500/125 yd) | authored, as-is |
| Sun direction | the invented arc (`SunDirectionAt`) | same — seam factored in `SunDirectionFor`, dnc arc unwired |
| WMO doorway spill (`InteriorSpill`) | **1.8** recommended | **1.0** recommended |

The dnc.db curve is `max(DayIntensity, NightIntensity)` — the real client runs
sun and moon as two lights, we have one, and band 0 already colour-shifts to
moon-blue at night, so the two intensities collapse honestly to max(): 0.8
through the day, 1.0 deep night, a dip through the dawn/dusk crossover. If
`dnc.db` fails to load, Parity degrades to "no curve", i.e. Msui behaviour.

Surfaces: the combo at the top of *Video Options → Lighting and sky*, mirrored
in the probe panel (same value through `ApplyLightingModeDefaults` +
`ApplySettings`, so the two surfaces cannot disagree). Quality presets never
touch the mode.

### 4.3 The world clock — where the hour comes from (2026-08-12)

Until now `TimeOfDayHours` was a slider pinned at noon; the day/night curve
above never moved. The hour is now supplied by a **time source**
(`Lighting.TimeSource`, settings v7; pre-v7 files migrate to `Server`, or
`Cycle` if they had `CycleTimeOfDay=true` — read from the raw file, since the
key no longer exists):

- **Server** (default) — vanilla behaviour. `SMSG_LOGIN_SETTIMESPEED` (0x0042)
  carries a bit-packed game time (minute:6, hour:5, weekday:3, day:6, month:4,
  year:5 from the LSB) plus a timescale in game-minutes per real second
  (0.01666667 = real time). `World\WorldClock.cs` decodes it in the
  `Program.Net.cs` drain and advances it locally from the receive stamp; until
  a server time arrives (offline, creator mode) it follows the machine's wall
  clock rather than freezing at noon.
- **Fixed** — the pinned-hour slider, the pre-v7 behaviour.
- **Cycle** — the accelerated debug day/night (`GameHoursPerMinute`).

`UpdateWorldClock` (Program.LightProbe.cs, beside the resolve — it is CORE,
same as `UpdateExteriorLighting`) writes `WorldAtmosphere.TimeOfDayHours` once
per frame, before the Light.dbc resolve consumes it, so **both lighting modes**
(MSUI raw and Parity's dnc.db curve) light the same instant. The DevTools HUD
slider / Noon–Sunset–Night buttons and vantage restores act through a
transient **dev pin** (`_devTimePin`): the clock freezes at the chosen hour
without touching the persisted preference, and "Resume clock" hands the world
back to the source.

---

## 5. Two bugs this system produced, and what they teach

### 5.1 A packed colour is not a number

Colour bands were first sampled through the same code as scalar bands, which
interpolated the **packed** value. Lerping `0x0000FF` toward `0xFF0000` carries
across byte boundaries and lands on a colour belonging to neither key. The
symptom at 11:11 was **green ambient, cyan fog, dark-purple sun** — while every
scalar band in the same rows read perfectly.

That asymmetry *was* the diagnosis: same rows, same layout, same id mapping, so
only the value path could be wrong. `LightColorBand` now decodes both bracketing
keys and interpolates per channel. **Never reintroduce a shared sampler.**

### 5.2 An instrument that computes the answer should apply it

The convention scorer first printed a table and left a human to pick from a
dropdown. Nico's response — *"am I supposed to click through 6 things?"* — is the
correct review. It now auto-detects, applies, and reports its margin; the
dropdown remains only as an override.

### 5.3 The tuning pass was fighting the data

Handbook §3.35 records a 2026-07-23 pass that rejected a blue-biased ambient of
`(0.42, 0.50, 0.60)` as "what made the world look cool" and replaced it with warm
`(0.50, 0.46, 0.38)`. **The authored value at noon is `(0.408, 0.510, 0.604)`** —
almost exactly the value that was rejected.

The tune was not careless; it had no yardstick. That is the whole argument for
building the probe before touching a colour.

---

## 6. The instrument — the light probe

`Program.LightProbe.cs`, HUD panel *"Light probe — what the DBCs say"*. It
resolves the chain for the player's position and time and shows, in one place:
contributing zones with distances and blend weights, all 18 colours with
swatches, all 6 scalars, and a **`data` vs `applied` block with deltas**.

With `Use authored lighting` on and the strength sliders at 1.0, **every delta
should read 0.000**. That is the correctness check, and it is exact.

Also present: a time pin for scrubbing the 24-hour curve without relighting the
scene, a raw key dump so a sampled colour can be checked against its neighbours,
`Score all conventions`, `Re-detect from here`, and a console print so a reading
can be pasted into a plan instead of screenshotted.

---

## 7. Not done — the honest ceiling

- **Skybox models.** `LightParams.lightSkyboxID` -> `LightSkybox.dbc` is read and
  reported by the probe, applied nowhere. Zones with authored skyboxes
  (Blackrock and friends) will not look right until it is.
- **Clouds.** Bands 9-12 plus float band 3 (cloud density) are resolved and
  unused. There is no cloud layer at all.
- **Weather.** Only `ParamsClear` is ever read. `ParamsStorm`, `ParamsClearWat`,
  `ParamsStormWat` and `ParamsDeath` are parsed and ignored — underwater lighting
  in particular is a visible gap.
- **`highlightSky` and `glow`** are read and unused.
- ~~**Water colours.**~~ **Wired 2026-07-25 by PLAN_12**, unverified. Bands 13-16
  and the `LightParams` alphas now reach `LiquidRenderer` through
  `WorldAtmosphere.SetAuthoredWater`, on the same `UseAuthoredData` gate as the
  sky. PLAN_12 §7 is the test protocol and §4 H4 records why the river pair is
  suspect. **More suspect since 2026-08-12** (§2.3): noggit/wowdev put the water
  colours at bands **14–17**, one past ours, and the decoded values agree —
  band 16 is a murky river green `(0.31,0.36,0.08)` where band 15 (our "river
  close") is a deep ocean blue. Not touched in the lighting-mode pass (water
  rides its own switch); flagged for its own pass.
- **The dnc.db sun arc is unwired.** `World\dnc.db` `DayX/Y/Z` is the vanilla
  client's authored sun direction per hour (X pinned at 0.7, Y/Z rotating). Both
  lighting modes still use the invented `SunDirectionAt` arc; the per-mode seam
  (`SunDirectionFor`) exists, the dnc arc's mapping into our world space does
  not. Same for the dnc grey day/ambient/fog colour ramps — only the intensity
  curve is consumed (§4.2).
- **Band heights unverified.** §4's three stops are guesses until a real-client
  capture exists. This is the single most likely reason the sky still looks off.
- **No `refs/` capture.** PLAN_09 §7 steps 6 is unrun; `refs/` holds only a
  README. Everything above is verified numerically and **nothing is verified
  photographically.**

---

## 8. Lineage

- PLAN_09_EXTERIOR_LIGHTING.md — the reasoning, the test protocol, the schemas.
- Schemas verified 2026-07-25 against wowdev.wiki `DB/Light`, `DB/LightParams`,
  `DB/LightIntBand`, `DB/LightFloatBand`, then re-verified against the record
  sizes in Nico's own files.
- Handbook §3.35 — the by-eye tuning pass this system supersedes.
