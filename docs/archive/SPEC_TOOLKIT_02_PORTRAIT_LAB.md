# SPEC TOOLKIT 02 — Portrait Lab (in-game) + framing override store

Instrument I2. The "fine-tune a portal" workflow aimed at portraits: cycle any model,
re-bake live under slider control, see the verdict beside the pixels, persist a
per-model override. Requires SPEC 01 stage 1A (PortraitVerdict + ring).

Grounded in `Program.Portraits.cs` as read 2026-07-30: `BoundsPortraitCamera(feet,
modelYaw, modelHeight)` currently hard-codes head fraction **0.92**, window
**0.34·head clamped [0.55, 1.10]**, fovy **0.5 rad ≈ 28.65°**, yaw offset **+0.42**,
pitch **0.02** (inside `PortraitCamera`), near `max(0.02, distance − head)`.
`CreatureBoundsPortraitCamera` uses `framing.EyeHeight/Distance/Height` from
`CreatureRenderer.TryGetPortraitFraming`. These constants are the tunables.

---

## Stage 2A — `PortraitTuning` + override store (engine side)

### New file `MSUIClient/Engine/PortraitTuning.cs`

```csharp
namespace MSUIClient.Engine;

/// The knobs behind bounds-framing. Defaults MUST equal the current hard-coded
/// constants exactly, so behavior with no override is bit-identical.
public sealed record PortraitTuning
{
    public float HeadFraction      { get; init; } = 0.92f;
    public float WindowFraction    { get; init; } = 0.34f;
    public float WindowMin         { get; init; } = 0.55f;
    public float WindowMax         { get; init; } = 1.10f;
    public float FovyDegrees       { get; init; } = 0.5f * 180f / MathF.PI; // ≈28.6479
    public float YawOffset         { get; init; } = 0.42f;
    public float Pitch             { get; init; } = 0.02f;
    public float NearFloor         { get; init; } = 0.02f;
    // Camera-source force: null = normal precedence (authored → bounds).
    public PortraitCameraSource? ForceSource { get; init; } = null;
    public static readonly PortraitTuning Default = new();
}

public sealed class PortraitOverrideStore
{
    // portrait-overrides.json at ClientConfig repo root; same System.Text.Json
    // conventions as VantageStore / ClientConfig (indented, comment-tolerant).
    // Keys: "player:<race>-<gender>"  and  "creature:<displayId>".
    public PortraitTuning? Find(string key);
    public void Set(string key, PortraitTuning t);   // upsert + save immediately
    public void Remove(string key);                  // + save
}
```

Read `Engine/Vantage.cs` (`VantageStore`) first and copy its load/save/root-resolution
idiom exactly, including tolerant-parse behavior on a hand-edited file.

### Hooks in `Program.Portraits.cs` (the only core edits)

1. `BoundsPortraitCamera` gains a `PortraitTuning tuning` parameter and uses its
   fields in place of the literals. All existing callers pass a resolved tuning.
2. Resolution helper on `GameLoop`:
   `PortraitTuning ResolveTuning(string key)` → override store hit, else
   `PortraitTuning.Default`. Player key: build from the character's race/gender
   (grep how race/gender are held — likely on the char-create/appearance state; use
   whatever canonical names exist, lowercase). Creature key: `creature:<DisplayId>`.
3. Camera-source precedence at both bake sites becomes:
   `ForceSource == Bounds` → skip authored; `ForceSource == Authored` → never fall
   back to bounds (bake blank stays blank — that's the point of forcing);
   `null` → exactly today's behavior.
4. `CreatureBoundsPortraitCamera`: apply `tuning.YawOffset`, `FovyDegrees`,
   `Pitch`, `NearFloor` the same way. Do **not** re-derive EyeHeight/Distance from
   HeadFraction/Window in this stage — the creature path keeps `framing.*` as its
   base (deviating from the player path is intentional; note it in code comment).
   Exception: if an override exists for the creature key, and its
   `HeadFraction/WindowFraction` differ from defaults, prefer the player-style
   derivation using the creature's `framing.Height` as the model height — this is
   what makes a stubborn wolf hand-tunable.
5. `PortraitVerdict.CameraSource` reports `Override` when a store hit changed the
   outcome path.

**Gate for 2A:** with an empty/missing `portrait-overrides.json`, every portrait
bakes byte-identically to before (same verdict numbers). This is the B2-style
bit-identity test and it is mandatory before 2B starts.

---

## Stage 2B — the Lab UI

### New file `MSUIClient/Program.DevTools.Portraits.cs` (partial GameLoop)

DevTools layer rules apply (gated by `config.DevTools`; core never calls in). Find
where the existing DevTools HUD draws its sections (grep `Vantage` in `Program.cs` /
the HUD panel) and add a collapsing header **"Portrait Lab"** in the same panel,
drawn from this new file via one call — mirror however the vantage section is
invoked.

**Panel contents, top to bottom:**

1. **Subject row.** Radio: `Player | Target | Specimen`. Player/Target inspect the
   live `_playerPortrait`/`_targetPortrait`. `Specimen` drives the booth (2C); until
   2C lands the radio shows but Specimen is disabled with "(stage 2C)".
2. **Evidence panel.** The selected subject's baked texture drawn at 512 logical px
   (2× the native 256) via the same `AddImage` UV-flip idiom as `DrawPortrait` (copy it —
   OpenGL bottom-left origin). Beside it, read-only text: every field of the most
   recent matching `PortraitVerdict` from the ring (`_verdicts.Recent<PortraitVerdict>`),
   plus `BindPoseHeight`, `VisiblePieces`. If the latest verdict is `Blank`, draw the
   texture anyway (it shows the clear color / partial pixels — that IS the evidence).
   Add a `Save PNG` button → reuse the `DumpFailedPortrait` path with name
   `lab-<subject>` (writes `portrait-diagnostics/lab-<subject>-blank.png`; rename the
   helper's suffix handling if needed so a non-blank save doesn't say "blank").
3. **Tuning sliders** bound to a `_labTuning` working copy (start = resolved tuning
   for the subject): HeadFraction 0.5–1.2, WindowFraction 0.1–1.0, WindowMin 0.1–1.5,
   WindowMax 0.5–2.5, FovyDegrees 10–70, YawOffset −π–π, Pitch −0.5–0.5, NearFloor
   0.005–0.5, and the ForceSource radio (`auto | authored | bounds`). **Any change →
   set the subject's dirty flag** (`_playerPortraitDirty = true` / for the target:
   clear `_targetPortraitUsable` and `_targetPortraitRetryAt = 0` so the existing
   bake loop re-bakes next frame). The bake resolution path must consult the live
   `_labTuning` for the lab-selected subject while the panel is open — thread it via
   `ResolveTuning` (lab override wins over store while open).
