# Toolkit Slice 1 — implementation report 2026-07-30

Build status: BUILT+GATES-PASS

## Stages completed

| Stage | Status | Commit/summary |
|---|---|---|
| Core prerequisite | IMPLEMENTED, GATES PASS | Commit `931f1f2`: restored bounded vanilla-v256 authored-camera parsing, expanded the camera instrument, and corrected false historical verification claims. |
| 1A | IMPLEMENTED, GATES PASS; LIVE UNVERIFIED | Added verdict infrastructure and captured player/target portrait verdicts at the existing bake decision sites. Stage commit: `toolkit: verdicts-1A portrait verdict ring`. |
| 1E | IMPLEMENTED, GATES PASS; LIVE UNVERIFIED | Added the DevTools Verdicts panel with derived channel filters, text filtering, pause snapshots, bottom-following log rows, and row/visible/tail clipboard actions. Stage commit: `toolkit: verdicts-1E add copyable verdict panel`. |
| 1B | IMPLEMENTED, GATES PASS; LIVE UNVERIFIED | Added branch-derived cast-target reasons, cast verdict capture at every existing send/refusal exit, and mechanical reason assertions for the existing pure-law scenarios. |
| 1C | IMPLEMENTED, GATES PASS; LIVE UNVERIFIED | Centralized requested-versus-played animation resolution and added always-ringed, transition-latched animation verdicts for player and creature base/action/spell-hold tracks. |
| 1D | IMPLEMENTED, GATES PASS; LIVE UNVERIFIED | Unified visible action-button state and drawing through `ActionButtonVerdict`; ring/console output occurs only when usability, range, flashing, or checked state transitions. |
| 2A | IMPLEMENTED, GATES PASS; LIVE FBO UNVERIFIED | Added tolerant per-model portrait overrides and routed player/creature bounds cameras through default-identical tunings, including explicit authored/bounds forcing. |
| 2B | IMPLEMENTED, GATES PASS; LIVE UNVERIFIED | Added the DevTools Portrait Lab for live Player/Target evidence, tuning, source forcing, persistence, PNG capture, and a dedicated unmasked bake. |
| 2C | IMPLEMENTED, GATES PASS; LIVE UNVERIFIED | Added the offline specimen booth over all resolvable CreatureDisplayInfo rows, text filtering, bracket/button cycling, synthetic entities, and shared live/specimen creature bake law. |
| 3 | IMPLEMENTED, STANDARD GATES PASS; FULL BATCH G1 FAIL (17 BLANK) | Added in-client `--portrait-batch`, list/limit/diff/unmasked options, complete creature enumeration, shared-path sequential bakes, CSV/PNG/contact-sheet/summary evidence, cache chunking, and meaningful exit codes. Full 10,534-specimen run completed without OOM/hang; its 17 Blank verdicts are preserved as the Portrait Lab worklist. |

## Files touched

| File | New/Edit | What |
|---|---|---|
| `MSUIClient/Engine/Verdicts.cs` | New | `IVerdict`, portrait enums/record, and the 256-entry single-threaded verdict ring. |
| `MSUIClient/Program.Portraits.cs` | Edit | Captured portrait verdicts, loads overrides independently of DevTools, applies tuning/source precedence, and provides the shared live-target/specimen creature bake helper. |
| `MSUIClient/Program.DevTools.Verdicts.cs` | New | DevTools-only copyable Verdicts panel. |
| `MSUIClient/Program.cs` | Edit | Calls the Verdicts and Portrait Lab panels only inside the existing `_config.DevTools`-gated HUD. |
| `MSUIClient/Net/CastTargetLaw.cs` | Edit | Added a reason to each existing pure-law exit without changing target resolution. |
| `MSUIClient/Program.ActionBars.cs` | Edit | Captured cast verdicts from the existing send/refusal locals. Stage 1D also moved the existing per-button predicates into `ComputeButtonVerdict`; stateful drawing now consumes that result directly. |
| `tools/combat-wire-check/Program.cs` | Edit | Added one expected-reason assertion to each existing cast-target scenario; no new scenario was added. |
| `MSUIClient/World/Units/M2Animator.cs` | Edit | Added the single runtime resolution point that classifies cached exact, on-demand bake, explicit authored fallback, or missing without changing clip order. |
| `MSUIClient/World/Units/CharacterRenderer.cs` | Edit | Routed existing player animation choices through the classified resolver and forwarded its results. Track mapping: base `0`, action `1`, spell hold `2`. |
| `MSUIClient/World/Units/CreatureRenderer.cs` | Edit | Routed existing per-display creature animation choices through the same resolver and forwarded its results with `creature:<display>` identity. |
| `MSUIClient/Program.AnimationVerdicts.cs` | New | Captured all resolution results into the ring and emitted warning kinds only when a unit/track choice transitions. |
| `MSUIClient/Program.Net.cs` | Edit | Connected the gameplay creature renderer to the animation verdict capture sink. |
| `MSUIClient/Engine/PortraitTuning.cs` | New | Default-identical tuning record plus case-insensitive, comment/trailing-comma-tolerant `portrait-overrides.json` load/upsert/remove persistence. |
| `MSUIClient/Program.DevTools.Portraits.cs` | New | Player/Target/Specimen lab UI, full verdict evidence, tuning/persistence/PNG controls, dedicated unmasked baking, filtering, and edge-detected cycling. |
| `MSUIClient/Formats/CreatureDbc.cs` | Edit | Exposed the already-parsed display rows as a read-only collection for complete specimen enumeration. |
| `MSUIClient/Net/ObjectFields.cs` | Edit | Added a narrow synthetic-unit descriptor factory for display ID and scale; it neither enters `EntityStore` nor touches the wire. |
| `MSUIClient/World/Units/CreatureRenderer.cs` | Edit | Exposed sorted resolvable display IDs plus normalized model paths for the lab. |
| `tools/portrait-camera-check/Program.cs` | Edit | Diagnosis provenance/raw v256 camera output, Stage 2A default-tuning bit checks, and Stage 2C specimen/wolf-filter enumeration. No resolver/parser behavior changed. |
| `SPEC_TOOLKIT_REPORT_2026-07-30.md` | New | This stage-boundary report. |
| `MSUIClient/Program.PortraitBatch.cs` | New | Batch CLI parsing, batch-only startup, sequential shared-path sweep, outputs, diffing, gates, progress, and summary. |
| `MSUIClient/Engine/PortraitRenderTarget.cs` | Edit | Exposed a top-left-origin RGBA snapshot for CPU contact-sheet composition; `SavePng` consumes the same orientation-normalized bytes. |
| `.gitignore` | Edit | Ignores regenerable `portrait-batch/` output, which measured 1.28 GiB for the full run. |

