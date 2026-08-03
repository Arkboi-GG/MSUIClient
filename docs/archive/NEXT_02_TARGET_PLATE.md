# NEXT 02 — Missing name + health bar floating above the selected target

**Screenshot symptom:** real 1.12 shows "Ragged Young Wolf" + a gold health bar + level
floating over the targeted wolf. MSUI shows nothing over the world unit. This is the 1.12
**V-key nameplate** (`CGNamePlateFrame`) — a 2-D overlay distinct from the overhead *name*
in NEXT_01.

MSUI has no plate renderer today (grep: no nameplate/vplate/DrawTargetPlate).

---

## The benilla vplate anatomy (PROOF — all byte-verified)

Source: `benilla/crates/benilla/src/vplates.rs` (+ `vplates/border.rs`). Module header
`vplates.rs:1-60`: "the toggled health-bar plates over units' heads, a **2-D overlay**." All
geometry is in **gx screencoord units** where 1 unit = the screen **diagonal** √(W²+H²)
(`plate_basis`, `vplates.rs:246-258`).

**Constants** (`vplates.rs:120-165`, verified verbatim):
```
BAR_TEXTURE    = "Interface\TargetingFrame\UI-TargetingFrame-BarFill"   // :120
BORDER_TEXTURE = "Interface\Tooltips\Nameplate-Border"                  // :121  (128x32 art)
PLATE_W = 0.1     PLATE_H = 0.025                                       // :127-128
BAR_OFF_X = 0.0031  BAR_OFF_Y = 0.003125  BAR_W = 0.0804  BAR_H = 0.007025  // :130-133
NAME_H  = 0.01    LEVEL_H = 0.0086                                      // :142-143
PLATE_LIFT = 2.0/3.0   (yd, added to the head anchor)                   // :151
MAX_DIST_SQ = 20*20                                                     // :153
LIT_BOOST = 255.0/215.0   (highlight brighten multiplier)              // :165
PLATE_HOSTILE = [1,0,0,1]                                              // :180
```

**Draw order (back→front)** — `Nameplate-Border` fills the 0.1×0.025 rect and is drawn
**over** the fill so its rounded bevels cap the fill ends (`Z_BORDER=5 > Z_FILL=4`,
`vplates.rs:171-172, 619-626`).

**Health bar:** `UI-TargetingFrame-BarFill`, BOTTOMLEFT + (0.0031, 0.003125), size
0.0804×0.007025. Fill = HEALTH/MAXHEALTH as a **left-anchored crop** (`right = left + frac*W`,
`u1 = frac`, `vplates.rs:630-663`), instant, **no backing behind missing health** (border
alone). **Reaction-tinted** by `plate_tint(rank, is_player)` (`vplates.rs:180-199`) — note
this differs from the overhead-name palette: **players are PURE BLUE `[0,0,1]`** here, not the
pale ring blue. hostile(rank≤1) red / player blue / friendly(rank≥4) green / neutral(2-3)
yellow.

**Name:** Friz, height 0.01, **BOTTOM seated at plate CENTER**, **WHITE** (yellow only while
the plate rect is hovered), black 1px drop shadow (`vplates.rs:664-688`).

**Level + skull:** con-colored level, height 0.0086, CENTER at plate BOTTOMRIGHT +
(−0.0092, +0.0071) (`vplates.rs:690-694`); `UI-TargetingFrame-Skull` replaces the number for a
world boss (creature rank 3) or a hostile ≥10 levels up (`vplates.rs:697-719`). **No cast bar**
on 1.12 plates.

**Anchor:** the overhead head point **+ 2/3 yd** (`PLATE_LIFT`), projected per frame; the
plate's **TOP-CENTER lands on the point** and it hangs below (`vplates.rs:543, 586-591`).
Constant screen size, never uiScale.

**Highlight / show gate:** LIT = mouseover ∪ target — the bar brightens by `LIT_BOOST`
(`vplates.rs:608-609, 648-661`). Gate (`vplates.rs:439-532`): never own unit; never
NOT_SELECTABLE (flags bit 25); dead(health≤0)→no plate; ≤20 yd; enemy-toggle reaction≤neutral,
friendly-toggle reaction≥friendly. A plated unit suppresses its overhead name
(`nameplates.rs:388`).

---

## Minimum MSUI implementation that reproduces the screenshot

The screenshot is just the **current target** plate. Draw one plate over `_selectionGuid`
(optionally hover too). MSUI already has: the art (`UI-TargetingFrame-BarFill`,
`UI-TargetingFrame-Skull` via `_gameplayArt.Handle`, used in `Program.UnitFrames.cs:35,69`),
`ReactionColorU32` / `ReactionTargetTowardPlayer`, the name maps, `Camera.TryWorldToScreen`,
and `HealthFraction`/`Level`/`IsDead`/`IsPlayer` on `WorldEntity`.

