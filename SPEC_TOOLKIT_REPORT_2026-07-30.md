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

## SPEC 06 — F10 gameplay dump

`Program.DevTools.GameplayDump.cs` adds the DevTools-only F10 gameplay-plane
capture. F10 uses the existing edge-detect pattern (`Program.cs:1236-1240`);
the Scene and vantage panel and Verdicts panel expose the same arm operation as
**Dump gameplay (F10)** / **Dump (F10)**. The request is armed before the next
gameplay HUD is built (`Program.cs:1908`) and written from `OverlayTop` after the
ImGui pass (`Program.Net.cs:1254-1259`), so the collected rectangles and paired
screenshot belong to one completed frame.

The JSON keys are emitted in the specified order: name/time/map, scenario,
portraits, actionBar, animator, verdicts, wire, and layout. Scenario health,
power, and lootability reuse the established descriptor accessors
(`Net/ObjectFields.cs:151-174`). Pending, auto-repeat, and queued-melee spell ids
come directly from the action state (`Program.ActionBars.cs:23-25`), while cast
bar spell/stage/timing come directly from `Program.Casting.cs:11-15`. No new
descriptor offset or field interpretation was introduced.

The armed frame records all 12 action slots and their same-call
`ComputeButtonVerdict` results, all eight micro buttons, four bag slots plus the
backpack, the action/micro/bag containers, the active cast bar, and whichever
player/target frames were actually drawn. Collection occurs at the draw sites
(`Program.ActionBars.cs:214-359`, `Program.Inventory.cs:189-205`,
`Program.Casting.cs:282`, and `Program.UnitFrames.cs:20-21`), so screen rectangles
are the values consumed by ImGui rather than a dump-time re-derivation. The
collector is only active for the armed frame.

After the JSON write, default-framebuffer RGBA readback is vertically flipped and
passed through the extracted `PortraitRenderTarget.SaveRgbaPng` encoder. PNG
failure is isolated and leaves a successful JSON dump intact. Success prints the
specified one-line `[gdump] wrote ...` summary and copies the JSON path.

One bounded omission is recorded: there is no target-portrait dirty field or
accessor in the current client (only `_targetPortraitUsable` and retry/request
state), so the dump does not invent one. Player `dirty` is reported from the
existing `_playerPortraitDirty` field. This follows SPEC 06's no-new-descriptor-
guess rule.

Offline gates:

```text
Build succeeded. 1 Warning(s), 0 Error(s) — only the pre-existing CA2014 warning.
combat/movement/targeting/wire foundation checks passed
[camera-check] MPQ archive ordering assertions passed
DwarfMale inside=1224; HumanMale inside=1289; Wolf inside=56
portrait camera check passed
```

The OpenGL framebuffer, clipboard, two-resolution layout comparison, repeated
dump comparison, and `devTools:false` inertness require a live gameplay window
and remain Nico's live verification boundary.

## SPEC 01 Stage 1F — partitioned verdict history

The live CHECKS_GAMEPLAY A/C finding was reproduced in the capture path. The
animation callback's console warning was transition-latched, but `_verdicts.Add`
ran before that latch, so identical per-frame `Exact`/`BakedOnDemand` choices
still consumed the shared 256-row history. Panel filtering could only filter the
surviving rows and could not recover evicted login-time portrait evidence.

`VerdictRing` now owns independent channel rings with the specified capacities:
portrait 64, cast 128, action 512, and anim 1024
(`Engine/Verdicts.cs:134-205`). `Add` routes through `IVerdict.Channel`.
`Snapshot(channel)` preserves each channel's oldest-to-newest retained history;
`SnapshotAll()` merges all retained rows by verdict time, with insertion sequence
as the stable tie-break. The F10 `verdicts` block uses `SnapshotAll()` and keeps
its existing JSON shape (`Program.DevTools.GameplayDump.cs:112,242-248`). No
verdict record changed.

The Verdicts panel builds its display from the four independent snapshots
(`Program.DevTools.Verdicts.cs:51-93`). Channel and text filters therefore operate
on each channel's complete retained ring rather than a shared-ring remainder.
**Hide routine anim (Exact/BakedOnDemand)** defaults on and suppresses those rows
only while rendering/copying the visible view; capture and the F10 merged snapshot
remain complete. Pause snapshots all four channel histories independently.

