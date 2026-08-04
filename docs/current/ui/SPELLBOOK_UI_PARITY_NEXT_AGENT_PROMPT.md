# Spellbook UI Pixel-Parity — Next-Agent Prompt

Prepared: 2026-08-04  
Repository: `C:\Users\nico\source\repos\MSUIClient`

## Assignment

Continue the empirical 1.12 spellbook parity work. Do not restart the implementation and do not
reinterpret a passing build as visual success. The latest user verdict is the controlling status:

> The main spellbook page is closer, but spacing issues remain. The tooltip is still wrong in font
> size, feel, and look.

The immediate job is to measure and correct those remaining differences against same-resolution
1.12 captures. The spellbook page and the tooltip are separate typography/layout problems; do not
assume that one renderer conversion factor is correct for both.

## Read first

1. This entire prompt.
2. `docs/current/ui/UI_INTERFACE_PROPORTIONS_HANDOFF.md`
3. `docs/systems/SYSTEM_GAMEPLAY_UI.md`
4. `docs/current/spells/SPELL_FX_SEMANTIC_PARITY_NEXT_AGENT_PROMPT.md` only to understand the validated
   spell-FX boundary that must not be disturbed.
5. The relevant direct client sources, in this authority order:
   - mounted build-5875 MPQs / extracted FrameXML and fonts;
   - `C:\Users\nico\Desktop\benilla-main\crates\benilla\assets\ui\Fonts.xml`;
   - `SpellBookFrame.xml`, `GameTooltip.xml`, and `ActionBar.xml` beside it;
   - Benilla renderer code when FrameXML does not describe engine-side behavior.

Direct MPQ evidence outranks Benilla's transcription if they differ.

## Non-negotiable empirical contract

Do not translate FrameXML numbers directly into ImGui values.

Before changing code, produce a provenance table for every affected visual property:

- final resolved FrameXML value;
- inherited font-object properties;
- Lua/runtime overrides;
- renderer-specific conversion;
- expected on-screen measurement.

Do not retain guessed colors, offsets, font sizes, or fallback styling. Mark anything unresolved as
`UNKNOWN`.

After implementation, do not call it correct based on compilation or static tests. Compare a
same-resolution MSUI capture against the 1.12 reference and measure:

- visible glyph height and width;
- baseline and inter-line distance;
- shadow color and offset;
- active and passive text colors;
- rasterization smoothness;
- UI-scale behavior.

Distinguish **source value copied** from **rendered output matched**. Only claim parity after the
rendered pixels agree.

Use these status labels in notes and reports:

- `SOURCE_VERIFIED`: directly established from MPQ/FrameXML/Lua/DBC evidence.
- `STATIC_IMPLEMENTED`: present in MSUI and covered by code/static checks.
- `CAPTURE_MEASURED`: measured in paired, same-resolution screenshots.
- `USER_VALIDATED`: Nico has confirmed it by eye.
- `PARITY_CERTIFIED`: rendered measurements agree and Nico has accepted the result.
- `UNKNOWN`: not yet proved. Never silently replace this with a guess.

## Working-tree warning

The repository is intentionally dirty and contains user-owned work across spell rendering, UI,
catalogs, tools, and documentation. Some important new files are untracked. Do not reset, clean,
checkout, or broadly revert the tree. Inspect `git status --short` before editing and preserve all
unrelated changes.

Particularly important untracked files include:

- `MSUIClient/Engine/UI/SpellbookLaw.cs`
- `MSUIClient/Engine/UI/SpellTooltipLaw.cs`
- `MSUIClient/Program.SpellFxInspector.cs`
- `MSUIClient/World/Spells/SpellParticleTrailLaw.cs`

The earlier spell-FX result is user-validated: Fireball and Frostbolt look good, and Blizzard shards
now travel at an angle with their trails. Do not alter the spell particle/ribbon/missile pipeline as
part of this typography task.

