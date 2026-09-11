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
is drawn bold; the others dim. That is the read-only half of the spatial
editor. NOT built here, by design: drag handles (the position patcher is ready
for them), bone re-parenting, bone rotation authoring, the keyframe timeline —
see §4.

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

## 3. Where things live

| Piece | File |
|---|---|
| Spec (this) | `shared_docs/SPELL_CREATOR_IDE.md` |
| Void stage | `GameLoop/CreatorMode/GameLoop.Creator.Stage.cs`; `uStage*` in `World/TerrainRenderer.cs`, `World/Wmo/WmoRenderer.cs`, `Shaders/terrain.frag`, `Shaders/wmo.frag` |
| Effect clock + replay | `GameLoop/CreatorMode/GameLoop.Creator.Clock.cs`; `SpellClockNow`/`SpellClockDelta` consumed in `Program.cs` Render, `GameLoop.Casting.cs` UpdateSpellPresentation, `GameLoop.DevTools.SpellAnimation.cs`, `GameLoop.Creator.Spells.cs` |
| Gizmos + grid | `GameLoop/CreatorMode/GameLoop.Creator.Gizmos.cs`; `World/Spells/SpellGizmoRenderer.cs`, `World/Spells/SpellEmitterGizmoLaw.cs`, `Shaders/gizmo.vert`, `Shaders/gizmo.frag`; `SpellParticleSystem.EmitterFrames()` / `ResetPools()`; `SpellEffectSource.LiveInstances()` |
| Panel | Spell Workshop sections `ws-stage` and `ws-clock` (grid/gizmo switches live in `ws-stage`); the IDE layout in `GameLoop/CreatorMode/GameLoop.Creator.SpellIde.cs` |
| Settings | `Settings.Creator.{StageRadius, SpellGizmos, SpellGrid, GizmoThroughWalls, GridMinorSixthYard}` |

## 4. Slice order and what is still open

Built 2026-09-10 (live-unverified at time of writing; the owner launches):
void stage; effect clock with pause/speed/step/scrub and deterministic replay;
grid; read-only emitter gizmos with panel-linked highlight.

Open, in order: drag handles for the origin (write through
`M2EmitterParser.PatchPosition`); bone re-parent picker with candidate bone
frames drawn as gizmos (the honest "rotation" story — an emitter has no rotation
field of its own); static bone rotation authoring; a keyframe timeline over the
scrubber (the panel edits only the FIRST key of each scalar track — Cleave's
animated rate is why it reads 0).

## 5. Verification

- Build both trays. `interface-wire-check --shared-docs-only` and
  `--imgui-policy-only` stay green (creator files are dev-excluded from the
  ImGui policy).
- Live: boot Creator Mode (scratch settings via `MSUI_SETTINGS_PATH` with
  `"LaunchMode": "Creator"`), pick Cone of Cold, Loop the cast, open Spell
  Workshop → Stage: Stage on, Grid on, Gizmos on. Expected: black world, ground
  disc, three grid planes at the feet, one gizmo per emitter at the hand with
  reach rays pointing where the cone actually goes. Clock: Pause → the cloud
  holds; drag Speed on an emitter → the held cloud re-lays with the new reach;
  Step ±; Scrub. Resume restores everything; Stage off restores the world.
