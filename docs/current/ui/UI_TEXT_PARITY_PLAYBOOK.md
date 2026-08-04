# UI Text Parity Playbook — applying the derived text law to any panel

Prepared: 2026-08-04, from the spellbook/tooltip typography session.
Repository: `C:\Users\nico\source\repos\MSUIClient`

## What this is

The spellbook session replaced guessed font conversions with derived 1.12 renderer laws
(`MSUIClient/Engine/UI/GameTextLaw.cs`, and section 6 of
`SPELLBOOK_UI_PARITY_NEXT_AGENT_PROMPT.md`). The laws are global; only the spellbook and spell
tooltip use them so far. **112 raw `dl.AddText(ImGui.GetFont(), logicalSize * s, ...)` call
sites remain across ~30 gameplay panel files** — every one draws scaled text out of the
supersampled atlas and carries the same unit mismatch and softness the spellbook had.

This document is the standing method for migrating any panel. A next-agent prompt for a panel
should say: *"Apply `docs/current/ui/UI_TEXT_PARITY_PLAYBOOK.md` to `<panel>`"* plus the panel's
current visual verdict from Nico. Do not re-derive the laws; do not invent new conversions.

## The laws (settled — do not re-litigate)

1. **Em**: device em = `round(FontHeight × gameplayScale)`, cap 32. FontHeight IS the em.
2. **ImGui seam**: draw size = em × `(hhea.ascent − hhea.descent)/unitsPerEm`, read from the
   actual TTF (FRIZQT = 1.215). Never hardcode per-face factors at call sites.
3. **Advance**: `floor(raw advance) + 1` px per glyph (+1 more only for THICK outline). Baked
   into glyph advances post-atlas-build.