## Symbol verification

| Cited symbol | Found as | Note |
|---|---|---|
| `BoundsPortraitCamera` | `Program.Portraits.cs:236` before edits | Exists as cited. |
| `CreatureBoundsPortraitCamera` | `Program.Portraits.cs:250` before edits | Exists as cited. |
| `BakeDirtyPortraits` | `Program.Portraits.cs:51` before edits | Exists as cited. |
| `ReadbackStats` | `Engine/PortraitRenderTarget.cs:13` | Exact fields are `SubjectPixels`, `MinRgb`, `MaxRgb`, `MinAlpha`, and `MaxAlpha`; range values are bytes. |
| `_config.DevTools` | `Program.cs` and existing DevTools partials | Exists; Stage 1A adds no UI or dev-only behavior. |
| `NowSeconds()` | `Program.Casting.cs:347` | Exists as a private static member of the same partial `GameLoop`. |
| `VisiblePieces` | `World/Units/CharacterRenderer.cs` | Exists for the player renderer. Creature pieces are unavailable at this capture site, so the specified `-1` is used. |
| `BindPoseHeight()` | `World/Units/CharacterRenderer.cs:409` | Exists for the player. Target framing exposes `Height`, which is recorded as the target bind/framing height. |
| Existing DevTools HUD | `Program.cs:1885-1903` | The master `_config.DevTools` return and existing main HUD were found; `DrawVerdictsPanel()` is invoked only below that gate. |
| `CastTargetLaw.Resolve` consumer | `Program.ActionBars.cs` (`ResolveCastTarget` called by `TryCast`) | The real send/refusal site is here, not in the spec-cited `Program.Casting.cs`. `_net.CastSpell` and all local refusal gates are in the same method. |
| `M2Animator.FindOrBake` | `World/Units/M2Animator.cs` | Existing exact on-demand path confirmed. Stage 1C's `Resolve` preserves it while adding classification and the existing authored fallback lists at one decision point. |
| Exact spell-action paths | `CharacterRenderer.BeginSpellVisual/ReleaseSpellVisual`; `CreatureRenderer` `AuthoredExact` and spell-hold branches | Both still request the authored ID with on-demand baking and no Stand fallback. |
| Action-button state rules | `Program.ActionBars.cs` (`DrawActionBars`, former `SpellActionUsable` and `ActionInRange`) | Usability, range/reach, current/flash, carried-grid, equipped, and stack predicates existed as cited. They now execute once in `ComputeButtonVerdict`. |
| Portrait Lab host | Existing `_config.DevTools` HUD in `Program.cs` | `DrawPortraitLabPanel()` is adjacent to the Verdicts panel and unreachable when DevTools is false. |
| Batch startup seam | `Program.Main`, `GameLoop.Load`, `GameLoop.Update` | Batch mode keeps normal window/GL initialization, subscribes only Load/Update/Closing, then initializes MPQ/DBC/creature/portrait resources and never calls `InitNet` or begins world loading. |

## Deviations

| Spec § | What differed | What I did instead / BLOCKED |
|---|---|---|
| SPEC 01 Stage 1A mapping note | `ReadbackStats` names the ranges `MinRgb/MaxRgb/MinAlpha/MaxAlpha`, not `RgbLo/RgbHi/AlphaLo/AlphaHi`. | Mirrored the real byte fields into the verdict's integer range fields without synthesizing measurements. |
| SPEC 00 §4 gate | The prescribed portrait-camera command initially failed because the committed parser never populated `M2Model.PortraitCamera`. | Resolved in the separately approved core commit `931f1f2`; the exact documented counts now pass. |
| SPEC 01 Stage 1A paper doll | The paper-doll bake has no `Analyze()` call. | Skipped exactly as the spec directs. |
| SPEC 01 Stage 1E generic timestamp | The specified `IVerdict` shape exposed no timestamp even though every verdict record carries `Time` and the generic panel must format it. | Added `double Time { get; }` to `IVerdict`; positional verdict records satisfy it from the same captured timestamp without reflection or recomputation. |
| SPEC 01 Stage 1E console identity | Stage 1A explicitly keeps the legacy `[portrait]` console line unchanged and adds no `[verdict:portrait]` stdout line, while 1E asks copied rows to match a verdict console line. | Panel rows use the stable future-channel shape `HH:mm:ss.f [verdict:<channel>] <ToLine()>`; no new console output was invented. Later transition-only channels can use the same `[verdict:<channel>] <ToLine()>` payload, excluding the panel-only wall-clock prefix. |
| SPEC 01 Stage 1B capture-site citation | The spec names `Program.Casting.cs`, but the sole `CastTargetLaw.Resolve` consumption and `_net.CastSpell` send/refusal path are in `Program.ActionBars.cs`. | Instrumented the actual consumer and its existing local exits. No cast-selection, refusal, or send behavior was changed. |
| SPEC 01 Stage 1C track values | The spec requires an integer track but does not assign values. | Used the renderer's three real layers: base/locomotion `0`, combat/action `1`, and held spell `2`; documented here and shared by player and creature paths. |
| SPEC 02 Stage 2A bit-identity evidence | The offline tools cannot execute the live OpenGL portrait FBO and therefore cannot truthfully produce before/after `[portrait]` subject counts. | Confirmed `portrait-overrides.json` is missing and extended the mandatory camera gate to assert the default player target/window/FOV/distance/near and shared yaw/pitch values are float-bit identical to the replaced literals. Rendering and analysis code are unchanged; live FBO numbers remain explicitly unverified. |
| SPEC 03 §2 asynchronous streaming | The shared creature portrait path is synchronous: `TryGetModel` calls `LoadModel` inline and exposes no pending state or streaming pump. | Measured the synchronous call. A completed call over 10 seconds is recorded `Skipped/timeout`; cancellation cannot safely interrupt inline GL/model creation. No specimen exceeded the limit in the full run. |
| SPEC 03 §3 Windows filenames | Keys contain `:`, which is illegal in Windows filenames. | CSV/contact-sheet identities retain exact `creature:<id>` keys; individual files use the reversible safe form `creature-<id>.png`. |
| SPEC 03 §4 players | Stage 2C found no cheap server-free, naked all-races player construction seam. | Default enumeration remains creatures only; explicit `player:*` list entries emit `Skipped/unsupported-v1`, including the exercised `player:dwarf-male` case. |
| SPEC 03 §5 G1 target | The complete canonical-data sweep found 17 Blank rows. | Preserved the rows, images, failing `G1 blanks: FAIL (17)` summary, and exit 3 semantics. No framing or parser adjustment was made to chase the gate; these rows are the intended Lab worklist. |

