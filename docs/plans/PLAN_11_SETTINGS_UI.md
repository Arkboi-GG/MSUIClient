# Plan 11 — Player-facing settings modal (Escape menu)

Status: **specified and built in one pass, runtime-unverified.** Written before the
code in this session; the code landed in the same session. Everything below that
says "guess" is still a guess — §7 is how you settle each one.

## 1. Problem

There is no settings page. Every knob the client owns lives in the DevTools HUD —
one 970-line `GameLoop.Gui()` window plus `Water Tuning` and `Foliage Tuning` — and
that window is gated behind `_config.DevTools`, sorted by *which subsystem the
programmer was debugging*, and **nothing it changes survives the process**. Close
the client and the fog, the grass density, the doodad distance and the field of
view all snap back to whatever `client-config.json` and the renderer field
initialisers happen to say.

Three separate costs:

- **Nothing persists.** The Draft-20 by-eye lighting retune, the foliage curation
  pass, the water Draft-2 look: each of those sessions ended with a set of slider
  positions that were *the answer*, and each one had to be hand-copied back into a
  field initialiser or lost. That is a transcription step between "I found it" and
  "it is the default", and transcription steps are where values quietly drift.
- **There is no player surface at all.** `DevTools: false` is meant to be the
  shipping mode (FOUNDATION_PLAN §12) and in that mode the client has *zero*
  controls. Not reduced controls — none. No view distance, no vsync, no FOV.
- **Tests and preferences are mixed.** `Bind pose`, `Solo one geoset`,
  `Magenta unbound` and `Console visibility trace` are instruments: they exist to
  answer a question and are meaningless to a player. `Doodad distance`,
  `Ground clutter density`, `Field of view` and `VSync` are preferences. They sit
  in the same list, in the same visual weight, with the same styling.

**From a vantage:** at `looking at the visible castle`, changing
`Building distance` from 777 to 1250 is one drag — and is gone the moment the
process exits. There is no artifact of that decision other than a screenshot.

## 2. Class

**Addition**, with one emulation-core sub-goal.

The *taxonomy* and *behaviour* are an addition: measured against intent, not
against the real client. Vanilla 1.12's video options are a much smaller set than
this client already exposes, and deliberately so — matching them would throw away
controls that exist and are useful.

The *look* is emulation-core: the modal should read as a 1.12 dialog frame, built
from the client's own `Interface\` BLPs out of the MPQs, not from a hand-picked
palette that resembles one. That half is measured against a real-client capture.

## 3. Target

**Addition half — written intent.**

0. **Escape opens the Game Menu, not a settings page.** 1.12's Escape frame is
   `GameMenuFrame`: a 195×226 panel of 144×21 buttons — Video Options, Sound
   Options, Interface Options, Key Bindings, Logout, Quit, Continue. Video
   Options is a *second* frame it opens. The first version of this plan collapsed
   the two into one left-rail modal, which is a shape that exists nowhere in WoW.
   Escape from Video Options steps **back** to the Game Menu; Escape from the
   Game Menu closes it.
1. Both frames are centred. Quitting is a button in the Game Menu, the way the
   real game menu works.
2. Every control in it is a preference, not a test. If a control exists to answer
   "why is this broken", it stays in the DevTools HUD.
3. Each section shows **two or three simple controls by default** and hides the
   rest behind one drill-down. Simple controls are usually *composites* — one
   "View distance" slider that moves fog, far plane, building distance and doodad
   distance together in a coherent set. The drill-down exposes each of those four
   individually, and touching one flips the composite to `Custom`.
4. Changes apply **live**, as you drag. This client's entire working method is
   by-eye A/B (handbook §6); an apply-on-OK modal would break it.
5. `Okay` writes `settings.json`. Next boot reads it. That is the whole point.
6. Named presets, user-authored, saved into the same file, plus five built-in
   quality levels that are code-defined so they cannot rot.

**Emulation-core half — target capture.** `refs/settings-modal.png`: the 1.12
video options frame, so the border inset, the edge thickness, the header ornament
and the button metrics can be lined up rather than guessed. **This capture does
not exist yet** and until it does, §4 H3 is unsettled.

## 4. Key design decisions

