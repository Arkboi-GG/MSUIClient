# SYSTEM — Settings UI and the 1.12 frame layer

Draft 1 — 2026-07-25.

Covers the Escape menu: the Game Menu frame, the Video Options frame, the
persisted settings model, and the Blizzard-art skin layer all three are drawn
with. Extracted from `PLAN_11_SETTINGS_UI.md` under the handbook's §1.2 rule
after the modal survived its first working session. **PLAN_11 keeps the argument
and the test protocol; this file is the truth about how it works.**

> Named `SYSTEM_SETTINGS_UI.md`, not the `SYSTEM_SETTINGS.md` PLAN_11 §8
> promised. The scope grew past settings: two thirds of what follows is the
> frame, font and skin layer, which the character sheet and the bag windows will
> want long before they want a preference store.

---

## 0. The one-page version

Escape opens **GameMenuFrame** — a 195×226 panel of red `UI-Panel-Button` rows.
From it, **Video Options**, **Camera and Controls** and **Streaming** open as
separate frames; Escape steps back one level, then closes and writes
`settings.json`. Everything applies live as you drag. Cancel restores the
snapshot taken when the menu opened.

Every pixel of the frame is Blizzard's own art, read out of `interface.MPQ` at
startup, and every layout number is Blizzard's own, transcribed from the
FrameXML that ships in the same archive.

**Four files:**

| File | Owns |
|---|---|
| `Engine/UI/WowSkin.cs` | The art. Loads the BLPs, draws backdrops / buttons / checkboxes / sliders, pushes the ImGui style. Knows nothing about settings. |
| `Engine/UI/UiFont.cs` | Extracts `FRIZQT__.TTF` from the archives to a temp file before the window exists. |
| `Engine/GameSettings.cs` | Pure data + `settings.json` + presets + composite curves. No renderer references, no GL, no ImGui. |
| `GameLoop/Panels/GameLoop.Settings.cs` | The frames, and the two-way bridge between `GameSettings` and the live renderers (`ApplySettings` / `CaptureSettings`). |

Plus small hooks: `Program.Main` (font extraction, restart-scoped settings,
deferred quit), `Engine/ClientWindow.cs` (font path → `ImGuiController`),
`Program.cs::Gui()` (one call, above the DevTools return).

---

## 1. Ground truth — established empirically, do not re-derive

This section cost four runtime iterations. Every line of it was got wrong once
by recall and then settled by reading the archive or measuring the pixels.

### 1.1 The real UI ships in the archive. It is data, not a description of data

`interface.MPQ` contains **`Interface\FrameXML\` — 194 files of Blizzard's own
UI source.** `GameMenuFrame.xml`, `OptionsFrame.xml`, `BasicControls.xml` and
`UIPanelTemplates.xml` are the specification for every number in §1.3–§1.6.

Two rounds of plausible guessing lost to one extraction. **When a UI question
comes up — a frame size, a texcoord, a backdrop, a font — read the FrameXML.**
"Empirical over documented" (handbook §6) applies to Blizzard's UI exactly as it
applies to their file formats.

### 1.2 Texture paths need `.blp`. FrameXML's do not

`MpqMount.ReadFile` takes a literal internal path and the archive stores the
extension. FrameXML omits it because the engine appends it. Copying the FrameXML
form gave **0/14 resolved** on the first run.

```
Interface\DialogFrame\UI-DialogBox-Background.blp     <- what ReadFile wants
Interface\DialogFrame\UI-DialogBox-Background         <- what FrameXML says
```

`Interface\FrameGeneral\UI-Background-Marble` does **not** exist in 1.12 and was
dropped. Everything else in `WowSkin.Paths` is verified present in
`(listfile)`.

### 1.3 The three backdrops, transcribed verbatim

These are the only three `<Backdrop>` shapes in `OptionsFrame.xml`. Sizes are
Blizzard UI pixels, multiplied by `WowSkin.Scale` at draw.

| Use | bgFile | edgeFile | EdgeSize | TileSize | Insets (l t r b) |
|---|---|---|---|---|---|
| Dialog frame | `UI-DialogBox-Background` | `UI-DialogBox-Border` | 32 | 32 | 11 12 12 11 |
| Group box | `UI-Tooltip-Background` | `UI-Tooltip-Border` | 16 | 16 | 5 5 5 5 |
| Slider track | `UI-SliderBar-Background` | `UI-SliderBar-Border` | 8 | 8 | 3 6 3 6 |

There are **no `<BackgroundColor>` or `<EdgeColor>` tints anywhere** in the
extracted FrameXML. If a backdrop looks wrong, the drawing code is wrong — do
not tune these by eye.

### 1.4 The edge-file layout — eight cells, horizontal, and the rotation

`UI-DialogBox-Border.blp` is **256×32: eight 32×32 cells in a horizontal
strip.** `UI-Tooltip-Border` is 128×16 and `UI-SliderBar-Border` is 64×8 — the
same eight-cell rule at their own edge size, which is why one nine-slice routine
serves all three.

Order, read off the decoded texture:

```
index  0     1      2     3       4        5         6           7
      LEFT  RIGHT  TOP  BOTTOM  TOPLEFT  TOPRIGHT  BOTTOMLEFT  BOTTOMRIGHT