## Findings (bugs noticed, NOT fixed)

- The target path retries the bounds camera whenever a drawn bake is blank, even when the initial camera was already bounds-derived. Stage 1A preserves that existing behavior byte-for-byte.
- The authored-camera parser omission and false prior gate claims were resolved/corrected in the separately approved core commit `931f1f2` before Stage 1A was committed.

### Stage 2C discovery — specimen booth

- `WorldEntity` is constructible without `EntityStore` (`Net/Entities.cs:9`). The portrait path reads only `Type == Unit`, descriptor-backed `DisplayId` and `Scale`, plus `Position` and `Orientation`; `RenderPortrait` does not consult live health, combat, spline, selection, or store membership (`CreatureRenderer.cs:415-529`). A reserved fake GUID can remain entirely outside `_entities`.
- The only construction seam is descriptor initialization: `DisplayId` and `Scale` are read-only projections of sparse `ObjectFields` (`ObjectFields.cs:137-138`). The smallest primary-path support is a narrow synthetic-unit factory on `ObjectFields`; no network packet or entity-store insertion is needed.
- Resolution is `CreatureDisplayInfo.Id -> ModelId -> CreatureModelData.ModelPath`, with display/model scales and texture/extended-NPC data folded into `CreatureModelInfo` (`CreatureDbc.cs:217-240`). The table currently has `Find` and `Count` but no enumeration surface, even though all rows are already held in `_rows` (`CreatureDbc.cs:31-33`); exposing a read-only row collection is sufficient.
- Model acquisition is synchronous, not asynchronous: `TryGetModel` calls `LoadModel` immediately on the first cache miss and caches the result (`CreatureRenderer.cs:532-543`). A false portrait draw therefore means unresolved/load failure, not “still streaming”; the lab will retain the one-second retry idiom without claiming asynchronous streaming.
- Creature names are not present in `CreatureDisplayInfo`/`CreatureModelData`; the cheap specimen label is display ID plus normalized model path. The primary synthetic-entity path is viable, so the Player/Target-only fallback is not needed.

## Stage 3 evidence — batch portrait bake

The smoke protocol produced 20/20 CSV rows, 20 individual portrait PNGs, one
2048×2048 labeled contact sheet, and `summary.txt`; visual inspection confirmed
top-left image orientation, row-major cells, circular masking, and bottom-left
display-ID stamps. Result: 19 Ready, 1 Skipped (`creature:4`, model unavailable),
0 Blank, exit 0.

The list/diff protocol used `creature:30` plus `player:dwarf-male`. The player row
was `Skipped/unsupported-v1`. A temporary, subsequently removed bounds override
changed exactly one diff row:

```text
[batch-diff] creature:30: Ready/35248 -> Ready/22879
```

The complete default sweep ran the same in-client shared helper over all 10,534
resolvable display rows. It completed without OOM or hang, crossing every
128-specimen cache-release boundary:

```text
specimens: 10534/10534
Ready: 10505
Blank: 17
NotDrawn: 0
Skipped: 12
G1 blanks: FAIL (17)
G2 subject band: 10261 Ready outside [800, 30000] (informational)
durationSeconds: 1351.256
cachePolicy: release creature model/texture cache every 128 specimens
```

Direct process-code checks returned `2` for an unknown option and `3` for a
one-entry `creature:860` blank run; the green smoke run returned `0`.

Artifacts: 10,534 individual portrait PNGs, 10,534 CSV rows, 165 contact sheets,
`summary.txt`, total 1,306.2 MiB under `portrait-batch/codex-full/`. The 17 Blank
rows comprise ten `AncientProtector` display IDs plus `InvisibleMan`, two
`MouthofKathune01`, three `PortalofKathune`, and `InvisibleStalkerNoName`. This
classification is evidence, not a framing-code change. The 12 Skipped rows are
unavailable models and do not count against G1 by the specified law.

## DIAGNOSIS — authored M2 cameras missing from the parsed model

### Root cause

Commit `74349395f0fbb5021c21bcb0da1d5802b39cab4c` (`Action bars, inventory, portraits, skills books`, 2026-07-30 08:22:41 -0400) introduced an incomplete authored-camera feature:

