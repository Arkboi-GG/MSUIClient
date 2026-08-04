# Spell FX → benilla 1:1 parity — HANDOFF (updated 2026-08-02)

## UPDATE (session 2, 2026-08-02) — the failure table below was STALE. Re-verified by fresh capture.

**The single genuine render bug was the AoE ground rings, and it is now FIXED.** Everything else in the table
below was already working or is at benilla-parity. Do not trust the old table; here is the re-captured truth:

| Spell | Old claim | Actual (re-captured) | Verdict |
|---|---|---|---|
| Arcane Explosion (1449) | tiny sparkle, no ring | **purple dome + expanding white ground ring** | FIXED |
| Frost Nova (122) | tiny blue sparkle, no ring | **bold expanding white-blue ground ring** | FIXED |
| Arcane Intellect (1459) | nothing renders | **blue-white arcane glow halo** | already OK |
| Frost Armor (168) | floating vertical beam | upward frost plume above the head | benilla-parity* |
| all instant/self | `animation=Stand` | caster plays cast anim (52/54) | already OK |

**The fix (one line, `World/Units/SpellEffectMeshRenderer.cs`):** `BuildGroundQuad` was handed the already
camera-relative `model` matrix, then subtracted the camera **again** (final = world − 2·camera ≈ 9000yd off)
AND fed camera-relative coords to world terrain-height sampling. Every ground-anchored decal (Frost Nova,
Arcane Explosion rings) drew off-screen. Fix: pass `item.Source.Transform` (WORLD) so the eye is subtracted
once and `sampleGround` gets world coords. Verified from a look-DOWN camera (`scenarios/spells/ring-topdown.txt`).

**Why the old table was wrong about the rest:** the mesh (`SpellEffectMeshRenderer`) and ribbon
(`SpellRibbonRenderer`) paths are NOT "unported" — they are real and fairly complete (ground decals, billboard
bones, colour/alpha tracks, blend mapping). The AoE rings ARE mesh (verified: `Frost_Nova_area` = 3 flat-ground
SHOCKWAVE quads scaling 0.14yd→19yd via bone-scale anim; `ArcaneExplosion_Base` = DALARONDOME hemisphere + 3
flat rings). The scale animation works (offline anim probe in `--dump-emitters` confirms it). Caster animation
is wired (`Program.Casting.cs` `BeginSpellVisual`/`ReleaseSpellVisual` with the kit AnimationData id).

*Frost Armor: `FrostArmor_Low_Head` attaches at head 0x14 and its white STAR4 emitters are authored `vRange=0`
(fire straight up) clustered ~1yd above the head → an upward plume; the effect's own bones are ≈identity so
benilla renders it the same. The old "floating beam" was parallax from a front angle. Open question is only
benilla-vs-real-1.12.

**Still genuinely missing (unchanged):** spell SOUND (root cause B) — but MSUI has NO audio subsystem at all,
so that is a separate large project, not a render fix. Channel unify (root cause D) untouched.

**Tooling added this session (keep):** `--dump-emitters` now also prints MESH structure (submeshes / batches /
bboxes / `[FLAT-GROUND]` flag) and an ANIM probe (per-bone scale + skinned world bbox over the sequence) — it
is how you tell a mesh bug from a particle bug offline. New scenario `scenarios/spells/ring-topdown.txt`.

---

## (ORIGINAL, now partly stale) Status: Fireball's PARTICLE path is done. The rest of the spell system is NOT.

**Honest accounting of where this stands, because the last pass over-scoped to one spell.** The original
mission (below) was a full benilla-faithful rewrite of the spell-visual system: **P1 particles, P2 ribbons,
P3 mesh, P4 orchestration.** What actually got done is **P1 particles, and only for the effects Fireball
uses.** Fireball now looks correct end-to-end. But **self-buffs (Arcane Intellect), armor buffs (Frost
Armor), and instant AoE (Arcane Explosion, Frost Nova) are all wrong** — because their visible effects are
**mesh- and ribbon-based**, and those render paths were never ported. Plus **spell sound is entirely
missing**, and **instant spells don't animate the caster.** This doc is the deep dive that pins each of
those to a file:line, verified against benilla AND against live captures.