```

**The TOP and BOTTOM cells are stored standing up.** TOP's bar runs down the
**left** of its cell; BOTTOM's runs down the **right**. Both are drawn rotated a
quarter turn **clockwise**, which puts the left column along the top of the
strip and the right column along the bottom. *One rotation satisfying both is
the check that it is the right one* — the first implementation rotated
anticlockwise and it satisfied neither.

In UV terms: display-Y maps to texture-U, display-X maps to texture-V running
backwards.

### 1.5 Edges are drawn ONE QUAD PER TILE, not one quad with UV 0..n

The obvious implementation is a single `AddImage` whose `v` runs past 1 and lets
`GL_REPEAT` tile it. That drew a thin smear instead of a riveted bar: the wrap
mode set at texture creation was not what the sampler used by the time ImGui
issued the draw.

**Do not try to fix this by chasing sampler state.** Every edge emits one quad
per tile with `v` inside `[0,1]`, which cannot be wrong under any wrap mode. The
last tile is usually partial and its rect *and* its `v` are cut by the same
fraction — shortening one without the other stretches it. Twenty extra quads for
a frame edge is nothing; being at the mercy of global sampler state for the whole
look of the UI is not.

Edge textures are therefore loaded **clamped** on purpose: with per-tile drawing,
`REPEAT` would let linear filtering at `v=0` blend in the bottom row and put a
seam across every rivet.

### 1.6 The background textures are FLAT. The "stone" is the world

Measured over every texel:

| Texture | Size | Content |
|---|---|---|
| `UI-DialogBox-Background` | 64×64 | uniform RGBA **(0, 0, 0, 153)** — black at 60% |
| `UI-Tooltip-Background` | 64×64 | uniform RGBA **(142, 140, 142, 187)** — grey at 73% |

The mottled dark stone you see inside a real 1.12 dialog **is the world behind
it, darkened by that one flat texture and nothing else.** There is no stone
texture. Anything that makes the panel opaque destroys the single most
characteristic thing about the frame — see §2.2.

### 1.7 Buttons are one stretched quad, not a nine-slice

`BasicControls.xml`:

```xml
<Texture name="DialogButtonNormalTexture" file="Interface\Buttons\UI-Panel-Button-Up">
  <TexCoords left="0" right="0.625" top="0" bottom="0.6875"/>
