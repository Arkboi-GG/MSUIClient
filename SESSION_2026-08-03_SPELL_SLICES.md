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
   7.7 yd north at ankle height) — and the 3.3 s clip needed looping across the 8 s
   object. After both: snowfall spawns ~9.5 yd overhead at the bound point and falls.
6. **`b779a25` — the ground-decal projector (trace doc §9, benilla decal.rs/ground_fx.rs).**
   Decals are no longer drawn as their own quads: the real rendered terrain triangles
   (4-per-cell fan, true MCVT inner vertices — new per-tile inner-height grid +
   `GatherGroundTriangles`) are clipped to a fitted frame (Sutherland–Hodgman, 6 planes)
   and re-emitted with bilerped corner UVs + vertical-fade alpha, depth bias 8192.
   Consumers: the targeting reticle and the AoE ground rings. Frost Nova now draws a
   complete, unbroken, terrain-hugging 360° ring. **Nico verified: terrain clipping is
   fixed.**

## Open items (the honest list)

- **Green targeting circle: pattern still wrong.** Placement/draping is right; the
  LOOK is not the 1.12 reference (full rune circle: outer+inner rings, glyphs, crossing
  arcs). Current texture guess is `SPELLS\AURARUNE256.BLP`, axis-aligned, fixed 8 yd.
  Next: pin down what the real 1.12 AoE cursor actually renders (texture, orientation,
  radius source — SpellRadius.dbc via EffectRadiusIndex, possibly rotation animation).
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
- **Per-spell radius** for the reticle (SpellRadius.dbc) instead of fixed 8 yd.
- Diagnostics kept in-tree, all off by default: `MSUI_MUTE_SPELL_PARTICLES`,
  `MSUI_MUTE_SPELL_AUDIO`, `MSUI_FX_TRACE`, `[fx-load]` cold-load timers, the
  `[dynobj-fx]` spawn log, and the `castground <id> [yards]` protocol step.

## Method notes (why this session moved)

Code-trace parity claims failed this project five times before today. What worked
instead, every single time: **live evidence first** — hitch recorder, particle census,
wire verdicts, 3-angle captures, offline `--dump-emitters` probes — then one narrow
fix, then re-capture. The trace doc (`BENILLA_SPELL_SYSTEM_TRACE.md`) earned its keep
twice (billboard axes, decal projector); benilla itself is NOT the reference for
dynamic objects (it only parses them) — for area effects the authority is the real
1.12 client and the M2 data.