Add `Interface\Tooltips\Nameplate-Border` to the art handles (not loaded yet). Skip the
gx-diagonal machinery for the minimum: use native **128×32** scaled by `GameplayUiScale()` and
benilla's fractions ÷0.1 (X) / ÷0.025 (Y):

| element | plate fraction | px @128×32 |
|---|---|---|
| bar offset X | 0.031·W | 3.97 |
| bar offset Y (from bottom) | 0.125·H | 4.0 |
| bar width | 0.804·W | 102.9 |
| bar height | 0.281·H | 9.0 |
| name font height | 0.40·H | 12.8 |
| level font height | 0.344·H | 11 |
| level center X (from right) | 0.092·W | 11.8 |
| level center Y (from bottom) | 0.284·H | 9.1 |

```csharp
private void DrawTargetPlate()   // call from DrawCombatHud, before DrawTargetFrame
{
    if (_selectionGuid == 0 || !_entities.TryGet(_selectionGuid, out WorldEntity u)) return;
    if (u.IsDead && !u.IsPlayer) return;                       // health<=0 gate
    Vector2 display = ImGui.GetIO().DisplaySize;
    float head = MathF.Max(0.3f, 2.2f * MathF.Max(0.01f, u.Scale));
    Vector3 point = u.Position + new Vector3(0, 0, head + 0.667f);   // +2/3 yd lift
    if (!_window.Camera.TryWorldToScreen(point, display, out Vector2 s)) return;

    float sc = GameplayUiScale();
    float W = 128f * sc, H = 32f * sc;
    Vector2 min = new(s.X - W * 0.5f, s.Y);                     // TOP-CENTER on the point
    ImDrawListPtr dl = ImGui.GetForegroundDrawList();

    float frac = Math.Clamp(u.HealthFraction, 0, 1);
    uint tint = PlateTintU32(ReactionTargetTowardPlayer(u), u.IsPlayer); // pure-blue player!
    Vector2 barMin = new(min.X + 0.031f * W, min.Y + H - 0.125f * H - 0.281f * H);
    Vector2 barMax = new(barMin.X + 0.804f * W * frac, barMin.Y + 0.281f * H);
    uint fill = _gameplayArt.Handle(@"Interface\TargetingFrame\UI-TargetingFrame-BarFill");
    if (fill != 0) dl.AddImage((nint)fill, barMin, barMax, Vector2.Zero, new Vector2(frac, 1), tint);

    uint border = _gameplayArt.Handle(@"Interface\Tooltips\Nameplate-Border");
    if (border != 0) dl.AddImage((nint)border, min, min + new Vector2(W, H));   // OVER the fill

    string name = u.IsPlayer ? _playerNames.GetValueOrDefault(u.Guid, "")
                             : _creatureNames.GetValueOrDefault(u.Entry, $"Creature {u.Entry}");
    DrawPlateText(dl, new Vector2(s.X, min.Y + H * 0.5f), name, 0.40f * H, 0xffffffff, bottomSeated: true);
    if (u.Level > 0)
        DrawPlateText(dl, new Vector2(min.X + W - 0.092f * W, min.Y + H - 0.284f * H),
                      u.Level.ToString(), 0.344f * H, ConColorU32(_myLevel, u.Level), bottomSeated: false);
    _vplateUnits.Add(u.Guid);   // so NEXT_01 suppresses this unit's overhead name
}
```

- `DrawPlateText` = measure ink, seat bottom-or-center on the anchor, stamp a black copy at
  +(1,1)px then the colored copy — **plain `ImGui.GetFont()`, no OutlineText** (see NEXT_01).
- `PlateTintU32` mirrors `plate_tint` (`vplates.rs:180-199`): hostile red / **player pure blue
  `[0,0,1]`** / friendly green / neutral yellow.
- `ConColorU32` mirrors `con_color` (`vplates.rs:204-231`): ≥+5 red `0xFFFF1919`, +3/+4 orange
  `0xFFFF7F3F`, −2..+2 yellow, else green `0xFF3FB23F`, then gray. `_myLevel` = the player's
  `Level`.
- Optional polish: skull when hostile ≥+10 or world boss; dim non-target plates to 178/255;
  hover→yellow name. Not needed for the screenshot.

**Verification:** target the wolf — a name + gold/red health bar + level "1" floats above it,
top-center pinned ~2/3 yd over the head; the bar drains left-anchored as it loses health; the
overhead name (NEXT_01) does NOT also draw on the same unit.
