# Glue Screens (Login / Character Select / Character Create) - status & handoff

Scope: the 1.12 glue screens - the per-race 3D "glue booth" background, the character standing in
it, the 2D roster chrome, the character-CREATE chrome that shares the same booth, and the logon
progress dialog. This doc is the running record + handoff. (It started as char-select only; the
create screen grew onto the same machinery, so it lives here too - `SPEC_CHARACTER_CREATE.md` stays
the authored-layout reference, this is the as-built.)

STATUS (2026-07-28): phases 1-3 are BUILT and in the client. What works: the per-race 3D
background, the selected character built + dressed + standing on the stage framed by the scene's
own camera, and the full skinned 2D chrome (roster panel, name plate, buttons). What is NOT
finished: the per-race scene BACKDROP LIGHTING is close but its sun DIRECTION is still wrong (see
"THE LIGHTING SAGA" - this is the top open item), plus a handful of later-phase dialogs. The
CHARACTER's own face lighting was fixed 2026-07-28 (viewer-relative key light, section 3d). Read
"OPEN ISSUES" before continuing.

UPDATE (2026-07-29, second pass): a long tuning session with Nico took the create screen from
"drawn" to "dialled in", and closed several as-built gaps. In brief - the full record is in sections
9-11:
- The row-highlight TEXT layering is SOLVED (2a) - the panel moved, not the text.
- Drag-anywhere model rotation on BOTH glue screens; the char-select rotate pair is now the OG
  `UI-RotationRight-Big` art, under Enter World.
- The create screen's dial arrows, icon check-button highlight, hover/selected labels, faction
  banners, info-panel scrollbar and fixed panel heights are all benilla-faithful now (section 9).
- The logon progress screen is a real GlueDialog; Delete Character works end to end (section 10).
- Every hardcoded offset that got in the way became a live dial; the signed-off values are baked as
  defaults and listed in section 11.

UPDATE (2026-07-29): the selected/hovered row highlight is now the REAL 1.12 art drawn with TRUE
additive blend (alphaMode="ADD"), with live brightness/crispness dials - baked defaults in section
2a. The text-layering piece is now CLOSED: the glow composites under the single ImGui pass (so the
row text is naturally in front, benilla order) and the roster panel's FILL is cut away in a band
behind the card so the glow is not dimmed. No second ImGui frame, no GL text pass - interaction is
untouched. See "2a - text layering: SOLVED".

The two hardest pieces the original plan leaned on both proved out: the dressed 3D character
pipeline (SYSTEM_CHARACTER.md) and the fullscreen glue-scene machinery (SYSTEM_LOGIN.md /
GlueScene). Char-select glued them together.

benilla is the byte-faithful ground truth. On this machine the FULL benilla repo is at
`C:\Users\nico\Desktop\benilla-main` (read the device copy; the cloud upload is a partial subset).
The reference files that mattered this session are indexed at the bottom.

## Files touched this session (all under MSUIClient/ unless noted)

- `Engine/GlueScene.cs` - GENERALIZED from the login-only UI_MainMenu scene into a reusable UI_*
  glue-scene renderer, plus every lighting change below. The login still constructs it exactly as
  before (`new GlueScene(gl, mpq, config)` -> UI_MainMenu, fog on) and is meant to be byte-identical.
- `Engine/GlueBooth.cs` - NEW. The char-select booth: owns the per-race GlueScene + a booth-owned
  CharacterRenderer, does the camera lock + stage placement, and holds `BoothTune` (the dev knobs).
- `Program.Net.cs` - booth wiring (`_booth`, `InitNet`, `DrawCharacterSelectScene`, dispose on
  enter-world), the SKINNED `DrawCharacterSelect` (replaced the debug ImGui window), the
  `DrawBoothTuning` modal, and `RealmDisplayName()`.
- `Program.cs` - one line: `DrawCharacterSelectScene();` right after `DrawGlueScene();` in Render().
- `Net/NetworkClient.cs` - added `RealmName` (published from the realmlist at logon) so the chrome
  shows the realm NAME, not host:port.
- `Engine/UI/GlueAdditive.cs` - NEW (2026-07-29). The TRUE-additive (alphaMode="ADD") GL overlay for
  the row highlight, the NORMAL-blend lane (`EnqueueBlend`) that lays the panel patch under it, and the
  GL-text pass from the abandoned on-top attempt. See 2a.
- `Engine/UI/WowSkin.cs` - the `glue.select.hi` (+ `.raw`) highlight art, `TextureHandle()`, the
  `GlueTune.SelectHi*` dials, the hole-punching `DrawBackdrop` overload + `FillStrip`, and
  `BackdropFillSlice` (the GL-side patch geometry) (2026-07-29).
- `Engine/ClientWindow.cs` - `OnOverlay` / `OnOverlayTop` hooks: dedicated GL passes composited UNDER
  / OVER the ImGui HUD, for the additive highlight (2026-07-29).
- `Program.Settings.cs` - constructs `GlueAdditive` (guarded; falls back to the straight-alpha
  highlight if the GL setup throws).

Second pass (2026-07-29), on top of the above:
- `Program.CharCreate.cs` - the whole create-screen chrome + `CreateTune` (its dial bank).
- `Net/WorldSession.cs` - `DeleteCharacter` (CMSG_CHAR_DELETE -> SMSG_CHAR_DELETE).
- `Net/NetworkClient.cs` - `ParkReq.Delete` + `DeleteCharacter` / `TryTakeDeleteResult`, and the
  **login retry fix** (section 10.3).
- `Engine/UI/WowSkin.cs` - the spinner-arrow art, the icon check-button highlight (+ its white-mask
  rebuild), the scrollbar art, `GlueArrowButton`, `BackdropFillSlice`, and the char-select chrome
  dials.
