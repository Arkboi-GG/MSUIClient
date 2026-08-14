# Login Screen - status & handoff

Scope: the complete 1.12 login screen. Two layers, plus a live tuning modal:

1. The 3D "glue scene" - `Interface\Glues\Models\UI_MainMenu\UI_MainMenu.m2` (the
   burning gate) rendered fullscreen behind everything, through the model's own
   authored camera, with fog, an authored light rig, animated M2Colour tracks, and
   its 28 particle emitters. `Engine/GlueScene.cs` + `World/Particles/ParticleRenderer.cs`.
2. The 2D chrome - the WoW logo, the account/password edit boxes, the Login/Quit
   buttons, the Remember checkbox, the cosmetic main-menu buttons, and the
   version/copyright text. Drawn as ImGui draw-lists skinned with Blizzard's own art
   (from interface.MPQ) with layout numbers transcribed from `AccountLogin.xml`.
   `Engine/UI/WowSkin.cs` + `GameLoop/Scene/GameLoop.Net.cs`. NOT FrameXML - it is data-driven skin,
   not a FrameXML interpreter.

benilla is the byte-faithful ground truth throughout:
`crates/benilla/src/login/{mod.rs,screen.rs}`, `crates/benilla/src/glue/{art.rs,widgets.rs,backdrop.rs}`,
`crates/benilla/src/portrait/glue_booth.rs`, `crates/benilla/src/particles/*`.

## Status

Both layers are COMPLETE and signed off by Nico as matching the OG login. The only
login work still open is **Stage B** (the realm-select modal, see "Open / next").

The 2D chrome, buttons, inputs, checkbox, text and particles were all dialed in
against real 1.12 screenshots and the actual extracted textures (see mpqpeek below).

## Files

- `Engine/GlueScene.cs` - the 3D scene: mesh, authored camera, fog, light rig,
  M2Colour tint tracks, emitter setup, particle Simulate/Render, `TryInitParticles`.
  Also sets the two per-frame particle size knobs from `GlueTune`.
- `World/Particles/ParticleRenderer.cs` - the emission kernel (world-space flames vs
  model-space swirls), the flipbook, the portal knobs. The glue owns its OWN
  ParticleRenderer instance, independent of the in-world one. Two global sprite-size
  multipliers: `SpriteSizeScaleAll` (model-space swirls) and `BrazierSizeScaleAll`
  (world-space flames).
- `Engine/UI/WowSkin.cs` - the 2D skin. `DrawBackdrop` (nine-slice, now tint-aware),
  `GlueButton`, `CheckBox`, `GlueImage`, `Has`, the glue texture set + UVs, and the
  static `GlueTune` knob block at the bottom of the file.
- `GameLoop/Scene/GameLoop.Net.cs` (partial `GameLoop`) - `NetHud` (state router), `DrawLoginScreen`
  (the whole login layout), `LoginField` (one edit box), `GlueText` (a shadowed glue
  label), `GlueMenuButton` (cosmetic buttons), and `DrawGlueTuning` (the modal).
- `Engine/ClientWindow.cs` - builds the ImGui font atlas. FRIZQT is now rasterised
  SUPERSAMPLED (see below).
- `Engine/UI/UiFont.cs` - extracts `Fonts\FRIZQT__.TTF` from fonts.MPQ to a temp file.

## The 3D glue scene (formerly "Phase 1")

All three of the original open items are resolved:

- **Colour / warmth.** The scene now reads warm-gold like OG instead of the old
  cool grey-green. The fix was the benilla lighting law, applied consistently:
  point lights use the fixed falloff `1/(0.7*d + 0.03*d^2)` with NO authored radius
  clamp; directional lights fold into an order-2 SH probe whose per-normal closed
  form is `C*(4/17)*(0.375 + 2*mu + 1.875*mu^2)`, `mu = N.u`; the light sum saturates
  to [0,1] BEFORE the texture multiply (FFP combine); everything is done in gamma
  space with a single sRGB decode as a full-frame post-step.
- **Zoom / framing.** Tighter, matching OG. The authored FOV is treated as a
  DIAGONAL angle and converted to vertical by linear angle division:
  `fovy = fov / sqrt(1 + aspect^2)` (NOT tangent-based). benilla frames the same
  glue booth the same way (`portrait/framing.rs`, `glue_booth.rs`).