Animation capture is now genuinely transition-latched. The key remains
`(unit, track)` and the compared state is `(requested, played, kind)`; an unchanged
state returns before constructing or adding an `AnimChoice`
(`Program.AnimationVerdicts.cs:7-31`). A changed transition is captured once, and
the existing fallback/missing/substituted console behavior is unchanged.

The combat-wire gate now also floods the anim ring, proves the portrait row
survives, verifies all four exact capacities, and verifies the 1,728-row merged
snapshot is time ordered. Final gates:

```text
Build succeeded. 1 Warning(s), 0 Error(s) — only the pre-existing CA2014 warning.
combat/movement/targeting/wire foundation checks passed
[camera-check] MPQ archive ordering assertions passed
DwarfMale inside=1224; HumanMale inside=1289; Wolf inside=56
portrait camera check passed
```

CHECKS B2 is re-specified: this client has no cooldown implementation yet, so the
cooldown-refusal check becomes a pending-cast refusal. Recast a different spell
mid-cast and expect `Sent=false reason=PendingCast`. The reason already exists at
`Net/CastTargetLaw.cs:17`, and the existing guard already emits precisely that
verdict at `Program.ActionBars.cs:114-118`; no gameplay-code change was required.

Live acceptance remains: stand two minutes in a creature-heavy area, then verify
the login-time portrait rows remain visible/copyable and CHECKS B/C cast/action
rows are findable with routine animation hidden.

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

SPEC TOOLKIT 06 (verbatim):

1. Reproduce any current visual gripe, press F10, send the JSON (+PNG) to the
   assistant instead of describing the gripe. That exchange is the toolkit's
   acceptance test.

## SPEC 07 — Stage 7A character-variant diagnosis (HARD STOP)

Stage 7A is diagnosis only. No archive order, renderer, DBC parser, texture law,
or gameplay behavior was changed. This section is the required ruling packet;
work stops here until Nico rules on it.

### Stage status

| Stage | Status | Result |
|---|---|---|
| 7A H1 archive-supplier audit | Complete | 61,499 relevant archive members compared; 38 supplier changes found. Player `CharSections`/hair-geoset/facial-hair data did not change supplier. |
| 7A player trace | Partial — named case missing | The request retained its `[FILL IN]` placeholder. The available captured Human male `0/0/0/0/0` case was traced as a control, but cannot be represented as Nico's wrong case. |
| 7A helm/NPC trace | Complete | Deputy Willem display 2072 / extra 675 / head display 14964 resolves to a present HuM helm model and texture. The renderer hides his hair for that helm but never mounts the authored NPC head model. |
| 7A cape trace | Partial — named case missing | The built-in cape diagnostic display 13963 was traced as a control and resolves correctly. No reported wrong cape display id was supplied. |
| 7A H2 scoped history | Complete | Relevant commits since 2026-07-27 inventoried below. |
| 7A boundary | **HARD STOP** | Diagnosis packet only; no 7B fix is authorized. |

### Files changed in 7A

| File | Change |
|---|---|
| `tools/portrait-camera-check/Program.cs` | Added read-only `--variant-suppliers` and `--variant-trace` diagnostic modes. |
| `SPEC_TOOLKIT_REPORT_2026-07-30.md` | Appended this evidence and ruling packet. |

Pre-existing user changes in `vantages.json` and the untracked
`SPEC_TOOLKIT_07_CHARACTER_VARIANTS.md` were not modified or staged.

### Symbol verification

| Contract / symbol | Verified location | Finding |
|---|---|---|
| `CreatureDisplayInfoExtra` equipment decode | `MSUIClient/Formats/CreatureDbc.cs:121-175,200,240` | Ten display ids are preserved in canonical head-through-tabard order. |
| NPC equipment/geoset application | `MSUIClient/World/Units/CreatureRenderer.cs:831-844` | Body equipment affects geosets; head display affects helm visibility only. No head or shoulder attachment is created. |
| Creature attachment draw path | `MSUIClient/World/Units/CreatureRenderer.cs:531-560` | Draws server virtual weapons; it does not consume authored extra head/shoulder displays. |
| Player async appearance mapping | `MSUIClient/World/Units/CharacterRenderer.cs:919-1029` | Resolves skin, face, hair, facial hair, geosets, and baked layers. |
| `CharSections` lookup | `MSUIClient/Formats/DbcReader.cs:188-284` | Parses the ten-field row but does not retain/use the final Flags field when choosing duplicate keys. |
| Cape texture law | `MSUIClient/World/Units/CharacterRenderer.cs:1290-1370` | Includes canonical `Item\\ObjectComponents\\Cape\\<stem>.blp`. |
| Diagnostic control cape | `MSUIClient/World/Units/CharacterEquipment.cs:430` | Display 13963 is the existing `Cape texture test`. |

