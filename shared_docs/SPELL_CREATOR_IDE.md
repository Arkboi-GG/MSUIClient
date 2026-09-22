# Spell Creator IDE — stage, effect clock, gizmos (2026-09-10)

Owner's brief (2026-09-10): the spell workshop in Creator Mode is powerful but
"not quite a proper IDE for spells". Three asks, in the owner's words: a toggle
that blacks out everything but the ground around the character; a way to pause
the in-progress effect and edit it while paused; and a 3D reference grid with
the character as the priority, so an emitter's ORIGIN and DIRECTION are visible
and a new emitter can be PLACED by sight ("right now I can get an emitter to
cast backwards from the back of the character, but I don't understand its
origin point nor the rotation").

This document is the design authority for those three features and the law for
how they interact with the existing workshop. `docs/current/creator/CREATOR_MODE.md`
(ignored scratch) describes the workshop itself; `SPELL_EMITTER_LAB.md` there is
the empirical emitter model this builds on.

## 1. Why the workshop felt blind: the frame chain

An emitter's world placement is FOUR frames deep, and nothing on screen showed
any of them:

1. **Caster attachment** — the kit's attachment id resolved on the caster's
   live skeleton every frame (`SpellAttachment`), in the caster's facing.
2. **Effect root** — the effect M2's origin, placed at that attachment.
3. **Emitter bone** — the emitter rides a bone of the effect M2; an animated
   bone sweeps the birth frame through space (Cleave's crescent IS this).
4. **Raw offset** — the `+0x008` vec3 the panel's "Local position" edits, in
   WoW's Z-up local frame of the effect M2 (the panel patches raw bytes).

Census evidence (`tools/spell-emitter-lab`, `dumps/spell-emitter-census.csv`):

| Spell | What the data says |
|---|---|
| Cleave cast (`Cleave_Cast_Base.m2`) | three emitters at about X −0.6, Y +0.5, Z +1.5 on animated bones 7/8/9. Negative X is BEHIND the caster. The crescent is the bone sweep, not the emitter. Its rate track is animated with a zero first key, so the panel shows rate 0. |
| Cone of Cold (`ConeofCold_Hand.m2`) | every emitter at 0,0,0 on bones 3..13; a 45° spread and speed −27.8 on the cloud/snowflake emitters. Negative speed travels AGAINST the bone frame. |

So "where does it start / where does it end / which way does it face" are
questions about frames 1–3 and the SIGN of speed. The answer is a spatial view,
not more sliders.

## 2. The three features

### 2.1 Void stage (`GameLoop.Creator.Stage.cs`)

A sibling of Creator X-Ray: while the stage is on, buildings, doodads, foliage,
water and sky are switched off, the clear colour is black, and the ground is
kept ONLY inside a disc around the acting body: radius `Settings.Creator.StageRadius`
(default 6 yd), height band ±`StageHalfHeight` (default 2 yd) around the feet.
Terrain and WMO floors both honour the disc (`uStageActive/uStageCentre/
uStageRadius/uStageHalfHeight` in `terrain.frag` and `wmo.frag`, camera-relative
like the roof cut); the disc edge fades to black over its outer 25 % so the
plinth reads as a plinth and not a cliff. Fog on the two ground renderers is
forced black for the same reason.

Laws: the stage never persists across a boot (like X-Ray it always starts OFF);
switching it on snapshots the renderer enable flags and switching it off
restores them; the stage and X-Ray are mutually exclusive (turning one on turns
the other off).

### 2.2 Effect clock (`GameLoop.Creator.Clock.cs`)

The whole spell presentation stack runs on ONE clock: `SpellClockNow` replaces
every uptime read on the spell path (the render pass's `spellNow`, the effect
source tick, `PresentSpellEffect`, the creator loop, the creator missile and area
spawns), and `SpellClockDelta` replaces the render `dt` fed to the particle
simulator. Outside the creator world the clock is a pass-through (uptime,
frame dt) — client mode is untouched.

In the creator world the clock owns:

- **Pause / resume.** Paused: delta 0, `now` frozen, the effect source is not
  ticked (self-terminating kits are not reaped), the creator loop does not
  re-present, creator audio is stopped. The caster's body is held on its exact
  frame (`UnitState.FreezePose`), and the spawned targets are held through a
  creator-owned `TacticalFreezePoseLaw` lock (id `CreatorPauseLockId`), the same
  latch the tactical freeze uses, so an impact victim's flinch freezes too.
- **Speed.** 0.1× … 1× while running (slow motion is the cheap first look).
- **Step.** ±1/30 s while paused.
- **Scrub.** A slider over the live effect's span while paused.

**Determinism law.** Step, scrub and every EDIT while paused go through
`ReplayCreatorEffects()`: the particle pools are cleared and re-simulated from
the earliest live instance's start to the target time with fixed 1/60 s steps.
Pool seeds derive from the pool key, so the same steps give the same picture;
pausing itself performs one replay so the paused picture is already the
deterministic one, and an edit then changes ONLY what was edited. Limits,
stated rather than hidden: the caster pose used during replay is the pose at
the paused instant (a bone sweep on the BODY is not re-walked; the effect M2's
own bones are); a missile's trail replays from its paused position, not its
flight path.

### 2.3 Grid and emitter gizmos (`GameLoop.Creator.Gizmos.cs`)

A line pass (`World/Spells/SpellGizmoRenderer.cs`, `gizmo.vert/frag`) drawn in
the debug pass after the world, depth-tested by default so the character
occludes it ("the character model is the priority"), with a Through-walls
switch. Geometry is built by the pure `SpellEmitterGizmoLaw` (no GL) from the
particle system's live pool frames (`SpellParticleSystem.EmitterFrames()`),
which are the SAME origin, linear frame and scalars the emission kernel uses —
the gizmo cannot disagree with the particles.

Grid: the owner's full cube lattice by default (`GridLattice`), in the acting
body's facing frame — minor cells of 0.25 yd (or a sixth of a yard, half a foot,
with `GridMinorSixthYard`), a brighter cage on the yard boundaries, reaching
`GridExtent` yards forward/left (default 3) and the same height up from the
floor. "The character model is the priority": every lattice line is clipped
against a vertical cylinder around the body (the controller capsule plus a
margin), so the model stands in a clear pocket instead of behind a fence of
lines. Off, the grid is three planes through the feet — floor, side and front.
The facing axes are coloured: forward red, left green, up blue. (2026-09-10
live feedback: the first build drew only the planes; the lattice is the ask.)

Per emitter, in the texture slot's identity colour (the same swatch the panel
rows wear):