## What has already been done

### 1. Spellbook contents and tabs

- `SkillLineCatalog.SpellTab(...)` now joins the skill-line/race/class data so spells route into
  General plus the class's three specialization tabs.
- Non-specialization abilities, racial abilities, and passives go into General.
- The spellbook presents no more than four right-side tabs: General plus up to three available specs.
- General uses `Interface\Icons\Ability_Kick`.
- Spell ranks sort numerically through `SpellbookLaw.LeadingRankNumber(...)`.
- Spellbook eligibility comes from `SpellInfo.InSpellbook` via `SpellbookLaw.Eligible(...)`.

Status: `STATIC_IMPLEMENTED`; previously visually inspected and substantially improved.

### 2. Icon seating, tab appearance, and hover behavior

- Spellbook icon background/art ordering was corrected so the frame masks icons instead of leaving
  visible gaps around them.
- Main spell icons and right-side tab icons were repositioned within their bezels.
- Inactive tab icons render at normal brightness; selected/hover state is separate.
- Spellbook and action-bar hover now use `GameplayArt.BrightHighlightHandle(...)`, replacing the
  incorrect darkening behavior with the bright additive-looking outline expected from 1.12.
- Action-bar rings render underneath the hover highlight.

Status: largely `USER_VALIDATED`; preserve unless a new paired capture demonstrates a regression.

### 3. Overall gameplay UI scale

- `Program.GameplayLayout.cs` uses `GameplayReferencePreference = 2.0f`.
- The shipped UI preference of 1.8 therefore presents the authored 1024x768 layout at 90%.
- This corrected the spellbook being globally too large relative to the 1.12 reference.

Status: empirically selected in the previous same-resolution comparison. Do not restore the old
`1.8 / 1.8` relationship as a shortcut for local text-spacing problems.

### 4. Tooltip content/data pipeline

- `SpellTooltipLaw.Build(...)` resolves name, rank, cost, range, cast time, cooldown, and description
  from the spell catalog.
- Supported 1.12 description substitutions (`$s`, `$a`, `$d`, `$t`, `$o`, related references and
  selectors) are resolved rather than shown literally.
- Static interface checks scan eligible spell descriptions for supported unresolved tokens.
- Spellbook and main/multi action-bar spell hovers use the same tooltip-content builder.
- Action-bar spell tooltips use the FrameXML-style bottom-right default-anchor semantics.
- Spellbook tooltips use owner-right placement.
- Intentional product choice: keep the rank visible on the title line in spellbook tooltips even
  though the original `SetSpell` path did not always show it. Nico explicitly prefers this.
- Tooltip width is measured from content, with 260 logical pixels as a wrap ceiling rather than a
  mandatory fixed width.

Status: content is `STATIC_IMPLEMENTED`; font rendering, line geometry, backdrop feel, and final size
are **known mismatches**, not parity.

### 5. Spellbook typography calibration pass

`Program.Spellbook.cs` currently contains a temporary live calibration surface:

- F6 toggles it while DevTools are enabled.
- `_spellbookFontRenderScale` defaults to `1.25f`.
- buttons provide 1.20x, 1.25x, and 1.30x starting points.
- `_spellbookFontPixelSnap` defaults to true.
- the production spellbook labels currently use this live value.

The pass also:

- changed passive spell names from gray to the source-derived faded yellow;
- added the black shadow inherited by `GameFontNormal` at one logical pixel right/down on screen;
- kept `SubSpellFont` rank/category text unshadowed;
- constrained spell names to the authored width and up to three lines;
- recalculated rank placement after name wrapping instead of relying on the former fixed top offset.

Nico's result after this pass: **the main page is closer, but spacing still needs work**. The selected
calibration value, if Nico changed it during that run, was not persisted. At the start of the next
session, record the actual scale and pixel-snap state being viewed before changing anything.