Independent Benilla comparison:

- `crates/benilla-formats/src/creatures.rs:331-365` decodes the same extra
  appearance and ten equipment slots.
- `benilla/src/entities/equipment.rs:541-585,625-670` consumes NPC head and
  shoulder display ids, mounts the helm at the HELM attachment, and resolves
  `Head\\<stem>_<race><sex>.m2` independently of its object texture.
- `benilla/src/entities/attach/char_skin.rs:404-428` uses the same canonical cape
  object-component path.
- `benilla/src/entities/attach/sections.rs:78-150,180-220,265-277` confirms the
  section keys, hair variation-1 fallback, layer order, and standard-row flag law.

### H1 — archive order / supplier delta

The corrected MPQ precedence changed the winning supplier for 38 of 61,499
relevant candidates:

```text
DBFilesClient: AreaPOI, AreaTable, AreaTrigger, Cfg_Categories,
CreatureDisplayInfo, CreatureModelData, CreatureType, DurabilityCosts,
EmotesTextData, Faction, GroundEffectTexture, ItemSet, LFGDungeons,
MailTemplate, Map, NamesProfanity, QuestInfo, QuestSort, SkillLine,
SkillLineAbility, SkillRaceClassInfo, SkillTiers, SoundEntries, Spell,
SpellFocusObject, SpellItemEnchantment, SpellMechanic, SpellVisual,
TaxiNodes, WMOAreaTable, WorldSafeLocs, WorldStateUI:
    patch.MPQ -> patch-2.MPQ
DBFilesClient\\ItemDisplayInfo.dbc:
    patch.MPQ -> patch-4.MPQ
ITEM\\ObjectComponents\\HEAD\\Helm_Plate_RaidWarrior_C_01_NiM.m2:
    patch.MPQ -> patch-2.MPQ
ITEM\\ObjectComponents\\HEAD\\Helm_Robe_DungeonWarlock_A_01_TaF.m2:
    patch.MPQ -> patch-2.MPQ
ITEM\\ObjectComponents\\WEAPON\\Sword_1H_Stratholme_D_01.m2:
    patch.MPQ -> patch-2.MPQ
ITEM\\TEXTURECOMPONENTS\\TORSOUPPERTEXTURE\\Robe_AhnQiraj_A_Green_Chest_TU_U.blp:
    patch.MPQ -> patch-2.MPQ
ITEM\\TEXTURECOMPONENTS\\TORSOUPPERTEXTURE\\Robe_AhnQiraj_A_Purple_Chest_TU_U.blp:
    patch.MPQ -> patch-2.MPQ
```

`CharSections.dbc`, `CharHairGeosets.dbc`,
`CharacterFacialHairStyles.dbc`, and the sampled Character textures are absent
from the delta. H1 therefore does **not** explain the sampled player appearance.
H1 remains a general item-variant risk because `ItemDisplayInfo.dbc` changed, but
the old and current rows for Willem's helm 14964 and the cape control 13963 are
field-identical and resolve to the same assets. H1 does not explain either trace.

### H2 — scoped commit inventory

| Commit | Relevant surface | Diagnostic ruling |
|---|---|---|
| `be1fbbd` (2026-07-30) | `CharacterRenderer`, Glue/Net | Added the current asynchronous player appearance preparation path. Temporally plausible for a player-only regression; the available control agrees with Benilla, so the named failing input is required to rule it. |
| `13de8f5` (2026-07-30) | `CharacterRenderer`, `CreatureRenderer`, `AttachedItemRenderer` | Split/shared asynchronous asset work; no demonstrated variant-key law change in the traced rows. |
| `10ecbc9`, `fd73621`, `dbe637a`, `0210014`, `52c45ff` | diagnostics, portrait catalog/cache, animation/wire | No demonstrated face/hair/cape/head selection-law change. |
| `7434939` (2026-07-29) | attachments/equipment and render hosts | Relevant attachment plumbing, but current creature attachments remain limited to virtual weapons. |
| `57ee29d` (2026-07-29) | character creation/select wiring | Relevant source of player customization inputs; no named failing input supplied for comparison. |
| `d055c65` (2026-07-27) | initial NPC/player renderer pipeline | `git blame` places the Willem `BuildNpcEquip` omission here: authored head display hides hair but is never attached. |