Ranked, each falsifiable.

**H1 — the modal must not be gated behind `DevTools`.**
It is the *player* surface; gating it behind the developer flag reproduces exactly
the seam violation the Draft 24 stop point already records against
`UpdateLightProbe`. `GameLoop.Gui()` returns early when `DevTools` is false, so
the settings modal is drawn **before** that early return, and the dev HUD after it.
*Falsifiable:* set `"devTools": false` in `client-config.json`; Escape must still
open the modal and the HUD must not appear.

**H2 — settings are a separate file from `client-config.json`.**
`client-config.json` is per-machine wiring — MPQ paths, vmap paths, realmd host,
start position, the DevTools flag — and is gitignored for that reason.
`settings.json` is taste, and gets its own gitignored file at the repo root, loaded
by the same repo-root convention (`ClientConfig.FindRepoRoot`) that
`vantages.json` uses. The settings modal never rewrites the file that holds the
MPQ paths.
*Falsifiable:* delete `settings.json`; the client must start on shipped defaults
with `client-config.json` untouched.

**H3 — the frame is built from the MPQs' own `Interface\` BLPs. ~~Guess~~
**SETTLED, 2026-07-25** — the first attempt guessed and got 0/14.**

The first pass shipped a remembered path list and a remembered edge layout. It
resolved **nothing**, because `MpqMount.ReadFile` takes a literal internal path
and the archive stores `.blp`, while FrameXML omits the extension (the engine
appends it). One missing suffix, fourteen misses. The frame drew, because the
fallback held — but the whole hypothesis was untested guesswork that a five-minute
look at the archive would have killed.

**What replaced it.** `interface.MPQ` was read directly with a Python port of this
repo's own `MpqArchive`/`MpqCrypto`, and the answers were extracted rather than
recalled:

- **`(listfile)` gives the paths.** All fourteen were right modulo `.blp`.
  `Interface\FrameGeneral\UI-Background-Marble` does *not* exist in 1.12 and was
  dropped; `UI-Panel-Button-*`, `UI-CheckBox-*`, `UI-SliderBar-Button-Horizontal`
  and `UI-DialogBox-Header` do.