Status: `STATIC_IMPLEMENTED`, with a partial visual improvement. It is not `PARITY_CERTIFIED`.
**Superseded by section 6** - the calibration factor is gone; the renderer conversion is now derived.

### 6. Derived renderer law replacing the 1.25 calibration (session 2026-08-04)

The empirical 1.25 factor was identified as a measurable font property and replaced with three
byte-verified 1.12 renderer laws, implemented in the new `MSUIClient/Engine/UI/GameTextLaw.cs`:

1. **Raster size (client fn `0x5ca030`)**: the client rasterises at
   `clamp(round(FontHeight/768 x deviceHeight), 2, 32)` px and FreeType makes that the em. With the
   gameplay scale `s`, the em is `round(FontHeight x s)`. FontHeight IS the em - and stb_truetype
   (ImGui) sizes by hhea ascent-descent instead, so the ImGui draw size for an em of N is
   `N x (hhea.ascent - hhea.descent) / unitsPerEm`. For the shipped FRIZQT (SHA prefix
   `8F798FEB...`, upm 1000, ascent 965, descent -250) that is **exactly 1.215** - what 1.25 was
   hand-approximating. The factor is read from the extracted TTF at startup, not hardcoded.
2. **Advance (client fn `0x5d1120`)**: per glyph, the pen advances `floor(FT_advance) + 1` px. The
   +1 tracking per glyph is why 1.12 text reads wider/denser than raw metrics (it is ~+6 px on
   "Attack" alone) and why no size factor could close the width gap. Baked into the glyph advances
   after the atlas builds; F6 can A/B it live.
3. **Line pitch (client fn `0x5cdc20`)**: `lineStep = em + spacing`, spacing defaults 0 - the
   pitch is the font height itself, NOT an ascent+descent line height. Spellbook name wrapping and
   the tooltip line stack now stack by the em in device pixels.

Source for the three laws: Benilla's byte-verified transcriptions in
`benilla-main\crates\benilla\src\ui_text\mod.rs` (wow-re `system/font`,
`fontstring-overflow.md`, `font-size-to-freetype-em.md`). Benilla is at
`C:\Users\nico\Desktop\benilla-main` and models the same seam MSUI has.

Implementation notes:

- Gameplay text (spellbook names/ranks/title/page, both tooltip fonts) now draws from dedicated
  atlas fonts rasterised at their **exact on-screen pixel size** (no supersample-downscale, no
  oversampling), baked in `ClientWindow`'s ImGui `onConfigureIO` seam for FontHeights 10/12/14 at
  the startup gameplay scale. The glue screen keeps the supersampled atlas untouched.
- PixelSnapH is deliberately OFF at bake: ImGui would ROUND advances and the law needs
  `floor(raw)+1`. Draw origins are snapped instead; the law's integer advances keep the pen on
  whole pixels.
- ImGui.NET's managed `ImFontGlyph` mis-declares the native bitfield layout (48 vs 40 bytes), so
  glyph advance mutation is raw-pointer at the native 40-byte stride, layout-validated by
  round-tripping known codepoints before any write (see `GameTextLaw.GlyphLayoutLooksSane`).
- Tooltip layout constants were cross-checked against Benilla's engine-side law
  (`benilla-ui/src/widget/kinds/mod.rs`): Pad 10 / LineGap 2 / DoubleGap 40 / WrapWidth 260 all
  agree with `SpellTooltipLaw`; the tooltip's wrongness was typography, not those constants.
  `GameTooltipHeaderText`/`GameTooltipText` are shadowless (no MasterFont chain) - the tooltip
  draws no text shadow.
- `_spellbookFontRenderScale` is renamed `_spellbookFontDiagnosticScale`, default **1.0 = the
  derived law**. The F6 panel now shows the derived quantities (em factor, baked em sizes,
  per-font em px), the advance-law A/B checkbox, the diagnostic multiplier, and pixel snap.