The `CharSections` lookup's omission of Flags and the current cape candidate law
predate the scoped history. Flags are a real robustness gap, but the installed
Human male control rows happen to place the standard (`flags=0`) row first, so it
is not claimed as the cause of the reported player symptom without the requested
named case.

### H3 — concrete traces and root-cause rulings

| Case | Exact trace | Root-cause ruling | Smallest later fix (not implemented) |
|---|---|---|---|
| Player control: Human male `skin=0 face=0 hairStyle=0 hairColor=0 facialHair=0` | `CharSections=patch.MPQ`; standard skin `HumanMaleSkin00_00`; face `FaceLower00_00` + `FaceUpper00_00`; style-0 hair row is blank and canonical variation-1 fallback selects `Hair03_00` plus scalp layers; hair/facial geoset rows are present. | Control matches Benilla. Reported wrong-player root cause is **UNRULED: named failing creation values and expected appearance were not supplied**. | Once supplied, trace those five values through both input and async renderer. If a duplicate-key flag collision is proven, retain Flags and prefer standard rows; if sync/async divergence is proven, converge on one resolver. |
| Deputy Willem, NPC 823, display 2072 | Display 2072 -> Human male model 49 -> extra 675; extra head display 14964; HuM model `Helm_Plate_B_01Stormwind_HuM.m2` is in `patch.MPQ`; matching head BLP is in `texture.MPQ`. | **H3, never-correct renderer omission introduced by `d055c65`.** `BuildNpcEquip` applies helm visibility (hiding hair) but never mounts extra equipment head/shoulder models. This directly explains a bald, unhelmeted Willem despite valid assets. | Feed `ExtEquipment[0]` and `[1]` through the shared attached-item renderer using race/sex suffix resolution, alongside (not replacing) virtual weapons. |
| Cape control, item display 13963 | `ItemDisplayInfo=patch-4.MPQ`; `Cape_Mage_A_01Black` -> `Item\\ObjectComponents\\Cape\\Cape_Mage_A_01Black.blp` in `texture.MPQ`; legacy/current rows identical. | Control matches Benilla. Reported wrong-cape root cause is **UNRULED: no failing cape display id supplied**. | Trace the actual display id before selecting a fix; compare legacy/current row only if it is among changed patch content. |

Deputy Willem's NPC/display identity was cross-checked against the local database
name data and a public 1.12 database entry for NPC 823 / model 2072. The remainder
of the trace is entirely from the installed client data.

### Deviations / blocked inputs

1. The spec request's named wrong-player-variant field remained literally
   `[FILL IN]`. Stage 7A does not invent the player's intended creation choices.
2. The symptom named capes generally but supplied no failing cape display id. The
   existing display-13963 diagnostic is recorded only as a known control.
3. The prior report contains no completed Stage 1G supplier diagnosis and no
   completed NPC extra-display diagnosis, so both were folded into 7A as ordered.
4. No fix was attempted. The proposed patches above are boundaries for a later
   ruled stage, not authorization.

### Console evidence

```text
[camera-check] portrait tuning defaults are float-bit identical
[camera-check] MPQ archive ordering assertions passed
[variant-supplier] candidates=61499 changed=38
[variant-trace] player=human-male skin=0 face=0 hairStyle=0 hairColor=0 facialHair=0 CharSections=patch.MPQ
[variant-trace] willem display=2072 modelId=49 extraId=675 scale=1 CreatureDisplayInfo=patch-2.MPQ
[variant-trace] willem-extra id=675 race=1 sex=0 skin=4 face=6 hair=0/8 facial=6 equipment=14964/7541/7223/0/7224/7225/7255/0/7698/6255 CreatureDisplayInfoExtra=patch.MPQ
[variant-trace] willem-helm-model path=Item\\ObjectComponents\\Head\\Helm_Plate_B_01Stormwind_HuM.m2 supplier=patch.MPQ
[variant-trace] cape-test display=13963 ItemDisplayInfo=patch-4.MPQ model='' texture='Cape_Mage_A_01Black'
[variant-trace] cape-test-texture capePath=Item\\ObjectComponents\\Cape\\Cape_Mage_A_01Black.blp capeSupplier=texture.MPQ
```

