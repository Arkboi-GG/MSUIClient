# Plan 21 — HUD layout editor ("Edit Mode"), Command View first

Status: **phase 1 BUILT 2026-09-03** - laws, registry, overlay, movers, grid, snap, nudge, undo/redo, selection card, Default + Custom layout, v11->v12 migration, guard `interface-wire-check --hud-layout-only`. Written 2026-09-02 from a survey of the live tree; the file:line facts in section 5 describe the tree BEFORE the build.
Everything under §5 is a file:line fact from that survey; everything under §4 and
§6 is a decision to argue with before building.

## 1. Problem

At the Command View vantage (Ctrl+F, `_freeView` true) the HUD is nine or ten
pieces of furniture, each nailed to a screen corner by an inline constant:

| Piece | Where its rect is computed | Anchored how |
|---|---|---|
| Command shelf (squads + info card + command card) | `GameLoop/Hud/GameLoop.CommandShelf.cs:384-387` | bottom-centre, pivot (0.5,1), 640×116 logical |
| Control-group rail | `GameLoop/Hud/GameLoop.RtsControlGroups.cs:480-481` | top-centre, y=78 |
| Command palette + hidden-state button | `GameLoop.RtsControlGroups.cs:541-543`, `:565-566` | top-right, x=display−338 / −175 |
| Angle knob (lock / primary AI / pitch) | `GameLoop/Scene/GameLoop.Control.cs:2279-2281` | bottom-right, pivot (1,1) |
| RTS control guide | `GameLoop.Control.cs:2996-3010` | stacked above the knob |
| Minimap (square) | `GameLoop/Hud/GameLoop.Minimap.cs:63-65` | `_freeView ? (8, Y−200) : (X−192, 0)` |
| Chat frame | `GameLoop/Panels/GameLoop.Chat.cs:523-528` | `ChatFrameLaw.FrameOrigin` − 124 in free view + saved offset |
| Territory strip | `GameLoop/Hud/GameLoop.RtsTerritory.cs:55-58` | top-centre, y=72 |
| Companions panel | `GameLoop/Panels/GameLoop.Companions.cs:46-51` | ImGui `FirstUseEver`, session-only |
| Party frames / unit frames | `GameLoop.PartyFrames.cs:714-716`, `GameLoop.UnitFrames.cs:16-24` | `*Law` constants × scale |

The user can move exactly one of them (chat), through a per-frame special case:
`Settings.HudLayout.{ChatUnlocked, ChatOffsetX, ChatOffsetY}` plus a drag handle
that only that file knows about. Every other piece is where the author left it.
There is no grid, no snapping, no reset-all, no named layouts, and no way to say
"minimap on the right" without editing C#.

## 2. Class

**Addition.** Vanilla 1.12 could not move frames without addons, so there is no
real-client reference. The yardstick is intent (§3), informed by what the games
that solved this well converged on (§4). The *look* of the editor chrome stays
emulation-core: vanilla chrome, `GameText`, `VanillaButton`, no ImGui widgets
(`GameplayImguiPolicyLaw`).

## 3. Target (intent)

1. One **Edit Mode** toggle. While it is on, every registered HUD frame shows a
   named mover box, gameplay clicks are swallowed, and the world keeps rendering.
2. Drag to move, arrow keys to nudge, a grid you can see and snap to, and magnet
   snapping to screen edges/centres and to other frames' edges.
3. A frame's position is an **anchor + offset in logical pixels**, so a layout
   survives UI-scale and resolution changes and content-sized frames keep their
   alignment.
4. **Command View and body play are separate layouts** of the same frames. The
   minimap already lives in different corners per mode; the data model should
   say so instead of an `if (_freeView)` at the draw site.
5. Named layouts with an immutable Default, reset per frame and reset all, undo,
   and an explicit Save / Revert on exit.
6. Every frame that opts in costs its draw site **one line**: replace the inline
   position with a `HudFrame(...)` call.

## 4. What the games do, and what to borrow