- **Origin + frame**: a dot and a forward/left/up triad of the emitter's live
  linear frame (bone × effect root × attachment).
- **Birth shape**: plane → the L×W rectangle; sphere → rings at the two radii;
  spline → the origin only (20 emitters in the whole game).
- **Reach**: the boundary of the (vertical, horizontal) spread drawn as rays of
  length |speed|×lifespan with the sign of speed applied, and the parabola of
  the mean ray under gravity — start and end of the effect, drawn.
- **Parentage**: a thin line from the origin to the effect root (the caster
  attachment).
- **Label**: `e<index>` at the origin (ImGui background draw list; creator mode
  is a dev surface under the ImGui policy).

Selection: an emitter whose panel category is open, or whose header is hovered,
is drawn bold; the others dim. Ribbons get a diamond marker and an `r<index>`
label where they sit this instant (their position through their bone's live
frame) and the committed edge (−heightBelow..+heightAbove) as a line; the
selected phase's skeleton draws `b<index>` labels. Every label is clickable:
an emitter or ribbon label selects it in the tree, a bone label picks it for
the BONES section, a double-click frames it. The write half — drag handles on
the selection — is §2.10.

### 2.4 The layout (`GameLoop.Creator.SpellIde.cs`)

Owner, 2026-09-10: "drop the sidebars, bottom etc - keep a little back button up
top left ... we need as much visual space as possible." Agreed on the goal,
with one correction: drill-downs do NOT open further windows (that is window
management, and it overlaps the effect being tuned). The shape that holds up:

- **Strip** (top-left, one row): Back, the spell, the transport (Loop / Pause /
  Step / Speed), the view toggles (Stage / Grid / Gizmos / Tree / Inspector),
  Hide, dials, deck, help.