### Mandatory gates

```text
dotnet build MSUIClient.sln -c Debug
Build succeeded. 1 Warning(s), 0 Error(s).
The warning is the pre-existing CA2014 at Engine/UI/GlueAdditive.cs:141.

dotnet run --project tools\\combat-wire-check\\MSUICombatWireCheck.csproj -c Release
combat/movement/targeting/wire foundation checks passed

dotnet run --project tools\\portrait-camera-check\\MSUIPortraitCameraCheck.csproj -c Release -- GameData\\Data
[camera-check] portrait tuning defaults are float-bit identical
[camera-check] MPQ archive ordering assertions passed
DwarfMale inside=1224; HumanMale inside=1289; Wolf inside=56
portrait camera check passed
```

### Live checks / ruling requested

**HARD STOP.** Nico must supply/rule the following before any 7B implementation:

1. Supply the exact wrong player case: race, sex, skin, face, hair style, hair
   color, facial hair, and what was expected versus rendered.
2. Supply at least one wrong cape item/display id (and expected versus rendered).
3. Rule whether to proceed with the isolated Willem head/shoulder attachment fix.
4. Rule whether the player Flags hardening belongs in 7B only if the named trace
   proves a collision, or should be accepted as preventive hardening separately.

## SPEC 07 — revised Stage 7A NPC head-texture ruling packet (HARD STOP)

Nico withdrew the named player-variant case and the cape case. The replacement
Stage 7A symptom is extra-dressed humanoid NPC head texturing: clothing/armor from
the baked body atlas appears on hair or scalp. This revision is report-only. No
renderer, parser, batch tool, archive law, or game data was changed.

### Revised stage status

| Stage | Status | Result |
|---|---|---|
| Player case | Withdrawn | Removed from the active 7A ruling surface. |
| Cape case | Withdrawn | No failing cape case exists; 7B's items-axis diff owns cape coverage. |
| NPC head-texture trace | Complete | Willem plus a hair-bearing authored-clothing control prove two per-geoset binding faults. |
| VMaNGOS mapping capability | Attempted read-only; server unavailable | Connection details and the zero-query result are recorded below. Installed DBC data supplied the trace mappings. |
| Revised 7A boundary | **HARD STOP** | 7B/7C sequencing is recorded, but none is implemented. |

### Files changed in revised 7A

| File | Change |
|---|---|
| `SPEC_TOOLKIT_REPORT_2026-07-30.md` | Appended this revised diagnosis and ruling packet. |

Temporary read-only trace programs were created under the Windows temporary
directory, executed, and removed. The repository's diagnostic and production
sources are unchanged. The pre-existing `vantages.json` change and untracked
`SPEC_TOOLKIT_07_CHARACTER_VARIANTS.md` remain untouched.

### Symbol verification and commit correlation

| Symbol / behavior | Current location | Provenance / finding |
|---|---|---|
| NPC batch preparation | `MSUIClient/World/Units/CreatureRenderer.cs:1163-1211` | Every batch independently calls the common texture resolver, but supplies no geoset/region context. |
| NPC texture resolution | `MSUIClient/World/Units/CreatureRenderer.cs:1386-1428` | Type 1 always prefers the baked NPC atlas. Type 6 has no branch and falls through to the display's creature texture array, which is empty for the traced character-model NPCs. |
| Player head exception | `MSUIClient/World/Units/CharacterRenderer.cs:3154-3169` | Type-1 hair/scalp/ear pieces switch from the dressed atlas to `_bareSkin` per draw. There is no creature equivalent. |
| Player CharSections hair binding | `MSUIClient/World/Units/CharacterRenderer.cs:919-1088` | Resolves the type-6 hair-mesh BLP by race/sex/style/color, including the literal-1 fallback. There is no creature equivalent. |
| Shared geoset selection | `MSUIClient/Formats/CharacterGeosets.cs:80-151` | Correctly selects the NPC's hair/scalp and equipment geosets; selection is not the texture fault. |
| CharSections lookup | `MSUIClient/Formats/DbcReader.cs:220-284` | The needed NPC hair BLP is locally available through the existing table parser. |