- `World/Units/CharacterRenderer.cs` - the SCALP/hairline composite + the blank-hair-row substitute
  (recorded in `SYSTEM_CHARACTER.md` section 1.5, not here).

## 1. The booth - per-race scene + character (phases 1 + 2)

### 1a. GlueScene generalized (Engine/GlueScene.cs)

GlueScene was UI_MainMenu-only. Now it takes a model path + a fog flag:
- `GlueScene(gl, mpq, config)` - the LOGIN. UI_MainMenu, fog on. Behaviour preserved.
- `GlueScene(gl, mpq, config, modelPath, fogEnabled)` - any UI_* scene. The booth passes a per-race
  model with `fogEnabled: false` (benilla renders the char-SELECT race scene UNFOGGED; char-CREATE
  keeps fog; MainMenu is always fogged - byte-verified fork, benilla glue_booth.rs:476-479). Fog-off
  is one shader uniform (`uFogEnabled = 0`); the login passes 1 so its shader path is unchanged.

New public surface used by the booth:
- `Eye`, `Target`, `FovDiag`, `NearPlane`, `FarPlane`, `Ambient` - the authored camera 0 + rig,
  in the glTF Y-up space the mesh lives in.
- `StageSpot` (Vector3?) - attachment 0 of the scene model (where the character stands). Parsed
  from `model.Attachments` (M2Attachment.Position is already absolute model-space Y-up).
- `PrimaryLightDir` - the scene's dominant DIRECTIONAL light as a horizontal Y-up to-light dir
  (the "sun"), used to aim the supplemental fill.
- `SceneTint`, `SceneFillDir`, `SceneFillColor` - booth-only lighting knobs (see the lighting saga).

### 1b. GlueBooth (Engine/GlueBooth.cs)

Owns the scene + the character. `SetCharacter(Character c)` switches the background to the race's
scene (`SceneToken`: Troll shares Orc, Gnome shares Dwarf, Undead->Scourge; Orc placeholder before
a roster) and builds the dressed model with the exact `ApplyServerCharacter` recipe (race/gender +
appearance bytes + the 19 equipment display ids -> Load/Reload). Latches the built guid so a pick
whose model can't load is attempted once, not every frame.

### 1c. THE CAMERA LOCK (phase 2b) - proven, not guessed

benilla renders the character through the scene's OWN authored camera 0 and stands it on attachment
0 (one camera, one depth buffer - glue_booth.rs). MSUIClient's problem: the scene mesh is glTF
Y-up, but CharacterRenderer works in WoW Z-up with its own orbit Camera. The bridge:

- `R = RotX(+90)`, i.e. `ToZup(x,y,z) = (x, -z, y)`. It sends Y-up +Y (up) to Z-up +Z (up).
- Put the character at `ToZup(StageSpot)` and give its Camera `Position = ToZup(sceneEye)`,
  `EyeTarget = ToZup(sceneTarget)`, up = +Z. Then character clip == scene clip, so they lock and
  depth-test against each other. The orbit-camera params are solved from those (Target = ToZup(tgt),
  EyeHeight 0, pitch = asin(d.z/|d|), yaw = atan2(-d.y,-d.x), dist = |d|,
  FieldOfViewDegrees = (FovDiag / sqrt(1+aspect^2)) in degrees, near/far = scene near/far).
- VERIFIED NUMERICALLY before shipping (replicated .NET's CreateLookAt/CreatePerspectiveFieldOfView
  in python): a scene point and its ToZup image project to identical NDC to ~2e-16, and "up" stays
  up. So the feet land exactly on attachment 0, upright.
- Facing: the character is yawed to face the camera. Model forward maps to world (sin h, -cos h, 0),
  h = Yaw + 90deg, so `baseYaw = atan2(n.x, -n.y) - pi/2` where n = horizontal dir to the eye. A
  `CharYawDegrees` fine-tune sits on top (flip 180 if it ever faces away).

Depth is SHARED with the scene (no depth clear) - the two-pass render (below) lays down opaque
depth first, so the character occludes/composites correctly.

## 2. The 2D chrome (phase 3) - Program.Net.cs DrawCharacterSelect

Full-bleed skinned chrome over the booth, same WowSkin approach as the login (logo, tinted
Glue-Tooltip backdrop, GlueButton, GlueText), scaled to a 1024x768 canvas by s = height/768:
- WoW logo TOPLEFT (3,7); right-column frame 260x642 at TOPRIGHT (-5,-15) with the realm banner,
  a DISABLED Change Realm, up to 10 rows (name / "Level L Race Class" / ghost) with the REAL additive
  highlight on the selected/hovered row (see 2a), and a Create New Character at the bottom.
- Selected NAME over the model (raised clear ABOVE the Enter World button); Enter World bottom-centre
  flanked by a rotate pair (`<`/`>`, hold to spin - drives CharYawDegrees); Back + a DISABLED Delete
  Character bottom-right; AddOns bottom-left.
- DRAG THE MODEL TO SPIN IT (2026-07-29, benilla char_select/input.rs `rotate_model`): a full-height
  InvisibleButton over the booth, left of the roster panel, registered LAST in the window. ImGui gives
  hover to the FIRST item that claims a position, so every real widget above it wins the hit test and
  the catcher only ever sees the scene - no widget is shadowed, and drags on the roster panel do
  nothing, same as 1.12. Left-drag turns the model at `BoothTune.DragRotateDegPerPx` (0.2 deg/px
  after Nico found 0.4 too twitchy; live slider), in the same direction as the `>` button; the facing is
  wrapped to [-180,180] each frame so the tuning slider stays meaningful.