- Fallbacks: no TTF -> the old supersampled path with the metric-derived factor; em sizes not
  baked (window resized, preference changed at runtime) -> nearest bake rescaled, logged once.

Status: `SOURCE_VERIFIED` for the three laws and the 1.215 factor; `STATIC_IMPLEMENTED` for the
renderer (build + interface wire checks pass). **Not `CAPTURE_MEASURED` and not
`PARITY_CERTIFIED`** - no paired capture has been taken since this change.

Known remaining divergence, deliberately not papered over: the client runs 2004-era FreeType
**with hinting**; stb_truetype does not hint. Hinted rasterisation can grid-fit advances upward
before the floor+1, so client text may still run 0-1 px per glyph wider than this law applied to
unhinted advances, and stems may read a hair slimmer. Benilla ships the identical divergence.
Whether it is visible at the shipped scale is `UNKNOWN` until the Phase A paired capture - if
widths land consistently short by ~1 px/glyph, that is the cause, and the fix is hinted advances
(FreeType-side), not another scale factor.

Same-day first visual check (Nico, maximised window) surfaced two defects, both fixed:

1. **Soft/washed text**: the F6 panel read `baked em [11,13,15]` against wanted em 19/16/22 -
   the atlas was baked for the CONFIG window size while the window ran maximised, so every
   string upscaled ~40% out of the nearest bake. The atlas now retargets and rebuilds at
   runtime: `GameTextLaw.Retarget` (set-compare of wanted em sizes), a full atlas rebuild in
   `ClientWindow.RebuildFontAtlas` between frames, seeded from the real framebuffer at load and
   driven every frame by `GameLoop.Gui -> EnsureGameplayTextScale(GameplayUiScale())`. A resize
   or UI-scale change re-bakes; nothing upscales silently (a nearest-bake fallback remains only
   for the single frame before a rebuild lands, and logs).
2. **Rank text touching the name**: `SpellBookFrame.xml` (extracted from the MPQ) declares
   SubSpellName as a FIXED 79x18 box anchored TOPLEFT to the name's BOTTOMLEFT at (0,+4), and
   FontStrings default to justifyV MIDDLE - the 10px rank text floats centered in the 18-unit
   box. The renderer had drawn the ink at the box top. The centering term
   `(RankBoxHeight x s - rankEm)/2` now supplies the visible air. (SpellName itself is width
   103, height 0 = auto, maxLines 3 - confirmed from the same extraction.)

Second visual check (Nico): typography and spacing are now "a reasonable pass for the moment".
One further source fix landed from it: the "Spellbook" title was drawn 14px white; the MPQ's
`SpellBookTitleText` inherits `GameFontNormal` - 12px, VanillaGold, MasterFont shadow - and now
does. Its CENTER (6,230) anchor equals the existing (198,26) top-left point (verified).

**Known tooltip CONTENT gaps - noted by Nico, deliberately NOT fixed in this pass** (they are
`SpellTooltipLaw.Build` content-law work, not typography, and must not be conflated with the
renderer effort above):

- Attack's tooltip lacks the "%.2f%% chance to crit" line. In 1.12 this is the chance-to-X law
  (Benilla `ui_tooltip.rs` `chance_line`, law §3-CHANCE): `Effect[0]` 78/20/22/23 selects
  crit/dodge/parry/block against the player's live percentages; ATTACK bypasses the passive
  gate, dodge/parry/block require it. Needs the player's PLAYER_*_PERCENTAGE fields.
- Dodge (and the other passive avoidance spells) are "not really correct" for the same reason -
  their line stack needs the chance line and its gates.
- Related 1.12 lines Benilla implements that MSUI's builder does not yet: "Requires <item
  class>" via ItemSubClassMask.dbc (law §3-EQUIPITEM, e.g. wand Shoot / Parry), "Requires
  <form>" from the Stances mask with met/unmet coloring, reagents with inline red shortfall
  (law §3.8), and the cast-line omission gate widened to TRADE_SKILL/ATTACK Effect[0]
  (law §3.4 - Attack currently shows a cast cell it should omit).

