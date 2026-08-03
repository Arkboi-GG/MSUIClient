# NEXT — gameplay-UI fix handoff (2026-07-30, from a live 1.12-vs-MSUI screenshot pass)

> **Superseded status note (2026-07-30 09:00):** this is the original research handoff, not the
> current implementation status. The later live review explicitly rejected floating target plates,
> accepted the flush bar and purple XP bar, and produced additional corrections for additive button
> art, cast targeting, spell animation recovery, selected-character persistence, and immediate
> loading cover. Use `July-30-20206-9AM-HANDOFF.md` and `SYSTEM_GAMEPLAY_UI.md` for current status.
> The statement below that nothing was compiled is historical; the current tree has since been
> built and its targeted cast-target tests pass.

Seven defects Nico flagged by comparing a live MSUI screenshot (Dwarf "Dwfpala" in Coldridge
Valley) against the real 1.12 client. **Each doc below finds the benilla source with
file:line proof, states the current MSUI gap, and gives a concrete implementation spec.** All
proof was read from the staged benilla tree at `C:\Users\nico\Desktop\benilla-main` and MSUI at
`C:\Users\nico\source\repos\MSUIClient`. Nothing was compiled (no .NET SDK in the assistant
sandbox) — `dotnet build` is the first gate for whoever implements these.

| # | doc | defect | benilla source (proof) | primary MSUI file |
|---|---|---|---|---|
| 1 | `NEXT_01_NAMEPLATE_TEXT.md` | overhead unit-name text uses the glue yellow-outline font | `src/nameplates.rs` (Outline::None, reaction color, 1px shadow) vs `WowSkin.OutlineText` glue path | new `DrawOverheadName` |
| 2 | `NEXT_02_TARGET_PLATE.md` | no name+health bar floating over the selected target | `src/vplates.rs` (Nameplate-Border + UI-TargetingFrame-BarFill + level, +2/3 yd, reaction tint) | new `DrawTargetPlate` |
| 3 | `NEXT_03_BOTTOM_BAR_FLUSH.md` | bottom bar not flush with screen bottom | `assets/ui/ActionBar.xml:654-760` (BOTTOM anchors, ±544 endcaps) | `Program.ActionBars.cs:417`, `GameplayLayout.cs:20` |
| 4 | `NEXT_04_XP_BAR.md` | no XP bar | `ActionBar.xml:669-711` (BenillaExpBar, purple 0.58/0/0.55, 4 notches) | new `DrawExpBar` |
| 5 | `NEXT_05_ACTIONBAR_PAGE_ARROWS.md` | missing action-bar page up/down arrows | `ActionBar.xml:823-843` (ScrollUp/DownButton, art-only) | `Program.ActionBars.cs:399` |
| 6 | `NEXT_06_PERFORMANCE_BAR.md` | missing ping/performance meter | `ActionBar.xml:520-521, 560-565, 867-892` (tint red>600/yellow>300/green) | new `DrawPerformanceMeter` (+ RTT) |
| 7 | `NEXT_07_PORTRAITS.md` | portraits fall back to flat stand-in art | `src/portrait/framing.rs:173-208` (model-derived framing) | `Program.Portraits.cs` fallback camera |

## The two most impactful

- **#7 portraits** — root-caused: the fallback camera is hard-framed to a ~1.8-yd human, so a
  Dwarf/Gnome/wolf head falls outside the `z∈[1.02,2.08]` window and the bake is blank. The
  client **already logs** `camera=authored|bounds`, the rgb range, and `pieces=`, and dumps
  `portrait-diagnostics/*-blank.png` — read that line FIRST (STEP 0 in the doc); it names the
  exact branch. Fix = model-adaptive framing (mirror benilla `framing::frame`) + near-plane
  clamp + don't latch a failed bake.
- **#3 bottom bar** — the vertical math is already flush; the real errors are (a) endcaps 32 px
  too far inward (`-64→-96`, `960→992`) and (b) a possible `io.DisplaySize` ≠ framebuffer
  (HiDPI) mismatch that floats the whole cluster — verify that in the ImGui backend, not the
  bar code.

## Cross-cutting note on text (#1, #2)

World/unit text must **never** use the glue treatment (`GlueGold` + `WowSkin.OutlineText`'s 8×
black outline + opaque shadow). Overhead names are reaction-colored with a thin 1px shadow;
plate names are white with a 1px shadow. Both draw with plain `ImGui.GetFont()` (Friz) like the
existing `DrawFloatingCombatText`. #1 and #2 are mutually exclusive per unit — a unit with a
plate draws no overhead name (track a `_vplateUnits` set).

## What benilla files back this up (staged locally during research)

`src/nameplates.rs`, `src/vplates.rs`, `src/vplates/border.rs`, `src/names.rs`,
`src/ui_text/{atlas,mod,markup,layout/mod}.rs`, `src/glue/art.rs`, `src/perf.rs`, `src/main.rs`,
`src/target/*`, `src/portrait/{mod,framing,booth,light}.rs`, and
`assets/ui/{ActionBar,UnitFrames,MultiBars,StanceBar,MicroMenu}.xml`.
