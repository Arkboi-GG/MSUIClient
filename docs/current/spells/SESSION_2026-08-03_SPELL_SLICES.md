# Session 2026-08-03 — Rollback, root causes, and the ground-decal projector

One sitting, eleven commits, every one live-verified before it landed. This doc is the
map of what happened, what was proven, and what is still open. Nico's eye was the
acceptance gate throughout; "verified" below means a live capture or wire log, not a
code read.

## Where main stands

```
b779a25  slice(decal): true terrain-triangle projection for ground decals + reticle
290d4e6  fix(area-fx): stand dynamic-object visuals upright + loop their sequence
9f97b66  slice(cast): ground-target reticle + DynamicObject area visuals
225950b  fix(cast+ui): targeting-hint AV crash; spellbook-to-actionbar drops land
ac478ba  slice(cast): ground-target AoE casting - Blizzard et al. no longer refused
857f15e  slice(audio): move all MCI + file IO to a worker thread - kills the cast freeze
618ece0  slice(fx-mesh): billboard basis used benilla's local axes - collapsed cards edge-on
4412dff  slice(wire): 1.12 SOURCE_LOCATION target block is xyz only - no transport guid
c4e0d6a  slice1(cast): clear stale pending-cast lock so a missing GO can't deadlock
750e63d  checkpoint: codex spell-port attempt (WIP)     <- fireball fix is INSIDE this
db32413  a lot                                          <- pre-codex; where origin/main sat
```

Local branch `codex-era` preserves the two dropped commits (`f7bdd19` DXT dispatch,
`1787fce` HDR render path). Both were **disproven** as the Cone of Cold fix by direct
experiment this session — cherry-picked, captured, still squares, reverted.

## The morning: getting back to "good"

- Nico judged the codex-era state a regression. First rollback target (`db32413`) was
  wrong — **the fireball fix never had its own commit; it is entangled inside the codex
  checkpoint `750e63d`** along with ~80 files of unrelated UI work. Second target
  (`750e63d`) still had the cast deadlock. Final resting point: `c4e0d6a` — fireball
  perfect, Arcane Explosion "half working". Nothing was ever lost: reflog, stashes and
  dangling objects were all searched; every committed state is accounted for.
- MPQ side-quest: MSUI mounts `patch-4.MPQ` (custom, item cosmetics only — no spell
  content) and `backup.MPQ`; benilla's hardcoded chain stops at `patch-2`. Not a
  spell-visual factor, but the clients do not mount identical archive chains.

## Root causes found and fixed (each its own commit)

1. **`4412dff` — the "Arcane Explosion never gets its own SPELL_GO" mystery.** Codex's
   packet parser read a packed GUID before the 0x20 source-location vector — a
   TBC+ wire shape. 1.12 sends three floats only (vmangos SpellCastTargetsInfo.cpp,
   benilla messages/spells.rs). The desync shredded every self-centred AoE GO.
   Fireball (unit-target flag) was untouched — hence "fireball perfect, AE half-dead".
   The packet had been arriving all along.
2. **`618ece0` — thin lines / half-visible effects.** `ApplyBillboardBones` built its
   facing matrix in benilla/Bevy's model-local axis convention; MSUI's M2 space is
   `(x, z, -y)`. Every billboarded bone (and its children) faced the camera with its
   thin edge from all angles. Diagnosed by muting particles (`MSUI_MUTE_SPELL_PARTICLES`)
   + 3-angle capture: thin-from-everywhere = camera-tracking collapse. After the fix,
   Frost Armor shows the full icy shield with hanging icicles — matching Nico's real-1.12
   reference shot.