- **`Interface\FrameXML\` ships in the archive — 194 files of Blizzard's actual
  UI source.** `GameMenuFrame.xml`, `OptionsFrame.xml` and `BasicControls.xml`
  are the specification: frame sizes (195×226 and 450×575), button size (144×21),
  button tex-coords (0–0.625, 0–0.6875), header offsets (+12 above the frame,
  caption 14 down), and the three `<Backdrop>` blocks —

  | Backdrop | bgFile | edgeFile | EdgeSize | TileSize | Insets (l t r b) |
  |---|---|---|---|---|---|
  | Dialog | `UI-DialogBox-Background` | `UI-DialogBox-Border` | 32 | 32 | 11 12 12 11 |
  | Group box | `UI-Tooltip-Background` | `UI-Tooltip-Border` | 16 | 16 | 5 5 5 5 |
  | Slider track | `UI-SliderBar-Background` | `UI-SliderBar-Border` | 8 | 8 | 3 6 3 6 |

- **The edge layout was settled by decoding the texture and looking at it.**
  `UI-DialogBox-Border.blp` is 256×32 — eight 32×32 cells in a *horizontal* strip.
  Order confirmed by eye: LEFT, RIGHT, TOP, BOTTOM, TOPLEFT, TOPRIGHT,
  BOTTOMLEFT, BOTTOMRIGHT. The TOP cell's bar runs down the **left** of its cell
  and the BOTTOM cell's down the **right**, so both are drawn rotated a quarter
  turn **clockwise** — one rotation satisfies both, which is the check that it is
  right. **The first version rotated anticlockwise.** `UI-Tooltip-Border` (128×16)
  and `UI-SliderBar-Border` (64×8) follow the same eight-cell rule at their own
  edge sizes, which is why one nine-slice routine serves all three.

**The lesson, for the handbook's §6 list:** the UI was *data in the archive we
already mount*, not something to be recalled. Two rounds of plausible guessing
were beaten by one extraction. "Empirical over documented" applies to Blizzard's
own UI exactly as it applies to their file formats.

The fallback stays — every lookup still degrades to a procedural panel and the
client still prints `[ui-skin] n/14 resolved` plus a `UI skin` DevTools readout —
but it is now a safety net rather than the thing holding the feature up.

**H4 — simple controls are composites, and a composite is a real value, not a
label.**
"View distance = 62%" must map to a specific `(fogStart, fogEnd, wmoDistance,
doodadDistance, farPlane)` tuple by a single documented curve, so that two machines
at 62% look the same. Dragging any of the four underneath sets the composite to
`Custom` and stops the mapping applying. The alternative — a preset button that
scatters four values and then forgets it did — is what makes settings menus
untrustworthy.

**H5 — live apply, snapshot on open, revert on Cancel.**
Opening the modal deep-copies the current settings. `Cancel` restores that copy and
re-applies it. `Okay` serialises and closes. `Defaults` resets **only the visible
page** to shipped defaults, not the whole file.

**H6 — resolution and MSAA sample count are restart-scoped; everything else is
live.**
`ClientWindow` requests `Samples` at window creation and Silk cannot change the
sample count afterwards. VSync, the multisample *enable*, UI scale, FOV and every
renderer knob are live. Restart-scoped controls are labelled as such in the UI and
written to `settings.json` immediately, so the next boot picks them up. This is
also why `SettingsStore.Load` runs in `Program.Main` **before** `ClientWindow` is
constructed, not in `GameLoop.Load`.

## 5. Resources

Checked before writing, per the handbook's twice-earned rule:

| Source | Line range | What it gave |
|---|---|---|
| `Engine/Vantage.cs` | 19–76, 84–167 | The store pattern copied wholesale for `SettingsStore` — repo-root JSON, never throws on read, upsert-then-save. `Vantage` is also *already* a partial settings snapshot (WMO/doodad/atmosphere toggles); §10 records why it stays separate. |
| `ClientConfig.cs` | 289–363 | `JsonSerializerOptions`, `FindRepoRoot`, `ResolvePath`. Reused, not re-derived. |
| `Program.cs` | 1668–2637 | `Gui()` — the source list for the taxonomy in §6. Every control was classified preference/test by hand. |
| `Program.cs` | 2645–2854 | `WaterTuningWindow` / `FoliageTuningWindow` — the whole water and foliage sets moved into the Graphics page's advanced drill-downs. |
| `Engine/ClientWindow.cs` | 82–91, 267–290, 362–379 | `VSync` setter, `UiScale` / `FontGlobalScale` / `ScaleAllSizes`, `MultisamplingEnabled`, `Texture.ConfigureAnisotropy`. Establishes H6's live/restart split. |
| `Engine/Texture.cs` | 121–155 | `FromRgbaNoMips` — exactly the entry point a UI texture wants (no mips, clamp). Nothing new was written. |
| `Formats/BlpDecoder.cs` | 31 | `GetPixels(blp, mip, out w, out h)` returns BGRA; the skin swizzles to RGBA once at load. |
| `Formats/MpqMount.cs` | 74 | `ReadFile(internalPath)` returns `null` on a miss, which is what makes H3's fallback cheap. |
| `SYSTEM_FOLIAGE.md` §4, `SYSTEM_WATER.md` §4 | — | The per-knob meaning behind every slider moved out of the two tuning windows. Labels and tooltips were taken from there rather than reinvented. |

## 6. The taxonomy

The classification rule: **a control is a preference if a player who is not
debugging would ever want it; it is a test if it exists to isolate a suspect.**

### Moved into the settings modal

| Section | Simple (always visible) | Advanced (drill-down) |
|---|---|---|
| Quality | Overall quality: Low / Fair / Good / High / Ultra / Custom | — |
| Display | VSync, Multisampling, UI scale, Resolution* | Anisotropy*, framebuffer sample count readout |
| View distance | View distance (composite), Field of view | Fog start, fog end, fog on/off, stop-submitting-past-fog, couple far plane to fog, building distance, near/far plane, terrain visibility |
| Environment detail | Object detail (composite), Building detail (composite) | Doodad distance, doodad instancing, doodad frustum cull, flat cull bounds, doodad alpha cut, stream-only-nearby, WMO frustum cull, distance-only city shells, force two-sided, WMO alpha cutoff, impostor max verts, inside margin, interior cull, shell near-guard, occlusion cull + min distance |
| Ground clutter | Density, Distance | Max per cell, scale, scale jitter, max instances, rescatter distance, wind strength, wind speed, fade linkage + fraction / explicit start-end, alpha cutoff, brightness, per-kind enable + density, per-cell layer map, no-doodad mask, skip terrain holes |
| Water | Render water, Water detail (composite) | Texture scale, animation FPS, frame blend, texture brightness / contrast / tint, opacity, shoreline alpha + width, deep darkening, depth rate, base brightness, ambient amount, sun amount, sky sheen, wave amplitude, wave speed |
| Lighting & sky | Time-of-day lighting, Use authored DBC lighting | Sun strength, ambient strength, interior brightness (MOCV), doodad interior brightness (MODD), sky band stops, cycle time of day, game hours per minute |
| Camera & controls | Mouse sensitivity, Invert pitch, Camera collision | Raw cursor, turn speed, eye height, camera clearance, camera restore speed |
| Streaming | — (advanced only) | Terrain tile radius*, WMO preload radius*, drain preloads at startup*, doodad demand radius |

`*` = restart-scoped (H6), labelled in the UI.

### Left in the DevTools HUD, deliberately

Bind pose, force angle, solo geoset, geosets drawn, hide hair, magenta unbound,
attached-item switches, appearance sliders, torso/twist bones, strafe style,
collision wireframe + solid + isolate blocker + offset nudge, the group picker and
override buttons, `Console visibility trace`, `Dump groups on load`,
`Re-scatter now`, `ReclassifyShells`, every perf and GPU readout, the hitch
recorder, the light probe, the portal panel, vantages and the scene dump.

Two controls are *both*, and both surfaces keep them, reading the same property:
time of day and the noon/sunset/night buttons. They are a preference when cycling
and an instrument when pinned.

## 7. Test protocol

Written before the change. Nothing here needs a running server.

1. **Persistence.** Vantage `looking at green river water`. Escape, open Water's
   drill-down, drag `Opacity (deep)` to a visibly different value, `Okay`, quit,
   relaunch, load the same vantage. The water must come back at the new value, and
   `settings.json` must contain it. *This is the whole feature; if it fails
   nothing else matters.*
2. **Cancel really reverts.** Same vantage. Escape, drag four sliders across three
   sections, `Cancel`. Every one of the four must snap back, on screen, in the same
   frame.
3. **The DevTools seam (H1).** `"devTools": false`, relaunch. Escape opens the
   modal; no HUD window exists; the water and foliage tuning windows are gone; the
   Graphics page still drives them.
4. **Escape does not quit (regression).** Escape, Escape — the client is still
   running. `Exit Game` inside the modal quits.
5. **The skin readout (H3).** `[ui-skin] 16/16 resolved from the MPQs` is the
   pass condition; anything less names its own fix. Run 1 was 0/14 and named it.
6. **Composite coherence (H4).** Set View distance to 30%, dump the scene, set it
   to 100%, dump again. Diff the two dumps: `fogStart`, `fogEnd`, `wmoDistance`,
   `doodadDistance` and the far plane must all have moved, together, and no WMO
   group's reason code may change for a reason other than distance.
7. **Built-in presets do not rot.** Select each of Low/Fair/Good/High/Ultra and
   confirm the client stays above 30 FPS in Trade District on the Iris Xe at Fair
   or below. This is the only step that needs a specific machine.
8. **No frame-time regression.** The modal is ImGui like everything else, but it
   draws textured 9-slices. `HudMilliseconds` with the modal open must stay under
   1 ms; the baseline with it closed is ~0.25 ms and must not move at all.

## 8. Definition of done

- Steps 1–4 and 6 pass. They are pure additions and are measured against intent.
- Step 5 is **done**: run 1 reported 0/14, the archive was read, and the paths
  and the edge layout now come from `(listfile)`, FrameXML and the decoded
  texture. §4 H3 records what was found.
- Step 8's number is recorded.
- The emulation-core half is **not** done until `refs/settings-modal.png` exists
  and the frame has been lined up against it. Until then this plan ships with its
  look explicitly unverified, the same honesty SYSTEM_EXTERIOR_LIGHTING.md applies
  to the sky band heights.
- ~~`SYSTEM_SETTINGS.md` is extracted~~ **DONE, 2026-07-25** — extracted as
  **`SYSTEM_SETTINGS_UI.md`** (Draft 1) after the modal survived its first working
  session. The name changed because the scope did: two thirds of that doc is the
  frame, font and skin layer, which the character sheet and the bag windows will
  want long before they want a preference store.

  **This plan is now the argument and the test protocol. `SYSTEM_SETTINGS_UI.md`
  is the truth about how the thing works** — every ground-truth fact learned
  during the build (the `.blp` suffix, the FrameXML transcription, the edge-cell
  layout and rotation, the flat backgrounds, `PopupBg` vs `WindowBg`, the
  asymmetric ImGui clip rect, modals ignoring Escape, and teardown-from-`Gui()`)
  lives there, not here. Do not re-derive them from §4.

## 9. Fallback

Every layer degrades on its own:

- **No `Interface\` BLPs resolve at all** — the modal draws with the procedural
  style and is fully usable. The feature does not depend on the skin.
- **Wrong edge-file layout** — the live edge-size / corner-inset sliders, or set
  `"useTexturedFrame": false` in `settings.json` and the procedural style takes
  over with no rebuild.
- **A composite curve feels wrong** — the drill-down under it exposes all four
  underlying values, so nothing is reachable *only* through a composite.
- **`settings.json` is corrupt** — `SettingsStore.Load` never throws; it logs and
  starts from defaults, exactly as `VantageStore` does.
- **The whole modal is a mistake** — it is additive. `GameLoop/Panels/GameLoop.Settings.cs`,
  `Engine/GameSettings.cs` and `Engine/UI/WowSkin.cs` are new files; the changes to
  `Program.cs` are the Escape branch, one call in `Gui()`, one call in `Load()`,
  and the deletion of controls that now live elsewhere.

## 10. Reconciliation

**PLAN_05 / `TuningState`.** PLAN_05's HUD reorganisation is the closest prior
work and is recorded as "exists in `GameLoop/Dev/GameLoop.DevTools.cs` but the HUD is not fully
reorganized". This plan does the reorganisation from the other end: instead of
restructuring the HUD in place, it *removes* the preference half of it. What is
left is a much smaller instrument panel, which is what PLAN_05 wanted. **PLAN_05
should be re-read and reduced** — half its motivation is now spent.

**`Vantage` vs `GameSettings` — deliberately two types.** A vantage is *a place and
an instant*: position, camera, time, and the toggle state needed to reproduce a
frame. Settings are *a preference that outlives every place*. They overlap on
about fifteen fields and merging them is tempting and wrong: loading a vantage must
be able to stomp your fog values (that is what reproducing a frame means), and it
must not then have changed your saved preferences. **`ApplyVantage` therefore does
not write to `GameSettings`, and the modal shows a "modified by vantage" marker
when the live values have drifted from the saved ones.** If that marker turns out
to be confusing in practice, the fix is to make `ApplyVantage` push a settings
snapshot it can pop, not to merge the types.

**FOUNDATION_PLAN §12 (the DevTools seam).** This plan puts the *first* real
consumer on the non-DevTools side of that seam. The Draft 24 stop point records one
existing violation — authored exterior lighting reached only through
`UpdateLightProbe`, which early-returns when DevTools is off. **That defect now
has teeth:** with a settings modal that works in a DevTools-off build, a player
build renders with the invented lighting constants while the settings page happily
offers `Use authored DBC lighting`. Fix the seam violation *before* trusting the
lighting section of this page.

**Handbook §1.2.** Adds one entry to the documentation map:
`SYSTEM_SETTINGS.md`, status "planned extraction — after one working session".

**Build order impact.** None on streaming or lighting work; the files do not
overlap. The one collision is `Program.cs::Gui()`, which every session touches —
land this before the next HUD change rather than merging two edits to the same
970 lines.