| Game / addon | Mechanism | Borrow |
|---|---|---|
| **WoW Edit Mode** (Dragonflight) | In-game overlay, world runs. Every frame is a blue mover with a name; click selects, a side card shows frame settings (scale, orientation, rows, visibility). Grid toggle with two spacings; snap-to-grid and snap-to-frame with guide lines. Layouts: locked presets + user layouts, account or per-character; Save / Revert; import/export string. | The whole shape: overlay, mover + name, selection card, grid + snap, layouts with an immutable default, explicit Save/Revert. |
| **ElvUI** `/moveui` | Coloured movers; right-click a mover for a nudge window (±1/±5, numeric X/Y); grid-size slider; positions stored as `point, relativePoint, x, y` with the point auto-picked from the nearest screen corner so layouts survive resolution changes; per-mover and global reset; profiles. | Nearest-anchor re-pick on drop; numeric nudge; arrow-key nudging; per-frame reset button appears only once a frame has moved (chat already does this). |
| **FFXIV HUD Layout** | Dedicated layout screen, four HUD slots, per-element scale 60–200 %, per-element hide, grid snap, "copy layout to slot". Elements listed by name so hidden ones can be found. | Frame list in the toolbar (find/select a frame that is small, hidden or off-screen); per-frame scale and hide as a later phase; copy layout. |
| **RTS genre** (SC2, AoE4, CoH) | Fixed layouts. UI scale slider plus a few discrete choices: minimap side, HUD scale. | Discrete corner choices must be one click: the 9-dot anchor picker on the selection card *is* the "minimap left/right" toggle. Don't force RTS users through free drag for the common case. |
| **Divinity: OS2** | Free drag of every panel, persisted per profile, no grid. | Warning case: free drag without snap or reset produces sloppy layouts people can't get back from. Grid + reset are not optional. |
| **MoveAnything** (old WoW) | A list of every frame with unlock/hide/scale/alpha. Powerful, ugly, hard to discover. | Nothing beyond "hide" and "scale" as per-frame fields. Editing through a list is the fallback, not the front door. |

Two things every good implementation shares: **the editor is a mode, not a
screen** (you see the live HUD you are editing), and **positions are relative
to an anchor, never absolute**.

## 5. Resources (what already exists)

- **Scale**: one factor, `GameplayUiScale()` → `InterfaceScaleLaw.ResolveForFramebuffer`
  against a 1600×900 reference (`GameLoop/Hud/GameLoop.GameplayLayout.cs:21-31`,
  `Engine/UI/InterfaceScaleLaw.cs:76-88`). Every frame authors in logical px and
  multiplies once. No DPI awareness; framebuffer pixels only.
- **Chat mover** (`GameLoop.Chat.cs:572-603`): offset-from-authored, `MouseDelta / scale`,
  clamped by `ChatFrameLaw.ClampFrameOrigin`, saved on release via `_chatDragDirty`
  + `IsItemDeactivated`, lock toggle and reset in Options (`GameLoop.Settings.cs:1374-1394`).
  This is the per-frame prototype the editor generalises and then deletes.
- **Creator Mode edit layout** (`GameLoop/CreatorMode/GameLoop.Creator.Ui.cs:1084-1137`):
  green outlines, `clicked = false` in edit mode, offsets dictionary, reset. The
  swallow-clicks precedent.
- **`BeginVanillaWindow(movable: true)`** (`GameLoop/Hud/GameLoop.VanillaUi.cs:64-88`):
  session-only drag; one caller. Will be superseded.
- **Dev layout capture**: `CollectGameplayLayout(id, x, y, w, h, screenMin, screenSize)`
  (`GameLoop/Dev/GameLoop.DevTools.GameplayDump.cs:37-43`) already reports rects
  from 8 body-play sites. The frame registry is this, made live.
- **Settings**: `HudLayoutSettings` (`Engine/GameSettings.cs:282-289`), `Version = 11`,
  `Migrate` at `:1462`. `SettingsStore.Save()` writes the whole file.
  Per-character precedent exists only for keybindings
  (`keybindings.character-{guid:X16}.json`, `GameLoop/Panels/GameLoop.Bindings.cs:328-334`).
- **Input**: `ImGui.GetIO().WantCaptureMouse` is the pointer arbiter. Edge pan
  (`GameLoop.Control.cs:1946`) and marquee (`:1669`) gate on it. Z-order is ImGui's
  stack steered by `NoBringToFrontOnFocus` on furniture; the bug at
  `GameLoop.CommandShelf.cs:389-397` is what happens when an overlay gets that
  flag wrong.