- **Flame jitter / flipbook.** The FlameLick 4x4 flipbook is now driven PER PARTICLE
  by each particle's own age via the head_cells `(begin,end)` ramp (two segments
  split at MidPoint), instead of one global-time cell for the whole draw group.
  Offsets: head_cells at record `+0x168/+0x16a` (seg A), `+0x16e/+0x170` (seg B),
  repeats `+0x16c/+0x172`, MidPoint `+0x14c`. CellRamp: forward arm base=begin
  span=end-begin+1, reverse base=begin+1 span=end-begin-1, index=floor(base+span*t)&0xFF,
  endpoint inset t*0.99+0.005. The instance buffer carries a `Vector4 CellRect`
  (attribute location 4, `aCellRect` in `particle.vert`); non-flipbook pools (the
  1x1 GLOWBALL swirls) upload `(0,0,1,1)` so their UVs stay byte-identical.

Ground rule from Nico that still holds: **do not touch the model-space swirling
particles** - they are correct.

## The 2D chrome (formerly "Phase 2")

Everything is ImGui draw-list drawing, skinned with the real art, positioned by
`AccountLogin.xml` numbers scaled to a 1024x768 glue canvas by `s = windowHeight/768`
and anchored to screen edges - exactly how UIParent lays the login out.

Layout numbers (canvas units, from AccountLogin.xml): logo TOPLEFT(3,7) 256x128;
account edit box 160x37 bottom-anchored at 345; password at 270; Login 170x45 top 519;
Quit 150x38 BOTTOMRIGHT(5,29); Remember checkbox 20x20 at (17,653); Blizzard logo
100x100 BOTTOM(0,8); version/copyright small gold text bottom-left / bottom-centre.
The cosmetic side buttons (Cinematics/Credits/Terms of Use on the right; Manage
Account/Community Site on the left) are placed by eye (benilla cut these) - left
column sits above the checkbox so it never clips it.

### The de-halo saga (READ THIS - it burned several rounds)

The `Glue-Panel-Button-*` sprites ship a soft ~38%-alpha black GLOW RING baked
around the pill. **That ring IS the 1.12 button's shadow** - it grounds the button
on the scene. Do NOT remove it.

An early complaint about a "black box around the button" led to a `DehaloNearBlack`
routine that faded near-black pixels to transparent. This was WRONG: it stripped the
authentic shadow, rendered the metal border transparent, and (worse) turned the
darker-red PRESSED pill transparent, because the down sprite's pill red runs down to
`max(rgb) ~16-38` and the de-halo threshold (40) ate it. After extracting the real
`Glue-Panel-Button-Up.blp` / `-Down.blp` with mpqpeek and compositing them over a
scene-bright background, it was obvious the raw texture matches OG and the de-halo'd
one is the washed/transparent one. **The de-halo was removed entirely** for the
button faces; sprites now upload as authored. The only per-sprite transform left is
on the highlight (see below). Do not reintroduce de-halo on the button faces.

Measured texture facts (mpqpeek, over interface.MPQ): both Up and Down are opaque
red pills with a grey metal bevel and the soft black glow ring; Up pill centre
~(112,0,0), Down pill centre ~(82,0,0) (~27% darker - that is the entire "pressed"
difference, there is no black interior in the real texture).

### Buttons - `WowSkin.GlueButton`

- Face art: one stretched quad of the sheet's benilla `BUTTON_TC` region
  `[0,0.578125] x [0,0.75]` = `[0,148] x [0,48]` of 256x64, uploaded as authored.
  `up` normally, `glue.btn.down` while held, `glue.btn.off` when disabled.
- Pressed ("drops down"): when held the sprite is nudged down-right one UI px
  (`drawPos = pos + (1,1)*Scale`) - WoW's pushed offset. The pressed look is the
  darker-red Down sprite; there is NO black fill (that was a wrong turn).
- Hover glow: `glue.btn.hi` overlaid additively. The highlight sheet is a black
  field + a bright rim meant to be ADD-blended; drawn straight it would veil the pill
  with a dark shadow on hover, so at load its alpha is rebuilt from brightness
  (`HighlightAlphaFromLuma`: dark field -> transparent, bright rim -> tint). Strength
  is the `GlueTune.HoverGlow` knob (0 = off).
- Caption: FRIZQT sized to the button (`GlueTune.CaptionSizeRatio` of button height),
  shrunk to fit long labels, lifted a little (`GlueTune.CaptionLift`) for optical
  centring, gold at rest / white on hover, with its OWN drop-shadow ratio
  (`GlueTune.CaptionShadowRatio`, a smooth sub-pixel offset so the slider is
  continuous and 0 = no shadow - the label shadow ratio is separate because small
  labels bottom out at a 1px floor and would otherwise inflate the big caption).