All are `SOURCE_VERIFIED` in Benilla's transcriptions and `UNKNOWN`-to-`STATIC_IMPLEMENTED`
work in MSUI. Pick them up as a content pass after the typography is `USER_VALIDATED`.

## Source-verified spellbook provenance

The following values were read from build-5875 sources. They are source semantics, not automatically
correct ImGui draw sizes.

| Property | Final resolved source value | Inheritance/runtime behavior | Current renderer conversion | Expected on-screen result | Status |
|---|---:|---|---|---|---|
| Font face | `Fonts\FRIZQT__.TTF` | `GameFontNormal` inherits `MasterFont`; `SubSpellFont` declares FRIZQT | Loaded into the MSUI ImGui font atlas | Same letterforms as 1.12 | `SOURCE_VERIFIED`; raster feel still `UNKNOWN` |
| Active name font object | `GameFontNormal`, FontHeight 12 | inherits black `MasterFont` shadow | `12 * _spellbookFontRenderScale * GameplayUiScale()` through `AddText` | Reference visible glyph dimensions, not merely a 12-derived call | source verified; output not matched |
| Active name color | `(1,.82,0)` = RGB `(255,209,0)` | normal runtime color | exact ImGui ABGR packing | yellow reference pixels | `SOURCE_VERIFIED` / `STATIC_IMPLEMENTED` |
| Passive name color | `PASSIVE_SPELL_FONT_COLOR=(.77,.64,0)` = RGB `(196,163,0)` | Lua runtime override for passive entries | exact ImGui ABGR packing | faded/darker yellow, never gray | `SOURCE_VERIFIED` / `STATIC_IMPLEMENTED` |
| Name shadow | black, offset `(1,-1)` in FrameXML coordinates | inherited from `MasterFont` by `GameFontNormal` | second draw at +1,+1 logical screen pixels, scaled and optionally snapped | black right/down edge like 1.12 | source/static done; capture re-measure required |
| Rank/category font | `SubSpellFont`, FontHeight 10 | no `MasterFont` parent and therefore no inherited shadow | `10 * _spellbookFontRenderScale * GameplayUiScale()` | dark-brown smaller line tucked under name | source verified; output/spacing not matched |
| Rank/category color | `(.35,.2,0)` = RGB `(89,51,0)` | declared by `SubSpellFont` | exact ImGui ABGR packing | dark brown | `SOURCE_VERIFIED` / `STATIC_IMPLEMENTED` |
| Spell button | 37x37 | `SpellButtonTemplate` | logical rect times gameplay UI scale | slot chrome and icon footprint match | source/static done; preserve |
| Name box | width 103, auto height, max 3 lines | left of name is button right +4 | logical width and custom wrap | long names wrap like Sword/Mace Specialization in 1.12 | source/static done; capture required |
| Name Y | +4 with rank, +2 without rank | Lua changes anchor depending on secondary text | custom resolved placement | correct name top/baseline | spacing remains wrong |
| Rank box | width 79, height 18 | top-left to name bottom-left, Y +4 | custom placement after rendered name lines | approximately 3 visible pixels between glyph bounds in reference crop | spacing remains wrong |

Source notes:

- `Interface\FrameXML\Fonts.xml` resolves from `patch.MPQ`, shadowing the copy in `interface.MPQ`.
- `Fonts\FRIZQT__.TTF` comes from `fonts.MPQ`.
- The extracted font used during the audit had a SHA-256 beginning
  `8F798FEB09A0E9DC97DAF0A54B52A9A1A7B4CF7103FCB2AA1566AB36AC4`.
- `tools/mpqpeek/mpqpeek.py` is available for direct extraction/search.

## Measurements already made