**Do not repeat the last mistake: verify the PICTURE, not the code.** Nico caught this because Fireball's
data parsed the same way the others do, yet the others render as tiny sparkles / floating beams / nothing.
Same pipeline + different picture = the render diverges. Capture every category and compare by eye.

---

## What actually got fixed (Fireball, the PARTICLE path only)

All verified by live capture. Files: `World/Spells/SpellParticleSystem.cs`, `World/Units/SpellAttachment.cs`,
`World/Units/SpellEffectSource.cs`, `Formats/M2Reader.cs`.

1. **Precast fire cupped in the hands** — `SpellAttachment.World` composed the MODEL-SPACE `M2Attachment.Position`
   through the posed bone frame `T(pivot)·Skin`, adding an extra `T(pivot)` that slid every spell attach off
   its joint by ~the bone pivot (hand→elbow). Fixed to `T(pos−pivot)·(T(pivot)·Skin)` = raw-skin, matching the
   working equipment path (`AttachedItemRenderer.cs:594`). **This fix helps ALL attaches** (precast, impact,
   state, missile launch) — but only the *particle* half of each effect.
2. **Missile launches from the hand + flies flat** — `ResolveModelPoint` (release marker) had the same pivot
   double-count (launch sat ~1yd in front of the hand); and the missile homed to dest-attach `0x22`, which on
   some NPCs resolves 60+yd off (into the sky). Fixed the pivot; added `ResolveMissileDestination` body-clamp
   (>12yd from base → chest centre). `SpellEffectSource.cs`.
3. **Impact placement + duration** — impact `0x22` clamped to body centre; and the impact **lasted seconds**
   because MSUI ignored (a) the emitter **enabled gate** (`+0x1dc`, now parsed as `M2ParticleEmitter.EnabledTrack`
   / `SampleEnabled`) and (b) the **0x8000 burst flag** (now a rising-edge single puff). Impact is now a ~0.5s
   flash. `M2Reader.cs`, `SpellParticleSystem.cs`.

**All of the above is the particle path.** None of it touches mesh or ribbon rendering, sound, or the caster's
cast animation.

---

## What is BROKEN — proven by live capture (2026-08-02) + render-path + code

Cast on a level-60 Magetest (all spells, 1000g). Effect chains from the `spell-sweep` verdict; render paths
from `--dump-emitters`; the "renders" column is what the PNG actually showed.

| Spell | Category | SpellVisual effect chain | Model make-up | Should look like | Actually renders |
|---|---|---|---|---|---|
| Arcane Explosion (1449) | instant self-AoE | `Magic_PreCast_Hand \| ArcaneExplosion_Base \| ArcaneExplosion_Impact_Chest` | Base = **mesh + 1 emitter** | big arcane blast **ring at the feet**, radiating out | **tiny sparkle** near the head; no ring |
| Frost Nova (122) | instant self-AoE | `Ice_Precast_Low_Hand \| Frost_Nova_area \| Ice_Precast_Uber_Head` | area = **mesh + 1 emitter** | expanding **ice ring** on the ground | **tiny blue sparkle**; no ring |
| Frost Armor (168) | persistent self-buff | `Ice_Precast_Low_Hand \| FrostArmor_Low_Head` | Head = **8 emitters** | frost **shimmer on the body** | **vertical fountain of light floating behind the mage** |
| Arcane Intellect (1459) | self/friendly buff | `Magic_Cast_Hand \| ArcaneIntellect_Impact_Base` | Impact = **PURE MESH, 0 emitters** | golden **arcane glow** on the target | **nothing renders** |
| all of the above | instant / self | — | — | caster plays a quick **cast gesture** | `animation=Stand` — caster does **not** animate |