- **Outliner** (left, collapsible): spell → Loop / Clock / Stage / Audio /
  Session rows → phases → emitters (with the texture identity swatch). Hovering
  an emitter row draws its gizmo bold.
- **One inspector** (right, draggable, resizable, docked by default): the dials
  for the selection, swapped in place. Phase = model dials + textures + an
  on/off list of its emitters; emitter = that emitter's editor. `pin` keeps a
  copy open deliberately; `x` hides it until the next selection. The texture
  swap picker and the audio picker stay the only transient windows.
- **Timeline** (bottom, only while paused): step, scrub, readout, Resume, the
  live phases. Nothing there while running.
- **Hide**: every window folds to a "Show workshop" pill; TOGGLEUI (Alt+Z)
  clears the pill as well.
- **World → tree**: the emitter origin nearest the mouse (within 18 px) is
  hover-highlighted and a left click selects it. The click is not consumed from
  the gameplay targeting path; in the creator world that is harmless.

The selection is session state (`SpellIdeSelection`: fixed section, phase path,
or phase path + emitter index); a new spell heals it to the first phase. The
classic floating panel and the deck still draw the same registered sections;
the model editor was split into `DrawCreatorModelLook` (dials + textures) and
`DrawCreatorEmitterBody` (one emitter) so the IDE and the classic editor share
one body each. The two-sidebar focus layout, its `SpellFocusLayoutLaw` and the
`SpellFocusFraction` setting were retired; `Settings.Creator.SpellFocus` stays
as the permanent switch and the deck header's `IDE` button re-enters it.

### 2.5 Mesh layers and ribbons (`GameLoop.Creator.MeshRibbon.cs`)

Live feedback, 2026-09-10: with every Cleave emitter off, "I still have the rear
crescent, the front, and the outbound band". Correct observation: an effect M2
draws THREE kinds of thing and the workshop edited one. The crescent is mesh
geometry (textured planes swept by the model's bones; the GRADIENTC texture
with no Hue button is its tell), and slash streaks are ribbon emitters. Owner:
"we need to be able to edit all."

Same treatment as emitters, byte-level, so every edit previews live AND
travels in the patched M2 to the Completer:

- **Readers** (`Creator/M2MeshParser.cs`, `Creator/M2RibbonParser.cs`): view 0's
  batches as *mesh layers* (submesh, texture slot through the lookup, material
  flags + blend, colour/alpha first keys, transparency first key) and the ribbon
  records (bone, raw position, texture slot, material, edges/s, lifetime,
  gravity, sprite cells, height above/below, colour, alpha, visibility keys).
  Layout facts are the ones `M2Reader` already parses; the parsers state them.
