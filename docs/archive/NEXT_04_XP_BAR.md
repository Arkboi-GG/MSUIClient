# NEXT 04 — Missing XP bar

**Screenshot symptom:** real 1.12 has the purple XP bar spanning the top of the bottom bar,
above the action slots. MSUI has none.

---

## benilla spec (PROOF)

Source: `benilla/crates/benilla/assets/ui/ActionBar.xml`, `BenillaExpBar` (`:669-711`, header
note `:661`). A `StatusBar`, `1024x13`, `<Anchor point="TOP" relativePoint="TOP"><Offset 0,0>`
(`:670-672`) — the top 13 px of the 53-px bar (the empty band NEXT_03 identified):

- **BACKGROUND** (`:675-679`): a solid texture, `Color r=0 g=0 b=0 a=0.5` — black, half-alpha,
  full 1024×13.
- **Bar fill** (`:661` + StatusBar body): `BarTexture Interface\TargetingFrame\UI-StatusBar`,
  `BarColor r=0.58 g=0.0 b=0.55` (purple). Fill width = `1024 * currXP/nextXP`.
- **4 notch OVERLAY strips** (`:681-700`), file `Interface\MainMenuBar\UI-MainMenuBar-Dwarf`,
  each `256x10`, `<Anchor point="BOTTOM"><Offset y=3>`, x = **−384 / −128 / +128 / +384** from
  bar center, distinct TexCoord bands (verified `:689` for strip 1):

  | strip | x | TexCoord top | bottom |
  |---|---|---|---|
  | 0 | −384 | 0.79296875 | 0.83203125 |
  | 1 | −128 | 0.54296875 | 0.58203125 |
  | 2 | +128 | 0.29296875 | 0.33203125 |
  | 3 | +384 | 0.04296875 | 0.08203125 |

  (left=0 right=1.0 for all four; band height = 0.83203125−0.79296875 = **0.0390625**.)

**Feeding** (`BenillaExpBar_Update`, `ActionBar.xml:471-476`): `SetMinMaxValues(0,
UnitXPMax("player"))`, `SetValue(UnitXP("player"))` — i.e. `PLAYER_XP` / `PLAYER_NEXT_LEVEL_XP`.
MSUI already exposes both: `Net/ObjectFields.cs:69-70` (`PLAYER_XP=716`,
`PLAYER_NEXT_LEVEL_XP=717`) and `:204-205` (`Experience`, `NextLevelExperience`).

---

## MSUI implementation

Call a new `DrawExpBar` from `DrawActionBars` **before** `DrawMainMenuBarArt` (drawn-first ⇒
lowest, so the dwarf plate overlaps it — benilla child order `ActionBar.xml:10-16`). Reuse
`DrawVanillaStatusBar` (`Program.UnitFrames.cs:158-167`, which already stretches
`UI-StatusBar` by a fraction).

```csharp
private void DrawExpBar(ImDrawListPtr dl, Vector2 barMin, float scale)
{
    if (_gameplayArt is null || _net is null ||
        !_entities.TryGet(_net.PlayerGuid, out WorldEntity p)) return;
    uint cur = p.Fields.Experience, max = p.Fields.NextLevelExperience;
    float frac = max > 0 ? (float)cur / max : 0f;

    Vector2 min = barMin;                                     // top of the 53px bar
    Vector2 size = new Vector2(1024f, 13f) * scale;
    dl.AddRectFilled(min, min + size, 0x80000000);            // black a=0.5 background
    DrawVanillaStatusBar(dl, min, size, frac, new Vector4(0.58f, 0f, 0.55f, 1f)); // purple fill

    uint dwarf = _gameplayArt.Handle(@"Interface\MainMenuBar\UI-MainMenuBar-Dwarf.blp");
    if (dwarf == 0) return;
    float center = barMin.X + 512f * scale;
    (float x, float top)[] notch =
        [(-384, 0.79296875f), (-128, 0.54296875f), (128, 0.29296875f), (384, 0.04296875f)];
    foreach (var (x, top) in notch)
    {
        // 256x10 strip, BOTTOM-anchored y=3 inside the 13px band → top = barMin.Y + (13-10-3) = barMin.Y
        Vector2 nmin = new(center + (x - 128f) * scale, barMin.Y);
        Vector2 nmax = nmin + new Vector2(256f, 10f) * scale;
        dl.AddImage((nint)dwarf, nmin, nmax, new Vector2(0, top), new Vector2(1, top + 0.0390625f));
    }
}
```

(Mouse-hover XP tooltip — benilla `BenillaExpBar_OnEnter`, `ActionBar.xml:505-508` — is optional
polish; art-first clears the defect.)

**Verification:** the purple XP bar spans the top of the bottom bar with the four dwarf notch
segments; it fills to `Experience/NextLevelExperience`. At level cap (NextLevelExperience 0) it
reads empty (frac 0) — acceptable.