These measurements came from the supplied 1.12 and MSUI crops **before** the latest 1.25 calibration
pass. They explain why the pass was needed but must not be reused as proof of the current output.

| Sample | Prior MSUI foreground bounds | 1.12 foreground bounds | Finding |
|---|---:|---:|---|
| Attack | about 47x12 px | about 58x13 px | MSUI name substantially too narrow/small |
| Diplomacy | about 78x15 px | about 97x17 px | same renderer-conversion mismatch |
| Racial Passive | about 84x9 px | about 108x11 px | subtext too small/narrow |
| Name/rank vertical separation | overlapping/touching bounds | about 3 visible px | anchor conversion was wrong |

Additional measured facts:

- Surrounding slot chrome differed only roughly 3–4%, while text widths differed roughly 23–29%.
  Therefore this was not principally a global panel-scale error.
- 1.12 active and passive names contain exact black shadow pixels; the prior MSUI output did not.
- The exact 1.12 passive color was `(196,163,0)`, while prior MSUI used gray.
- Both captures already agreed on active `(255,209,0)` and subtext `(89,51,0)` source colors.

Make fresh measurements from the current build. The user's qualitative result supersedes the old crop:
the page is closer now, but spacing remains visibly wrong.

## Why the remaining mismatch existed (superseded 2026-08-04 - see section 6)

The diagnosis below drove the section-6 rework and is kept for the record. The supersampled
atlas + `AddText`-with-logical-size pipeline it describes no longer serves gameplay text; the
glue screen still uses it by design.

- An authored FrameXML `FontHeight=12` is not ImGui `AddText(..., 12, ...)`: the client's em law,
  the stb/FreeType sizing seam, the +1/glyph advance law, and the em line pitch all differ from
  ImGui's conventions. All four are now implemented as derived laws in `GameTextLaw`.
- The tooltip shared the same wrong assumptions through its own 14/12 constants; it now renders
  through the same law (shadowless, per Fonts.xml).
- Smoothness/hinting parity remains the one open rasterisation question (see section 6's known
  divergence note).

## Exact next steps

### Phase A — Freeze and capture current spellbook state

1. Record resolution, DPI behavior, configured UI scale, calibration factor, and pixel-snap state.
2. Capture the current MSUI General tab at the same resolution and UI state as the existing 1.12
   reference. Use a clean crop without a tooltip covering the entries.
3. Measure matched entries: Attack, Diplomacy, Dodge, Mace Specialization, Sword Specialization,
   The Human Spirit, Perception, and Shoot.
4. For each, record visible glyph bounding box, advance width, top/baseline, wrapped-line spacing,
   name-to-subtext gap, shadow pixels, and exact colors.
5. Normalize against the 37x37 slot chrome to prove the comparison uses the same effective UI scale.

Do not change code until the current provenance/measurement table is filled in.

### Phase B — Correct spellbook spacing and raster output

1. Reconcile FrameXML anchor boxes with **rendered glyph metrics**. The anchor is applied to a
   FontString box, not directly to the thresholded top pixel of a glyph.
2. Treat single-line, wrapped two-line, and no-subtext entries separately according to the actual Lua
   anchor mutations. Do not add one global top offset that happens to improve only Attack.
3. Tune the renderer conversion only from paired measurements. Keep source constants in
   `SpellbookLaw`; renderer conversion belongs in the renderer/calibration layer.
4. If glyph geometry becomes correct but edges remain rough, test a dedicated font atlas/size or a
   revised supersample/downscale path. Do not hide rasterization problems by changing the whole panel
   scale.
5. Re-capture and repeat until widths, heights, baselines, line gaps, shadows, and wrapping agree.

### Phase C — Trace and rebuild tooltip typography independently

Before editing the tooltip renderer, produce a separate provenance table for:

