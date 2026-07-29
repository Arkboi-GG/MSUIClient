# Porting benilla's gameplay + UI to MSUIClient — a cold-start reference

**Systems covered:** target selection & highlighting · combat · spells / spellbook / casting ·
action bars · inventory (bags/bank) · character page · skills · talents · unit portraits · the
shared UI foundations they all sit on.

**Source of truth:** `benilla` — a from-scratch, from-scratch-faithful WoW **1.12.1 (build 5875)**
client in Rust/Bevy that ships and drives the real Blizzard FrameXML. It is READ-ONLY reference,
at `C:\Users\nico\Desktop\benilla-main`.
**Port target:** `MSUIClient` — the native C# client (Silk.NET / OpenGL, **ImGui** HUD, no
FrameXML), at `C:\Users\nico\source\repos\MSUIClient`.

This document is meant to be *most of what a new Claude (or a new contributor) needs* to build
these systems in MSUI without re-reading benilla from scratch. Each part traces how benilla does
the system in code, states exactly what MSUI has today, and gives an ImGui-native port plan with
the authentic 1.12 numbers.

---

## How this document was built, and how to trust it

Seven research passes read the staged benilla source (166 files: the native `benilla` game
modules, the `benilla-ui` FrameXML engine, the shipped `assets/ui/*.xml`, the `benilla-protocol`
wire code, the `benilla-formats` DBC readers, and their tests) alongside the relevant MSUI C#
files. Every non-obvious claim carries a `file:line` citation to the code as it stands today, in
the same style as the repo's existing `BENILLA_VS_MSUI_PORTAL.md`.

Citation convention: benilla paths are **repo-relative** (`crates/benilla/src/target/click.rs:123`);
MSUI paths are `MSUIClient/MSUIClient/Net/WorldSession.cs:45`. Line numbers are real (read from
source), not guessed.

**What is byte-pinned vs. what to double-check.** The wire layouts, the FrameXML dimensions, the
colour tables, and the field/opcode numbers in the appendices were read from benilla's own source
and its byte-golden protocol tests. A handful of constants live in benilla files that were not in
the staged subset at research time; those are flagged inline and consolidated in
**Appendix E (Open items to verify against source)**. The opcode numbers (**Appendix A**) and the
UpdateFields indices (**Appendix B**) were subsequently read from benilla's authoritative
`messages/opcode.rs` and `fields/mod.rs` and supersede any inline "verify against
UpdateFields_1_12_1.h" caveat a part still mentions — one such caveat was already an outright
error (`UNIT_POWER1..5` is **23-27**, not the 47-51 a first pass guessed; see Appendix B).

---

## The one decision that shapes everything: FrameXML → ImGui

benilla runs the **real Blizzard FrameXML** — it ships `ActionBar.xml`, `CharacterFrame.xml`,
`UnitFrames.xml`, `SpellBookFrame.xml`, … and a whole `benilla-ui` crate that is a FrameXML +
anchor-layout + Lua-script engine (`crates/benilla-ui/src/lib.rs:18-24`). MSUI does not have that
and **should not build it.**

> **The strategy, stated once for the whole document:** treat benilla's shipped `.xml` as the
> authoritative *spec* — exact dimensions, textures, anchors, colours, behaviour — and its native
> Rust modules (`ui_*`, `net/apply/*`, `benilla-ui` bindings) as the *game-state logic*. Rebuild
> each panel as a **native ImGui window** (or a small custom-GL element where ImGui cannot express
> it), reading MSUI's own C# game state each frame. Do **not** port an anchor resolver, a strata
> engine, a template expander, or a Lua host.

Custom GL (not ImGui) is warranted in exactly three places, all detailed in **Part 1/2
(Foundations & Portraits):**

1. **World-anchored elements** — nameplates, the ground selection ring, over-unit health bars,
   floating combat text: these project a world position through the live camera and depth/scale
   with distance.
2. **The 3D portrait / paperdoll / dress-up** — a unit M2 rendered to an offscreen FBO texture,
   then shown with `ImGui.Image`.
3. Optionally, a bespoke cooldown-swipe if the ImGui triangle-fan approach proves insufficient.

Everything else — unit frames, status bars, buff grids, tooltips, bags, action bars, the character
sheet — is plain ImGui reading the spec dimensions in the parts below.

---

## MSUI today: what exists, what is greenfield

The transport and the world are built; the gameplay UI layer is almost entirely new. Verified
against the staged files:

| Layer | State in MSUI | Where |
|---|---|---|
| 1.12 protocol (auth, SRP, header crypto, world session, char enum) | **Built** | `Net/{NetworkClient,WorldSession,RealmClient,Srp6Client,WorldHeaderCrypto}.cs` |
| UPDATE_OBJECT codec + entity store | **Built** (units/GOs; **no item objects**) | `Net/{ObjectFields,UpdateObject,Entities}.cs` |
| Descriptor field accessors | **Partial** — guid/type/entry/scale, health/maxhealth, level, faction, unit/npc flags, displayId, target, bytes0/2 only; **no** power, stat, skill, AP, resistance, item, inventory fields | `Net/ObjectFields.cs` |
| Selection / attack opcodes | **Wired but unused** — `SetSelection`, `AttackSwing`, `AttackStop`, `CreatureQuery` exist; nothing calls them | `Net/WorldSession.cs` |
| Movement + character controller | **Built** | `Player/CharacterController.cs`, `Net/MonsterMove.cs` |
| Unit rendering (skinned player + creature M2, animation, equipment) | **Built & mature** | `World/Units/{CharacterRenderer,CreatureRenderer,M2Animator,CharacterEquipment,AttachedItemRenderer}.cs` |
| M2 / BLP / WMO / DBC readers | **Built** (generic `DbcFile`; ItemDisplayInfo, CharSections, AreaTable, Light…) | `Formats/{M2Reader,BlpDecoder,DbcReader,CreatureDbc}.cs` |
| ImGui HUD scaffold + glue screens + Blizzard skin helper + FRIZQT font | **Built** | `Program.Net.cs`, `Engine/{ClientWindow,GlueBooth,GlueScene}.cs`, `Engine/UI/WowSkin.cs` |
| Render-to-texture (FBO) | **Missing** — `Texture.cs` uploads CPU bytes only; needed for portraits/paperdoll | — |
| **The 9 gameplay systems in this doc** (target UI, combat feedback, spellbook/cast/casting-bar, action bars, bags/bank, character sheet, skills, talents, portraits, unit frames, tooltips, auras) | **Greenfield** | — |
| Spell/Talent/SkillLine DBC readers | **Greenfield** (1.12 items are server-queried, NOT a client `Item.dbc`) | — |

The upshot: MSUI already has the hard parts a UI needs to *read from* (a live entity store, a
skinned unit renderer, DBC/BLP access, an ImGui context, a wire session). What is missing is the
gameplay state models, a handful of DBC readers, the item-query cache, an FBO, and the ImGui
panels themselves.

---

## Build the shared toolkit first

Almost every part reuses the same primitives. Building these once, up front, is the single biggest
lever — it turns each subsequent panel into "read state → call helpers." The full specs live in
**Part 1 (Foundations)** and **Part 2 (Portraits)**; the checklist:

- **`StatusBar(frac, w, h, color, tex, spark?, text?)`** — the one bar behind health / mana / cast
  / xp / reputation. Fill is a **left-anchored crop** of the art (`right = left + frac·width`), not
  a squeeze. (Part 1 §A3.)
- **`IconButton(glTex, size, cooldownFrac?, count?, borderColor?)`** — the shared cell for action
  slots, buff/debuff grids, bag slots, spellbook, talents. Icon via `ImGui.Image((IntPtr)tex.Handle,…)`.
- **`CooldownSwipe(frac)`** — a dark radial wedge (triangle fan on the ImGui draw list,
  `angle = frac·2π`) or a top-down alpha wipe. `frac = (now − start)/duration`, **start absolute on
  the clock**; goes cold at `start+duration`. (Part 6 §5.)
- **`Tooltip(lines)`** — the shared hover renderer: navy/white/gold plate, left/right line pairs.
  The spell, item, unit, talent tooltips all fill this. (Part 1 §A5.)
- **`WorldToScreen(worldPos)`** — project through `Camera.ViewProjection` (or the camera-relative
  matrix the renderers already use), cull `w<=0`, return screen pixels. Backbone of nameplates,
  over-unit bars, floating combat text. (Parts 3–4.)
- **`Camera.ScreenPointToRay(px, size)`** — unproject a click to a world ray for unit picking.
  (Part 3 §7a.)
- **FBO / `RenderTarget` + `PortraitBooth`** — render a unit M2 head-shot (256²) / full body (512²)
  to an offscreen texture, `ImGui.Image` it. The one piece of custom GL the UI needs. (Part 2 §B10.)
- **`PackedGuid` read/write** — already present (`Net/ByteBuffer.cs`), but note the wire's guid
  encoding is *not uniform*: some combat-log packets use raw u64, others packed. (Parts 4–5.)
- **The item-template cache** (`entry → ItemInfo`, ask-once, cache negatives) — shared by bags,
  the character sheet, action-bar item slots, tooltips. (Part 7 §1c, §9a.)
- **Colour constants** ported verbatim: PowerBarColor, ReactionColor, DebuffTypeColor, item quality
  colours, combat-text colours, tooltip navy/white/gold. One C# static class. (Parts 1, 3, 4, 7.)
- **DBC readers to add** (all follow the existing `DbcFile.Parse` pattern): Spell, SpellIcon,
  SpellCastTimes, SpellDuration, SpellRange, SpellRadius, SpellShapeshiftForm, Talent, TalentTab,
  SkillLine, SkillLineCategory, SkillRaceClassInfo, FactionTemplate, plus extending `ItemDisplayRow`
  to capture the icon field. Consolidated in **Appendix C**.

---

## Recommended port order (dependency-aware)

The systems form a dependency graph, not a flat list. A sensible sequence that keeps each step
runnable and testable:

1. **Foundations (Part 1) + the shared toolkit above.** StatusBar, IconButton, CooldownSwipe,
   Tooltip, WorldToScreen, colour constants, the extra `ObjectFields` accessors. Nothing renders
   yet, but everything after is cheap.
2. **Target selection (Part 3).** Screen-ray pick → local `SelectionGuid` → `CMSG_SET_SELECTION`
   (already stubbed in `WorldSession`), ground ring, nameplates, a minimal target unit frame. This
   gives you a "current target" — a hard dependency for combat and casting. Needs `FactionTemplate.dbc`
   for reaction colour.
3. **Combat feedback (Part 4).** Handle `SMSG_ATTACKERSTATEUPDATE` + the combat-log opcodes, drive
   attack/death M2 clips, floating combat text, over-unit + player resource bars. Wires the unused
   `AttackSwing`/`AttackStop` to the target from step 2.
4. **Spells + casting (Part 5).** The Spell DBC readers + catalog, `SMSG_INITIAL_SPELLS` → known
   spells, the `TryCast(spellId, targetGuid)` pipeline, the casting bar, the spellbook window.
   `TryCast` is the entry point the action bar depends on.
5. **Action bars (Part 6).** The 120-slot model, `SMSG_ACTION_BUTTONS`/`CMSG_SET_ACTION_BUTTON`,
   the ImGui hotbar, usability tinting, cooldown swipe, keybinds → `TryCast`/item-use, stance bar.
6. **Inventory (Part 7).** The item-query cache, inventory descriptor fields → item model, bag/bank
   windows, the cursor/drag model, item tooltips. Unlocks item action-bar slots and equipped-item
   tooltips.
7. **Character page + skills + talents (Part 8).** The stat table (reuses the new `ObjectFields`
   accessors), the paperdoll (reuses the Part 2 FBO booth + the Part 7 item model), the skills
   window (SkillLine DBCs), the talent tree (Talent DBCs + the Part 5 known-spell set for ranks).
8. **Portraits (Part 2)** can slot in wherever the unit frames need them — the FBO booth is
   independent and only *needs* the unit renderers MSUI already has.

Auras and tooltips (Part 1) are cross-cutting and get filled in as target frame, combat, spells,
and inventory each need them.

---

## How the rest of this document is organised

- **Part 1 — UI Foundations:** the FrameXML→ImGui mapping, the shared unit-frame template, the
  StatusBar primitive, auras (buffs/debuffs), the GameTooltip, and the ImGui convention set.
- **Part 2 — Unit portraits:** what a 1.12 portrait actually is (a live 3D head-shot baked to a
  texture), the camera-from-M2 derivation, and the FBO `PortraitBooth` + paperdoll plan.
- **Part 3 — Target selection, hover, highlighting & nameplates.**
- **Part 4 — Combat:** attack state, the combat log, floating combat text, health/power sourcing.
- **Part 5 — Spells:** the DBC data model, spellbook, the cast pipeline (`TryCast`), the casting bar.
- **Part 6 — Action bars:** the slot model, server sync, cooldowns, usability tinting, the stance bar.
- **Part 7 — Inventory:** the server item-query model, bags, bank, the drag/drop cursor, item tooltips.
- **Part 8 — Character page + skills + talents:** the stat spec, the skills window, the talent tree.
- **Appendix A — Opcode table** (byte-verified from benilla `opcode.rs`).
- **Appendix B — UpdateFields index table** (byte-verified from benilla `fields/mod.rs`).
- **Appendix C — DBC readers to add.**
- **Appendix D — Empirical verification checklist** (tests to run once each system is built).
- **Appendix E — Open items to verify against source.**

Each part is self-contained and file:line-cited; the internal section numbers (§1, §2, …) restart
per part. Cross-part references name the part.

---

# Part 1 — UI Foundations (FrameXML → ImGui)

Ground truth: **benilla** (Rust/Bevy, READ-ONLY) at `crates/…`. Target: **MSUIClient** (C#, Silk.NET/OpenGL,
ImGui HUD, **no FrameXML**) at `MSUIClient/…`. WoW 1.12.1 build 5875. Citations are `file:line` read
directly; every non-obvious dimension/colour/law is pinned. The other six sections (target, combat, spells,
action bars, inventory, character page) lean on the strategy statement (§A1), the shared unit-frame /
statusbar / aura / tooltip primitives (§A2–A5), the ImGui convention set (§A6), and the portrait/paperdoll
render-to-texture plan (§B7–B11) established here.

Staging note: the MSUI upload is a **subset**. `Engine/Camera.cs`, `ClientWindow.cs`, `GlueBooth.cs`,
`Texture.cs`, `Shader.cs`, `World/Units/CharacterRenderer.cs` (+Creature/M2Animator) and `Program.Net.cs`
are present and cited from source. `Engine/UI/WowSkin.cs`, `UiFont`, `Engine/GlueScene.cs`, and
`Formats/M2Reader.cs` are **referenced by the task brief but not in the staged tree** — claims about them
are flagged `[brief, unverified]` and lean on the one in-tree cross-reference each has (e.g. `WowSkin.Highlight`
used at `MSUIClient/MSUIClient/Program.Net.cs:804`).

---

## A. UI Foundations

## A1. The FrameXML model in one page, and the porting strategy

### What benilla actually is
benilla runs the **real Blizzard FrameXML**: it ships `ActionBar.xml`, `CharacterFrame.xml`,
`UnitFrames.xml`, `GameTooltip.xml`, … under `crates/benilla/assets/ui/`, and a whole `benilla-ui` crate that
is a **FrameXML + layout + Lua-script engine** (`crates/benilla-ui/src/lib.rs:1`). That crate is deliberately
engine-free: `toc` (addon manifest), `framexml` (document parser), `layout` (anchor resolver), `widget`
(frame arena), `order` (draw-order key), `script` (an mlua 5.1 host binding the model to the FrameScript
object model), `loader` (materialises a parsed doc into live frames) — `lib.rs:18-24`. The Bevy app supplies
game-state data through Lua globals; FrameXML/addon Lua runs unmodified against it.

**A frame** is the modelled subset of `CSimpleFrame` (`crates/benilla-ui/src/widget/mod.rs:195-261`):
- `kind` — one of Frame/Button/CheckButton/EditBox/StatusBar/ScrollFrame/ScrollingMessageFrame/Slider/
  Minimap/Cooldown/GameTooltip (the ctor's kind-state match, `widget/mod.rs:471-486`); region leaves are
  Texture or FontString (`RegionKind`, used at `widget/mod.rs:264-277`).
- `parent`, `children` (insertion order), `regions`; `name` auto-published **first-wins, non-overwriting**
  (`widget/mod.rs:391`).
- geometry via the layout input (below), plus `strata` (default MEDIUM), `level` (u16, default 0), `alpha`,
  `scale`, `shown`/`effective_visible`, `mouse_enabled` (default false; buttons/editbox/etc. enable in ctor,
  `widget/mod.rs:441-458`), `clamped_to_screen` (true only for GameTooltip, `:462`), `hit_rect_insets`.

**Anchors / points / size** (`crates/benilla-ui/src/layout.rs`). A frame is placed by a list of `Anchor`s;
each is `{point, relative_to, relative_point, x_off, y_off}` (`layout.rs:157-169`) — "pin *my* `point` to
*target's* `relative_point` + offset". Nine points, client discriminants 0..8 TOPLEFT..BOTTOMRIGHT
(`layout.rs:69-79`). A rect is `[bottom,left,top,right]`, **y-up** (`top>bottom`), screen pixels
(`layout.rs:109-152`, `:27-31`). Explicit `width`/`height` of `0.0` means "derive from the opposing anchors"
(`layout.rs:195-203`). Resolution runs an anchor graph → resolved rects through four float kernels
(`combine_edge` `:268`, `combine_center` `:291`, `anchor_resolve_x/anchor_resolve_y` `:317`/`:346`) and an
`assemble` that fails on any unresolved edge and optionally clamps into `[0,extent]`
(`assemble_rect`, `:388`; `ResolveOutcome`, `:243`). Offsets and the size span scale by
`layoutScale` = effective scale; intermediates run in f64, narrowed to f32 only at the client's documented
store sites (`layout.rs:12-22`).

**Draw order** is a **flat painter's order, not a hierarchical walk** (`crates/benilla-ui/src/order.rs:12-14`).
Every visible frame is an independent entry in a `(strata, level)` bucket; regions draw inside their frame by
`(draw layer, sub-level, texture<fontstring, declaration order)`. Nine strata WORLD..TOOLTIP (+ Era BLIZZARD),
`order.rs:46-67`; five layers BACKGROUND/BORDER/ARTWORK/OVERLAY/HIGHLIGHT, `:106-115`. The whole render list
is one sort on a packed `ZKey` u64 (`order.rs:139-160`, `traversal` `:280-306`). Tie-break within a bucket is
frame **insertion order**, and a frame re-sequences to its bucket tail when it *becomes visible*
(`widget/mod.rs:365-385`) — "show order is draw order". Hit-testing walks the sorted list **top-down**,
first mouse-enabled visible frame whose rect contains the cursor wins (`order.rs:332`).

**Propagation** (`widget/mod.rs:9-44`): `effectiveVisible = shown AND parent-chain visible` (a hidden
mid-tree frame blocks its subtree); strata forces the **whole subtree** to one value; level shifts same-strata
children by the delta; `effectiveScale = parentScale · ownScale` (ε-gated, `SCALE_EPS` `:54`); 1.12 alpha is a
set-time **overwrite cascade** (not a live parent×child product).

**Template model** (`crates/benilla-ui/src/framexml.rs`): a document is an order-preserving `Vec<TopLevel>`
(Include/Script/Font/Template/Instance, `:87-100`) so interleaved Lua load order survives; `virtual="true"`
registers a template by name (`:200-209`); `inherits="A,B"` merges **inherited-first, own-last**, children
concatenated, attrs overridden left-to-right (`expand`/`merge`, `:284-359`); `$parent` name tokens substitute
the nearest resolved ancestor name, fallback literal `"Top"` (`resolve_name`, `:386-394`, `DEFAULT_PARENT_NAME`
`:365`). Scripts (`<OnLoad>`, `<OnEvent>`, …) are Lua bodies dispatched by the `script` host.

### FrameXML concept → ImGui equivalent (or skip)

| FrameXML concept (benilla) | MSUI ImGui equivalent | Notes |
|---|---|---|
| `<Frame>`/panel, `frameStrata`/`frameLevel` | `ImGui.Begin`/child region; window z = focus/begin order | Skip the 9-strata engine; pick the right window flags + draw order manually. |
| Anchor graph (`SetPoint`, 9 points, opposing-edge size) | Manual layout: `SetNextWindowPos/Size`, `SetCursorPos`, `Dummy`/`SameLine` | Use benilla's XML **only as the dimension/anchor spec** (px offsets, sizes). Do not build a resolver. |
| `layoutScale` / effective scale | `ImGuiStyle.ScaleAllSizes` + `FontGlobalScale` (already done, `MSUIClient/MSUIClient/Engine/ClientWindow.cs:331-332`) | One global UI scale, not per-frame cascade. |
| `<Texture>` region (BLP) | `ImGui.Image(glTexId, size, uv0, uv1)` | `Engine/Texture.cs` already gives a GL handle usable as `ImTextureID`. |
| `<FontString>` + `<Font>` objects | `ImGui.TextColored` / push font; FRIZQT via `UiFont` `[brief]` | Colour tables ported as C# constants (see §A2/A5). |
| `<StatusBar>` fill model | custom bar draw (fraction × width crop) — §A3 | `ImGui.ProgressBar` is close but can't do the WoW texture crop/spark; use a small helper. |
| `<Backdrop>` (tile bg + edge) | `WowSkin.DrawBackdrop` `[brief]` (`Program.Net.cs:804` uses `WowSkin.*`) | The plate art is the same BLPs; draw with `AddImage` calls. |
| `<Cooldown>` swipe | custom radial/alpha overlay primitive — §A6 | No ImGui builtin. |
| GameTooltip engine (line stack, SetOwner, auto-size) | one shared `Tooltip` helper — §A5 | ImGui tooltips give the window; you supply lines/backdrop/anchor. |
| `<Model>`/`<PlayerModel>` 3D pane; unit portrait | **render M2 to an FBO texture**, `ImGui.Image` it — Part B | The one place custom GL is mandatory. |
| Lua scripts / event dispatch / addon VM | **skip entirely** — native C# game state → ImGui each frame | benilla's `script`/`loader`/`toc` have no MSUI analogue. |

### The strategic recommendation (the statement the other sections lean on)
> **MSUI does not port FrameXML.** Treat benilla's shipped `.xml` as the **authoritative spec** — exact
> dimensions, textures, anchors, colours, and behaviour — and its native Rust modules (`ui_*`, `benilla-ui`
> bindings) as the **game-state logic**. Rebuild each panel as a **native ImGui window** (or a custom-GL
> element where ImGui can't express it), reading MSUI's own C# game state. Do **not** build an anchor resolver,
> a strata engine, a template expander, or a Lua host.

**Where custom GL (not ImGui) is warranted** — three cases only, each because ImGui draws 2D screen-space quads:
1. **World-anchored elements** — nameplates, selection/target rings, world health bars — must project a world
   position through the live camera and depth-test/scale with distance (benilla draws these in-world, not in
   the frame tree). ImGui overlay draw lists can *place* a projected 2D bar, but the ring itself is a GL decal.
2. **The 3D portrait / paperdoll / dress-up** — an M2 rendered to an offscreen texture (Part B).
3. Optional: a bespoke cooldown-swipe if the ImGui triangle-fan approach (§A6) proves insufficient.
Everything else — unit frames, bars, buff grids, tooltips, bags, action bars, character sheet stats — is
plain ImGui reading the spec dimensions below.

---

## A2. The shared unit-frame template (`UnitFrames.xml`)

Every unit frame (player, target, party — and the target/combat sections' instances) shares one anatomy. The
player and target are **horizontal mirrors of one art sheet** `Interface\TargetingFrame\UI-TargetingFrame`
(player samples it flipped → portrait window on the **left**; target un-flipped → window on the **right**),
`crates/benilla/assets/ui/UnitFrames.xml:7-10`.

**Layering law** (draw order must read *trough < bars < ring art < text* so bars look recessed): parent frame
holds trough + portrait in **BACKGROUND**; the `$parentHealthBar`/`$parentPowerBar` StatusBars come next; a
child `$parentTextureFrame` (fills the parent) holds the ring art (BACKGROUND) + name/level/dead text (OVERLAY),
declared **last** so it paints over the bar edges and the portrait (`UnitFrames.xml:12-25`). In ImGui this
maps to draw-call ordering within one window (bars first, then the frame art image, then text).

**Player frame** (`UnitFrames.xml:697-794`), authentic dims:
- Frame `BenillaPlayerFrame` **232×100**, `frameStrata="BACKGROUND"` (`:697-698`), anchored UIParent
  TOPLEFT (−19,−4) (`:700`).
- **Portrait** `$parentPortrait` **64×64** at TOPLEFT (42,−12) (`:714-717`) — round, live model bake (Part B).
- **HealthBar** `$parentHealthBar` StatusBar **119×12** at (106,−41) (`:726-729`).
- **PowerBar** `$parentPowerBar` StatusBar **119×12** at (106,−52) (`:735-738`); a **maxPower 0** unit hides
  the power bar (`:29`).
- Name / level / dead text live in the TextureFrame at OVERLAY (`:783-794`).

**Target frame** (`UnitFrames.xml:871-899`): `BenillaTargetFrame` 232×100, `frameStrata="LOW"` (one above the
player frame), `hidden` by default (`:871-872`); portrait mirrored to TOPRIGHT (−42,−12) (`:896-899`).

**Colour tables** (port these verbatim as C# constants):
- **PowerBarColor** by power type token (`UnitFrames.xml:94-100`): MANA `(0,0,1)`, RAGE `(1,0,0)`, FOCUS
  `(1,0.5,0.25)`, ENERGY `(1,1,0)`, HAPPINESS `(0,1,1)`. Power type comes from `UNIT_FIELD_BYTES_0` byte 3
  (`crates/benilla-protocol/src/messages/update_object/fields/unit.rs:108-113`).
- **UnitReactionColor** 1..8 (hated..exalted) for the name/level plate tint (`UnitFrames.xml:106-114`): 1–2
  red, 3 orange `(1,0.5,0)`, 4 yellow `(1,1,0)`, 5–7 green. This is the same reaction the target/combat
  sections tint by.

**Elite/rare dragon border**: creature `rank` surfaces (query record; the tooltip's rank word reads it), but
the classification **border-art swap is an explicit unbuilt gap** in benilla (`UnitFrames.xml:53-56`) — MSUI
should implement it from `rank` (1/2 → Elite dragon, 3 → Boss/world-boss skull) since it has the data.

**Aura rows** on the target frame (the target section instantiates these): 5 buff / 16 debuff buttons under
the frame; icons **21px shrinking to 17px** when the debuff count reaches the wrap; dispel-tinted
`UI-Debuff-Overlays` border; stack count shown above 1; **no timers** (the 1.12 wire carries no duration for
another unit) (`UnitFrames.xml:72-81`). Ordering for another unit is ascending raw slot (§A4).

Combat/rest/leader/PvP icons: PvP icon exists; rest/combat/leader/group/raid-target/target-of-target are
listed **OUT / future** in benilla (`UnitFrames.xml:60,69-70`) — MSUI can add them from its own state.

---

## A3. StatusBar — the one bar primitive (`script/statusbar.rs`)

Every health / mana / cast / xp / reputation / tooltip-health bar is one primitive. Byte-verified fill law
(`crates/benilla-ui/src/script/statusbar.rs:8-10`): `SetValue` writes the bar region's 4-corner UV block with
`u1 = GetValue()` **and** recomputes `right = left + frac·width` — the fill is a **left-anchored crop of the
art, never a squeeze**. State/methods:
- `SetMinMaxValues(min,max)` — a reversed pair is swapped (`:106-117`); `SetValue(v)` clamps to `[min,max]`
  and fires `OnValueChanged` only on a real change (`:49-55`, `:123-128`).
- Orientation HORIZONTAL(0)/VERTICAL(1) (`:134-154`).
- `SetStatusBarTexture(path|rgba)` and `SetStatusBarColor(r,g,b[,a])` target a bar region created on first use
  (`:158-229`).

Verified crop arithmetic: a 119-wide bar at fraction 0.6 yields a fill quad of width **71.4**
(`crates/benilla/src/ui_script/unit_frame_tests.rs:184-186`).

**MSUI helper** (`StatusBar`): draw the background art, then the fill as `AddImage` with `uv1.x = frac`
(cropping the texture, matching the client) and `size.x = frac·width`; tint by the caller's colour. Optional
spark texture at the fill edge and a centred text overlay (hidden unless a "show text" toggle is on — benilla
omits the value FontStrings to match the default look, `UnitFrames.xml:40`). `ImGui.ProgressBar` is a fallback
but cannot crop a WoW bar texture or draw the spark — prefer the custom helper.

---

## A4. Auras — buffs/debuffs (`ui_aura.rs`, `script/aura.rs`, `BuffFrame.xml`)

**Where auras come from.** The unit descriptor's `UNIT_FIELD_AURA` block: 48 slots, **buffs 0–31, debuffs
32–47** (`crates/benilla-protocol/src/messages/update_object/fields/unit.rs:94-106`). A slot is *live* iff its
`AURAFLAGS` nibble has an effect-index bit set (`flags & 0x0E`), not merely a non-zero spell id (`:72-93`). Per
slot the wire gives spell id, flags, caster **level**, and **stacks** (`stack−1` on the wire, so an occupied
slot is ≥1) (`:83-92`).

**AuraState** — the projection every aura display reads (`crates/benilla-ui/src/script/aura.rs:26-49`):
`spell_id`, `name`, `icon` (extensionless `Interface\Icons\…` from Spell.dbc), `count` (stacks; shown only
>1), `debuff_type` (dispel class name), `duration`, `expiration_time`, `helpful` (buff vs debuff),
`cancelable`. The `UnitAura` return tuple order is the Era signature (`aura.rs:103-122`).

**Buff vs debuff**: `helpful = slot < UNIT_AURA_POSITIVE_SLOTS` (`crates/benilla/src/ui_aura.rs:299`).

**Debuff border colour by dispel school** — the `DebuffTypeColor` table
(`crates/benilla/assets/ui/BuffFrame.xml:51-56`), keyed by `UnitDebuff`'s `debuffType` return:
- `none` (undispellable) → `(0.80, 0.00, 0.00)` red
- `Magic` → `(0.20, 0.60, 1.00)` blue
- `Curse` → `(0.60, 0.00, 1.00)` purple
- `Disease` → `(0.60, 0.40, 0.00)` brown
- `Poison` → `(0.00, 0.60, 0.00)` green

The class name comes from `SpellDispelType.dbc` via `SpellCatalog::dispel_name` (`ui_aura.rs:295`), not from
the raw dispel id. Verified: a Magic debuff draws the border at `(0.20,0.60,1.00)`
(`crates/benilla/src/ui_script/buff_tests.rs:147`).

**Durations**: **self only.** `SMSG_UPDATE_AURA_DURATION` fills a per-slot timer for the player
(`ui_aura.rs:48-79`); every other unit's auras have `duration = 0`, and the reference target/party frames show
no timers (`aura.rs:38-43`, `ui_aura.rs:376-379`). The player buff bar counts down against `expiration_time`
using `GetTime()`, exactly as the cast bar does.

**Display filter**: a spell flagged never-display (`SPELL_ATTR_DO_NOT_DISPLAY` / `NO_AURA_ICON`, e.g. a warrior
stance) is hidden on **every** aura display, and tracking spells are diverted to the minimap tracking icon
(`ui_aura.rs:180-217`). Catalog miss → fail-open (stays visible).

**Ordering**: the player's own bar is an **insertion-ordered cache** that repacks on removal (a new aura
appends at the end, a dropped one closes its gap — `ui_aura.rs:81-152`); any other unit is read **straight,
ascending raw slot** (`unit.rs:94-106`). For MSUI, maintaining the player's insertion cache is optional
polish; ascending slot for both is a reasonable v1.

**Buff-grid layout** (`BuffFrame.xml:15-18`, buttons `buff_tests.rs:132-135`): **16 buff buttons (two rows of
8)** above **8 debuff buttons**; `BuffButton0..15` are buffs, `BuffButton16..` debuffs; icon **30px** + a
**15px** duration gutter, rows **45px** apart; the bar is right-aligned under the minimap at UIParent TOPRIGHT
**(−175, −13)**. Flash/warning constants (`BuffFrame.xml:43-47`): `BUFF_FLASH_TIME_ON/OFF = 0.75`,
`BUFF_MIN_ALPHA = 0.3`, `BUFF_WARNING_TIME = 31`, `BUFF_DURATION_WARNING_TIME = 60`.

**MSUI aura display**: an `IconButton` grid (§A6). Each icon = the Spell.dbc icon BLP; overlay a dispel-tinted
border quad for debuffs; draw the stack count when >1; for the player, draw the countdown text and pulse alpha
under 31s. Right-click cancels a buff (queue `CMSG_CANCEL_AURA` by spell id, gated on the cancelable bit —
`aura.rs:229-258`).

---

## A5. GameTooltip — the shared tooltip (`ui_tooltip.rs`, `tooltip_unit.rs`, `GameTooltip.xml`)

The tooltip other sections fill with spell/item/unit content. In benilla it is a real `<GameTooltip>` engine
widget; the **instance** in `GameTooltip.xml` declares only the plate + money row + health bar.

**Plate / backdrop** (`crates/benilla/assets/ui/GameTooltip.xml:223-239`): `frameStrata="TOOLTIP"`, hidden by
default; initial `Size 120×32` then auto-grows to the measured lines; `Backdrop` = bg
`Interface\Tooltips\UI-Tooltip-Background` (tiled) + edge `UI-Tooltip-Border`, **EdgeSize 16, TileSize 16,
insets 5/5/5/5**. Default colours set in OnLoad: **border white** `(1,1,1)`, **background** `(0.09,0.09,0.19)`
dark navy (`:20-21`). A GameTooltip is the one frame kind that is **clamped to screen** by construction
(`crates/benilla-ui/src/widget/mod.rs:462`) — no tooltip ever leaves the window.

**Line model**: engine-created named FontString **pairs** `GameTooltipTextLeftN` / `TextRightN`
(`GameTooltip.xml:390-397`); left column is the content, right column the aligned value (cost, "Magic", etc.).
Below the lines sits `$parentStatusBar` (health, height 8) and a money-coin trio (13×13) (`:261-262`, `:320-321`).

**Anchoring — two modes**:
1. **To a frame / owner**: `SetOwner(parent, anchor)` then a `SetPoint`; the default screen-corner seat is
   `GameTooltip_SetDefaultAnchor` → BOTTOMRIGHT of UIParent at offset `(−CONTAINER_OFFSET_X − 13, +70)`
   (`GameTooltip.xml:22-26`, `:76-80`) — bottom-right, above the bag row.
2. **To the cursor**: a world/GameObject hover plate rides the cursor position each frame
   (`crates/benilla/src/ui_tooltip.rs:565-572`). The tooltip **rebuilds once per hover-target change**, not
   every frame (`ui_tooltip.rs:1-6`).

**Unit tooltip content** (`crates/benilla-ui/src/script/tooltip_unit.rs:1-25`, `:55-97`): line 1 **name**
(gold `(1,0.824,0)` = `0xffffd200`, recoloured by reaction, `:42-43`); creature subtitle (white); a **level
line** built from three slots ("Level N", + class/type/rank, "??" for a world boss / much-higher hostile /
unknowable); faction name; PvP (white) / Skinnable (red) / Civilian (green) / Leader (white); and **health on
the attached `$parentStatusBar`**.

**MSUI helper** (`Tooltip`): use `ImGui.BeginTooltip`/`SetTooltip` for the window, but supply your own backdrop
(the navy/white plate via `WowSkin.DrawBackdrop` `[brief]`) and left/right line rows so spell/item/unit content
looks authentic. Colour lines by the tables above. This one helper is what the spell, inventory, and
target sections call to render their hover content.

---

## A6. MSUI ImGui UI conventions (the recommendation the other sections adopt)

MSUI today: **ImGui-only HUD**. `ClientWindow.cs` owns the window/GL/input and the `ImGuiController`
(`MSUIClient/MSUIClient/Engine/ClientWindow.cs:6,60,309`) from `Silk.NET.OpenGL.Extensions.ImGui`; it raises an
`OnGui` hook where HUD windows are built (`:102`), and draws the 3D scene first, ImGui second
(`:158-189`). `Program.Net.cs` draws every current panel — login (`Program.Net.cs:359`), char-select
(`:646`), and a minimal debug **"Server"** in-world window (`:861-896`, currently just stats + a Disconnect
button; **no real unit frames yet**). A `WowSkin` helper (gold colour, backdrop/plaque/button helpers) and a
FRIZQT `UiFont` exist per the brief and are referenced (`WowSkin.Highlight`/`WowSkin.Muted` at
`Program.Net.cs:804`) `[brief for full API]`. `Texture.cs` uploads BLP→GL (`Engine/Texture.cs:60-114`) and a
`Texture.Handle` (`:21`) is directly usable as an ImGui `ImTextureID`.

Recommended shared convention set (build once, every panel reuses):
- **Window chrome**: wrap each HUD panel in `WowSkin.DrawBackdrop` for the tiled bg + edge plate; headings via
  `WowSkin.HeaderPlaque` in the gold heading colour; FRIZQT font pushed for all UI text. Match the spec
  strata by draw/begin ordering (tooltips last / topmost).
- **`StatusBar(frac, width, height, color, tex, spark?, text?)`** — §A3 (crop fill, not `ProgressBar`).
- **`IconButton(glTex, size, cooldownFrac?, count?, borderColor?)`** — the shared cell for action bars, buff
  grids, bag slots, spellbook: draws the icon via `ImGui.Image`, an optional dispel/quality border quad, a
  stack/count number, and a cooldown swipe.
- **`CooldownSwipe(frac)`** — no ImGui builtin; draw a dark radial wedge (triangle fan on the ImGui draw list,
  angle = `frac·2π`) or a simple top-down alpha wipe over the icon; matches benilla's `<Cooldown>` overlay.
- **`Tooltip(lines)`** — §A5, the shared hover renderer.
- **Colour constants** ported verbatim: PowerBarColor, ReactionColor (§A2), DebuffTypeColor (§A4), quality
  colours, tooltip navy/white/gold. One C# static class, referenced everywhere.

---

# Part 2 — Unit portraits (2D + 3D)

## B7. What a 1.12 portrait actually is

The real client renders a unit's model **once** into a tiny **64×64** off-screen texture, freezes it (re-baked
only on model change), then stamps a **round alpha stencil** — the low resolution is a 2004 shortcut, not a
look (`crates/benilla/src/portrait/mod.rs:1-8`). So a WoW portrait is a **live 3D head-shot render captured to
a texture**, then sampled flat and circle-masked in the frame — **not** a hand-authored 2D image. (A static 2D
fallback exists only as a stand-in while a model streams: `TemporaryPortrait-{Sex}-{Race}` / `-Monster`,
`portrait/mod.rs:26-27`.)

benilla keeps the *idea* and bakes it **properly** (hi-res 256², studio-lit, then circle-cut at draw), one
render layer + camera per slot into a `PortraitImages` entry (`portrait/mod.rs:10-18`, `PORTRAIT_SIZE = 256`
`:119`; slots player/target/npc/party `:97-99`). The circle cut is done in the UI quad shader at draw time
(the ref's stencil), so the bake stays a plain square texture.

**The portrait camera — derived from the M2.** The framing is the model's **authored portrait camera**,
selected as `cameraLookup[0]` (`portrait/mod.rs:29-40`; `crates/benilla-formats/tests/m2_portrait_camera.rs:1-4`
pins vanilla camera-record **stride 0x7c** + the `cameraLookup[0]` selection). The camera record gives eye,
target, roll, fov, near, far. `frame()` builds it verbatim: `lookAt(eye, target, up)` with `up = R(fwd, roll)·+Y`,
plus the client's exact projection (`crates/benilla/src/portrait/framing.rs:173-208`). Regression pin
(HumanMale): **fov = π/4** (a *diagonal* angle), eye `(0.6335, −0.3879, 1.8867)`, target `(0.0627, 0.0343,
1.8636)`, roll 0 (`m2_portrait_camera.rs:54-73`). WoW models face **+X**, and a portrait camera sits **+X of
the subject looking back** (`dx > 0.1`, `:46-51`) and off to the model's right — which is why the reference
portrait faces viewer-left.

**Projection** (`framing.rs:24-60`): the client's `gxumath` perspective is a **diagonal-FOV** projection —
half-angle `θ = (fov/2)/√(aspect²+1)`, `m11 = 1/tan θ`, `m00 = m11/aspect`, fed `aspect = 4/3`. Net: vertical
half-angle **`0.3·fov`** (i.e. vertical opening `0.6·fov`; `DIAG_TO_VERT = 0.6` `:22`) — a tight crop ~1.72–
1.75× tighter than a naive `tan(fov/2)` read. benilla runs the matrix at **aspect 1.0** (square-true; the 3:4
anamorphic squeeze was falsified against reference captures, `framing.rs:50-60`), keeping only the 0.6
diag→vert crop factor.

**Head-anchor fallback** (camera-less creatures/props, `framing.rs:139-208`): aim at the bind-pose head — the
**KeyBone-6 head bone pivot**, else the **helm attach point (attachment id 11)** — from a slight three-quarter
yaw, sized by the model's own height with a footprint floor. Bone pivots are reconstructed by a parent-chain
translation walk (`bind_bone_global`, `:110-122`).

**Lighting rig** (`crates/benilla/src/portrait/light.rs`): a booth model is drawn with the world's own
materials **cloned with only the light buffer swapped** (`material_variant`, `:71-100`) — so a bake never
inherits the world's time of day (a night portrait would render black) and fog is forced OFF in the booth. Two
fixed rigs: round portraits use a **neutral front-lit studio** (ambient `(0.58,0.56,0.54)`, diffuse
`(0.85,0.82,0.78)`, key from the camera's own three-quarter side, `studio_light_rows` `:160-179`); the
**body/paperdoll** panes use the reference `<PlayerModel>` widget's own light (directional, to-light `(0,1,0)`,
diffuse `(0.8,0.8,0.64)`, ambient `(0.7,0.7,0.7)` — a pure side light, so the pane reads mostly ambient-lit,
`model_pane_light_rows` `:102-147`).

**The "booth" render-to-texture pattern** (`crates/benilla/src/portrait/booth.rs`): `spawn_booth_model` builds
a **fresh throwaway instance posed at Stand**, never the unit's live world pose (`:48-94`) — its own joint
hierarchy, the parts' skinned twins bound to it, riders (helm/shoulder/weapon) seated on their bones
(`:157-184`). `BoothMotion` is **Frozen** for a still portrait (Stand paused at t=0) or **Loop** for the live
char-create preview (`:51-55`, `:133-155`, `:205-240`). The camera renders that instance into the offscreen
target; the frame samples the texture.

## B8. The PlayerModel / DressUp widget — render-to-texture-in-a-panel

WoW's paperdoll (character sheet), inspect, and dress-up are `<PlayerModel>`/`<DressUpModel>` widgets: a live,
rotatable 3D model **inside a UI frame**. Note the staged `crates/benilla-ui/src/script/model.rs` is actually
the VM's central `Model` data struct (arena/layout/scripts), and it carries the pane rotation state —
`paperdoll_yaw` and `inspect_yaw` (`script/model.rs:552`, `:566-567`) driven by `Model:SetRotation` /
`Model_OnUpdate` at `ROTATIONS_PER_SECOND = 0.5` (`crates/benilla/assets/ui/UIParent.xml:69`).

**benilla's actual implementation of the pane is the render-to-texture pattern MSUI should copy.** Rather than
port the live `<PlayerModel>` widget, benilla materialises the character-sheet model pane as a **plain
`<Frame>` that samples a booth bake**: `CharacterModelFrame` (233×224) draws `BenillaSetBoothTexture(region,
"paperdoll")` (`crates/benilla/assets/ui/CharacterFrame.xml:78-82`, `:1327-1330`). The `"paperdoll"` slot
reuses the exact booth pipeline but frames the **whole standing figure from the model's bounds** (not the bust
camera), bakes at **512²**, spins to a live yaw, and is sampled **square** (no circle mask)
(`portrait/mod.rs:100-123`). Body framing derives the eye distance from the figure's vertical span through the
same projection (`body_frame`, `framing.rs:247-279`; `BODY_FOV = 0.85`, vertical half-angle `0.3·BODY_FOV`).
Left-drag on the pane rotates it (`frame.facing` ↔ `model.rotation`, `CharacterFrame.xml:1049`, `:1096`).
The character-sheet **portrait** (60×60, `CharacterFrame.xml:1222-1226`) is the same booth as the unit-frame
`"player"` slot.

## B9. What MSUI has today

MSUI is well-positioned — it already renders skinned unit M2s and drives a booth camera:
- **`GlueBooth`** renders a per-race scene fullscreen + a **dressed character standing in it**
  (`MSUIClient/MSUIClient/Engine/GlueBooth.cs:329-426`). It owns its **own `CharacterRenderer` + `Camera`**
  (`:58-71`), builds the character with the full appearance/equipment recipe (`:257-325`), and drives the
  camera by solving orbit params to match the scene's authored camera 0 (`:356-378`). This is a working
  M2-to-scene render — the natural basis for a portrait booth.
- **`CharacterRenderer.Render(Camera, in UnitState)`** draws a skinned, equipped M2
  (`World/Units/CharacterRenderer.cs:1808-1837`); `UnitState { Position, Yaw, Grounded, … }` (`:93-102`);
  `ModelScale` (`:277`), `SunDirection` (`:1988`), `Load(race,gender)` (`:420`), a `BindPose` flag for a
  static pose (`:1816`). `CreatureRenderer` + `M2Animator` cover non-player units.
- **`Camera`** is a WoW-Z-up orbit camera with `CreateLookAt` + `CreatePerspectiveFieldOfView`, exposing
  `Target/Yaw/Pitch/Distance/FieldOfViewDegrees/AspectRatio/Near/Far/EyeHeight` and `View`/`Projection`/
  `RelativeViewProjection` (`Engine/Camera.cs:26-233`). A head-shot is just a specific
  target/position/fov/aspect.
- **`Texture`** uploads BLP→GL and exposes a GL `Handle` usable as an ImGui `ImTextureID` (`Engine/Texture.cs:21`).

**The one missing piece: there is no FBO / render-to-texture path.** `Texture.cs` only uploads CPU byte
buffers (`From2D`/`Array2D`/`FromRgbaNoMips`, `:60-140`) — **no `GenFramebuffer`/color-attachment support**;
the only "Framebuffer" references in the tree are MSAA sample count + window size in `ClientWindow.cs` (no
offscreen target). `GlueBooth` renders straight to the **default framebuffer** over the backdrop
(`GlueBooth.cs:337`, `:417-425` share the scene depth buffer), never to a texture. So a portrait ≈ **the
GlueBooth character render, but into an FBO, then `ImGui.Image` the FBO colour texture**.

## B10. Port plan for MSUI

**(a) Offscreen FBO render target + a `PortraitBooth`.**
1. Add an `Fbo`/`RenderTarget` helper (new — MSUI has none): `GenFramebuffer` + an RGBA8 colour texture
   (`GenTexture`, `TexImage2D(..., null)` at e.g. 256² portraits / 512² paperdoll) + a depth renderbuffer
   (`GenRenderbuffer`, `Depth24`). Expose the colour texture's GL handle for `ImGui.Image` (like
   `Texture.Handle`, `Engine/Texture.cs:21`).
2. A `PortraitBooth` that, per bake: `BindFramebuffer`, set viewport to the target size, clear colour+depth,
   frame a head-shot camera (below), call `_char.Render(cam, state)` with `BindPose` (freeze at Stand), unbind.
   Reuse `CharacterRenderer`/`CreatureRenderer` — the same instances/recipe `GlueBooth` already builds
   (`GlueBooth.cs:257-325`). Cache per unit; re-bake only on model/appearance change (as benilla freezes and
   re-bakes only on change, `portrait/mod.rs:1-8`).
3. **Head-shot camera (spec)** — derive it like benilla. Best: parse the M2's `cameraLookup[0]` portrait
   camera (record stride **0x7c**; `m2_portrait_camera.rs:1-4`) and set the MSUI `Camera` to eye = `position`,
   `Target` = `target`, and fov so the **vertical** opening = `0.6·fov_record` (i.e.
   `FieldOfViewDegrees = 0.6·fov_record` at aspect 1.0, since the booth renders a **square** target —
   `framing.rs:24-60`, `:44-48`). Fallback for a camera-less model: aim at the **head bone (KeyBone 6)** or
   helm attach (id 11), three-quarter yaw, distance sized by height (`framing.rs:139-208`). Note MSUI's
   `M2Reader` is not in the staged tree — a portrait-camera parse must be added there `[brief]`; until then use
   the head-bone fallback, which `CharacterRenderer`'s skeleton already exposes.
4. **Lighting**: reuse the `GlueBooth`/`CharacterRenderer` light knobs (`SunDirection`, `AmbientIntensity`,
   `SunIntensity`, `GlueBooth.cs:390-404`) — a fixed neutral front-lit studio key from the camera side matches
   benilla's `studio_light_rows` (`light.rs:160-179`). Force fog OFF in the booth.

**(b) 2D fallback portraits.** While a model streams or for a display-less unit, sample a static
`TemporaryPortrait-{Sex}-{Race}` / `-Monster` BLP via the existing `Texture` path (`portrait/mod.rs:26-27`).
Cheap and avoids a blank ring.

**(c) Wire into the unit frames (§A2).** In the player/target frame ImGui window, draw the portrait via
`ImGui.Image(booth.ColorTexId, new Vector2(64,64))` at the portrait anchor (player TOPLEFT (42,−12); target
mirrored), then draw a **circular alpha mask** over/into it (ImGui rounded-image or a pre-masked circle
texture) to match the client's always-round portraits (`UnitFrames.xml:22-23`, `:34-39`). Health/power bars
(§A3) sit beside it.

**(d) Paperdoll / dress-up full-body variant (character sheet).** Same booth at **512²**, **full-body bounds
framing** (not the bust camera), **sampled square** (no circle), with **drag-to-rotate** driving the model yaw
(`portrait/mod.rs:100-123`; `framing.rs:247-279`; `CharacterFrame.xml:78-82`, `:1327-1330`). MSUI's char-select
already proves the full-body render + rotate (`GlueBooth` auto-rotate, `GlueBooth.cs:387-388`); the character
sheet is that render into an FBO shown in an ImGui child region, plus the equipment slot grid around it.

## B11. Gotchas / empirical notes

- **Coordinate spaces.** MSUI is natively WoW **Z-up** end-to-end with **no conversion layer** (`Camera.cs:8-19`),
  which is simpler than benilla's WoW→Bevy map. `GlueBooth` bridges the glTF-Y-up scene mesh to Z-up with
  `R = RotX(+90)` (`GlueBooth.cs:428-429`) — a portrait booth avoids this entirely by putting the character at
  the origin in Z-up and placing the camera directly (the M2 camera record is already model-local).
- **Head-bone framing.** The authored camera is a **face bust** — aiming a body pane through it crops to the
  face (`framing.rs:233-238`); the paperdoll must frame from **bounds**, not `cameraLookup[0]`. Conversely the
  round portrait *must* use `cameraLookup[0]` (or the head bone) or every race/creature crops inconsistently.
- **fov is a *diagonal* angle** in the client convention — do **not** feed `fov_record` straight into a
  vertical-FOV projection; the on-screen vertical opening is `0.6·fov` (`framing.rs:44-48`). Getting this wrong
  makes faces ~1.7× too small/large.
- **Portrait lighting must be booth-local.** Reuse the world material with only the light swapped and fog off,
  or a night/fogged unit bakes dark (`light.rs:17-27`, `:71-100`).
- **Per-race/creature framing differs.** Camera 0 exists on characters and most creatures (verified for
  HumanMale, Wolf, Rabbit — `m2_portrait_camera.rs:22-51`), but some creatures/props are camera-less and need
  the head-anchor heuristic (`framing.rs:139-208`); a head-less model falls to a pivot-height guess.
- **Pose = fresh Stand instance, not the live world pose**, frozen at t=0 (a portrait still) — reusing the
  world instance would show combat/animation frames (`booth.rs:48-94`, `:133-155`). MSUI's `BindPose`
  (`CharacterRenderer.cs:1816`) gives this directly.
- **FBO lifecycle.** Bake lazily and cache by unit; re-bake only on model/appearance/equipment change (benilla
  re-bakes only when the parts key changes, `portrait/mod.rs:20-27`). Restore the previous framebuffer binding,
  viewport, and depth/cull state after each bake — `GlueBooth.Render` already documents the cull/depth-state
  handoff hazard (`GlueBooth.cs:417-425`). Reuse one FBO per size across slots rather than one per unit.
- **`CharacterRenderer` uses camera-relative rendering** (subtracts `camera.Position` from the model
  translation, `CharacterRenderer.cs:1830-1836`) — fine for a booth (put the model near the origin and the
  camera a short standoff away), just don't double-offset.
- **MSAA.** The main framebuffer may be multisampled (`ClientWindow.FramebufferSamples`); a portrait FBO can be
  single-sampled at higher resolution (benilla bakes 256²/512² and relies on resolution, not MSAA,
  `portrait/mod.rs:119-123`).

---

## Open uncertainties

1. **MSUI staging is partial.** `Engine/UI/WowSkin.cs`, `UiFont`, `Engine/GlueScene.cs`, and
   `Formats/M2Reader.cs` are **not in the staged tree** — the brief describes them and they are referenced in
   staged code (e.g. `WowSkin.*` at `Program.Net.cs:804`), but their exact APIs (WowSkin's method signatures,
   whether `M2Reader` already parses `cameraLookup`) could not be verified from source. The port plan assumes
   `M2Reader` needs a portrait-camera parse added (the head-bone fallback works without it).
2. **benilla `kinds.rs` and `script/tooltip.rs` not staged.** The widget kind-state structs (`StatusBarState`,
   `TooltipState`, the `TOOLTIP_PAD/LINE_GAP/WRAP_WIDTH` constants exported at `widget/mod.rs:178-179`) and the
   tooltip line-append/auto-size engine were characterised from their call sites and the XML instance, not the
   defining files. Tooltip padding/wrap numeric constants are therefore not pinned here (the EdgeSize 16 /
   insets 5 from `GameTooltip.xml:236-238` are the concrete plate numbers).
3. **Aura slot constant values** (`UNIT_AURA_SLOTS`, `UNIT_AURA_POSITIVE_SLOTS`) are **imported but not
   defined** in the staged protocol subset; 48 total / 32-buff split is taken from the descriptor comment
   `crates/benilla-protocol/src/messages/update_object/fields/unit.rs:94` ("buffs (0–31) before debuffs
   (32–47)") — treat as high-confidence but not line-pinned to a `const`.
4. **benilla's anamorphic-squeeze decision** (aspect 1.0 vs 4/3) was reversed against director captures
   (`framing.rs:50-60`); MSUI should render the portrait FBO **square (aspect 1.0)** and keep only the
   `0.6·fov` diag→vert factor, matching benilla's shipped choice — but if a side-by-side against a real 1.12
   client shows a squeeze, the 4/3 path is the documented alternative.

# Part 3 — Target selection, hover, highlighting & nameplates

Scope: how benilla turns a click into a selected unit, colours the ground ring and overhead
name by reaction, and paints nameplates — and how to port all of it to MSUI's ImGui/OpenGL
HUD. Faction/reaction decode and unit-frame chrome lean on the shared **Foundations** section;
this section owns the picking, the ring, the cursor, and the selection wire.

Reference layout in benilla: `crates/benilla/src/target/` is a plugin (`mod.rs`) whose Update
chain runs, in order, `update_pick_occlusion → update_hover → update_hovered_object →
classify_cursor → world_right_click_payload → select_on_click → act_on_right_click →
clear_target_requests → target_unit_requests → auto_acquire_attacker → tab_target →
acquire_and_attack → drive_flash → update_ring`, all `.after(WorldStage::Input)`
(`crates/benilla/src/target/mod.rs:178-199`), then `apply_highlight` in PostUpdate
(`mod.rs:202`). State resources: `Selection` (hard target), `Hovered` (mouseover unit),
`HoveredObject` (mouseover GameObject), `PickOcclusion`, `WorldCursor`, `CombatFlash`
(`mod.rs:157-163`).

---

## 1. Mouse-picking: screen click → world unit

### Ray build
Each frame `update_hover` reads the primary window's `cursor_position()` and builds a world ray
with Bevy's `camera.viewport_to_world(cam_tf, cursor)` → `(ray.origin, *ray.direction)`
(`crates/benilla/src/target/hover.rs:130-151`). The pick is **inert while mouse-looking**
(`rig.is_looking()`, cursor hidden) or while the pointer is over the dev UI (`pointer_over_ui.0`)
(`hover.rs:126-129`). MSUI's equivalents already exist: `ClientWindow.MousePosition` (window
pixels, top-left origin) and `ClientWindow.FramebufferSize` — whose doc comment is literally
"for unprojecting the cursor into a ray" (`MSUIClient/MSUIClient/Engine/ClientWindow.cs:207-212`);
the mouse-look gate is `ClientWindow.MouseCaptured` and the over-UI gate is
`ImGui.GetIO().WantCaptureMouse` (already used at `ClientWindow.cs:358,518`).

### The two-phase hit-test (authentic client pick)
benilla reproduces the real 1.12 client's pick volume — it is **silhouette-accurate**, not a
bounding shape:

1. **Broad phase — ray vs bounds sphere.** For each unit root, the *current animation
   sequence's* bounds sphere is world-placed and world-scaled (uniform scale = the root's baked
   `OBJECT_FIELD_SCALE_X`), **no padding**: centre `gt.transform_point(c.bounds_center)`, radius
   `c.bounds_radius * gt.scale().max_element()`. Reject when the ray's closest approach to the
   centre exceeds the radius; a centre behind the origin projects to `t=0` so a unit you stand
   inside stays clickable (`hover.rs:177-191`). An unauthored radius passes through (permissive).
2. **Pass 1 — ray vs posed render mesh.** Each drawn vertex is skinned through the live joint
   palette (the same world-from-bind-pose matrices GPU skinning applies, `hover.rs:194-207`) and
   every triangle is ray-tested (Möller–Trumbore, **two-sided**, `ray_triangle` at
   `hover.rs:463-483`). Nearest world-distance hit wins, **unbounded** (`hover.rs:234-247`).
3. **Pass 2 — halo retry, only if pass 1 hit nothing anywhere.** Same meshes, every vertex
   displaced **+1 model-unit along its skinned normal** (a ~1-yd halo, fattest where the body is
   widest, `ray_posed_mesh(..., inflate=true)` at `hover.rs:388-443`). Resolved by a **priority
   ladder**, not pure distance: last frame's pick sticks (`u32::MAX`, anti-flicker), else an
   **alive** unit (priority 3) beats a **dead** one (priority 2) even when farther, ties by
   distance (`hover.rs:272-291`).
4. **Skinless (cube-fallback) units** keep an interim world-AABB test at pass-1 level
   (`world_aabb`/`ray_aabb`, `hover.rs:252-266,488-514`).

### Priority / tie-breaking when things overlap
- **Nameplate/V-plate rects win first.** Before any world ray, `update_hover` checks the plate
  screen rects (last frame's layout) in reverse draw order and, on a hit, sets `Hovered` with
  `distance = 0.0` and returns — UI beats world at any tie (`hover.rs:141-147`).
- **World occlusion.** `update_pick_occlusion` casts one physics ray through the `PickOccluder`
  set (terrain, WMO walk faces, static doodads — **not** net entities) and records the distance
  (`hover.rs:38-67`). The final unit hit is discarded **iff the world hit is strictly nearer**
  (a tie keeps the object): `if best.is_some_and(|(t,_)| limit < t) { best = None }`
  (`hover.rs:293-297`). A unit behind a wall is not hoverable.
- **Unit vs GameObject.** GameObjects are picked separately into `HoveredObject`
  (`update_hovered_object`, mesh-accurate, `hover.rs:314-386`) and composed by `go_is_nearest`:
  a GO wins the click only when **strictly closer** than the hovered unit (`mod.rs:128-134`).

### Porting geometry note for MSUI
The posed-mesh pick needs skinned vertices at pick time; MSUI's `CreatureRenderer` skins on the
GPU only (`World/Units/CreatureRenderer.cs:496-523`), so a faithful CPU narrow-phase would mean
re-skinning on the CPU. **Recommended approximation: ray vs a vertical bounding cylinder** at
`e.Position`, radius = the unit's ring footprint (§4 formula), half-height ≈ 2× that. It is
"good enough" and matches how the ring is already sized. Take the nearest hit along the ray;
break ties by distance-to-camera and add a one-frame sticky-hover cache to stop strobing between
overlapping units (mirroring `last_pick`, `hover.rs:280-284`). `EntityStore.NearestUnits(from,
max)` (`Net/Entities.cs:134-138`) is a ready screen-independent fallback.

---

## 2. Cursor modes (`cursor_mode.rs`)

`classify_cursor` resolves this frame's `WorldCursor { kind, unable }` from the nearer of the
hovered unit/GO (`crates/benilla/src/target/cursor_mode.rs:362-467`). `kind` is a
`CursorKind` enum whose `stem()` names the `Interface\Cursor\<Name>.blp` file, and `unable`
selects the grayed `Unable<Name>` twin (`cursor_mode.rs:42-137`).

**Unit branch** (`resolve_unit`, `cursor_mode.rs:408-456`):
- **Dead + `UNIT_DYNFLAG_LOOTABLE`** → `Pickup` (the loot base mode `8`); **dead +
  `UNIT_FLAG_SKINNABLE` (0x04000000)** → `Skin`; plain corpse → Point (`cursor_mode.rs:419-432`).
- **Alive, interactable NPC** (not a player, reaction rank ≥ 3 = not attack-worthy) → the
  `UNIT_NPC_FLAGS` **service ladder**, lowest bit wins (`service_cursor`,
  `cursor_mode.rs:328-356`): gossip/questgiver→`Speak`, vendor→`Pickup` (the pouch),
  flightmaster→`Taxi`, trainer→`Trainer`, innkeeper→`Interact`, banker/auctioneer→`Buy`, …
  REPAIR (0x4000) is deliberately never consulted. The questgiver bit additionally gates on the
  cached `SMSG_QUESTGIVER_STATUS` (`questgiver_has_quest`, `cursor_mode.rs:316-319`).
- **Alive, attackable** (non-player rank ≤ 3, or a player rank ≤ 1) → `Attack`
  (`cursor_mode.rs:452-454`).
- Friendly non-service unit / friendly player → Point.

**Reaction** comes from `ring_reaction` (§4/§8), so the cursor and the ring always agree.

**`unable` (grayed) gates, byte-verified** (`cursor_mode.rs:163-176`):
- Service NPC: gray beyond **5.5556 yd** (`SERVICE_RANGE_SQ = 30.864`, `cursor_mode.rs:167,448`).
- Attack: gray beyond a fixed **10.45 yd** (`ATTACK_RANGE_SQ = 109.2025`,
  `cursor_mode.rs:170,453`). Attack is *only* grayed, never blocked — the server holds the swing
  until you close.
- Loot/Skin: gray outside the melee interact reach `max(reachA + reachB + 1.333, 5.0)`, the 5.0
  a **floor** not a cap, centre-to-centre, boundary-inclusive (`MELEE_OFFSET`/`MELEE_FLOOR`,
  `cursor_mode.rs:175-176,415-417`); `reach` from `UNIT_FIELD_COMBATREACH` (default 1.5).

MSUI has **no OS-cursor-BLP machinery** today (its `ClientWindow` only toggles
Raw/Hidden/Normal). Port options: (a) load the `Interface\Cursor\*.blp` set and swap the OS
cursor, or (b) skip the art and just drive an ImGui reticle tint / a small kind label — the
*classification* logic ports 1:1 regardless of how it is drawn.

---

## 3. Selection: left vs right click, wire format, storage

### Local selection state (client-authoritative)
`Selection { target: Option<Entity>, guid: Option<u64> }` (`mod.rs:71-75`). It is set **the
instant you click**, before any server round-trip — the ring appears immediately; sending
`CMSG_SET_SELECTION` only *informs* the server (`mod.rs:7-10`, `ring.rs:33-34`). MSUI stores the
same as a single `ulong SelectionGuid` (+ optional cached `WorldEntity`), keyed into
`EntityStore`.

### Left-click = select only
`select_on_click` acts on a clean `WorldClick` (a left **drag** orbits the camera instead — the
click-vs-drag split, `mod.rs:20-21`). It selects `Hovered`, fires the NPC greeting on the select
gesture, and commits (`crates/benilla/src/target/click.rs:52-107`). Clicking empty
ground/non-unit **clears** the target (`click.rs:101-106`). Skipped while the inspector is armed.

### Right-click = select **and** context action
`act_on_right_click` (`click.rs:133-371`) first commits the selection, then branches by the same
cursor classification: **Attack** (auto-draw + `CMSG_ATTACKSWING`), **Loot** (dead + lootable →
`CMSG_LOOT`), **Skin**, or **Interact** on an in-range friendly service NPC (vendor →
`CMSG_LIST_INVENTORY`, flightmaster → taxi, everything else → universal `CMSG_GOSSIP_HELLO`,
`interact_command` at `click.rs:594-627`). A right-click on empty ground is just a camera turn —
it never deselects (`click.rs:131`).

### The one commit law (`scan::commit`, `scan.rs:456-484`)
Every selection writer funnels through it: **dedup** (bail if guid unchanged); on an engaged
switch (you were auto-attacking and had an old target) the byte-exact **stop → select →
re-swing** sequence — `AttackStop`, then `SetSelection{guid}`, then `AttackSwing{guid}` (unless
the new target is yourself or unattackable). A plain select sends only `SetSelection`. `clear`
sends `SetSelection{guid:0}` (+ `AttackStop` if engaged), no-op when nothing is selected
(`click.rs:708-716`).

### CMSG_SET_SELECTION wire format
`crates/benilla-protocol/src/world/writer/selection.rs:20-22`: opcode `CMSG_SET_SELECTION`,
body = a **raw full 8-byte GUID** (`messages::full_guid`), little-endian u64; `guid == 0`
clears. **MSUI already implements this exactly**: `WorldSession.SetSelection(ulong) =>
SendFullGuid(Op.CMSG_SET_SELECTION, guid)` (`MSUIClient/MSUIClient/Net/WorldSession.cs:201`,
`233-238`) — nothing calls it yet.

### How UNIT_TARGET relates (server's view ≠ your selection)
`CMSG_SET_SELECTION` makes the **server** record your pick in **your** `UNIT_FIELD_TARGET` and
relay it to observers (`selection.rs:16-19`). That is a *different* thing from your local
`Selection`: `UNIT_FIELD_TARGET` on any streamed unit is **who that unit is attacking/looking
at**, read via `unit_target()`
(`crates/benilla-protocol/src/messages/update_object/fields/unit.rs:21-25` — `get_guid` filtered
non-zero). benilla uses a unit's own `UNIT_TARGET` for the "is this mob fighting *me*" test:
`store.0.unit_target() == me && unit_flags & (1<<19) != 0` (`scan.rs:202-204`). So for MSUI:
your target is the local `SelectionGuid`; a mob's target is `entity.Fields.Target`.

### UNIT_TARGET field index (confirmed)
**`UNIT_FIELD_TARGET = 16 (0x10)`, a 2-dword GUID (slots 16 & 17).** The numeric constant lives
in a `mod.rs` not included in this staged subset, but it is the canonical build-5875 layout and
MSUI already has it right: `ObjectFields.UNIT_TARGET = 16` with a `Target` GUID accessor
(`MSUIClient/MSUIClient/Net/ObjectFields.cs:21,98`), and every neighbouring index MSUI carries
(HEALTH=22, MAXHEALTH=28, LEVEL=34, FACTIONTEMPLATE=35, FLAGS=46, DYNAMIC_FLAGS=143,
DISPLAYID=131, NPC_FLAGS=147; `ObjectFields.cs:16-31`) matches the vanilla `UpdateFields_1_12_1`
table that puts TARGET at 16. Related indices this section needs, same layout:
`OBJECT_FIELD_SCALE_X = 4`, `UNIT_FIELD_BOUNDINGRADIUS = 129`, `UNIT_FIELD_COMBATREACH = 130`.
(BOUNDINGRADIUS/COMBATREACH are not yet in MSUI's `ObjectFields` and would need adding for the
melee-reach gate.)

### Other (non-mouse) selection writers, for parity
TAB / Shift-TAB nearest-enemy (`scan::tab_target`, `scan.rs:491-588`; 41-yd range, on-screen
frustum tiering, weighted score, history cycling), auto-acquire on attack-with-no-target
(`acquire_and_attack`, `scan.rs:602-644`), and self-defense auto-target when an attacker's swing
lands on you with no current target (`auto_acquire_attacker`, `scan.rs:649-676`). All optional
for a first MSUI pass; TAB is the highest-value follow-up.

---

## 4. Selection RING + highlight + flash

### The ring radius formula (the headline number)
```
ring_world_radius = OBJECT_FIELD_SCALE_X × sqrt( 0.5 · sqrt(dx² + dy²) )
```
where `dx, dy` are the **horizontal (X,Y) extents of the unit's Stand-animation bounding box**
(the M2 `M2Sequence` CAaBox; Stand = animation id 0 via `animationLookup[0]`). The model-local
part `sqrt(0.5·sqrt(dx²+dy²))` is `M2Bounds::ring_footprint`; the world radius is it ×
`OBJECT_FIELD_SCALE_X`. This is **byte-traced + pixel-verified** in
`crates/benilla-formats/tests/selection_ring_radius.rs:1-58` — reference apitrace radii at
scale 1.0: **Chicken 0.572, HumanFemale 0.731, HumanMale 0.841, Horse 1.295**
(`selection_ring_radius.rs:36-41`). It is explicitly **not** the render bounding sphere
(that oversizes tall humans / undersizes squat birds; the nested-sqrt footprint compresses range
— `selection_ring_radius.rs:12-16,52-56`).

In `update_ring` the model-local radius is stamped at attach as the `SelectionRadius(f32)`
component (`mod.rs:136-143`) and applied as `radius = local * (unit.scale.x * mount_scale)`, with
a `local.max(0.05)` floor and a `(scale).max(0.01)` guard (`ring.rs:399-400`). **Model-less (cube)
units** fall back to `RING_FALLBACK_RADIUS = 0.7` (`ring.rs:65,397`). When mounted, the footprint
and extra scale column come from the mount model (`ring.rs:394-398`).

For MSUI: parse the Stand-sequence (`animationLookup[0]`) CAaBox from the M2 (MSUI already reads
M2s in `CreatureRenderer.LoadModel`), compute `ring_footprint`, multiply by `entity.Scale` (and
the model's DBC scale, which MSUI already applies as `model.DbcScale`,
`CreatureRenderer.cs:177`). If Stand bounds are inconvenient at first, ship the 0.7 fallback for
everything and refine — the ring is forgiving.

### Colour by reaction (the ring's own selector, shared with names)
`ring_variant(rank, is_player, is_dead, pvp, in_party)` (`ring.rs:189-220`) picks a variant; the
palette is **byte-verified `linear_rgb`** (`ring.rs:91-98`):

| Variant     | RGB (linear)            | Hex        | When |
|-------------|-------------------------|------------|------|
| Hostile     | (1, 0, 0)               | 0xFFFF0000 | NPC rank 0–1; hostile player |
| Unfriendly  | (1, 0.502, 0)           | 0xFFFF8000 | NPC rank 2 |
| Neutral     | (1, 1, 0)               | 0xFFFFFF00 | NPC rank 3 (also the no-data fallback) |
| Friendly    | (0, 1, 0)               | 0xFF00FF00 | NPC rank 4–7; PvP-flagged friendly player |
| Player      | (0.376, 0.376, 1)       | 0xFF6060FF | friendly player, unflagged (soft blue) |
| Dead        | (0.498, 0.498, 0.498)   | 0xFF7F7F7F | dead NPC (players skip the health check) |
| Party       | (0.667, 0.667, 1)       | 0xFFAAAAFF | party member (pale blue) |
| PartyPvp    | (0.667, 1, 0.667)       | 0xFFAAFFAA | party member, PvP (pale green) |

Rank is `ring_reaction` (§8); NPCs check dead→gray then rank; players branch first and never
gray (`ring.rs:196-219`). The **combat flash** (below) is a first-priority override.

### How the ring is drawn on the ground
benilla projects a **decal**: it clips the actual terrain + WMO surface triangles inside a box
under the unit and textures them top-down, so the ring is pixel-coplanar with the ground and
drapes over steps/ledges (`ring.rs:1-16,523-566`). The box is the (camera-yawed) texture square
half-extent `s = radius` horizontally, with a **vertical half-range `2s`** (box corners
`center±s` horizontal, `center±2s` vertical — byte-verified `0x608e00`, `ring.rs:528-529,546-557`),
plus a vertical trapezoid alpha fade. Texture: **`Textures\UnitSelectTexture.blp`** (a white ring),
loaded as `mpq://textures/unitselecttexture.blp` (`ring.rs:62-63`). Blend: **unlit + additive**
(`GL_SRC_ALPHA / GL_ONE`, `AlphaMode::Add`, `cull_mode: None`, `ring.rs:254-266`) — it glows and
never darkens the ground. A `depth_bias` of **8192** (`RING_DEPTH_BIAS`, `ring.rs:74`) keeps the
coplanar decal from z-fighting (the fixed-function twin of polygon offset). The bright arc always
faces the camera (`ring_fade_angle`, camera-relative, `ring.rs:504-521`). The ring is **steady —
no pulse** (`ring.rs:14-15,313`); a missing ground surface hides it (`ring.rs:480-482`).

For MSUI (no terrain-clip decal system): draw a **horizontal textured quad** of half-size =
world radius, centred at the unit's feet (`e.Position`), reusing `CreatureRenderer`'s exact
camera-relative transform pattern (`Scale * Basis * Translate(pos)`, subtract `camPos`, upload
with `RelativeViewProjection` — `CreatureRenderer.cs:179-184`). Bind `UnitSelectTexture.blp`,
additive blend (`BlendFunc(SrcAlpha, One)`, `DepthMask(false)`), depth-test `LEQUAL` (already
set, `ClientWindow.cs:411`) with a small polygon-offset/bias to avoid z-fighting the ground,
tint by the reaction colour. This is a small addition to MSUI's existing GL draw path.

### Highlight (`highlight.rs`) — the mouseover/target model brighten
The real client pushes a per-model emissive lift on hover/target (`SetHighlight` writes RGB
`0xff404040` ⇒ **+64/255 per channel**, added to material emissive). benilla carries it as **bit
31 of the per-instance `MeshTag`**; `wow_model.wgsl` adds the lift when set. `apply_highlight`
(PostUpdate) ORs the bit onto every part of the **hovered + selected** roots and clears it on
roots that left the set — hover and selection **stack** (`highlight.rs:1-80`). MSUI's creature
shader has no emissive term (`CreatureRenderer.cs:526-544`); a pragmatic port is a subtle
additive tint uniform on the hovered/selected model, or skip it and rely on the ground ring +
target frame. **Hover-highlight vs hard-selection**: hover is a per-frame recompute of `Hovered`
lighting only the model; hard selection is the persistent `Selection` that additionally draws the
ground ring, feeds the target unit frame, and drives the combat flash.

### Combat flash (`flash.rs`)
While *you are auto-attacking your current target* and it is legally attackable, its ring **and**
overhead name pulse **red ↔ orange** — a 1 Hz triangle wave on the green byte only
(`G = trunc(128·frac)`, red `0xFFFF0000` ↔ orange `0xFFFF8000`), recomputed every frame
(`flash.rs:1-126`). It is the ring/name selector's **first-priority branch** and overrides the
reaction palette (`ring.rs:466-476`, `nameplates.rs:460-470`). Gates: current target + engaged
(server-echoed auto-attack) + `can_attack` (`flash.rs:88-107`). Nice-to-have for MSUI; needs an
"am I auto-attacking" bracket first.

---

## 5. Nameplates (`nameplates.rs`)

Note benilla has **two** systems: the V-key nameplate (`crate::vplates`, screen-space HP bars,
mutually exclusive with names) and this **overhead-name** system (`nameplates.rs`). This section
covers the overhead name, which is the closer analogue to what MSUI would draw as world-projected
bars.

- **When shown** (`ShouldShowName` gate, `nameplates.rs:378-419`): your current **TARGET always
  shows** (bypasses cvars — the selection global, `nameplates.rs:406`); own unit by `SHOW_OWN_NAME`,
  players by `SHOW_PLAYER_NAMES`, NPCs by `SHOW_NPC_NAMES` (benilla defaults **all ON**,
  `nameplates.rs:90-92`). A **dead creature** shows only via the target rescue
  (`nameplates.rs:407-408`). Suppressed by: a live V-plate on the unit, a chat bubble, or a ghost
  visual (`nameplates.rs:388-395`). There is **no distance cutoff** on the floating name (it is
  cvar on/off, not range-gated — the V-plate system is the range-gated one).
- **Content** (`nameplates.rs:434-444`): NPC = `name` + `<Subname>` (empty subname → no second
  line); player = `[<AFK>/<DND>/<GM> prefixes]name` (`flag_prefix`, `nameplates.rs:105-115`).
- **Colour**: the **same selector as the ring** — `ring_variant(rank, is_player, is_dead, pvp,
  in_party)` painted directly, plus the combat-flash override (`nameplates.rs:445-470`). One law,
  not a copy (decision 0659).
- **World→screen / geometry**: it is a real **world billboard mesh**, camera-facing, drawn in the
  world 3-D pass **depth-tested so walls occlude names** (`nameplates.rs:1-12`). Anchor = the
  posed `PlayerName` attachment, re-read every frame (`nameplates.rs:475-482,562`). **Scale by
  unit height**: `d = anchor.z − feet.z`; `scale = d > 4 ? (d/4)·1.5·0.2 : 0.2` (`height_scale`,
  `nameplates.rs:71-125`) — taller model, bigger name; humanoids sit at the 0.2 floor.
- **Stacking/anchoring**: the name block's **top line baseline** sits at `anchor.z +
  lineCount·scale` and the block **hangs down** from there (`nameplates.rs:31-39,206-268`).
  Placement runs in PostUpdate after transform propagation so a moving unit's name doesn't lag
  (`place_nameplates`, `nameplates.rs:533-599`); transparent-sort bias `NAMEPLATE_DEPTH_BIAS =
  4.0e4` draws names after ordinary transparents (`nameplates.rs:84,358`).

For MSUI (ImGui HUD): the simplest faithful port is **world-project the head position and draw in
ImGui**. Per visible unit, project `e.Position + (0,0,headHeight)` through `Camera.ViewProjection`
(absolute) to clip → NDC → screen pixels; if `w > 0` and on-screen, draw an ImGui foreground
draw-list text + optional HP bar there, coloured by reaction. Cull behind-camera (`clip.w <= 0`)
and, unlike benilla, **do add a distance cap** (e.g. 60–100 yd) for HUD clutter/perf. A GL
billboard (like the ring quad) is the higher-fidelity alternative if you want wall occlusion.

---

## 6. What MSUI has today (precisely)

- **Wire ready, unused.** `WorldSession.SetSelection(ulong)`, `AttackSwing(ulong)`,
  `AttackStop()`, `CreatureQuery(entry, guid)`, `NameQuery(guid)` all exist and are correct
  (`WorldSession.cs:201-203,220-229`). **Nothing calls `SetSelection`** — there is no selection
  producer.
- **Descriptor ready, unused for targeting.** `ObjectFields.Target` (UNIT_TARGET=16, GUID),
  `FactionTemplate` (35), `UnitFlags` (46), `NpcFlags` (147), `DynamicFlags` (143), `DisplayId`
  (131), `Health/MaxHealth/Level`, `IsDead`, `HealthFraction`, `Scale` are all decoded and merged
  correctly (`ObjectFields.cs:88-109`, `Entities.cs:29-34`). No `Selection`, no `Hovered`.
- **World model ready.** `EntityStore` is game-thread-owned, keyed by GUID, with `Units`,
  `NearestUnits(from, max)`, per-entity `Position` (raw WoW space), `Orientation`, `Scale`,
  `DisplayId` (`Entities.cs:40-138`).
- **Camera ready, no unproject.** `Camera` exposes `View`, `Projection`, `ViewProjection`,
  `RelativeViewProjection`, `Position`, `Forward`, and a clip-space `BoxInFrustum`
  (`Engine/Camera.cs:219-290`) — but **no screen→world ray** helper yet. Native WoW coords
  throughout (X north, Y west, Z up), no conversion layer (`Camera.cs:5-25`).
- **Input ready, no click-vs-drag.** `ClientWindow` polls `MouseLeftDown/RightDown/MiddleDown`,
  exposes `MousePosition`, `FramebufferSize`, `MouseCaptured`, and gates on
  `ImGui.GetIO().WantCaptureMouse` (`ClientWindow.cs:199-212,497-525`). **But both left and right
  MouseDown immediately `BeginLook` (camera capture)** (`ClientWindow.cs:356-360`) — there is **no
  "clean click" concept**; every press is currently a camera drag. The self player is
  `_controller.Position` / `_net.PlayerGuid` (`Program.Net.cs:123,865`).
- **Rendering ready.** `CreatureRenderer.Render(camera, entities)` draws each creature's M2 at
  `e.Position/Orientation/Scale`, camera-relative (`CreatureRenderer.cs:132-228`), called from the
  world pass (`Program.Net.cs:318-322`). **No selection ring, no nameplate, no hover, no cursor
  swap.** `GuidInfo.IsPlayer/IsCreatureOrPet` exist for classification (`Net/GuidInfo.cs:19-22`).

---

## 7. Port plan for MSUI (ImGui-native), ordered

**(a) Screen ray → EntityStore hit test.** Add `Camera.ScreenPointToRay(Vector2 px, Vector2
size)`: NDC `x = 2·px.X/size.X − 1`, `y = 1 − 2·px.Y/size.Y` (flip Y for top-left pixels);
`Matrix4x4.Invert(ViewProjection)`; unproject two clip points `(x,y,−1,1)` and `(x,y,+1,1)` as
**row vectors** (`Vector4.Transform`), divide by w, subtract → dir; origin = `Camera.Position`.
Each frame, if `!MouseCaptured && !ImGui.WantCaptureMouse`, ray-test `EntityStore.Units`
(skip self `_net.PlayerGuid`) with ray-vs-vertical-cylinder at `e.Position`, radius = §4 ring
footprint (fallback 0.7). Nearest hit → `HoveredGuid`; tie-break distance-to-camera; keep a
one-frame sticky cache. (`EntityStore.NearestUnits` is a fine coarse fallback.)

**(b) Hover state + cursor swap.** Store `HoveredGuid`; recompute per frame. Classify a
`CursorKind` from reaction + `NpcFlags` + dead/lootable/skinnable exactly as `cursor_mode.rs`
(port `service_cursor` + the attack/loot/skin gates and the range grays: 5.5556 yd service,
10.45 yd attack, `max(rA+rB+1.333, 5.0)` loot/skin). Drive either an OS-cursor BLP swap or an
ImGui reticle tint / label. Optionally add a hover tint uniform to `CreatureRenderer` (highlight).

**(c) Left-click sets local target + sends selection.** First add a **click-vs-drag arbiter** to
`ClientWindow`: on left MouseDown record the press pixel + time and start a *potential* orbit; if
motion stays under a small threshold (≈4 px) and release is quick and look never engaged, emit a
`WorldClick` (screen pos); otherwise it was an orbit drag. On `WorldClick` (not over UI): set
`SelectionGuid = HoveredGuid` (instant, client-authoritative) and call
`WorldSession.SetSelection(HoveredGuid)`; a click on nothing sets `SelectionGuid = 0` and sends
`SetSelection(0)` only if something was selected. Right-click → same click detection → context
action (attack/interact); a right *drag* stays a camera turn and never deselects. Port
`scan::commit`'s stop→select→re-swing only once auto-attack exists; until then a bare
`SetSelection` is correct.

**(d) Ground selection ring.** Build a unit-quad VAO once; each frame for `SelectionGuid` draw a
horizontal textured quad at `e.Position`, half-size = `ring_footprint × e.Scale × DbcScale`
(fallback 0.7), reusing `CreatureRenderer`'s camera-relative transform (`Scale·Basis·Translate`,
subtract `camPos`, `RelativeViewProjection`). Texture **`Textures\UnitSelectTexture.blp`**;
additive blend (`SrcAlpha, One`), `DepthMask(false)`, depth-test `LEQUAL` + a small polygon
offset; tint from the §4 palette via the ported `ring_variant`. Steady, no pulse. Optionally
draw a second, dimmer ring under `HoveredGuid`.

**(e) Nameplates.** For each visible unit (target always; others up to a distance cap): project
`e.Position + head` through `Camera.ViewProjection`, cull `w<=0`/off-screen, draw ImGui
foreground text + HP bar at the screen pixel, coloured by reaction. HP from
`Fields.HealthFraction`; name from `NameQuery`/`CreatureQuery` cache.

**(f) Target unit frame.** An ImGui panel bound to `EntityStore[SelectionGuid]`: name, level,
`HealthFraction` bar, dead/PvP flags, reaction-tinted name (same palette). This is the direct
data read that replaces benilla-ui's FrameXML `"target"` token (whose `Unit*` globals read a
per-frame snapshot fed from `selection.target`'s store —
`crates/benilla-ui/src/script/unit/mod.rs:32-60`, `crates/benilla/src/ui_unit.rs:585-620`).
**Defer chrome (borders, portrait, aura rows, tooltip) to Foundations.**

---

## 8. Gotchas / empirical notes

- **Reaction/faction is the shared dependency.** Rank comes from `ring_reaction`
  (`ring.rs:600-633`), resolved in the client's own order: **duel leg** (both PvP-flagged, matching
  `PLAYER_DUEL_TEAM`/`PLAYER_DUEL_ARBITER`) → **both-FFA leg** → **reputation-first** (if the
  target's faction has a reputation slot, use *our standing* rank — this is why every Stormwind NPC
  is green to a human even in GM mode) → else the **FactionTemplate.dbc comparator**
  (`FactionTemplate::reaction_toward`, byte-exact `0x606640`). **Direction matters**: it is the
  *target's* reaction toward the local player (`ring.rs:568-571`) — reversing it paints passive
  yellow beasts hostile-red. Missing DBC/fields → **neutral (3)** fallback. **MSUI needs
  FactionTemplate.dbc parsed** (it isn't today) plus `UnitReaction`; defer duel/FFA (needs PvP
  descriptor fields MSUI doesn't decode yet).
- **Rank scale off-by-one.** The ring/name selector uses the **raw rank 0..7** (0–1 red … 4–7
  green); the Lua `UnitReaction` scale is **1..8** (benilla adds +1 when feeding the frame,
  `ui_unit.rs:592-597`). Pick one and be consistent.
- **`can_attack` = not friendly + not disqualified.** Reaction rank ≤ 3 **and** none of
  `UNIT_FLAGS` bits 1/7/16/20/25 set (`FLAG_DISQUALIFIERS`, `scan.rs:65-89`). This is the same
  predicate the Attack cursor, the flash, and TAB share.
- **Interaction distance is per-action, and attack is never blocked.** Attack only *grays* beyond
  10.45 yd (`cursor_mode.rs:453`) — you can still select and swing; the server holds the swing
  until you close. Service NPCs gray beyond 5.556 yd and MSUI should **not send** the interact then
  (no auto-approach). Loot/skin use `max(rA+rB+1.333, 5.0)`, centre-to-centre, boundary-inclusive.
- **Left-click selects; right-click selects *and acts*.** The NPC greeting sound fires on the
  **left-click select**, not the right-click (`click.rs:73-82`). Both require a *clean click*, not
  a drag — MSUI must add the click-vs-drag arbiter (§7c) because today every mouse press is a
  camera drag.
- **Selection is client-authoritative and instant.** The ring appears the moment you click, before
  the server replies (`mod.rs:7-10`). Don't gate the ring/target-frame on a server echo.
  `UNIT_FIELD_TARGET` echo is only for reflecting *other* units' targets to observers.
- **Death and stream-out clear the target client-side.** On a target's alive→dead **edge** the
  client clears the selection and sends `SetSelection(0)` (`ring.rs:439-449`); a destroyed/
  streamed-out target clears too (`ring.rs:489-494`). benilla tracks `(guid, dead)` **per frame**,
  not only at selection change, so a `.respawn`ed creature reusing its guid still triggers the
  second kill's clear (`ring.rs:336,439-443`) — worth replicating.
- **Nameplate ≠ V-plate.** benilla's floating overhead name (this section) has no distance gate;
  its screen-space HP-bar V-plate is a separate system. For an MSUI HUD, add your own distance cap.
- **Picking fidelity.** benilla's pick is silhouette-accurate (posed mesh + halo). MSUI's cylinder
  approximation will diverge slightly on overlapping/oddly-shaped models; the sticky-hover cache and
  nearest-along-ray tie-break (not nearest-to-camera-centre) are what keep it from strobing.

### Cross-section dependencies
- **Foundations**: FactionTemplate.dbc / reputation decode (`ring_reaction`), `UnitReaction`, and
  the unit-frame chrome (portrait, aura rows, tooltips) this section defers.
- **Auras/tooltips** section: the target frame's buff/debuff rows and the mouseover unit tooltip
  are out of scope here.
- **Combat** (future): the stop→select→re-swing commit, the combat flash, and TAB auto-acquire all
  depend on an "am I auto-attacking" (`Engaged`) bracket MSUI doesn't have yet.

### Uncertainties
- The exact `dx/dy` extent convention inside `M2Bounds::ring_footprint` (half-extent vs full width)
  is in a `benilla-formats` source **not in this staged subset** — only its test is. The **formula
  and the four measured radii are pinned** (`selection_ring_radius.rs`), so validate an MSUI
  implementation against Chicken 0.572 / HumanMale 0.841 / Horse 1.295 at scale 1.0 rather than
  trusting an extent guess.
- The numeric `FIELD_UNIT_TARGET` constant (=16) is derived from the canonical 5875 layout +
  MSUI's own `ObjectFields`, not read from a staged benilla `mod.rs` (that file isn't in this
  subset); the accessor semantics (2-dword GUID, non-zero filter) are confirmed at
  `fields/unit.rs:21-25`.

# Part 4 — Combat: attack state, the combat log, floating combat text & feedback

Ground truth = benilla (Rust/Bevy 1.12.1, build 5875). Target = MSUIClient (C#,
Silk.NET/OpenGL, ImGui HUD, no FrameXML). Citations are repo-relative:
`crates/...` = benilla, `MSUIClient/MSUIClient/...` = MSUI. Every non-obvious
claim carries file:line; line numbers are real (from Read).

Note on the staged benilla subset: `benilla-protocol`'s `lib.rs`, `messages/mod.rs`
and `messages/opcode.rs` were **not** staged, so the `ServerPacket` enum, the
`AttackerState` struct body, and the opcode→u16 table are inferred from their
consumers (`net/apply/combat*.rs`) and the protocol integration test
(`crates/benilla-protocol/tests/spells.rs`), which pin the exact wire bytes. Where
a numeric opcode is given without a benilla file:line it is the canonical vanilla
5875 value and is flagged "verify vs opcode.rs".

---

## 1. Attack flow (auto-attack, ATTACKSTART/STOP, in-combat, swing anim)

**Initiation (client → server).** Melee auto-attack is a latch, not a per-swing
send: one `CMSG_ATTACKSWING(u64 victim_guid)` starts it, one `CMSG_ATTACKSTOP`
(empty body) ends it. The server then drives every swing. MSUI already has both
builders: `WorldSession.AttackSwing(ulong guid)` writes the full 8-byte guid,
`WorldSession.AttackStop()` sends an empty body
(`MSUIClient/MSUIClient/Net/WorldSession.cs:202-203`), with opcodes
`CMSG_ATTACKSWING=0x0141`, `CMSG_ATTACKSTOP=0x0142`
(`MSUIClient/MSUIClient/Net/Opcodes.cs:68-69`). They are currently unused (nothing
calls them).

**SMSG_ATTACKSTART / STOP (server → client), the engagement bracket.** benilla's
handlers are tiny and their whole job is a marker component, not damage:

- `attack_start(attacker, victim)` — resolves the attacker guid to its entity and
  inserts an `Engaged` marker, clearing the `RangedHold` weapon-visual hold
  (`crates/benilla/src/net/apply/combat.rs:15-26`). The client's true gate is "the
  auto-attack-target guid is set"; benilla mirrors that as `Engaged` on the
  attacker, **including our own echo** (the server echoes our own start back).
- `attack_stop(attacker, victim)` — removes `Engaged` and writes a `SwingFlush`
  (flush any pending swing text, no sounds) (`combat.rs:29-43`). Death/stun arrive
  as this same packet.

Wire (from the protocol test): `SMSG_ATTACKSTART` = attacker u64 + victim
PackedGUID (`tests/spells.rs:150-159`); `SMSG_ATTACKSTOP` = attacker PackedGUID +
victim PackedGUID + u32 (`tests/spells.rs:160-169`). Opcodes
`SMSG_ATTACKSTART=0x0143`, `SMSG_ATTACKSTOP=0x0144` (already in
`Opcodes.cs:70-71`).

**"In combat" is two different facts.** Do not conflate them:
1. *Auto-attack engagement* (the `Engaged` marker) — set/cleared by
   ATTACKSTART/STOP, per attacker unit. Drives the "weapon-drawn Ready idle" and
   gates actions like sheath toggle (`crates/benilla/src/player.rs:655-667`: a
   `Z`-key sheath toggle is refused while `engaged`).
2. *The player's own combat flag* — `UNIT_FIELD_FLAGS` bit `0x0008_0000`
   (`UNIT_FLAG_IN_COMBAT`), read off the self descriptor and edge-detected to fire
   `PLAYER_REGEN_DISABLED`/`PLAYER_REGEN_ENABLED`
   (`crates/benilla/src/ui_unit.rs:658-673`). This is what drives "Entering/Leaving
   Combat" and regen, **not** the attack packets. MSUI already streams
   `UNIT_FLAGS` (`ObjectFields.cs:96`) so this bit is available today.

**Swing animation timing (decision 0073).** The attacker's swing clip is **not**
started by ATTACKSTART — it starts on each `SMSG_ATTACKERSTATEUPDATE` (one packet
per completed swing). benilla's `attacker_state` writes a `SwingMessage` for the
attacker (`combat.rs:118-130`); the swing clip then plays, and *victim* feedback
(blood, flinch, impact sound, the floating number) is deferred to that clip's
**impact keyframe** via `SwingImpact` — everything except the center combat text,
which fires synchronously at packet parse (`combat.rs:66-71` header;
`crates/benilla/src/combat_text/mod.rs:370-413` melee_impact_text). The
`creature_anim` module that owns `SwingMessage`/`SwingImpact`/`Engaged`/`SwingFlush`
and maps them to M2 clip ids was **not staged**, so the exact swing-clip ids are
inferred from AnimationData.dbc (see §7b) — the victim flinch id **9 CombatWound**
is confirmed in `crates/benilla-formats/src/spell_visual.rs:57-58` ("AnimationData
id 9 = CombatWound … corroborating decision 0107's wound-flinch ids 8-10").

---

## 2. SMSG_ATTACKERSTATEUPDATE (0x14A) — exact wire layout

This is the melee equivalent of the spell combat-log packets: one packet = one
completed swing. The `AttackerState` struct (consumed at
`crates/benilla/src/net/apply/combat.rs:73-142`) carries: `attacker u64`,
`victim u64`, `hit_info u32`, `victim_state u32`, `damage u32`, `absorb u32`,
`resist i32`, `blocked u32`.

Byte layout, pinned by `crates/benilla-protocol/tests/spells.rs:171-208`
(cites vmangos `Unit::SendAttackStateUpdate`, Unit.cpp:4572-4605):

```
u32   HitInfo                      # bitfield, see below (offhand bit 0x4 rides here)
PackGUID attacker                  # NOTE: attacker FIRST, before victim
PackGUID victim
u32   TotalDamage                  # post-mitigation total
u8    SubDamageCount               # usually 1; up to ~7 (one per school)
  repeat SubDamageCount:
    u32  school                    # spell-school index of this sub-block
    f32  damage_float
    u32  damage_int
    u32  absorb                    # SUMMED across all sub-blocks -> AttackerState.absorb
    i32  resist                    # summed across all sub-blocks -> AttackerState.resist
u32   VictimState                  # 0..8, see below
u32   (unused, zero)
u32   spell_id                     # 0 for a plain white swing
u32   blocked                      # blocked amount
```

The test proves the sub-damage sum: two sub-blocks with absorb 5 and 7 yield
`AttackerState.absorb == 12` (`tests/spells.rs:183,188,202-204`), and
`blocked == 15` reads straight from the tail (`tests/spells.rs:193,205`).
`TotalDamage` (42 in the test) is what `damage` reports (`tests/spells.rs:199`);
the per-sub-block `damage_int` is not separately surfaced.

**HitInfo bits benilla actually tests** (vmangos `UnitDefines.h`; consts at
`crates/benilla/src/sound/combat.rs:58-61`):

| bit      | meaning        | where used |
|----------|----------------|------------|
| `0x0004` | OFFHAND swing  | picks off-hand weapon for whoosh/impact (`sound/combat.rs:193,241`) |
| `0x0010` | MISS           | `HITINFO_MISS` (`sound/combat.rs:59`; drives the whiff whoosh) |
| `0x0020` | full ABSORB    | zero-damage → "Absorb" word (`net/apply/combat_log.rs:154-155`, `combat_text/law.rs:169-170`) |
| `0x0040` | full RESIST    | zero-damage → "Resist" word (`combat_log.rs:156-157`, `law.rs:171-172`) |
| `0x0080` | CRITICAL       | `HITINFO_CRITICALHIT`; picks crit category / "CRITICAL" descriptor (`law.rs:166`, `sound/combat.rs:60`, `ui_unit.rs:150`) |
| `0x0800` | BLOCK          | full-block descriptor when state not rewritten (`ui_unit.rs:161`) |
| `0x4000` | GLANCING       | "GLANCING" descriptor (`ui_unit.rs:152-153`) |
| `0x8000` | CRUSHING       | `HITINFO_CRUSHING`; "CRUSHING" descriptor + crushing injury vocal (`sound/combat.rs:61,288`, `ui_unit.rs:154`) |
| `0x0060` | absorb|resist mask | injury-vocal suppression when either set (`sound/combat.rs:284`) |

**VictimState (u32)**, the outcome enum (`combat_log.rs:134-140`, `law.rs:156-176`):
`0 UNAFFECTED · 1 NORMAL(hit) · 2 DODGE · 3 PARRY · 4 INTERRUPT(silent NORMAL
alias) · 5 BLOCK · 6 EVADE · 7 IMMUNE · 8 DEFLECT`.

**Client-side full-block synthesis (decision 0279).** The wire's `blocked` amount
is otherwise invisible: benilla rewrites `victim_state = 5 (BLOCK)` iff *victim
resolvable AND damage == 0 AND blocked != 0*, before any consumer sees the record
(`combat.rs:96-98`). A *partial* block (damage > 0 with blocked > 0) stays state 1
and is indistinguishable from a plain hit at the state level — the blocked amount
only appears as a partial trailer in the center text (§4).

**Everything you need to show a hit** is therefore: attacker guid (who animates the
swing), victim guid (who flinches + where the number floats), `damage`,
`victim_state` (dodge/parry/block/immune words), `hit_info` crit/glancing/crushing
bits, and `absorb`/`resist`/`blocked` for the partial trailer. Melee is always
physical: benilla hardcodes `school = 0` for the melee UNIT_COMBAT feed because the
sub-block school is not carried into `AttackerState` (`ui_unit.rs:190`).

---

## 3. Combat log — which opcodes, their fields, and the UI events

benilla parses seven inbound combat-log packets (all decoded in
`crates/benilla-protocol/src/messages/combat_log.rs`, applied in
`crates/benilla/src/net/apply/combat_log.rs`). All are **inbound only** (no
outbound twin). Opcode numbers below are canonical vanilla 5875 — **verify vs
`benilla-protocol/src/messages/opcode.rs` (not staged)**; MSUI's `Opcodes.cs`
comment says its values were checked against exactly that file.

| packet (SMSG) | ~opcode | struct / wire (combat_log.rs) | fields |
|---|---|---|---|
| SPELLNONMELEEDAMAGELOG | 0x0250 | `SpellDamageLog`, read `:39-64` | target PackGUID, attacker PackGUID, spell_id u32, damage u32, school u8, absorb u32, resist i32, periodic u8(bool), unused u8, blocked u32, hit_info u32, extended u8(drop). **Crit = `hit_info & 0x2`** (`SPELL_HIT_TYPE_CRIT`, `:21`). |
| PERIODICAURALOG | 0x024E | `PeriodicAuraLog`, read `:115-157` | target PackGUID, caster PackGUID, spell_id u32, count u32, then per tick `{auraType u32, payload}`. Payload by aura type (`:104-141`): DAMAGE(3)/DAMAGE_PERCENT(89) → {amount u32, school u32, absorb u32, resist i32}; HEAL(8)/OBS_MOD_HEALTH(20) → {amount u32}; OBS_MOD_MANA(21)/ENERGIZE(24) → {power u32, amount u32}; MANA_LEECH(64) → {power u32, amount u32, mult f32}. Unknown aura type = hard error (can't skip). |
| SPELLHEALLOG | 0x0150 | `SpellHealLog`, read `:173-181` | target PackGUID, healer PackGUID, spell_id u32, amount u32, critical u8(bool). |
| SPELLENERGIZELOG | 0x0151 | `SpellEnergizeLog`, read `:198-206` | target PackGUID, caster PackGUID, spell_id u32, powerType u32 (0 mana/1 rage/2 focus/3 energy/4 happiness), amount u32. |
| SPELLDAMAGESHIELD | 0x024F | `DamageShield`, read `:222-229` | victim **raw u64**, attacker **raw u64**, damage u32, school u32. Reflected (thorns) damage lands on the *attacker*. |
| ENVIRONMENTALDAMAGELOG | 0x01FC | `EnvironmentalDamageLog`, read `:250-260` | victim **raw u64**, damage_type u8 (0 exhausted/1 drowning/2 fall/3 lava/4 slime/5 fire), damage u32, absorb u32, resist i32. |
| SPELLLOGMISS | 0x024B | `SpellLogMiss`, read `:279-299` | spell_id u32, caster **raw u64**, useExtended u8(vmangos always 0), count u32, then `{target raw u64, missInfo u8}[count]` (+2×f32 per entry iff useExtended). |

Note the guid encoding split: the spell-damage/periodic/heal/energize packets use
**PackedGUID**; damage-shield, environmental, and log-miss use **raw u64**
(`:220-221`, `:247-249`, `:275-278`). MSUI's `ObjectFields.GetGuid` reads raw
low/high halves; a PackedGUID reader (mask byte + present bytes) is needed for the
first group — check whether MSUI's `UpdateObject.cs` already has one to reuse.

`SMSG_LOG_XPGAIN` (~0x01D0) also feeds combat text (`xp_gain`,
`combat_log.rs:571-585`): `"XP: %d"` over self, purple row 4.

**How packets become UI events.** Each arm fans out to up to three channels
(`combat_log.rs`):
1. **Floating worldtext** (over the victim/target) via `CombatTextSpawn` — gated:
   only *my* or *my pet's* damage, never over my own head (§4 Gate A).
2. **`UNIT_COMBAT`** portrait hit-indicator via `UnitCombatFeedback` — **ungated**
   (any source, any recipient, self included), fired at packet receive
   (`combat_log.rs:11-15`, `ui_unit.rs:41-64`). This is the "red flash + number on
   the portrait" channel; `CombatFeedback.lua` is the consumer.
3. **Center scrolling text** (`COMBAT_TEXT_UPDATE`) via `CombatTextEvent` —
   **self-recipient only** (`combat_log.rs:218-227`), the Blizzard_CombatText addon
   (§4).

Spell→category mapping helpers worth copying: `spell_center_text` (damage→
`SPELL_DAMAGE`, else `SPELL_ABSORBED`/`SPELL_RESISTED`; **a spell crit does NOT
become a crit type** — `combat_log.rs:98-118`), `miss_center_type`/`miss_action`
(miss code 1-11 → SPELL_* words / UNIT_COMBAT actions, `:482-514`), `spell_feedback`
(damage→WOUND(+CRITICAL), else ABSORB/RESIST descriptor, `:81-96`). Heals and
energize **never float** as worldtext in 5875 — chat/center/portrait only
(`:249-250`, `:424-427`).

---

## 4. Floating / scrolling combat text — the full law (THE highlight)

**There are two distinct systems.** Do not merge them:

- **(A) World-anchored floating numbers over the unit** — the classic "damage
  numbers over the mob". benilla engine transcription in
  `crates/benilla/src/combat_text/{mod,law}.rs`. Numbers are WHITE for melee.
- **(B) Center screen-space scrolling text over your own character** — the 1.11+
  "Floating Combat Text" option, the Blizzard_CombatText addon transcribed in
  `crates/benilla/assets/ui/CombatText.xml`, driven by `COMBAT_TEXT_UPDATE`. Damage
  is RED here. Self-recipient only.

Both are authentic 5875 and both fire off the same packets. MSUI will most likely
build (A) first (it's the "numbers over units" everyone means), but (B)'s constants
and colors are equally pinned below.

### (A) World-anchored floating text (`combat_text/law.rs`, `mod.rs`)

**Anchor & motion** (`mod.rs:1-95`, `:199-243`):
- Anchor = the unit's **overhead** point (PlayerName attach joint / head height;
  fallback `feet + scale × bbox_z × 1.25`), then lifted **z − 1/3**
  (`mod.rs:207-231`). Snapshotted **at spawn** — the text does NOT track a moving
  unit; walking away leaves the numbers behind (`mod.rs:129-131`).
- The category's `rise` is added to **world z before projection** (`mod.rs:271`),
  then re-projected to screen every frame. Block is **horizontally centered with
  its bottom at** the projected point, so it rises above the anchor
  (`mod.rs:316-349`).
- **On-screen size is constant with distance** (no depth counter-scale — that's the
  nameplate path only): `px = round_half_away(scale_value × √(W²+H²))`
  (`law.rs:218-220`). At 1024×768 (diag 1280): normal number **23 px**, crit
  settles **35 px**, crit pop peaks **~70 px** (`law.rs:342-357`). At 1920×1080:
  normal **40 px**.
- Font = **DAMAGE_TEXT_FONT = FRIZQT__.TTF, no outline** (`mod.rs:44-46`,
  `:293-298`). Black drop shadow, offset **{round(0.002·W), round(0.002·H)} px
  down-right** (at 1920×1080 → {4,2}; `law.rs:226-236`, `:363-375`).
- **Hard cap: 4 concurrent texts per unit** — a 5th is dropped outright, no eviction
  (`mod.rs:152-163`, `:443-481`).

**The 6 category rows** (`law.rs:27-82`) — `rise` (world units over full life),
fade-in end (ms), fade-out start (ms), duration (ms), default color (ARGB):

| cat | use | rise | fade_in | fade_out | dur | default color |
|----|-----|------|---------|----------|-----|---------------|
| 0 | normal number | 2.0 | 150 | 760 | **1500** | `0xFFFFFFFF` white |
| 1 | ABSORB word | 2.0 | 150 | **90** | 1500 | white (fade_out<fade_in ⇒ quick flicker, no plateau) |
| 2 | crit number | 0.0 | 150 | 1000 | 1500 | white; scale ramps to crit |
| 3 | miss/dodge/parry/etc word | 2.0 | 150 | 1000 | 1500 | white |
| 4 | XP | 0.0 | 500 | 2000 | **4500** | `0x8094008B` purple |
| 5 | honor | 0.0 | 500 | 2000 | 4500 | `0xFFE0CA0A` gold |

Scale values are bit-exact: `VALUE_NORMAL = 0.018333` (`0x3c962fc9`),
`VALUE_CRIT = 0.0275` (`0x3ce147ad`) (`law.rs:21-22`). Crit "pop" keyframes
(category 2, `law.rs:87-91`): pop to **2×** in the first 10% of life, settle to
**1×** by 20% — so a settled crit is **1.5×** a normal number (`law.rs:435`).

**Fade** (`law.rs:266-286`): alpha REPLACES the color's alpha byte (never
multiplies). Fade-in ramps text `255·t`, shadow `127·t` with `t = elapsed/duration`
(the fade-in boundary is a *step*/pop-in, not a ramp arrival). Plateau =
unconditional `(0xFF, 0x7F)`. Fade-out: text `255−clamp(255·u)`; shadow is
min-capped to the text alpha so it never black-ghosts.

**The COLOR law (B/K), `damage_color`** (`law.rs:110-146`, `mod.rs:17-31`) — this is
the "melee vs spell differ" law:
- `K` = source ownership (`classify_source`, `combat_log.rs:43-57`): **Player** (me),
  **Pet** (a unit whose `UNIT_SUMMONEDBY`/`UNIT_CREATEDBY` == me), or **None = every
  other source, SUPPRESSED entirely** (another unit's fight floats nothing — the
  emitter returns before submitting; it is never "white").
- `B` = melee-styled bit (`melee` = spell record NULL, or `AttributesEx3 & 0x8000`
  `SPELL_ATTR3_NORMAL_RANGED_ATTACK` — a Throw/Auto Shot floats white,
  `melee_styled` at `combat_log.rs:69-75`, `mod.rs:27-29`).

| source | melee (B) | spell (¬B) |
|--------|-----------|------------|
| Player (me) | **WHITE** (row default) | **GOLD `0xFFFFDE00`** |
| Pet (owned) | **ORANGE `0xFFFF8400`** | **GOLD `0xFFFFDE00`** |
| other | — suppressed — | — suppressed — |

Constants: `COLOR_SPELL_GOLD = 0xFFFFDE00`, `COLOR_PET_MELEE_ORANGE = 0xFFFF8400`
(`law.rs:112-113`). **Crit does NOT recolor** — it only selects the pop row. **There
is NO school coloring** in the worldtext path (`mod.rs:24-26`).

**Gate A** (self-suppression, `combat_log.rs:30-37`, `mod.rs:376-413`): outgoing
damage floats over the **victim**; incoming damage **never** floats over your own
head. The XP emitter (cat 4) is the one exception — self-anchored by design,
skips Gate A (`combat_log.rs:569-585`).

**Number vs word split** (`melee_text`, `law.rs:155-179`; `spell_text`, `:185-201`):
- A word state (2/3/5/6/7/8) floats its localized WORD unconditionally, damage
  ignored → category 3 (ABSORB word → category 1).
- Else landed damage floats the bare post-mitigation number → category 0, or **2 if
  `hit_info & 0x80` (melee crit)** / `hit_info & 0x2` (spell crit). A partial
  block/absorb is **not** annotated in the worldtext number (unlike the center text).
- Zero damage → `hit_info & 0x20` "Absorb", `& 0x40` "Resist", else "Miss" (the fn
  never tests `HITINFO_MISS 0x10`; a vs-0 miss falls through to this default).

Words table (indexed by outcome code 1-11, shipped enUS GlobalStrings,
`law.rs:97-107`): `Miss, Resist, Dodge, Parry, Block, Evade, Immune, Immune,
Deflect, Absorb, Reflect`.

**Melee number is DEFERRED** to the swing impact keyframe (`melee_impact_text` reads
`SwingImpact`, `mod.rs:376-413`); spell numbers spawn at packet receive
(`combat_log.rs:236-246`).

### (B) Center scrolling text (`assets/ui/CombatText.xml`)

Screen-space FontStrings scrolling up from UIParent-bottom +384 (≈ over the
character). Constants (`CombatText.xml:27-41`):
```
NUM_COMBAT_TEXT_LINES     = 20     COMBAT_TEXT_SCROLLSPEED   = 1.9   (life, s)
COMBAT_TEXT_FADEOUT_TIME  = 1.3    COMBAT_TEXT_HEIGHT        = 25    (px, 768-space)
COMBAT_TEXT_CRIT_MAXHEIGHT= 60     COMBAT_TEXT_CRIT_MINHEIGHT= 30
COMBAT_TEXT_CRIT_SCALE_TIME=0.05   COMBAT_TEXT_CRIT_SHRINKTIME=0.2
COMBAT_TEXT_STAGGER_RANGE = 20     COMBAT_TEXT_SPACING       = 10
COMBAT_TEXT_MAX_OFFSET    = 130    LOW_HEALTH/LOW_MANA_THRESH= 0.2
```
Scroll paths (`:488-527`): mode 1 (default) start Y 384 → end Y **609** (up); mode 2
384 → **159** (down); mode 0 = `FountainScroll`, a 150-radius arc
(`:522-527`). Coordinate space is the 768-virtual system verbatim (`:483-486`). Life
= 1.9 s; fade begins at 1.3 s: `alpha = 1 − (t−1.3)/(1.9−1.3)` (`:321-323`). Crit:
seed 30, grow toward 60 within 0.05 s, shrink back toward 30 by 0.2 s, and **parks**
(endY=startY, never scrolls away — `:326-334`, `:384-387`).

Message formats (`:228-301`): DAMAGE/DAMAGE_CRIT/SPELL_DAMAGE → `"-N"`;
HEAL/HEAL_CRIT/PERIODIC_HEAL → `"+N"`; MANA → `"+N Mana"`, RAGE/FOCUS/ENERGY
likewise; BLOCK/ABSORB/RESIST append `" (N blocked/absorbed/resisted)"` trailers.

**Center color scheme** (`COMBAT_TEXT_TYPE_INFO`, `:98-150`) — RGB floats, plus the
`show`/`var` gate (only `show=1` types draw by default):

| type | r,g,b | default shown? |
|------|-------|----------------|
| DAMAGE, DAMAGE_CRIT | 1, 0.1, 0.1 (red) | **yes** |
| SPELL_DAMAGE | 0.79, 0.3, 0.85 (purple) | **yes** |
| HEAL, HEAL_CRIT, PERIODIC_HEAL | 0.1, 1, 0.1 (green) | **yes** |
| SPELL_CAST, SPLIT_DAMAGE | green / white | **yes** |
| MANA/RAGE/FOCUS/ENERGY | 0.1, 0.1, 1 (blue) | no (`SHOW_MANA`=0) |
| SPELL_ABSORBED, SPELL_RESISTED | 0.79, 0.3, 0.85 (purple) | no (`SHOW_RESISTANCES`=0) |
| MISS/DODGE/PARRY/BLOCK/ABSORB/RESIST/EVADE/IMMUNE/DEFLECT | 1, 0.1, 0.1 (red) | no (`SHOW_DODGE_PARRY_MISS`/`SHOW_RESISTANCES`=0) |
| SPELL_* miss words | 1,1,1 (white) | no |
| HONOR_GAINED | 0.1,0.1,1 (blue) | yes (`SHOW_HONOR_GAINED`=1) |
| ENTERING/LEAVING_COMBAT, HEALTH_LOW, MANA_LOW | 1, 0.1, 0.1 (red) | gated by `SHOW_COMBAT_STATE`/`SHOW_LOW_HEALTH_MANA` |

Master gate `SHOW_COMBAT_TEXT`: benilla defaults it **"1"** (ref ships "0") —
"the director's ask IS the enabled experience" (`:11-12`, `:46`). Note the deliberate
divergence between systems: worldtext damage is **white** for your melee, center
damage is **red** — both authentic. The center-text tests
(`crates/benilla/src/ui_script/combat_text_tests.rs`) assert: "-17" at height 25
scrolls up and expires at 1.9 s (`:39-114`), a crit seeds 30 → peaks ~60 uncapped
and parks (`:119-150`, `:237-261`), low-health latches once and re-arms
(`:169-231`).

---

## 5. Health / power sourcing (no health packet in 1.12)

**1.12 has no `SMSG_HEALTH_UPDATE`.** Current HP/power live entirely in UNIT
descriptor fields and stay current via `SMSG_UPDATE_OBJECT` **Values** deltas.
benilla reads them straight off `ObjectFields`
(`crates/benilla-protocol/src/messages/update_object/fields/unit.rs`):
`UNIT_FIELD_HEALTH` (idx 22, `:44-47`), `UNIT_FIELD_MAXHEALTH` (28, `:48-51`),
`UNIT_FIELD_POWER1..5` (`unit_power(ty)`, `:157-161`), `UNIT_FIELD_MAXPOWER1..5`
(`:162-165`), power type from `UNIT_FIELD_BYTES_0` byte 3 (`:108-113`).
`unit_is_dead` = `maxhealth > 0 && health == 0` (`:52-59`); the `maxhealth>0` guard
avoids reading an un-streamed unit as dead, and an already-dead corpse streams
`MAXHEALTH` but **no** `HEALTH` (create masks in only non-zero fields → absent reads
0).

**Keeping it current** is a merge, not a packet: benilla applies Values deltas onto
the stored descriptor. MSUI already does exactly this — `ObjectFields.Merge` overlays
a delta's present fields (`MSUIClient/MSUIClient/Net/ObjectFields.cs:64-68`), and
`EntityStore.Apply` merges Values updates onto the existing entity
(`MSUIClient/MSUIClient/Net/Entities.cs:76-82`). So after a swing the server sends an
`SMSG_UPDATE_OBJECT` Values block with the new `UNIT_HEALTH`; the entity's HP is
current on the next PumpNet drain. **The floating number is cosmetic** — it comes
from the combat-log/attacker-state packet, *not* from the HP delta; the two arrive
independently and you must not derive damage by differencing HP (see §8).

**Crit vs normal** never comes from HP — it is a packet flag: melee `hit_info &
0x80`, spell `hit_info & 0x2`, heal `SpellHealLog.critical`.

**The player's own resources** are the same fields on the self descriptor. benilla's
`snapshot` reads `unit_health/max_health/unit_power(power_type)/unit_max_power`
(`crates/benilla/src/ui_unit.rs:318-360`) and fires per-field transition events
`UNIT_HEALTH`, `UNIT_MAXHEALTH`, `UNIT_POWER_UPDATE`, `UNIT_MAXPOWER`,
`UNIT_DISPLAYPOWER` (`:503-520`). Power type (0 mana/1 rage/2 focus/3 energy/4
happiness) selects which POWER slot is "the" resource (`unit_power_type`,
`fields/unit.rs:108-113`). MSUI reads `Bytes0.PowerType` already
(`ObjectFields.cs:101-104`) but has **no** `UNIT_POWER1..5`/`MAXPOWER1..5` accessors
yet — those indices (**POWER1..5 = 23-27, MAXPOWER1..5 = 29-33**, byte-verified vs
benilla `fields/mod.rs:50,52`; see Appendix B) must be added for player resource bars.

---

## 6. What MSUI has today (combat)

- **Outbound**: `WorldSession.AttackSwing(guid)` / `AttackStop()` / `SetSelection(guid)`
  exist and are wired through `NetworkClient` (`WorldSession.cs:201-203`,
  `NetworkClient.cs:171`). **Currently unused** — nothing calls AttackSwing/Stop, and
  there is no stored "current target" guid on the game thread (only the char-select
  `_selectedChar`).
- **Opcodes present**: `CMSG_ATTACKSWING 0x0141`, `CMSG_ATTACKSTOP 0x0142`,
  `SMSG_ATTACKSTART 0x0143`, `SMSG_ATTACKSTOP 0x0144`, `SMSG_ATTACKERSTATEUPDATE
  0x014A`, `SMSG_AI_REACTION 0x013C`, `CMSG_SET_SELECTION 0x013D`, plus spell
  `SMSG_SPELL_START/GO` (`Opcodes.cs:66-77`). **Missing**: every combat-log opcode
  (SPELLNONMELEEDAMAGELOG, PERIODICAURALOG, SPELLHEALLOG, SPELLENERGIZELOG,
  SPELLDAMAGESHIELD, ENVIRONMENTALDAMAGELOG, SPELLLOGMISS, LOG_XPGAIN).
- **Inbound dispatch**: `PumpNet` drains `NetworkClient` and switches on opcode, but
  only handles UPDATE_OBJECT / COMPRESSED_UPDATE_OBJECT / DESTROY_OBJECT /
  MONSTER_MOVE (`MSUIClient/MSUIClient/Program.Net.cs:147-178`). **0x14A and the
  combat-log opcodes fall through (no-op).**
- **Health/power**: `EntityStore` holds health/maxhealth via `ObjectFields`
  (UNIT_HEALTH=22, UNIT_MAXHEALTH=28), with `HealthFraction` and `IsDead` helpers
  (`ObjectFields.cs:92-93,106-109`; `Entities.cs:31-32`). Deltas merge correctly.
  **No power fields, no attack-power/damage/attack-time fields.**
- **Rendering/anim**: `CreatureRenderer.SelectClip` picks Stand(0)/Walk(4)/Run(5)
  from spline speed via `M2Animator.FindFirst` (`CreatureRenderer.cs:230-247`).
  `M2Animator` bakes arbitrary AnimationData.dbc ids (`Build`, `Find`, `FindFirst`,
  `Evaluate`; `M2Animator.cs:279,467,470,578`) and knows one-shot vs looping
  (`OneShotAnimations = {37,39}`, `:127`). **Death (id 1) is bakeable but never
  selected; no swing/attack/flinch clip is ever driven.**
- **No combat UI at all**: no floating/center combat text, no combat-log window, no
  over-unit health bars, no player resource bars, no UNIT_COMBAT portrait flash.
- **Projection available**: `camera.RelativeViewProjection` is the world→screen
  matrix already used by the renderers (`CreatureRenderer.cs:144`) — reuse it for
  over-unit anchors (defer to the Foundations world→screen helper).

---

## 7. Port plan for MSUI (ImGui-native), ordered

**(a) Parse the packets into a `CombatEvent` queue in PumpNet.**
Add the missing opcodes to `Op` (values from `opcode.rs`), then add cases to the
`PumpNet` switch (`Program.Net.cs:152`). Parse each into a small struct mirroring
`combat_log.rs`; the exact byte layouts are in §2/§3. Watch the guid encoding split
(PackedGUID for spell-damage/periodic/heal/energize; raw u64 for damage-shield/
environmental/log-miss) — you need a PackedGUID reader (reuse the one in
`UpdateObject.cs` if present). For 0x14A, remember the **sub-damage loop** (sum
absorb & resist across sub-blocks) and the **full-block synthesis** (damage==0 &&
blocked!=0 && victim known ⇒ victim_state:=5) *before* any consumer reads it
(`combat.rs:96-98`). Push results onto a `Queue<CombatEvent>` (attacker guid, victim
guid, amount, school, victim_state, hit_info-derived flags: crit/glancing/crushing/
block/absorb/resist/miss) drained by the HUD each frame. Classify **source** for
color: is attacker == my guid, or a unit whose `UNIT_SUMMONEDBY`/`CREATEDBY` == my
guid (pet) — else suppress the floating number (but still allow health-bar / log).

**(b) Drive M2 clips on attacker & victim.**
Give `WorldEntity` a transient "action clip" overlay `{ animId, startTime, oneShot }`
that `SelectClip` consults before the locomotion pick
(`CreatureRenderer.cs:230-247`): if an action clip is active and unfinished, play it
(non-looping), else fall back to Stand/Walk/Run. On `SMSG_ATTACKERSTATEUPDATE`, set
the **attacker's** swing clip; on the victim, set the flinch. AnimationData.dbc ids
(the scheme matches MSUI's existing table `M2Animator.cs:889-907` — 0 Stand, 4 Walk,
5 Run, 37-42 jump/fall/swim, 69 dance verify the mapping):
- Swing (pick by wielded weapon like `sound/combat.rs:76-115`; 2H subclasses
  {1,5,6,8,10,17}): **AttackUnarmed 16 · Attack1H 17 · Attack2H 18 · Attack2HL 19**.
- Combat-ready idle (while `Engaged`): **ReadyUnarmed 25 · Ready1H 26 · Ready2H 27 ·
  Ready2HL 28**.
- Victim flinch: **CombatWound 9** (confirmed benilla
  `spell_visual.rs:57-58`), **CombatCritical 10** on crit, **StandWound 8**.
- Death: **Death 1** (then hold **Dead 6**), **Rise 7** on res. Defensive:
  Dodge 30, Parry1H 21, ShieldBlock 24. (These ids are from AnimationData.dbc —
  benilla's `creature_anim` map was not staged, so **verify each against the dbc /
  benilla once available**; only 9 and the locomotion ids are file-confirmed here.)
Sync note: benilla starts the swing clip on the packet and defers the victim
number/blood/impact-sound to the clip's **impact keyframe** (`combat.rs:66-71`).
Without keyframe tags, approximate: play the swing, and pop the floating number ~40-50%
into the swing clip (see §8).

**(c) Floating combat text system (ImGui, system A).**
Each frame: drain the `CombatEvent` queue into a `List<FloatingText>`; for each,
snapshot the victim's overhead world point (unit position + a head-height offset,
benilla lifts **z − 1/3**, `mod.rs:207-231`), project with
`camera.RelativeViewProjection` (world→screen; defer to Foundations helper), and add
`rise` to world-Z *before* projecting (`mod.rs:271`). Render with ImGui
`AddText`/foreground draw list at the projected point, **h-centered, bottom at the
point**. Authentic numbers: duration **1.5 s** (cat 0/1/2/3), fade-in 150 ms, fade-out
start 760 ms (cat 0); size `round(scale × screen_diagonal)` with `scale=0.018333`
normal / `0.0275` crit (≈40 px normal at 1080p), crit pop 2× in first 10% settle to
1.5× by 20% (`law.rs`); black drop shadow at {0.002·W, 0.002·H}; **max 4 per unit,
hard drop**. Colors (B/K law): my melee **white `0xFFFFFFFF`**, my spell **gold
`0xFFFFDE00`**, pet melee **orange `0xFFFF8400`**; crit does not recolor; **no school
color**. Gate A: never over the player's own head; other units' damage draws nothing.
Words for dodge/parry/block/evade/immune/deflect/miss/absorb/resist (`law.rs:97-107`).
*Optionally* also build system (B) center-scroll (over your own character, red
damage, green heals, the CombatText.xml constants) — but (A) is the higher-value port.

**(d) Health bar over units + player resource bars.**
Over-unit: project each unit's overhead point, draw an ImGui filled rect using
`WorldEntity.HealthFraction` (`Entities.cs:31`, already available) with the
reaction color (defer coloring to the target-frame/Foundations section). Player
resource bars: add `UNIT_POWER1..5` (23-27) and `UNIT_MAXPOWER1..5` (29-33)
accessors to `ObjectFields`, select the active slot by `Bytes0.PowerType`
(0 mana/1 rage/2 focus/3 energy), draw HP + the one power bar. In-combat tint from
`UNIT_FLAGS & 0x0008_0000`.

**(e) Optional scrolling combat-log ImGui window.**
A ring buffer of formatted lines from the same `CombatEvent` queue (e.g. `"You hit
Kobold for 42."`, `"Kobold's Bite hits you for 15."`), an `ImGui.BeginChild` with
auto-scroll. Uses names from the name/creature-query cache. Ungated (unlike the
floating text): shows everyone's actions, matching the `UNIT_COMBAT`/chat scope.

---

## 8. Gotchas / empirical notes

- **The no-health-packet trap.** Never compute damage by differencing
  `UNIT_HEALTH`. The number to show comes from the combat-log / attacker-state
  packet; the HP delta arrives separately (and is post-mitigation, coalesced, and
  may lag or batch). Health bars follow HP deltas; combat numbers follow combat
  packets. They are independent streams (§5).
- **hit_info bits are a bitfield, read them as flags** (§2 table): crit `0x80`
  (melee) vs `0x2` (spell) — **different bits for the same concept**. Offhand `0x4`,
  glancing `0x4000`, crushing `0x8000`, full absorb `0x20`, full resist `0x40`, full
  block `0x800`. `HITINFO_MISS 0x10` is *not* tested by the text emitter — a zero-
  damage swing with no absorb/resist bit falls through to the "Miss" word
  (`law.rs:163-176`).
- **Full block only shows via synthesis.** `blocked` on the wire is invisible unless
  you run benilla's rewrite: damage==0 && blocked!=0 && victim-known ⇒ victim_state:=5
  (`combat.rs:96-98`). A *partial* block stays state 1 (looks like a plain hit);
  its amount only surfaces as a center-text trailer.
- **VictimState precedence.** A word state (dodge/parry/block/evade/immune/deflect)
  wins unconditionally over any damage — draw the word, ignore the number
  (`law.rs:156-177`).
- **Source suppression (K) is total, not "white".** Another player/mob's damage
  floats *nothing* over units near you (`combat_log.rs:43-57`, `:228-230`). Only your
  own and your pet's. This keeps the screen clean; do not "default to white" for
  foreign damage. (The center text and combat-log window are self/participant-scoped
  differently — see §3/§4B.)
- **Melee number ≠ spell number color.** Your melee number is **white**; your spell
  number is **gold**; pet melee is **orange**. Crit changes size (2× pop), not color.
  No school tint in the worldtext path. (The *center* system colors damage red and
  spell purple — different system, §4B.) Getting these backwards is the single most
  visible authenticity bug.
- **A spell crit does not make a crit-typed center message** — `spell_center_text`
  always emits `SPELL_DAMAGE` regardless of crit (`combat_log.rs:98-118`); only the
  *worldtext* number uses the crit pop category. Heals *do* have HEAL_CRIT.
- **Heals/energize never float** as worldtext in 5875 (chat/center/portrait only,
  `combat_log.rs:249-250,424-427`). Don't draw green numbers over units.
- **Animation-vs-swing-timer sync.** benilla starts the swing clip at packet receive
  and hangs the victim number/blood/impact-sound on the clip's *attack-hit
  keyframe* (`combat.rs:66-71`, `sound/combat.rs`). MSUI's `M2Animator` doesn't
  expose keyframe events yet, so approximate the impact at a fixed fraction of the
  swing clip (~40-50%); the center text can fire immediately (benilla fires it
  synchronously at parse, `ui_unit.rs:66-71`).
- **Guid encoding is not uniform.** PackedGUID for spell-damage/periodic/heal/
  energize; raw u64 for damage-shield/environmental/log-miss/attack-stop-victim
  (§3). A wrong reader desyncs the whole packet.
- **PeriodicAuraLog can't skip unknown tick types** — an unrecognized aura type is a
  hard parse error, not a skip, because the payload width is type-dependent
  (`combat_log.rs:142-147`). Decode the full known set or you lose the stream.
- **Center-text crit "parks"** (endY=startY) and never scrolls away; its height is
  uncapped past the old 32 clamp (decision 0582, `combat_text_tests.rs:237-261`).
- **Damage shield reflects onto the attacker** — the number floats over the unit
  that *struck* the shield bearer, not the bearer (`combat_log.rs:382-386`,
  `messages/combat_log.rs:208-211`).

---

## Cross-section deps & uncertainties

- **Target frame / selection** (own section): MSUI has `SetSelection` + a
  `CMSG_SET_SELECTION` opcode but no stored current-target guid on the game thread.
  The over-unit health bar's "your target" emphasis, and wiring AttackSwing to a
  selected target, both need that selection state.
- **World→screen projection** (Foundations): all of §7c/§7d anchoring relies on the
  shared projection helper; `camera.RelativeViewProjection` (`CreatureRenderer.cs:144`)
  is the matrix. The benilla overhead-anchor (`z − 1/3`, head-height attach) is in
  `combat_text/mod.rs:207-231`.
- **Auras** (own section): PeriodicAuraLog ticks are DoT/HoT feedback; the aura
  *icons/timers* live with the aura descriptor fields (`UNIT_FIELD_AURA`, decision
  0257) — combat text only shows the tick numbers.
- **Portrait / unit-frame chrome** (Foundations/target-frame): the `UNIT_COMBAT`
  portrait hit-flash is a distinct ungated channel (`ui_unit.rs:41-64`); can be a
  later add-on to the resource bars.
- **Uncertainties**: (1) combat-log **opcode numbers** are canonical vanilla values
  — verify against `benilla-protocol/src/messages/opcode.rs` (not staged). (2) The
  **combat/attack/death M2 animation ids** beyond CombatWound=9 and the locomotion
  set are from AnimationData.dbc knowledge — benilla's `creature_anim` id map was not
  staged; verify each id. (3) benilla flags several combat-text specifics PROVISIONAL
  (COMBAT_TEXT_UPDATE participant scope; the SPELLLOGMISS record-push color) — noted
  in `ui_unit.rs:66-76` and `combat_log.rs:516-519`.

# Part 5 — Spells: data model, spellbook, casting pipeline & casting bar

Porting spec for MSUIClient (C#/Silk.NET/ImGui, WoW 1.12.1 build 5875). Ground truth =
benilla (Rust/Bevy). Citations: `crates/…` = benilla repo-relative; `MSUIClient/…` = MSUI.
All benilla line numbers are real (verified by reading), not guessed.

This section owns: the spell DATA model + its DBC readers, `SMSG_INITIAL_SPELLS` → known
spells, the cast pipeline (`CMSG_CAST_SPELL` + `SMSG_SPELL_START`/`GO`/cast-result), the casting
bar, and the spellbook window. The action-bar section consumes the **`TryCast` entry point**
defined in §4. Tooltips/icons/textures share the "Foundations" section; FX (`SpellVisual`) is
noted but not owned here.

---

## 1. The spell DATA model — which DBCs, which columns

benilla's spell "display catalog" is `SpellCatalog`, built by `load_spell_catalog`
(`crates/benilla-formats/src/spells/mod.rs:515-652`). It joins **Spell.dbc** with **SpellIcon.dbc**
(and SpellDispelType.dbc for dispel names). Every column was pinned empirically against extracted
5875 data + wow-re byte reads; the module header `crates/benilla-formats/src/spells/mod.rs:1-112`
is the authority and documents the derivation for each.

### 1a. Spell.dbc — 22357 records × **173 fields**, record size 692 B

All fields are `u32` on disk EXCEPT the ones read as another type below (the DBC is a flat table of
4-byte cells; `i32` vs `u32` is only which accessor you use — same bytes). Strings are a **byte
offset into the string block**, not inline. Column indices are 0-based (col 0 = record id = spell
id). Schema built at `mod.rs:489-512`; the field extraction loop at `mod.rs:523-645`.

The **complete column set a port must read** (const name → column → meaning; consts at
`mod.rs:143-311`):

| Field | Col | Read as | Const | Meaning / bits a port needs |
|---|---|---|---|---|
| ID | 0 | u32 | — | the spell id (map key) |
| Category | 2 | u32 | `COL_CATEGORY` | shared-cooldown category (SpellCategory.dbc id); 0 = none |
| CastUI | 3 | u32 | `COL_CAST_UI` | spellbook add-gate (nonzero ⇒ hidden); 0 for player spells |
| Dispel | 4 | u32 | `COL_DISPEL` | SpellDispelType.dbc id (aura tooltip's debuff class) |
| Attributes | 6 | u32 | `COL_ATTRIBUTES` | **bit table below** |
| AttributesEx | 7 | u32 | `COL_ATTRIBUTES_EX` | 0x200 initiates-combat; 0x1000_0000 no-aura-icon |
| AttributesEx2 | 8 | u32 | `COL_ATTRIBUTES_EX2` | 0x20 auto-repeat; 0x2_0000 no-reset-combat-timers; 0x8_0000 allow-while-unshifted; 0x10_0000 initiate-combat-post-cast |
| AttributesEx3 | 9 | u32 | `COL_ATTRIBUTES_EX3` | 0x8000 normal-ranged-attack (white dmg); 0x40_0000 casting-cancels-autorepeat |
| Stances | 11 | u32 | `COL_STANCES` | form mask spell is castable in (`1<<(form-1)`); 0 = any |
| StancesNot | 12 | u32 | `COL_STANCES_NOT` | forbidden-form mask |
| Targets | 13 | u32 | `COL_TARGETS` | `TARGET_FLAG_*` seed mask for the cast-arm (§4) |
| RequiresSpellFocus | 15 | u32 | `COL_REQUIRES_SPELL_FOCUS` | SpellFocusObject.dbc id (anvil/forge nearby) |
| CasterAuraState | 16 | u32 | `COL_CASTER_AURA_STATE` | required caster aura-state index |
| TargetAuraState | 17 | u32 | `COL_TARGET_AURA_STATE` | required target aura-state (Execute's <20%) |
| **CastingTimeIndex** | 18 | u32 | `COL_CASTING_TIME_INDEX` | **→ SpellCastTimes.dbc row** |
| RecoveryTime | 19 | u32 | `COL_RECOVERY_TIME` | spell's own cooldown, ms |
| CategoryRecoveryTime | 20 | u32 | `COL_CATEGORY_RECOVERY_TIME` | category shared cooldown, ms |
| InterruptFlags | 21 | u32 | `COL_INTERRUPT_FLAGS` | cast-break flags; **bit 0x1 = movement** |
| ChannelInterruptFlags | 23 | u32 | `COL_CHANNEL_INTERRUPT_FLAGS` | channel-break flags; **bit 0x8 = moving** |
| ProcChance | 25 | u32 | `COL_PROC_CHANCE` | percent (`$h` token); vmangos 101 = "always" |
| **DurationIndex** | 30 | u32 | `COL_DURATION_INDEX` | **→ SpellDuration.dbc row** |
| powerType | 31 | u32 | `COL_POWER_TYPE` | 0 mana / 1 rage / 3 energy |
| manaCost | 32 | u32 | `COL_MANA_COST` | flat cast cost in powerType's unit |
| **rangeIndex** | 36 | u32 | `COL_RANGE_INDEX` | **→ SpellRange.dbc row** |
| Speed | 37 | **f32** | `COL_SPEED` | projectile speed (world u/s); 0 = instant impact |
| Totem[2] | 40–41 | u32 | `COL_TOTEM_1` | required tool items (present, not consumed) |
| Reagent[8] | 42–49 | i32 | `COL_REAGENT_1` | consumed item entries (0 = unused slot) |
| ReagentCount[8] | 50–57 | u32 | `COL_REAGENT_COUNT_1` | per-reagent count |
| EquippedItemClass | 58 | **i32** | `COL_EQUIPPED_ITEM_CLASS` | required worn-item class; **−1 = none** |
| EquippedItemSubClassMask | 59 | u32 | `COL_EQUIPPED_ITEM_SUBCLASS_MASK` | `1<<subclass` refinement |
| Effect[3] | 61–63 | u32 | `COL_EFFECT_1` | effect type enum (78 = ATTACK, 36 = LEARN_SPELL, 0x21 OPEN_LOCK, 0x2f TRADE_SKILL) |
| EffectDieSides[3] | 64–66 | **i32** | `COL_EFFECT_DIE_SIDES_1` | die size (`$s` token) |
| EffectBaseDice[3] | 67–69 | i32 | `COL_EFFECT_BASE_DICE_1` | dice count (`$s`) |
| EffectBasePoints[3] | 76–78 | **i32** | `COL_EFFECT_BASE_POINTS_1` | roll floor (`$s`); −1 = weapon-dmg sentinel |
| EffectRadiusIndex[3] | 88–90 | u32 | `COL_EFFECT_RADIUS_INDEX_1` | **→ SpellRadius.dbc row** (`$a` token) |
| EffectApplyAuraName[3] | 91–93 | u32 | `COL_EFFECT_APPLY_AURA_1` | aura-type enum (36 = MOD_SHAPESHIFT) |
| EffectAmplitude[3] | 94–96 | u32 | `COL_EFFECT_AMPLITUDE_1` | periodic tick period ms (`$t`/`$o`) |
| EffectMultipleValue[3] | 97–99 | **f32** | `COL_EFFECT_MULTIPLE_VALUE_1` | chain falloff (`$e`) |
| EffectChainTarget[3] | 100–102 | u32 | `COL_EFFECT_CHAIN_TARGETS_1` | extra chain targets (`$x`) |
| EffectItemType[3] | 103–105 | u32 | `COL_EFFECT_ITEM_TYPE_1` | created item entry (crafting) |
| EffectMiscValue[3] | 106–108 | **i32** | `COL_EFFECT_MISC_1` | misc payload (form id, LockType, …) |
| EffectTriggerSpell[3] | 109–111 | u32 | `COL_EFFECT_TRIGGER_1` | triggered spell id (learn/proc) |
| SpellVisual | 115 | u32 | `COL_VISUAL_ID` | SpellVisual.dbc id (FX; §5 note) |
| SpellIconID | 117 | u32 | `COL_ICON_ID` | **→ SpellIcon.dbc row** (the book/bar face) |
| ActiveIconID | 118 | u32 | `COL_ACTIVE_ICON_ID` | stance-active icon (druid forms) |
| SpellName enUS | 120 | **str** | `COL_NAME_ENUS` | name (loc block 120..127, +128 flags) |
| SpellNameSubtext enUS | 129 | **str** | `COL_NAME_SUBTEXT_ENUS` | "Rank N" (block 129..136, +137) |
| Description enUS | 138 | **str** | `COL_DESCRIPTION_ENUS` | tooltip body, raw `$`-tokens (block 138..146) |
| AuraDescription enUS | 147 | **str** | `COL_AURA_DESCRIPTION_ENUS` | buff/debuff tooltip text (block 147..155) |
| ManaCostPercentage | 156 | u32 | `COL_MANA_COST_PCT` | % of base mana added to flat cost |
| StartRecoveryCategory | 157 | u32 | `COL_START_RECOVERY_CATEGORY` | **GCD category** (133 = ordinary) |
| StartRecoveryTime | 158 | u32 | `COL_START_RECOVERY_TIME` | **GCD ms** (1500 ordinary; 0 = no GCD) |
| StanceBarOrder | 166 | **i32** | `COL_STANCE_BAR_ORDER` | stance-bar sort key (−1 last) |

**Attributes (col 6) bit table** — the display/gating bits benilla consumes (consts
`mod.rs:313-405`, predicates in `display.rs`):
- `0x2` RANGED (uses ranged slot) · `0x20` IS_TRADESKILL · `0x40` PASSIVE · `0x80` DO_NOT_DISPLAY
  (hidden in book/aura bar) · `0x4|0x400` (=`0x404`) ON_NEXT_SWING · `0x1_0000` NOT_SHAPESHIFT ·
  `0x2_0000` ONLY_STEALTHED · `0x8_0000` CASTABLE_WHILE_DEAD · `0x100_0000` castable-while-mounted ·
  `0x200_0000` COOLDOWN_ON_EVENT (bit 25) · `0x1000_0000` NOT_IN_COMBAT (bit 28).

The book add-gate `in_spellbook()` = `Attributes & (0x80|0x20) == 0 && castUI == 0`
(`display.rs:328-330`). `passive()` = `Attributes & 0x40` (`display.rs`, field at `mod.rs:562`).

The per-effect `[3]` arrays are parallel (slot0 col, +1, +2). `SpellDisplay` (the parsed struct,
one per spell) is fully documented field-by-field at `crates/benilla-formats/src/spells/display.rs:9-186`.

### 1b. SpellIcon.dbc — 1033 records × 2 fields

`ID(0)`, `TextureFilename(1, str)` = `Interface\Icons\<Name>` **with no file extension** — the BLP
loader appends `.blp` (`mod.rs:10-12`). Loaded as a `HashMap<iconId → path>` by
`crate::dbc::load_spell_icon_map` (called at `mod.rs:516`). Icon resolution:
`SpellIconID (col 117) → SpellIcon.dbc → path → BLP → texture`. Icon id 0 ⇒ no icon (render the
`?` fallback) (`mod.rs:538-540`). Auto-attack (Effect[0]==78) and ranged shots substitute the
equipped weapon icon instead — see §6/§9.

### 1c. The five satellite DBCs (each its own reader/catalog)

- **SpellCastTimes.dbc** — 52 rows × 4 fields, 16 B/rec: `ID(0)`, `Base(1)` ms, `PerLevel(2, i32,
  signed)` ms/level, `Minimum(3)` ms floor. **Row 1 = {0,0,0} = the universal instant sentinel**;
  row 16 = {1500,0,1500} (Fireball). `crates/benilla-formats/src/spells/cast_times.rs:11-18,60-89`.
  Client cast time = `clamp(Base + PerLevel·(casterLvl − spellLvl), min=Minimum)` — a port can ship
  the flat `Base` (exact for the vast majority of 1.12 rows).
- **SpellDuration.dbc** — 82 rows × 4 fields: `ID(0)`, `Duration(1, i32)`, `DurationPerLevel(2,
  i32)`, `MaxDuration(3, i32)`, **all signed**. **Row 21 = {−1,0,−1} = permanent** ("until
  cancelled"). `duration.rs:10-17,84-110`.
- **SpellRange.dbc** — 28 rows × **22 fields**: `ID(0)`, `MinRange(1, f32)`, `MaxRange(2, f32)`,
  `flags(3)` where **bit 0 = melee family** (substitute combat-reach sum, floor 5.0, for the
  authored pair). Row 2 = {0,5,melee}, 114 = {8,35} Auto Shot, 35 = {0,35} Fireball, 95 = {8,25}
  Charge. `ranges.rs:13-31,53-83`.
- **SpellRadius.dbc** — 24 rows × 4 fields: `ID(0)`, `Radius(1, f32)`, `RadiusPerLevel(2, f32)`,
  `RadiusMax(3, f32)`. `$a` token source. `radius.rs:1-5,44-67`.
- **SpellShapeshiftForm.dbc** — 32 rows × 14 fields, 56 B/rec: `ID(0)` = form id,
  `BonusActionBar(1)`, `Name(2, str)` (block 2..9, the tooltip "Requires %s"), `flags1(11)` where
  **bit 0 = stance** (doesn't count as shapeshifted) and **bit 0x2 = block toggle-cancel**,
  `creatureType(12, i32)`. `forms.rs:16-92`. Consumed by the form gate `usable_in_form(form,
  form_is_stance)` at `display.rs:446-462` (StancesNot → Stances → NOT_SHAPESHIFT / bit19-waiver
  composition).

---

## 2. Tooltip token substitution ($s1/$d/…) — `tokens.rs`

The spell Description/AuraDescription hold raw `$`-tokens the client substitutes at render time.
`substitute(text, spell, ctx)` at `crates/benilla-formats/src/spells/tokens.rs:169-272`, formulas
byte-verified (module header `tokens.rs:1-25`). A port needs this for correct spell tooltips.

- `$s<n>` / `$m` / `$M` — effect value, slot `n` (1-based, default 1, clamped to ≤2 at
  `tokens.rs:246-252`). `MIN = BasePoints + BaseDice`, `MAX = BasePoints + DieSides·BaseDice`
  (`effect_bounds`, `tokens.rs:41-46`). `$s` prints one number when MIN==MAX else `"MIN to MAX"`;
  **values print sign-absolute** ("reduces armor by 30", not −30) (`tokens.rs:95-114`).
- `$o<n>` — over-time total = `perTick · duration / period`, period = EffectAmplitude (default 5000
  ms when 0) (`tokens.rs:115-127`).
- `$d` — duration via SpellDuration.dbc: "until cancelled" if permanent, else whole sec/min/hours
  (interim formatter, `tokens.rs:56-68,128-132`).
- `$t<n>` period sec · `$a<n>` radius yards (SpellRadius.dbc) · `$h` proc chance · `$x<n>` chain
  targets · `$e<n>` multiple-value float · `$z` home-bind area name (from `SMSG_BINDPOINTUPDATE`,
  `tokens.rs:30-38,153-157`). `$r` range and `$u` stacks are **passed through unresolved**.
- Cross-spell refs `$<id><token><idx>` (e.g. `$6136s1`) resolve through `ctx.lookup`
  (`tokens.rs:200-209,253-262`).
- `$/N;` / `$*N;` divide/multiply the next token's value (`tokens.rs:183-199`).
- `$l<sing>:<plur>;` picks by the last substituted numeric value; `$g<m>:<f>;` gender (renders male
  until a gender input exists) (`tokens.rs:210-233`).
- **Unknown tokens pass through verbatim** (visible, greppable) rather than vanishing
  (`tokens.rs:268`) — a deliberate fold-back flag.

Trap: the value formula is the **general n-dice rule**, not `base+1..base+sides`; per-level dice
terms exist but are dropped (interim, exact for most 1.12 rows). Test vectors at
`tokens.rs:307-330,335-357` (Fireball "14 to 22", the `$/2;`+`$l` plural combo).

---

## 3. SMSG_INITIAL_SPELLS — the spellbook wire

`SMSG_INITIAL_SPELLS = 0x012A` (confirmed `MSUIClient/MSUIClient/Net/Opcodes.cs:80`). One packet,
sent once at login, carries **both** the known-spell id list and the active-cooldown list — they
cannot be split (`crates/benilla-protocol/src/messages/spellbook.rs:1-16`).

Wire layout (`read_initial_spells`, `spellbook.rs:35-58`; golden bytes
`crates/benilla-protocol/tests/spells.rs:18-41`):

```
u8   unknown (always 0)
u16  n
n ×  { u16 spellId, u16 0 }          // the second word is "not slot id", always 0, skip
u16  m
m ×  { u16 spellId, u16 castItemId, u16 category, u32 spellCdMs, u32 categoryCdMs }  // SpellCooldown
```

The cooldown block carries **remaining** ms (`spellbook.rs:22-33`). A permanent cooldown = `spellCdMs
== 1` with the category word's top bit set.

**The book is NOT organised by the wire** — the wire is a flat id set. benilla applies it verbatim
into `PlayerActions.spells` (a `HashSet<u32>`) at `crates/benilla/src/net/apply/spells.rs:21-38`,
and the **tab/page organisation is a client-side render concern** built by `ui_spellbook`
(§6). Book grows after login via:
- `SMSG_LEARNED_SPELL` — `{u16 spellId, u16 actionBarSlot(dropped)}` (`spellbook.rs:65-69`); inserts
  into the set (`apply/spells.rs:52-57`).
- `SMSG_SUPERCEDED_SPELL` — `{u16 old, u16 new}` (`spellbook.rs:74-76`); rank-up swaps in book AND on
  the action bar (`apply/spells.rs:63-73`).

Tabs are by **skill line** (SkillLine.dbc × SkillLineAbility.dbc), routed through a per-race/class
General collapse — this is `ui_spellbook`'s join, not this packet's; see §6.

---

## 4. The CAST pipeline end-to-end

Opcodes (confirmed from MSUI's own enum, `MSUIClient/MSUIClient/Net/Opcodes.cs:74-77`):
`CMSG_CAST_SPELL = 0x012E`, `SMSG_CAST_RESULT = 0x0130`, `SMSG_SPELL_START = 0x0131`,
`SMSG_SPELL_GO = 0x0132`. Additional opcodes benilla uses but **MSUI's enum lacks** (add from vmangos
`Opcodes_1_12_1.h`; benilla references them symbolically via `messages::opcode::*` — the numeric
opcode table was not in the staged benilla tree, so a port must pull the exact values from
vmangos): `CMSG_CANCEL_CAST`, `CMSG_CANCEL_CHANNELLING`, `CMSG_CANCEL_AURA`, `SMSG_SPELL_FAILED_OTHER`,
`SMSG_SPELL_DELAYED`, `MSG_CHANNEL_START`, `MSG_CHANNEL_UPDATE`, `SMSG_SPELL_COOLDOWN`,
`SMSG_ITEM_COOLDOWN`, `SMSG_COOLDOWN_EVENT`, `SMSG_CLEAR_COOLDOWN`, `SMSG_CANCEL_AUTO_REPEAT`,
`SMSG_LEARNED_SPELL`, `SMSG_SUPERCEDED_SPELL`.

### 4a. CMSG_CAST_SPELL wire format

`crates/benilla-protocol/src/messages/spells.rs:311-322` (`cast_spell`) + writer
`crates/benilla-protocol/src/world/writer/spells.rs:25-30`. Golden bytes
`crates/benilla-protocol/tests/spells.rs:79-100`:

```
u32  spellId
u16  targetMask            // SpellCastTargets flags
[SpellCastTargets payload per the mask]
```

**There is NO cast-count byte in 1.12.1.** (The leading `u8 castCount` is a TBC 2.x+ addition.)
`cast_spell(6673, None)` = `11 1a 00 00  00 00` — 6 bytes, spell + mask, nothing else. This is a
common porting trap; do not add a count byte.

The three shapes benilla writes:
- **Self / implicit** — mask `TARGET_FLAG_SELF (0x0000)`, nothing follows. Server resolves the
  target from the spell's implicit targeting (`spells.rs:314-315`).
- **Explicit unit** — mask `TARGET_FLAG_UNIT (0x0002)` + the target guid **packed**
  (`spells.rs:316-320`). `cast_spell(5176, guid) = 38 14 00 00  02 00  c9 2a 45 30 f1` — the trailer
  is a packed guid: 1 mask byte (bit i set iff byte i of the u64 is nonzero) then the nonzero bytes
  low→high (`tests/spells.rs:86-91`). MSUI already has `WritePackedGuid`
  (`MSUIClient/MSUIClient/Net/ByteBuffer.cs:132`).
- **GameObject** (open-lock) — mask `GAMEOBJECT|LOCKED (0x4800)` + packed GO guid
  (`cast_spell_gameobject`, `spells.rs:335-341`). **Item** (enchant) — mask `TARGET_FLAG_ITEM
  (0x0010)` + packed item guid (`cast_spell_item`, `spells.rs:348-354`). Both out of the action-bar's
  scope but the same opcode.

**Target flags** (`spells.rs:59-67`, and the cast-arm bit table `cast_target.rs:36-52`): UNIT 0x2,
UNIT_RAID 0x4, UNIT_PARTY 0x8, ITEM 0x10, SOURCE_LOCATION 0x20, DEST_LOCATION 0x40, UNIT_ENEMY 0x80,
UNIT_ASSIST 0x100, CORPSE_ENEMY 0x200, EXPLICIT_GATE 0x400, GAMEOBJECT 0x800, TRADE_ITEM 0x1000,
STRING 0x2000, LOCKED 0x4000, CORPSE_ALLY 0x8000.

**How the wire target is chosen** (this is the subtle part — `cast_target.rs`): the client does NOT
just ship the current selection. `resolve_cast_target` (`cast_target.rs:262-310`) seeds a flag_word
from `Spell.dbc Targets (col 13)`, adjusts it by the `EffectImplicitTargetA[0] (col 82)` switch
(`cast_target_mask`, `cast_target.rs:182-199`: enum 6/53→enemy bit, 21/45→assist bit, 1→self,
25/63→unit, etc.), then:
- **flag_word == 0** ⇒ self-implicit (mask 0, no guid). Ice Armor/Battle Shout/Feign Death. Shipping
  the selection here is the classic "Invalid target" bug (`cast_target.rs:7-10,276-278`).
- **nonzero** ⇒ bind the selection if it satisfies every bit (relation checks: assist ≥ friendly,
  enemy = attackable, corpse = dead) (`clear_satisfied_bits`, `cast_target.rs:209-257`); else fall
  back to **self** when `autoSelfCast` is on (buffing with an enemy selected → casts on you,
  `cast_target.rs:293-303`); else refuse locally (`ERR_NO_TARGET 0x09` / `ERR_INVALID_TARGET 0x0A`,
  `cast_target.rs:54-57,304-309`). Ground-AoE / item / GO / string masks are refused, not guessed
  (`cast_target.rs:279-283`).

### 4b. SMSG_SPELL_START (0x0131) — a cast began

`crates/benilla-protocol/src/messages/spells.rs:142-181`. VERIFIED vmangos `SendSpellStart`:

```
packed  item_or_caster   // cast-item guid if any, else the caster
packed  caster           // always the casting unit
u32     spellId
u16     castFlags        // always 0x2 (UNKNOWN2); +0x20 AMMO for ranged
u32     castTimeMs       // remaining ms (0 for an instant — precast trigger)
SpellCastTargets         // mask (0x0002/pguid, 0x0000 self, …)
[ammo block if castFlags & 0x20]:  u32 displayId, u32 inventoryType(dropped)
```

`SpellCastTargets` decode `spells.rs:87-127` — must follow vmangos' **write-side** branch order
(UNIT > GAMEOBJECT > CORPSE): exactly ONE packed guid rides for the unit/GO/corpse bits, then
item(0x10|0x1000)→pguid, source(0x20)→Vector3d, dest(0x40)→Vector3d, string(0x2000)→cstring. Golden
`tests/spells.rs:240-315`.

### 4c. SMSG_SPELL_GO (0x0132) — the cast launched

`spells.rs:187-247`. Same guid pair + spellId as START, then:

```
u16     castFlags        // always 0x100 (UNKNOWN9); +0x20 AMMO for ranged
u8      hitCount ;  hitCount × u64  (RAW, unpacked hit guids)
u8      missCount; missCount × { u64 guid, u8 SpellMissInfo, u8 reflectResult(only if reason==11) }
SpellCastTargets
[ammo block if castFlags & 0x20]
```

Sent at **launch**, not impact — nothing about missile travel rides this; the server schedules
impact off `Spell.dbc Speed` (`spells.rs:187-194,218`). Golden `tests/spells.rs:317-389`; the
GameObject-target variant `tests/spells.rs:502-535`.

### 4d. SMSG_CAST_RESULT (0x0130) — the server's verdict ("cast failed")

`spells.rs:30-53`. **This is the packet the task calls "SMSG_CAST_FAILED".** Layout:
`u32 spellId, u8 status` — status 0 = OKAY (ends the body), status 2 = FAIL + `u8 reason`
(reason-specific trailing arg words are left unread; the slice ends with the packet). Golden
`tests/spells.rs:131-147`. **vmangos never sends an OK CAST_RESULT for a normal cast** — success is
observed via `SMSG_SPELL_GO`; only failures ride this (`crates/benilla/src/ui_cast.rs:150-155`).

### 4e. Interrupt / pushback / channel

- `SMSG_SPELL_FAILED_OTHER` — `{u64 caster(raw), u32 spellId}` (`spells.rs:249-257`): the
  broadcast cancel notice; our own in-flight cast interrupted arrives here (server answers a cancel
  with FAILED_OTHER + a failing CAST_RESULT reason 0x23 SPELL_FAILED_INTERRUPTED,
  `ui_cast.rs:280-285`).
- `SMSG_SPELL_DELAYED` — `{u64 caster(raw), u32 delayMs}` (`spells.rs:263-267`): **pushback**, a hit
  on a pushback-eligible cast; the cast does NOT cancel, the timer stretches by `delayMs`.
- `MSG_CHANNEL_START` — `{u32 spellId, u32 durationMs}`, **self-only, no guid on the wire**
  (`spells.rs:272-276`). `MSG_CHANNEL_UPDATE` — `{u32 remainingMs}`, self-only; **0 = channel over**
  (natural end AND interrupt both send 0) (`spells.rs:281-283`).
- Cancels (outbound, `world/writer/spells.rs:63-75`): `CMSG_CANCEL_CAST {u32 spellId}`,
  `CMSG_CANCEL_CHANNELLING {u32 spellId}` (server ignores the id, cancel is unconditional),
  `CMSG_CANCEL_AURA {u32 spellId}` (`spells.rs:361-363` — by spell, **not** by aura slot).

### 4f. The cast state machine (ui_cast.rs) — the app-side model

`crates/benilla/src/ui_cast.rs` holds four resources, all spell-id-keyed and self-guid-gated:

- **`PendingCast`** (`ui_cast.rs:79-134`) — the optimistic in-flight guard. **Armed at SEND**
  (not at START — spam lands during the round trip), `arm` gives a 5 s provisional deadline
  (`SEND_PROVISIONAL`, `ui_cast.rs:50`); `refine(castTimeMs)` tightens it to the real time + 2 s
  slack at SPELL_START (`ui_cast.rs:112-116`); `delay(ms)` extends on pushback
  (`ui_cast.rs:121-125`); `clear_if(spellId)` opens it on the resolving GO / failing CAST_RESULT /
  FAILED_OTHER (id-keyed so a proc's GO mid-cast can't open it early, `ui_cast.rs:129-133`). This
  guard is why mashing a key no longer fires duplicate casts the server bounces as spurious bar
  cancels (`ui_cast.rs:57-78`).
- **`QueuedMeleeSpell`** (`ui_cast.rs:163-185`) — on-next-swing spells (`Attributes & 0x404`:
  Heroic Strike, Cleave). Deadline-less, single-slot, wire-cleared. Never blocks the next cast.
- **`ActiveChannel`** (`ui_cast.rs:196-224`) — the running channel: `start(spell, dur)` at
  CHANNEL_START, `update(ms)` re-times (pushback shortens), `update(0)` ends. Slack deadline
  self-heals a lost UPDATE(0).
- **`Casting`** component (per-entity, `apply/spells.rs:243-247`) — set at SPELL_START for cast_time
  > 0, reaped at GO/FAILED, `{spell_id, until}`.

The **local self-cancel** (`local_self_cancel`, `ui_cast.rs:295-399`): move/jump/Esc mid-cast cancels
**locally, same frame** (~16 ms) instead of waiting for the server's 0.5-yd position-delta interrupt
(~150 ms+). Gated per spell: cast cancels iff `InterruptFlags & 0x1` (movement); channel cancels iff
`ChannelInterruptFlags & 0x8` (moving) (`ui_cast.rs:238-239,347-398`). Esc can cancel a cast but
**NOT a channel** (the vanilla `/stopcasting` quirk, `ui_cast.rs:268-276`), and stops auto-repeat
first (`ui_cast.rs:322-330`). Uncataloged spells fail open (cancel). This is a **feel** feature — the
server stays the safety net; a port can defer it and rely on the server, but it is authentic.

### 4g. THE ENTRY POINT for the action-bar section — `TryCast`

benilla's single cast-send path is `send_spell_cast(spellId, ctx, …)`
(`crates/benilla/src/ui_action/cast_send.rs:40-281`) — "one cast path" so the action bar, spellbook,
stance bar and craft window can't drift (`cast_send.rs:1-13`). It transcribes the client's
`TryCast 0x6e4b60` → commit `0x6e54f0` ladder, gate for gate.

**MSUI should expose exactly this as the coordination point.** Recommended signature:

```csharp
// The one cast-send path. targetGuid = the caller's current selection (0/null = none);
// the cast system itself decides self-implicit vs unit target from Spell.dbc, NOT the caller.
// Returns the local verdict; on refusal, reason is a client error code (see §9) for the red line.
CastResult TryCast(uint spellId, ulong targetGuid = 0);
enum CastOutcomeLocal { Sent, ToggledOff, RefusedLocally }  // + reason byte on Refused
```

The ladder `TryCast` runs, in order (each an early-out on refusal; **a refusal is local and
pre-commit — no packet, no GCD, no guard armed**), from `cast_send.rs`:
1. Profession-opener intercept (Effect[0]==0x2f TRADE_SKILL → open craft window, no packet)
   `cast_send.rs:58-66`.
2. Auto-repeat re-press toggle (re-pressing the running auto-repeat cancels it) `cast_send.rs:67-76`.
3. Cooldown refusal (`is_on_cooldown`, reason 0x3c) `cast_send.rs:77-85`.
4. GCD refusal (`gcd_locked`, reason 0x3c; GCD is NOT part of the cooldown test) `cast_send.rs:86-95`.
5. In-flight guard (`pending.in_flight`: same spell bails silent, different errors 0x61)
   `cast_send.rs:110-120`.
6. Mounted gate (reason 0x39) `cast_send.rs:121-140`.
7. Reagent/totem possession (reason 0x78/0x5c) `cast_send.rs:141-150`.
8. Target resolution (`resolve_cast_target`, §4a — self/unit/refuse) `cast_send.rs:151-168`.
9. Range gate (`cast_range_refusal` on the bound unit target) `cast_send.rs:169-196`.
10. Commit tail: ranged-stance arm, auto-repeat arm, **send `CMSG_CAST_SPELL`**, then
    `pending.arm` (normal) or `queued_melee.arm` (on-next-swing), auto-attack start
    (`initiates_auto_attack`), GCD arm (`start_gcd`) `cast_send.rs:217-280`.

A minimal first port can implement steps 1(skip), 3–5, 8, 10 and defer 6/7/9 — but keep **step 8's
self-vs-unit decision** (that's the "Invalid target" correctness bug) and **step 5's guard** (the
spam fix). The action-bar agent calls `TryCast(spellId, currentTargetGuid)` and reads the returned
reason for its red error line; it must NOT build the `CMSG_CAST_SPELL` body itself.

---

## 5. The casting bar UI — `CastingBar.xml` + ui_cast

benilla drives an authentic transcription of 1.12's `CastingBarFrame`
(`crates/benilla/assets/ui/CastingBar.xml`). `ui_cast` fires FrameScript events off the wire; the XML
Lua runs the bar. **MSUI (ImGui) reimplements the state machine directly** — the XML is the spec for
dims/colours/behaviour.

Event contract `ui_cast` fires (`feed_cast_bar`, `ui_cast.rs:411-478`; XML registers at
`CastingBar.xml:44-56`): `SPELLCAST_START(name, ms)`, `SPELLCAST_STOP`, `SPELLCAST_FAILED`,
`SPELLCAST_INTERRUPTED`, `SPELLCAST_DELAYED(ms)`, `SPELLCAST_CHANNEL_START(ms, name)`,
`SPELLCAST_CHANNEL_UPDATE(ms)`, `SPELLCAST_CHANNEL_STOP`. The **spell name is resolved from the
catalog here** (`ui_cast.rs:434-440`) — the bar shows a name string, not an id.

Behaviour (`CastingBar.xml:58-180`):
- **Normal cast**: bar fills **left→right**, value rises from `startTime` to `startTime + ms/1000`
  (`CastingBar.xml:63-64,133-145`). Colour **orange (1.0, 0.7, 0.0)** (`:60,:237`).
- **Channel**: bar counts **DOWN** (drains right→left), `barValue = startTime + (endTime − time)`
  (`CastingBar.xml:107-129,146-160`). Same orange.
- **Complete** (STOP / CHANNEL_STOP): snap to full, colour **green (0.0, 1.0, 0.0)**, hide spark, arm
  flash+fade (`:72-86`).
- **Failed / Interrupted**: colour **red (1.0, 0.0, 0.0)**, text = `FAILED`/`INTERRUPTED`, hold 1 s
  then fade — but **ignored while channeling** (`:87-100`).
- **Pushback** (DELAYED): shift `startTime`+`maxValue` out by `arg1/1000` — the spark jumps back, the
  bar keeps running, never cancels (`:101-106`).
- **Spark**: `sparkPosition = ((value − startTime)/(maxValue − startTime)) * 195`, anchored CENTER to
  the frame's LEFT + offset, y=2 (`:141-145,159-160`).

Empirical dims (`CastingBar.xml:183-238`): StatusBar **195 × 13**, bar texture
`Interface\TargetingFrame\UI-StatusBar`; anchored BOTTOM y=55 default (UIParent bumps to y≈100, or
140 with the stance bar — `:20-25`); BACKGROUND black a=0.5; border
`Interface\CastingBar\UI-CastingBar-Border` 256×64 (TOP y=28); spark
`Interface\CastingBar\UI-CastingBar-Spark` 32×32 (ADD); flash `…-Flash` 256×64 (ADD); text
`CastingBarText` (GameFontHighlight) 185×16, TOP y=5. Constants `CastingBar.xml:28-38`: alpha step
0.05, flash step 0.2, hold 1 s, normalized to a 30 Hz reference tick (the ref applied steps per
OnUpdate; normalize to render-rate-independent, `:33-38,164,172`).

**Duration source**: cast time = `SMSG_SPELL_START.castTimeMs` (server-authoritative, already
level-scaled + haste-adjusted); channel = `MSG_CHANNEL_START.durationMs`. The bar never computes cast
time from SpellCastTimes.dbc itself — that DBC feeds tooltips (§1c), the wire feeds the bar.

---

## 6. The spellbook window — `SpellBookFrame.xml` + ui_spellbook

Authentic 1.12 `SpellBookFrame` (`crates/benilla/assets/ui/SpellBookFrame.xml`). The **model** is
built by the app (`crates/benilla/src/ui_spellbook.rs`), the **render** by the XML. MSUI reimplements
both in ImGui; the constants are the spec.

**Model** (`build_book`, `ui_spellbook.rs:174-261`) produces `SpellBookState { tabs:
Vec<SpellTabView{name, texture, offset, num_spells}>, slots: Vec<SpellSlotView{spell_id, name, rank,
texture, passive}> }`:
- **Add-gate**: only `in_spellbook()` spells (§1a; drops languages, proficiencies, hidden passives,
  tradeskills, castUI≠0) `ui_spellbook.rs:184-199`. A spell absent from Spell.dbc is dropped too.
- **Tabs = skill lines** routed through a per-race/class **General collapse** (racials, generic and
  cross-class spells fall into the General tab, key 0) via `SkillLineCatalog::spell_tab`
  (`ui_spellbook.rs:195-198`). **General is pinned FIRST**, then tabs alphabetical by SkillLine.dbc
  name (`ui_spellbook.rs:201-219`). General's name+icon are hardcoded (`"General"` +
  `Interface\Icons\Ability_Kick`, `ui_spellbook.rs:64-68,208-210`).
- **Within a tab**: sort by name, then **parsed rank number** ascending (scan the first digit run of
  the "Rank N" subtext — not `strip_prefix`), then the rank string (`spell_sort_key` +
  `leading_number`, `ui_spellbook.rs:270-287`).
- **`offset`/`num_spells`** are each tab's slice into the flat `slots` list (`ui_spellbook.rs:223-259`).
- **Passive** flag per slot (`ui_spellbook.rs:250`). **Icon substitution**: auto-attack
  (Effect[0]==78) shows the equipped weapon icon (or Spell-Reset unarmed); ranged shots
  (`ranged_icon_substitution`, both Attributes 0x2 + AttributesEx2 0x20) show the ranged weapon icon;
  else the spell's own icon (`ui_spellbook.rs:229-244`).

Rebuilt each frame, diffed, and pushed with a `SPELLS_CHANGED` event on change
(`ui_spellbook.rs:160-169`).

**Render dims** (`SpellBookFrame.xml`): window **384 × 512** (`:486`); `SPELLS_PER_PAGE = 12`,
`MAX_SKILLLINE_TABS = 8` (`:86-87`). The page grid is **2 columns × 6 rows** with **column-major
ids** (col1 ids 1–6, col2 ids 7–12 — the button `id=` is its within-tab 1-based position, NOT
screen order) (`:588-627`). Spell button **37 × 37**, background
`UI-Spellbook-SpellBackground` 64×64 at (−3,3), NormalTexture `UI-Quickslot2` 64×64, icon
setAllPoints; button 1 at TOPLEFT (34, −85), horizontal spacing +157, vertical −14 (`:432-481,592-627`).
Skill-line tab **32 × 32**, backdrop `SpellBook-SkillLineTab` 64×64 at (−3,11); tabs down the right
edge starting (−32, −65), spacing −17 (`:396-422,630-653`). Book-id formula:
`id + selectedSkillLineOffset + 12·(page−1)` (`:357-360`); maxPages = `max(ceil(numSpells/12), 1)`
(`:384`).

**Interaction** (`:233-242`): plain click → `CastSpell(id)` (routes to the ONE cast path,
`ui_spellbook.rs:290-333` → `send_spell_cast` = the §4g `TryCast`); shift-click/drag →
`PickupSpell(id)` (**drag-to-action-bar hook**: packs cursor kind 0x00 SPELL + spell id, placed on an
action button through the shared slice-4 machinery — `spellbook_tests.rs:169-195`). **Passive**
spells are greyed (normalTexture vertex (0,0,0), UI-PassiveHighlight, PASSIVE_SPELL_FONT_COLOR) and
the engine's `CastSpell` refuses them (`:290-299`). Rank shows as the subtext second line
(`GetSpellName` returns name + rank; **even Rank 1 shows literally**, no "hide Rank 1" case —
`mod.rs:50-53`). Cooldown sweep, autocast overlay, and the currently-casting checked ring are
OMITTED in benilla's build (`:42-51`) — a port may add the cooldown sweep once cooldowns are wired.
End-to-end drive test `crates/benilla/src/ui_script/spellbook_tests.rs:102-196`.

---

## 7. What MSUI has today

- **Protocol present, spells NOT wired.** `WorldSession.cs` handles only auth / char-enum / warden
  (grep for CAST/SPELL/START/GO returns nothing — `MSUIClient/MSUIClient/Net/WorldSession.cs`). No
  `SMSG_INITIAL_SPELLS` handler despite the opcode being defined.
- **Opcodes**: `CMSG_CAST_SPELL 0x012E`, `SMSG_CAST_RESULT 0x0130`, `SMSG_SPELL_START 0x0131`,
  `SMSG_SPELL_GO 0x0132`, `SMSG_INITIAL_SPELLS 0x012A`, `SMSG_ACTION_BUTTONS 0x0129` are already in
  the enum (`Net/Opcodes.cs:66-81`). **Missing**: cancel-cast/channel/aura, spell-failed-other,
  spell-delayed, channel-start/update, all cooldown packets, learned/superceded — must be added.
- **DBC reader present, no spell DBCs.** `DbcReader.cs` has the generic `DbcFile` (WDBC header +
  record block + string block) with `GetUInt/GetInt/GetFloat(row,field)` and
  `GetString(row,field)` (byte-offset into the string block) — `MSUIClient/MSUIClient/Formats/DbcReader.cs:25-121`.
  Existing typed tables (ItemDisplayTable, CharSectionsTable) follow the pattern `const string
  MpqPath` + static `Parse(byte[])` → typed rows (`DbcReader.cs:143-380+`). **No Spell / SpellIcon /
  SpellCastTimes / SpellDuration / SpellRange / SpellRadius / SpellShapeshiftForm reader — all
  greenfield.**
- **Packed guid ready**: `ByteBuffer.cs:64` (`ReadPackedGuid`) / `:132` (`WritePackedGuid`).
- No cast state machine, no casting bar, no spellbook, no cooldown store.

---

## 8. Port plan for MSUI (ImGui-native), ordered

**(a) DBC readers** — one typed table per DBC, mirroring `DbcFile`'s `Parse` pattern
(`DbcReader.cs:143+`). Use `GetUInt/GetInt/GetFloat/GetString`. List with key columns (all from §1):
- `SpellTable` ← `DBFilesClient\Spell.dbc` (173 fields). Read at minimum: 0(id), 2(category),
  3(castUI), 4(dispel), 6/7/8/9(attributes), 11/12(stances), 13(targets), 15/16/17, **18(castTimeIdx)**,
  19/20(recovery), 21/23(interrupt), 25(proc), **30(durationIdx)**, 31/32(power/mana),
  **36(rangeIdx)**, 37(speed,f32), 40-57(totems/reagents), 58(equipClass,i32)/59, 61-63(effects),
  64-111(effect arrays — 76 basePoints i32, 88 radiusIdx, 91 applyAura, 94 amplitude, 106 miscValue
  i32, 109 triggerSpell), 115(visual), **117(iconId)**, 118(activeIcon), **120(name,str)**,
  **129(rank,str)**, **138(desc,str)**, **147(auraDesc,str)**, 156(manaPct), 157/158(GCD),
  166(stanceOrder,i32). String cells = byte offsets → `GetString`.
- `SpellIconTable` ← `SpellIcon.dbc` (2 fields): id→`GetString(row,1)` = `Interface\Icons\…`
  (append `.blp`).
- `SpellCastTimesTable` ← `SpellCastTimes.dbc` (4 fields): id, base ms, perLevel(i32), min.
- `SpellDurationTable` ← `SpellDuration.dbc` (4 fields, all i32): id, base(−1=permanent), perLevel, max.
- `SpellRangeTable` ← `SpellRange.dbc` (22 fields): id, min(f32,1), max(f32,2), flags(3,bit0=melee).
- `SpellRadiusTable` ← `SpellRadius.dbc` (4 fields): id, radius(f32), perLevel, max.
- `SpellShapeshiftFormTable` ← `SpellShapeshiftForm.dbc` (14 fields): id, bonusBar(1),
  name(str,2), flags1(11), creatureType(i32,12). (Also SkillLine.dbc + SkillLineAbility.dbc +
  SkillRaceClassInfo.dbc for tabs — coordinate with skills; can defer to one General tab initially.)

**(b) SpellCatalog** keyed by spell id: a `Dictionary<uint, SpellRecord>` joining Spell×SpellIcon
(and the learn-spell hop: Effect==36 → EffectTriggerSpell, `mod.rs:525-535`). Resolve iconId→path at
build. This is the port of `SpellCatalog`/`SpellDisplay` (`mod.rs:432-481`, `display.rs`).

**(c) SMSG_INITIAL_SPELLS handler** (§3) → a `HashSet<uint> knownSpells` + seed the cooldown store
from the cooldown block. Add `SMSG_LEARNED_SPELL`/`SMSG_SUPERCEDED_SPELL` to grow it.

**(d) TryCast + wire** (§4): the `CMSG_CAST_SPELL` writer (spell + mask + packed guid via
`WritePackedGuid`), the target resolver (§4a — self-implicit vs unit from Targets col 13 +
implicit col 82), and the `SMSG_SPELL_START`/`GO`/`CAST_RESULT`/`FAILED_OTHER`/`DELAYED` +
`CHANNEL_START`/`UPDATE` handlers feeding the cast state (§4f: PendingCast guard, ActiveChannel,
Casting). Expose `TryCast(spellId, targetGuid)` (§4g) as the action-bar's entry point.

**(e) ImGui casting bar** (§5): a 195×13-proportioned bar, orange fill (L→R cast / R→L channel),
green on complete, red 1 s hold on fail/interrupt, spark at the fill edge, pushback shifts the
window. Duration from `SMSG_SPELL_START.castTimeMs` / `MSG_CHANNEL_START.durationMs`. Spell name from
the catalog.

**(f) ImGui spellbook panel** (§6): 2×6 grid, 12/page, skill-line tabs (General first), name+rank
lines, passive greying, click→`TryCast`, drag→action-bar payload. Reuse `DbcFile` + the BLP/Texture
path (SpellIcon.dbc → `Interface\Icons\…` → BLP → texture) shared with Foundations.

---

## 9. Gotchas / empirical notes

- **NO cast-count byte in 1.12.1 `CMSG_CAST_SPELL`.** Body is `u32 spell + u16 mask + [pguid]`
  only. The `u8 castCount` is 2.x+ (`tests/spells.rs:81-91`). Highest-risk trap in this section.
- **The client picks the wire target, not the caller.** flag_word 0 ⇒ self-implicit (mask 0, no
  guid) — shipping the selection for Ice Armor/Battle Shout is the "Invalid target" bug
  (`cast_target.rs:7-10,276`). Only nonzero unit masks ship the guid; friendly-required casts fall
  back to self (autoSelfCast). Ground/item/GO masks refuse, not guess (`cast_target.rs:262-310`).
- **SMSG_CAST_RESULT status: 0 ends the body, only status 2 has a reason byte.** vmangos sends NO OK
  result for a normal cast — success = `SMSG_SPELL_GO`. Don't wait on an OK result
  (`spells.rs:42-52`, `ui_cast.rs:150-155`). Client reason codes seen: 0x09 no-target, 0x0A invalid,
  0x23/0x24 interrupted, 0x39 mounted, 0x3c not-ready, 0x5c/0x78 reagent/totem, 0x61 another-action.
- **Icon resolution**: `SpellIconID (col 117) → SpellIcon.dbc → Interface\Icons\<Name>` **without
  extension** — the BLP loader appends `.blp` (`mod.rs:10-12`). Icon 0 ⇒ `?` fallback. Auto-attack
  (Effect[0]==78, only spell 6603) and ranged shots substitute the equipped **weapon** icon, not the
  DBC `Temp` placeholder (`display.rs:332-374`, `ui_spellbook.rs:229-244`).
- **Rank handling**: rank = SpellNameSubtext (col 129), literally "Rank N" — **even first ranks show
  "Rank 1"** (no hide case, `mod.rs:50-53`). Sort by the **parsed leading digit run**, not string or
  `strip_prefix("Rank ")` (`ui_spellbook.rs:279-287`). Rank-up (`SMSG_SUPERCEDED_SPELL`) swaps the id
  in book AND on the action bar (`apply/spells.rs:63-73`).
- **Token traps**: values are sign-absolute (`$s` prints 30, never −30); the value formula is the
  general n-dice rule `base+dice … base+sides·dice` (not `base+1…`); per-level dice terms are dropped
  (interim, exact for most rows); unknown tokens stay verbatim (`tokens.rs:12,95-114,268`).
- **Target-flag packing**: SpellCastTargets emits exactly ONE packed guid for the unit/GO/corpse
  bits by write-side priority (UNIT > GAMEOBJECT > CORPSE) — decode must mirror that branch order or
  the stream desyncs (`spells.rs:69-127`). Packed guid = 1 mask byte (bit i = byte i nonzero) + the
  nonzero bytes low→high.
- **Channel vs cast are different wires and different bar directions.** Casts: SPELL_START (fill up,
  L→R) / SPELL_GO. Channels: MSG_CHANNEL_START (count down, R→L) / MSG_CHANNEL_UPDATE(0 = end),
  both **self-only with no guid on the wire** (`spells.rs:272-283`, `CastingBar.xml:107-160`). Esc
  cancels a cast but not a channel; movement cancels either (per the interrupt-flag bit)
  (`ui_cast.rs:268-276`).
- **GCD is client-modelled, separate from cooldown.** `StartRecoveryCategory (157)` / `StartRecoveryTime
  (158)` = 133 / 1500 ms for ordinary spells, 0/0 for GCD-free (Attack, Auto Shot). Armed at send
  (`start_gcd`), tested separately from the spell cooldown (`is_on_cooldown` skips GCD fields), and a
  failing CAST_RESULT reverts it (`cast_send.rs:86-95,276-280`). Refusing a GCD-locked press LOCALLY
  is what stops the "spam-press vanished-pie" bug (`cast_send.rs:86-90`).
- **A normal cast's cooldown is CLIENT-tracked, started at SPELL_GO, not from a packet.** vmangos
  sends `SMSG_SPELL_COOLDOWN` only for school lockouts / pets / item procs / GM resets — the client
  starts the RecoveryTime/CategoryRecoveryTime sweep itself at the GO self-insert
  (`spellbook.rs:10-13`, `apply/spells.rs:334-380`). `SMSG_SPELL_COOLDOWN` with `cooldown_ms == 0`
  means "use Spell.dbc's own recovery" (`spellbook.rs:82-85`).
- **In-flight guard is optimistic (armed at send, not START)** — the spam lands during the round
  trip (`ui_cast.rs:57-78,103-109`). Ranged/auto-repeat/on-next-swing shots never arm it and are
  never blocked by it (`cast_send.rs:96-120`).
- **Ranged spells suppress the cast bar** even with a nonzero server cast time (vmangos pads Throw to
  ~500 ms): `ranged_slot()` gates the bar off (`apply/spells.rs:207-235`). Instants (cast_time 0)
  show no bar either (their GO follows immediately).
- **SpellVisual (col 115)** → SpellVisual.dbc (2165×16) → per-stage SpellVisualKit chain (precast/
  cast/impact/channel) — FX only, brief per scope. Schema at
  `crates/benilla-formats/src/spell_visual.rs:1-55`; not needed for the bar/book/cast-result path.

# Part 6 — Action bars: slots, cooldowns, usability & the stance bar

Ground truth = benilla (`crates/…`). Target = MSUIClient (`MSUIClient/MSUIClient/…`), native C#/ImGui, no
FrameXML. In benilla the action bar is split across an **app seam** (`crates/benilla/src/ui_action/`, Bevy
systems that own the authoritative 120-slot table, the cast pipeline, and the per-frame dynamic-state
compute) and an **engine seam** (`crates/benilla-ui/src/script/`, the FrameXML-facing API bindings +
shipped XML in `crates/benilla/assets/ui/`). MSUI has neither today — it must fold both faces into one
ImGui hotbar. Every number below is authentic 1.12 FrameXML, quoted by benilla from the extracted
`patch.MPQ`.

---

## 1. The action model (typed slot, 120 ids, 6 bars, paging)

**The wire slot.** One occupied slot is `ActionButton { slot: u8, action: u32, kind: u8 }`
(`crates/benilla-protocol/src/messages/action_bar.rs:23-32`). The bar is **120 packed `u32`s**
(`MAX_ACTION_BUTTONS`); each word packs `action` in bits 0–23 and `kind` in bits 24–31; a zero word is an
empty slot and is not surfaced (`action_bar.rs:19-22`, `34-52`).

**The type byte** (`action_bar.rs:15-17`, mirrored engine-side in
`crates/benilla-ui/src/script/action.rs:28-29`):
- `ACTION_KIND_SPELL = 0x00`
- `ACTION_KIND_MACRO = 0x40`
- `ACTION_KIND_ITEM  = 0x80`
- `0x01` ("click?") exists in the vmangos enum, carried raw if it ever appears (`action_bar.rs:30-31`). The
  engine-side crate only names SPELL and ITEM because it is protocol-free (`action.rs:26-29`).

**Companion/pet** is not a distinct action kind in 1.12 — the pet bar is a separate frame; the action-slot
type space is only spell/macro/item(/click). benilla models spell + item fully; **macro is a stated gap**
(no macro window ships — `drain.rs:258-263`, `feed.rs:275-276`).

**The app store.** `PlayerActions { buttons: HashMap<u8, ActionButton>, spells: HashSet<u32>, dirty: bool }`
(`crates/benilla/src/ui_action/mod.rs:73-88`). `buttons` is keyed by 0-based wire slot; **`spells` is the
spellbook** from `SMSG_INITIAL_SPELLS`. The bar is **client-authoritative** (`mod.rs:69-72`): the server
stores the 120 words and hands them back at login, never editing in normal play.

**Lua action id = wire slot + 1** (`action_bar.rs:28`, `action.rs:5-11`, `state.rs:213-214`). The live API
space is 1..120; the drain converts back with `slot = id - 1` (`drain.rs:116`, `drain.rs:286`).

**The 6 bars and paging.** All 120 ids live in one flat space; a "bar" is a 12-id window into it. Page N →
actions `(N-1)*12+1 .. N*12`. The main bar's window is chosen by the **bonus-bar offset** (warrior stances,
druid forms); the multibars carry a **fixed page base as per-button data** (`ActionBar.xml:104-113`):

| Bar | Page | Actions | How the window is chosen |
|---|---|---|---|
| Main bar (12) | 1 (+bonus) | 1..12, or `(6+offset-1)*12 + i` | `GetBonusBarOffset()` (`ActionBar.xml:95-102`) |
| MultiBarBottomLeft (12) | 6 | 61..72 | fixed `base = 60` (`MultiBars.xml:31-34,50`) |
| MultiBarBottomRight (12) | 5 | 49..60 | fixed `base = 48` (`MultiBars.xml:33-34,108`) |
| MultiBarRight / -Left | 3 / 4 | 25..48 | **OUT** in benilla (`MultiBars.xml:27-28`) |

The main-bar formula: `base = (6 + offset - 1)*12` when `offset > 0`, else `0`; the button shows `base +
index` (`ActionBar.xml:95-113`). So offset 1 (battle stance) pages the main bar to actions **73..84**
(verified `crates/benilla/src/ui_script/action_bar_tests.rs:33-34,93-95`). The multibars **never re-page** on
a bonus flip — only the main bar does (`multibar_stance_tests.rs:132-146`, `action.rs:13-17`). The
`GetBonusBarOffset` value is the player's shapeshift-form byte indexed into `SpellShapeshiftForm.dbc`'s
**BonusActionBar** column (`feed.rs:207-220`, `mod.rs:106-109`), pushed on change with
`UPDATE_BONUS_ACTIONBAR`.

**Local reflection of the server buttons.** The identity feed (`feed.rs:60-311`) resolves each occupied
slot to `ActionSlot { texture, kind, action, count }` (`action.rs:33-46`), diffs against the VM, pushes the
changed slots, and fires `ACTIONBAR_SLOT_CHANGED(actionId)` per transition. Spell icon = Spell.dbc ×
SpellIcon.dbc (or the borrowed weapon icon, §weapon-icon); item icon = the ask-once item-template chain,
falling back to the engine's literal `Interface\Icons\INV_Misc_QuestionMark` (`feed.rs:47`, `255-268`); item
`count` = the bag walk `ui_items::count_of` (`feed.rs:269-272`). Two things refresh **every frame** rather
than on the dirty flag: an item slot's **count** (eating a stack never touches `SMSG_ACTION_BUTTONS`) and a
weapon-substituting **icon** (`feed.rs:312-366`).

---

## 2. Server sync — the two wire packets

**`SMSG_ACTION_BUTTONS` (0x0129), login snapshot.** 120 packed `u32`s to end-of-body; benilla reads to the
boundary (robust to a different count), drops zero words, surfaces the rest as `ActionButton`s with
`action = packed & 0x00FF_FFFF`, `kind = (packed >> 24) as u8`
(`action_bar.rs:37-52`). In practice login-only, because the bar is client-authoritative (`action_bar.rs:1-7`).

**`SMSG_INITIAL_SPELLS` (0x012A)** fills `PlayerActions.spells` (the known-spell set — `mod.rs:77`).

**`CMSG_SET_ACTION_BUTTON` (opcode 296 = 0x0128), the one-slot write.** Body is **5 bytes**: `button: u8`
then `packetData: u32` (little-endian), where `packetData = action | (kind << 24)` — the exact `ActionButton`
packing (`action_bar.rs:54-65`, verified against vmangos `Server/Packets/Misc.cpp:87-90`,
`Opcodes_1_12_1.h:299`). `packed == 0` **clears** the slot (server `removeActionButton`). The writer is one
verb (`crates/benilla-protocol/src/world/writer/action_bar.rs:22-27`):
`send(CMSG_SET_ACTION_BUTTON, set_action_button(button, packed))`.

**Persistence law** (`world/writer/action_bar.rs:3-21`, `action_bar.rs:54-59`, `drain.rs:269-311`):
- Client sends **exactly one** `CMSG_SET_ACTION_BUTTON` per local slot mutation.
- A drag-**swap is two independent sends**, never atomic.
- There is **no answer packet** — `SMSG_ACTION_BUTTONS` only re-arrives on a server-side edit (GM command,
  macro-menu save), never as an echo of your own edit.

`drain_action_sets` (`drain.rs:277-311`) is the outbound half: for each queued `(lua_id, packed)` it writes
`PlayerActions.buttons` locally (`packed==0` removes; else inserts `action = packed & 0xFFFFFF`,
`kind = packed >> 24`), sets `dirty` so the identity feed re-pushes + fires `ACTIONBAR_SLOT_CHANGED`, and
sends the one CMSG. This is what makes **drag-to-slot persist server-side**.

---

## 3. Slot activation → cast/use (cast_send / cast_target / cast_fail)

`drain_action_uses` (`crates/benilla/src/ui_action/drain.rs:90-267`) drains queued `UseAction(id)`, resolves
`slot = id-1`, looks up `buttons.get(slot)`, and forks on kind:

- **Auto-attack** (`kind==SPELL`, `action==SPELL_ATTACK==6603`, `mod.rs:66`): mounted-refusal gate
  (`ERR_ATTACK_MOUNTED`, `errors.rs:89-99`), then `CMSG_ATTACKSWING` at the current selection, or
  `AttackNearestRequest` when there is no target (`drain.rs:121-161`). Melee start also snaps the sheath and
  cancels any running auto-repeat.
- **Spell** (`kind==SPELL`): `send_spell_cast(...)` (`drain.rs:162-180`).
- **Item** (`kind==ITEM`): `item_action_route` (`drain.rs:32-88`, `186-257`) — the byte-verified **two-stage
  equip-vs-use** law: `InventoryType==0 → USE`; `!=0 & worn → USE IN PLACE`; `!=0 & not worn → EQUIP`
  (`drain.rs:44-52`). An on-use item on cooldown surfaces client-local reason `0x28`
  ("Item is not ready yet.", `drain.rs:229-240`); the wire's third byte is the spell **block ordinal**, not a
  flag (`drain.rs:245-255`). A copy found nowhere is a debug-log-and-skip, **not** a red error line
  (`drain.rs:181-213`) — because nothing was attempted server-side.
- **Macro** (`kind==0x40`): stated gap (`drain.rs:258-263`).

**The one cast-send path** (`crates/benilla/src/ui_action/cast_send.rs:40-281`) is the whole `TryCast →
commit` ladder, gate for gate, and is **shared** by the action bar, the spellbook, the stance bar, the
trade-skill/craft windows (`cast_send.rs:1-13`, `28-32`). Order:
1. Profession-window intercept — an `Effect[0]==SPELL_EFFECT_TRADE_SKILL` cast opens the crafting book,
   **no packet** (`cast_send.rs:59-66`).
2. Auto-repeat re-press toggles it **off** (press-again-to-stop, `cast_send.rs:67-76`).
3. Local not-ready: **spell/category cooldown** refuses `0x3c` (`cast_send.rs:77-85`) — deliberately skips
   the GCD fields.
4. **GCD** leg refuses `0x3c` separately (`cast_send.rs:86-95`); GCD-free presses (Heroic Strike queue,
   Attack, Shoot) pass.
5. In-flight guard: same spell bails silently, a different one errors `0x61`
   ("Another action is in progress", `cast_send.rs:110-120`).
6. Mounted gate → `0x39` unless the spell carries castable-while-mounted (Attributes bit 24)
   (`cast_send.rs:121-140`, `state.rs:102-104`).
7. Reagent/totem possession → `0x78`/`0x5c` (`cast_send.rs:141-150`, `errors.rs:112-133`).
8. **Target binding** (`resolve_cast_target`) then **range** refusal (`cast_send.rs:152-196`).
9. Commit tail: ranged-stance sheath, auto-repeat key arm, `CMSG_CAST_SPELL{spell_id, target}`,
   `pending.arm`/`queued_melee.arm`, auto-attack initiation, `start_gcd` (`cast_send.rs:217-280`).

A refusal is **local and pre-commit**: no packet, no GCD, no pending arm — just the red-line reason
(`cast_send.rs:12-13`).

**Target resolution** (`crates/benilla/src/ui_action/cast_target.rs:262-310`). The cast never blindly ships
the current selection. `resolve_cast_target` seeds a flag_word from `Spell.dbc Targets` + the
implicit-target switch (`cast_target_mask`, `cast_target.rs:182-199`), then:
- **flag_word == 0** → `SelfImplicit` (wire mask `TARGET_FLAG_SELF`, no guid). The server fills the target;
  the client *never* ships the selection for these — shipping it is exactly the "Invalid target" bug this
  fixes (Ice Armor, Battle Shout, `cast_target.rs:7-11`, `276-278`).
- **unit bits** → satisfy each bit against the selection (`clear_satisfied_bits`, `cast_target.rs:209-257`);
  a fully-cleared word binds `TARGET_FLAG_UNIT` + that guid.
- **unsatisfied** → fall back to the **active player**, gated on `autoSelfCast` (the classic "buffing with an
  enemy targeted casts on yourself", `cast_target.rs:292-303`).
- **still unbound** → refuse locally with `ERR_NO_TARGET = 0x09` ("You have no target.") or
  `ERR_INVALID_TARGET = 0x0A` ("Invalid target") (`cast_target.rs:56-57`, `304-310`).

**Self-cast / focus modifiers.** benilla defaults `autoSelfCast` **ON** (`cast_target.rs:164-175`) — a *named
deviation* from the ref CVar default `"0"`, because benilla hasn't modeled the targeting-cursor machine yet.
The `onSelf` third arg to `UseAction` is accepted and **ignored** (self-cast modifier not modeled,
`action.rs:202-210`); there is no focus unit in 1.12.

**What `cast_fail.rs` surfaces** (`crates/benilla/src/ui_action/cast_fail.rs`). Two layers, all strings from
the VM's loaded `GlobalStrings.lua` (localized, never hardcoded):
1. `CAST_FAIL_KEYS[146]` maps the wire reason → `SPELL_FAILED_<name>` (`cast_fail.rs:27-174`).
2. ~12 reasons have an **errorId override** that REPLACES the message (`cast_fail.rs:207-245`): `0x3c` →
   cooldown family (spell/ability/potion/food by category & attr), `0x4d` NO_POWER → the spell's own power
   family ("Not enough rage" for a rage ability, `cast_fail.rs:176-187`, `238-241`), `0x59` → "Out of
   range.", `0x28` → item cooldown, `0x17` DONT_REPORT → hidden. Reagent/totem `0x78`/`0x5c` get the failing
   item's name filled into `%s` in the feed (`feed.rs:119-152`). An absent key shows nothing.

Local refusals reach the same red line through `CastErrors` (reason-coded) and `UiErrorKeys` (by GlobalStrings
key — `errors.rs:26-99`); all four queues drain in `feed_actions` firing `UI_ERROR_MESSAGE`
(`feed.rs:78-203`).

---

## 4. Usability & tinting (the exact colour states)

The **compute** lives in `crates/benilla/src/ui_action/usable.rs` — `spell_usable(...)` returns
`(usable, not_enough_mana)` (`usable.rs:85-188`), the full `IsSpellUsableNow 0x6e3d60` gate walk: tradeskill
early-out, dead, reagents/totems, equipped-item class/subclass, shapeshift-form, only-stealthed,
not-in-combat, caster aura-state, target aura-state (+attack/assist fork), cooldown-on-event fold, and
**power**. Any tripped gate answers `(false, false)`; **only the power leg** (leg 12, `usable.rs:172-186`)
sets `not_enough_mana`. The state feed calls this per occupied spell slot each frame and pushes it via
`set_action_state` (`state.rs:242-257`); `IsUsableAction(id)` returns the `(1/nil, 1/nil)` pair
(`action.rs:263-271`).

The **tint** is in the FrameXML — `BenillaActionButton_UpdateUsable` (`ActionBar.xml:224-238`), verbatim ref
colours. `IsUsableAction` gives `(isUsable, notEnoughMana)`:

| State | Rule | icon `SetVertexColor` | normalTexture (ring) |
|---|---|---|---|
| **Usable** | `isUsable` | `1.0, 1.0, 1.0` (white) | `1.0, 1.0, 1.0` |
| **Not enough power** | `notEnoughMana` | `0.5, 0.5, 1.0` (blue-grey) | `0.5, 0.5, 1.0` (ring too) |
| **Unusable (other)** | else | `0.4, 0.4, 0.4` (grey) | `1.0, 1.0, 1.0` (ring stays white) |

Note the blue tints **both** icon and ring; the grey tints **only** the icon (`ActionBar.xml:228-237`).
Verified: `action_bar_tests.rs:219-244` asserts OOM → icon `(0.5,0.5,1.0)`; `multibar_stance_tests.rs:280-292`
asserts stance not-castable → `(0.4,0.4,0.4)`.

**Out of range is a separate channel — the hotkey, not the icon.** `BenillaActionButton_OnUpdate`
(`ActionBar.xml:288-306`) runs a **0.2 s** range recheck (`TOOLTIP_UPDATE_TIME`, `ActionBar.xml:54`); when
`IsActionInRange(action) == 0` it paints the **HotKey** `SetVertexColor(1.0, 0.1, 0.1)` (red); in range →
`0.6, 0.6, 0.6` (grey). For an unbound multibar button (no key label) it instead shows a red `"●"` dot
(`ActionBar.xml:294-302`, verified `multibar_stance_tests.rs:164-194`). The **range source** is the state
feed's `in_range` (`state.rs:263-268`: `d2 ∈ [min², max²]`), computed from `resolve_range` — the byte-verified
`GetMinMaxRange 0x6e3480` law (`state.rs:106-148`): melee reach pad `1.3333` + floor `5.0`, ranged pads both
bounds by the bare reach sum but a min-0 row never grows a min, self-cast → flat `100`. `IsActionInRange`
returns `nil` (no range / no target) / `0` / `1` (`action.rs:274-290`).

**Equipped green border** (an item worn): `IsEquippedAction` → `Border:SetVertexColor(0, 1.0, 0, 0.35)`
(`ActionBar.xml:359-364`).

The four dynamic reads (usable/oom, in-range, current/auto-repeat, cooldown) are recomputed and diff-pushed
every frame in `state::feed_action_state` (`state.rs:150-388`), which fires
`ACTIONBAR_UPDATE_USABLE`/`_STATE`/`_COOLDOWN` on the transitions.

---

## 5. Cooldowns (the radial swipe model)

**The pushed value.** A cooldown is `(start_ms, duration_ms, enabled)` where **`start_ms` is the absolute
start on the `GetTime` clock** (`action.rs:76-84`). Absolute-start is load-bearing: one running cooldown
re-derives the same triple every frame (no diff churn), while a re-arm derives a new one (the sweep restarts)
— this fixed the "vanished-GCD-pie-on-spam" bug (`state.rs:306-312`). Stored converted to seconds
(`action.rs:113-132`). `GetActionCooldown(id)` returns `(start_s, duration_s, enable)` and **goes cold** once
`start + duration <= now`, answering `(0, 0, 1)` so a later re-feed can't replay the finish flash
(`action.rs:321-335`).

**The gate.** `CooldownFrame_SetTimer(this, start, duration, enable)` (`Cooldown.xml:12-19`, shared by action
buttons and bag slots) shows-and-arms **only when all three are positive**, else hides.

**The swipe math.** The widget models the ref's `Model` playing `UI-Cooldown-Indicator.mdx`
(`crates/benilla-ui/src/script/cooldown.rs:1-16`): sequence-0 sweep fraction =
**`(GetTime() - start) / duration`**, clamped; at `1.0` it flips to the realtime finish flash, then
`OnAnimFinished` hides. The pie is the **60 %-black** overlay and the finish flash is the **`star4`** sprite
(`cooldown.rs:12-14`). `SetCooldown(start, duration)` only (re)arms the timer; show/hide stays the authored
helper's job (`cooldown.rs:48-59`). Verified: a `(6000, 10000)` cooldown at `GetTime==10` (started 6 s in,
4 s elapsed) → sweep fraction **0.4**, no flash (`action_bar_tests.rs:184-201`). The exact finish-flash
window is `COOLDOWN_FLASH_SECS` (`crates/benilla-ui/src/widget/mod.rs:178`; the constant + fraction/flash
extraction live in `widget/kinds.rs`, which is **not in this staged slice** — the model is fully pinned by
the doc + test above).

**GCD vs spell cooldown.** These are *two distinct legs* of the send ladder, both refusing reason `0x3c`
locally: `is_on_cooldown` (spell/category cooldown, **skips** the GCD fields — `cast_send.rs:77-85`) and
`gcd_locked` (the `startRecoveryCategory` GCD — `cast_send.rs:86-95`). The GCD is armed **at send** via
`start_gcd` (`cast_send.rs:276-280`) and cleared by a later `SMSG_CAST_RESULT` failure. The swirl draws
**whichever cooldown the store reports** for that spell via `cooldowns.info(...).ui_triple(...)`
(`state.rs:269-270`) — GCD and spell cooldown share the one pie. A store-generation change fires
`ACTIONBAR_UPDATE_COOLDOWN` + `SPELL_UPDATE_COOLDOWN` + `BAG_UPDATE_COOLDOWN` together (`state.rs:353-357`).
(The `crate::cooldowns::Cooldowns` resource that merges GCD + category + spell cooldown and produces
`CooldownInfo{ remaining_ms, duration_ms }.ui_triple` is a **consumed dependency not in this staged slice** —
noted as a cross-section dependency below.)

**Authentic cooldown dims.** The `Cooldown` child is **36×36 CENTER (0,-1)** on the 36×36 button
(`ActionBar.xml:626-631`); the stance-bar cooldown is **30×30** (`StanceBar.xml:126-131`).

---

## 6. Stance / shapeshift bar

**When it shows.** `GetNumShapeshiftForms() > 0` → show the frame, else hide (`StanceBar.xml:70-76`). A
formless class (mage, warlock, priest, hunter, etc.) never shows it. Populated for warrior stances, druid
forms, rogue Stealth, shaman Ghost Wolf, etc.

**Slot count.** Up to `NUM_SHAPESHIFT_SLOTS = 10` buttons (`StanceBar.xml:28`), one `CheckButton` per **known**
form; the count is the length of the app-pushed form list (`GetNumShapeshiftForms`,
`crates/benilla-ui/src/script/shapeshift.rs:89-95`). Buttons past `numForms` hide (`StanceBar.xml:77-98`). So
a warrior shows 2 (later 3) stances, a druid 1–4 forms, a rogue 1 (Stealth).

**Per-form data.** `GetShapeshiftFormInfo(i) → texture, name, isActive, isCastable`
(`shapeshift.rs:99-118`). `isActive` = the descriptor's `UNIT_FIELD_BYTES_1` form byte matches this form (the
checked ring); **`isCastable` IS the same usable walk** as §4 (`mod.rs:57-60`). Not-castable greys the icon
`0.4,0.4,0.4`, else `1.0,1.0,1.0` (`StanceBar.xml:89-93`). Cooldown swipe via `GetShapeshiftFormCooldown(i)`
— `GetActionCooldown`'s exact triple + goes-cold rule (`shapeshift.rs:120-135`).

**How a stance is activated.** Click → `CastShapeshiftForm(index)` (`StanceBar.xml:42-49`) → queues the
form's spell id (`shapeshift.rs:138-147`) → the app drains it onto the wire (cast-vs-cancel decided
app-side). The `CheckButton` toggles itself *before* `OnClick`; the handler **reverts** that toggle so the
checked ring follows only `isActive` — a clicked non-active form stays unchecked until the server's form byte
confirms, and clicking the **active** stance never untoggles it (`StanceBar.xml:36-49`, verified
`multibar_stance_tests.rs:304-330`). The app resolves the click to a cast or a silent cancel at drain time
(stances never cancel — the `0x4b4963` guard, `StanceBar.xml:39-41`). One refresh event drives it all:
`UPDATE_SHAPESHIFT_FORMS` (+ `PLAYER_ENTERING_WORLD`), the app having diffed the whole list
(`StanceBar.xml:16-25`).

**Geometry** (`StanceBar.xml:112-156`, permanently in the ref's *raised* mode because the bottom multibars
are always on): frame BOTTOMLEFT at `BenillaActionBar` TOPLEFT **+(30, 45)**; buttons **30×30**, button 1 at
frame BOTTOMLEFT **+(11, 3)**, chained **+7** each; NormalTexture ring `UI-Quickslot2` **50×50** CENTER
(0,-1). Verified button 1 rect `x[41,71] y[101,131]` (`multibar_stance_tests.rs:265-271`).

---

## 7. Authentic dimensions (the numbers MSUI's ImGui hotbar should use)

From `ActionBar.xml` / `MultiBars.xml` (rendered **1:1, no UIParent scale** — `ActionBar.xml:19`):

- **Button:** `36 × 36` CheckButton (`ActionBar.xml:596-597`).
- **Icon:** fills the button, owner-sized `36 × 36`, BACKGROUND layer (`ActionBar.xml:585-586,599-601`).
- **Chain / stride:** main button 1 at art-frame BOTTOMLEFT **+(8, 4)**; buttons 2..12 anchor LEFT of the
  previous's RIGHT **+(6, 0)** → **stride 42 px** (36 wide + 6 gap) (`ActionBar.xml:771-821`; verified stride
  42, button 1 rect `x[8,44] y[4,40]` in `action_bar_tests.rs:60-72`).
- **HotKey fontstring:** `NumberFontNormalSmallGray`, `justifyH=RIGHT`, size `36 × 10`, anchor **TOPLEFT
  +(-2, -2)** (`ActionBar.xml:604-609`). Default labels `1 2 3 4 5 6 7 8 9 0 - =` (`ActionBar.xml:62`).
- **Count fontstring:** `NumberFontNormal`, `justifyH=RIGHT`, anchor **BOTTOMRIGHT +(-2, 2)**
  (`ActionBar.xml:610-614`).
- **Ring (NormalTexture):** `Interface\Buttons\UI-Quickslot2`, `66 × 66`, CENTER **(0, -1)**
  (`ActionBar.xml:633-638`). Pushed `UI-Quickslot-Depress`; highlight `ButtonHilight-Square` (ADD); checked
  `CheckButtonHilight` (ADD) (`ActionBar.xml:639-641`).
- **Equipped border:** `UI-ActionButton-Border`, `62 × 62`, CENTER, ADD (`ActionBar.xml:617-622`).
- **Cooldown child:** `36 × 36`, CENTER **(0, -1)** (`ActionBar.xml:626-631`).
- **Bar:** `1024 × 53`, anchored **BOTTOM** of the screen (`ActionBar.xml:653-657`).
- **Multibars:** each `500 × 38`; BottomLeft BOTTOMLEFT on main button 1's TOPLEFT **+(0, 17)**; BottomRight
  LEFT on BottomLeft's RIGHT **+(10, 0)**; 12 buttons, same 36×36 icons at **+6** stride
  (`MultiBars.xml:42-155`; verified BottomLeft button 1 `x[8,57..93]`, BottomRight `x[518,...]` in
  `multibar_stance_tests.rs:104-113`).

For a **single ImGui hotbar** MSUI can start with the main 12: 36 px icons, 6 px gaps (42 px stride), a
~1 px inset for the icon inside the ring, hotkey top-left, count bottom-right, the whole strip centered on
the screen bottom. The full 1024-wide MainMenuBar plate art is optional chrome.

---

## 8. What MSUI has today (verified against the staged files)

- **Protocol opcodes exist but are inert.** `SMSG_ACTION_BUTTONS = 0x0129`, `SMSG_INITIAL_SPELLS = 0x012A`,
  `CMSG_CAST_SPELL = 0x012E`, `SMSG_CAST_RESULT = 0x0130` are declared
  (`MSUIClient/MSUIClient/Net/Opcodes.cs:74-81`) but explicitly **"parsed leniently / ignored for now"**
  (`Opcodes.cs:79`). The in-world dispatch (`MSUIClient/MSUIClient/Program.Net.cs:152-172`) handles only
  `SMSG_UPDATE_OBJECT`/`_COMPRESSED_UPDATE_OBJECT`/`_DESTROY_OBJECT`/`SMSG_MONSTER_MOVE`; action buttons and
  initial spells fall through the switch unread.
- **No `CMSG_SET_ACTION_BUTTON`** (0x0128) opcode, no `CMSG_USE_ITEM`, no cast writer — grep of `Opcodes.cs`
  and `WorldSession.cs` finds none.
- **No action bar, no cooldown tracking, no ability keybinds.** Nothing in the tree draws a hotbar.
- **Keyboard input** is a `HashSet<Key>` (`_held`) with `IsDown(Key)` / `Axis(...)`, fed by Silk.NET
  KeyDown/KeyUp (`MSUIClient/MSUIClient/Engine/ClientWindow.cs:112-113,342-349,614-617`) — movement only
  today.
- **UI is ImGui-only.** `Program.Net.cs` draws the login + char-select **glue screens** via ImGui, using a
  **`WowSkin`** helper (`GlueButton`/`GlueText`/`GlueGold`/`Highlight`/`Muted`) in namespace
  `MSUIClient.Engine.UI` (`Program.Net.cs:3,6,401-464`). Note: the `WowSkin` class file and the FRIZQT font
  asset are **referenced but not present in this staged slice** — the skin helper and a bordered-window /
  gold-text style exist and are reusable, but I could not read their source here.
- **Icon plumbing exists.** `Engine/Texture.cs` exposes GL textures with a `uint Handle`, `From2D`,
  `FromRgbaNoMips` (`MSUIClient/MSUIClient/Engine/Texture.cs:14-162`) — an ImGui `Image`/`AddImage` takes
  `(IntPtr)Handle` as the texture id.
- **Wire I/O helpers exist.** `PacketWriter` (`WriteU8`, `WriteU32`, `ToArray`) and `PacketReader`
  (`ReadU32`, `Remaining`, `HasMore`) (`MSUIClient/MSUIClient/Net/ByteBuffer.cs:41-147`); packets flow
  through `session.SendPacket(opcode, body)` and a `ConcurrentQueue` drained on the main thread
  (`NetworkClient.cs:48,147-149,304`; `Program.Net.cs:147`).

Net: **the transport is there, the action layer is entirely absent.**

---

## 9. Port plan for MSUI (ImGui-native), ordered

**(a) ActionSlot model + 6 bars + paging.** Port `ActionButton` as
`struct ActionSlot { byte Kind; uint ActionId; }` keyed by 0-based wire slot 0..119, packing
`packed = ActionId | (Kind<<24)` exactly as `action_bar.rs:23-32,60-65`. Constants `KIND_SPELL=0x00`,
`KIND_MACRO=0x40`, `KIND_ITEM=0x80`. Keep the flat 120-space; a bar is a 12-window: main = page 1 (+bonus
offset formula `base=(6+offset-1)*12`), BottomLeft = base 60 (61..72), BottomRight = base 48 (49..60)
(`ActionBar.xml:95-113`, `MultiBars.xml:31-34`). Store the bonus offset from the shapeshift-form byte →
`SpellShapeshiftForm.dbc` BonusActionBar column (start with offset 0 / one bar; add stance paging with §6).

**(b) SMSG handler + CMSG writer.** Add `CMSG_SET_ACTION_BUTTON = 0x0128` to `Op`. Parse
`SMSG_ACTION_BUTTONS` in the `Program.Net.cs:152` switch: loop `while (r.Remaining >= 4)` reading `u32`,
non-zero → slot occupied with `ActionId = w & 0x00FF_FFFF`, `Kind = (byte)(w >> 24)` (`action_bar.rs:37-52`);
parse `SMSG_INITIAL_SPELLS` into a known-spell set. Writer:
`var w = new PacketWriter(5); w.WriteU8(button); w.WriteU32(action | ((uint)kind<<24)); session.SendPacket((ushort)Op.CMSG_SET_ACTION_BUTTON, w.ToArray());`
(`world/writer/action_bar.rs:22-27`, `ByteBuffer.cs:110,114,147`). Client-authoritative: mutate the local
table first, send **one** CMSG per change, `packed==0` to clear, drag-swap = two sends, expect no echo
(`action_bar.rs:54-59`, `drain.rs:277-311`).

**(c) ImGui hotbar at authentic dims.** Per-frame, draw N slots at 36 px with 42 px stride (§7) via
`ImGui.GetBackgroundDrawList()` / `AddImage`: ring `UI-Quickslot2` (66×66 centered), icon (36×36, ~1 px
inset), hotkey top-left (`+(-2,-2)`), count bottom-right (`+(-2,2)`), cooldown swipe overlay. Icons reuse
`Engine/Texture.cs` (`(IntPtr)tex.Handle` — `Texture.cs:21`) via an icon-path→Texture cache. Cooldown swipe:
draw a 60 %-black pie whose swept fraction = `(now - start)/duration` clamped to [0,1] (`cooldown.rs:9-13`,
`action_bar_tests.rs:198-201`), plus a brief finish flash; hide when `start+duration <= now`
(`action.rs:328-334`). ImGui has no radial primitive — build the pie from a triangle fan
(`ImDrawList.PathArcTo` / `AddConvexPolyFilled`).

**(d) Usability tinting each frame.** Recompute `(usable, notEnoughMana)` from the spell/item state and tint
per §4: usable → white; `notEnoughMana` → icon+ring `0.5,0.5,1.0`; other-unusable → icon `0.4,0.4,0.4` (ring
white). Out-of-range → tint the **hotkey** `1.0,0.1,0.1` (else `0.6,0.6,0.6`), rechecked ~5×/s
(`ApplyVertexColor` on the ImGui image tint arg). Equipped item → green border. Drive `notEnoughMana` from
the power gate only (§4); range from a squared-distance test against `SpellRange.dbc` using the
`GetMinMaxRange` law (`state.rs:106-148`).

**(e) Keybind activation.** In `ClientWindow` input, map `Key.Number1..Number0`, `Minus`, `Equal` → slots
1..12 (edge-triggered like the ref's down/up, `ActionBar.xml:194-209`). On press, resolve the slot's
`(Kind, ActionId)` and dispatch: SPELL → the Spells section's `TryCast(spellId, targetGuid)`; ITEM → the
Inventory section's use/equip; auto-attack (spell 6603) → `CMSG_ATTACKSWING`; macro → later. Keybinds
**never place** on the cursor (unlike a mouse click) (`action.rs:202-223`, `action_bar_tests.rs:96-124`).

**(f) Stance bar.** A second ImGui strip of 30 px buttons, shown only when the class has forms, each drawing
icon + checked-ring (active form) + 0.4 grey (not castable) + cooldown; click → `TryCast(formSpellId)` (§6,
`StanceBar.xml`).

**Reuse points:** `Engine/Texture.cs` for icons; `WowSkin` (`Program.Net.cs`, if its source is available) for
ring/border/gold-number styling and the FRIZQT font; `PacketWriter`/`PacketReader` for wire; the existing
`Program.Net.cs` inbound switch for the SMSG handler; `ClientWindow`'s `_held`/`IsDown` for keybinds.
**Depends on (cross-section):** the **Spells** section's `TryCast(spellId, targetGuid)` entry point (steps c-e-f)
and its cooldown/power/range state (step d); the **Inventory** section's item use/equip API and per-item
count + cooldown (steps c-d-e); the **Foundations** section's FrameXML→ImGui tooltip + icon/cooldown-swipe
drawing primitives (step c).

---

## 10. Gotchas / empirical notes

- **Cooldown-swipe math is `(now - start)/duration`, start is absolute on the GetTime clock** — not
  `remaining/duration`. Push the absolute start so a running cooldown re-derives an identical value each frame
  (no diff churn) and a re-arm restarts the sweep; a `(remaining, duration)` scheme aliased a fail-clear+re-arm
  into "unchanged" and vanished the pie (`state.rs:306-312`, `action.rs:76-84`). The read must go cold at
  `start+duration` or a re-feed replays the finish flash (`action.rs:328-334`).
- **GCD and spell cooldown are separate gates, both reason `0x3c`, and the local cast path deliberately does
  NOT test the GCD as a spell cooldown** (`cast_send.rs:77-95`). Sending a GCD-locked cast would draw the
  server's NOT_READY, whose revert clears the *running* GCD — the spam-press vanished-pie bug. GCD-free
  actions (Heroic Strike's queue, Attack, Shoot) must bypass the GCD leg.
- **Colour channels are orthogonal:** power/usability tints the **icon (+ring for OOM)**; range tints the
  **hotkey text**. Don't conflate them — an out-of-range but castable spell keeps a white icon and a red
  hotkey (`ActionBar.xml:224-238` vs `288-306`).
- **`notEnoughMana` is *only* the power verdict.** Every other unusability answers `(false, false)` → the 0.4
  grey. Reagents, forms, stealth, range are NOT "blue" (`usable.rs:84-188`, `state.rs:18-24`).
- **Action-type packing:** `action | (kind << 24)`, low 24 bits are the id, high byte is the kind; `0` = empty
  slot / clear. A stray non-SPELL kind must never show a count (`GetActionCount` returns 0 for non-item —
  `action.rs:228-238`).
- **Paging edge cases:** only the **main** bar re-pages on a bonus flip; the multibars carry a fixed base and
  never move (`multibar_stance_tests.rs:132-146`). Hovering/clicking must use the button's *own* paged id, not
  the main-bar formula — reaching past the per-button base reads "the slot below" (the 2026-07-21 tooltip bug,
  `multibar_stance_tests.rs:341-431`). MultiBarRight/-Left (actions 25..48) are unimplemented in benilla but
  live in the same 120-space for free.
- **Self-cast targeting:** a `flag_word == 0` spell (Ice Armor, Battle Shout) must ship `TARGET_FLAG_SELF`
  with **no guid** — never the current selection, or the server answers "Invalid target"
  (`cast_target.rs:7-11,276-278`). A friendly cast on a non-friend falls back to self only if `autoSelfCast`
  is on (benilla defaults it ON, a *named deviation* — `cast_target.rs:164-175`).
- **Auto-attack borrows the equipped weapon's icon**, not spell 6603's placeholder; unarmed →
  `Interface\Buttons\Spell-Reset`; a weapon swap changes the icon without touching the action table, so the
  icon must refresh every frame (`weapon_icon.rs:28-113`, `feed.rs:312-366`).
- **Item slots name an *entry*, not a position** — clicking must walk the bags to find a copy and then apply
  the two-stage equip-vs-use law; a miss is a silent skip, not a red error (`drain.rs:32-88,181-213`).
- **Stance checked ring never follows the click** — it reverts the CheckButton's own toggle and reflects only
  the server's form byte, so a warrior's active stance stays lit through the click's silent no-op
  (`StanceBar.xml:36-49`, `multibar_stance_tests.rs:304-330`).
- **Empty slots must draw no tint quad.** Running the usable tint over an empty (texture-less) icon paints a
  solid grey plate — gate the per-slot state paint on "has action" (`ActionBar.xml:387-401`,
  `action_bar_tests.rs:598-658`).
- **`crate::cooldowns::Cooldowns` and the app-side `crate::ui_shapeshift` feed/drain are consumed
  dependencies not in this staged slice** — the cooldown *store* (GCD+category+spell merge → `remaining_ms`,
  `duration_ms`, `ui_triple`) and the shapeshift *feed* (which known spells are forms; their
  active/castable/cooldown) are referenced by the files above but their bodies weren't provided; the seam
  shapes (`set_action_state`, `set_shapeshift_forms`, `take_shapeshift_casts`) are fully specified in
  `action.rs` / `shapeshift.rs`.

# Part 7 — Inventory: item model, bags, bank, drag/drop & item tooltips

Porting target: MSUIClient (C#, Silk.NET/OpenGL, ImGui HUD, no FrameXML), WoW 1.12.1 build 5875.
Ground truth: benilla (Rust/Bevy). All benilla cites are repo-relative (`crates/…`); MSUI cites are `MSUIClient/…`.

The single most important fact for this whole section: **in 1.12 the client owns NO item template
database.** There is no client `Item.dbc`. Every item's name/quality/stats/icon/requirements comes
from the SERVER, answered per-entry by a query pair, and cached client-side. `ItemDisplayInfo.dbc`
(which MSUI already reads for equipped-gear models) supplies only the *visual* (model/icon), keyed
by a `displayInfoID` that the item-template answer hands you. Confirmed by the benilla module doc:
"The 1.12 wire carries no item *templates* in descriptors — like unit names, they answer a query
pair" (`crates/benilla-protocol/src/messages/items.rs:3-6`), and by benilla shipping no `Item.dbc`
reader anywhere (only `ItemDisplayInfo`, `ItemSubClass`, `ItemSet`, `ItemSubClassMask` under
`crates/benilla-formats/src/`).

---

## 1. The item DATA model — the query pair (the porting spec)

### 1a. CMSG_ITEM_QUERY_SINGLE (ask) — opcode 0x56 (86)
Body = `entry: u32` + `guid: u64` (12 bytes; guid = 0 for a template-only ask, or the concrete
item's guid when asking about one in hand). `crates/benilla-protocol/src/messages/items.rs:452-457`;
writer `crates/benilla-protocol/src/world/writer/items.rs:22-27`; wire golden
`crates/benilla-protocol/tests/items.rs:14-16` (`item_query(117,0)` = `750000000000000000000000`).

### 1b. SMSG_ITEM_QUERY_SINGLE_RESPONSE (answer) — opcode 0x58 (88)
Parser + exact field order (VERIFIED vmangos `HandleItemQuerySingleOpcode`,
`ItemHandler.cpp:269-415`): `crates/benilla-protocol/src/messages/items.rs:246-447`. Byte-exact
goldens (a full weapon, and a minimal potion proving the all-zero-slots→empty-Vec path):
`crates/benilla-protocol/tests/items.rs:80-289` and `:299-446`.

**Wire layout, in order** (this is the port spec — every field little-endian):

| # | field | type | notes |
|---|---|---|---|
| 0 | entry | u32 | **top bit set ⇒ MISS**: `entry & 0x8000_0000 != 0` → unknown entry, body ends here; real entry is `entry & 0x7FFF_FFFF` (`items.rs:247-250`). Same shape as the creature-query miss. |
| 1 | class | u32 | ItemClass (0 consumable, 2 weapon, 4 armor, …) |
| 2 | subclass | u32 | ItemSubClass (server zeroes it for consumables — `ItemHandler.cpp:300`) |
| 3 | name | cstring | **then THREE empty cstrings** name2..name4 (server always sends 1 real + 3 empty; `items.rs:253-256`) |
| 4 | display_info_id | u32 | **the `ItemDisplayInfo.dbc` key → icon + model** |
| 5 | quality | u32 | 0 poor … 6 artifact |
| 6 | flags | u32 | `ItemPrototypeFlags` (0x2 conjured, unique, no-sell, …) |
| 7 | buy_price | u32 | copper |
| 8 | sell_price | u32 | copper (0 = unsellable) |
| 9 | inventory_type | u32 | INVTYPE_* — equip-slot family (1 head, 13 weapon, 21/22 mh/oh, 18 bag, 24 ammo, …) |
| 10 | allowable_class | **i32** | class bitmask; `-1` = all (signed on purpose) |
| 11 | allowable_race | **i32** | race bitmask; `-1` = all |
| 12 | item_level | u32 | |
| 13 | required_level | u32 | 0 = none |
| 14 | required_skill | u32 | SkillLine.dbc id |
| 15 | required_skill_rank | u32 | |
| 16 | required_spell | u32 | Spell.dbc id |
| 17 | required_honor_rank | u32 | |
| 18 | required_city_rank | u32 | |
| 19 | required_rep_faction | u32 | Faction.dbc id |
| 20 | required_rep_rank | u32 | server sends 0 when faction is 0 (`ItemHandler.cpp:321-322`) |
| 21 | max_count | u32 | account cap (1 ⇒ "Unique") |
| 22 | stackable | u32 | max stack (1 = non-stacking) |
| 23 | container_slots | u32 | nonzero only for bags = slots it grants |
| 24 | **10× ItemStat** | `{type: u32, value: i32}` | benilla keeps only slots where type OR value ≠ 0 (`items.rs:280-287`). ItemModType: 0 mana,1 health,3 agi,4 str,5 int,6 spi,7 stam |
| 25 | **5× Damage** | `{min: f32, max: f32, school: u32}` | block 0 always mirrored to `dmg_min/dmg_max/dmg_type`; benilla drops blocks with `max<=0` (`items.rs:289-311`). school 0 physical,1 Holy…6 Arcane |
| 26 | armor | u32 | |
| 27 | **6× resistances** | i32 each | order `[holy, fire, nature, frost, shadow, arcane]` (armor is its own field above; `items.rs:313-324`) |
| 28 | delay_ms | u32 | weapon speed = delay/1000 |
| 29 | ammo_type | u32 | 0 none, 2 arrow, 3 bullet |
| 30 | ranged_mod_range | f32 | |
| 31 | **5× Spell** | `{spell_id:u32, trigger:u32, charges:i32, cooldown_ms:i32, category:u32, category_cooldown_ms:i32}` | server always writes all 6 words; unresolved-slot sentinel = `0,0,0,-1,0,-1`. benilla keeps blocks with `spell_id≠0`, records block-0's raw `charges` separately, and surfaces the first `trigger==0` block as `use_spell` (`items.rs:330-371`). trigger 0 ON_USE, 1 ON_EQUIP, 2 CHANCE_ON_HIT |
| 32 | bonding | u32 | 0 none,1 BoP,2 BoE,3 on-use,4/5 quest |
| 33 | description | cstring | italic flavor line |
| 34 | page_text | u32 | readable-item text id (0 = not a book) |
| 35 | language_id | u32 | |
| 36 | page_material | u32 | |
| 37 | start_quest | u32 | nonzero ⇒ "This Item Begins a Quest" AND changes the use fork (§3) |
| 38 | lock_id | u32 | Lock.dbc (chest/lockbox) |
| 39 | material | u32 | |
| 40 | sheath | u32 | holster style |
| 41 | random_property | u32 | ItemRandomProperties.dbc (the concrete roll is on the instance) |
| 42 | block | u32 | shield block value |
| 43 | item_set | u32 | ItemSet.dbc id (0 = not a set piece) |
| 44 | max_durability | u32 | 0 = indestructible |
| 45 | area | u32 | AreaTable.dbc (zone-bound) |
| 46 | map | u32 | Map.dbc (map-bound) |
| 47 | bag_family | u32 | accepted-item bitmask (soul/herb bag; 0 = normal) |

The full struct is `ItemInfo` (`crates/benilla-protocol/src/messages/items.rs:29-163`), with
`ItemDamage` (`:193-199`), `ItemSpellEntry` (`:206-224`), `ItemUseSpell` (`:233-242`). Helper
predicates worth porting: `has_finite_charges` (block-0 charges ≠ 0 and ≠ -1; `:170-172`),
`use_spell_index` (block ordinal of first ON_USE — the CMSG_USE_ITEM spell byte; `:178-180`),
`placeable_on_action_bar` (on-use spell OR `inventory_type != 0`; `:186-188`).

### 1c. The client cache — the "ask-once" discipline
`crates/benilla/src/items.rs` is the model store. It holds TWO maps
(`crates/benilla/src/items.rs:42-62`):

- **Templates** `HashMap<u32, Option<ItemInfo>>` keyed by entry. `Items::template(entry, guid, …)`
  (`:101-115`): if unknown, send the query ONCE (deduped via a `pending: HashSet`), return `None`;
  a re-ask for the same entry while in flight is suppressed; **negatives are cached** (`Some(None)`)
  so a bad entry never becomes a query loop (`:139-150`, `:122-124`). Templates survive disconnect
  (static); only instances + in-flight asks clear (`:175-178`).
- **Objects** `HashMap<u64, ObjectFields>` keyed by guid — the item/container *instances* the server
  streamed (our own inventory at login, loot, trade). Fed by create→merge→destroy
  (`:67-90`). This is where per-instance stack count / durability / charges live (§2).

Push half: every landed template is queued to the tooltip UI unprompted (`fresh`, `take_fresh`,
`:169-171`) so the first hover of an already-visible item never misses; a `template_epoch`
counter (`:155-157`) lets a second view re-resolve. This mirrors the real client's
`DBCACHECALLBACK` redisplay. **Port this exact pattern** — a store the UI only fills on a read miss
re-creates the "hover twice to see the tooltip" flake.

---

## 2. Where items LIVE — descriptor slots → guid → template

Two-level indirection: the **player descriptor** names which item *guid* sits in each slot; the
**item object** (streamed separately, keyed by that guid) carries entry + stack/durability/charges;
the **template cache** turns entry → name/quality/icon.

### 2a. Item OBJECT fields (per streamed item instance)
Accessors on `ObjectFields` in `crates/benilla-protocol/src/messages/update_object/fields/player.rs`
(field indices also spelled out raw in `crates/benilla/src/ui_items/mod.rs:683-688`):

- `OBJECT_FIELD_ENTRY` = **3** → `object_entry()` (`player.rs:10-12`)
- `ITEM_FIELD_STACK_COUNT` = **14** → `item_stack_count()` (`:14-16`)
- `ITEM_FIELD_SPELL_CHARGES[0..4]` = **16**+i → `item_spell_charges(i)` (signed; `:23-27`)
- `ITEM_FIELD_CREATOR` = **10** (guid) → "Made by %s" / letter sender (`:38-40`)
- `ITEM_FIELD_FLAGS` = **21** → wrapped/unlocked bits (`:32-34`)
- `ITEM_FIELD_ITEM_TEXT_ID` = **45** → readable letter (`:44-46`)
- `ITEM_FIELD_DURABILITY` = **46**, `MAXDURABILITY` = **47** → `item_durability()`/`item_max_durability()` (`:49-55`)
- `CONTAINER_FIELD_NUM_SLOTS` = **48** → `container_num_slots()` (`:57-59`)
- `CONTAINER_FIELD_SLOT_1 + 2i` = **50**+2i (guid) → `container_slot(i)`, i<36 (`:62-64`)

A bag is just an item object whose `CONTAINER_FIELD_SLOT_*` array holds its contents' guids.

### 2b. PLAYER descriptor slot arrays (which guid is where)
On the self-player's descriptor (`player.rs`):

- `PLAYER_FIELD_INV_SLOT_HEAD + 2i` = **486**+2i (guid) → `player_inv_slot(i)`, i<23. **Slots 0–18 =
  equipped gear (paperdoll), slots 19–22 = the four equipped bags** (`:78-80`).
- `PLAYER_FIELD_PACK_SLOT_1 + 2i` = **532**+2i (guid) → `player_pack_slot(i)`, i<16 = the **16-slot
  backpack** (`:82-84`).
- `PLAYER_FIELD_BANK_SLOT_1 + 2i` (guid) → `player_bank_slot(i)`, i<24 = the **24 generic bank
  slots** (`:86-88`).
- `PLAYER_FIELD_BANK_BAG_SLOT_1 + 2i` (guid) → `player_bank_bag_slot(i)`, i<6 = the **6 bank-bag
  item slots** (`:92-94`). The bag's *contents* stream as an ordinary `CONTAINER_FIELD_SLOT_*` block
  on that bag item.
- `PLAYER_FIELD_VENDORBUYBACK_SLOT_1` (12) + `PLAYER_FIELD_BUYBACK_PRICE_1` — buyback (`:97-108`).
- `PLAYER_VISIBLE_ITEM_<slot>_0` → `player_visible_item_entry(i)`, i<19 = the **public worn item
  ENTRY** other clients render from (`:69-76`) — 12 fields per slot. Useful cross-check / the entry
  source for equipped gear before item objects decode.
- `PLAYER_BYTES_2` byte 2 → `player_bank_bag_slots_purchased()` = how many of the 6 bank-bag slots
  are bought (`:362-369`).

### 2c. The 1.12 bag/slot addressing scheme (wire) — CRITICAL
Two addressing conventions coexist. The **wire** `(bag_index, slot)` used by every item CMSG:
- `BAG_PLAYER_INVENTORY = 255` (= `INVENTORY_SLOT_BAG_0`): with it, `slot` indexes the player's own
  array directly — **equipment 0–18, equipped-bag slots 19–22, backpack 23–38, bank 39–62, bank
  bags 63–68, buyback 69+** (`crates/benilla-protocol/src/messages/items.rs:459-467`).
- A bag's own player-array slot (19–22 for equipped, 63–68 for bank bags) used AS the `bag_index`,
  with `slot` = the 0-based inner slot within that bag.

The **live-API** `(bag, 1-based slot)` used by the UI: bag 0 = backpack, 1–4 = equipped bags,
`-1` = bank vault, 5–10 = bank bags, plus the paperdoll sentinel. The single mapping between them is
`wire_pos()` (`crates/benilla/src/ui_items/mod.rs:102-118`), pinned by tests at `:630-670`:

```
live (0, s)         → wire (255, 23 + s0)      backpack
live (1..4, s)      → wire (18+bag, s0)        equipped bag: its own array slot 19..22 as bag byte
live (-1, s)        → wire (255, 39 + s0)      bank vault
live (5..10, s)     → wire (62+bag, s0)        bank bag: its own array slot 63..68 as bag byte
live (DOLL, id)     → wire (255, id-1)         paperdoll: HeadSlot 1→wire0 … Tabard 19→18,
                                               bag icons 20..23→19..22, bank-bag buttons 64..69→63..68
```
Resolution live→guid is `slot_guid()` (`:135-169`) and `slot_guid_count()` (`:175-195`). The
reference inventory *search order* (for "where is a copy of entry X", action bars etc.) is
equipment 0–18 → each equipped bag then its contents → backpack 23–38 → keyring; bank/buyback never
searched — `find_item()` (`:273-332`, tests `:676-813`).

---

## 3. The cursor / drag model (the interaction spec)

### 3a. One payload space
benilla has ONE cursor payload enum — `CursorPayload::{Item, Spell, Action}` — and every surface
(bags, paperdoll, action bar, bank) routes pick-up/place through it. `CursorItem`
(`crates/benilla-ui/src/script/container.rs`, fields visible at `cursor/doll.rs:48-58`): `bag, slot,
item_id, texture, link, quality, count: Option<u32>, bar_placeable, equip_slots: Vec<u8>`. **`count:
None` = whole-stack carry; `Some(n)` = a split carry.** `equip_slots` (the 1-based doll slots this
item fits) rides the payload from pickup so the fit-rule works anywhere.

### 3b. Pick up / place / swap (bags) — `pickup_container_item`
`crates/benilla-ui/src/script/container.rs:197-265`:
- empty cursor + resolved, UNLOCKED slot → pick it up (`count: None`), lock the source, fire
  `ITEM_LOCK_CHANGED`; a locked or unresolved slot refuses (`:198-225`, `:783-793`).
- holding + SAME slot → cancel (`:226-230`).
- holding whole stack + click elsewhere → **queue the move and CLEAR the cursor** — empty,
  same-item (merge), and different-item (swap) all alike. **The displaced item does NOT hop onto the
  cursor** (bag placements are server-authoritative — decision 0218, byte-verified; the wire swaps
  and the server settles both slots). `:231-259`, tests `:639-780`.
- split carry (`Some`) onto empty/same-item → queue split & clear; onto a DIFFERENT item → no-op,
  kept (can't swap a partial stack) (`:241-258`, `:795-868`).
- Spell/Action payload over a bag slot → refused, kept (`:260-263`).

The paperdoll twin is `pickup_inventory_item` (`crates/benilla-ui/src/script/cursor/doll.rs:36-98`),
keyed on the `EQUIPMENT_BAG` sentinel; the fit rule is `held.equip_slots.contains(&id)`; a split
carry can't equip (`:74-92`). Satellites: `cursor_can_go_in_slot` (highlight driver, `:121-128`),
`auto_equip_cursor_item` (frame-level drop → `container_autoequips`, `:141-154`), `use_inventory_item`
(`:162-164`), `is_inventory_item_locked` (`:170-178`).

### 3c. The drag GESTURE
`crates/benilla-ui/src/script/cursor/drag.rs`: arm on mouse-down over a `RegisterForDrag` frame
(`:38-52`), start once past a **4px threshold** (`DRAG_START_THRESHOLD`, `:13`, `:58-78`), resolve on
release (`:82-90`). Click-carry and drag are ONE code path — the XML routes `OnDragStart`/
`OnReceiveDrag` to the same `OnClick("LeftButton")` (BagFrame.xml note at
`crates/benilla/assets/ui/BagFrame.xml:72-77`). A drag released over the world does **nothing** to
the payload (keeps carrying); only a completed *click* over the world triggers the delete flow
(`drag.rs:217-306`). SHIFT+left-click on a stack ≥2 opens the split spinner →
`SplitContainerItem(bag, slot, n)` (`BagFrame.xml:90-103`).

### 3d. The drop → CMSG map (drain, `crates/benilla/src/ui_items/drain.rs`)
This is the interaction-to-wire table to reproduce exactly:

| gesture / context | CMSG | benilla site |
|---|---|---|
| whole-stack move, **both ends wire-255** (backpack↔backpack, doll↔backpack, doll↔doll, backpack→bag-icon) | **CMSG_SWAP_INV_ITEM** `{src_slot, dst_slot}` op 269 | `drain.rs:342-349` |
| whole-stack move, **either end an equipped bag** | **CMSG_SWAP_ITEM** `{dst_bag,dst_slot,src_bag,src_slot}` — **destination FIRST** — op 268 | `drain.rs:350-361` |
| split placement (`count Some`) | **CMSG_SPLIT_ITEM** `{src_bag,src_slot,dst_bag,dst_slot,count}` op 270 | `drain.rs:363-378` |
| delete-confirm accept | **CMSG_DESTROYITEM** `{bag,slot,count,0,0,0}` (count 0 = whole stack) op 273 | `drain.rs:405-435` |
| right-click use, non-equippable | **CMSG_USE_ITEM** `{bag,slot,spellSlot,u16 mask=0}` op 171 | `drain.rs:282-293` |
| right-click, equippable (`inventory_type≠0`, not quest-starter) | **CMSG_AUTOEQUIP_ITEM** `{bag,slot}` op 266 | `drain.rs:264-281` |
| right-click, ammo (INVTYPE 24) | **CMSG_SET_AMMO** `{entry: u32}` | `drain.rs:269-273` |
| right-click, quest-starter (`start_quest≠0`) | **CMSG_QUESTGIVER_QUERY_QUEST** {item guid, quest} | `item_use_command`, `mod.rs:353-373` |
| paperdoll drop (auto-equip) | **CMSG_AUTOEQUIP_ITEM** | `drain.rs:32-70` |
| bank open, click a bank slot | **CMSG_AUTOSTORE_BANK_ITEM** (withdraw) / **CMSG_AUTOBANK_ITEM** (deposit) | `drain.rs:195-210` |
| merchant open, click a bag slot | **CMSG_SELL_ITEM** {vendor, guid, count 0} | `drain.rs:165-181` |
| repair mode | **CMSG_REPAIR_ITEM** {vendor, guid} | `drain.rs:138-157` |

The **equip-vs-use fork is client-side** (`drain.rs:246-294`): resolve the slot's template, and if
`inventory_type != 0` (and no `start_quest`) send AUTOEQUIP, else USE. The one shared use fork
(`item_use_command`, `mod.rs:334-373`) also diverts quest-starters. Writers:
`crates/benilla-protocol/src/world/writer/items.rs`.

### 3e. Optimistic prediction — `pending_item_ops.rs`
`crates/benilla/src/pending_item_ops.rs`: every move/split/destroy send **locks the live `(bag,
slot)` positions it touches** — both ends of a move/split, the one slot of a destroy — baselined on
each slot's `(guid, stack_count)` **at send time** (`add`, `:41-48`). Baseline is `(guid, count)`
NOT guid alone: a partial split-merge or partial destroy leaves the guid unchanged and moves only
the count (`:10-18`, `:180-205`). Clear paths:
- `resolve()` (`:65-79`): the whole entry unlocks the instant ANY of its slots' *current* `(guid,
  count)` differs from baseline (the descriptor field-update stream landed).
- `clear_by_failure(item_guid)` (`:90-110`): a non-zero `SMSG_INVENTORY_CHANGE_FAILURE` clears the
  entry naming that guid, or — guid 0 / unmatched — clears everything (moves are serial, so a
  blanket clear is the safe over-approximation).

A locked slot **dims** (icon vertex color 0.4; `BagFrame.xml:217-223`) and `ITEM_LOCK_CHANGED` fires
so windows repaint. Port this to keep drag/drop responsive without waiting a round-trip, and to
avoid the "stuck-dark slot" bug when a server refusal or a popup-No cancels a pending op.

---

## 4. Bag window — authentic dimensions (BagFrame.xml + container.rs)

Geometry QUOTED from the real 1.12 `ContainerFrame.xml`/`.lua`
(`crates/benilla/assets/ui/BagFrame.xml`):

- **Slot button = 37×37** (`BenillaBagSlotTemplate`, `BagFrame.xml:755-756`).
- Ring = `Interface\Buttons\UI-Quickslot2` **64×64**, centered at `(0, -1)` (`:782+`).
- Stack **count** = `NumberFontNormal`, right-justified, at `BOTTOMRIGHT (-5, 2)`; shown only when
  count > 1 (`:762-766`, paint at `:224-228`).
- **Cooldown** overlay = native Cooldown widget **36×36**, centered `(0,-1)` (`:775-780`), driven by
  `GetContainerItemCooldown` (`(start,duration,enable)` on the GetTime clock;
  `crates/benilla-ui/src/script/container.rs:343-355`).
- **Backpack = 16 slots, 4 columns** (`NUM_CONTAINER_COLUMNS = 4`, `:860`), buttons chain from
  **bottom-right growing up-and-left**: each slot anchors `BOTTOMRIGHT→BOTTOMLEFT` of the previous
  with a **−5px** horizontal gap (`:872-881`); every 4th wraps a row up with `BOTTOMLEFT→TOPRIGHT`
  **+4px** (`:884-885`). Backpack frame ≈ **192×240** (`:810`).
- **Equipped bags 1–4: up to 20 slots (5 rows × 4 cols)** — `BENILLA_BAG_WINDOW_MAX_SLOTS = 20`
  (`:54`; 20 = vanilla's largest bag). Same 37×37 / −5 / +4 chain. Windows snug-fit their stitched
  background to `ceil(size/4)` rows (`BenillaBagWindow_FitBackground`, `:296-351`).
- Slot paint (`BenillaBagSlot_Update`, `:203-234`): icon `SetTexture(info.iconFileID)`; lock →
  dim 0.4 else white; count text; cooldown. The physIndex→game-slot map is `slot = size − physIndex
  + 1` (`:211`) so slot 1 is top-left regardless of fill.

**Quality colour — an authenticity gotcha:** 1.12 bag slots have **NO quality-colored border** on
the icon. `BenillaBagSlot_Update` paints only icon + count + lock-dim + cooldown (`:203-234`) — the
colored slot border is a *later-expansion* feature. Quality color appears only in the item **name /
`|Hitem|` link** and the **tooltip name**, via `quality_color()`
(`crates/benilla/src/ui_items/mod.rs:377-387`): 0 `ff9d9d9d`, 2 `ff1eff00`, 3 `ff0070dd`, 4
`ffa335ee`, 5 `ffff8000`, 6 `ffe6cc80` (1 = white default). For MSUI: do NOT draw a modern quality
ring unless you deliberately want to diverge — port the color into the name text instead.

The `C_Container` data seam benilla feeds (`container.rs:22-77`): per-bag `ContainerState {name,
num_slots, slots: map<slot, ContainerSlot>}`; each `ContainerSlot` carries `texture, count, quality,
item_id, link, locked, equip_slots, bar_placeable, durability(cur,max), cooldown, readable, creator,
flags`. Slot resolve = guid → object (entry/count/durability) → template (ask-once) → icon (via
`ItemDisplayInfo`) — `feed.rs::resolve_slot` (`crates/benilla/src/ui_items/feed.rs:395-480`). Feed
diffs whole bags, fires `BAG_UPDATE(bagID)` per change + one `BAG_UPDATE_DELAYED`
(`feed.rs:702-742`).

---

## 5. Bank (ui_bank.rs + BankFrame.xml)

The vault is **streamed at login like the backpack** — the 24 bank slots + 6 bank bags ride the
PLAYER descriptor; the window only *reveals* them (`crates/benilla/src/ui_bank.rs:6-9`). So the
container feed pushes bank containers unconditionally: `-1` = the 24 vault slots (off
`player_bank_slot`), `5..10` = the bank bags (each an ordinary container object)
(`feed.rs:621-699`).

Authentic layout (`crates/benilla/assets/ui/BankFrame.xml`): main window **384×512** (`:275`);
**24 generic slots = 6 columns × 4 rows**, `TOPLEFT (40, -73)`, **12px** column gap, **−7px** row gap
(`:319-418`) — reusing the exact 37×37 `BenillaBagSlotTemplate` (same click/drag/split/lock/cooldown
chain), parented under a frame with `id="-1"` so `GetParent():GetID()` = BANK_CONTAINER. **6 bank-bag
buttons** below (container ids 5..10; `:424-447`), each opening a popout `ContainerFrame`
(`:559+`).

Purchasable bag slots (`ui_bank.rs`):
- `PurchaseSlot()` → **CMSG_BUY_BANK_SLOT** {banker guid} (`:220-224`).
- **No success packet** — the only confirmation is the `PLAYER_BYTES_2` byte-2 delta
  (`player_bank_bag_slots_purchased`), which fires `PLAYERBANKBAGSLOTS_CHANGED` (`:11-14`, `:200-202`;
  `player.rs:362-369`).
- Next cost from **`BankBagSlotPrices.dbc`** (client data: 10s/1g/10g/25g/50g/100g, then a sentinel;
  `ui_bank.rs:34-38`) — the one item-adjacent client DBC here.
- Failures answer **SMSG_BUY_BANK_SLOT_RESULT only on failure** → red line (`bank_slot_error_text`,
  `:111-119`).
- `SMSG_SHOW_BANK` → banker guid opens the window; there is **no close opcode** — closing is a
  client-side clear (`:60-63`, `:14`). NPC-range auto-close applies (`:71-82`).

---

## 6. Item tooltip CONTENT (item_stats.rs + feed.rs)

(Chrome/borders belong to the shared Foundations section; the CONTENT and the red-line law are
here.) The tooltip renders from `ItemTemplateView`
(`crates/benilla-ui/src/script/item_stats.rs:29-115`) — the wire fields plus app-resolved display
strings — built by `template_view` (`crates/benilla/src/ui_items/feed.rs:92-166`). Lines, in the
order the real builder emits:

- **Name** — quality-colored (`quality_color`).
- **"Unique"/"Unique (N)"** from `max_count` (+ flags 0x2 "Conjured Item").
- Binding line from `bonding` (BoP/BoE/on-use/quest).
- **Damage** "X - Y Damage" + school suffix; **Speed** = `delay_ms/1000`; secondary damage blocks.
- **Armor**, **Block**, **"+N <School> Resistance"** (resistances array).
- **Stat lines** "+N Strength/…"" from `stats` (types 0 mana,1 health,3 agi,4 str,5 int,6 spi,7 stam).
- **Durability N / N** from `max_durability` (real instance shows current/max off object fields).
- **"Requires Level N"** (printed only for N>1) — **red when player level < N**.
- **Requires <skill> (N)** — SkillLine.dbc name (app-resolved) — red if rank short.
- **Requires <spell>** — red if not in spellbook.
- **Requires <Faction> - <Standing>** — Faction.dbc + standing label (`standing_label`,
  `feed.rs:73-84`) — red if rep rank short.
- class/race allowable — red if the player's bit is absent.
- **"N Charge(s)"** — `charges_count` (`feed.rs:63-69`): a template charge of `0` or the `-1`
  consume-on-use sentinel prints **NO line** (food/water/potions); a real pool (e.g. `-5`) prints
  `abs` = "5 Charges".
- **Trigger lines** "Use:/Equip:/Chance on hit:" — green, spell text app-resolved from the spell
  catalog (`spell_triggers`, `feed.rs:154-159`).
- **flavor description** (yellow italic); **"<Right Click to Read>"** when `page_text≠0`.
- **sell price** money row (merchant open) or "No sell price".

**The red-line law** = `item_usable(view, PlayerReqState, knows_spell)` — the client's
`0x5ea930` gate, 9 legs in binary order: level, class mask, race mask, proficiency
(`SMSG_SET_PROFICIENCY` mask, no subclass-alt walk here), skill, spell, honor rank, city rank (always
fails — dead field), reputation (`item_stats.rs:171-223`, tests `:322-416`). `PlayerReqState`
(`:120-144`) is pushed on change from the self-player descriptor + proficiencies + reputations
(`feed_player_req`, `feed.rs:317-387`).

**Subclass slot|type line** — `ItemSubClass.dbc` (`crates/benilla-formats/src/itemsubclass.rs`):
`proficiency_alt` (an alternate proficiency softens which cell reds — e.g. a 2H axe under 1H-axe
proficiency reds the SLOT, not the TYPE; `:71-77`) and `hides_name` (displayFlags bit 0 = the
"Miscellaneous" family: rings/trinkets/shirts never print an armor type; `:153-157`).

**Set bonuses** — `ItemSet.dbc` (`crates/benilla-formats/src/itemsets.rs`): `ItemSetInfo {name,
items[≤17], bonuses: Vec<(threshold, spell)>, required_skill/rank}` (`:24-31`, load `:77-105`). The
SET block (`feed_item_sets`, `feed.rs:175-244`) shows the set name "(owned/total)", each member NAME
(each asked once through the SAME template cache — the real client queries set members), and
threshold bonuses (green, spell $-text), sorted threshold-ascending at print time.

---

## 7. equip_error.rs — SMSG_INVENTORY_CHANGE_FAILURE

Parse (`crates/benilla-protocol/src/messages/items.rs:552-566`): `reason: u8` (`InventoryResult`;
`0` = OK, no tail), then — only when failed — a `u32 required_level` **iff reason == 1**
(`CANT_EQUIP_LEVEL_I`), two item guids (u64 each), and a bag subslot (u8). Returns `(reason,
required_level, item_guid)`. Tests `:624-661`; wire golden `tests/items.rs:26-55`.

The reason→GlobalStrings-key table is the **full build-5875 enum** (VERIFIED vmangos
`ItemDefines.h`) — `crates/benilla/src/ui_items/equip_error.rs:16-76`. Representative codes: 1
`ERR_CANT_EQUIP_LEVEL_I` (its string has a `%d` filled with the packet's required level), 3
`ERR_WRONG_SLOT`, 8 `ERR_PROFICIENCY_NEEDED`, 22 `ERR_SLOT_EMPTY`, **37 STUNNED / 38 DEAD** (note:
NOT the TBC-era 39/40 — on this wire 39/40 are CLIENT_LOCKED_OUT / INTERNAL_BAG_ERROR), **50
`ERR_INV_FULL`** ("Inventory is full."), 34 `ERR_NO_BANK_SLOT`, 63/64 rank/reputation. Code 59 and
anything past the enum have no 1.12 string → hex debug fallback (`:74`, `:96-97`). benilla resolves
the key against the VM's own loaded `GlobalStrings.lua` (`feed.rs:509-518`). **MSUI has no
GlobalStrings VM**, so port this as a `Dictionary<byte,string>` of the enUS strings directly
(the enUS values are pinned by the test at `equip_error.rs:106-135`).

---

## 8. What MSUI has today (verified against staged files)

- **Descriptor codec exists and is reusable, but has ZERO item awareness.**
  `MSUIClient/MSUIClient/Net/ObjectFields.cs` is a faithful port of benilla's `ObjectFields`
  (sparse mask decode, `GetU32/GetI32/GetF32/GetGuid`, `Merge`, `AsCreated` — `ObjectFields.cs:44-85`)
  — but its field-index constants **stop at `PLAYER_BYTES_2 = 194`** (`:35-36`). There are **no**
  `ITEM_FIELD_*`, `INV_SLOT`, `PACK_SLOT`, `BANK_SLOT`, `CONTAINER_FIELD_*` constants or accessors.
- **UpdateObject does not parse any inventory fields** — `MSUIClient/MSUIClient/Net/UpdateObject.cs`
  has no PLAYER/INV/PACK/BANK/ITEM_FIELD/CONTAINER handling at all (grep confirmed empty).
- **The entity store tracks no item objects.** `MSUIClient/MSUIClient/Net/Entities.cs` (`EntityStore`)
  keys `WorldEntity` by guid and handles Create/Values/Movement/OutOfRange for units/GOs — there is
  no item-object create-seed path (`Entities.cs:53-99`). No `Items`-style template/instance store.
- **Equipped gear renders from ROSTER displayIds, not from item entries.**
  `MSUIClient/MSUIClient/Net/Character.cs` carries `CharEnumEquip[19] {DisplayId, InventoryType}`
  from SMSG_CHAR_ENUM (`Character.cs:8, 23, 57-58`) — an `ItemDisplayInfo` id, **not** an item entry,
  so it can't be queried for a name/stats/tooltip.
- **Display resolution exists (models only).** `MSUIClient/MSUIClient/World/Units/CharacterEquipment.cs`
  turns a `DisplayId` → `ItemDisplayInfo` row → 3D models + body-atlas textures for the paperdoll
  (`CharacterEquipment.cs:73-100`, `ResolveSlotTexture` builds `Item\TextureComponents\…\*.blp` at
  `:218-233`). `AttachedItemRenderer.cs` attaches held-item models.
- **The ItemDisplayInfo DBC reader exists but SKIPS the icon field.**
  `MSUIClient/MSUIClient/Formats/DbcReader.cs` has `ItemDisplayInfoTable`
  (`DBFilesClient\ItemDisplayInfo.dbc`, indexed by display id; `:354`) and `ItemDisplayRow`
  (`:143-184`) — but the row captures ModelName1/2, ModelTexture1/2, GeosetGroup, HelmetGeosetVis,
  BodyTextures[0..7], ItemVisualId and **does not read field [5] `m_inventoryIcon`** (the icon
  stringref; noted in the schema comment at `:129` but never stored). So the icon path exists in the
  file but must be captured.
- **No `Item.dbc` reader (correct — 1.12 has none), no item-template query, no item cache, no bag /
  bank / inventory window, no item drag/drop, no item tooltip.** The UI is ImGui-only.
- **A BLP→GPU texture loader exists**: `MSUIClient/MSUIClient/Engine/Texture.cs` (`Texture` class) —
  the reuse point for bag icons.

---

## 9. Port plan for MSUI (ImGui-native), ordered

**(a) Item-template cache + query.** No `DbcReader` needed (server-sourced). Add:
`CMSG_ITEM_QUERY_SINGLE` writer (`{entry:u32, guid:u64}`), a `SMSG_ITEM_QUERY_SINGLE_RESPONSE`
parser that reproduces the §1b table **exactly** (mind: the top-bit miss, the 3 empty name cstrings,
the 10-stat/5-damage/6-resist/5-spell blocks, i32 vs u32 columns), an `ItemInfo` DTO, and an
`ItemCache` with benilla's ask-once discipline: dedupe in-flight asks, **cache negatives**, push
landed templates to any open tooltip (avoid the hover-twice flake). Mirror
`crates/benilla/src/items.rs:101-150`.

**(b) Read inventory descriptor fields into an inventory model.** Extend `ObjectFields.cs` with the
`ITEM_FIELD_*` / `CONTAINER_FIELD_*` / `PLAYER_FIELD_*_SLOT_*` constants + accessors from §2, and add
an **item-object store** (guid → `ObjectFields`) fed by the item CREATE/VALUES/DESTROY stream (extend
`EntityStore` or add a parallel `ItemStore` — benilla keeps items *separate* from world entities:
`crates/benilla/src/items.rs:1-18`). Resolve `(bag, slot) → guid → object.entry → template`; compute
the wire↔live addressing from §2c (`wire_pos`). Reuse `ObjectFields.Merge`/`AsCreated` as-is.

**(c) ImGui bag windows at authentic dims.** Backpack 16 (4 cols), equipped bags ≤20 (4 cols),
37×37 cells, count bottom-right, cooldown sweep, lock-dim at 0.4. Icon = template `display_info_id`
→ `ItemDisplayInfo` row's `m_inventoryIcon` (**field [5], not currently captured — add it to
`ItemDisplayRow`**) → `Interface\Icons\<icon>.blp` → `Engine/Texture.cs`. **Do NOT add a quality
border** (1.12 has none — §4); put quality color in the name/link. Diff-and-repaint per bag.

**(d) An "item on cursor" drag model.** Port the single-payload cursor + optimistic
`PendingItemOps` (both-ends lock, `(guid,count)` baseline, resolve on field-update or clear on
`SMSG_INVENTORY_CHANGE_FAILURE`). Emit the right CMSG per drop using the §3d table — the
**both-ends-255 ⇒ SWAP_INV_ITEM vs either-end-a-bag ⇒ SWAP_ITEM (dest first)** split is the easy
thing to get wrong. Client-side equip-vs-use fork; quest-starter and ammo sub-forks. ImGui makes
this easy: track a `CursorPayload?` and paint the held icon at the mouse each frame; a slot click
calls the same pick/place logic.

**(e) Item tooltip content.** Build the §6 line set from `ItemInfo` + a `PlayerReqState`
(level/class/race/skills/proficiency/rep/honor) for the red-line law (`item_usable`, 9 legs). Load
`ItemSubClass.dbc` (proficiency-alt + hide-name), `ItemSet.dbc` (set block). Coordinate the tooltip
chrome with Foundations; the CONTENT is here.

**(f) Bank.** 24 vault slots (6×4) + 6 bank-bag popouts; `CMSG_BUY_BANK_SLOT` with the
`PLAYER_BYTES_2` byte-2 delta as the sole success signal; `BankBagSlotPrices.dbc` for cost;
`SMSG_SHOW_BANK` open / client-side close; deposit/withdraw via AUTOBANK/AUTOSTORE_BANK.

**Reuse points to name explicitly:** `ObjectFields` codec (`Merge`/`AsCreated`/`GetGuid`);
`Engine/Texture.cs` (BLP→GPU) for icons; `DbcReader.ItemDisplayInfoTable`/`ItemDisplayRow` (extend
with the icon field 5 — the model resolution already used by `CharacterEquipment`); the
`ItemDisplayInfo`→icon join is the same one `CharacterEquipment` already does for models.

---

## 10. Gotchas / empirical notes

1. **Item templates are SERVER-sourced, not a client `Item.dbc`.** This is the whole architecture.
   Everything flows from CMSG_ITEM_QUERY_SINGLE. Don't look for an `Item.dbc` (there is none in 1.12).
   `ItemDisplayInfo.dbc` gives only the icon/model, keyed by the `display_info_id` the query answer
   provides.
2. **The miss encoding** is the lone `entry | 0x8000_0000` u32 — you must mask the top bit and cache
   a NEGATIVE, or a bad entry loops forever (`items.rs:247-250`, `crates/benilla/src/items.rs:122-124`).
3. **Bag/slot addressing is dual and asymmetric.** Wire uses `bag_index=255` for the player's own
   grid (equipment 0–18, bags 19–22, backpack 23–38, bank 39–62, bank bags 63–68) OR a bag's own
   player-array slot (19–22 / 63–68) as the bag byte with a 0-based inner slot. The UI uses live ids
   (0 backpack, 1–4 bags, −1 bank, 5–10 bank bags, doll sentinel). Get `wire_pos` right or every
   move goes to the wrong slot (`crates/benilla/src/ui_items/mod.rs:102-118`).
4. **Drag CMSG selection is not one opcode.** Same intent, three swap opcodes by addressable space:
   SWAP_INV_ITEM (both player-grid), SWAP_ITEM (either end a bag — **destination pair FIRST** in the
   body), SPLIT_ITEM (partial). Plus AUTOEQUIP for equippables, USE for consumables, SET_AMMO for
   ammo, QUESTGIVER_QUERY for quest-starters, DESTROYITEM for deletes. The equip-vs-use decision is
   **client-side** off the template's `inventory_type`.
5. **Placements are server-authoritative and CLEAR the cursor** (no hop of the displaced item) —
   decision 0218, byte-verified. Only the ACTION BAR hops (it's client-authoritative)
   (`container.rs:177-190`, `cursor/bar.rs:1-15`). Getting this wrong makes bag swaps feel like an
   endless juggle.
6. **Optimistic lock baseline is `(guid, count)`, not guid alone** — a partial split-merge/destroy
   moves only the count. Miss this and those slots stay locked (dark) forever
   (`pending_item_ops.rs:10-18`, `:180-205`).
7. **`SMSG_INVENTORY_CHANGE_FAILURE` has a conditional field**: the `u32 required_level` is present
   **only for reason 1**. The 5875 reason enum is NOT the TBC one — 37/38 = stunned/dead, 50 =
   inventory full (`equip_error.rs`, `items.rs:552-566`).
8. **Split & destroy need confirmations that don't hit the wire until accepted.** Split opens a
   spinner (SHIFT+click, ≥2 stack) → `SplitContainerItem` puts a `count: Some(n)` carry on the
   cursor; the CMSG_SPLIT_ITEM only sends when the carry is placed. A world-drop of a held item shows
   a `DELETE_ITEM_CONFIRM` popup; only "Yes" sends CMSG_DESTROYITEM (count 0 = whole stack). A drag
   released over the world does NOTHING (keeps carrying) — only a completed *click* over the world
   triggers delete (`drag.rs:217-306`, `delete_item_tests.rs`).
9. **Charges tooltip gate**: template charge `0` or `-1` (consume-on-use) prints NO "Charges" line —
   food/water/potions carry `-1` and must show nothing; a real pool (`-5`) shows `abs`=5
   (`feed.rs:63-69`).
10. **The bank vault streams at login** like the backpack — it's descriptor data, not something the
    bank window fetches. The window only reveals it. A bank-slot purchase has **no success packet**;
    the `PLAYER_BYTES_2` byte-2 delta is the only confirmation (`ui_bank.rs:6-14`).
11. **No quality border on 1.12 bag slots** — a modern-WoW instinct that would be a visible
    anachronism (§4). Quality lives in the name color only.
12. **Equipped-gear tooltips need the item ENTRY, which the roster does not give you.** The roster's
    `CharEnumEquip` is a `displayId`, not an entry. To show real stats/tooltips on worn gear the
    Character page must read the worn ENTRY from either `PLAYER_VISIBLE_ITEM_<slot>` (public entry,
    `player.rs:69-76`) or the `INV_SLOT` 0–18 item guids → item objects → entry, then query the
    template — i.e. it depends on this section's pipeline (see the shared-model note).

---

## Note for the Character-page (paperdoll) section — the SHARED item model

The Character page and this section must share ONE item model. Concretely, the shared pieces are:

1. **The item-template cache** (entry → `ItemInfo`) built in step 9(a), with the ask-once discipline.
   Both bag hover tooltips and paperdoll-slot hover tooltips resolve through it.
2. **The item-tooltip CONTENT builder** (§6, `ItemTemplateView` + `item_usable` red-line law). A
   hovered equipped item and a hovered bag item render the *same* tooltip — build it once.
3. **The `display_info_id` → icon (`ItemDisplayInfo` field 5 → BLP → `Texture`) resolution** — the
   same join `CharacterEquipment` already does for models; extend `ItemDisplayRow` to also capture
   the icon field, and both sections use it.

The coordination point: **the paperdoll currently renders from roster `displayId`s and has no item
ENTRY**, so it cannot show stats/tooltips today. Once this section lands (b) reading `INV_SLOT` 0–18
guids → item objects → entries (or the public `PLAYER_VISIBLE_ITEM_<slot>` entries), the Character
page should switch its equipped-slot *tooltips/stats* to that entry-based path and reuse this
section's template cache + tooltip builder — while keeping its existing `displayId`-based 3D model
rendering. Equip/unequip drag from a bag onto a paperdoll slot is OWNED here (the
`EQUIPMENT_BAG`/doll cursor arm, §3b) and lands on the same wire map; the Character page only needs
to expose its 19 slot rects as drop targets and call the shared pick/place.

### Uncertainties / caveats
- **Opcode numbers**: benilla's message docs name CMSG_USE_ITEM=171, AUTOEQUIP=266,
  AUTOSTORE_BAG=267, SWAP_ITEM=268, SWAP_INV_ITEM=269, SPLIT_ITEM=270, DESTROYITEM=273, and
  item-query 86/88 (`messages/items.rs`). Cross-check these against MSUI's own `Net/Opcodes.cs`
  numbering before wiring (I did not verify MSUI's opcode table covers them).
- **CMSG_SET_AMMO opcode**: benilla's comment is self-contradictory (module doc cites `0x268`=616 in
  one place; note it collides numerically with SWAP_ITEM's 268 decimal — they're different opcodes,
  the ammo one is hex). Confirm the concrete value before shipping ammo (`items.rs:538-546`).
- **AUTOBANK vs AUTOSTORE_BANK direction** is marked INFERRED in benilla (either lands correctly
  server-side; `drain.rs:189-192`).
- **Bank-bag / keyring counts beyond the 6 bank bags** aren't modeled (benilla ships no keyring).
- MSUI's `ItemDisplayRow` icon field (index 5) needs verification against a real record — benilla's
  `ItemDisplayInfo` schema treats field 5 as the icon string and MSUI's own comment agrees, but MSUI
  never reads it, so it's untested there.

# Part 8 — Character page, skills & talents

Scope: the three windows benilla renders as `BenillaCharacterFrame` (paperdoll page +
`BenillaSkillFrame` sub-page), `BenillaTalentFrame`, and the inspect twin `InspectFrame`.
Ground truth is benilla's transcription of the authentic 1.12.1 (build 5875) FrameXML.
Target is MSUI (ImGui, no FrameXML). Every window here is **greenfield** in MSUI.

Citations: benilla paths are repo-relative (`crates/…`); MSUI paths are `MSUIClient/…`.

> Field-index caveat (read first). benilla splits its descriptor map: `fields/player.rs` +
> `fields/unit.rs` (staged) hold the **named accessors and the relative packing/offset logic**;
> the **absolute wire indices** live in a sibling `mod.rs` that was *not* staged (every accessor
> doc says "`mod.rs` holds the indices + codec", e.g. `crates/benilla-protocol/src/messages/update_object/fields/player.rs:2`).
> So every offset/packing claim below is cited to a real staged line; the absolute base index
> (e.g. "`UNIT_FIELD_STAT0` = ?") must be lifted from vmangos `UpdateFields_1_12_1.h` (build 5875) —
> the same source both benilla's `mod.rs` and MSUI's `ObjectFields.cs` already cite
> (`MSUIClient/MSUIClient/Net/ObjectFields.cs:5-6`). MSUI's `ObjectFields.cs` today defines only
> a handful of absolute indices (health 22, maxhealth 28, level 34, bytes0 36, playerbytes 193,
> playerbytes2 194 — `ObjectFields.cs:16-36`); **none** of the stat/skill/AP/resistance/percentage
> fields exist there yet. The port adds them.

---

## 1. CHARACTER PAGE — the paperdoll

### 1a. Equipment slot set, order, and on-screen positions

The paperdoll page `BenillaPaperDollFrame` is **384×512** at TOPLEFT of the character frame
(`crates/benilla/assets/ui/CharacterFrame.xml:1294-1296`); background art is four quadrant slabs
`Interface\PaperDollInfoFrame\UI-Character-CharacterTab-{L1,R1,BottomLeft,BottomRight}`
(256/128-wide, `CharacterFrame.xml:1300-1317`). Each slot button inherits
`BenillaPaperDollItemSlotTemplate`, **37×37** (`CharacterFrame.xml:1123`).

There are **two independent numberings** — do not conflate them:

- **Wire `EQUIPMENT_SLOT_*` (0-based)**, the `PLAYER_FIELD_INV_SLOT_HEAD + 2i` guid array index
  (`fields/player.rs:77-80`, `player_inv_slot(i)`, `i<23`). Order: Head 0, Neck 1, Shoulder 2,
  Shirt 3, Chest 4, Waist 5, Legs 6, Feet 7, Wrist 8, Hands 9, Finger0 10, Finger1 11, Trinket0 12,
  Trinket1 13, Back 14, MainHand 15, OffHand 16, Ranged 17, Tabard 18 (+ equipped bags 19-22).
  ui_char.rs pins the three that matter for combat: `SLOT_MAIN_HAND=15, SLOT_OFF_HAND=16,
  SLOT_RANGED=17` (`crates/benilla/src/ui_char.rs:52-54`).
- **Live-API `GetInventorySlotInfo` id (1-based, ammo=0)**, the id the Lua sees. It is exactly
  `wire index + 1`, with ammo at 0. The whole 24-row table (name, id, empty-slot art suffix) is
  transcribed byte-exact from `PaperDollItemFrame.dbc` in `SLOT_INFO`
  (`crates/benilla-ui/src/script/char_stats.rs:218-243`): AmmoSlot 0, HeadSlot 1 … TabardSlot 19,
  Bag0Slot..Bag3Slot 20-23. **Oddballs** (`char_stats.rs:214-217`, 234/219): `BackSlot` shows the
  **Chest** empty-art, `AmmoSlot` shows the **Ranged** empty-art (their `SlotTexture` points at the
  other row's string). `GetInventorySlotInfo(name)` returns `(id, "Interface\Paperdoll\UI-PaperDoll-Slot-<art>", false)`
  — checkRelic is always false in vanilla (`char_stats.rs:688-708`).

**On-screen layout is hand-authored per slot and NOT in either numeric order**
(`CharacterFrame.xml:1661-1739`) — this is a porting gotcha. The three visual groups:

| group | anchor origin | chain | slots in visual order (top→bottom) |
|---|---|---|---|
| left column | TOPLEFT (21, −74) | BOTTOMLEFT (0, −4) | Head, Neck, Shoulder, **Back**, Chest, **Shirt**, **Tabard**, Wrist |
| right column | TOPLEFT (305, −74) | BOTTOMLEFT (0, −4) | Hands, Waist, Legs, Feet, Finger0, Finger1, Trinket0, Trinket1 |
| weapon row | page BOTTOMLEFT (122, 127) | TOPRIGHT (+5, 0) | MainHand, SecondaryHand(OffHand), Ranged |

Ammo slot `BenillaCharacterAmmoSlot` is **27×27**, NOT the shared template (own pouch art), pinned
LEFT of the Ranged slot's RIGHT (+15, 0) (`CharacterFrame.xml:1741-1789`). Note the left column
interleaves Back(14)/Shirt(3)/Tabard(18) among Head/Neck/Shoulder/Chest/Wrist — visual, not index.

### 1b. The 3D model inset (reuse MSUI CharacterRenderer via render-to-texture)

In benilla the model pane `BenillaCharacterModelFrame` is **233×224** at TOPLEFT (65, −78)
(`CharacterFrame.xml:1327-1332`). benilla ships it as a **booth-texture stand-in, not a live
`<Model>`/`<PlayerModel>`** — a plain `<Frame>` sampling a "paper-doll booth" render-to-texture bake
(`CharacterFrame.xml:78-81, 1329`). Two rotate buttons (35×35) at the pane's TOPLEFT drive yaw
(`CharacterFrame.xml:1347-1368`); each click nudges 2×0.03 rad; the yaw is written by
`BenillaPaperDollModel_SetFacing(radians)` → `UiScript::paperdoll_yaw()` (persistent scalar, sampled
each frame, `char_stats.rs:341-343, 710-722`), and ui_char.rs mirrors it onto the booth
(`ui_char.rs:590-591`). Default facing 0.61 rad.

**MSUI reuse:** `MSUIClient/MSUIClient/World/Units/CharacterRenderer.cs` already renders the player's
skinned M2 with equipment; `CharacterEquipment.cs` resolves equipped pieces
(`Piece{Name,DisplayId,InventoryType,Row}`, keyed by vanilla InventoryType constants Head=1 …
MainHand=21/OffHand=22, `MSUIClient/MSUIClient/World/Units/CharacterEquipment.cs:33-51`). The
paperdoll inset is a **render-to-texture of that renderer** posed with a fixed camera + the yaw
scalar. **This is the dependency on the Foundations section** (portrait/paperdoll RTT widget); this
section owns only the framing (233×224), the two rotate buttons, and the yaw.

### 1c. How equipped items map to slots

self-feed path (`ui_char.rs::slot_view`, `ui_char.rs:396-478`): `player_inv_slot(slot0)` guid →
`Items::object(guid)` → `object_entry()` (the item id) → ask-once `ITEM_QUERY` template →
icon/name/quality/link/durability/flags. Icon resolves through `ItemDisplays` catalog
(display_info_id → `Interface\Icons\…`). The 24-wide snapshot is built in `inventory_slots`
(`ui_char.rs:514-573`): `[0]`=ammo (`PLAYER_AMMO_ID`, count bag-summed, `ui_char.rs:483-508`),
`[1..=19]`=equipment, `[20..=23]`=equipped-bag icons. Live durability `(cur,max)` and
`ITEM_FIELD_FLAGS` bits (0x08 wrapped, 0x10 force-red) drive the broken/alert tint
(`char_stats.rs:257-296`).

---

## 2. THE STAT SPEC — the port's stat table

Data flows: `ui_char.rs::combat_stats` reads the self descriptor into a `PlayerCombatStats`
snapshot (`ui_char.rs:311-387`); `char_stats.rs` exposes it as the ref Lua globals; the transcribed
`CharacterFrame.xml` Lua formats each line. **All stat fields are OWNER_ONLY/PRIVATE** — any
non-`"player"` token serves zeros (`char_stats.rs:5-9, 349-361`). Buff decomposition is the ref's
inverse math: `base = effective − posBuff − negBuff`; negBuff arrives **negative-or-zero**
(`char_stats.rs:11-16`).

### The five bindings' return shapes (char_stats.rs) and their wire fields

| paperdoll line | Lua binding (char_stats.rs) | wire field(s) (accessor) | snapshot fill (ui_char.rs) |
|---|---|---|---|
| 5 attributes Str/Agi/Sta/Int/Spi | `UnitStat(player,1..5)` →(base,eff,pos,neg) `:407-426` | `UNIT_FIELD_STAT0+i` (`unit.rs:259`); `PLAYER_FIELD_POSSTAT0+i` (`player.rs:211`, f32); `PLAYER_FIELD_NEGSTAT0+i` (`player.rs:216`, ≤0) | `:319-326` |
| 5 resistances | `UnitResistance(player,school)` →(base,res,pos,neg) `:430-449` | `UNIT_FIELD_RESISTANCES+school` (`unit.rs:264`, [0]=armor); `PLAYER_FIELD_RESISTANCEBUFFMODSPOSITIVE/NEGATIVE+school` (`player.rs:221/227`) | `:327-334` |
| Armor | `UnitArmor(player)` →(base,effArmor,armor,pos,neg) `:453-467` | resistance school 0 | `:331` |
| melee dmg/DPS/speed | `UnitDamage` `:474-488`, `UnitAttackSpeed` `:494-504` | `UNIT_FIELD_MINDAMAGE/MAXDAMAGE` (`unit.rs:303/307`), `MINOFFHANDDAMAGE/MAXOFFHANDDAMAGE` (`unit.rs:312/316`), `PLAYER_FIELD_MOD_DAMAGE_DONE_POS/NEG[0]` (`player.rs:233/238`), `_PCT[0]` (`player.rs:245`, def 1.0), `UNIT_FIELD_BASEATTACKTIME[0..1]` (`unit.rs:329`) | `:362-373` |
| melee AP | `UnitAttackPower` →(base,pos,neg) `:509-519` | `UNIT_FIELD_ATTACK_POWER` (`unit.rs:268`) + `_ATTACK_POWER_MODS` split i16 (`unit.rs:274`) | `:336,374-376` |
| Attack (weapon skill) | `UnitAttackBothHands` →(value,mod) `:539-548` | equipped MH weapon's `PLAYER_SKILL_INFO` (value, temp+perm) — see §4 packing | `:349,383` |
| ranged attack/dmg/AP | `UnitRangedAttack` `:552-561`, `UnitRangedDamage` `:567-580`, `UnitRangedAttackPower` `:523-533` | `UNIT_FIELD_RANGED_ATTACK_POWER`(+mods) (`unit.rs:286/291`), `MINRANGEDDAMAGE/MAXRANGEDDAMAGE` (`unit.rs:320/324`), `RANGEDATTACKTIME` (`unit.rs:333`), ranged weapon skill pair | `:377-384` |
| wand gate | `HasWandEquipped()` `:585-593` | ranged item subclass==19 (`ui_char.rs:345-347`) | — |

### The formulas (transcribed ref Lua, CharacterFrame.xml)

- **Constants** (`CharacterFrame.xml:225-230`): `BENILLA_NUM_STATS=5`, `BENILLA_NUM_RESISTANCE_TYPES=5`,
  `BENILLA_ATTACK_POWER_MAGIC_NUMBER=14`, and the resistance display order
  `BENILLA_MAGICRES_IDS = {6, 2, 3, 4, 5}` = **Arcane, Fire, Nature, Frost, Shadow** (school indices).
- **Attributes** `SetStats` (`:419-453`): print `effectiveStat`; green if `posBuff>0`, red if
  `negBuff<0`; tooltip shows `base (=eff−pos−neg)` with the `+pos`/`neg` deltas.
- **Resistances** `SetResistances` (`:458-502`): print `resistance`; **red if `abs(neg)>pos`, plain
  if equal, green if `abs(neg)<pos`** (`:466-472`) — note this is a magnitude compare, not a sign
  test. Rating bucket = `resistance / max(level,20)`: >5 Excellent, >3.75 VeryGood, >2.5 Good,
  >1.25 Fair, >0 Poor, else None (`:483-498`).
- **Armor** `SetArmor` (`:507-518`): `reduction = effectiveArmor / (85*level + 400)`;
  `pct = 100 * (reduction/(reduction+1))` (`:515-516`). Note ref passes `base` (not `armor`) to the
  formatter — transcribed verbatim, not "fixed" (`:504-506`).
- **Damage/DPS** `SetDamage` (`:521-594`): unwind percent/bonus →
  `minDamage=(min/percent)−physPos−physNeg`; `base=(min+max)*0.5`;
  `full=(base+physPos+physNeg)*percent`; **`DPS = max(full,1)/speed`** (`:531-536`). Offhand mirrors
  when `UnitAttackSpeed` returns a second value (`:569-593`); offhand gate is an offhand *weapon*
  (`ui_char.rs:342-344`, class 2 — a shield doesn't count).
- **Attack Power** `SetAttackPower` (`:597-606`): value `= base`; subtext `(base+pos+neg)/14` = the
  AP→bonus-DPS contribution (`:605`). Ranged AP identical (`:733-754`).
- **Weapon skill** `SetAttackBothHands` (`:611-630`): print `value+mod`, green/red on mod sign.
- **Ranged** (`:637-754`): if `GetInventoryItemTexture("player",18)` is nil ⇒ every ranged line is
  `NOT_APPLICABLE` ("N/A") (`:645-652`); if `HasWandEquipped()` ⇒ ranged-AP shows `"--"` (`:745-748`).

### Not on the 1.12 sheet (empirical — important)

- **health/mana/regen are NOT on the paperdoll.** The 1.12 sheet shows only the above (Level line,
  5 attributes, Armor, Damage/Speed, AP, weapon-skill Attack, the 3 Ranged lines, 5 resistances).
  Health/mana are `UNIT_FIELD_HEALTH/MAXHEALTH` (`unit.rs:45/49`, already `ObjectFields.Health/MaxHealth`
  in MSUI) and `UNIT_FIELD_POWER1..5/MAXPOWER1..5` (`unit.rs:159/163`); they live on the unit frame,
  and there is **no regen field** shown anywhere.
- **crit / hit / defense / dodge / parry / block are NOT on the 1.12 paperdoll.** grep of
  `CharacterFrame.xml` finds no dodge/parry/block/defense/crit wiring. The fields *exist* and are
  decoded — `PLAYER_CRIT_PERCENTAGE` (`player.rs:206`), `PLAYER_DODGE_PERCENTAGE` (`:197`),
  `PLAYER_PARRY_PERCENTAGE` (`:201`), `PLAYER_BLOCK_PERCENTAGE` (`:193`), all already floats-as-percent
  — but their consumer in 1.12 is the **spell tooltip "chance to X" lines**, not the character sheet
  (`player.rs:190-208`). The Base-Stats/Melee/Ranged/Defenses tab set is a 2.0+ feature; do not build it.

---

## 3. Durability, title, money, played time on the sheet

- **Durability** is a *separate* frame `DurabilityFrame`, **60×65**, parented to UIParent (not the
  char frame), `hidden="false"` (`crates/benilla/assets/ui/DurabilityFrame.xml:109-114`). It is the
  "armor-guy" silhouette with per-region textures from `Interface\Durability\UI-Durability-Icons`
  (Head 18×22, Shoulders 48×22, Chest 20×22, Wrists, … `DurabilityFrame.xml:122-160`). Fed by
  `GetInventoryAlertStatus(1..11)` (`DurabilityFrame.xml:11, 74`; binding `char_stats.rs:660-670`).
  Alert regions map to live-slot ids `ALERT_SLOTS = [1,3,5,6,7,8,9,10,16,17,18, 0]` = Head, Shoulders,
  Chest, Waist, Legs, Feet, Wrists, Hands, Weapon, Shield, Ranged, +ammo (`char_stats.rs:245-251`).
  Status enum (`char_stats.rs:264-296`): 0 none, **3 damaged = 1..5 points left (absolute, not %)** or
  low-ammo ≤20, **4 broken = durability 0 (max>0) or the 0x10 force-red bit**; wrapped (0x08) never
  alerts. Statuses 1/2 (temp-enchant) are unfed and the 1.12 FrameXML disables their colors anyway.
- **Title / guild / PVP-rank: NOT surfaced.** All three are named deferrals in benilla —
  `CharacterTitleText` and `CharacterGuildText` are dropped, `UnitPVPName` falls back to plain
  `UnitName` (guild needs `CMSG_GUILD_QUERY`; rank needs the honor arc) (`CharacterFrame.xml:41-43,
  388, 1235`). CharTitles.dbc is a TBC+ concept; there is no title on the 1.12 sheet.
- **Money: NOT on the character sheet in 1.12.** grep finds no money/GetMoney on `CharacterFrame.xml`.
  The purse (`PLAYER_FIELD_COINAGE`, `player.rs:111` `player_money()`) renders on the backpack/bag bar,
  owned by the Inventory section — do not add a money row here.
- **Played time: NOT on the sheet.** It is the `/played` chat command (`CMSG_PLAYED_TIME`), never a
  paperdoll field. Out of scope for this window.

---

## 4. SKILLS

### 4a. Where skills come from — the `PLAYER_SKILL_INFO` packing (the key detail)

One decoded skill slot is `player_skill(slot)`, `slot < PLAYER_SKILL_SLOTS`
(`fields/player.rs:253-269`). The packing — **three dwords per slot, each split into two u16s**:

```
base = FIELD_PLAYER_SKILL_INFO_1_1 + 3*slot          (player.rs:257)
  dword base    : (skill_id : u16, step  : u16)      get_u16_pair  (player.rs:258)
  dword base+1  : (value    : u16, max   : u16)                    (player.rs:259)
  dword base+2  : (temp_bonus: i16, perm_bonus: i16)               (player.rs:260)
```

So each known line is `(id, step, value, max, tempBonus, permBonus)`. A slot with `skill_id==0` is
empty. Absent value/max/bonus dwords default to 0 (server writes id+value together on `SetSkill`,
`player.rs:248-252`). `PLAYER_SKILL_SLOTS` is the slot count (re-exported from
`benilla_protocol::messages`, `ui_char.rs:36`) — canonically 128 slots in 1.12 (verify against
`mod.rs`). The **displayed modifier is `tempBonus + permBonus`** (`ui_char.rs:233`,
`char_stats.rs::skill_pair` `ui_char.rs:295-307`).

There is **no skill-up message on the wire** — skills mutate only as `PLAYER_SKILL_INFO` descriptor
deltas; benilla diff-watches its own block to synthesize the "Your skill in %s has increased to %d."
and "You have gained the %s skill." chat lines (`ui_char.rs:92-180`).

### 4b. Names / categories / icons — SkillLine.dbc

`crates/benilla-formats/src/skill_lines.rs` loads three DBCs (formats verified vs vmangos
`DBCStructure.h`/`DBCfmt.h`):
- **`SkillLine.dbc`** (`SkillLinefmt="nixssssssssxxxxxxxxxxi"`, 22 fields/88B, `skill_lines.rs:13-19`):
  id 0, **categoryId col 1**, **NameEnUs col 3** (`:73`), **DescEnUs col 12** (`:74`, the detail-pane
  body), **spellIcon col 21** → `SpellIcon.dbc` → `Interface\Icons\…` (`:75, 357-359`). Yields
  `SkillLineInfo{name, category_id, icon, description}` (`:112-127`).
- **`SkillLineCategory.dbc`** (8 rows, 11 fields/44B, `:48-54`): id→(name, displayOrder). The pane's
  headers and group order: **Class Skills (7, order 2), Professions (11, 3), Secondary Skills (9, 4),
  Weapon Skills (6, 5), Armor (8, 6), Languages (10, 7)**; Attributes (5, 1) never carries player
  rows; **Not Displayed (12, 8)** is the hide bucket (`SKILL_CATEGORY_NOT_DISPLAYED=12`, `:88-89`).
- **`SkillRaceClassInfo.dbc`** (`:34-46`): the unlearn gate — `abandonable(line,race,class)` is true
  iff the admitting row's `flags & 0x20 (SKILL_FLAG_UNLEARNABLE)` (`:104-108, 223-226`). Real-data
  split: primary professions droppable, secondary skills/weapons/languages/class-lines not
  (`:606-628`). (`SKILL_FLAG_DISPLAY_SORTED 0x80` routes spellbook tabs to General — spellbook's
  concern, not the skills pane.)

`ui_char.rs::feed_skills` (`:191-249`) builds one flat `SkillEntry` per known line
(`skill_id, name, value, max, modifier, category_id, category_name, category_order, description,
abandonable`), skipping the Not-Displayed category and any line with no `SkillLine.dbc` row.

### 4c. How SkillFrame groups them + authentic dims

The **engine** groups/sorts (`crates/benilla-ui/src/script/skills.rs::build_groups`, `:159-183`):
group by category, **groups sorted by `category_order` asc (category_id tiebreak)**, **entries
sorted by collated name within group**, one header row per non-empty group, each group starts
expanded (`skills.rs:12-24`). Visible rows = headers always + entries of expanded groups
(`rows()`, `:187-198`). API surface: `GetNumSkillLines()`, `GetSkillLineInfo(i)` (13-tuple: header
rows vs entry rows, `skills.rs:26-29`), `ExpandSkillHeader/CollapseSkillHeader(i)` (0=all,
`:219-244`), `SetSelectedSkill/GetSelectedSkill` (persists by skill id across re-push, `:246-262`),
`AbandonSkill` → drain → `CMSG_UNLEARN_SKILL` (0x202, one u32 skill id, no ack — the removal comes
back as a `PLAYER_SKILL_INFO` delta; `crates/benilla-protocol/src/messages/skills.rs:1-13`,
`world/writer/skills.rs:14-24`, drained `ui_char.rs:256-264`). `numTempPoints`/training points are
always 0 (dead in 1.12, `skills.rs:29`).

Authentic dims (`crates/benilla/assets/ui/SkillFrame.xml`): page 384×512 (`:438`); **12 displayed
rows** (`SKILLFRAME_SKILLS_DISPLAYED=12`), **18px pitch**. Rank bar template
`BenillaSkillRankTemplate` **271×15** StatusBar (`:385-386`), SkillName FontString at LEFT (+6,1),
SkillRank number 128 wide at name-RIGHT+13 (`:389-394`); bar color **blue (0,0,1,0.5)** for a skill
row, **transparent (0,0,0,0)** for a header (`:123-126`). Header template `BenillaSkillHeaderTemplate`
**285×14** with a 16×16 +/− collapse glyph (`:418-427`). Row instances: 12 rank bars first at
TOPLEFT(38,−79) chaining (0,−3), 12 header labels at (22,−86) chaining (0,−18) (`:543-576`).
"ALL" collapse-all tab 54×32 at (70,−49) (`:509-528`); selection highlight 281×15
`UI-Listbox-Highlight2` (`:533-541`); faux-scroll frame 296×216 on the right (`:582-585`); minimal
detail pane (selected line's own bar + name + red circle-slash unlearn button) at the bottom
(`:587-599`).

---

## 5. TALENTS — the data model

### 5a. Talent.dbc + TalentTab.dbc (`crates/benilla-formats/src/talents.rs`)

Formats verified vs vmangos `DBCStructure.h`/`DBCfmt.h` (`talents.rs:6-33`):

- **`Talent.dbc`** (`"niiiiiiiixxxxixxixxxi"`, 21 fields/84B): id 0, **tabId col 1**, **row(tier,
  0-based) col 2**, **col(0-based) col 3**, **rankSpell[5] cols 4-8** (the spell taught per rank; a
  talent with n ranks fills the first n), **prereqTalent col 13**, **prereqRank(0-based) col 16**,
  **flags col 19** (bit0 = `isExceptional`), **requiredSpell col 20** (`talents.rs:10-17, 46-54`).
  Struct `Talent{id, tab, row, col, ranks[5], prereq_talent, prereq_rank, required_spell,
  exceptional}` (`:66-87`); `max_rank()` = count of populated rank spells (`:89-94`).
- **`TalentTab.dbc`** (`"nxxxxxxxxxxxiix"`, 15 fields/60B): id 0, **NameEnUs col 1**, **raceMask col
  11**, **classMask col 12**, **orderIndex col 13 — never read by the client** (dead data;
  file order is the law), **backgroundFile col 14** (the `Interface\TalentFrame\<base>-` art base,
  e.g. "MageArcane") (`talents.rs:19-25, 56-60`). Struct `TalentTabInfo{id, name, race_mask,
  class_mask, background}` (`:96-107`).

**Ordering is the law** (byte-verified, `talents.rs:27-33`): a class's tabs = the rows matching
**both** raceMask AND classMask, in **raw file order** (`tabs_for_class`, `:121-131` — bit
`1<<(race−1)` & `1<<(class−1)`); a tab's talents are indexed in **native DBC row order**, never
re-sorted by (tier,col) (`talents_in_tab`, `:135-137`). Shipped data happens to author tabs in
(row,col) order (27/27), a coincidence, not a rule. Every class has exactly **3 tabs**
(`:267-271`); grid is **≤8 tiers × 4 columns**; prereqs always resolve **within the same tab**
(`:289-326`).

### 5b. Current ranks (from known spells) + points available

benilla holds **no talent knowledge** on the wire; the app derives it (`crates/benilla/src/ui_talent.rs`):
- **rank** = the highest rank whose spell is in the known-spell set (learn-up-to grants every lower
  rank): `rank_of(t, known) = max{i+1 : ranks[i]!=0 && known.contains(ranks[i])}`
  (`ui_talent.rs:152-161`). `known` is `PlayerActions.spells` — **the Spells section's known-spell
  set** (`ui_talent.rs:117-124, 175`). This is the cross-section dependency: ranks come from the
  spellbook, not any talent field.
- **points available** = `PLAYER_CHARACTER_POINTS1` (`player.rs:128` `player_talent_points`) exposed
  as `UnitCharacterPoints("player")` first return; second return `PLAYER_CHARACTER_POINTS2`
  (`player.rs:133` free primary professions) (`ui_talent.rs:113-116`; binding
  `crates/benilla-ui/src/script/talent.rs:216-226`). These are PRIVATE — self only. (There is no
  "points from level" formula in the client; the server maintains CHARACTER_POINTS1 and streams it.)
- **points_spent** per tab = Σ `rank_of` over the tab's talents (`ui_talent.rs:176`).

### 5c. Prereq / tier / availability logic

`build_pages` (`ui_talent.rs:164-264`) computes per talent:
- **tier gate**: `t.row * 5 <= tabSpent` (`ui_talent.rs:191`); locked ⇒ red line
  "Requires N points in <Tab> Talents" (`:194-200`).
- **prereq arrow**: `t.prereq_talent` → the prereq's (tier+1, col+1) 1-based seat + `learnable =
  rank_of(prereq) >= prereq_rank+1` (`:208-217`). If unmet ⇒ red line "Requires N point(s) in
  <prereqName>" (`:218-229`). Prereqs are always same-tab, always drawn.
- **meetsPrereq** = `required_spell==0 || known(required_spell)` (a spell gate, distinct from the
  talent prereq — `:190`).
- **display face** = the display-rank spell's name/icon (`ranks[max(rank,1)-1]`), from the Spells
  catalog (`:181-187, 236-238`); **next_spell** = `ranks[rank]` when `0<rank<max` (`:182-186`).
- **learnable** (green "Click to learn" hint) = `rank < max_rank && points.0 > 0` (`:235`).

### 5d. Learn action

`LearnTalent(tab, index)` (1-based) queues an intent (`talent.rs:207-214`); ui_talent drains it
(`ui_talent.rs:269-317`): resolve (tab,index)→Talent, gate on **not-at-max only** (points/prereqs
are the server's to enforce), send `ClientCommand::LearnTalent{talent_id, rank}` where **rank = the
current rank count (0-based next rank)** = vmangos learn-up-to semantics (→ CMSG_LEARN_TALENT).

---

## 6. THE TALENT TREE UI (ui_talent.rs semantics + TalentFrame.xml dims)

Window `BenillaTalentFrame` **384×512** (`crates/benilla/assets/ui/TalentFrame.xml:902-905`), 3 tabs
(`BENILLA_MAX_TALENT_TABS=3`, not the ref's defensive 5 — `:186, 27-33`). Tab buttons inherit
`CharacterFrameTabButtonTemplate` with a 32×32 tab icon (`BenillaTalentTabTemplate`, `:829-845`), 3
static instances `BenillaTalentFrameTab1..3` (`:1032-1040`). Switching a tab repaints the four
background quadrants `Interface\TalentFrame\<background>{TopLeft,TopRight,BottomLeft,BottomRight}`
(`:367-370`).

**Grid** (`:177-188`): `MAX_NUM_TALENT_TIERS=8`, `NUM_TALENT_COLUMNS=4`, `TALENT_BUTTON_SIZE=32`,
`INITIAL_TALENT_OFFSET_X=35`, `INITIAL_TALENT_OFFSET_Y=20`. Button placement
(`BenillaSetTalentButtonLocation`, `:772-776`):

```
x = (column-1)*63 + 35          y = -((tier-1)*63) - 20      -- 63px pitch, 1-based tier/column
```

Talent button template `BenillaTalentButtonTemplate` **37×37** (icon 32×32, Quickslot2 ring 64×64,
a "Rank"/"RankBorder" overlay) (`:861-888`). Buttons are pooled `BenillaTalentFrameTalent1..N`
inside a scroll child (`:1112`), SetPoint'd dynamically. **Rank pip text** "cur/max": rank shown on
the button; unlearned (rank 0) hides the rank border; a locked/unavailable talent desaturates the
icon to 0.65 gray and grays the rank text (`:425-444`).

**Scroll** (`:188-191`): scroll child height `BENILLA_TALENT_SCROLL_CHILD_H=504` (8 tiers ×63px
pitch: `20 + 7*63 + 32 = 493 < 504`), scroll-frame height 332, step 20, child width 320.

**Prereq branches + arrows** — a `TALENT_BRANCH_ARRAY[tier][column]` grid of edge flags
(`up/down/left/right/leftArrow/rightArrow/topArrow`), each **1 = unlocked (yellow) or −1 = locked
(gray)** — the reference draws the chain **either way** (`:275-286`, `crates/benilla/tests/talent_frame.rs:54-102,
121-237`). Branch texture atlas `Interface\TalentFrame\UI-TalentBranches` with
`TALENT_BRANCH_TEXTURECOORDS` (`:196-238`); arrows `Interface\TalentFrame\UI-TalentArrows` with
`TALENT_ARROW_TEXTURECOORDS` (`:239-266`); both are pooled/reset per redraw
(`BenillaTalentFrameBranch1..`, `BenillaTalentFrameArrow1..`) and SetPoint'd off the scroll child
(`:552-559`). The test asserts the vertical chain for a same-column, two-tier-up prereq
(Improved Rend→Deep Wounds): tier1.down, tier2.up+down, tier3.topArrow (`talent_frame.rs:121-176`),
and that a locked tier still draws them gray (`:220-237`).

**Points-remaining**: `UnitCharacterPoints("player")` first return, refreshed on
`CHARACTER_POINTS_CHANGED` (fired by the feed on any snapshot change, `ui_talent.rs:133`).
**Tooltip** `GameTooltip:SetTalent(tab,index)` renders through the spell tooltip channel with talent
lines (Rank cur/max, red req lines, green learn hint, "Next rank:" block) — depends on the Spells
section's spell-tooltip store being pre-fed (`talent.rs:18-29, 231-267`).

---

## 7. WHAT MSUI HAS TODAY (precisely)

- **Descriptor decode is partial, not complete.** `MSUIClient/MSUIClient/Net/ObjectFields.cs` is a
  sparse index→u32 map with named accessors for **only**: guid/type/entry/scale, health/maxhealth,
  level, faction, unit flags, npc flags, displayId, mount, target, bytes0, playerbytes/bytes2
  (`ObjectFields.cs:16-104`). It has the merge/created semantics right. But it defines **none** of
  the stat/skill/AP/resistance/damage/percentage/coinage/xp/character-points fields — so today the
  character stats are **not** decodable without adding accessors. (The brief's "ObjectFields decodes
  them all" overstates the staged file.)
- **A 3D dressed character model exists.** `CharacterRenderer.cs` (88KB) renders the skinned M2 with
  gear; `CharacterEquipment.cs` resolves pieces by InventoryType (`CharacterEquipment.cs:33-51`);
  `CharacterGeosets.cs` handles geoset visibility. Reusable as the paperdoll inset via RTT (with
  Foundations).
- **A generic DBC reader exists**, `Formats/DbcReader.cs` — `DbcFile.Parse` (WDBC header + string
  block, `DbcReader.cs:34, 13-24`). It has readers for ItemDisplayInfo, CharSections, CharHairGeosets,
  GroundEffect*, the Light chain, AreaTable — but **no** Talent.dbc / TalentTab.dbc / SkillLine.dbc /
  SkillLineAbility.dbc / SkillLineCategory.dbc / SkillRaceClassInfo.dbc / CharTitles.dbc readers
  (greenfield). `EMPIRICAL_CHECKS.md` covers only camera/render forks — nothing for this section.
- **No UI.** No character sheet, no skills window, no talent tree; ImGui-only. Equipped-item *stats*
  (for tooltips) come from the server item query, owned by the Inventory section.

---

## 8. PORT PLAN FOR MSUI (ImGui-native), per window

### (a) Character sheet
1. **Add descriptor accessors to `ObjectFields.cs`** for: `UNIT_FIELD_STAT0..4`, `RESISTANCES 0..6`,
   `ATTACK_POWER`(+MODS), `RANGED_ATTACK_POWER`(+MODS), `MINDAMAGE/MAXDAMAGE`,
   `MIN/MAXOFFHANDDAMAGE`, `MIN/MAXRANGEDDAMAGE`, `BASEATTACKTIME[0..1]`, `RANGEDATTACKTIME`,
   `PLAYER_FIELD_POSSTAT0..4`/`NEGSTAT0..4` (f32), `RESISTANCEBUFFMODSPOSITIVE/NEGATIVE 0..6` (f32),
   `MOD_DAMAGE_DONE_POS/NEG/PCT 0..6`, `PLAYER_FIELD_COINAGE`, `PLAYER_XP`/`NEXT_LEVEL_XP`,
   `CHARACTER_POINTS1/2`, and the skill block (§b). **Get the absolute 5875 indices from
   UpdateFields_1_12_1.h** (benilla's `mod.rs`); the relative packing is in `fields/player.rs`/`unit.rs`.
2. **Port the stat table (§2) verbatim.** Compute `base=eff−pos−neg`; buff coloring (green pos / red
   neg); the DPS/armor/AP-÷14/resistance-rating formulas exactly as `CharacterFrame.xml:419-754`.
   Build ONE stat struct mirroring `PlayerCombatStats` (`char_stats.rs:60-119`). Ship the authentic
   1.12 line set only (no dodge/parry/block/crit/defense; no health/mana/regen).
3. **ImGui paperdoll**: an ImGui window with equipment-slot icon buttons at the authentic anchors
   (§1a), the 233×224 model inset (RTT of `CharacterRenderer`, yaw + 2 rotate buttons — Foundations),
   the attributes block + resistances left, damage/AP/attack/ranged right. Reads `PLAYER_FIELD_INV_SLOT`
   guids → item objects → **reuse the Inventory section's item model** for icon/quality/tooltip.
4. **DurabilityFrame** as a small always-on overlay: armor-guy silhouette, per-region tint from the
   11 alert statuses (§3).
5. Money row belongs to Inventory, not here; no title/played on the sheet.

### (b) Skills
1. **Add `SkillLine.dbc` + `SkillLineCategory.dbc` + `SkillRaceClassInfo.dbc` readers** on
   `DbcReader.cs` (formats §4b). Optional: `SkillLineAbility.dbc` only if you want item-required-skill
   names (already owned by Inventory).
2. **Add the `PLAYER_SKILL_INFO` accessor** (the 3-dword/6-u16 packing, §4a) to `ObjectFields.cs`;
   iterate all `PLAYER_SKILL_SLOTS`, keep `skill_id!=0`.
3. **Group in ImGui** by category (order: Class, Professions, Secondary, Weapon, Armor, Languages;
   skip Not-Displayed/12), collapsible headers, name-sorted rows, each a value/max progress bar
   (271×15 feel, blue), with the unlearn (circle-slash) button gated on `abandonable`. Send
   `CMSG_UNLEARN_SKILL` (0x202, one u32) on unlearn; refresh from the returned skill-field delta.

### (c) Talents
1. **Add `Talent.dbc` + `TalentTab.dbc` readers** (formats §5a) — 21/15-field schemas, string cols at
   Talent none / TalentTab {1 name, 14 background}. Filter tabs by both masks in **file order**;
   keep each tab's talents in **native row order**.
2. **Compute per-talent state** (§5b/c): rank = highest known rank spell (needs the **Spells
   section's known-spell set**); points = `CHARACTER_POINTS1`; tier gate `row*5<=tabSpent`; prereq
   edge + learnable; face/next from the spell catalog.
3. **ImGui tree renderer**: 3 tab buttons repainting the 4 background quadrants; a scrollable
   `4-col × 8-tier` grid at `x=(col-1)*63+35, y=(tier-1)*63+20`, 37×37 icon buttons with a "cur/max"
   rank pip; branch/arrow lines between prereqs (yellow if unlocked, gray if locked — draw both) via
   the `UI-TalentBranches`/`UI-TalentArrows` atlases; a points-remaining header; tooltip with
   Rank cur/max + req lines + learn hint. Learn → `CMSG_LEARN_TALENT{talent_id, currentRank}`.

Name-reuse points: the Inventory item model (icons/quality/tooltip for equipped + required-skill
items), the Foundations paperdoll RTT widget, the Spells known-spell set + spell-tooltip store.

---

## 9. GOTCHAS / EMPIRICAL NOTES

- **Skill-field packing is 3 dwords, six u16s** — `(id,step)|(value,max)|(tempBonus,permBonus)` with
  `base = SKILL_INFO_1_1 + 3*slot` (`player.rs:257-260`). Easy to mis-stride as one-field-per-dword.
  Displayed bonus = temp+perm. There is **no skill-up packet** — everything is a descriptor delta
  (`ui_char.rs:92-101`); a client that wants "skill increased" chat must diff its own block.
- **Talent ranks come from the SPELLBOOK, not any talent field.** rank = highest known rank spell
  (`ui_talent.rs:152-161`). The only talent-owned wire field is `CHARACTER_POINTS1` (unspent points).
  No `LEARNED_SPELL`/rank field exists — hard dependency on the Spells section.
- **TalentTab `orderIndex` (col 13) is dead** — tab order is raw **file order** filtered by both
  masks (`talents.rs:22-24, 121-131`). The mage's Arcane/Fire share orderIndex 0; only file order
  reproduces Arcane/Fire/Frost. Don't sort by orderIndex.
- **Prereq arrows are drawn even when the tier is locked**, in gray (−1 keys), not hidden
  (`talent_frame.rs:220-237`). Branch array cells carry both a yellow(1) and gray(−1) state per edge.
- **Paperdoll slot order ≠ any numeric order.** Three numberings coexist: wire `EQUIPMENT_SLOT_*`
  (0-based), live-API `GetInventorySlotInfo` id (wire+1, ammo=0), and the hand-authored on-screen
  anchors (§1a). `BackSlot`/`AmmoSlot` borrow the Chest/Ranged empty-art (`char_stats.rs:214-217`).
- **Stat formulas that surprise:** (1) DPS uses **AP magic number 14** for the AP subtext
  (`CharacterFrame.xml:227,605`); (2) armor reduction is `armor/(85*level+400)` then `r/(r+1)`
  (`:515-516`); (3) resistance color is a **magnitude** compare `abs(neg) vs pos`, not sign
  (`:466-472`); (4) `SetArmor` deliberately passes `base` not `armor` to the formatter — a verbatim
  ref quirk, don't "fix" it (`:504-506`); (5) negBuff/negStat are stored **negative-or-zero** on the
  wire (`char_stats.rs:11-16`); posstat/negstat/resist-buffs are **floats** despite the header's INT
  tag (`player.rs:209-210`).
- **1.12 sheet is minimal.** No crit/hit/defense/dodge/parry/block (those fields feed spell tooltips,
  `player.rs:190-208`); no health/mana/regen; no title/guild/money/played. The Base-Stats/Melee/
  Ranged/Defenses tab set is TBC+. Building it would be a fidelity error.
- **Inspect vs self** (`crates/benilla/src/ui_inspect.rs`): the inspect window reuses the *same*
  `GetInventoryItem*` bindings but a different source — foreign players expose only PUBLIC
  `PLAYER_VISIBLE_ITEM_*` entries (`player.rs:69-76`, template-only, no item objects), so inspect
  shows **icon/name/quality only — no counts, no durability, no locks** (`ui_inspect.rs:1-17,
  102-141`). Only 19 equipment slots (1..19), never ammo/bags. It re-resolves the **token** each
  frame (so `"target"` follows retarget) and gates on a `CanInspect` squared-distance check
  (`ui_inspect.rs:69-74, 167-197`). Stat panes are self-only (all stat fields PRIVATE) — an inspect
  window shows the paperdoll + model, not the stat table.
- **has_offhand / has_wand are template-derived, not field flags** — read the equipped item's
  class/subclass (offhand weapon = class 2; wand = subclass 19) (`ui_char.rs:342-347`); the offhand
  damage fields stream regardless of what's equipped (`unit.rs:310-311`).
- **Absolute field indices were not in the staged tree** — only names + relative packing
  (`player.rs`/`unit.rs`) and a partial set in MSUI's `ObjectFields.cs`. The port must transcribe the
  stat/skill/AP/percentage base indices from `UpdateFields_1_12_1.h` (build 5875).

## Cross-section dependencies
- **Inventory section** — the item model (icon/quality/name/link/tooltip via `ItemTemplateView`,
  `crates/benilla-ui/src/script/item_stats.rs:28-115`): reused for equipped-slot icons/tooltips and
  for item-required-skill names. Equipped-item *stats* come from the server item query it owns.
- **Foundations section** — the paperdoll/portrait **render-to-texture** widget (the 233×224 model
  inset is an RTT of `CharacterRenderer`); FrameXML→ImGui tooltip/icon generalities.
- **Spells section** — the **known-spell set** (`PlayerActions.spells`) is the sole source of talent
  current ranks (`ui_talent.rs:153-161, 175`); and the **spell-tooltip store** backs the talent
  tooltip's rank/next-rank lines (`talent.rs:231-267`).

## Uncertainties
- Absolute wire indices for the stat/skill/AP/resistance/percentage fields are **not** in the staged
  files (non-staged `mod.rs`); cite `UpdateFields_1_12_1.h` (5875) when adding them. Only names +
  relative offsets are proven here.
- `PLAYER_SKILL_SLOTS` and `MAX_TALENT_RANK`(=5, `talents.rs:63`) counts: `PLAYER_SKILL_SLOTS`'s
  numeric value is re-exported from the non-staged protocol root (canonically 128 in 1.12 — verify).
- `CMSG_LEARN_TALENT` / `CMSG_INSPECT` opcode numbers are referenced via `ClientCommand` enums
  (`ui_talent.rs:312`, `ui_inspect.rs:205`) but their numeric opcodes aren't in staged files
  (`CMSG_UNLEARN_SKILL`=0x202 IS, `skills.rs:1`). Confirm against the opcode table.
- The skills-pane group order/within-group sort is flagged INTERIM in benilla (unpinned law,
  `skills.rs:12-24`, `ui_char.rs:186-190`) — faithful but not byte-verified against the live client.

# Appendix A — Opcode table (build 5875)

Values read from benilla `crates/benilla-protocol/src/messages/opcode.rs`. Hex is the wire value;
decimal in parentheses. "In MSUI?" marks opcodes already present in
`MSUIClient/MSUIClient/Net/Opcodes.cs` (some declared but not yet handled). Everything else must be
added.

### Target selection
| Opcode | Value | In MSUI? | Notes |
|---|---|---|---|
| CMSG_SET_SELECTION | 0x013D (317) | yes | body = raw full 8-byte guid; 0 clears. `WorldSession.SetSelection` implements it. |
| CMSG_INSPECT | 0x0114 (276) | no | inspect another player (Part 8). |

### Combat (attack + combat log)
| Opcode | Value | In MSUI? | Notes |
|---|---|---|---|
| CMSG_ATTACKSWING | 0x0141 (321) | yes | latch: one send starts auto-attack. |
| CMSG_ATTACKSTOP | 0x0142 (322) | yes | empty body. |
| SMSG_ATTACKSTART | 0x0143 (323) | yes | attacker u64 + victim packed. |
| SMSG_ATTACKSTOP | 0x0144 (324) | yes | attacker packed + victim packed + u32. |
| SMSG_ATTACKERSTATEUPDATE | 0x014A (330) | yes (unhandled) | one packet per swing (Part 4 §2). |
| SMSG_SPELLNONMELEEDAMAGELOG | 0x0250 (592) | no | spell damage; crit = hit_info & 0x2. |
| SMSG_PERIODICAURALOG | 0x024E (590) | no | DoT/HoT ticks; payload width is aura-type-dependent (can't skip unknown). |
| SMSG_SPELLHEALLOG | 0x0150 (336) | no | heals (never float as worldtext). |
| SMSG_SPELLENERGIZELOG | 0x0151 (337) | no | power gains. |
| SMSG_SPELLDAMAGESHIELD | 0x024F (591) | no | thorns; lands on the attacker. raw u64 guids. |
| SMSG_ENVIRONMENTALDAMAGELOG | 0x01FC (508) | no | fall/drown/lava/etc. raw u64. |
| SMSG_SPELLLOGMISS | 0x024B (587) | no | miss list. raw u64. |
| SMSG_LOG_XPGAIN | 0x01D0 (464) | no | "XP: %d" self-anchored purple text. |

### Spells / casting
| Opcode | Value | In MSUI? | Notes |
|---|---|---|---|
| CMSG_CAST_SPELL | 0x012E (302) | yes | `u32 spell + u16 mask + [SpellCastTargets]`. **No cast-count byte in 1.12.** |
| SMSG_CAST_RESULT | 0x0130 (304) | yes | the "cast failed" packet: `u32 spell, u8 status`; status 2 → +`u8 reason`. No OK sent for a normal cast. |
| SMSG_SPELL_START | 0x0131 (305) | yes | drives the cast bar (`castTimeMs`). |
| SMSG_SPELL_GO | 0x0132 (306) | yes | launch (not impact); success signal. |
| SMSG_INITIAL_SPELLS | 0x012A (298) | yes (unhandled) | known-spell id list + cooldown list, at login. |
| SMSG_LEARNED_SPELL | 0x012B (299) | no | `{u16 spell, u16 slot(drop)}`. |
| SMSG_SUPERCEDED_SPELL | 0x012C (300) | no | rank-up `{u16 old, u16 new}`; swaps in book AND action bar. |
| CMSG_CANCEL_CAST | 0x012F (303) | no | `{u32 spell}`. |
| CMSG_CANCEL_CHANNELLING | 0x013B (315) | no | `{u32 spell}` (server ignores id). |
| CMSG_CANCEL_AURA | 0x0136 (310) | no | `{u32 spell}` — by spell, not aura slot. |
| SMSG_SPELL_FAILED_OTHER | 0x02A6 (678) | no | `{u64 caster, u32 spell}` broadcast cancel. |
| SMSG_SPELL_DELAYED | 0x01E2 (482) | no | `{u64 caster, u32 delayMs}` pushback (does not cancel). |
| MSG_CHANNEL_START | 0x0139 (313) | no | `{u32 spell, u32 durMs}`, self-only, no guid. |
| MSG_CHANNEL_UPDATE | 0x013A (314) | no | `{u32 remainMs}`, self-only; 0 = channel over. |
| SMSG_SPELL_COOLDOWN | 0x0134 (308) | no | school lockouts / pets / item procs only. |
| SMSG_COOLDOWN_EVENT | 0x0135 (309) | no | cooldown-on-event start. |
| SMSG_CLEAR_COOLDOWN | 0x01DE (478) | no | GM/ability reset. |
| SMSG_CANCEL_AUTO_REPEAT | 0x029C (668) | no | stop Auto Shot/Wand. |

### Action bars
| Opcode | Value | In MSUI? | Notes |
|---|---|---|---|
| SMSG_ACTION_BUTTONS | 0x0129 (297) | yes (unhandled) | 120 packed u32s at login (client-authoritative thereafter). |
| CMSG_SET_ACTION_BUTTON | 0x0128 (296) | no | 5 bytes: `u8 button, u32 (action \| kind<<24)`; 0 clears. One send per mutation. |

### Inventory / items
| Opcode | Value | In MSUI? | Notes |
|---|---|---|---|
| CMSG_ITEM_QUERY_SINGLE | 0x0056 (86) | no | `{u32 entry, u64 guid}`. |
| SMSG_ITEM_QUERY_SINGLE_RESPONSE | 0x0058 (88) | no | the full item template (Part 7 §1b). Miss = `entry \| 0x80000000`. |
| CMSG_USE_ITEM | 0x00AB (171) | no | `{bag, slot, spellSlot, u16 mask=0}`. |
| CMSG_AUTOEQUIP_ITEM | 0x010A (266) | no | `{bag, slot}`. |
| CMSG_SWAP_ITEM | 0x010C (268) | no | `{dstBag,dstSlot,srcBag,srcSlot}` — **destination first**; either end a bag. |
| CMSG_SWAP_INV_ITEM | 0x010D (269) | no | `{srcSlot,dstSlot}` — both ends the player grid (wire-255). |
| CMSG_SPLIT_ITEM | 0x010E (270) | no | `{srcBag,srcSlot,dstBag,dstSlot,count}`. |
| CMSG_DESTROYITEM | 0x0111 (273) | no | `{bag,slot,count,0,0,0}`; count 0 = whole stack. |
| SMSG_INVENTORY_CHANGE_FAILURE | 0x0112 (274) | no | `u8 reason` (+`u32 reqLevel` iff reason==1, then 2×u64, u8). |
| CMSG_SET_AMMO | 0x0268 (616) | no | `{u32 entry}`. **Note the hex** — 0x0268, distinct from SWAP_ITEM's decimal 268. |
| SMSG_SHOW_BANK | 0x01B8 (440) | no | banker guid opens the vault; no close opcode (client-side clear). |
| CMSG_BUY_BANK_SLOT | 0x01B9 (441) | no | `{banker guid}`; success = a `PLAYER_BYTES_2` byte-2 delta, not a packet. |

CMSG_AUTOSTORE_BAG_ITEM (267 / 0x010B) and the bank-store pair (CMSG_AUTOSTORE_BANK_ITEM /
CMSG_AUTOBANK_ITEM) exist in `opcode.rs`; their exact numbers were not captured in this pass — read
them from the file before wiring (Appendix E).

### Skills / talents
| Opcode | Value | In MSUI? | Notes |
|---|---|---|---|
| CMSG_UNLEARN_SKILL | 0x0202 (514) | no | `{u32 skillId}`; removal comes back as a skill-field delta. |
| CMSG_LEARN_TALENT | 0x0251 (593) | no | `{u32 talentId, u32 currentRank}` (learn-up-to). |

---

# Appendix B — UpdateFields index table (build 5875)

The single reference for the descriptor indices the UI reads. Values marked ✓benilla were read from
`crates/benilla-protocol/src/messages/update_object/fields/mod.rs`; values marked ✓MSUI are already
correct in `MSUIClient/MSUIClient/Net/ObjectFields.cs`. This table **supersedes** any inline
"verify against UpdateFields_1_12_1.h" caveat in the parts. All indices are 0-based dword slots; GUID
fields occupy two consecutive slots.

### OBJECT (all types)
| Field | Index | Src |
|---|---|---|
| OBJECT_FIELD_GUID | 0 (2 slots) | ✓MSUI |
| OBJECT_FIELD_TYPE | 2 | ✓MSUI |
| OBJECT_FIELD_ENTRY | 3 | ✓MSUI |
| OBJECT_FIELD_SCALE_X | 4 | ✓MSUI |

### ITEM / CONTAINER (per item object)
| Field | Index | Src |
|---|---|---|
| ITEM_FIELD_STACK_COUNT | 14 | ✓benilla |
| ITEM_FIELD_SPELL_CHARGES (×5) | 16..20 | ✓benilla |
| ITEM_FIELD_FLAGS | 21 | ✓benilla |
| ITEM_FIELD_ITEM_TEXT_ID | 45 | ✓benilla |
| ITEM_FIELD_DURABILITY | 46 | ✓benilla |
| ITEM_FIELD_MAXDURABILITY | 47 | ✓benilla |
| CONTAINER_FIELD_NUM_SLOTS | 48 | ✓benilla |
| CONTAINER_FIELD_SLOT_1 (+2·i, i<36) | 50 | ✓benilla |

### UNIT
| Field | Index | Src |
|---|---|---|
| UNIT_FIELD_TARGET (GUID) | 16 | ✓benilla / ✓MSUI |
| UNIT_FIELD_HEALTH | 22 | ✓MSUI |
| **UNIT_FIELD_POWER1..5** | **23..27** | ✓benilla — **NOT 47-51**; a first pass guessed wrong |
| UNIT_FIELD_MAXHEALTH | 28 | ✓MSUI |
| **UNIT_FIELD_MAXPOWER1..5** | **29..33** | ✓benilla |
| UNIT_FIELD_LEVEL | 34 | ✓MSUI |
| UNIT_FIELD_FACTIONTEMPLATE | 35 | ✓MSUI |
| UNIT_FIELD_BYTES_0 (byte 3 = power type) | 36 | ✓MSUI |
| UNIT_FIELD_FLAGS | 46 | ✓MSUI |
| UNIT_FIELD_AURA (48 slots: buffs 0–31, debuffs 32–47) | (aura block) | ✓benilla (comment); base index per fields/mod.rs |
| UNIT_FIELD_AURASTATE | 125 | ✓benilla |
| UNIT_FIELD_BASEATTACKTIME (2 slots: main, offhand) | 126 | ✓benilla |
| UNIT_FIELD_RANGEDATTACKTIME | 128 | ✓benilla (= BASEATTACKTIME+2) |
| UNIT_FIELD_BOUNDINGRADIUS | 129 | ✓benilla |
| UNIT_FIELD_COMBATREACH | 130 | ✓benilla |
| UNIT_FIELD_DISPLAYID | 131 | ✓MSUI |
| UNIT_FIELD_MINDAMAGE / MAXDAMAGE | 134 / 135 | ✓benilla |
| UNIT_DYNAMIC_FLAGS | 143 | ✓MSUI |
| UNIT_NPC_FLAGS | 147 | ✓MSUI |
| UNIT_NPC_EMOTESTATE | 148 | ✓benilla |
| UNIT_FIELD_STAT0..4 | 150..154 | ✓benilla |
| UNIT_FIELD_RESISTANCES (×7; [0] = armor) | 155..161 | ✓benilla |
| UNIT_FIELD_BASE_MANA | 162 | ✓benilla |
| UNIT_FIELD_BYTES_2 | 164 | ✓benilla |
| UNIT_FIELD_ATTACK_POWER | 165 | ✓benilla |
| UNIT_FIELD_ATTACK_POWER_MODS (two i16) | 166 | ✓benilla |
| UNIT_FIELD_ATTACK_POWER_MULTIPLIER (f32) | 167 | ✓benilla |
| UNIT_FIELD_RANGED_ATTACK_POWER | 168 | ✓benilla |
| UNIT_FIELD_RANGED_ATTACK_POWER_MODS | 169 | ✓benilla |
| UNIT_FIELD_RANGED_ATTACK_POWER_MULTIPLIER (f32) | 170 | ✓benilla |

`UNIT_FIELD_MINOFFHANDDAMAGE/MAXOFFHANDDAMAGE` and `MINRANGEDDAMAGE/MAXRANGEDDAMAGE` sit just after
MAXDAMAGE (their relative accessors are in `fields/unit.rs`); read the exact indices from
`fields/mod.rs`/`UpdateFields_1_12_1.h` when adding the character sheet (Appendix E).

### PLAYER
| Field | Index | Src |
|---|---|---|
| PLAYER_BYTES (appearance) | 193 | ✓MSUI |
| PLAYER_BYTES_2 (byte 2 = bank bag slots bought) | 194 | ✓benilla / ✓MSUI |
| PLAYER_VISIBLE_ITEM_1_CREATOR (12 fields/slot, 19 slots; public worn ENTRY sub-field) | 258 | ✓benilla |
| PLAYER_XP | 716 | (= NEXT_LEVEL_XP−1) |
| PLAYER_NEXT_LEVEL_XP | 717 | ✓benilla |
| PLAYER_SKILL_INFO_1_1 (×384 = 128 skills × 3 dwords) | 718 | ✓benilla |
| PLAYER_CHARACTER_POINTS1 (unspent talent points) | 1102 | ✓benilla |
| PLAYER_CHARACTER_POINTS2 (free professions) | 1103 | ✓benilla |
| PLAYER_FIELD_COINAGE | 1176 | ✓benilla |
| PLAYER_FIELD_POSSTAT0..4 (f32) | 1177..1181 | ✓benilla |
| PLAYER_FIELD_NEGSTAT0..4 (f32, ≤0) | 1182..1186 | ✓benilla |
| PLAYER_FIELD_RESISTANCEBUFFMODSPOSITIVE (×7, f32) | 1187..1193 | ✓benilla |
| PLAYER_FIELD_RESISTANCEBUFFMODSNEGATIVE (×7, f32, ≤0) | 1194..1200 | ✓benilla |
| PLAYER_FIELD_INV_SLOT_HEAD (+2·i, i<23: equip 0–18, bags 19–22) | 486 | ✓benilla |
| PLAYER_FIELD_PACK_SLOT_1 (+2·i, i<16 backpack) | 532 | ✓benilla |
| PLAYER_FIELD_BANK_SLOT_1 (+2·i, i<24) | 564 | ✓benilla |
| PLAYER_FIELD_BANK_BAG_SLOT_1 (+2·i, i<6) | 612 | ✓benilla |
| PLAYER_FIELD_VENDORBUYBACK_SLOT_1 (+2·i, i<12) | 624 | ✓benilla |

Skill-slot packing (each of the 128 slots at `718 + 3·slot`): dword0 `(skillId:u16, step:u16)`,
dword1 `(value:u16, max:u16)`, dword2 `(tempBonus:i16, permBonus:i16)`. Displayed modifier =
temp+perm. `skillId==0` ⇒ empty slot.

The crit/dodge/parry/block **percentage** fields exist and are decoded but feed spell tooltips, not
the 1.12 character sheet — their relative accessors are in `fields/player.rs`; read exact indices
from `UpdateFields_1_12_1.h` if you ever surface them.

---

# Appendix C — DBC readers to add

All follow the existing `DbcFile.Parse` pattern in `MSUIClient/MSUIClient/Formats/DbcReader.cs`
(`GetUInt/GetInt/GetFloat/GetString(row, col)`; strings are byte offsets into the string block).
**1.12 has no client `Item.dbc`** — item templates are server-queried (Part 7). File paths are under
`DBFilesClient\`.

| DBC | Fields | Needed for | Key columns (see the cited Part) |
|---|---|---|---|
| Spell.dbc | 173 | spells, action bars, tooltips, talents, auras | Part 5 §1a full table (name 120, rank 129, desc 138, icon 117, castTime 18, duration 30, range 36, powerType 31, mana 32, GCD 157/158, reagents 42–57, effects 61–111…) |
| SpellIcon.dbc | 2 | spell/aura icons | id→`Interface\Icons\<name>` (append `.blp`) |
| SpellCastTimes.dbc | 4 | tooltip cast time | base ms (row 1 = instant) |
| SpellDuration.dbc | 4 (i32) | tooltip `$d` | row 21 = permanent (−1) |
| SpellRange.dbc | 22 | range gate, tooltip | min/max f32, flags bit0 = melee |
| SpellRadius.dbc | 4 | tooltip `$a` | radius f32 |
| SpellShapeshiftForm.dbc | 14 | stance bar, form gates | BonusActionBar 1, name 2, flags1 11 (bit0 = stance) |
| SpellDispelType.dbc | — | debuff border class name | id→dispel name |
| Talent.dbc | 21 | talent tree | tab 1, row 2, col 3, rankSpell[5] 4–8, prereq 13, prereqRank 16, flags 19, reqSpell 20 |
| TalentTab.dbc | 15 | talent tabs | name 1, raceMask 11, classMask 12, background 14 (orderIndex 13 is **dead** — use file order) |
| SkillLine.dbc | 22 | skills window, spellbook tabs | category 1, name 3, desc 12, icon 21 |
| SkillLineCategory.dbc | 11 | skills grouping | id→(name, displayOrder) |
| SkillRaceClassInfo.dbc | — | unlearn gate | flags bit 0x20 = unlearnable |
| SkillLineAbility.dbc | — | spellbook tab-by-skill (optional; defer to one General tab) | spell→skill line |
| FactionTemplate.dbc | — | reaction colour (ring/nameplate/tooltip) | reaction comparator; + Faction.dbc for rep + names |
| BankBagSlotPrices.dbc | — | next bank-slot cost | 10s/1g/10g/25g/50g/100g |
| SpellVisual.dbc (+kits) | 16 | spell/combat FX (defer) | Part 5 §5 |

Also **extend `ItemDisplayRow`** in `DbcReader.cs` to capture field **[5] `m_inventoryIcon`** (the
icon stringref) — the row is read today for models but skips the icon, which the bag/paperdoll UIs need.

---

# Appendix D — Empirical verification checklist

One discriminating test per claim, in the spirit of the repo's `EMPIRICAL_CHECKS.md` — run each once
the system is built; a failure points at the specific mechanism.

**Target selection.** At scale 1.0 the selection ring radius should be ≈ **HumanMale 0.841**,
**Chicken 0.572**, **Horse 1.295** yd (Part 3 §4). Clicking a mob sends `CMSG_SET_SELECTION` (0x13D)
with the full 8-byte guid; clicking empty ground sends guid 0. Reaction colour resolves in the
*target→player* direction (a passive beast reads yellow/neutral, not red).

**Combat.** `SMSG_ATTACKERSTATEUPDATE` sums absorb/resist across sub-blocks (two blocks of absorb 5+7
→ 12); a full block is synthesized client-side when `damage==0 && blocked!=0`. Your melee number is
**white**, your spell number **gold** (`0xFFFFDE00`), pet melee **orange** (`0xFFFF8400`); a crit
changes size (2× pop → settles 1.5×), not colour. Another unit's damage floats **nothing**. Power
bars read `UNIT_POWER1..5` at **23-27** (not 47-51).

**Spells.** `CMSG_CAST_SPELL` for a self/implicit spell (Ice Armor) is **6 bytes** — `u32 spell +
u16 mask(0)` with **no guid and no cast-count byte**; shipping the current selection there is the
"Invalid target" bug. `SMSG_CAST_RESULT` sends nothing on success (success = `SMSG_SPELL_GO`). The
casting bar fills L→R for a cast, drains R→L for a channel.

**Action bars.** A slot packs `action | (kind<<24)` with kind 0x00 spell / 0x40 macro / 0x80 item;
`CMSG_SET_ACTION_BUTTON` is 5 bytes and a drag-swap is **two** sends. Cooldown fraction
`(now−start)/duration` with `start` absolute — `(6000,10000)` at t=10s → **0.4**. Out-of-power tints
the **icon+ring** blue `(0.5,0.5,1)`; out-of-range tints the **hotkey** red `(1,0.1,0.1)` — different
channels.

**Inventory.** An unknown item query answers `entry | 0x80000000` — mask the top bit and cache the
**negative** or it loops. Wire bag **255** addresses the player grid (equip 0–18, bags 19–22,
backpack 23–38); a backpack↔backpack move is `SWAP_INV_ITEM`, a move touching an equipped bag is
`SWAP_ITEM` with the **destination pair first**. 1.12 bag slots have **no quality border** (quality
is in the name colour only).

**Character / skills / talents.** Skills live at `PLAYER_SKILL_INFO_1_1` (718) `+ 3·slot`, six u16s
per slot. Talent current rank = the **highest known rank spell** (from the spellbook, not any field);
points available = `PLAYER_CHARACTER_POINTS1` (1102). The 1.12 sheet shows **no** crit/hit/defense/
dodge/parry/block and no health/mana/regen — building those is a fidelity error.

**Portraits.** The M2 portrait camera `fov` is a **diagonal** angle: the on-screen vertical opening
is `0.6·fov`. A round portrait must frame from `cameraLookup[0]` (or the head bone); a paperdoll
must frame from model **bounds**, not the bust camera.

---

# Appendix E — Open items to verify against source

Consolidated from all parts. None blocks a first implementation; each is a place to read one more
benilla/vmangos file before shipping the detail.

1. **Combat/attack/death M2 animation ids** beyond `CombatWound = 9` and the locomotion set
   (Stand 0 / Walk 4 / Run 5) are from AnimationData.dbc knowledge — benilla's `creature_anim` id map
   was not staged. Verify the swing/ready/death/defensive ids (Part 4 §7b) against the dbc or benilla
   before relying on them.
2. **A few PLAYER/UNIT field indices** used only by the character sheet — `MINOFFHANDDAMAGE`/
   `MAXOFFHANDDAMAGE`, `MINRANGEDDAMAGE`/`MAXRANGEDDAMAGE`, `MOD_DAMAGE_DONE_*`, and the
   crit/dodge/parry/block percentages — have their **relative** accessors pinned in `fields/player.rs`
   / `fields/unit.rs`; read their absolute indices from `fields/mod.rs` (staged) or
   `UpdateFields_1_12_1.h` when adding those lines. (The stat/AP/resistance bases in Appendix B are
   already byte-verified.)
3. **A few inventory opcode numbers** — `CMSG_AUTOSTORE_BAG_ITEM` (267/0x010B), and the bank-store
   pair `CMSG_AUTOSTORE_BANK_ITEM` / `CMSG_AUTOBANK_ITEM` — exist in `opcode.rs` but were not captured
   in this pass; read them before wiring bank deposit/withdraw. benilla marks the AUTOBANK-vs-
   AUTOSTORE_BANK *direction* as INFERRED (either lands correctly server-side).
4. **`ItemDisplayRow` icon field [5]** is untested in MSUI (the row never reads it today) — confirm
   the field index against a real ItemDisplayInfo record when you add it.
5. **`PLAYER_SKILL_SLOTS` = 128** is canonical and consistent with the `×384` skill block; confirm the
   constant in benilla's protocol root if you want it pinned.
6. **Tooltip inner padding / wrap constants** (`TOOLTIP_PAD`, `LINE_GAP`, `WRAP_WIDTH`) live in
   benilla's `widget/kinds.rs`, not staged; the plate dimensions (EdgeSize 16, insets 5, navy/white/
   gold) are pinned from `GameTooltip.xml`.
7. **Portrait square vs 4:3.** benilla ships a **square** (aspect 1.0) portrait FBO keeping only the
   `0.6·fov` diag→vert factor; if a side-by-side against a real 1.12 client shows a squeeze, the 4/3
   path is the documented alternative (Part 2 §B11).
8. **The skills-pane group order / within-group sort** is flagged INTERIM in benilla (faithful, not
   byte-verified against the live client).
9. **benilla's `autoSelfCast` default is ON** — a *named deviation* from the retail CVar default "0"
   (Part 6 §3). Decide MSUI's default deliberately.

---

*End of document. Generated 2026-07-29 from benilla `C:\Users\nico\Desktop\benilla-main` (read-only
reference) for the MSUIClient port. Every part is `file:line`-cited to benilla and MSUI source as it
stood at generation time.*