- It added the nullable `M2Model.PortraitCamera` property, now at `MSUIClient/Formats/M2Reader.cs:55-59`.
- It added the `M2PortraitCamera` record, now at `MSUIClient/Formats/M2Reader.cs:2055-2061`.
- It added `tools/portrait-camera-check`, which immediately requires that property to be populated.
- It did **not** add a camera parse or any assignment to `model.PortraitCamera`. `M2Reader.Parse` runs from `MSUIClient/Formats/M2Reader.cs:1039-1126`; its final parsed blocks are particles/transparency and attachments, and it returns the model without reading the camera arrays at header offsets `0x124..0x130`.

Therefore every successfully parsed M2 has `PortraitCamera == null`. The player path then returns false at `MSUIClient/World/Units/CharacterRenderer.cs:411-418`; the creature path does the same at `MSUIClient/World/Units/CreatureRenderer.cs:409-416`. This is not collateral from `FindOrBake`: the missing assignment was introduced in the same commit that introduced the property and gate.

The same commit added the `SYSTEM_GAMEPLAY_UI.md` claim that the gate passed at DwarfMale 1,224 / HumanMale 1,289 / Wolf 56. That claim is internally inconsistent with the committed implementation: repository-wide history contains no committed `ParsePortraitCamera`, no committed read of header offset `0x124`, and no assignment to `PortraitCamera` in `M2Reader`.

Full-history reconciliation requested before the fix confirms **case (b): the documentation claims never corresponded to committed running code**. `git log --all -S "ParsePortraitCamera" -p --` finds the symbol only in `NEXT_07_PORTRAITS.md`; it never appears in a source file. `git log --all -S "PortraitCamera" -p --` finds only commit `74349395`, where the property/type, consumers, tool, `NEXT_07` claim, and `SYSTEM_GAMEPLAY_UI` passing numbers arrived together without a parser. There is no earlier implementation to restore. Both documents now carry explicit 2026-07-30 correction notes; their coordinate/count statements remain requirements, not historical verification evidence.

### Raw-file and archive evidence

The authorized tool instrumentation prints the absolute directory, complete priority chain, supplying archive, byte count, shared-resolver byte comparison, and raw camera fields before calling `M2Reader.Parse`.

All three models resolve from `patch.MPQ`; the independent provenance read is byte-identical to `MpqMount.ReadFile` (`sharedBytesMatch=True`):

| Model | Bytes | Version | Cameras | Camera lookup | Selected camera | Parsed result |
|---|---:|---:|---|---|---|---|
| `Character\Dwarf\Male\DwarfMale.m2` | 2,134,688 | 256 | `2 @ 0x209050` | `2 @ 0x209290`, lookup[0]=0 | `0x209050` | `PortraitCamera=null` |
| `Character\Human\Male\HumanMale.m2` | 2,577,936 | 256 | `2 @ 0x2753C0` | `2 @ 0x275600`, lookup[0]=0 | `0x2753C0` | `PortraitCamera=null` |
| `Creature\Wolf\Wolf.m2` | 714,000 | 256 | `2 @ 0xAE2C0` | `2 @ 0xAE500`, lookup[0]=0 | `0xAE2C0` | `PortraitCamera=null` |

Each selected 124-byte camera record is well-formed. Examples:

- Dwarf: fov `0.785398`, clip `0.2222..27.7778`, position/target/roll tracks each have one key.
- Human: fov `0.785398`, clip `0.2222..27.7778`, position/target/roll tracks each have one key.
- Wolf: fov `0.950022`, clip `0.2222..27.7778`, position/target/roll tracks each have one key.

This proves the canonical data contains the cameras and the shared MPQ read returns those bytes. The failure is exclusively that the committed parser never consumes them.

### Archive-chain comparison: tool versus client

There is no production resolver difference and no duplicated production chain:

- Tool: `tools/portrait-camera-check/Program.cs:16` constructs `new MpqMount(data)`, where the prescribed argument resolves to `C:\Users\nico\source\repos\MSUIClient\GameData\Data`.
- Client: `MSUIClient/client-config.json` names `GameData\Data`; `ClientConfig.cs:419` resolves it against the repo root; `Program.cs:377` constructs `new MpqMount(_config.ClientDataPath)`.
- Both therefore use `MSUIClient/Formats/MpqMount.cs:63-149`, including the same first-hit read at lines 89-107 and the same private load order at lines 125-149.

Observed shared order, verbatim:

```text
patch.MPQ > patch-4.MPQ > patch-2.MPQ > terrain.MPQ > model.MPQ > backup.MPQ > base.MPQ > dbc.MPQ > fonts.MPQ > interface.MPQ > misc.MPQ > sound.MPQ > speech.MPQ > texture.MPQ > wmo.MPQ
```

The diagnostic-only provenance helper in the tool mirrors that private order solely to name the supplying archive, then verifies its bytes against the real shared resolver. It is not used by the game or by the tool's parse path.

One separate finding is visible but is not causal here: reverse full-path lexical sorting puts `patch.MPQ` before `patch-4.MPQ`, contrary to `MpqMount`'s comment that numbered patches beat the base patch. Both client and tool share that order, and the selected `patch.MPQ` files above contain valid cameras, so it does not explain this failure and was not changed.

**Named follow-up — MPQ patch precedence audit.** Risk: custom patches may be shadowed by `patch.MPQ` or may shadow content they should not. Handle separately with the real 1.12 load-order rules as authority and a two-archive resolution test; do not combine it with portrait parsing.

### Git history since 2026-07-27

Every commit in the requested window touching `M2Reader` or the camera tool is listed below. No commit in the window changed `MpqMount` or `MpqArchive`.