- `GameTooltipHeaderText` and `GameTooltipText` font face, size, color, outline/shadow, and inheritance;
- title/rank row behavior;
- left/right double-line anchors and separation;
- per-line vertical offsets;
- wrapped description width and line height;
- backdrop insets, border, alpha, and padding;
- `GameTooltip_SetDefaultAnchor` runtime placement;
- differences between `SetSpell` and action-button tooltip population.

Then use paired captures for at least:

- Arcane Explosion in the spellbook;
- Diplomacy in the spellbook;
- Fireball on the action bar at bottom-right.

Measure the final outer tooltip dimensions as well as the text. Preserve the deliberate spellbook-rank
improvement. Spellbook and action-bar tooltips should share content rules, but may have different
anchors. Do not blindly reuse the spellbook-page 1.25 conversion for tooltip fonts.

### Phase D — Validate and harden

1. Run the build and interface wire checks.
2. Capture current MSUI beside the same-resolution 1.12 reference.
3. Present the measured before/after table and ask Nico to validate by eye.
4. Only after acceptance, persist the final conversion or replace the temporary F6 calibration UI
   with documented renderer law and regression checks.
5. Validate at a second resolution/UI scale so a one-resolution pixel fudge is not mistaken for a
   correct scale rule.
6. Update this prompt and `SYSTEM_GAMEPLAY_UI.md` with actual captured measurements and validation
   status. Never upgrade a status from static to parity without the paired captures.

## Acceptance criteria

Do not call this work complete until all are true:

- active names render in the exact yellow with the correct black shadow;
- passive names render as faded yellow, never gray;
- rank/category text is dark brown, smaller, unshadowed, and correctly nested under the name;
- single-line and wrapped names match 1.12 visible width, height, baseline, and line spacing;
- name-to-rank spacing matches instead of touching or floating too far apart;
- glyph edges have the same smoothness/weight/feel as the reference at the matched resolution;
- the spellbook tooltip matches title/body font size, weight, shadow, line stack, width, height,
  backdrop, and placement;
- the action-bar tooltip uses the same resolved spell data and matches the 1.12 bottom-right layout;
- the intentional spellbook title-line rank remains present;
- corrected icon seating, tab count/brightness, bright hover, global UI scale, and spell FX do not
  regress;
- Nico validates the final paired captures by eye.

## Relevant files

- `MSUIClient/Program.Spellbook.cs`
- `MSUIClient/Program.ActionBars.cs`
- `MSUIClient/Program.GameplayLayout.cs`
- `MSUIClient/Engine/UI/GameTextLaw.cs` (the derived renderer law - section 6)
- `MSUIClient/Engine/UI/SpellbookLaw.cs`
- `MSUIClient/Engine/UI/SpellTooltipLaw.cs`
- `MSUIClient/Engine/UI/UiFont.cs`
- `MSUIClient/Engine/ClientWindow.cs`
- `MSUIClient/Engine/UI/GameplayArt.cs`
- `MSUIClient/Formats/SkillLineCatalog.cs`
- `MSUIClient/Formats/SpellCatalog.cs`
- `tools/interface-wire-check/Program.cs`
- `tools/mpqpeek/mpqpeek.py`
- `docs/current/ui/UI_INTERFACE_PROPORTIONS_HANDOFF.md`
- `docs/systems/SYSTEM_GAMEPLAY_UI.md`

## Last known verification

The following passed after the section-6 derived-law implementation (2026-08-04):

```powershell
dotnet build MSUIClient\MSUIClient.csproj --no-restore
dotnet run --project tools\interface-wire-check\interface-wire-check.csproj --no-restore
git diff --check
```

These checks establish build/static integrity only. They are not visual evidence.

## Required opening response in the next session

Begin by stating, plainly:

1. that the main page is closer but still has a known spacing mismatch;
2. that the tooltip remains a known typography/layout mismatch;
3. that FrameXML source values are not being treated as direct ImGui values;
4. which current capture/calibration facts are still `UNKNOWN`;
5. exactly which paired screenshots and measurements will be collected before the next code change.