### Root cause A (primary): the MESH and RIBBON render paths are UNPORTED

The benilla-faithful rewrite only produced `SpellParticleSystem.cs` (the emitter path). The **mesh** and
**ribbon** halves still use the pre-rewrite renderers:
- `World/Units/SpellEffectMeshRenderer.cs` (27 KB) — its own doc still says *"the particle half stays in
  ParticleRenderer"*, i.e. it predates the particle rewrite. Wired at `Program.cs:1954-1955` via
  `SpellEffectSource.MeshInstances`.
- `World/Units/SpellRibbonRenderer.cs` (13 KB) — wired at `Program.cs:1958-1959` via `RibbonInstances`.

This is exactly why the categories above break and Fireball doesn't: **Fireball's visible effects are all
particle** (precast-hand particles, missile-trail particles, impact particles → the path I fixed). The AoE
rings and buff glows are **mesh** (and some ribbon). `ArcaneIntellect_Impact_Base` is **pure mesh, 0 emitters**
→ the particle path draws nothing and the mesh path renders it invisibly/mislocated → **nothing on screen.**
The AoE "Base"/"area" models are **mesh + 1 emitter** → only the lone emitter draws (the tiny sparkle), the
mesh ring is missing. Frost Armor's 8 emitters draw but at the wrong place (floating beam) — a placement bug
in the particle path *for a non-hand attach and/or its mesh part*.

**benilla renders these through `attach_effect_visuals` (`entities/spell_fx.rs:297-491`)**, which draws the
model's skinned mesh parts, billboard cards, ground-plane **decals** (for base-anchored AoE — the ring
draping the ground), emitters, AND ribbons, all riding the effect model's own joint rig. MSUI's mesh/ribbon
renderers are not that. **P3 (mesh) and P2 (ribbons) must be rebuilt benilla-faithfully the same way
`SpellParticleSystem` replaced the portal particle path** — including ground-decal handling for AoE rings.

### Root cause B: spell SOUND is entirely missing (all categories, Fireball included)

- benilla plays a kit sound on precast/cast/impact/channel/state (`creature_anim/spell_visual.rs:359-361,
  559-564, 786-787`, state self-gated `404-408`) and a looping missile sound (`723, 737-748`).
- MSUI **parses** `SpellVisualKitInfo.Sound` and `SpellVisualStages.MissileSound` (`SpellVisualCatalog.cs:143,
  198, 51, 217`) but **never plays them**: `SpellEffectSource.SpawnKit` (`SpellEffectSource.cs:101-128`) reads
  only `kit.Effects`; `SpawnMissile` has no sound parameter; `ApplySpellGo` never reads `MissileSound`
  (`Program.Casting.cs:86-92`). Every cast whoosh, impact, buff, and Fireball's `FireMissileLoop` is silent.

### Root cause C: instant spells don't animate the caster

Every capture above shows `animation=Stand`. Fireball animates (`Anim51/53`) because it has a cast time
(precast hold → cast release). Instant/self spells resolve SPELL_START and SPELL_GO in the same tick, and MSUI
plays no cast one-shot. benilla still plays the kit's `AnimationData` id (kit field 2) on the body per stage
(`play_kit`, `spell_visual.rs:348-358`). Wire the cast/impact kit anim so instant casts show their gesture.

### Root cause D: the channel path is split (self vs observed)

benilla drives ALL channels (including self) from one `UNIT_CHANNEL_SPELL` descriptor poll with
replace/stop-hold semantics (`spell_visual.rs:757-816`). MSUI splits it: self via `BeginChannel`
(MSG_CHANNEL_*, no reap/stop, `Program.Casting.cs:325-337`) and observed via `UpdateObservedChannels` which
**skips the local player** (`:488`). Unify to one poll.