| Commit | M2/archive change | Relevance and current line cites |
|---|---|---|
| `c8fceddcb93956906e13b78a63781377561b48a6` — 2026-07-27 22:56 | Corrected inclusive animation-range iteration; added particle head-cell flipbook parsing. | Animation/particle only: current `M2Reader.cs:374-406`, `617-622`, `856-905`, `1744-1749`. No camera or archive-resolution change. |
| `57ee29df9ec1cb96e6b3d9b3b3bf494fc31d7e05` — 2026-07-29 16:09 | Added sequence blend time and extensive particle global-sequence/twinkle/bone-chain parsing. | Animation/particle only: current `M2Reader.cs:276`, `697-722`, `1223`, `1787-1805`, `1875`, `1900-2052`. No camera or archive-resolution change. |
| `74349395f0fbb5021c21bcb0da1d5802b39cab4c` — 2026-07-30 08:22 | Added `PortraitCamera` property/type and the camera-check tool, without adding the parser that populates the property. | **Offending commit:** current `M2Reader.cs:55-59`, `2055-2061`; absent camera work in `Parse` at `1039-1126`; tool consumes the unpopulated property. No archive-resolution change. |

The `FindOrBake` / spell-animation work does not touch the raw camera header or populate this property. It exposed no regression here; `74349395` shipped the camera consumer and test around a model field that was never wired.

### Item 5 live line — CONFIRM

Provided live scrollback:

```text
[portrait] player bake ready (subject=15001, rgb=0..255, alpha=90..255, camera=bounds, pieces=6)
```

With no preceding `authored bake blank ... retrying` line, `authoredCamera` was false before the first bake. That is exactly the `CharacterRenderer.TryGetAuthoredPortrait` false path caused by `_m2.PortraitCamera == null`; this is not an authored camera that rendered blank.

### Smallest proposed fix — IMPLEMENTED AS APPROVED

Add one bounded vanilla-v256 camera parser inside `MSUIClient/Formats/M2Reader.cs` and call it from `M2Reader.Parse`:

1. Read camera count/offset from `0x124/0x128` and camera-lookup count/offset from `0x12C/0x130`.
2. Resolve signed `cameraLookup[0]`; reject `-1`, out-of-range indices, and any 124-byte record outside the file.
3. Parse the selected record's fov/far/near, position track + base, target track + base, and roll track. For these canonical specimens each track has one static key; use the existing vanilla flat-track bounds conventions rather than inventing a second animation format.
4. Assign exactly one `M2PortraitCamera` to `model.PortraitCamera` and leave models without a valid lookup as null.
5. Re-run the unchanged gate and require the documented 1,224 / 1,289 / 56 results before committing Stage 1A.

No MPQ resolver change was needed. The approved bounded parser shipped in core commit `931f1f2`; it preserves diagonal FOV at parse time, applies the vertex-identical `(x, z, -y)` basis to eye/target, accepts only absent/single static keys, and leaves malformed/animated camera tracks null for bounds fallback.

## Console evidence

### Build gate

```text
Build succeeded.
MSUIClient/Engine/UI/GlueAdditive.cs(141,28): warning CA2014 (pre-existing)
1 Warning(s)
0 Error(s)
```

### Combat-wire gate

```text
combat/movement/targeting foundation checks passed
```

### Portrait-camera gate

```text
Character\Dwarf\Male\DwarfMale.m2: vertices=3246, inFront=3246, inside=1224
  transformed renderer path inside=1224
Character\Human\Male\HumanMale.m2: vertices=3159, inFront=3159, inside=1289
  transformed renderer path inside=1289
Creature\Wolf\Wolf.m2: vertices=557, inFront=557, inside=56
  transformed renderer path inside=56
portrait camera check passed
```

No real `[verdict:portrait]` line is claimed: Stage 1A intentionally keeps the existing `[portrait]` console line unchanged and the new structured verdict is ring-only. A live client/debugger check remains Nico's verification boundary.

No real `[verdict:cast]` line is claimed from the offline gates. The combat-wire gate verifies the existing implicit-self, self-fallback, and selected-unit scenarios now carry `ImplicitSelf`, `SelfFallback`, and `SelectedUnit` respectively; Nico's Holy Light and cooldown checks remain the live verification boundary.

No real `[verdict:anim]` line is claimed from the offline gates. `Exact` and `BakedOnDemand` are ring-only; `Fallback`, `Missing`, and the currently unreachable `Substituted` tripwire are console-visible only when the `(unit, track, requested, played, kind)` choice changes. The live cast-and-move check remains Nico's verification boundary.

No real `[verdict:action]` line is claimed from the offline gates. The first live draw and later changes emit only on the specified `Usability | Range | Flashing | Checked` state tuple. Range still uses the original squared-distance predicate; `DistanceToTarget` adds a square root for evidence only. Nico's out-of-range, low-power, auto-attack, and same-scenario visual A/B checks remain the verification boundary.

Stage 2A empty-store evidence:

```text
portrait-overrides.json: MISSING (empty-store path)
[camera-check] portrait tuning defaults are float-bit identical
```

No live player/target FBO count is claimed. With no override, the authored path is unchanged and every bounds-camera arithmetic operation receives a default value whose float bits match the former literal; the live before/after `[portrait]` pair remains Nico's verification boundary.

Stage 2B has no offline visual claim. The unmasked checkbox writes only to the dedicated `_labPortrait`; `_playerPortrait` and `_targetPortrait` retain their existing circular mask calls. Slider latency, evidence/pixel agreement, PNG capture, and persistence remain live checks.

Stage 2C enumeration evidence:

```text
[dbc] CreatureDisplayInfo: 10534 record(s), 48B each, 10534 indexed
[dbc] CreatureModelData: 430 record(s), 64B each, 430 indexed
[camera-check] portrait specimens=10534, wolfFilterMatches=94
```

The booth uses the primary path, not the Player/Target fallback. Its synthetic unit is never inserted into `_entities`; live target and specimen rendering both call `TryBakeCreaturePortrait`. Actual cycling pixels and Blank/NotDrawn worklist remain live-unverified.

