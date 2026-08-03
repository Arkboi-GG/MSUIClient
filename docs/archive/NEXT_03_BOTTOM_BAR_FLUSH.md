# NEXT 03 — Bottom bar not flush with the screen bottom

**Screenshot symptom:** the whole bottom cluster (action slots + micro menu + bags + dragon
endcaps) floats above the bottom edge; in real 1.12 it is perfectly flush.

---

## benilla's anchor law (PROOF)

Source: `benilla/crates/benilla/assets/ui/ActionBar.xml`. Everything is one frame tree
bottom-pinned to the screen with `y=0` offsets.

- **Bar frame** `BenillaActionBar`: `Size 1024x53`, `<Anchor point="BOTTOM"
  relativePoint="BOTTOM"><Offset 0,0>` (`ActionBar.xml:654-657`). Bar bottom = screen bottom.
- **Dwarf art strips**: `256x43`, `<Anchor point="BOTTOM"><Offset y=0>` (`:728-729`) →
  bar-y `10..53`.
- **XP StatusBar** `BenillaExpBar`: `1024x13`, `<Anchor point="TOP"><Offset 0,0>` (`:670-672`)
  → bar-y `0..13`.
- **End caps** `UI-MainMenuBar-EndCap-Dwarf`: `128x128`, `<Anchor point="BOTTOM"
  relativePoint="BOTTOM"><Offset x=-544 y=0>` (left, `:754-756`) and `x=+544 y=0` (right,
  `:758-760`). Each cap's **bottom edge = screen bottom**; horizontal center = bar-center ±544.

So the total bar is exactly 53 px tall, bottom edge at the screen bottom, and the endcaps are
bottom-flush with horizontal centers at bar-center ±544.

---

## Current MSUI state — the arithmetic already resolves flush

MSUI (`Program.GameplayLayout.cs:20`, `Program.ActionBars.cs`):
- `GameplayBarMin.Y = display.Y - 53*scale` → bar bottom = `barMin.Y + 53*scale = display.Y`. ✓
- Dwarf art (`ActionBars.cs:407-408`): `barMin.Y + 10*scale` .. `barMin.Y + 53*scale = display.Y`. ✓
- Endcap (`ActionBars.cs:417-418`): top-left `Y = barMin.Y - 75*scale`; bottom =
  `-75 + 128 = +53` → `display.Y`. ✓  **The `-75` is correct: it is `barHeight - endcapSize
  = 53 - 128`.**
- Buttons (`ActionBars.cs:148`): `display.Y - 40*scale` .. `display.Y - 4*scale` = benilla's
  `(8,4)` Button1 bottom-anchor (`ActionBar.xml:775`). ✓

**So the vertical constants are already flush.** Two real problems remain:

### (a) The genuine endcap defect is HORIZONTAL — 32 px too far inward
benilla left-cap center = bar-center − 544 = `barLeft + 512 − 544 = barLeft − 32`; right-cap
center = `barLeft + 1056`. MSUI puts left-cap top-left at `barLeft − 64` (center `barLeft`)
and right at `barLeft + 960` (center `barLeft + 1024`) — each cap is **32 px too far inward**.
Fix (`ActionBars.cs:417-418`):
```csharp
// left cap center = barLeft-32  → top-left X = -32-64 = -96
Vector2 left  = barMin + new Vector2(-96f, 53f - 128f) * scale;   // was (-64, -75)
// right cap center = barLeft+1056 → top-left X = 1056-64 = 992
Vector2 right = barMin + new Vector2(992f, 53f - 128f) * scale;   // was (960, -75)
```
(Writing the Y as `53f - 128f` documents the flush relationship.)

### (b) If a UNIFORM whole-cluster float persists → it's a DisplaySize/framebuffer (DPI) mismatch
Every bottom element anchors to `ImGui.GetIO().DisplaySize.Y`. If the ImGui backend sets
`io.DisplaySize` to the *logical* window size while the GL viewport
(`Engine/ClientWindow.cs:248 FramebufferSize`, `:682 _gl.Viewport`) is a *larger physical*
framebuffer (HiDPI), then `y = display.Y` lands short of the physical bottom and the whole
cluster floats up by the DPI factor — exactly this symptom.
**Verify `io.DisplaySize == FramebufferSize`.** If they differ, fix `io.DisplaySize` /
`io.DisplayFramebufferScale` in the ImGui backend setup, NOT the bar code. (This lives in the
ImGui.NET controller, outside the source tree — check where `io.DisplaySize` is assigned each
frame; it should equal the physical framebuffer size the GL viewport uses.)

**Hardening:** replace the three duplicated `53f` literals (`GameplayLayout.cs:20`,
`ActionBars.cs:118`, the implicit `-75`) with one `const float GameplayBarHeight = 53f;` so
plate/cap/button anchors can never drift apart.

**Verification:** the dragon endcaps sit hard in the bottom corners (centers at bar-center
±544), the whole bar's bottom pixel row is the screen's bottom row at 1920×1080 and 2560×1440
and any HiDPI display.