- Dev "tune" text top-right toggles `DrawBoothTuning` (the BoothTune sliders as a modal).

Disabled buttons (Change Realm / Delete / Create) + the AreaTable zone name on each row are the
later phases. Row location currently shows level/race/class only.

Realm banner + login realm line now show `RealmDisplayName()` = `_net.RealmName` (from the
realmlist, e.g. "Barrens Chat") if connected, else configured realm, else host:port.

## 2a. The row highlight - TRUE additive (2026-07-29)

The selected/hovered row highlight is now the REAL 1.12 art drawn with the REAL blend, not the
rounded-rect approximation.

GROUND TRUTH (benilla): the highlight is `Interface\Glues\CharacterSelect\Glue-CharacterSelect-
Highlight.blp`, drawn as an `AddUiMaterial` (`crates/benilla/src/glue/add_material.rs`) whose pipeline
blends `SrcAlpha / One` - WoW `alphaMode="ADD"` (`dst + src*srcAlpha`). `char_select/screen.rs:417`
draws it as ONE 256x74 card, lit for `selected` OR `hovered`. add_material.rs's own docs say it
REPLACED an alpha-encode approximation (`a' = a*max(r,g,b)`, NORMAL blend) that "could only fake the
add over dark backgrounds" and "darkened the art instead of brightening it" - EXACTLY the failure the
first straight-alpha attempts showed (hard line, no transparency, no glow). Straight alpha was the
wrong blend, full stop.