</Texture>
```

`UI-Panel-Button-Up` is 128×32; the used region is the top-left **80×22**,
stretched across the button rect. There is no three-slice and no cap logic —
which is why real WoW buttons look slightly squashed at odd widths too.
`-Highlight` is `alphaMode="ADD"` in FrameXML; draw lists have no per-quad blend
mode, so it goes on at partial alpha instead. Close enough, and the alternative
is a draw-list flush per button.

### 1.8 Frame geometry

From `GameMenuFrame.xml` and `OptionsFrame.xml`:

| Thing | Value |
|---|---|
| GameMenuFrame | 195 × 226 |
| Menu button | 144 × 21, `UIPanelButtonTemplate` |
| First button | centre at 37 below the frame top (top edge ≈ 26.5) |
| Button gap | 1 px; 16 px before Continue |
| Header plaque | `UI-DialogBox-Header`, 256 × 64, anchored TOP with **+12 (it hangs ABOVE the frame)** |
| Plaque caption | 14 px down from the plaque's top |
| OptionsFrame | 450 × 575 |

The plaque's art is mostly transparent padding — the visible metal is roughly
the middle 70% horizontally and spans about y = 5..35 of its 64. That is why
content can start 30 px below the frame top without colliding with it.

**Our Video Options is 540 × 620 UI pixels, not 450 × 575**, because this client
exposes several times as many controls. That is the one deliberate departure
from Blizzard's numbers, and it is sized in UI pixels — never as a fraction of
the display. Run 2 used 52% of the screen width and produced a 2000-pixel frame
full of 1500-pixel sliders, proportions that exist in no version of WoW.

### 1.9 The font is in the archive too

`fonts.MPQ` holds `FRIZQT__.TTF` (62 KB, the main UI face), `MORPHEUS.TTF`
(quest headers), `SKURRI.TTF` (damage) and `ARIALN.TTF`.

ImGui's default bitmap face (ProggyClean) was **the loudest single thing wrong**
with the first two attempts — louder than the frame art, because every label on
screen is in it.

Sizing: GameFontNormal is FRIZQT at 12 pt inside a 21-pixel button, so the face
is a little over half the button height. `UiFont.SizeFor` keeps that ratio:
`round(12 × UiScale)`, clamped to [10, 64].

---

## 2. ImGui traps this system hit, and how each was found

Every one of these looked like an art bug and was not.

### 2.1 A modal popup is filled with `PopupBg`, not `WindowBg`

`BeginPopupModal` paints the window with `ImGuiCol.PopupBg`. Leaving that at the
near-opaque fill that combo boxes and tooltips want meant ImGui painted the
window solid *before* anything was drawn, and the backdrop then composited over
black instead of over the world.

The visible damage was **not** the missing translucency — it was the *border*.
The frame art is dark grey metal, so against a black fill only its highlight edge
survived and the whole frame read as a thin bright hairline.

Fix: push a transparent `PopupBg` around `Begin` **only**, then pop immediately.
ImGui samples the background colour once at `Begin`, and every nested popup after
that still wants the opaque one.

### 2.2 There is no modal dim in WoW

`ImGuiCol.ModalWindowDimBg` defaults to 45% black. Stacked under a 60%-black
backdrop that is 78% total, which reads as solid. WoW does not dim behind the
game menu. It is pushed to zero.

### 2.3 ImGui's inner clip rect is inset horizontally but not vertically

This is the one that cost the most, because it produced a symptom that looks
exactly like broken art: **top and bottom borders correct, both side borders
completely absent, header plaque sliced off at the top.**

`Begin()` leaves the window clipped to roughly:

```
InnerClipRect.Min.x = InnerRect.Min.x + max(floor(WindowPadding.x / 2), WindowBorderSize)
InnerClipRect.Min.y = InnerRect.Min.y + WindowBorderSize
```

Horizontally it insets by **half the window padding**; vertically by only the
border size, which this style sets to 0. At `WindowPadding.x = 24` and
`Scale = 1.8` that is **21 px on the left and right, 0 top and bottom**.

The visible metal of a 32-px edge cell sits **9.9 to 19.8 px** in — entirely
inside the horizontal inset, entirely outside the vertical one. So the side bars
were clipped away completely and the top/bottom ones were untouched. The plaque
hangs 21.6 px *above* the window and was cut at the window's top edge.

**Fix: the frame is not content, so it must not be clipped like content.**
`dl.PushClipRectFullScreen()` around the backdrop and plaque, `PopClipRect()`
after. Everything else stays clipped normally.

*This is the general rule for this file: anything drawn AT or OUTSIDE the window
rect needs the clip rect lifted first.*

### 2.4 ImGui does NOT close modal popups on Escape

`NavUpdateCancelRequest` excludes them by name:

```cpp
if (g.OpenPopupStack.Size > 0 &&
    !(g.OpenPopupStack.back().Window->Flags & ImGuiWindowFlags_Modal))
    ClosePopupToLevel(...);