`git blame` places both `CreatureRenderer.ResolveBatchTexture`'s uniform type-1
baked-atlas law and `NpcBodySkinCandidates` in `d055c654` (2026-07-27). The same
commit added the player draw-time exception that prevents hair/scalp/ears from
sampling the dressed atlas. Thus the correct distinction existed for players in
the introducing commit but was omitted from the new NPC renderer. `13de8f5`
(2026-07-30) added asynchronous NPC appearance preparation and reused the same
resolver, preserving the omission in both synchronous and asynchronous paths;
neighboring portrait/catalog commits did not alter this binding law.

Benilla independently separates these materials for both players and NPCs:

- `crates/benilla/src/entities/attach/char_skin.rs:24-102` gives an NPC a baked
  body atlas but retains its race/sex/hair selectors in the same `CharLook`.
- `crates/benilla/src/entities/attach/char_skin.rs:373-402` binds the hair mesh
  from `CharSections::hair_mesh_texture`, explicitly for players and NPCs.
- `crates/benilla-formats/src/models/m2_batches.rs:45-70` classifies M2 type 1 as
  Body and type 6 as Hair; neither is a creature skin-variation slot.

### Per-geoset trace

#### A. Willem scalp control — display 2072 / extra 675

Willem's authored helm currently forces hair selection to the bare scalp geoset
1, while the separate 7C-1 attachment omission leaves that scalp visible.

| Batch | Geoset | Region | M2 texture type | Current resolved texture | Expected texture class |
|---:|---:|---|---:|---|---|
| 0 | 0 | body/base head atlas | 1 | `Textures\\BakedNpcTextures\\c5c3858a5d86e950a1c2f0f43c9dc69f.blp` | Body may use the baked dressed atlas. |
| 12 | 1 | bare scalp | 1 | `Textures\\BakedNpcTextures\\c5c3858a5d86e950a1c2f0f43c9dc69f.blp` | Undressed/bare head-skin atlas, matching the player head exception. |

This confirms the reported scalp half of the hypothesis: the visible scalp is
bound to the authored-clothing baked composite. 7C-1 will conceal it under the
helm, but does not correct this law for the wider NPC cohort.

#### B. Hair-bearing authored-clothing control — display 3340 / extra 54

The installed DBCs supplied a direct positive control without a database lookup:
Human male, hair style/color `3/9`, facial hair `2`, no authored helm, authored
chest/pants/boots/wrist equipment, bake
`a924d87d84c0c55e898c596f6dbecb6d.blp`. Its visible hair geoset is 4.

| Batch | Geoset | Region | M2 texture type | Current resolved texture | Expected resolved texture |
|---:|---:|---|---:|---|---|
| 0 | 0 | body/base head atlas | 1 | `Textures\\BakedNpcTextures\\a924d87d84c0c55e898c596f6dbecb6d.blp` | Body may use the baked dressed atlas. |
| 15 | 4 | selected hair mesh | 6 | `NONE` | `Character\\Human\\Hair02_09.blp` from `CharSections` variation 3/color 9. |
| 18 | 4 | selected hair/scalp under-pass | 1 | `Textures\\BakedNpcTextures\\a924d87d84c0c55e898c596f6dbecb6d.blp` | Undressed/bare head-skin atlas. |

The HumanMale M2 has three runtime texture references (`type 1`, `type 6`, and
`type 2`); its 56 batches distribute as type1×34, type6×12, type2×10. The selected
hair geoset deliberately has both a type-6 hair pass and a type-1 under-pass.
The current NPC resolver loses that distinction.

### Revised root-cause ruling

The hypothesis is **confirmed and refined into two adjacent H3 omissions**:

1. **Dressed-atlas leak:** `CreatureRenderer` binds the baked full-body/dressed
   type-1 atlas uniformly. It lacks the player renderer's per-geoset exception
   for hair/scalp/ears, so the selected hair under-pass samples clothing pixels.
2. **Missing hair sheet:** type 6 is not treated as a character runtime hair
   slot. For character-model NPCs the fallback creature texture array is empty,
   so a real `CharSections` hair BLP resolves as `NONE`.

Both are never-correct behavior introduced in `d055c654`; `13de8f5` propagated
them into async preparation. This is not H1 archive precedence and not a recent
content-row change.

The smallest prospective 7C-2 fix, **not implemented**, is to carry batch geoset
id/region into NPC appearance binding, prepare an equipment-free CharSections
head/body composite from the extra row for type-1 head pieces, and bind type 6
from race/sex/hair-style/hair-color using the existing literal-1 fallback. The
baked NPC atlas remains the type-1 source for ordinary body/clothing geosets.
The 7B CSV must expose this choice as strings rather than infer it from pixels.