# Toolkit Slice 2 — implementation report 2026-07-30

Build status: BUILT+GATES-PASS

## Slice 2 stages completed

| Stage | Status | Commit/summary |
|---|---|---|
| 4A | IMPLEMENTED, GATES PASS | Replaced the guessed G2 band with measured tiny/full cohorts and added single-readback subject-pixel `meanLuma` evidence. |
| 4B | IMPLEMENTED, GATES PASS | Added the two-entry expected-blank allowlist and separated expected from unexpected G1 failures without changing any portrait pixels or framing. |
| 4C | DIAGNOSIS COMPLETE — EXONERATED; NO FIX | The specimen booth and live creature renderer use the identical display-texture resolution path. Dark rows are already selected by their DBC display data; no booth divergence exists. |
| 4D | ACCEPTED, GATES PASS | Replaced lexical MPQ precedence with the numeric 1.12 order. Nico ruled both changed rows correct real-1.12 behavior; the pre-fix state was the defect. |
| 4E | IMPLEMENTED, GATES PASS | Added a distinct 15-row known-deferred blank worklist; G1 now fails only for blanks in neither classification list. |
| 05 | IMPLEMENTED, GATES PASS; LIVE LOOT CHECK PENDING | Added the always-on 512-packet wire ring, opt-in binary/text recorder, and interleaved wire pseudo-channel. Replay was not built. |

## Slice 2 files touched

| File | New/Edit | What |
|---|---|---|
| `MSUIClient/Engine/PortraitRenderTarget.cs` | Edit | Computes subject-only mean luminance inside the existing classification/readback pass. |
| `MSUIClient/Program.PortraitBatch.cs` | Edit | Adds `meanLuma` after `alphaHi` and measured tiny/full summary cohorts with the 20 most extreme keys. |
| `MSUIClient/Formats/MpqMount.cs` | Edit | Adds pure numeric `OrderArchives`, locale-tier handling, and the once-per-mount priority line. |
| `tools/portrait-camera-check/Program.cs` | Edit | Adds exact numeric/locale ordering assertions and arbitrary-path before/after provenance reporting. |
| `.gitignore` | Edit | Keeps generated portrait runs ignored while tracking the canonical baseline path. |
| `portrait-batch/baseline/verdicts.csv` | New | Accepted post-4D full-sweep CSV; canonical input for future `--diff` runs. |
| `portrait-expected-blank.txt` | New | Exactly `creature:15435` and `creature:16925`, each with its required invisible-by-design reason. |
| `portrait-known-blank.txt` | New | The 15 current unresolved framing/effect blanks, explicitly deferred pending Lab rulings. |
| `MSUIClient/Engine/WireRing.cs` | New | Thread-safe 512-packet metadata/prefix ring plus buffered `.wlog`/`.txt` recorder. |
| `MSUIClient/Net/WorldSession.cs` | Edit | Exception-isolated observation at the single post-send and post-decryption choke points. |
| `MSUIClient/Net/NetworkClient.cs` | Edit | Carries the observer into each world session without changing queue/dispatch order. |
| `MSUIClient/Program.Net.cs` | Edit | Owns the ring/recorder, captures packets, and drains buffered log writes on the game thread. |
| `MSUIClient/Program.DevTools.Verdicts.cs` | Edit | Adds the recording toggle and merged, filterable, copyable `wire` pseudo-channel. |
| `tools/combat-wire-check/Program.cs` | Edit | Verifies ring rollover, opcode naming, file shape, payload cap, and auth suppression. |
| `SPEC_TOOLKIT_REPORT_2026-07-30.md` | Edit | Adds this Slice 2 stage-boundary record. |

## Slice 2 symbol verification and evidence

- `PortraitRenderTarget.Analyze` is the single FBO readback and subject-classification pass; `ReadbackStats` has no other construction site.
- The accepted full CSV independently reproduces 10,505 Ready rows, min 250, p1 23,793, p50 45,116, max 65,536, 47 rows below 8,000, and 13 rows at/above 63,000. The p99 calculation method differs slightly (nearest-rank source value versus floor-index 59,077), but both round to the documented ≈59k and do not affect either specified cutoff.
- Stage 4A's 200-specimen run produced 197 populated Ready `meanLuma` values, `tiny: 1`, `full: 2`, and an empty `diff.txt` against the pre-column `codex-full/verdicts.csv` baseline.
- Stage 4B's full sweep completed 10,534/10,534 in 1,407.2 seconds: 10,505 Ready, 17 Blank, 12 Skipped, `G1 blanks (unexpected): 15 (FAIL)`, `expected-blank: 2`, `tiny: 47`, and `full: 13`. The only expected keys are `creature:15435` and `creature:16925`; AncientProtector and Kathune remain unexpected. The process retained exit 3.

## Stage 4C diagnosis — dark specimen textures

**Verdict: exonerated.** The specimen/synthetic path does not drop or bypass
`CreatureDisplayInfo` texture variations. It enters the same display-id resolver,
cache key, model loader, texture candidate selector, and draw batches as a live
world unit. No fix is proposed.

Evidence and exact path:

1. `CreatureDisplayInfo` parses its three texture stems from fields 6/7/8
   (`MSUIClient/Formats/CreatureDbc.cs:16-24,36-54`). `CreatureModelResolver.TryResolve`
   joins display ID to model and extra rows, then carries `d.Textures` plus the full
   extended-NPC skin/bake/equipment identity into `CreatureModelInfo`
   (`CreatureDbc.cs:189-241`).
2. The live world path calls `_resolver.TryResolve(e.DisplayId)`, derives
   `CacheKey(info)`, then `LoadModel(info)` (`CreatureRenderer.cs:258-270`). The key
   includes all beast texture stems, or the extended race/sex/skin/hair/bake/equipment
   identity (`CreatureRenderer.cs:632-635`), so displays sharing an M2 cannot alias
   across skins.