### Suspect refinements (verify at runtime, secondary to A–C)
- **S1 — `HasVisibleContent` gate.** `SpawnKit` skips models failing `HasVisibleContent`
  (`SpellEffectSource.cs:113` → `SpellAttachment.cs:168-170`: needs emitters/ribbons/submeshes). If MSUI's M2
  parser under-populates a state/AoE model, its kit is silently dropped where benilla (`spell_fx.rs:716-761`,
  no gate) shows it. (ArcaneIntellect has submeshes so it passes the gate — its failure is the mesh renderer,
  not this — but confirm per model.)
- **S2 — self-terminating span on zero-key sequences.** `SelfTerminatingSpan` reads `Sequences[0]` end-start,
  falls back to 1.0s (`SpellAttachment.cs:151-157`); benilla reads the raw first-seq span (`spell_fx.rs:766-772`).
  Can cut an AoE/impact short or hold it long.

---

## The deep dive — benilla orchestration (the reference), with MSUI's equivalents

MSUI's **orchestration is largely faithful already** — every stage, the aura watcher, and the Speed gate exist.
Keep this map when rebuilding the render paths so you don't regress the routing.

**Router:** benilla `creature_anim/spell_visual.rs` `route_cast_visuals` (`:433-819`) ↔ MSUI `Program.Casting.cs`
(`ApplySpellStart :24`, `ApplySpellGo :48`, `ApplySpellImpact :123`).

**The 5 stages** (SpellVisual.dbc fields 1-5; `SpellVisualCatalog.cs:202-218` ↔ `spell_visual.rs:9-11`):
| Stage | Field | Lifetime | Attaches to | Fires | benilla | MSUI |
|---|---|---|---|---|---|---|
| precast | 1 | persistent (Hold) | caster | SPELL_START | `spell_visual.rs:497-577` | `Program.Casting.cs:24-46` |
| cast | 2 | self-terminating | caster | SPELL_GO | `:579-644` | `:48-111` |
| impact | 3 | self-terminating | target | arrival / inline | `play_impact :387-415` | `ApplySpellImpact :123-158` |
| state | 4 | persistent (AuraState) | target/self | **aura present** | `arm_aura_state_fx :846-898` | `UpdateAuraStateVisuals :456-480` |
| channel | 5 | persistent (Hold) | caster/self | channel field set | `:757-816` | `BeginChannel/UpdateObservedChannels :325,482` |

- **State/self-buff** is owned by an **aura watcher**, not the cast: benilla `arm_aura_state_fx` spawns the
  state kit's models when the spell id **appears** in `unit_auras()` and reaps when it **leaves**
  (`spell_visual.rs:846-898`); MSUI mirrors this in `UpdateAuraStateVisuals` off `unit.Fields.Auras()`
  (`Program.Casting.cs:456-480`). This structure is correct — the buff *visual* fails at the render path (A).
- **AoE / no-missile** gates on **`Spell.dbc` Speed alone** (`spell_visual.rs:688`): Speed>0 → missile;
  Speed<=0 → inline `play_impact` per hit. MSUI matches (`Program.Casting.cs:99-102`). The AoE burst is the
  **cast kit** at the caster (plays on GO regardless of Speed) + the impact kit on each hit.
- **KIT_SLOT_TAGS** `[0x14,0x22,0x13,0x15,0x16,0x11,0x17,0x18,0x19]` (kit fields 3-11 → M2 AttachmentID),
  attach cascade tag→0xf→0x13→base — byte-identical between clients.

Full benilla render body to port from: `entities/spell_fx.rs:297-491` (`attach_effect_visuals` — mesh parts,
cards, ground decals, emitters, ribbons on the effect rig), `ribbons.rs`, `particles.rs` (model-particle
geometry emitters), `entities/missile.rs`.

---

## How to verify (harness)