### Read-only VMaNGOS connection record

| Field | Value used |
|---|---|
| Configuration source | `C:\\Users\\nico\\source\\repos\\MangosSuperUI\\MangosSuperUI\\appsettings.Development.json` → `ConnectionStrings:Mangos` |
| Server / port | `localhost:3306` |
| User / database | `root` / `mangos` |
| Password | Read from the local configuration at runtime; deliberately not copied into this report or console output. |
| Intended statements | Parameterized `SELECT * FROM creature_template WHERE entry=@entry` and `SELECT * FROM creature WHERE id=@entry`, with `entry=823`; no write statements existed. |
| Result | Connection timed out before opening. **Zero SQL statements executed and zero database rows read.** |

The installed `CreatureDisplayInfo.dbc` / `CreatureDisplayInfoExtra.dbc` mappings
therefore remain the provenance for displays 2072 and 3340 in this packet. The
capability grant is recorded for future read-only diagnoses when the local server
is available; it does not authorize database writes.

### Revised console evidence

```text
[npc-geoset-trace] display=2072 extra=675 model=Character\\Human\\Male\\HumanMale.m2
[npc-geoset-trace] batch=12 geoset=1 region=hair/scalp textureIndex=0 type=1 resolved='Textures\\BakedNpcTextures\\c5c3858a5d86e950a1c2f0f43c9dc69f.blp'
[npc-extra-candidate] extra=54 displays=3340 hair=3/9 facial=2 bake=a924d87d84c0c55e898c596f6dbecb6d.blp equipment=0/0/0/5345/0/5346/5347/3897/0/0
[npc-hair-trace] display=3340 extra=54 hair=3/9 expectedHair='Character\\Human\\Hair02_09.blp'
[npc-hair-trace] batch=15 geoset=4 type=6 current='NONE' expected='Character\\Human\\Hair02_09.blp'
[npc-hair-trace] batch=18 geoset=4 type=1 current='Textures\\BakedNpcTextures\\a924d87d84c0c55e898c596f6dbecb6d.blp' expected='bare/head-skin atlas'
[vmangos-readonly] connection localhost:3306/mangos timed out before query execution
```

### Ordered next stages (recorded, not started)

1. **7B NPC-extras axis first.** Its CSV must include per-geoset resolved-texture
   string columns (including the exact texture bound to hair), plus NPC-extras
   contact sheets and full `--diff` evidence.
2. **7B items axis second.** This owns capes wholesale; cape wrongness surfaces
   as diffs rather than a named 7A case.
3. **7B reduced player sweep third.** The withdrawn named-player case adds no
   separate 7A fix.
4. **7C-1 Willem attachment fix.** GO in principle, but only after 7B; acceptance
   requires before/after NPC-extras cohort sweep, full `--diff`, and standard
   gates.
5. **7C-2 NPC hair/head texture fix.** This trace has landed. Implementation
   remains blocked pending Nico's explicit ruling after review.

### Mandatory gates

```text
dotnet build MSUIClient.sln -c Debug
Build succeeded. 1 Warning(s), 0 Error(s).
The warning is the pre-existing CA2014 at Engine/UI/GlueAdditive.cs:141.

dotnet run --project tools\\combat-wire-check\\MSUICombatWireCheck.csproj -c Release
combat/movement/targeting/wire foundation checks passed

dotnet run --project tools\\portrait-camera-check\\MSUIPortraitCameraCheck.csproj -c Release -- GameData\\Data
[camera-check] portrait tuning defaults are float-bit identical
[camera-check] MPQ archive ordering assertions passed
DwarfMale inside=1224; HumanMale inside=1289; Wolf inside=56
portrait camera check passed
```

### Revised deviations and ruling boundary

1. The authorized local database connection was unavailable; the attempted
   read-only connection and its zero-query outcome are recorded rather than
   substituting an external database.
2. Display 3340 was selected from installed DBC rows as the required hair +
   authored-clothing cohort member. No renderer instrumentation was retained.
3. No contact sheet, 7B sweep, CSV schema change, full diff, or 7C fix was begun.

**HARD STOP.** Nico's role resumes at reviewing the future 7B NPC-extras contact
sheets/CSV and issuing the 7C rulings. The immediate ruling requested from this
packet is whether the refined two-part 7C-2 boundary is accepted for later work.
