# Player rendering & remote units — MSUIClient vs benilla (audit)

Date: 2026-08-05. Scope: rendering and moving OTHER players (server bots + real clients) and, where it
overlaps, streamed creatures. Compares our new `PlayerRenderer` + remote-movement path against the Rust
reference client benilla (`C:\Users\nico\Desktop\benilla-main`). Citations below are benilla `file:line`.

**What we built this session** (all builds Debug+Release clean; live-verification is Nico's):
`MSUIClient/World/Units/PlayerRenderer.cs` — a shared-resource renderer for remote players; remote
movement interpolation in `Net/Entities.cs` (`ApplyRemotePlayerMove`) + dispatch in `Program.Net.cs`;
gait selection from movement flags; cape textures; appearance/atlas LRU eviction. Local player is still
drawn by `CharacterRenderer` (`_character`); creatures by `CreatureRenderer`.

---

## Layer-by-layer scorecard

| Layer | Ours | Benilla | Status |
|---|---|---|---|
| Model resolution | cache key `(race,gender)` → `Character\R\G\RG.m2` | key = **display id**; player uses the creature chain too | ✅ works; keying differs (fine for players-only) |
| Skin atlas / armor composite | `CharacterEquipment.Composite`, sig-keyed cache | `sections.rs composite_body`, `SkinKey`-keyed | ✅ same design; verify the priority table + mips (below) |
| Geosets | `CharacterGeosets.Visible` (ported) | `geosets.rs visible_geosets` | ✅ ported; spot-check helm-vis + robe branches |
| Attachments (helm/shoulder/wpn/shield) | `AttachedItemRenderer` + mount set | `equipment.rs` | ✅ same approach |
| Cape (type-2) | ✅ added this session | `char_skin.rs:404-428` | ✅ matches |
| **Mounts** | ❌ not built (rider drawn on foot) | `mount.rs` + `attach/mod.rs:273-372` | ⛔ **gap — recipe captured below** |
| Texture eviction | ✅ LRU cap 96 | **none** (grows for session) | ✅ ours is stricter (a deliberate divergence) |
| Culling / LOD | distance + frustum cull + animate cap | **no unit render cull, no cap**; off-frustum pose-eval parking (0.5s) | ⚠️ we cull harder; see note |
| Remote movement | 2-point spline **interpolation** | **extrapolation** (dead-reckon) + jitter buffer + collision-swept | ⚠️ different model; ours simpler |
| Gait selection | flags→idle/walk/run/back/strafe/turn/swim/sprint | `select.rs gait_candidates` | ✅ core matches; added flag-driven gaits this session |
| Remote speed changes | ❌ not applied to remotes | FORCE/SPLINE_SET/MOVE_SET applied to all movers | ⛔ **gap** (hasted/mounted remote animates wrong) |
| Skinning | uniform bone palette, MAX_BONES 160 | Bevy SSBO skinning, no small cap | ✅ ours fine for 1.12 (≤119 bones) |

---

## Detail + our status

### 1. Model resolution
Benilla resolves a player body through the **creature chain** (`displayId → CreatureDisplayInfo →
CreatureModelData → M2`, `creatures.rs:153-172`) and caches `DisplayModel` by **display id**
(`entities.rs:164-167`). We resolve by `(race,gender)` → `Character\R\G\RG.m2` and cache per race+gender
(~16 combos). Equivalent for players (a player's displayId encodes race/gender); benilla's single key also
covers character-model NPCs. **No change needed** for players.

### 2. Skin atlas / armor compositing
Both composite a 256×256 body atlas: base skin + face/facial/hair overlays, then equipped armor painted
per body region. Benilla: `sections.rs composite_body` (`:180-242`), cache key `SkinKey{race,sex,skin,
face,facial,hairStyle,hairColor,equip[8]}` (`entities.rs:268-278`). Ours: `CharacterEquipment.Composite`
keyed by our appearance+equipment signature. **Verify against benilla** (worth a targeted pass):
- Region rects: benilla `EQUIP_TILES` (`sections.rs:38-47`) — our `SlotRegions` should match the 10-rect
  partition (arm-upper/lower/hand + torso-upper/lower + leg-upper/lower + foot, plus the two face tiles).
- **Layer priority table** `EQUIP_LAYER_PRIORITY [[i8;8];8]` (`sections.rs:67-76`) — the RE'd `[0x803bf8]`
  stacking order (e.g. robe reaches legs, gloves over sleeves). Our `PaintOrder` is a per-inventory-type
  order, not the per-(bodyslot×layer) priority matrix — a likely source of subtle armor-layering diffs.
- Gendered region lookup `_M/_F/_U` — we do this (`GenderSuffix`).
- Benilla composites **per authored mip level** rather than compositing mip 0 and regenerating mips.

### 3. Geosets
Our `CharacterGeosets.Visible` is a port of `geosets.rs visible_geosets`. Confirmed structurally identical
(region bases, hair `max(1,·)`, facial +100/+300/+200, helm-vis race-bitmask forcing slots {0,1,2,3,7}
before equipment, robe-suppresses-tabard/pant-legs, boots keep naked 501). Benilla resolves duplicate
`(race,sex,var)` DBC keys **first-row-wins** (`geosets.rs:154-234`) — check our table parse does the same.

### 4/5. Attachments + cape
Same approach. Helm files are per-race/sex (`Helm_..._HuM`), shoulders are an L/R model pair, stowed vs
drawn keys on sheath state, a sheathed ranged weapon draws nothing (`equipment.rs`). Cape: the cloak's
`ItemDisplayInfo.ModelTexture[0]` bound as the type-2 texture on the cape geoset (`char_skin.rs:404-428`)
— we now do this (`PlayerRenderer.BuildCapeTexture`).

### 6. Mounts — the recipe (not yet built)
Currently a mounted player renders on foot (we ignore `UNIT_FIELD_MOUNTDISPLAYID`). Benilla
(`mount.rs`, `attach/mod.rs:273-372`, `driver.rs:985-999`):
1. Mount is a **second creature model**, spawned as a child from `MOUNTDISPLAYID`.
2. Mount scale = `SCALE_X × CreatureDisplayInfo.creatureModelScale` (the CDI column **alone**, not
   `CreatureModelData.modelScale`).
3. **Rider seat**: mount model **attachment id 0** → `(bone, offset)`; the rider's skeleton root parents
   under a seat node at that bone+offset (in our flat renderer: seat world pos =
   translation row of `Translate(attachment.Position) · mountSkin[bone] · mountWorldModel`; rider drawn at
   the rider's own scale, positioned at the seat, oriented with the mount). If the mount authors no
   attachment 0 → leave at the unit matrix.
4. Rider base animation = **AnimationData id 91 (Mount)**, held unconditionally (moving/turning/air),
   **not** rate-scaled, STAND fallback. Leg geosets are **not** hidden — the pose is the whole look.
5. The mount's own gait (walk/run/turn) is driven by the **rider's** movement state.
6. Force stand-state 0 and stow weapons while mounted.
7. Rebuild on any change to the mount field.
**Why deferred:** needs cross-renderer coordination (mount = creature model, so the draw belongs with
`CreatureRenderer`) + the seat math above + **live visual tuning of seat alignment** that can't be
validated headless. Ready to implement alongside a live pass.

### 7. Texture eviction
Benilla **never evicts** composited atlases or model textures (`entities.rs:261-278`, insert/read only).
We added an LRU cap (96 looks) that disposes the least-recently-seen unique atlas. A deliberate
divergence — safe as long as re-compositing on re-entry is acceptable (it is; it's async + cached again).

### 8. Culling / LOD
Benilla does **no distance/frustum render cull** of units (spawns with `NoFrustumCulling`); server-side
grid limits what streams. Its only LOD is **pose-evaluation parking** off-frustum after 0.5s (keeps anim
clocks + sound events live). We do distance+frustum **render** cull and an **animated-instance cap**.
For "the real deal" scale ours is a reasonable modernization, but note two behavioral differences: (a) we
won't draw players past `DrawDistance` (200yd) at all, where benilla would; (b) our cap bind-poses far
players rather than parking pose-eval. If distant crowds should still appear, raise `DrawDistance` / cap.

### 9. Remote movement (observer smoothing)
**This is the biggest design divergence.** Benilla dead-reckons each remote unit forward from its live
move flags + per-mover speeds every frame, and *reconciles/snaps* toward packets that are scheduled on a
per-unit **jitter-buffered fire-time queue** (`net/motion/remote.rs`, `relay.rs`); it also sweeps each
extrapolated step through the collision world. We instead build a short 2-point `CreatureSpline`
**interpolating** from the current position to the newly reported one over the inter-packet interval.
Both take facing from the packet and hold on stop. Ours is simpler and ~1 packet (≈0.5s) behind; benilla's
is lower-latency and jitter-tolerant. Upgrading to extrapolation is possible but needs live tuning; our
interpolation is a fine v1.

### 10. Gait selection
Our `PlayerRenderer.SelectClip` now mirrors benilla `select.rs gait_candidates`: 2×walk run boundary,
`speed/MoveSpeed` rate, `MOVING_EPSILON` stand threshold, plus (added this session, driven by the stored
`WorldEntity.MoveFlags`): backward (13), strafe/swim (41-45), turn-in-place shuffle (11/12), and sprint
(143 at ≥11 yd/s). Not yet: flying (135, N/A in 1.12 on-foot), and the masked upper-body one-shots while
mounted (needs mounts first).

### 11. Speed source — GAP
Benilla keeps a per-mover speed set from the UPDATE_OBJECT LIVING 6-speed array `[walk,run,run_back,swim,
swim_back,turn]` and live-updates it for **all** movers via `SMSG_FORCE_*_SPEED_CHANGE`,
`SMSG_SPLINE_SET_*_SPEED`, and `MSG_MOVE_SET_*_SPEED` (`objects.rs:439-500`, `decode.rs:644-670`); both
the extrapolator and the anim selector read it. We parse the LIVING speeds into `WorldEntity.Speeds`, but
we do **not** apply forced speed changes to remote players. Result: a hasted / slowed / mounted remote
player's walk↔run threshold (and, once extrapolation exists, its predicted speed) is stale until its next
LIVING block. **Recommended fix:** handle `SMSG_FORCE_*_SPEED_CHANGE` / `SPLINE_SET` / `MOVE_SET` for
non-self guids by writing the matching slot of `WorldEntity.Speeds` (no ack for units we don't control).

---

## Recommended next steps (priority order)

1. **Mounts** — implement the Layer-6 recipe (mount child model via `CreatureRenderer`, attachment-0 seat,
   rider anim 91), with a HUD seat-offset knob, paired with a live tuning pass. (Task #7.)
2. **Remote speed changes** — apply FORCE/SPLINE_SET/MOVE_SET speeds to remote movers so gait speed is
   correct (Layer 11). Small, high-value.
3. **Armor-layer priority table** — port benilla's `EQUIP_LAYER_PRIORITY` matrix into
   `CharacterEquipment` if armor layering shows diffs vs the reference (Layer 2).
4. **(Optional) Extrapolation** — if interpolation latency reads as laggy in live play, move to benilla's
   dead-reckon + jitter-buffer model (Layer 9). Bigger, needs live tuning.
5. **(Optional) LOD parity** — pose-eval parking instead of a hard instance cap if distant crowds must
   stay visible (Layer 8).
