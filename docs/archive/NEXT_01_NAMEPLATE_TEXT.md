# NEXT 01 — Overhead unit-name text is drawn in the GLUE font (wrong)

**Screenshot symptom (Nico, 2026-07-30):** in-world unit name text (the overhead nameplate
name over a mob) uses the same heavy **yellow-outline + opaque black shadow** font as the
login / character-select glue screens. Real 1.12 overhead names are **reaction-colored**,
drawn in the world pass, with only a **thin 1px black drop shadow** — never the glue outline.

> Scope note: MSUI today has **no overhead-name renderer at all** (grep: nothing draws unit
> names over the world; its only world text is the floating combat numbers in
> `Program.CombatFeedback.cs:DrawFloatingCombatText`). So this doc is really "add overhead
> names, and do NOT style them like the glue." If Nico is seeing glue-styled text over units,
> it is because whatever draws it (or is planned to) routes through the glue text path. The
> correct target style is below, proven from benilla.

---

## The correct benilla render law (PROOF)

Source: `benilla/crates/benilla/src/nameplates.rs` — "**Overhead unit names** — the 1.12.1
overhead-name system" (module header `nameplates.rs:1-49`).

**Font = Friz Quadrata, atlas default, NO baked outline.** The glyph spec is
`Outline::None, paint_halo: true` — i.e. a thin stamped drop-halo, never the 8-direction
outline ring:
- `nameplates.rs:240` `// UNIT_NAME_FONT = Friz Quadrata, the atlas default` (`path: None`
  resolves to the Friz family, `ui_text/atlas.rs:543,558-568`).
- `nameplates.rs:242-244` `outline: Outline::None, paint_halo: true`.
- The outlined-cell architecture exists ONLY for the `(face,size,radius)` triples the shipped
  `Fonts.xml` names (`atlas.rs:117, 730-764`); the overhead name never requests one.

**Color = reaction-colored, via the ground-ring selector** (NOT constant white, NOT gold):
- `nameplates.rs:14-23` — the render fetches `unit->vtable[0x2c] =
  CGUnit::GetSelectionCircleColor 0x605960`, "the SAME selector as the ground selection ring."
- `nameplates.rs:462-470` — benilla calls the ring's own `ring_variant(...)` and paints the
  answer: NPC dead→gray; else reaction red/orange/yellow/green; player→hostile red,
  PvP-flagged green, party pale variants, unflagged→soft blue. Combat-flash red↔orange pulse
  overrides while meleeing (`nameplates.rs:140-143, 366-374`).

**Scale = unit-height law** (`nameplates.rs:73-75, 120-123`):
```
SCALE_FLOOR = 0.2   // [0x80679c]
SCALE_KNEE  = 4.0   // [0x8112a8]
SCALE_RATE  = 1.5   // [0x8112ac]
d = anchor.z - feet.z
scale = d > 4 ? (d/4)*1.5*0.2 : 0.2     // humanoids sit at the 0.2 floor; > not >=
```

**Geometry / seat / gate:** world-pass, depth-tested billboard (walls occlude — an
acceptable divergence for MSUI's ImGui overlay, which draws through walls like its combat
text already does). Anchor = posed PlayerName attachment, re-read every frame, no smoothing
(`nameplates.rs:24-26`). Block **hangs DOWN** from the top line's baseline at
`anchor.z + lineCount*scale` (`nameplates.rs:30-39`). Show gate: the **current target shows
regardless of cvars** (`selection.target == Some(entity) → true`, `nameplates.rs:406`); a
plated unit (see NEXT_02) draws NO overhead name (`nameplates.rs:388`).

---

## Why the glue font is different (the contrast, PROVEN)

The glue "yellow-outline" look is a deliberate glue-only treatment; it must never touch a
world name. In the MSUI mirror (`Engine/UI/WowSkin.cs`):
- **Gold palette** `GlueGold` (`WowSkin.cs:266`) = `srgb(1.0, 0.78, 0.0)` — matches benilla's
  glue `GOLD` (`glue/art.rs:19`). NOT a reaction color.
- **8-direction black outline** `WowSkin.OutlineText` (`WowSkin.cs:410-419`) stamps the glyph
  black at all 8 offsets of `ow = max(1, sizePx * 0.038)` (`GlueTune.OutlinePx = 0.038`,
  `WowSkin.cs:1108`). Applied in `GlueButton` (`WowSkin.cs:827-828`) and the additive text
  pass `GlueAdditive.DrawQueuedText` (`GlueAdditive.cs:201-208`).
- **Opaque black drop shadow** at `ShadowAlpha = 1.0` (`WowSkin.cs:824-826, 1106-1108`).

So the glue look = `GlueGold` + `OutlineText` (8× black stamp) + opaque shadow. The overhead
name is the opposite on every axis: reaction color, `Outline::None`, thin drop shadow.
**Fix = draw world names WITHOUT the glue treatment.**

---

## Implementation spec (C#/ImGui)

Model on `DrawFloatingCombatText` (`Program.CombatFeedback.cs:167-209`) — it already does
world→screen + a manual shadow with the plain `ImGui.GetFont()` (Friz, `Engine/UI/UiFont.cs:32`)
and NO outline. Reuse `ReactionColorU32` (`Program.UnitFrames.cs:169-180`, which already encodes
the ring palette: dead-gray, player blue `.376,.376,1`, hostile red, friendly green, neutral
yellow) and `ReactionTargetTowardPlayer` (`Program.Targeting.cs:162`). World is Z-up.

```csharp
private void DrawOverheadName(WorldEntity u)   // call per visible unit from DrawCombatHud
{
    if (_vplateUnits.Contains(u.Guid)) return;              // plate wins over name (NEXT_02)
    bool isTarget = u.Guid == _selectionGuid;
    if (!isTarget && u.IsDead && !u.IsPlayer) return;       // dead creature: name only via target
    string name = u.IsPlayer ? _playerNames.GetValueOrDefault(u.Guid, "")
                             : _creatureNames.GetValueOrDefault(u.Entry, "");
    if (name.Length == 0) return;

    float d = MathF.Max(0.3f, 2.2f * MathF.Max(0.01f, u.Scale)); // anchor.z-feet.z ~ model height
    Vector3 point = u.Position + new Vector3(0, 0, d);           // top-of-head anchor
    Vector2 display = ImGui.GetIO().DisplaySize;
    if (!_window.Camera.TryWorldToScreen(point, display, out Vector2 s)) return;

    float px = 15f * GameplayUiScale();
    uint col = ReactionColorU32(ReactionTargetTowardPlayer(u), u.IsPlayer, u.IsDead);
    ImFontPtr font = ImGui.GetFont();
    Vector2 sz = ImGui.CalcTextSize(name) * (px / MathF.Max(ImGui.GetFontSize(), 1f));
    Vector2 pos = new(s.X - sz.X * 0.5f, s.Y - sz.Y);           // block hangs up from the anchor
    ImDrawListPtr dl = ImGui.GetForegroundDrawList();
    dl.AddText(font, px, pos + new Vector2(1, 1), 0xC0000000, name);  // 1px black drop shadow
    dl.AddText(font, px, pos, col, name);                            // reaction-colored fill
}
```

Hard rule for the implementer: **do not call `WowSkin.OutlineText`, do not use `GlueGold`, do
not route through `GlueAdditive`** for any world/unit text. One shadow copy + one reaction
copy, plain Friz.

**Verification:** target a mob — its overhead name is white/reaction-colored with a thin
shadow, not gold-with-a-thick-black-ring. A hostile mob reads red, a critter yellow, a
friendly NPC green. Compare against the login screen text (which stays gold+outlined).