4. **Line pitch**: em + FrameXML `spacing` (default 0). Never ascent+descent.
5. **Measure**: sum of advances (the client's `GetStringWidth`).
6. **Atlas**: exact-size rasters, rebuilt when em targets change (`GameTextLaw.Retarget` +
   `ClientWindow.RebuildFontAtlas`). Nothing upscales silently.

Provenance: Benilla's byte-verified transcriptions —
`C:\Users\nico\Desktop\benilla-main\crates\benilla\src\ui_text\mod.rs` (raster/advance/pitch),
`crates\benilla-ui\src\widget\kinds\mod.rs` (engine layout constants),
`crates\benilla\assets\ui\Fonts.xml` (annotated font objects). Direct MPQ evidence outranks
Benilla if they differ.

## Phase 0 infrastructure — DONE 2026-08-04. What exists:

1. **`MSUIClient/Engine/UI/FontObjectLaw.cs`** — the complete build-5875 Fonts.xml registry,
   transcribed from the MPQ, inheritance flattened: name → { face, height, color, shadow,
   outline }. The `MasterFont` chain carries the (1,-1) black shadow; fonts outside it are
   shadowless; `GameFontHighlightSmallOutline` really declares a shadow; QuestTitleFont's
   shadow is brown. `FontObjectLaw.BakedByDefault` lists the objects the exact-size atlas is
   baked for — **migrating a panel that uses a new font object means adding its name there**
   (a one-line data change). Unbaked objects still draw: nearest bake rescaled, logged.
2. **`MSUIClient/Engine/UI/GameTextLaw.cs`** — multi-face (FRIZQT + ARIALN extracted today;
   MORPHEUS/SKURRI are an `UiFont.Extract` + `Program.cs` faces-list addition away), per-face
   measured em factors, THICK-outline fonts baked as separate instances because their advance
   law differs (+1 extra tracking). Runtime atlas rebuild on em-target changes.
3. **`MSUIClient/Engine/UI/GameText.cs`** — the ONLY API panels call:
   - `GameText.Draw(dl, "GameFontNormal", text, boxTopLeft, s, colorOverride?)`
   - `GameText.DrawCentered(...)`, `GameText.DrawRightAligned(...)`
   - `GameText.MeasureWidth(fontObject, text, s)` — summed advances, device px
   - `GameText.LinePitch(fontObject, s)` — the em; stack wrapped lines with this
   - `GameText.BoxCenteredTop(fontObject, boxTopY, boxHeightLogical, s)` — the justifyV
     MIDDLE law for FIXED-height FontString boxes
   The color override is for runtime Lua recolors ONLY (SetTextColor). Heights, shadows, and
   outlines are never chosen at a call site.
4. **Regression fence** — `tools/interface-wire-check` scans every `Program.*.cs` for raw
   `AddText(ImGui.GetFont(), ...)` draws against a per-file ratchet baseline. New raw draws
   FAIL the check with a pointer to this playbook. When a panel migrates, lower (or delete)
   its baseline entry to lock the migration in — the check prints a reminder when a file is
   under baseline. It also asserts the registry against the Fonts.xml transcription.

Reference migration: `Program.Spellbook.cs` (names, ranks, title, page, both tooltip fonts) is
fully on GameText and sits at baseline 0.

## Per-panel migration loop (the repeatable agent task)

For each panel (one panel per session/PR — do not batch):

1. **Extract the source.** `mpqpeek cat 'Interface\FrameXML\<Panel>.xml'` and the matching
   `.lua`. Build the provenance table per FontString: font object (follow `inherits=`), box
   size (**fixed vs `y="0"` auto**), anchors, `justifyH`/`justifyV` (defaults LEFT is NOT the
   default — H default is CENTER unless declared; V default is MIDDLE), maxLines, and every
   Lua `SetPoint`/`SetTextColor`/`SetText` mutation. Mark unknowns `UNKNOWN`, never guess.
2. **Check Benilla** for engine-side behavior the XML does not describe (line stacking,
   auto-size, anchor gaps): `crates/benilla-ui/src/script/<panel>.rs` and its tests.
3. **Convert the box semantics, not just the draw calls.** The two traps that produced the
   spellbook's spacing bugs:
   - anchors position FontString BOXES, not ink; a FIXED-height box vertically centers its
     text (justifyV MIDDLE default) — the centering slack is visible air in 1.12;
   - auto-height (`y="0"`) boxes are `lines × em` tall.
4. **Replace every `AddText`/`CalcTextSize`** in the panel with `GameText` draws/measures. No
   logical-size × s AddText survives; no per-call-site colors that a font object already owns
   (runtime Lua recolors use the color override and get a comment naming the Lua source).
5. **Register new font objects**: add their names to `FontObjectLaw.BakedByDefault` (and, for
   a new face like MORPHEUS, extract it in `Program.cs` beside FRIZQT/ARIALN). Then lower the
   panel's entry in the wire-check ratchet baseline to its new count (0 when fully migrated).
6. **Verify**: build + `interface-wire-check` (static only), then a same-resolution paired
   capture against the 1.12 client for the panel, measured per the empirical contract in
   `SPELLBOOK_UI_PARITY_NEXT_AGENT_PROMPT.md` (glyph bounds, baselines, gaps, colors, shadow
   pixels). Status ladder applies: `SOURCE_VERIFIED` → `STATIC_IMPLEMENTED` →
   `CAPTURE_MEASURED` → `USER_VALIDATED`. Only Nico's eye upgrades the last step.
7. **Record** the panel's provenance table and verdict in the panel's doc (or this one's log).

## Known pitfalls (all hit once already — check before debugging)

- ImGui.NET's managed `ImFontGlyph` mis-declares the native bitfield layout (48 vs 40 bytes);
  glyph access must stay raw-pointer inside `GameTextLaw` (layout-validated). Never iterate
  `ImFontPtr.Glyphs` elsewhere.
- `ImFontConfig.PixelSnapH` ROUNDS advances at bake; the law needs `floor(raw)+1`. Keep it off.
- The advance law and index cache must both be refreshed after any advance mutation.
- Tooltip-family fonts (`GameTooltip*`) are shadowless; most `GameFont*` carry the MasterFont
  shadow. Quest parchment fonts are shadowless except `QuestTitleFont`. Read Fonts.xml, don't
  pattern-match.
- Remaining accepted divergence: stb is unhinted where the client hints (slightly slimmer
  stems, possibly ~1px/glyph narrower). Benilla ships the same. Report it separately from size
  or spacing misses; the fix, if ever needed, is hinted advances — not a scale factor.

## Panel order (by raw AddText count, highest leverage first)

CharacterPage (15), Mail (7), ActionBars (7), Professions (6), UnitFrames (5), Inventory (5),
Auction (5), Vendor/VanillaUi/Trainer/Quest/Nameplates/Loot/Help/CombatFeedback (4 each),
Social/Keybindings/Guild/Chat (3), the rest (1-2). `Engine/UI/WowSkin.cs` (13) is glue-screen
shared widgets — decide separately whether glue text joins the law or stays supersampled.

## Deferred content work (separate from typography — do not conflate)

Spell tooltip content gaps noted by Nico 2026-08-04 (chance-to-X line, equipped-item and form
requirement lines, reagents, the widened cast-line gate) are `SpellTooltipLaw.Build` content
law, catalogued in `SPELLBOOK_UI_PARITY_NEXT_AGENT_PROMPT.md` section 6. A typography migration
session must not absorb content-law scope, and vice versa.