4. **Persistence row.** `Save override` → `store.Set(key, _labTuning)`; `Clear
   override` → `store.Remove(key)`; label shows the key and whether a stored
   override exists. `Reset to defaults` restores `PortraitTuning.Default`.
5. **Mask toggle.** Checkbox `Show unmasked bake` — when on, the lab's displayed
   texture skips/undoes the circular mask so corners are inspectable. Implementation:
   for the lab only, re-bake with mask suppressed (add a `bool applyMask = true`
   parameter to the two `ApplyCircularMask()` call sites, driven by "lab wants
   unmasked AND panel open"). The HUD's real portrait consumers keep masked output —
   if that separation is not achievable without a second render target, add a
   dedicated `_labPortrait = new PortraitRenderTarget(gl)` and bake the subject into
   it unmasked instead of touching the live ones (this is the cleaner path — prefer it).

### Hotkeys

`[` / `]` cycle the Specimen list (2C) with the repo's edge-detect idiom (grep
`_flyKeyDown` for the pattern). While the Lab subject is Player/Target the keys do
nothing. No other new hotkeys.

---

## Stage 2C — specimen booth (arbitrary creature models, no server)

Goal: view any `CreatureDisplayInfo` id without finding it in the world.

**Discovery first (30 minutes, write findings in the report):** read how
`_creatures` (`CreatureRenderer`) turns a `WorldEntity` (`DisplayId`, `Scale`,
`Position`, `Orientation`, `Guid`, `IsCreature`) into a renderable — where the
display-id → model/texture resolution happens and what `RenderPortrait(camera,
entity)` actually needs from the entity. Also find where the client enumerates or
looks up `CreatureDisplayInfo.dbc` rows (grep `CreatureDisplayInfo` in `Formats/`
and `World/`).

**Primary path.** Construct a synthetic `WorldEntity` (or the minimal stand-in
`RenderPortrait` needs): `DisplayId` from the specimen list, `Scale = 1`, `Position =
Vector3.Zero`, `Orientation = 0`, a reserved fake guid (e.g. `0xDEAD_0000_0000 +
displayId`) that is never given to `_entities`. Bake it into `_labPortrait` through
**the same** `CreatureBoundsPortraitCamera`/authored precedence path as the live
target bake (extract the target-bake camera-selection block into a helper both call,
so lab and live cannot drift). Model streaming: if the renderer loads models
asynchronously, treat `drawn == false` as "still streaming" and keep the lab dirty
flag set — the existing retry pattern (`NowSeconds() + 1.0`) is the idiom.

**Specimen list.** All display ids the client can enumerate from its
CreatureDisplayInfo catalog, sorted, with a text filter box; `[`/`]` step through the
filtered list. Show `displayId`, model path, and (if cheaply available) model name.

**Fallback** (record which shipped): if `WorldEntity` cannot be constructed outside
the net layer without entanglement, Lab v1 ships with `Player | Target` only, plus
this line in the report: the blocker, the entangled type, and the smallest refactor
that would free it. The GM scenario deck (I8, later spec) covers specimen cycling
live in the meantime.

---

## Test protocol / definition of done

1. **Bit-identity (2A):** no override file → `[portrait]` lines' numbers identical
   before/after the refactor, both subjects.
2. **Slider loop (2B):** open Lab on Player, drag Distance-affecting sliders → bake
   visibly reframes within a frame or two; verdict panel numbers track every change;
   total edit-to-pixels latency feels immediate (no rebuild, no relog).
3. **Override loop:** tune the wolf via Specimen (or Target), `Save override`, relog
   → wolf portrait uses the override (verdict says `camera=Override`); delete the
   JSON entry by hand → back to derived framing (tolerant parse survives).
4. **Cycle loop (2C):** filter "wolf", `]` through several ids — each bakes and shows
   a verdict without touching the server or the live target frame.
5. `devTools:false` → no Lab, no behavior change, overrides still apply (they are
   data, not dev UI — same as vantages.json).

## Live checks for Nico (copy into report verbatim)

1. Open Portrait Lab, subject Player: drag FovyDegrees to 60 and back — portrait
   reframes live both ways; paste one before/after verdict pair.
2. Specimen-cycle 10 creatures with `]` — note any Blank/NotDrawn verdicts and save
   their PNGs (these are the next framing worklist, not bugs to fix now).
3. Tune one bad one until it looks right, Save override, relog, confirm it held.
4. Set `devTools:false` — confirm the game looks and behaves exactly as before.