- **Patch law.** A hidden mesh layer has its submesh's triangle count zeroed in
  EVERY view (the 1.12 client and MSUI both skip it; every layer stacked on that
  submesh goes with it). A material or texture-lookup change never writes a
  shared record: the array is relocated to EOF with a private entry appended
  and the batch (or the ribbon's material slot) repointed, the same
  resize-at-EOF strategy as `CloneEmitter`. Colour, alpha, transparency,
  ribbon heights write their FIRST key (the same first-key contract the emitter
  sliders have; a `*` marks an animated track). A silenced ribbon commits no
  edges (edges/s = 0) and has every visibility and alpha key zeroed.
- **Rebuild order.** Emitter edits and clones → hues → emitter off-switches →
  mesh/ribbon edits → texture swaps (the last resize). Appends never move what
  was written before them.
- **Where they show.** Tree rows `m<batch>` and `r<index>` under each phase
  (with the texture identity swatch and `[OFF]` FIRST, since the tree clips
  its tails); the phase inspector lists them with on/off ticks above the
  emitters; the mesh and ribbon inspectors carry the editors; the classic
  editor gets MESH LAYERS and RIBBONS categories. The session export records
  `hiddenSubmeshes`, `meshLayers`, `disabledRibbons`, `ribbons`.
- **Not built.** Ribbon gizmos (the ribbon renderer keeps its bone frames
  private); per-layer visibility when two layers share a submesh (the switch
  is per geometry); new keys on animated tracks.

### 2.6 Emitter completeness and bones (`GameLoop.Creator.Bones.cs`)

Owner, 2026-09-10: "we need this to be a full IDE ... I don't want 'well it
can do xy, but not z'". The remaining emitter block is now editable - the three
ARGB ramp colours with alpha, the ramp midpoint, head/tail mode, sprite-sheet
rows/columns, tail length, inherit scale, geometry tumble range, the bone it
rides and the image slot - and a *Sliders override animated tracks* switch
writes EVERY key of a scalar track so a slider can replace an authored ramp
(Cleave's rate, an impact's burst window). `M2EmitterParser` carries the
verified offsets (+0x14 bone, +0x16 texture, +0x2C head/tail, +0x30/+0x32
cells, +0x14C midpoint, +0x150 colours, +0x17C tail time, +0x190 inherit,
+0x19C/+0x1A8 tumble).

The rotation story, honestly told: an emitter has no rotation of its own, so
the workshop edits BONES (`Creator/M2BoneParser.cs`, 108-byte records at
header 0x34: pivot at +96, rotation track at +40, translation at +12). Per
phase model a BONES section picks a bone (b<n>), moves its pivot, poses it
(Euler degrees written as the rotation track's first key, or every key with
*Pose overrides animation*), and offsets its translation. Emitters and ribbons
re-parent to any bone. *Show bones* in the Void stage row draws the selected
phase's skeleton live (`SpellEffectSource.BoneFrames`) with b<n> labels so the
choice is made by sight.

### 2.7 Texture import (`GameLoop.Creator.TextureImport.cs`)

Your own art into any slot: *Import* on a texture row takes a PNG (converted to
BLP2 DXT3 with alpha at the nearest power-of-two size, 16..1024) or a BLP. The
bytes are served at a custom path (`Spells\Custom\<spell>\<name>_<n>.blp`)
by the MPQ mount's new override layer (`MpqMount.SetOverride`), so every
renderer reads them like archive art; the slot is swapped to that path through
the ordinary texture-swap patch, so the M2 names the file. The session ships
the bytes as a `tintedBlps` entry (`reason: import`) at that path, which the
Completer already writes into the patch as an extra file - no consumer change.

### 2.8 Phase composition (`GameLoop.Creator.Composition.cs`)

The kit level of a spell, above the per-model dials: per stage (precast, cast,
impact, state, channel) the caster animation (AnimationData id) and the nine
attachment slots in the 1.12 client's literal order (Head, Chest, Base, Left
hand, Right hand, Breath, Special 1..3), each holding an effect M2 path and a
scale; plus the missile model and scale. Any effect model in the game can go in
any slot - the picker searches spells and lists their models, or takes a path -
and a model new to the spell joins the tree as an editable phase (with its
geometry children). A stage the source never authored can be filled from
scratch.

Preview: `PresentSpellEffect` substitutes the composed `SpellVisualKitInfo`
(effects carry a `Scale`, premultiplied into the instance transform by
`SpellEffectSource`). Export: the session's `composition` block, only for
stages that changed, each with its animation and its COMPLETE slot list (an
absent slot clears the kit field), and the missile when changed.

### 2.9 The Completer side (MangosSuperUI, 2026-09-10)

`SpellVisualCloner.Clone` takes the composition: a composed stage is
authoritative (kit field 2 = animation, slots 3..11 written from the list, a
never-authored stage created from a template kit), every composed slot gets
its own `SpellVisualEffectName` row with the chosen path as `OriginalM2Path`
(so the patch builder reads THAT model from the archives, tuned bytes or not)
and field 4 = scale; the missile likewise, or cleared. The area kit (SpellVisual
field 13) is now cloned with the stage kits, so area audio lands on the spell's
own copy and the Completer's "area sound dropped" warning is gone. The
composition persists in the completer manifest and rides every rebuild;
`SpellPatchRequest.Composition` / `.MissileSpeed` carry it, and the Completer's
form gained *Missile speed* (spell_template.speed → Spell.dbc field 37).

### 2.10 Direct manipulation (`GameLoop.Creator.Handles.cs`, `World/Spells/SpellGizmoHandleLaw.cs`)