```

So the `p_open` handed to `BeginPopupModal` **never goes false for a modal**, and
a design that waits for it will never fire. Escape opened the menu and then did
nothing at all.

Every level of Escape is ours: latched on the key's rising edge in `Update`,
spent inside the popup's Begin/End scope — which is the only place
`CloseCurrentPopup` is legal. `p_open` is still passed only because
`BeginPopupModal` has no `(name, flags)` overload; its value is ignored and
commented as such.

Escape is deferred to ImGui while `WantTextInput` is true, so hitting it in the
preset-name field abandons the field rather than the whole menu.

### 2.5 Nothing invoked from `Gui()` may tear down

`ClientWindow.Close()` raises `Closing` **synchronously**, and `Closing` runs
`GameLoop.Dispose()` — deleting the skin's textures, every renderer's buffers,
and finally the GL context. Calling it from a button handler left the rest of
that ImGui frame drawing into freed memory: an `AccessViolationException` on
whatever widget came next, with a stack that points nowhere near the button.

Exit Game sets `_quitRequested`. `Update()` spends it at the very top — the one
point in the loop outside an ImGui frame *and* before anything is touched — and
returns immediately.

**Generalise this.** The hitch recorder, the vantage loader and the scene dump
are safe because they only read or write files. Anything that frees a GPU
resource from a button handler will fail the same way.

### 2.6 A real TTF must not be scaled twice

`ClientWindow` used to set `FontGlobalScale = UiScale`. With a TTF already
rasterised at the right pixel height, scaling it again blurs it and breaks every
size chosen to match a 21-pixel button. `FontGlobalScale` stays 1 when a real
font loads and only takes the scale when we are stuck with the bitmap face.
`ScaleAllSizes(scale)` always applies — the dev HUD still needs it.

---

## 3. The settings model

### 3.1 `settings.json` is not `client-config.json`

`client-config.json` is **per-machine wiring** — MPQ paths, vmap paths, realmd
host, start position, the DevTools flag — and is gitignored for that reason.
`settings.json` is **taste**, gets its own gitignored file at the repo root, and
the settings page never rewrites the file holding the MPQ paths.

`SettingsStore` copies `VantageStore`'s promises exactly: repo-root JSON,
human-readable, hand-editable, and **never throws on read**. A missing or corrupt
file logs a line and starts from shipped defaults.

### 3.2 A vantage is not a settings snapshot

They overlap on about fifteen fields and merging them is tempting and wrong. A
vantage is *a place and an instant*; loading one is **supposed** to stomp your
fog values, and it must not then have changed your saved preferences.
`ApplyVantage` therefore does not write to `GameSettings`.

The bridge in the other direction is the **`Adopt live`** button: it reads
whatever the renderers are set to right now into the settings object. That is the
feature PLAN_11 exists for. Every previous by-eye session — the lighting retune,
the foliage curation, the water Draft 2 look — ended with a set of slider
positions that had to be hand-copied into a field initialiser or lost. Tune on
the HUD, press Adopt live, press Okay.

### 3.3 Composites are real values, not labels

`View distance`, `Object detail`, `Building detail` and `Water detail` are
percentages that **generate** the values beneath them through a documented curve
in `GameSettings`, so two machines at 62% look the same. Touching any generated
value sets the group's `Custom` flag and the generator stops.

The alternative — a preset button that scatters four values and then forgets it
did — is what makes settings menus untrustworthy.

Two composites must never write the same value. `View distance` owns
`BuildingDistance`; `Building detail` owns the impostor/occlusion set and
deliberately does not touch distance.

### 3.4 Restart-scoped vs live

Read by `Program.Main` **before the window exists**, because they are decided at
window creation and cannot be changed after: **resolution, multisample count,
anisotropy, UI scale (font rasterisation), and the streaming radii.** They are
written to `settings.json` immediately so the next boot picks them up, and are
labelled in the UI.

Everything else is live. VSync, the multisample *enable*, FOV and every renderer
knob apply as you drag.

### 3.5 Built-in quality levels are code, not data

Low / Fair / Good / High / Ultra live in `GameSettings.ApplyQuality` so an old
`settings.json` cannot pin them to a stale definition. User presets are stored in
the file. A preset deliberately does **not** touch the 1.12 authenticity
switches, the water colour set or the **lighting mode** — those are not quality
dials.

### 3.6 Lighting mode (settings v6, 2026-08-12)

`Lighting.UseAuthoredData` (bool) became `Lighting.Mode` — a string-serialised
enum, `"Msui"` (MSUI Lighting) or `"Parity112"` (1.12 Parity). Both modes
consume the authored `Light.dbc` chain; they differ in interpretation
(SYSTEM_EXTERIOR_LIGHTING.md "Lighting modes"). The old JSON key is simply
ignored on load and the v6 migration pins every pre-v6 file to `Msui`, so an
existing install sees exactly the exterior look it had.

v6 also **persists the WMO doorway-spill multiplier** as
`Lighting.InteriorSpill` (was `WmoRenderer.InteriorBrightness`, a DevTools-only
slider that reset to `1.0` every launch — why the Northshire Abbey doorway glow
always shipped faint). The mode combo pushes per-mode recommended values through
`LightingSettings.ApplyLightingModeDefaults` (Msui `1.8`, Parity `1.0`) — a real
value push in the §3.3 sense, overridable in Advanced. The mode combo lives at
the top of *Video Options → Lighting and sky* and is mirrored (same value, same
path) in the DevTools light probe panel.

---

## 4. The DevTools seam

**The settings modal is drawn BEFORE `Gui()`'s DevTools early return.** It is the
*player's* surface and must exist in a shipping build where all developer tooling
is off. Do not move that call and do not add a DevTools check to it.

This is the first real consumer on the non-DevTools side of the seam
(FOUNDATION_PLAN §12), and it put weight on a defect that has since been
**fixed (2026-07-25)**: authored exterior lighting used to be reached only
through `UpdateLightProbe`, which early-returned when DevTools was off. The
resolve now runs in every build (`UpdateExteriorLighting` is core; see
SYSTEM_EXTERIOR_LIGHTING.md §4.0), so the Lighting section's mode combo is
honest in a DevTools-off build.

### What moved out of the dev HUD

Draw distance, fog, FOV, VSync, MSAA, doodad and building distance, every foliage
knob, every water knob, the impostor/occlusion set, mouse sensitivity, camera
collision. The `Water Tuning` and `Foliage Tuning` windows are **deleted** — a
dead HUD window is a second place for a value to disagree with itself. Where the
sliders were, the HUD now carries readouts of the same values.

### What stayed, and why

Bind pose, force angle, solo geoset, geosets drawn, magenta unbound, the group
picker and override buttons, collision wireframe and offset nudge, console
visibility trace, dump-groups-on-load, the hitch recorder, the light probe, the
portal panel, vantages and the scene dump, every perf and GPU readout.

The rule: **a control is a preference if a player who is not debugging would ever
want it, and a test if it exists to isolate a suspect.**

Time of day is the one control both surfaces keep — a preference when cycling, an
instrument when pinned.

---

## 5. Tuning and testing

| Instrument | Answers |
|---|---|
| `[ui-skin] n/16 resolved from the MPQs` | Whether the art loaded. Every miss names its own path and reason. **16/16 is the pass condition.** |
| `[ui-font]` console line | Which archive the typeface came from, and its size. |
| DevTools **UI skin** panel | Per-texture found/missing with dimensions, live frame-art scale, and the textured/procedural switch. |
| `Textured frame` setting | Turns the whole skin off. A broken skin can never make the settings unreachable. |
| Clutter **fade readout** | `visible to N yd, thinning from N yd - N tuft(s) placed`, always shown. See §6. |
| `Adopt live` | Bridges a DevTools tuning session into a saved preference. |

**Test protocol** lives in PLAN_11 §7 and still stands. The load-bearing one is
step 1: change a value, Okay, quit, relaunch, load the same vantage, and confirm
it came back. If that fails nothing else matters.

---

## 6. The clutter distance trap

Not a UI bug, but it surfaces here and it has bitten twice.

**Scatter radius and fade window are two separate values.** `FoliageRenderer`'s
own note:

> *THIS DEFAULTS ON BECAUSE THE SLIDERS LIE OTHERWISE. FadeEnd was a fixed 45 yd
> while Radius went to 120, so raising Radius scattered grass that was then faded
> out.*

With `Fade follows distance` off, `EffectiveFadeEnd` pins to `FadeEnd = 45` and
thinning starts at 30 — grass is scattered to 100 yd and faded away by 45. It
reads as a hard cap at about forty yards.

The effective numbers are therefore printed under the Clutter distance slider
permanently, with an amber line naming the switch when the fade is pinned closer
than the radius. **Turn the question into a readout rather than answering it
again.**

### A rescatter is expensive and must be debounced

A full re-scatter measured **2,438 ms at radius 45** and grows with the square of
it. Coverage changes therefore set a pending flag and rebuild **once, on mouse
release** — firing one per frame of a slider drag froze the client, and made the
distance look like it was not taking effect at all because the rebuild never
finished.

**Any future setting that triggers a rebuild must follow the same pattern.**

---

## 7. How these facts were obtained

Recorded because the method is reusable and beat two rounds of recall.

`interface.MPQ` was read directly in a scratch environment with a Python port of
this repo's own `MpqArchive` / `MpqCrypto` (v1 headers, encrypted hash and block
tables, zlib and stored sectors — enough for the interface archive) plus a port
of `BlpDecoder` (palettised, DXT1/3/5, raw BGRA). That gave:

- `(listfile)` → the real paths, and which ones do not exist
- `Interface\FrameXML\*.xml` → every layout number in §1.3–§1.8
- decoded BLPs written to PNG → the edge layout in §1.4 and the flat backgrounds
  in §1.6, settled by **looking at them**
- a pixel-accurate reimplementation of `DrawBackdrop` → which rendered the
  correct 1.12 frame, proving the algorithm was right and moving the search to
  ImGui, where §2.1 and §2.3 were waiting

That last step is the one worth repeating. **When the render looks wrong and the
algorithm looks right, render the algorithm somewhere else.** A deliberately
broken variant (clamped instead of tiled) matched the screenshot pixel for pixel
and named the layer the bug was in.

**The scripts are in the repo: `tools/mpqpeek/`.** Stdlib-only, read-only, not on
the build path, and its `load_order()` mirrors `MpqMount.LoadOrder` so a lookup
resolves to the same archive the client would.

```
python3 tools/mpqpeek/mpqpeek.py find  'UI-SliderBar'
python3 tools/mpqpeek/mpqpeek.py cat   'Interface\FrameXML\OptionsFrame.xml'
python3 tools/mpqpeek/mpqpeek.py stat  'Interface\Tooltips\UI-Tooltip-Background.blp'
python3 tools/mpqpeek/mpqpeek.py cells 'Interface\DialogFrame\UI-DialogBox-Border.blp' -o grid.png
```

`stat` is the one that is easy to skip and shouldn't be — it prints `FLAT COLOUR`
with the RGBA when every texel is identical, which is how §1.6 was found. `cells`
is what settles any nine-slice question in one picture.

Reach for it whenever a question is answerable from the archives. Its README
lists the gaps (no PKWARE explode, no writing).

---

## 8. Not done

- **`refs/settings-modal.png` does not exist.** The look is unverified against a
  real-client capture. Until it does, the frame is "matches the FrameXML numbers
  and looks right by eye", which is a weaker claim than this doc's tone
  elsewhere. Same honesty SYSTEM_EXTERIOR_LIGHTING.md applies to the sky bands.
- **Sound Options and Key Bindings are greyed placeholders.** They are in the
  menu so it does not change shape when they are built. Key rebinding needs an
  input-binding layer that does not exist.
- **No two-column layout.** Real Video Options packs controls in a grid inside
  each group box; ours are single-column. Cosmetic, and it costs a real layout
  pass.
- **No scroll frame art.** The Video Options body scrolls with an ImGui
  scrollbar, not `UI-ScrollBar-*`.
- **`MORPHEUS.TTF` is extracted-capable but unused.** Header plaques use FRIZQT.
- **Group-box backdrop height lags one frame** when a drill-down opens: the
  backdrop must be drawn before its contents, so it uses last frame's measured
  height. Self-correcting, visible once as a flicker.
- **Escape does not reach a nested combo popup** — closing an open dropdown with
  Escape closes nothing, because the combo is a second popup on the stack.
- **`ImGuiFontConfig` and `AddImageQuad` are version-sensitive.** Both bind fine
  against the Silk.NET 2.21 in use; a Silk upgrade should re-check them first.