```bash
dotnet run --project MSUIClient/MSUIClient.csproj -- MSUIClient/client-config.json --live-bootstrap --character Magetest --live-protocol scenarios/spells/<scn>.txt --out live-runs --timeout 170
```
`Magetest` = level-60 GM mage, all spells, 1000g. Server: LAN VMaNGOS. Window 1600×900. `scenarios/spells/noncombat-sweep.txt`
casts the four broken spells above (1459 Arcane Intellect, 168 Frost Armor, 1449 Arcane Explosion, 122 Frost
Nova) and dumps each. `cast <id>` fires by spell id; `dump <name>` writes `dumps/gameplay-<name>.png` + `.json`.
`--dump-emitters "Spells\<Model>.mdx"` (offline) prints emitters/ribbons/**sequences**/gate-over-time — use it
to see whether an effect is mesh vs particle vs ribbon before touching it.

Beware: several harness steps inject synthetic success verdicts. Only the **PNG** is real evidence. The dev
"Server" overlay occludes the left ~30% of every capture — orbit the camera (`camera <yaw> <pitch> <dist>`,
re-apply it on the line right before each `dump` or it decays) and `face 0` before casting or the server
rejects the cast.

---

## The plan (what remains — this IS the original P2/P3/P4, now with proof)

Verify each phase by capturing the broken spells above and comparing to real 1.12 / benilla by eye.
1. **P3 mesh (highest impact)** — rebuild the effect-model **mesh** render path benilla-faithfully
   (`attach_effect_visuals` mesh parts + **ground-plane decals** for base-anchored AoE rings + billboard cards),
   replacing `SpellEffectMeshRenderer`. This is what makes Arcane Explosion / Frost Nova / Arcane Intellect
   render at all. Reuse the now-correct attach math (`SpellAttachment.World`) and the effect-rig posing already
   in `SpellEffectSource.MeshInstances`.
2. **P2 ribbons** — rebuild the ribbon path from benilla `ribbons.rs` (trail ribbons, per-sequence visibility);
   replaces `SpellRibbonRenderer`. Needed for missile trail ribbons and some buff/channel effects.
3. **Sound** — play `SpellVisualKitInfo.Sound` in `SpawnKit` and thread `SpellVisualStages.MissileSound` into
   `SpawnMissile` as a tracked loop (state-kit sound self-gated). Data is already parsed.
4. **Cast anim on instant spells** — play the cast/impact kit's `AnimationData` id on the caster body for
   Speed==0 / instant casts (currently `animation=Stand`).
5. **Channel unify** — one `UNIT_CHANNEL_SPELL` poll for self + observed, with replace/stop-hold.
6. **S1/S2** — audit the `HasVisibleContent` gate per state/AoE model; align `SelfTerminatingSpan` with
   benilla's raw first-seq span.

## Start here (next session)
1. Read this whole doc + the memory files (`project-spell-particle-state`, `reference-benilla`,
   `project-live-run-harness`, `feedback-docs-oversell`). Full phased plan:
   `C:\Users\nico\.claude\plans\lively-inventing-swing.md`.
2. Run `scenarios/spells/noncombat-sweep.txt`; read the 4 PNGs; confirm the table above still holds.
3. `--dump-emitters` each broken model to see its mesh/emitter/ribbon make-up.
4. Do **P3 mesh first** (it unblocks the most-visible breakage), verifying each spell by eye at ≥1 angle
   against real 1.12. Do NOT claim a category is fixed until a capture shows it.

## Temporary diagnostics still in the tree (remove before any commit)
`[spell-fx-place]` Console log + `_loggedPaths` in `SpellParticleSystem.Simulate`; the `--dump-emitters`
sequence/gate additions in `Program.cs` are worth KEEPING (they're useful). Nothing is committed.

---

## Original mission (unchanged, for context)
Rip MSUI's spell FX out of the portal-tuned shared renderer and rebuild it as a **separate benilla-faithful
path** (benilla = the Rust reference at `C:\Users\nico\Desktop\benilla-main`), phased P1 particles → P2 ribbons
→ P3 mesh → P4 orchestration. **P1 is done for Fireball's particle effects; P2/P3 and the sound/anim/channel
orchestration gaps above are the remaining work.**