Owner, 2026-09-10: "we need emitter origins with the mouse, etc. The goal is a
true visual IDE that can be used easily enough."

The selection wears handles, drawn as a second, never depth-tested line pass so
a handle is always grabbable, sized in pixels (`WorldPerPixel`) so they are the
same size at any zoom:

- **Translate** (selected emitter, selected ribbon, picked bone with a
  translation track): a centre square (moves in the screen plane), three
  arrows in the LATTICE's frame (forward red, left green, up blue — a drag
  follows the grid), three plane squares. Shift snaps to the grid's minor cell.
- **Emitter shape**: the white diamond at the end of the mean reach arc sets
  SPEED (drag along the emission axis; the sign is kept, so a −27.8 cone stays
  a cone); the pink marks on the birth rectangle's +length / +width edges set
  AREA L / W; on a sphere the marks are the two radii. A zero-sized shape still
  gets a mark a few pixels out so it can be grown.
- **Rotate** (picked bone of the selected Phase, `Show bones` on): three rings
  about the bone's OWN live axes. Dragging a ring writes the exact quaternion
  the file must hold for the picture to turn by that angle, and turns on "Pose
  overrides animation" for an animated bone (a ring drag means "hold this").
  Shift snaps to 5°. The BONES dial shows the pose as Euler and, when moved,
  takes over from it.
- **Labels**: click `e`/`r` to select, `b` to pick the bone; double-click any
  label, or the strip's **Frame**, to centre the view on it (in the character
  view the orbit swings and zooms so the point sits on the line through the
  eye target; in the free view the rig is re-seated on it, the Command View
  pattern).
