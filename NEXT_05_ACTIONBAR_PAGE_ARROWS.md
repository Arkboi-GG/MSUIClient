# NEXT 05 — Missing action-bar page up/down arrows

**Screenshot symptom:** real 1.12 shows small up/down arrows near the action slots (the bar
page arrows). MSUI omits them.

---

## benilla spec (PROOF)

Source: `benilla/crates/benilla/assets/ui/ActionBar.xml`, `:823-843` (header note `:20-21`).
Two `Button`s, each `32x32`, `<Anchor point="CENTER" relativeTo="BenillaActionBarArtFrame"
relativePoint="TOPLEFT">`:

- **Up:** `<Offset x=522 y=-22>` (`:829`); `NormalTexture
  Interface\MainMenuBar\UI-MainMenu-ScrollUpButton-Up` (`:831`), `PushedTexture
  ...ScrollUpButton-Down` (`:832`), `HighlightTexture ...ScrollUpButton-Highlight` ADD (`:833`).
- **Down:** `<Offset x=522 y=-42>` (`:838`); `...ScrollDownButton-Up / -Down / -Highlight`.

**They are ART-ONLY / non-functional in benilla** — no `<OnClick>`; header `:20-21`: "Page
up/down + the page number are ART ONLY (we have no bar paging)." A static page number "1"
renders at art-frame CENTER + (30,−5) in `GameFontNormalSmall` (`:763-767`). MSUI is a
single-page bar too, so no paging logic is required — draw the arrows as art.

---

## MSUI implementation

Art frame TOPLEFT = `barMin`. A CENTER anchor at `(522, −22)` means the 32×32 button is
centered there → top-left = `barMin + (522−16, −22−16) = (506, −38)`; down = `(506, −58)`.
Add to `DrawMainMenuBarArt` (`Program.ActionBars.cs:399`), drawing on the same background list:

```csharp
void PageArrow(Vector2 center, string art)
{
    Vector2 min = barMin + (center - new Vector2(16f)) * scale;
    uint tex = _gameplayArt!.Handle(art);
    if (tex != 0) dl.AddImage((nint)tex, min, min + new Vector2(32f) * scale);
    // art-only; add an ADD-blend Highlight image on hover if desired.
}
PageArrow(new Vector2(522f, -22f), @"Interface\MainMenuBar\UI-MainMenu-ScrollUpButton-Up");
PageArrow(new Vector2(522f, -42f), @"Interface\MainMenuBar\UI-MainMenu-ScrollDownButton-Up");
```

Optional: the static "1" page number at `barMin + (512+30, 26.5-5)*scale` via `DrawActionText`.

**Verification:** the two yellow scroll arrows appear stacked just above/right of the action
slots (near the divider), matching the 1.12 screenshot; no paging behavior needed.