3. `LoadModel` passes that same `CreatureModelInfo` to `ResolveBatchTexture` for every
   batch (`CreatureRenderer.cs:653-756`). Monster-skin types 11/12/13 select the
   matching display texture slot; character body type 1 uses the extended NPC skin
   candidates (`CreatureRenderer.cs:776-811`). `RenderPortrait` binds the resulting
   `DrawBatch.Tex` objects directly (`CreatureRenderer.cs:486-540`).
4. The batch synthetic entity stores the requested display ID in the ordinary
   `UNIT_DISPLAYID` descriptor (`ObjectFields.cs:92-101`;
   `Program.PortraitBatch.cs:289-299`). `TryBakeCreaturePortrait` calls the same
   creature framing/authored-camera/`RenderPortrait` methods
   (`Program.Portraits.cs:305-351`). `TryGetModel` again resolves
   `entity.DisplayId` through the same resolver/key/loader
   (`CreatureRenderer.cs:543-554`). There is no specimen-only texture decision.

Three dark sheet-1 specimens:

| Display | Model | subjectPx | meanLuma | DBC texture evidence |
|---:|---|---:|---:|---|
| 16 | `Creature\HumanThief\HumanThief.m2` | 34,096 | 0.0000 | texture slots empty; extraId 0 |
| 39 | `Creature\TitanFemale\TitanFemale.m2` | 55,595 | 0.0000 | texture slots empty; extraId 0 |
| 53 | `Character\Dwarf\Male\DwarfMale.m2` | 55,574 | 0.0000 | texture slots empty; extraId 0 |

These rows bake a silhouette/eyes because the selected display rows provide no skin,
not because the booth forgot one. The live renderer receives the same empty
`CreatureModelInfo.Textures` for the same display ID.

Two same-model, different-skin pairs confirm distinct resolution:

| Pair | DBC skin stems | Batch evidence |
|---|---|---|
| HumanThief 16 / 149 | empty / `HumanThiefSkin` | meanLuma 0.0000 / 51.8954; subjectPx 34,096 / 30,032 |
| MineSpider 30 / 283 | `MineSpiderSkinSteel` / `MineSpiderSkinGreen` | meanLuma 25.1040 / 59.7323; subjectPx 35,248 / 45,858 |

The smallest fix for the hypothesized booth defect is therefore **none**. If an
empty-texture display is later ruled wrong in both a live spawn and the Lab, that
would be a shared renderer/data-fallback decision, outside 4C and not a specimen-path
repair.

## Stage 4D — MPQ patch precedence accepted

The pure priority function now orders numbered patches by descending numeric
priority, followed by the unnumbered patch tier and then base archives. Locale
patches follow the same numbered tiers and outrank the global patch within an equal
tier. Unit-style assertions cover the exact sample order, `patch-10 > patch-9`, and
locale/global ties. The installed mount reported exactly one shared priority line:

```text
[mpq] priority: patch-4.MPQ > patch-2.MPQ > patch.MPQ > terrain.MPQ > model.MPQ > backup.MPQ > base.MPQ > dbc.MPQ > fonts.MPQ > interface.MPQ > misc.MPQ > sound.MPQ > speech.MPQ > texture.MPQ > wmo.MPQ
```

The full sweep used `portrait-batch/calibration-4b-full/verdicts.csv` as its
baseline; `portrait-batch/codex-full` was untouched. It completed 10,534/10,534 in
1,305.437 seconds with the same 10,505 Ready / 17 Blank / 12 Skipped totals and
produced two changed rows. `--diff` compared outcome, subject-pixel movement above
15%, and—because both CSVs contain the column—absolute `meanLuma` movement above
10. Every row below names both its model supplier and the two creature-DBC
suppliers before/after. Nico accepted both changes as the client coming into
agreement with real 1.12 archive precedence; the pre-fix state was the defect.
Camera anchors held and no custom-patch shadowing was found.

```text
creature:5299: outcome Ready -> Ready; subjectPx 43479 -> 35309; meanLuma 69.3226 -> 58.1178 (delta -11.2048); model archive patch.MPQ -> patch.MPQ; CreatureDisplayInfo.dbc archive patch.MPQ -> patch-2.MPQ; CreatureModelData.dbc archive patch.MPQ -> patch-2.MPQ; model Creature\GolemHarvestStage2\GolemHarvestStage2.m2
creature:16943: outcome Ready -> Ready; subjectPx 47690 -> 31366; meanLuma 61.9745 -> 63.2271 (delta +1.2526); model archive patch.MPQ -> patch-2.MPQ; CreatureDisplayInfo.dbc archive patch.MPQ -> patch-2.MPQ; CreatureModelData.dbc archive patch.MPQ -> patch-2.MPQ; model Creature\Hippogryph\HippogryphPet.m2
```

Final gates:

```text
Build succeeded. 1 Warning(s), 0 Error(s) — only the pre-existing CA2014 warning.
combat/movement/targeting foundation checks passed
[camera-check] MPQ archive ordering assertions passed
DwarfMale inside=1224; HumanMale inside=1289; Wolf inside=56
portrait camera check passed
```

The accepted post-4D CSV is tracked at
`portrait-batch/baseline/verdicts.csv` and is the canonical baseline for future
`--diff` runs. `portrait-batch/codex-full` remains untouched historical evidence.

## Stage 4E — known-deferred portrait blanks

`portrait-known-blank.txt` is loaded through the same tolerant blank-list parser
as the expected-by-design list but remains semantically separate. It contains the
10 AncientProtector, two MouthofKathune, and three PortalofKathune rows from the
accepted baseline, each marked `deferred 2026-07-30, pending Lab ruling`.