### Edit boxes - `LoginField` + `WowSkin.GlueEditBox`

The field is the real `AccountLogin.xml` `<Backdrop>`: `UI-Tooltip-Background` tiled
inside a `Glue-Tooltip-Border` nine-slice (edge/tile 16, BackgroundInsets 10/4/5/9),
drawn by `DrawBackdrop` - a recessed frame, not a flat rectangle. Crucially the
backdrop is TINTED with AccountLogin's `DEFAULT_TOOLTIP_COLOR` (benilla
`login/screen.rs` `BOX_FILL = srgb(0.09,0.09,0.09)`, `BOX_BORDER = srgb(0.8,0.8,0.8)`):
the tooltip-background sheet is light, so at full white it reads whitish-grey over
the bright valley; the near-black fill tint is what makes it OG's dark recessed well.
`DrawBackdrop` now takes optional `(fillTint, edgeTint)` for this. The typed line is
`GlueEditBoxFont` (ARIALN 18 = benilla `EDIT_FONT_SIZE`); the InputText is frameless
(all FrameBg states transparent, no border) and the window font is scaled up to the
typed size for the InputText only.

### Checkbox - `WowSkin.CheckBox`

`UI-CheckBox-*` box + mark. Its label takes an explicit glue pixel size (`labelPx`):
the ambient ImGui font is tiny next to the s-scaled box, so the login passes
`GlueTune.CheckLabelUnits * s`. Gold label with the glue drop shadow.

### Text shadow

Every glue label carries the 1.12 MasterFont drop shadow: black, down-right, offset
scaled to the text size (`GlueTune.ShadowOffsetRatio`, `ShadowAlpha`). `GlueText`
draws it for the labels/version/copyright/realm; `CheckBox` and `GlueButton` draw
their own. benilla confirms the (1,-1) black drop shadow on all glue fonts.

### Font supersampling (crisp text)

ImGui rasterises the TTF atlas at ONE pixel size and scales the bitmap to whatever
size text is drawn at. The glue screen draws FRIZQT much larger than the in-game UI
(labels 17*s, typed 18*s, captions up to ~29px), so a 12px atlas was being up-scaled
and blurred. `ClientWindow` now rasterises the atlas SUPERSAMPLED - `UiFontSize *
FontSupersample` (`FontSupersample = 3`) - and sets `io.FontGlobalScale = 1/3`, so
the in-game text keeps its intended size but every larger glue size is DOWN-scaled
from a hi-res atlas, which is crisp. Draw-list text drawn at an explicit size (the
glue widgets) samples the sharper atlas and is unaffected by FontGlobalScale.

### Particles - ember vs brazier size split

The glue scene has TWO particle groups sized independently:
- model-space "swirls" (the floating embers converging inward) -> `SpriteSizeScaleAll`,
  driven by `GlueTune.ParticleSize` (default 0.5, Nico-confirmed).
- world-space "flames" (the brazier fires) -> `BrazierSizeScaleAll`, driven by
  `GlueTune.BrazierSize` (default 1.0 = authored).
Both are global per-sprite multipliers applied to the two size code paths in
`ParticleRenderer` (model-space line and world-space line). Because the glue owns its
own ParticleRenderer instance, these never touch in-world/creature particles.

## `GlueTune` - the live tuning modal

`WowSkin.GlueTune` is a static block of mutable knobs read every frame by the glue
widgets and layout. `GameLoop/Scene/GameLoop.Net.cs DrawGlueTuning` renders a skinned ImGui window of
sliders bound to them; it is toggled by a small "tune" text at the login's top-right
and rendered from `NetHud` right after the login screen. `Log values` prints a
copy-pasteable `[glue-tune] ...` line to the console; `Reset` restores defaults.