- **Bindings**: `GameBinding` enum + registry rows (`GameLoop.Bindings.cs:12-71`, `:211-250`).
- **Guard tests**: `tools/interface-wire-check/*ClinicalChecks.cs`; law-call
  asserts plus source-grep ratchets (`GameplayImguiPolicyClinicalChecks.cs`).
- **Law**: `CODE_STRUCTURE_LAW.md` §4.3 — pure logic in `Engine/UI/<Name>Law.cs`,
  never referencing `GameLoop`.

## 6. Key design decisions

### D1. Position model: anchor + pivot + logical offset

```
origin = display * AnchorFraction(anchor) - size * AnchorFraction(pivot) + offset
```

`anchor` and `pivot` are the same 9-point enum (TopLeft … BottomRight). A
content-sized frame (the rail grows with control-group count, the knob with row
count) keeps its alignment because the pivot is fractional. On drop, the law
re-picks `anchor` from the screen third the frame's centre landed in and
recomputes `offset` so the rect does not move; that is what makes a layout
survive a resolution change. Then clamp on-screen, guarding `min > max` on tiny
framebuffers (`Math.Clamp` throws; see `SpellFocusLayoutLaw.cs:47-49`).

### D2. Two layout contexts, one frame set

`Body` and `Command`. Each registered frame supplies an authored placement per
context (or the same one for both). User overrides are stored per context. Edit
Mode edits whichever context is live when you open it. This removes the
`if (_freeView)` shifts in chat and minimap; they become two authored defaults.

### D3. Registry is populated at draw time

Frames that are not drawn this frame do not exist for the editor (action bars
in Command View, everything under the commander map). The draw-site contract:

```csharp
// before (GameLoop.RtsControlGroups.cs:480)
ImGui.SetNextWindowPos(new Vector2((display.X - railWidth) * .5f, 78f * scale), ImGuiCond.Always);

// after
var rail = HudFrame("control-group-rail", "Control groups",
    authored: HudPlacement.At(HudAnchor.Top, x: 0, y: 78),
    logicalSize: new Vector2(railWidth, railHeight));
ImGui.SetNextWindowPos(rail.ScreenMin, ImGuiCond.Always);
```

`HudFrame` resolves authored → user override → clamp, records the rect in a
per-frame list for the overlay, and returns screen and logical rects. A frame
may name a `parent` (control guide → angle knob) and then its offset is from the
parent's resolved rect; children move with the parent and are not separately
draggable in phase 1.

### D4. Edit Mode is an overlay window drawn last

Full-screen transparent ImGui window, `NoDecoration | NoBackground |
NoSavedSettings | NoNav`, and **not** `NoBringToFrontOnFocus`, begun after every
frame it edits and before popups. One `InvisibleButton` per registered frame,
issued smallest-area first so a frame nested inside a larger one stays
grabbable. Because the overlay is topmost, the furniture underneath never sees
hover; that is the click swallow, with no per-site `if (_hudEditMode)` needed.

Explicit gates still required: `UpdateFreeCamEdgePan` (drag toward an edge would
pan the camera), marquee/order dispatch, and gameplay bindings other than camera
movement and the Edit Mode toggle itself.

### D5. Snapping

Candidates, in priority order when the pointer is within 6 logical px:
frame-to-frame edges and centres (if *Snap to frames*), screen edges and
centres, grid lines (if *Snap to grid*). A snap returns the corrected delta and
the guide lines to draw (foreground draw list, magenta, the convention every
layout tool uses). Grid spacing cycles 8 / 16 / 32 logical px; grid visibility is
independent of grid snapping. Holding Alt while dragging disables all snapping.

### D6. Live apply, explicit commit