THE ART (decoded with `tools/mpqpeek/mpqpeek.py stat/png`, not guessed): the BLP is 256x64, DXT,
alphaDepth 0 (OPAQUE - no alpha channel), min RGBA [0,0,0,255] max [255,227,99,255]. It is a bright
yellow rounded BORDER around a MEDIUM olive interior (~53% brightness). Drawn additively at high gain
the mid-tone interior saturates alongside the border and the frame stops standing out - the "washed
out" look. The fix is a CONTRAST (power) curve that drops the mid fill far more than the bright border
(the same square-law benilla's own FFXGlow uses), then a GAIN for overall brightness.

HOW IT'S DRAWN: `Engine/UI/GlueAdditive.cs` is a DEDICATED GL pass - never an ImGui draw callback
(`dl.AddCallback` flipping blend state INSIDE the Silk ImGui render CRASHED the loop: "You cannot call
'Reset' inside of the render loop!"). DrawCharacterSelect ENQUEUES the card as an additive quad;
ClientWindow flushes it via `OnOverlay` (composited UNDER the ImGui HUD) or `OnOverlayTop` (OVER it).
It uses the RAW (non-luma) copy `glue.select.hi.raw` so black adds nothing and only the bright rim
adds light. Frag: `shaped = pow(tex.rgb, uContrast); frag = vec4(shaped * uTint.rgb * uGain, tex.a *
uTint.a)`, blended SrcAlpha/One. If the GL setup throws, `GlueAdditive` stays null and the highlight
falls back to a straight-alpha draw (still legible, just not additive).

DIALS (Character-Select Booth Tuning modal; `GlueTune.SelectHi*`; baked defaults 2026-07-29, Nico
signed off on the look):
- Row highlight colour R198 G255 B0 A191 (`SelectHi`; A = coverage / overall scale)
- Highlight brightness (ADD gain) 3.08 (`SelectHiGain`)
- Highlight crispness (contrast) 2.20 (`SelectHiContrast`) - the dial that un-washed it
- Glow on top OFF (`SelectHiOnTop`) - the glow draws UNDER the ImGui pass so the row text is in front
- Panel behind the glow ON (`SelectHiPanelHole`) - the panel band is re-drawn under the card, undimmed
- Roster panel opacity 0.80 (`RosterAlpha`) - the glow is no longer dimmed by the panel, so this is
  purely how much cobblestone reads through the roster column
- Card geometry `SelectHiInsetX` 2.2 / `SelectHiTop` -9.3 / `SelectHiHeight` 76.9
`Log booth values` also prints the `[glue-tune]` SelectHi* line to bake.

WINS: additive matches the OG - crisp bright border, translucent interior, cobble reads through. The
wash was diagnosed by DECODING THE ACTUAL BLP (bright border + medium fill) and answered with the
contrast curve, not by guessing tint values.

### 2a - text layering: SOLVED (cut the panel, not the frame)

benilla order is panel -> glow -> TEXT: the row text sits IN FRONT of the ADD card. Two attempts to get
there by moving the TEXT failed; the fix was to move the PANEL instead.

- Attempt 1 (REVERTED): a SECOND ImGui pass to re-draw the lit-row text over an on-top glow. It BROKE
  all interaction - a second `_imgui.Update()` per frame starts a phantom ImGui frame, and ImGui needs
  ONE continuous frame to track hover/click across frames. No clicks, no selection. Removed.
- Attempt 2 (REVERTED as the default, code still present): a GL text pass re-drawing the lit row's
  name/level/zone as glyph quads from the ImGui font atlas (`EnqueueText` / `SetGlueFont` /
  `DrawQueuedText` / `DrawString` in GlueAdditive), right after the glow in the SAME pass. Interaction
  stayed intact, but the atlas glyphs never showed. Still wired for `SelectHiOnTop` mode only.
- THE FIX (CURRENT DEFAULT): neither the text nor the card moves - the PANEL does. Three parts:
  1. The glow goes back UNDER the single ImGui pass (`SelectHiOnTop = false`), which puts the row text
     in front for free and touches nothing about interaction.
  2. The panel would dim it from up there, so the panel FILL moves down with it. The WHOLE fill is
     drawn in the GL pass: `WowSkin.BackdropFillSlice` hands out its rect/UVs/texture (identical tiling
     maths to DrawBackdropFill) and `GlueAdditive.EnqueueBlend` draws it NORMAL-blended. Flush runs two
     sub-passes - every blend quad first, every additive quad after - so the on-screen order is
     panel -> glow -> text, exactly benilla's.
  3. ImGui then draws only the nine-sliced EDGE, via
     `WowSkin.DrawBackdrop(..., IReadOnlyList<Vector2>? fillHoleBands)` with one band covering the whole
     frame. (The overload is general - it clips the fill into strips around any set of bands - but
     char-select now passes the full height.)
  TWO REJECTED CUTS, both worth not repeating:
  - The hole with NO patch: it deleted the roster panel's background behind the lit row and showed raw
    cobblestone through it.
  - The hole patched per-BAND (just the card's own strip): correct in principle, but it left a hairline
    under the lower card. A band edge is a seam between two rasterisers - ImGui clips with an integer
    glScissor, the GL quad rasterises by its own pixel-centre rule - and snapping the edge to whole
    pixels did not reliably close it. Moving the ENTIRE fill leaves no internal boundary to misalign.
  `DrawCharacterSelect` still computes the row geometry + the lit rows BEFORE the panel (`hoverRow` is a
  read-only mouse hit test gated by `ImGui.IsWindowHovered`; the rows' InvisibleButtons still own the
  clicking).
- Net effect: one ImGui frame, one GL pass, glow at full undimmed ADD strength BEHIND the text - the
  same composite the old "Glow on top" mode produced, so the baked SelectHi* values still apply.
- Dial: `SelectHiPanelHole` (default ON, "Panel behind the glow, not over it"). Untick it for the old
  dimmed under-mode; tick "Glow on top" to go back to the glow-over-the-text look.

## 3. THE LIGHTING SAGA (read this - the scene lighting is the deep part, and partly OPEN)

The per-race scenes exposed several issues the login never did. In order found/fixed:

### 3a. "Screwed-up street" - RENDER ORDER (FIXED, root cause)

The foreground looked broken: the opaque water/caustic painted over the alpha-blended street.
Cause: GlueScene drew all batches in raw FILE order in one loop. UI_Human's `STREET 02` is blend 2
(alpha, drawn early, no depth write); the OPAQUE `CAUSTIC02` water + buildings draw later and painted
over it. benilla (model_render.rs:177-189) splits M2 blend modes into an OPAQUE pass (Opaque/
AlphaTest) then a TRANSPARENT pass (Blend/Mod/Mod2x/Add), which Bevy draws opaque-first. FIX: GlueScene
now renders TWO passes - opaque/alpha-key first (depth write ON), then blended/additive over them.
The login worked before only because its batch order happened to be opaque-first; two-pass keeps it
correct. This was confirmed CORRECT by Nico.

### 3b. Booth brightness/warmth + floor fill (booth-only, login-neutral)

The race scenes lack the login's brazier POINT lights, so they read dark/cool. Two booth-only knobs
were added to GlueScene, both defaulting to a no-op so the login is byte-identical:
- `SceneTint` (Vector3, default (1,1,1)) - a multiply on the LIT (non-emissive) geometry. Applied to
  EVERY lit surface incl. the alpha-blended street (the unlit caustic water/sky/clouds take the
  emissive path and are never boosted, so they can't blow out). Booth drives it from
  BoothTune SceneBrightness x SceneWarmth.
- `SceneFillDir`/`SceneFillColor` (default color 0 = off) - a supplemental SH fill lobe that lights
  up-facing surfaces (the floor/pathway) which the grazing authored rig leaves flat. The booth aims
  it along the scene's own sun (PrimaryLightDir) raised to a fill elevation, warm-tinted.

### 3c. Directional light DIRECTION = bone_z (IMPLEMENTED, but sun is STILL WRONG - TOP OPEN ITEM)

The console dump of UI_Human's rig showed both directional lights nearly HORIZONTAL
(`posdir (-0.79, 0.00, 0.62)` and `(-1.00, 0.00, -0.04)` - note the 0.00 vertical). That is because
MSUIClient was using the light's POSITION as the direction. benilla does NOT: a directional light's
to-light direction is its BONE'S local +Z axis (benilla-formats records.rs `bone_z_axis`;
glue_booth.rs:250 folds this and calls the position a "decoy - a directional's direction comes from
bone_z alone", test line 944).

IMPLEMENTED this session in GlueScene.ParseLights + `BoneZAxis` (a faithful port of benilla's
`bone_z_axis`):
- Bone table `nBones@0x34 / ofsBones@0x38`, stride `0x6c`. Per bone: flags `u32@4` (bit 0x04 = keep
  the model-root orientation, stop inheriting), parent `i16@8`, rotation M2Track `@0x28`.
- Take the FIRST rotation key of each bone (M2Track values `count@0x14 / ofs@0x18`, a 16-byte f32
  quaternion [x,y,z,w] - vanilla v256 is uncompressed), compose them up the parent chain
  (`global = root o ... o local`, Hamilton product), and apply to (0,0,1). Rotationless chains ->
  (0,0,1) = up.
- Quaternion helpers `QuatMul`/`QuatRotateZ` were VERIFIED numerically (rotX90 sends +Z->-Y,
  identity->+Z, matching benilla's closed forms). The bone axis is then converted WoW Z-up -> Y-up
  via `ToYUp` (same conversion the mesh verts use).
- GlueScene now logs `[glue] light rig: ... sun dir (x,y,z)` on load (the bone_z-derived direction).

STILL WRONG per Nico ("still wrong direction") after this change. So bone_z alone did NOT fix it.
Leads for the next engineer (do these BEFORE more tuning):
  1. CAPTURE the printed `[glue] ... sun dir (x,y,z)` for UI_Human and compare against the expected
     OG sun (upper-left, warm). If it prints ~(0,1,0) the UI_Human light bones have no rotation keys
     and bone_z is degenerate-up - meaning the sun angle must come from somewhere else (re-read
     benilla: is the position used after all for these, or is there a scene/global light benilla adds
     that MSUIClient doesn't? check glue_booth.rs scene_rig + light.rs again).
  2. VERIFY the rotation-key format for the ACTUAL file. UI_<Race> may load from `.mdx` (GlueScene
     tries .m2 then .mdx). Confirm the loaded bytes are M2 format and the rotation values really are
     16-byte f32 quats at the offsets above - if they are compressed/relative, the quat read is
     garbage and bone_z comes out wrong. (M2Reader parses vanilla bone rotations as 4-float quats, so
     this is expected, but the specific file should be checked.)
  3. CHECK the axis convention end-to-end: benilla folds `wow_to_bevy(bone_z)`; MSUIClient uses
     `ToYUp` = (x,z,-y). Confirm benilla's coords::wow_to_bevy matches that exactly (sign of each
     axis) - a flipped axis would put the sun on the wrong side. Also confirm the SH lobe direction
     (to-light vs from-light) is consistent between benilla and the MSUIClient shader.
  4. Only after the direction is correct: the fill (3b) becomes redundant - turn its default down or
     off, and re-tune SceneBrightness/SceneWarmth against OG.

The colours were also pulled toward OG (defaults below) but Nico's read is the DIRECTION is the
problem, not the tint - so fix the direction first.

### 3d. CHARACTER key light = VIEWER-RELATIVE (IMPLEMENTED 2026-07-28, addresses Nico's face-lighting note)

3c is about the BACKDROP (scene mesh) sun. Nico's follow-up was about the CHARACTER: his face was lit
from his left (viewer's right) with the far cheek in shadow, where OG lights the character's front-RIGHT
(viewer's front-LEFT) and reads fuller. Root cause: the booth set the character's ambient/sun INTENSITY
but never its sun DIRECTION, so `CharacterRenderer.SunDirection` stayed at its fixed world-space default
`normalize(0.45, 0.35, 0.82)`. A single WORLD vector cannot be right for every race, because each UI_<Race>
scene frames the character from its own camera orientation - so that one vector lands on a different cheek
per scene (screen-right on some, screen-left on others; numerically verified).

FIX (GlueBooth.cs): the character key light is now computed VIEWER-RELATIVE every frame from the camera
(scene Eye) and the stand point, via `KeyLightDir(eye, stand, az, el)`:
- `toCam` = horizontal unit vector stand->eye = the "toward the viewer" (front) direction.
- `viewerLeft` = `(toCam.Y, -toCam.X, 0)` = `-(up x toCam)`. In this WoW Z-up RIGHT-handed world
  (+X north, +Y west, +Z up), .NET `CreateLookAt` makes screen-right = `up x toCam`, so its negation is
  the screen's LEFT. Because the character is yawed to face the camera, screen-left == the character's
  own RIGHT cheek.
- `to-light = normalize( (cos az * toCam + sin az * viewerLeft) * cos el + up * sin el )`.
  `az` > 0 swings toward the viewer's left; `el` lifts it above the horizon.
Defaults `CharKeyAzimuthDeg 35`, `CharKeyElevationDeg 40` = an upper front-left three-quarter key, the OG
glue look. Two new BoothTune knobs + two sliders in the tuning modal ("Key azimuth deg" / "Key elevation
deg") tune it live; `Log booth values` now prints `keyaz`/`keyel`.

WHY VIEWER-RELATIVE, not the scene's authored sun: benilla lights the glue character with the scene rig
(3c's bone_z), but that path is still degenerate here, and OG glue lighting reads as a fixed SCREEN-space
three-quarter key anyway. Tying the key to the camera (not the model) also makes the `< / >` rotate turn
the character INTO the light like a real sun, instead of the lit side riding his body. Verified: the key
stays on the screen's upper-left for every camera orientation (replicated CreateLookAt's basis in python),
where the old fixed vector wandered side-to-side. This is the CHARACTER only; the BACKDROP sun (3c) is
untouched and still open. If 3c's bone_z is ever fixed, revisit whether to fold the character onto it.

## 4. Dev tuning - BoothTune (in GlueBooth.cs), the "tune" modal

ONE set of knobs drives BOTH glue screens: character SELECT and character CREATE render the same
booth character through `GlueBooth.Render`, so a preset baked here applies to both.

The CHARACTER-LIGHTING block is Nico's SIGNED-OFF preset (baked 2026-07-29 from the modal):
  AmbientIntensity 0.456, SunIntensity 0.555, CharSunWarmth 0.318, CharAmbientWarmth -0.190,
  CharShadowSoftness 0.226, CharKeyAzimuthDeg 29.320, CharKeyElevationDeg 19.545,
  DragRotateDegPerPx 0.200.
Change these only against a side-by-side with 1.12, and re-bake from `Log booth values`.

The rest, NOT signed off (the backdrop sun is still wrong - section 3c):
  CharScale 1.00, CharZOffset 0.00, CharYawDegrees 0, AutoRotate off / 30 deg-s,
  SceneBrightness 1.022, SceneWarmth 0.04 (backdrop tint),
  SunFillIntensity 0.00, SunFillElevDeg 45, SunFillAzimOffsetDeg 0, SunFillWarmth 0.06 (floor fill).
`Log booth values` prints a `[booth-tune] ...` line to bake dialled-in numbers as defaults.

## 5. OPEN ISSUES / next steps (priority order)

1. **BACKDROP sun DIRECTION still wrong** (section 3c) - the top item for the SCENE mesh. bone_z is
   implemented but the result is still off; work the 4 leads above. This gates the backdrop colour tuning.
   NOTE: the CHARACTER's own key light is no longer waiting on this - 3d gives it a viewer-relative key
   (defaults + live sliders). 3c is now specifically the backdrop/floor lighting.
2. ~~Row-highlight TEXT layering~~ - CLOSED 2026-07-29 by the panel-hole approach (section 2a). If the
   band edges ever read wrong, the dials are `SelectHiPanelHole` / `SelectHiTop` / `SelectHiHeight`.
3. **Create screen: confirm Randomize** (section 9.6) - read the `[cc] randomize -> dials` line
   before touching anything; the two possible causes are named there.
4. **Create screen leftovers** - Accept currently creates; the realm-select modal is still absent;
   the create screen's own Back/Accept placement has not been dialled against 1.12.
5. **Water submesh** - `CAUSTIC02` is blend 0 unlit (opaque). It reads OK now the render order is
   fixed, but confirm against OG whether it should be additive/animated.
6. **Transparent-vs-transparent sorting** - the two-pass fix does NOT sort within the transparent
   pass (file order). benilla sorts back-to-front (Bevy). Add if the animated GROUNDSHADOW / clouds
   layer oddly.
7. **Right-panel colours** - Nico flagged the roster panel tint is a bit off (low priority; `RosterAlpha`
   is now a live dial).
8. ~~Drag-anywhere rotate~~ - DONE 2026-07-29 (section 2). Drag the booth to spin the model; the
   `<`/`>` buttons still work.
9. ~~Later-phase dialogs~~ - Delete confirm and the Create New Character screen are DONE (sections
   9-10). The realm-select modal is the one left. AreaTable zone names on the rows are in.

## 6. Known-not-a-bug

Characters don't FULLY dress (bare legs/boots/gloves, weapon floats) because MSUIClient does not
mount the client's PATCHED MPQs (patch-*.MPQ) - the missing gear textures/models live there. This is
a data/MPQ-mount gap, NOT a booth or gear bug. Do not chase it or log it as an error; it fills in
once the patch MPQs are mounted.

## 7. benilla ground-truth reference index (device: C:\Users\nico\Desktop\benilla-main)

- `crates/benilla/src/portrait/glue_booth.rs` - THE booth: scene_token (race->model), scene_fog +
  the create/select fog fork (476-479), `scene_rig` (228-258: directional -> SH lobe with to-light =
  bone_z, point -> falloff 1/(0.7d+0.03d^2)), camera 0 assert (655-675), attachment-0 stage (517),
  yaw (838-842). The whole scene renders on ONE camera/layer, opaque-then-transparent.
- `crates/benilla/src/portrait/framing.rs` - attachment_point (bind bone global + offset), the
  diagonal->vertical FOV `fovy = fov / sqrt(1+aspect^2)`.
- `crates/benilla/src/portrait/light.rs` - the booth light lanes; the character is always UNFOGGED.
- `crates/benilla/src/model_render.rs` - M2 blend mode -> render state: Opaque/AlphaTest = opaque
  pass, Blend/Mod/Mod2x/Add = transparent pass (177-189). This is the two-pass source.
- `crates/benilla-formats/src/models/records.rs` - `bone_z_axis` (168), `read_m2_light` (204),
  `parse_m2_lights` (229), the quat helpers `quat_mul`/`quat_rotate_z`/`track_first_quat` (126-160).
  THE source for the directional-light direction. M2 light record offsets: `type@0`, `bone@2`,
  `position@4`, diffuse colour@0x48 / intensity@0x64, atten start@0x80 / end@0x9c.
- `crates/benilla/src/char_select/{mod,screen,refresh,input,dialog}.rs` - flow, layout, row text,
  drag-rotate, delete dialog. THE FULL benilla repo is on the device, so screen/refresh/input/dialog
  ARE readable there (the cloud upload is the partial subset). `screen.rs:417` draws the row highlight
  as the ADD card at 256x74; `refresh.rs` toggles it for `selected` OR `hovered`.
- `crates/benilla/src/glue/add_material.rs` - `AddUiMaterial`, the WoW `alphaMode="ADD"` UI blend
  (`SrcAlpha / One`). Its module docs explain why the `a*max(r,g,b)` alpha-encode was RETIRED - the
  ground truth that the char-select highlight is TRUE additive, not straight alpha (see 2a).
- MPQ: `Interface\GlueXML\CharacterSelect.xml` (authoritative layout); `Interface\Glues\Models\
  UI_<Race>\UI_<Race>.mdx` (or .m2) per-race scenes; `...\CharacterSelect\Glue-CharacterSelect-
  Highlight.blp` (the row highlight art - NOW USED as a true additive overlay, see 2a; 256x64, opaque
  DXT, a bright yellow border around a medium olive fill).

## 8. Ground rules (unchanged)

- Verify empirically; benilla is the byte-faithful reference; read the FULL repo on the device.
- **A benilla identifier is not a spec.** Its marker is called `HoverLabel`, but the labels track
  SELECTION (section 9.3). Its `composite_body` comment says Human male has no hair columns, but the
  DBC says otherwise (`SYSTEM_CHARACTER.md` 1.5). When a comment and a screenshot/byte disagree,
  the screenshot/byte wins - and record which, so it is not "corrected" back.
- When a layout number gets in the way twice, make it a dial rather than re-guessing it. All three
  banks print a copy-pasteable line to bake from (section 11).
- Do not touch the login's model-space swirling particles.
- Files: docs LF, C#/shaders CRLF; keep C# comments ASCII where possible; shader STRINGS pure ASCII.
- The GlueScene changes (two-pass, bone_z, SceneTint/fill) are SHARED with the login - the login
  constructs GlueScene the same way and the booth-only knobs default to no-ops, so the login should
  be unchanged, but glance at it after any GlueScene edit. Shipped documentation stays server-agnostic.

---

## 9. The CHARACTER CREATE screen (as built, 2026-07-29)

`SPEC_CHARACTER_CREATE.md` is the authored layout; this is what shipped and where it diverged. It
renders through the SAME `GlueBooth` character as select (`SetCreateLook`), so the booth lighting
preset in section 4 governs both screens - there is one set of knobs, not two.

### 9.1 The dial spinner arrows are ART, not text

benilla `glue/art.rs fn arrow()` builds them from `Interface\Glues\Common\Glue-{Left,Right}Arrow-
Button-{Up,Down,Highlight}`, and `char_create/screen.rs dial_row` places them 32x32 at the row's
right. MSUI was drawing a text `<` / `>` on a tower plate - which is benilla's NO-ART FALLBACK path,
not its normal one. `WowSkin.GlueArrowButton` now mirrors `glue/widgets.rs dial_arrow`: `-Up`
normally, `-Down` while held (with the 1px pushed offset), `-Highlight` on hover. The text buttons
remain as the fallback, exactly as the reference falls back.

### 9.2 The icon check-buttons: no border, a HELD highlight square

benilla `glue/widgets.rs icon_button`: a `ButtonHilight-Square` overlay lit on hover **and held while
selected** - "the template's `CheckedTexture` is commented out in the shipped 1.12 GlueXML, so the
locked square *is* the whole selected visual". The gold selected rect and white hover rect MSUI drew
were ours, not Blizzard's; both are gone.

Two traps this hit, in order:
1. Drawn through `HighlightAlphaFromLuma` (alpha from brightness, RGB kept) the square DARKENED the
   icon. That routine is right for a black-field/bright-rim sheet like `Glue-Panel-Button-Highlight`,
   but `ButtonHilight-Square` is a soft, fairly DARK blue-grey square, so mid-alpha composited a dark
   film and driving the dial harder only thickened it. It now loads through `WhiteGlowFromLuma` -
   **RGB forced white**, alpha still the texel's brightness - so straight-alpha blending lerps the
   icon TOWARDS the tint and can only ever lighten. `IconGlowColor` steers the hue (baked blue-white,
   matching the ref).
2. The glow covered only the ICON, while `cc.iconshadow` painted a ring 18% BEYOND it - so selecting
   an icon could not brighten the very thing making it look dark. `IconGlowBleed` is now the glow's
   size relative to the cell (negative = inside it, positive = spills over the shadow ring).

### 9.3 The icon LABELS track selection, not hover

benilla names the marker `HoverLabel`, and an earlier pass here took that identifier at face value
and wrote "1.12 is hover-only" into the code. **The 1.12 reference shot disproves it**: "Dwarf",
"Female" and "Warrior" are lit simultaneously and the mouse can only be over one. The label follows
`selected || hovered` - the same pair the highlight square lights. Go by the screenshot, not the
identifier.

### 9.4 Info panels: fixed heights + the real scrollbar

benilla `char_create/panels.rs right_stack` gives the three panels HARD heights (faction 160, race
260, class 210; 240 wide, 10 apart) and scrolls long text. MSUI auto-sized them to their content, so
raising the body font resized the layout instead of scrolling. Now fixed (with `PanelAutoSize` to get
the old behaviour back), and the scrollbar is the real one: `Interface\Buttons\UI-ScrollBar-
Scroll{Up,Down}Button-{Up,Down}` + `UI-ScrollBar-Knob`, with the decorative `UI-CharacterCreate-
ScrollBar-Top` / `UI-ClassTrainer-ScrollBar` track art behind, a 16-wide column inset 10 top and
bottom, arrows stepping half a track, a draggable knob, and the whole bar hidden while a panel fits
its text (the ref's `scrollBarHideable`).

> **Gotcha worth keeping**: those scroll button sheets are 32x32 but the control is only the CENTRE
> QUARTER (benilla `SCROLL_BTN_TC` = 0.25..0.75). Draw the full sheet and you get a button ringed by
> its own transparent margin.

The body block hangs off the TITLE (`PanelTitleTop` + `PanelBodyGap`), matching benilla's
title -> body -> abilities column, instead of a hardcoded title band.

### 9.5 The faction banners are TWO halves

`UI-CharacterCreate-Banners` holds both banners side by side and was drawn as ONE image stretched
across the tower, so they could only move and scale together - and only by resizing the tower. Each
half now draws on its own race column (Alliance uv 0..0.5, Horde 0.5..1) with its own
width/height/offsets, plus a `BannerSpread` that pushes the pair apart.

### 9.6 Randomize

`CharCreateState.Randomize` rolls all five dials against `DialCounts`, and the preview rebuilds off
`_cc.Dials` every frame via `SetCreateLook` - so the button needs to do nothing else. Nico reported
it as not working and no defect was found by reading; a `[cc] randomize -> dials a/b/c/d/e` console
line was added to split the two possible causes. **If the numbers change and the model does not, the
bug is the diff in `GlueBooth.SetCreateLook`; if they do not change, it is `DialCounts` returning
1s.** Do not re-audit the button.

---

## 10. Login / character-select flow work (2026-07-29)

### 10.1 The logon progress screen is a GlueDialog

1.12 flickers through the same states we do - connecting, authenticating, "Authentication
Successful", retrieving characters - each in a `GlueDialog`: the riveted `DialogFrame` box (the same
border the in-game menus use), a gold caption, one Cancel button. MSUI showed a bare ImGui window
printing the raw `NetState` enum. `DrawConnecting` now draws `WowSkin.Dialog` + `GlueText` +
`GlueButton`, with `LogonCaption()` mapping state -> the OG wording. Dials: `LogonBox*`,
`LogonTitlePx`, `LogonStatusPx`, `LogonBtn*`.

### 10.2 Delete Character works

`WorldSession.DeleteCharacter(guid)` sends CMSG_CHAR_DELETE (u64 guid) and awaits SMSG_CHAR_DELETE
(0x39 = CHAR_DELETE_SUCCESS, vmangos `Packets/Character.cpp`). It rides the SAME park channel as the
create (`ParkReq.Delete`), so the worker deletes and re-enumerates the roster **while still parked at
select** - the row disappears with no reconnect. The app polls `TryTakeDeleteResult`. The button
enables whenever a row is selected, and opens a confirmation GlueDialog naming the character
(benilla `char_select/dialog.rs`).

### 10.3 THE LOGIN RETRY BUG (fixed - read this before touching NetworkClient)

*"If I type in the wrong password I don't get to try again ... the login just stops doing anything."*

`Start()` was `if (_running) return;`. A bad password makes `RealmClient.Logon` throw
`AuthRejectException(0x04)`; the worker catches it, calls `Fail()` and **exits** - but nothing ever
cleared `_running`. The thread was gone while the flag still said "running", so every later `Login()`
hit that guard and returned silently. The flag was tracking *"a worker was started"*, not *"a worker
is running"*.

Fix: `Start()` gates on `_worker is { IsAlive: true }` (the actual thread) and joins/clears a
finished one; `Run()` got a `finally { _running = false; }` so the flag drops exactly when the worker
ends. The `when (_running)` exception filters are evaluated BEFORE `finally`, so a real error still
reports through `Fail()` and a `Stop()` still reads as shutdown. `ResetForNewAttempt()` wipes the
roster, session, queue and pending pick so a retry cannot inherit them.

> The rule: a flag that means "a thread exists" must not be used to answer "is work in progress".

### 10.4 Drag-to-rotate on both screens

benilla `char_select/input.rs rotate_model`: grab the model and spin it. Char-select gets a
full-height InvisibleButton over the booth, stopping at the roster panel's left edge, **registered
LAST in the window** - ImGui gives hover to the FIRST item that claims a position, so every real
widget wins the hit test and the catcher only ever sees the scene. No widget is shadowed and drags on
the panel do nothing, same as 1.12. Rate is `BoothTune.DragRotateDegPerPx`; the facing is wrapped to
[-180,180] each frame so the tuning slider stays usable. The char-select rotate PAIR is now the same
`UI-RotationRight-Big` `RotateButton` the create screen uses, sitting under Enter World.

---

## 11. The dial banks + the baked presets

Three tuning surfaces, three console lines to re-bake from:

| bank | where | log line | covers |
|---|---|---|---|
| `BoothTune` | `Engine/GlueBooth.cs` | `[booth-tune]` | the 3D booth: character lighting, scale, drag rate, backdrop tint/fill |
| `GlueTune` | `Engine/UI/WowSkin.cs` | `[glue-tune]` | shared glue chrome + the char-SELECT layout |
| `CreateTune` | `Program.CharCreate.cs` | `[cc-tune]` | the char-CREATE layout |

**Signed off by Nico 2026-07-29** (baked as the field defaults AND in each `Reset()`):

```
[booth-tune] scale 1.00 zoff 0.00 yaw 0 amb 0.46 sun 0.56 sunwarm 0.32 ambwarm -0.19 soft 0.23
             keyaz 29 keyel 20 scenebright 1.02 warmth 0.04 fill i0.00 el45 azoff0 w0.06
[glue-tune]  HoverGlow=0.775 SelectHi=0.78/1/0/0.75 Gain=3.08 Contrast=2.2 OnTop=False
             PanelHole=True RosterAlpha=0.8 InsetX=1.472 Top=-9.3 Height=76.9
             EnterWorld=187.3x60.6@30 text18.2 | ChangeRealm=h39.8 top32 inset60
             CreateChar=h48.6 bottom12 inset27.7 text14.3 | Rotate=54.4 gap-12.7 dx1.5 top-18.1
[cc-tune]    tower 28/49/194/617 | logo 271/0/0 content116 header15 | panel 240/28/20/10
             h160/260/210 ttop8 bgap13.2 line1.28 ins17/36/10 bar27x16 title15 body13
             picon -12.4/-6/42 | icon44 igap6 rgap26 banner 126.9x261.9 top-2 dx-2.5 spread17.8
             gender 44/3.3/0/15.3 label12+1 iglow0.71 bleed0 ishadow0.16
             dial27 dlabel13 arrow30.9x28 gap-3.5 right0.3 dy-0.9
             dbox pad20.3 left-8.1 gap-10 dval inset20.4 zone22.1 rnd 163x41 | rot 50/-8/164.7/20
```

Things that became dials this session because they were hardcoded and in the way: every panel/box
edge and inset, the dial plate and its two arrows independently, the gold value text, the icon
grid/gender/banner geometry, the hover-label size, the glow size and colour, the Randomize and
Enter World and Create Character and Change Realm buttons, the rotate pairs on both screens, and the
**caption size** of Enter World / Create Character (`GlueButton` now takes an optional explicit
`captionPx`; 0 keeps the old derive-from-height behaviour, which silently resized the text whenever
the button was resized).

---