Current defaults (Nico's dialed-in values, baked in):

| Knob | Default | Range | What it does |
|------|---------|-------|--------------|
| ButtonHeightMul    | 1.086 | 0.8-1.8  | height multiplier for every glue button |
| CaptionSizeRatio   | 0.389 | 0.25-0.65| caption height as a fraction of button height |
| CaptionLift        | 0.177 | 0-0.30   | upward optical nudge of the caption |
| CaptionShadowRatio | 0.05  | 0-0.14   | the red-button caption's own shadow offset ratio |
| HoverGlow          | 0.50  | 0-1      | hover highlight strength (0 = off) |
| ShadowAlpha        | 1.00  | 0.3-1    | label/field/checkbox shadow opacity |
| ShadowOffsetRatio  | 0.08  | 0.02-0.20| label/field/checkbox shadow offset ratio |
| FieldLabelUnits    | 17    | 10-30    | "Account Name"/"Account Password" label size |
| TypedTextUnits     | 18    | 10-30    | typed account/password line (benilla ARIALN 18) |
| CheckBoxUnits      | 24.8  | 12-40    | Remember checkbox box size |
| CheckLabelUnits    | 13.1  | 8-30     | Remember checkbox label size |
| ParticleSize       | 0.50  | 0.25-3   | model-space ember/swirl size |
| BrazierSize        | 1.00  | 0.25-3   | world-space brazier flame size |

The "tune" toggle + `DrawGlueTuning` + `GlueTune` are DEV scaffolding. For a release
build, remove the toggle in `DrawLoginScreen`, `DrawGlueTuning`, and (optionally)
fold `GlueTune`'s values into constants.

## Ground rules (Nico)

- Verify empirically - dumps, pixel diffs, measured angles, EXTRACTED TEXTURES (use
  `tools/mpqpeek/mpqpeek.py` against GameData/Data) - do not tune constants blind.
  benilla is the byte-faithful reference.
- Do not touch the model-space swirling particles - they are correct.
- Files: docs use LF; C#/shader files use CRLF. Keep C# comments ASCII where possible
  (a no-BOM file with exotic glyphs caused editor mojibake once).
- Shipped documentation stays server-agnostic.

## Open / next

- **Stage B - the realm-select modal.** A skinned modal popup (BeginPopupModal with a
  `UI-DialogBox` backdrop, like the OG realm list) to set `RealmdHost`/`RealmdPort` +
  a realm name, saved to config "like live" (persisted locally after entry). It is
  opened from the "Realm" line at the bottom-right of the login, which already has a
  hover hitbox (`##realm`) wired as the affordance. This is the last login item.
- **Character select.** BUILT (phases 1-3). See `SYSTEM_CHARACTER_SELECT.md` for the
  full status/handoff (the per-race glue backgrounds, the character-in-booth, the 2D
  chrome, and the still-open scene-lighting / sun-direction work).

## GlueScene is now SHARED with character select (2026-07-28)

`Engine/GlueScene.cs` was generalized to render any UI_* glue scene (char-select's per-race
backgrounds), not just UI_MainMenu. The login constructs it exactly as before
(`new GlueScene(gl, mpq, config)` -> UI_MainMenu, fog on) and is INTENDED to be byte-identical,
but several shared changes landed - glance at the login after any GlueScene edit:

- **Two-pass render** (opaque batches first with depth write, then blended/additive). The login
  worked before on raw file order only because its batches happened to be opaque-first; two-pass
  is the correct order and keeps it the same. See SYSTEM_CHARACTER_SELECT.md section 3a.
- **Directional lights now use the light BONE's +Z axis** (`bone_z_axis`, benilla-faithful), not
  the light position. The login's cool sky-fill directional is affected; the login is dominated by
  the brazier POINT lights so it should look the same. (Char-select's sun direction is still being
  worked - section 3c there.)
- **Per-instance booth-only knobs** `SceneTint` (default (1,1,1)), `SceneFillColor` (default 0),
  plus `uFogEnabled` - all default to no-ops for the login, so the login shader path is unchanged.

The `Realm` line now shows `RealmDisplayName()` (the realmlist NAME once connected, else configured
realm, else host:port), same helper the char-select banner uses.

## Key ground-truth references

- benilla login layout / flow: `login/screen.rs` (layout, BOX_FILL/BOX_BORDER,
  spawn_dialog), `login/mod.rs` (state machine, `to_select_on_roster`).
- benilla glue widgets/art: `glue/widgets.rs` (glue_button, glue_edit_box,
  outlined_text, caret_bar), `glue/art.rs` (palette, texcoords, BUTTON_TC), `glue/backdrop.rs`.
- benilla booth (shared login/create/select 3D scene): `portrait/glue_booth.rs`,
  `portrait/framing.rs`, `portrait/light.rs`.
- Real textures: `mpqpeek png "Interface\Glues\Common\Glue-Panel-Button-Up.blp"` etc.