Drags update the in-memory override immediately, so the real frame follows.
Entering Edit Mode snapshots the active layout; *Save & Exit* writes settings
once; *Revert & Exit* restores the snapshot. Escape = Save & Exit (matches the
chat mover's "saves on release" expectation; undo covers mistakes). No
per-frame `SettingsStore.Save()` during a drag.

### D7. Layouts

`Default` is implicit and immutable (empty override set). User layouts are
named; editing `Default` silently forks it to `Custom`. Layouts are global by
default; a `CharacterLayouts` map (guid → layout name) gives per-character
selection without a new file family. Copy / rename / delete from the toolbar.
Import/export as a string is phase 3.

### D8. Frame settings card

Selecting a mover opens a vanilla-chrome card at the screen edge farthest from
the selection: frame name, X / Y (editable), 9-dot anchor picker (this is also
"put the minimap in that corner"), *Reset frame*. Phase 2 adds *Hide* and
*Scale*; phase 3 adds a per-frame options hook (minimap square/round, shelf
compact) fed by the registering site.

## 7. Data model

```csharp
public enum HudAnchor { TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight }

public sealed class HudPlacement            // one frame, one context
{
    public HudAnchor Anchor { get; set; }
    public HudAnchor Pivot { get; set; }
    public float X { get; set; }            // logical px from anchor
    public float Y { get; set; }
    public float Scale { get; set; } = 1f;  // phase 2
    public bool Hidden { get; set; }        // phase 2
}

public sealed class HudLayout
{
    public string Name { get; set; } = "";
    public Dictionary<string, HudPlacement> Body { get; set; } = new();     // frame id -> override
    public Dictionary<string, HudPlacement> Command { get; set; } = new();
}

public sealed class HudLayoutSettings
{
    public string ActiveLayout { get; set; } = "Default";
    public List<HudLayout> Layouts { get; set; } = new();
    public Dictionary<string, string> CharacterLayouts { get; set; } = new(); // "{guid:X16}" -> name
    public int GridSize { get; set; } = 16;
    public bool GridVisible { get; set; }
    public bool SnapToGrid { get; set; } = true;
    public bool SnapToFrames { get; set; } = true;
}
```

Migration (`Version 11 → 12`): if `ChatOffsetX/Y` are non-zero, create layout
`Custom` with a `chat` placement in both contexts (anchor re-picked from the
resulting rect) and set it active; drop `ChatUnlocked`.

Authored defaults are **not** in settings; they live at the draw sites (they
know their sizes). The law only ever sees `(authored, override?)`.

## 8. Edit Mode UX

**Entry**: Options → Interface → *Edit HUD layout* (replaces the "Chat frame"
box); `/editui` in chat; a `GameBinding.ToggleHudEditMode` row, unbound by
default; chat right-click → *Move chat* opens Edit Mode with chat selected.

**Toolbar**: a slim strip along the top edge (y 0–60 is free in Command View;
the rail starts at 78): layout picker · Grid on/off · grid size · Snap on/off ·
frame list (select by name) · Undo · Redo · Reset all · Save & Exit · Revert &
Exit. The toolbar is not itself a movable frame.

**Movers**: tinted fill + 1 px border + name label (`GameText`) at the top-left;
selected mover gets the accent border and shows its logical X / Y while
dragging. Hidden frames (phase 2) render as an outline only.

**Keyboard**: arrows nudge 1 logical px, Shift+arrows 10; Ctrl+Z / Ctrl+Y;
Delete = reset selected frame; Escape = Save & Exit; Alt while dragging =
no snap.

**Grid**: background draw list, `GridSize` logical px, brighter lines at the
screen centre lines. Drawn only in Edit Mode with *Grid* on.

## 9. Architecture

| File | Role |
|---|---|
| `Engine/UI/HudLayoutLaw.cs` (new, pure) | `Resolve`, `Clamp`, `NearestAnchor`, `Snap` (returns delta + guides), `Nudge`, `Migrate11To12`. No `GameLoop` reference. |
| `Engine/UI/HudLayoutEditLaw.cs` (new, pure) | Edit session state: snapshot, selection, drag, undo/redo stack, commit/revert. |
| `GameLoop/Hud/GameLoop.HudFrames.cs` (new) | `HudFrame(...)` registration + per-frame list; `_hudEditMode`; context = `_freeView ? Command : Body`. |
| `GameLoop/Hud/GameLoop.HudLayoutEditor.cs` (new) | Overlay window, movers, toolbar, settings card, grid. Vanilla chrome only; enrolled in `GameplayImguiPolicyClinicalChecks.EnrolledCleanFiles`. |
| `Engine/GameSettings.cs` | New `HudLayoutSettings` shape, `Version = 12`, migration step. |
| Phase-1 draw sites (10 files) | One-line position swap each; delete the chat mover and the `_freeView` shifts. |
| `GameLoop/Panels/GameLoop.Settings.cs` | Replace the chat-frame box with *Edit HUD layout*; defaults action resets `HudLayout`. |
| `GameLoop/Scene/GameLoop.Control.cs` | Edit-mode gates on edge pan, marquee, order dispatch. |
| `GameLoop/Panels/GameLoop.Bindings.cs` | `ToggleHudEditMode` row; binding suppression while editing. |
| `tools/interface-wire-check/HudLayoutClinicalChecks.cs` (new) | §10. |

Draw order in `DrawCombatHud` (`GameLoop.CombatFeedback.cs:156-290`): overlay
goes after the HIGH stratum (`DrawMultiActionBars`) and before tooltips/popups.

## 10. Test protocol

Guard test (`interface-wire-check --hud-layout-only`):

- `Resolve` round-trips all 9×9 anchor/pivot pairs at 1600×900 and 3840×2160.
- `NearestAnchor` returns each corner for a frame parked in that corner, `Center`
  for one at the middle, and re-anchoring never moves the rect by more than
  1e-3 logical px.
- `Clamp` on a 1×1 display does not throw and returns a finite origin.
- `Snap` within 6 px picks frame edges over grid lines; beyond 6 px returns the
  raw delta and no guides.
- Undo/redo round-trips a three-step drag sequence.
- `Migrate11To12` on a settings blob with chat offset (40, −30) yields layout
  `Custom` active with `chat` in both contexts and no legacy keys.
- Source ratchet: each Phase-1 file contains `HudFrame("<id>"` and no longer
  contains its old `SetNextWindowPos(` constant; `GameLoop.HudLayoutEditor.cs` is
  in the ImGui-policy clean list; `GameLoop.Control.cs` gates
  `UpdateFreeCamEdgePan` on `_hudEditMode`.

Live run (live-run harness, Command View vantage):

1. Toggle Edit Mode, screenshot: all Phase-1 movers visible with names, grid on.
2. Scripted drag of the minimap to the bottom-right; guide lines appear at the
   screen edge; drop; card shows `Anchor: BottomRight`.
3. Save & Exit, restart the client, re-enter Command View: minimap is bottom-right
   and body-play minimap is unchanged (top-right).
4. Change `Display.UiScale` from 1.0 to 1.3: minimap stays flush bottom-right.
5. Ctrl+F out of Command View with Edit Mode on: the overlay switches to the Body
   context and the Command-View frames disappear from the mover list.

## 11. Phases

| Phase | Scope | Frames |
|---|---|---|
| **1 — Command View** | Laws, registry, overlay, movers, grid, snap, nudge, undo, settings card (name / X / Y / anchor / reset), Default + one custom layout, migration, guard test. | command-shelf, control-group-rail, command-palette, angle-knob (+ control-guide child), minimap, chat, territory-strip, companions, party-frames, player-frame, target-frame |
| **2 — Body play** | Register the 8 `CollectGameplayLayout` sites; per-frame Hide and Scale; layout copy / rename / delete; per-character layout map. | action-bar, micro-cluster, bag-cluster, cast-bar, pet-frame, pet-bar, stance-bar, buffs |
| **3 — Polish** | Import/export string; per-frame options hook; frame-to-frame anchoring in the card (WoW "anchor to"); ImGui `movable:` path removed. | — |

## 12. Fallback

If the overlay's hover swallow proves unreliable against a specific frame, the
Creator-Mode precedent (`clicked = false` when editing) is a per-site fix. If
snapping lands late, plain drag + clamp + reset is still a complete phase 1.

## 13. Open questions for Nico

1. Should body-play frames (action bars, unit frames) ever be movable, or is
   phase 2 only Hide/Scale for them? Vanilla did not allow it; this is an
   intent call, not a parity call.
2. Per-character layouts in phase 1 or 2? The map is cheap; the UI for it is not.
3. Per-frame scale: worth exposing at all, given the single `UiScale` and the
   1216-px bar-span clamp in `InterfaceScaleLaw`?
4. Should Escape save or prompt? WoW prompts; this plan says save, because undo
   and Revert both exist.