3. **`857f15e` — the cast freeze/jitter.** Codex's SpellSoundSystem ran `mciSendString
   ("open")` (50–300 ms), a whole-WAV temp-file write, and per-voice per-frame MCI
   status polls on the game thread. The hitch recorder measured 300–650 ms frames, one
   per cast. All audio now lives on a dedicated worker thread. Probe run: 25 recorded
   hitches → 1 (a world-load frame). Sound kept. (Lazy asset loading was ruled OUT
   first — `[fx-load]` probes showed a few ms total.)
4. **`ac478ba` + `225950b` — ground-target casting.** CastTargetLaw refused all
   location-bit spells (a faithful port of benilla's own deferral — there was nothing to
   port, so this was implemented from vmangos's byte-verified read side). Press → 1.12
   targeting-cursor mode → click binds the terrain point → `CMSG_CAST_SPELL` mask
   0x0040 + xyz. Blizzard: START → GO (mask echoed) → 8 s channel, verified. Plus two
   interactive-only fixes: the ImGui draw-list AV (never touch draw lists in the update
   phase) and spellbook→actionbar drag-drop (`IsItemHovered(AllowWhenBlockedByActiveItem)`
   — plain hover is suppressed during a drag, so drops landed nowhere).
5. **`9f97b66` + `290d4e6` — DynamicObject area visuals.** The server expresses every
   persistent ground effect as a DynamicObject (SPELLID=9, RADIUS=10, POS=11–13; they
   already streamed into Entities, unrendered). New area-anchor instance mode plays the
   spell's impact kit at the dynobj position for its lifetime. Two bugs then found by
   census forensics: the anchor was missing the model→world rotation — **the whole
   blizzard was lying on its side in the grass** (snow emitters 7.7 units "up" sat
   7.7 yd north at ankle height). An initial correction also manually rewound the 3.3 s
   clip across the 8 s object. The later full M2 animation audit disproved that second
   rule: Blizzard's first sequence has clamp bit `0x1`; only tracks authored to loop may
   loop, and global sequences keep their own clock. The manual area-clock rewind is now
   removed. Type-9 shard births remain owned by the DynamicObject duration.
6. **`b779a25` — the ground-decal projector (trace doc §9, benilla decal.rs/ground_fx.rs).**
   Decals are no longer drawn as their own quads: the real rendered terrain triangles
   (4-per-cell fan, true MCVT inner vertices — new per-tile inner-height grid +
   `GatherGroundTriangles`) are clipped to a fitted frame (Sutherland–Hodgman, 6 planes)
   and re-emitted with bilerped corner UVs + vertical-fade alpha, depth bias 8192.
   Consumers: the targeting reticle and the AoE ground rings. Frost Nova now draws a
   complete, unbroken, terrain-hugging 360° ring. **Nico verified: terrain clipping is
   fixed.**

## Open items (the honest list)

- **Green targeting circle: pattern still needs original-client capture.** Placement/draping is right and
  radius now comes from the maximum positive SpellRadius referenced by all populated effect lanes (full
  build-5875 census in `tools/spell-target-radius-check`; zero-radius placement retains an explicit 8-yard
  fallback). The remaining question is the exact 1.12 texture/orientation/animation and whether mixed-radius
  or zero-radius spells use a different presentation rule.
- **Blizzard: small snowflakes right, the BIG falling shards missing.** The model's
  ICE3B_C mesh batches (43 verts, translation-animated bones from ~14 yd up) are
  confirmed drawing with full opacity (`MSUI_FX_TRACE`) yet don't read as the big
  chunks 1.12 shows. Suspects: per-batch color/alpha ramps gating them to a sliver,
  the kit's scale-field=0 semantics, DYNAMICOBJECT_RADIUS model scaling (8 yd server
  radius vs ~3 authored), sequence phase. Needs isolation of the shard submesh.
- **Cone of Cold squares.** Neither dropped commit (HDR, DXT) fixes it — proven by
  direct experiment this session. First-principles benilla comparison needed: diff
  `CLOUDS.BLP` decode between the two decoders, get a benilla reference frame.
- **Reticle on WMO floors** falls back to the old grid (gatherer is terrain-only).
- **Multi-action-bars** have no drop detection (main bar drops work).
- **ESC does not cancel** ground-targeting (right-click does).
- Diagnostics kept in-tree, all off by default: `MSUI_MUTE_SPELL_PARTICLES`,
  `MSUI_MUTE_SPELL_AUDIO`, `MSUI_FX_TRACE`, `[fx-load]` cold-load timers, the
  `[dynobj-fx]` spawn log, and the `castground <id> [yards]` protocol step.

## Special particle motion/frame slice — static/data completion

The `0x10` model-space, `0x40` inherited-motion, and `0x4000` follow exceptions were traced end to end after
the ordinary root-cloud correction. The important defect was deeper than a missing flag branch:
`SpellEffectSource` supplied only the posed emitter origin and quaternion. That discarded live joint scale,
so model-space positions/tails and world-to-stored follow/inherit vectors could not match the reference.

The runtime now supplies the emitter joint's decomposed live TRS and composes it with the effect root at the
particle boundary. The complete frame drives ordinary birth offsets/directions, model-space draw and inverse
conversion, tail velocity, fixed-plane quad basis, and geometry-particle pose. Follow and inherit receive the
same 100 ms-clamped step as integration/emission. Inherit retains the reference's strict `>1/30 s` trigger,
current-frame-only delta, already-live gate, and held value.

`tools/spell-particle-motion-check` passes 61 checks. Its complete mounted listfile scan pins 9,717 paths,
9,654 parsed models, 7,860 emitters, and 2,550 unique special records (2,391 model-space, 124 inherit, 96
follow). The 599 referenced SpellVisual paths contain 505 model-space, 61 inherit, and 20 follow records.
There are 115 special records beneath scale-animated joint chains, including 52 spell records; a live
`AbolishMagic_Base.m2` source-frame probe confirms non-unit scale reaches the feed. Arcane Shot pins the follow
curve and Bloodlust pins combined model-space+inherit with scale 3.

Neighbor regressions pass: animation/lifecycle 5,788 checks, area 100,104, target radius 779, ordinary frame
law, interface wire check, and the full solution build. This is `STATIC/DATA_COMPLETE`; no synchronized
original-client runtime/pixel capture was made for these special-motion fixtures.

## Missile release/root/history/impact slice — static/data completion

The missile lane was re-traced as a single ownership and timing pipeline instead of accepting the older
independent “match” labels. Four hidden divergences were corrected: missile movement had inherited the
particle simulator's 100 ms hitch clamp; the parsed model basis did not map authored +X to flight; free
missile ordinary particles were stored world-absolute instead of relative to the moving root; and spells
with no SpellVisual row tried chest (`0x22`) before the reference fallback tail.

`SpellMissileLaw` now owns release-event, animation-finish, strict never-started backstop, GO-time deadline,
raw-dt homing, no-snap arrival, and roll-free flight-frame decisions. `SpellVisualCatalog` preserves an
explicit no-destination-tag sentinel. `SpellEffectSource` resolves release markers at the live caster pose,
re-resolves the target point every flight tick, carries ordinary clouds with live root translation without
free-model attachment rotation, and hands sound/root ownership to impact before the visual can snap.

`tools/spell-missile-pipeline-check` passes 54 checks. Its mounted census pins 981 speed spells, 824 with
visual rows, 157 without, 64 distinct missile paths (63 resolve; `Particles\FrostBolt_Missle.m2` is the one
shipped stale/typo path), 45 particle models, 35 ribbon models, 25 models with AnimationData 144, and 169
emitters (8 follow, 5 inherit). It also runs normal launch/motion/end/impact and past-deadline no-flight
handoffs through the production source. This is `STATIC/DATA_COMPLETE`; the prior Fireball image predates
the corrected frame law and is not pixel certification. Ribbon history is handled in the following slice.

## Ribbon committed-node/history slice — static/data completion

The old ribbon audit repeated two semantic-frame mistakes this project has now seen several times: it
accepted matching labels without converting coordinate bases, and it treated every notion of “effect age”
as one clock. Benilla's width axis is authored WoW bone-local +Y. MSUI parses raw `(x,y,z)` into `(x,z,-y)`,
so that direction is parsed local `-Z`; the former `Vector3.UnitY` was a 90-degree error. Benilla also uses
raw age for pair lifetime/U while its 100 ms-clamped simulation step advances emission, sag, and keyed ribbon
look tracks.

`SpellRibbonHistoryLaw` now makes those contracts explicit. The current posed bone/root builds the live head
and a new top/bottom pair only at the authored cadence. Committed pairs remain world-space, retain their born
width, sag and expire without any later pose transform, and continue keyed alpha/color during owner-loss
drain. Direct `position*skin*root` is regression-checked against the pivot-rebased posed-bone form, and the
width direction discards scale like Benilla's live joint rotation read.

`tools/spell-ribbon-history-check` passes 41 checks over all 9,717 mounted M2 paths: 176 ribbon models, 590
records, 350 spell ribbons, 318 referenced ribbons, 80 missile ribbons, 102 gravity records, 142 animated-
height records, 214 animated-alpha records, 90 scale-animated chains, and 570 animated-bone chains. Arcane
Shot proves a later real InFlight bone/root pose cannot move a committed pair; Holy Smite distinguishes the
clamped look clock from raw expiry; the thrown dagger pins Stand-off/InFlight-on visibility. This is
`STATIC/DATA_COMPLETE`, not original-client pixel certification. The next unresolved implementation slice
is multi-weight mesh/pivot composition.

## Method notes (why this session moved)

Code-trace parity claims failed this project five times before today. What worked
instead, every single time: **live evidence first** — hitch recorder, particle census,
wire verdicts, 3-angle captures, offline `--dump-emitters` probes — then one narrow
fix, then re-capture. The trace doc (`BENILLA_SPELL_SYSTEM_TRACE.md`) earned its keep
twice (billboard axes, decal projector); benilla itself is NOT the reference for
dynamic objects (it only parses them) — for area effects the authority is the real
1.12 client and the M2 data.