G1 and the process exit now count only blanks present in neither list. Expected
blanks take precedence if a key is accidentally present in both lists; the
known-deferred bucket excludes such overlaps. A new, unlisted Blank therefore still
fails G1 and exits 3.

Focused 17-row gate using all current blanks:

```text
[batch] expected-blank entries=2
[batch] known-deferred entries=15
specimens: 17/17
Blank: 17
G1 blanks (unexpected): 0 (PASS)
known-deferred: 15
expected-blank: 2
[batch] complete: 17/17, blanks=0, exit=0
```

The standard build, combat-wire, and portrait-camera gates all passed; camera
coverage remained 1,224 / 1,289 / 56.

## SPEC 05 — wire tap recorder stage A

The tap is installed at the two shared world-packet choke points. Outgoing capture
runs only after `_stream.Write` has completed; incoming capture runs after header
decryption and the full body read, immediately before `ReceivePacket` returns
(`WorldSession.cs:114-148`). Both calls pass through exception isolation
(`WorldSession.cs:151-159`), so recorder failure cannot turn a successful send into
a client failure or prevent a decoded SMSG from reaching dispatch. Replay stages
B/C were not implemented.

Threading verification: `WorldSession.ReceivePacket` is explicitly worker-thread
only (`WorldSession.cs:19-21`), and `NetworkClient` enqueues that decoded body for
the game loop at `NetworkClient.cs:432`. Outgoing sends may originate from the game
thread, worker, or ping timer. The ring and recorder therefore lock their shared
state. Packet capture copies at most 16 bytes for the UI ring and queues at most
256 bytes for recording; disk writes and five-second flushes run from
`Program.Net.cs`'s game-thread pump.

The always-on ring stores 512 metadata rows. The DevTools-only **Record wire log**
toggle creates a new timestamped `dumps/wire-*.wlog` and companion `.txt`, prints
and copies the path, closes/flushed both on toggle-off, and is disposed after the
network worker on client exit. With `devTools:false` there is no toggle and the
default-off recorder creates no files; the harmless in-memory ring still fills.

Payload storage is suppressed for these world auth/session opcodes:
`SMSG_AUTH_CHALLENGE (0x01EC)`, `CMSG_AUTH_SESSION (0x01ED)`,
`SMSG_AUTH_RESPONSE (0x01EE)`, and `SMSG_WARDEN_DATA (0x02E6)`. Their metadata and
true size remain visible, but stored size is zero. The realmd logon challenge/proof
exchange is handled by `RealmClient`, outside the tapped world stream, and is never
captured.

Offline gate evidence:

```text
combat/movement/targeting/wire foundation checks passed
```

That gate verifies 512-entry rollover order, cached known/unknown opcode names,
the exact binary fields, 256-byte prefix capping, zero-byte auth payloads, and the
human-readable companion. Build and portrait-camera gates also passed; camera
coverage remained 1,224 / 1,289 / 56. The live cast/loot sequence, in-client copy,
and toggle-close/new-file checks remain Nico's live verification boundary.

CSV head:

```text
key,kind,displayId,modelPath,outcome,cameraSource,authoredRetried,subjectPx,rgbLo,rgbHi,alphaLo,alphaHi,meanLuma,pieces,bindPoseHeight,eyeHeight,distance,fovyDeg,nearPlane,elapsedMs,note
creature:4,creature,4,Creature\CrystalSpider\CrystalSpider.m2,Skipped,,false,0,0,0,0,0,0,-1,0,0,0,0,0,90.2832,model-unavailable
creature:13,creature,13,Creature\HUFMCitizenLow\HUFMCitizenLow.m2,Ready,Authored,false,50459,0,255,127,255,106.3794,-1,1.9136,0,0,27,0.2222,210.8103,
```

## Live checks for Nico

1. Paste one `[portrait] player bake ...` line and confirm the HUD portrait matches
   its verdict (ready ↔ live model, BLANK ↔ stand-in).
2. Holy Light with a hostile wolf selected → paste the `[verdict:cast]` line
   (expect `SelfFallback`) and confirm the heal landed on you.
3. Drain mana, walk out of range, start auto-attack → paste the three
   `[verdict:action]` transition lines; confirm each matched the visual state.
4. Cast, then move immediately → paste any `[verdict:anim]` lines (expect none at
   `Missing`/`Substituted`); confirm locomotion resumed instantly.

SPEC TOOLKIT 02:

1. Open Portrait Lab, subject Player: drag FovyDegrees to 60 and back — portrait
   reframes live both ways; paste one before/after verdict pair.
2. Specimen-cycle 10 creatures with `]` — note any Blank/NotDrawn verdicts and save
   their PNGs (these are the next framing worklist, not bugs to fix now).
3. Tune one bad one until it looks right, Save override, relog, confirm it held.
4. Set `devTools:false` — confirm the game looks and behaves exactly as before.

SPEC TOOLKIT 03:

1. Run the full sweep; send the assistant `verdicts.csv` + the first contact sheet +
   `summary.txt`. (This artifact set replaces screenshot-driven portrait debugging.)
2. Skim the sheets by eye for anything framed absurdly that the gates didn't flag —
   name the displayId; it becomes a Lab session, not a code change.

SPEC TOOLKIT 04:

1. Lab-check the AncientProtector (any of its 10 display ids) and the Kathune
   mouth/portal: are they visible creatures in 1.12? Rule each: framing worklist
   (needs an override / giant-framing law) or expected-blank (allowlist it).
2. Lab-check one dark-cohort specimen against a live GM-spawned one of the same
   display id: same appearance? (Feeds 4C's verdict.)
3. Review 4D's diff.txt: every changed row is your ruling — expected custom
   content surfacing, or a problem.

SPEC TOOLKIT 05:

1. Record a session: enter world, cast once, loot once, toggle off. Send the
   assistant the `.txt` — this replaces "the loot window misbehaved" prose
   forever.