- **Undo**: Escape cancels a drag in flight (a pre-gate rung beside the dev
  editors', so the game menu does not open); Ctrl+Z or the strip's **Undo**
  restores the patch as it was before the last drag (64 deep). The inspector's
  own dials are not on the stack.

The mouse: while a handle is under the mouse or in hand the window's
`LeftButtonReservedForWorldClicks` is set (the dev editors' flag, with
bookkeeping so it is given back), which is what keeps a left-drag on a handle
from orbiting the camera. A drag rebuilds the model at ~12 Hz WITHOUT the
paused replay (`RebuildCreatorModel(model, replay: false)`) — the handle and
the gizmo move with the live frame — and once more with it on release.

The two frame facts the arithmetic rests on, both proven by the lab against
`M2Reader` on a real fixture:

1. The runtime's model space is the file's raw space with (x, y, z) → (x, z, −y);
   M2Reader swaps every position, pivot, translation key and quaternion
   imaginary part on parse. A world delta comes back through the live linear
   frame (`SpellEmitterFrame.LinearFrame` for an emitter, the bone's
   `skin[b] × transform` for a ribbon, the PARENT's for a bone translation)
   into model space, and `Unswap` takes it the last step into the bytes.
2. The animator composes a bone as S · R · T · parent, so the bone's world
   linear part is R_local · P. Rotating the world orientation by R_w about the
   dragged axis is R_new = world · R_w · P⁻¹ (`RotateBoneWorld`), the local
   rotation written as `BonePatch.Rotation` (raw quaternion, exact; wins over
   the Euler field).

Every drag writes the SAME patch the inspector writes (`EmitterPatch.Position*`
/ `EmissionSpeed` / `EmissionArea*`, `RibbonPatch.Position*`,
`BonePatch.Rotation` / `Translation*`), so nothing new travels in the session:
the bytes carry it.

## 2b. The completeness inventory

What a spell needs, and where it is done. "Design" is Creator Mode here,
"Data" is the SuperUI Completer.

| Need | Where | Status |
|---|---|---|
| Base spell to derive from | Design: spell search | done |
| Which models play in which phase, where they attach, how big | Design: Composition | done (§2.8) |
| Caster animation per phase | Design: Composition | done (§2.8) |
| Missile model, scale, trail | Design: Composition + the model's ribbons/emitters | done |
| Missile speed | Data: Completer form | done (§2.9) |
| Particle emitters: every inline field, ten scalar tracks, first key or all keys | Design: emitter editor | done (§2.6) |
| Add / duplicate / remove emitters, compose lines | Design | done (pre-existing) |
| Mesh layers: visibility, image, blend, flags, colour, alpha, transparency, clone | Design | done (§2.5) |
| Ribbons: every field, first keys, clone, off | Design | done (§2.5) |
| Bones: pivot, pose, translation, freeze animation; re-parent emitters/ribbons | Design: BONES + Show bones | done (§2.6) |
| Own textures (PNG/BLP) into any slot; swap any archive image; tint; hue | Design | done (§2.7 + pre-existing) |
| Per-phase sounds, incl. area | Design import → Data SoundEntries rows | done (§2.9 closes area) |
| See where things are: void stage, lattice, gizmos, ribbon markers, skeleton, pause/scrub | Design | done (§2.1–2.4, 2.6, 2.10) |
| Move, size and rotate by dragging in the world; frame the selection; undo a drag | Design: handles | done (§2.10) |
| Name, subtext, description, school, tab, icon | Data | done (pre-existing) |
| Mana, levels, cast time, range, duration, cooldown, ranks, trainers | Data | done (pre-existing) |
| What the spell DOES: effects, auras, points, ticks, misc | Data: mechanics editor | done (pre-existing) |
| Keyframe-by-keyframe editing of animated tracks | Design | not built: first key, or ALL keys (constant) |
| New bones / new geometry authored from scratch | Design | not built: the workshop composes existing M2s — SPEC in `shared_docs/SPELL_SKETCH.md` (draw/pick a shape, place, move, dress → compiled M2) |
| Reagents, power type, target flags, interrupt/threat flags | Data | not in the form: inherited from the source spell |

## 3. Where things live

| Piece | File |
|---|---|
| Spec (this) | `shared_docs/SPELL_CREATOR_IDE.md` |
| Void stage | `GameLoop/CreatorMode/GameLoop.Creator.Stage.cs`; `uStage*` in `World/TerrainRenderer.cs`, `World/Wmo/WmoRenderer.cs`, `Shaders/terrain.frag`, `Shaders/wmo.frag` |
| Effect clock + replay | `GameLoop/CreatorMode/GameLoop.Creator.Clock.cs`; `SpellClockNow`/`SpellClockDelta` consumed in `Program.cs` Render, `GameLoop.Casting.cs` UpdateSpellPresentation, `GameLoop.DevTools.SpellAnimation.cs`, `GameLoop.Creator.Spells.cs` |
| Mesh layers + ribbons | `GameLoop/CreatorMode/GameLoop.Creator.MeshRibbon.cs`; `Creator/M2MeshParser.cs`, `Creator/M2RibbonParser.cs` |
| Bones + emitter completeness | `GameLoop/CreatorMode/GameLoop.Creator.Bones.cs`; `Creator/M2BoneParser.cs`; `Creator/M2EmitterParser.cs` (inline block, `PatchTrackValueAll`) |
| Texture import | `GameLoop/CreatorMode/GameLoop.Creator.TextureImport.cs`, `GameLoop.Creator.FilePicker.cs`; `Formats/MpqMount.cs` (`SetOverride`) |
| Composition | `GameLoop/CreatorMode/GameLoop.Creator.Composition.cs`, `GameLoop.Creator.ModelPicker.cs`; `Formats/SpellVisualCatalog.cs` (`SpellVisualKitEffect.Scale`); `World/Units/SpellEffectSource.cs` (instance scale) |
| Completer | MangosSuperUI `Services/SpellServices/SpellVisualCloner.cs` (`SpellComposition`, area kit), `Services/PatchBuilderService.cs`, `Services/SpellServices/CompleterStore.cs`, `Controllers/SpellCompleterController.cs`, `Controllers/PatchController.cs`, `wwwroot/js/spellcompleter.js` |
| Gizmos + grid | `GameLoop/CreatorMode/GameLoop.Creator.Gizmos.cs`; `World/Spells/SpellGizmoRenderer.cs`, `World/Spells/SpellEmitterGizmoLaw.cs`, `Shaders/gizmo.vert`, `Shaders/gizmo.frag`; `SpellParticleSystem.EmitterFrames()` / `ResetPools()`; `SpellEffectSource.LiveInstances()` / `BoneFrames()` (frame + parent frame) |
| Drag handles, framing, undo | `GameLoop/CreatorMode/GameLoop.Creator.Handles.cs`; `World/Spells/SpellGizmoHandleLaw.cs` (pure); `Creator/M2BoneParser.cs` (`BonePatch.Rotation`); the Escape rung in `GameLoop/Panels/GameLoop.Settings.cs` |
| Panel | Spell Workshop sections `ws-stage` and `ws-clock` (grid/gizmo switches live in `ws-stage`); the IDE layout in `GameLoop/CreatorMode/GameLoop.Creator.SpellIde.cs` |
| Settings | `Settings.Creator.{StageRadius, SpellGizmos, SpellGrid, GizmoThroughWalls, GridMinorSixthYard}` |

## 4. Slice order and what is still open

Built 2026-09-10 (live-unverified at time of writing; the owner launches):
void stage; effect clock with pause/speed/step/scrub and deterministic replay;
grid; read-only emitter gizmos with panel-linked highlight.

Built later the same day (§2.5–2.9): mesh layers and ribbons, bone posing and
re-parenting with the skeleton drawn, the full emitter inline block, all-keys
writes, texture import, phase composition on both sides, area-kit cloning,
missile speed.

Built later still (§2.10): drag handles (translate on emitters, ribbons and
bones; speed and area marks on emitters; rotate rings on bones), ribbon markers,
click/double-click labels, Frame, Escape/Ctrl+Z.

Still open: a keyframe timeline over the scrubber (tracks are edited at the
first key or flattened to a constant, never key by key); per-layer hiding when
two layers share one submesh; a scale handle (effects scale through the kit's
`SpellVisualEffectName.Scale` in Composition, not per bone); bone scale and
"turn every key" offset modes in the bone patcher; the emitter's geometry-model
path. New geometry (the owner's "draw a crescent, copy it, fly it 3 yards with
a trail") is its own spec: `shared_docs/SPELL_SKETCH.md`.

## 5. Verification

- Build both trays. `interface-wire-check --shared-docs-only` and
  `--imgui-policy-only` stay green (creator files are dev-excluded from the
  ImGui policy).
- The byte patchers' standing proof is the emitter lab:
  `dotnet run --project tools/spell-emitter-lab --no-restore -- MSUIClient/client-config.json`
  cross-checks `M2MeshParser` / `M2RibbonParser` against `M2Reader` on every
  spell M2 in the mounted data and round-trips hide / private material /
  lookup / colour / alpha / transparency and ribbon field / first-key / off
  patches on a real fixture, then re-parses the result with the runtime reader.
  2026-09-10 baseline: 36,133 checks, 1,135 mesh layers (824 with a colour
  record), 312 ribbons, 0 mismatches; the ribbon and layer clones, the bone
  pivot/rotation patch and the emitter inline block (colours, cells, head/tail,
  midpoint, tail time, flattened track) round-trip on a real fixture too. Its first run caught two reader bugs
  (the version gate must accept every vanilla revision 256..263; a zero-vertex
  model has no mesh layers because the runtime never reads its view) - extend
  it whenever a new patcher lands. The handle law (§2.10) is checked there too:
  its swaps agree with M2Reader on every bone of the fixture (pivot,
  translation key, quaternion), the quaternion pose write round-trips through
  both readers, `RotateBoneWorld` turns a bone under a rotated, scaled parent by
  exactly the requested world angle, and the axis / plane / ring / world-delta
  arithmetic reads back known points.
- Live: boot Creator Mode (scratch settings via `MSUI_SETTINGS_PATH` with
  `"LaunchMode": "Creator"`), pick Cone of Cold, Loop the cast, open Spell
  Workshop → Stage: Stage on, Grid on, Gizmos on. Expected: black world, ground
  disc, three grid planes at the feet, one gizmo per emitter at the hand with
  reach rays pointing where the cone actually goes. Clock: Pause → the cloud
  holds; drag Speed on an emitter → the held cloud re-lays with the new reach;
  Step ±; Scrub. Resume restores everything; Stage off restores the world.
