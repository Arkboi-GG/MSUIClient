# MSUI Benilla Spell-Parity Implementation Record

Date: 2026-08-02  
Scope: MSUIClient spell-system changes made from the Benilla trace (A) and the pre-change MSUI trace (B)  
Status: implemented and compile-verified, with remaining differences explicitly inventoried below

## 1. Purpose and source identity

This is the requested after-change record. It explains what was changed in MSUI, why each change was made,
where the new behavior lives, and what is still different from Benilla. It is not a replacement for either
source trace:

- **A — Benilla reference:** `BENILLA_SPELL_SYSTEM_TRACE.md`, SHA-256
  `4568681015092AFEBA1BBBF2A389AEC4298E42AE1D6886817651C157CAE578B5`.
- **B — pre-change MSUI baseline:** `MSUI_SPELL_SYSTEM_TRACE.md`, SHA-256
  `0509BE6F9C8C992EA3B32AC4D8601C10AE1454ED22B1DF4231D94F229213D1EC`.

The baseline documents were intentionally left unchanged. A remains the behavior reference; B remains an
honest snapshot of MSUI before this parity pass. This document is the delta and the new implementation map.

The target is **Benilla parity**, not an invented local spell simulator and not an undocumented mixture of
original-client behavior. Benilla itself leaves gameplay outcomes to the server and has known limitations;
those are preserved unless this record explicitly says MSUI consciously supersedes one.

### Status terms

- **Matched:** this pass changed MSUI to follow the described Benilla behavior.
- **Already matched:** B's implementation already had the relevant Benilla behavior; no change was needed.
- **Preserved Benilla boundary:** the apparent omission is also an explicit Benilla limitation.
- **Partial:** this pass closed part of the gap, but the stated remainder is real.
- **Remaining:** not implemented and not claimed as parity.

## 2. Resulting system separation

The principal architectural change is that spell wire parsing no longer performs presentation mutations
inline. The new path is:

```text
network bytes
  -> complete immutable START/GO/FAIL/DELAY/CHANNEL/KIT-PUSH facts
  -> ordered spell-presentation FIFO
  -> object-update slice completes
  -> cast router consumes events in packet order
  -> body animation + effect roots + missiles + aura/channel watchers
  -> attachment refresh
  -> particle/ribbon simulation and draw
  -> positional sound maintenance
  -> cast/aura/cooldown UI
```

This implements A §19.2 and §19.5's separation and ordering contract and closes B §5.1/§5.2 and §19.3's
inline-routing weakness.

The concrete boundary is:

- Wire structures and complete target decoding: `MSUIClient/Net/SpellPackets.cs:10-91`.
- Immutable event types, monotonic sequence, FIFO drain: `MSUIClient/Program.SpellEvents.cs:12-69`.
- Packet handlers enqueue only: `MSUIClient/Program.Net.cs:640-681`.
- Drain after the current object-update slice and before spline/animation work:
  `MSUIClient/Program.Net.cs:749-759`.
- Stage routing and runtime watchers: `MSUIClient/Program.Casting.cs:26-574`.

This matters especially for an instant cast whose START and GO arrive together. START always exists as a
real event, then GO reaps that exact hold in order. The implementation does not merge them into one
"instant spell" shortcut and cannot leave a precast effect stuck merely because both packets were handled
in one frame. That is A §4.4, §11.4, and §19.3.

## 3. Wire fidelity and target preservation

### 3.1 START and GO facts

`SpellStartPacket` and `SpellGoPacket` now keep both packed caster identifiers:

- `ItemCaster`, the first packed GUID;
- `Caster`, the unit casting the spell;
- cast flags;
- START cast time;
- complete targets;
- both optional ammo values, display id and inventory type;
- GO hit GUIDs and miss `(GUID, reason)` rows in server order.

The implementation is `MSUIClient/Net/SpellPackets.cs:19-73`. This follows A §3.1/§3.2, §14.2, and
§19.2, and closes B §19.3's discarded first GUID and discarded ammo inventory type.

### 3.2 Target block

`SpellTargets` now retains:

- raw mask;
- unit/game-object/corpse GUID branch;
- item/trade-item GUID branch;
- source transport GUID and source position;
- destination position;
- string target.

See `MSUIClient/Net/SpellPackets.cs:10-17,77-91`. Benilla's public target projection intentionally drops
some of these values (A §14.1 and §20.4), but keeping already-decoded wire facts is a conscious, harmless
superset: it prevents loss at the decoder boundary without pretending MSUI has a UI sender or visual
consumer for every branch.

No target is synthesized from `Spell.dbc`. GO's hit/miss arrays remain the presentation multiplicity, as
required by A §13.3, §14.2, and §17.4. This is why speed-zero AoE and chained spells show one arrival for
each server hit, rather than running a local radius/chain query.

## 4. Cast admission and outbound specialization

### 4.1 Added shared-path admission checks

The ordinary action-bar path now performs these additional checks before sending:

1. the spell is actually in the server-fed learned-spell roster;
2. profession opener spells open the profession window and do not send a cast;
3. reagent counts satisfy every nonzero reagent row;
4. required tools/totems are carried;
5. the required nearby spell-focus game object is present.

See `MSUIClient/Program.ActionBars.cs:88-165`, `MSUIClient/Formats/SpellCatalog.cs:30-56,124-143`, and
`MSUIClient/Net/CastTargetLaw.cs:6-27`. Equipped inventory slots now participate in carried-item counts, so
an equipped tool is not incorrectly reported missing: `MSUIClient/Program.Professions.cs:387-415`.

These changes implement the relevant legs of A §14.4 and §14.6 and narrow B §19.1's admission gap. Existing
power, mounted, pending, target-relation, min/max range, auto-repeat, next-swing, and local-GCD gates remain
in their established ordering.

### 4.2 Specialized open-lock/game-object cast

MSUI now has Benilla's separate game-object path rather than forcing a GO through the ordinary unit target
builder:

- the clicked object's Lock record is inspected;
- learned spells are scanned for `OPEN_LOCK` effect id 33;
- an effect lane's `EffectMiscValue` must match a `LOCK_KEY_SKILL` slot;
- the selected spell is sent with mask `0x4800` and the packed game-object GUID;
- a successful send arms the normal pending spell observation.

See `MSUIClient/Program.GameObjects.cs:111-169`,
`MSUIClient/Net/WorldSession.cs:314-326`, and
`MSUIClient/Net/NetworkClient.cs:289-294`.

This directly implements A §14.6 "GameObject open-lock/gathering" and closes the game-object portion of B
§19.1. It remains a specialized path, deliberately bypassing parts of the shared cast gate just as Benilla
does.

### 4.3 Admission that remains absent

MSUI still does not provide generic ground-cursor, item-enchant cursor, trade-item, source-position, or
string-target sending. Generic ground sending is also absent in Benilla (A §14.6 and §20.4). Benilla does
have narrower item-craft and skinning paths; those remain MSUI gaps. Party/raid implicit targets beyond self
also remain unimplemented, matching Benilla's stated limitation.

The full Benilla usability predicate is not yet present. Death/ghost, stance/form, stealth,
out-of-combat-only, equipped class/subclass, caster/target aura-state, silence, stun, pacify, ammo, combo
point, and some attribute-specific gates still rely on the server. A itself names several of those as
omissions, but form/equipment/aura-state are genuine parity work still outstanding.

## 5. Cooldown ownership and timing

The old one-record-per-spell model was split into independent clocks:

- `_spellCooldowns`, keyed by spell id;
- `_categoryCooldowns`, keyed by category id;
- the existing GCD deadline remains separate.

The server's initial spell packet now loads spell recovery and category recovery independently.
`StartCooldown` accepts both durations, and all queries choose the active clock with the longest remaining
time. Expired records remove themselves. See `MSUIClient/Net/PlayerActions.cs:18-19,50-76,101-163`.

`SpellInfo` now retains Spell.dbc category field 2:
`MSUIClient/Formats/SpellCatalog.cs:4-15,124`. Action admission, action-button sweep, and diagnostic sweep
all pass the category:

- `MSUIClient/Program.ActionBars.cs:116,193-194,424-430`;
- `MSUIClient/Program.DevTools.SpellSweep.cs:68-75,84-93`.

The timing edges now align with A §16.6/§16.7:

- GCD begins at local send;
- a matching failure clears that local GCD, not spell/category recovery;
- spell and category recovery begin on the matching player GO;
- spell/category/GCD are queried separately;
- UI progress uses the longest relevant active spell/category clock.

The GO start is `MSUIClient/Program.Casting.cs:74-81`; local GCD admission/start remains
`MSUIClient/Program.ActionBars.cs:116,187-195`.

This closes B §19.2's category fan-out gap. Live post-login server cooldown-list overwrite packets and
cooldown-on-event parking/release are still not implemented.

## 6. Cast-stage router and lifetime rules

### 6.1 START / PRECAST

At START the router:

- records the PRECAST diagnostic stage;
- resolves the effective visual and precast kit;
- stops an older tracked hold sound for that caster;
- plays the precast kit sound;
- spawns every resolved effect slot as persistent hold FX;
- begins the authored player/creature body hold;
- snaps ranged sheath state;
- starts a normal cast bar only for timed, non-ranged player casts.

Implementation: `MSUIClient/Program.Casting.cs:26-51`. This follows A §4.1/§4.2, §11.4, §12.7, and
§16.4.

### 6.2 GO / CAST

At GO the router:

- reaps the matching persistent precast FX;
- stops the hold sound;
- plays the cast/release kit sound;
- spawns cast-kit FX with authored self-terminating lifetime;
- releases the body animation and completes the matching cast bar;
- clears matching pending/next-swing state;
- starts independent spell/category cooldowns for the local player;
- preserves hit and miss target order for the arrival path.

Implementation: `MSUIClient/Program.Casting.cs:53-90`. This follows A §4.1/§4.2, §16.7, and §19.3.

### 6.3 IMPACT and STATE

For each speed-zero hit or arriving projectile hit, MSUI:

1. spawns the impact kit as a self-terminating instance;
2. plays its sound;
3. plays the impact body's authored animation if nonzero;
4. then plays the state kit's body leg;
5. plays state sound only when the target is the local player;
6. leaves persistent state effect models to the aura watcher.

Implementation: `MSUIClient/Program.Casting.cs:122-170`. The order and the separation between impact-time
state body/sound and aura-owned state models follow A §4.5/§4.6, §15.5, and §19.3.

No generic wound is fabricated when the impact kit has no animation. Only authored wound-family ids 8, 9,
and 10 enter the wound reaction route. That preserves the distinction documented in A §11.5 and prevents
non-combat visuals from making the target flinch.

### 6.4 FAIL

A failure clears only matching local pending/queued state, cancels that caster's body hold, stops its
tracked sound, and reaps its persistent hold FX without playing the release kit. Remote
`SMSG_SPELL_FAILED_OTHER` uses the same visual cleanup. See
`MSUIClient/Program.Casting.cs:187-200` and `MSUIClient/Program.SpellEvents.cs:44-51`.

This is A §4.1 FAIL and §16.2.

### 6.5 CHANNEL and pushed kits

Local channel messages begin/update/stop the channel bar, persistent channel kit, body hold, and sound.
Remote units are descriptor-polled; a changed channel reaps the old kit/sound before arming the new one,
and descriptor clear reaps all three. Pushed raw visual-kit packets spawn a new transient kit and sound.

See `MSUIClient/Program.Casting.cs:332-381,536-574`. This follows A §4.7, §12.7, §15.9, and §19.3.
Remote channels deliberately still have no player-style cast bar because the descriptor carries no
duration; local channel packets remain implicitly self because that wire body carries no caster.

## 7. Missile pipeline

The projectile implementation was changed from a GO-time fixed deadline to Benilla's release-keyed,
launch-time homing contract.

### 7.1 Spawn and release

GO creates one missile for every server hit and miss when `Spell.Speed > 0`. The model chain remains
SpellVisual missile effect, error cube for a declared-but-unresolved missile effect, then ranged ammo model
and optional ammo texture fallback. Speed-zero rows hand off immediately per target.

The cast model is searched across `$CSL`, `$CSR`, `$CST`, and `$BWR`; the globally earliest event inside the
chosen cast sequence wins, rather than a hard-coded identifier priority. If no marker exists, the authored
cast sequence end is the release backstop; a degenerate sequence uses 0.25 seconds.

See `MSUIClient/Program.Casting.cs:91-119` and
`MSUIClient/World/Units/SpellEffectSource.cs:158-180,444-470`. This implements A §10.1/§10.2 and closes B
§19.3's release-order gap.

### 7.2 Launch-time travel

Source and destination attachment points are resolved when the missile actually launches. Remaining time
is calculated as:

```text
remaining = current_distance / speed - seconds_already_queued
```

If that value is nonpositive, arrival is handed off immediately without creating a visible flight or loop.
See `MSUIClient/World/Units/SpellEffectSource.cs:233-266`. This closes B §19.3's GO-time deadline bug and
implements A §10.3 and §19.6.

### 7.3 Homing, removal and arrival

Each update resolves the current live target attachment and applies:

```text
position += (live_destination - position) * (dt / remaining)
remaining -= dt
```

Arrival happens before a final position snap. A missing source at launch or missing target at any point
destroys the missile; it never homes toward world zero. The model's local +X axis is oriented along the live
flight direction with zero roll. See `MSUIClient/World/Units/SpellEffectSource.cs:267-292,367-386`.

Hits enter IMPACT. Misses suppress impact; only Dodge and Block request the corresponding body defense
reaction, exactly as Benilla documents. See `MSUIClient/Program.Casting.cs:122-130`.

This implements A §10.4-§10.6 and preserves A's known no-collision/no-ballistic-path limitation.

### 7.4 Missile particle and sound ownership

Emitter attachment is now an explicit instance property supplied by the effect source, not inferred from
whether a model path contains the word "Missile". Attached spell effects ride the owner; missile trails can
remain world-frozen. See `MSUIClient/World/Units/SpellEffectSource.cs:299-335` and
`MSUIClient/World/Spells/SpellParticleSystem.cs:92-97,128-184`.

Missile sound starts on visible launch and stops on arrival, target loss, or destruction. Each projectile
gets an independent voice, so one multi-target projectile cannot stop another target's flight loop. See
`MSUIClient/Program.Casting.cs:110-119` and
`MSUIClient/World/Units/SpellEffectSource.cs:52-54,233-289`.

## 8. Aura UI, timers, tracking and state FX

### 8.1 Timer-to-slot binding

Aura timers now store `{duration, expires, received, spellId}`. When the duration packet precedes a slot's
descriptor replacement, a timer may rebind to the new spell only within the one-second freshness window;
otherwise it is discarded. UI and diagnostics show a timer only when its stored spell id matches the live
slot. See `MSUIClient/Program.DevTools.Auras.cs:9-14,54-78,134-145`.

This implements A §15.3 and closes B §19.4's recycled-slot timer bug. A zero countdown still does not delete
the aura: descriptor removal is authoritative in both Benilla and MSUI.

### 8.2 Ordering, visibility and stack transitions

The local player's aura feed now preserves insertion order: surviving `(slot,spell)` pairs retain their
relative positions and new auras append. Other targets remain raw-slot ordered, and target-self mirrors the
player ordering. Stack-only changes now emit `STACK`, not a false `REMOVE+STACK` lifetime transition.

See `MSUIClient/Program.DevTools.Auras.cs:27-67,153-166` and
`MSUIClient/Program.UnitFrames.cs:154-248`. This implements A §15.2 and corrects the corresponding B §19.4
boundary.

Hidden-client-side spells remain filtered. Missing metadata or icon paths fail open through
`GameplayArt`'s question-mark fallback, so a live server aura is not silently removed merely because local
art metadata is incomplete. Tracking aura types 44, 45, and 151 are omitted from normal buff/debuff rows.

### 8.3 Tracking and cancellation

The minimap scans raw aura slots in ascending order and retains the last matching tracking aura, as Benilla
does. Right-click uses the same aura flag bit `0x01` cancelability law as the normal aura bar instead of
bypassing it. See `MSUIClient/Program.Minimap.cs:161-183` and
`MSUIClient/Program.DevTools.Auras.cs:82-103,168-173`.

### 8.4 Persistent state effects

The state watcher scans each streamed unit's distinct live aura spell ids. It arms the state FX key only
when at least one effect model actually spawned; failed resolution therefore remains retryable. Aura
removal reaps only `StageLife.AuraState`, avoiding same-spell hold/channel destruction.

See `MSUIClient/Program.Casting.cs:506-534` and
`MSUIClient/World/Units/SpellEffectSource.cs:104-134,217-220`. This implements A §15.5 and closes B §19.4's
false-active state bug.

## 9. Effect assets, model data and lifetime

### 9.1 Asset retry

Failed path/model loads are no longer permanently cached as null. A failed path is timestamped and becomes
eligible for another load after ten seconds; a successful load clears the failure. See
`MSUIClient/World/Units/SpellEffectSource.cs:60-64,477-503`.

This follows A §5.1/§5.7 and §19.8 and closes B §19.5's permanent negative-cache failure.

### 9.2 Unified effect instance fan-out

Kits still resolve all authored effect slots into model instances owned by one runtime source. Meshes,
billboards/ground handling, particle emitters, and ribbons consume the same instance transform, model age,
and attachment. Persistent, aura-state, self-terminating, and missile lifetime states remain distinct.
`SpawnKit` now returns its successful spawn count so callers can make liveness decisions from real content.

See `MSUIClient/World/Units/SpellEffectSource.cs:6-64,104-134,299-360` and A §4.3, §5.1, and §19.4.

Non-missile effect models continue to use their first preferred sequence; missiles request animation 144.
That follows A §1.6, §5.3, and §10.6 rather than inventing a per-stage effect-model animation table.

## 10. Particle data parsing

The vanilla 504-byte M2 particle record now retains data that B previously ignored:

- head/tail selector from byte `+0x2C`;
- validated power-of-two atlas rows and columns;
- both head cell ranges and repeats;
- both tail cell ranges;
- tail time;
- inherited-motion scale;
- base spin and angular-velocity ranges;
- follow response points `(speed1,scale1)` and `(speed2,scale2)`;
- spline control-point array;
- ribbon `TextureSlot` as an animated unsigned-short track.

The model fields and sampling helpers are in `MSUIClient/Formats/M2Reader.cs:600-790,1047-1149`; binary
reads are `MSUIClient/Formats/M2Reader.cs:2152-2215`; ribbon texture-slot parsing is
`MSUIClient/Formats/M2Reader.cs:2060-2075`.

Cell sampling uses the two authored ramps, shared repeat counts, and atlas wrap. Spline position is sampled
as cubic Bezier segments with a 16-chord arc-length approximation, matching A's named approximation in
§7.3 and §20.4. These changes implement A §2.6/§2.7, §7, and §8 and close the parser portions of B
§19.7/§19.8.

## 11. Particle simulation and rendering

Spell particles remain isolated from the portal/doodad particle renderer. The spell renderer loads
`MSUIClient/Shaders/spell_particle.vert` explicitly and is invoked after live spell attachment transforms
are available: `MSUIClient/Program.Net.cs:129-139`, `MSUIClient/Program.cs:2085-2093`.

### 11.1 Simulation changes

The spell particle simulation now implements:

- explicit attached versus detached/world-frozen storage;
- far-range freeze with no emission/integration catch-up and origin refresh on re-entry;
- follow-delta response using the authored two-point line;
- approximately 30 Hz inherited owner velocity and birth-time inheritance;
- plane, sphere, and real spline birth kernels;
- spline tangent-axis vertical spread and horizontal scatter;
- the required local-Z `Rot90` followed by WoW-to-MSUI axis swap exactly once;
- `zSource` direction pivot behavior;
- ground-snap birth flag `0x2000`, downward collision/terrain search up to 20 yards, and birth-size lift;
- the existing enabled gate, rising-edge burst, distance LOD, 0.1-second integration clamp, drag, gravity,
  kill-outbound, lifetime, ramp, and 1024-particle pool cap.

The relevant implementation is:

- placement/freeze/follow/inherit: `MSUIClient/World/Spells/SpellParticleSystem.cs:128-259`;
- integration/emission: `MSUIClient/World/Spells/SpellParticleSystem.cs:263-349`;
- birth kernels and ground snap: `MSUIClient/World/Spells/SpellParticleSystem.cs:351-470`;
- world ground query: `MSUIClient/Program.Casting.cs:495-507`.

This implements A §7.1-§7.7 and closes B §19.7's spline, follow, inheritance, ground, and path-heuristic
gaps. The 250-yard simulation gate and 1024 cap are retained because they are Benilla constants, not MSUI
accidents.

### 11.2 Render changes

The draw builder now supports:

- head-only, tail-only, and head+tail particle modes;
- camera-facing head cards;
- authored XY-plane head quads;
- time-based spin, including bit `0x20` alternating phase/sign behavior;
- tail vector `-velocity * effectiveTailTime`;
- flag `0x400` clamping tail time to particle age;
- degenerate-tail rejection at squared length `7.7e-4`;
- independent head and tail atlas cells;
- texture-repeat sampling;
- per-particle right/up axes in an 18-float instance record.

See `MSUIClient/World/Spells/SpellParticleSystem.cs:528-634,649-740` and
`MSUIClient/Shaders/spell_particle.vert:1-30`. This implements A §8.1-§8.7 and closes the sprite/tail/XY/spin
portion of B §19.7.

Model-particle geometry, model-particle tumble/rigs, recursive child emitters, wind, collision, soft
particles, lighting/fog/gamma matching, and global transparent sorting remain unimplemented. They are not
silently treated as complete.

## 12. Ribbon simulation and rendering

Ribbon `TextureSlot` is now sampled from the live model sequence. The value is clamped to the atlas cell
count, split into row/column, and used to map U along edge age within the selected cell and V to that cell's
row. Draining trails retain the atlas selection. See
`MSUIClient/World/Units/SpellRibbonRenderer.cs:101-185,193-231`.

This closes B §19.8's texture-slot/atlas gap and implements A §8.8's UV behavior.

The following behavior was deliberately retained because it is also Benilla behavior or an explicitly
documented Benilla approximation:

- at most one committed edge per frame;
- incremental `2 * gravity * dt` sag;
- current sampled color/alpha applied uniformly to surviving edges;
- blend modes 5/6 folded to ordinary alpha.

There is still no global transparent depth sort or spline smoothing. Those are not claimed as completed
render parity.

## 13. Spell sound

### 13.1 Data and playback owner

`SoundEntries.dbc` now has a data-only catalog covering the complete 29-field build-5875 row used by the
spell path: id, type, name, ten filenames, ten weights, directory, volume, flags, minimum distance, cutoff
distance, and EAX id. It exposes looping flag `0x200` and no-duplicates flag `0x20`.

See `MSUIClient/Formats/SoundEntriesCatalog.cs:3-54`. SpellVisual also retains both missile sound and strike
sound fields: `MSUIClient/Formats/SpellVisualCatalog.cs:50-51,205-219`. This implements A §2.8 and §12.4.

`SpellSoundSystem` owns variant choice, archive extraction, voices, per-unit tracked holds, untracked
missile loops, attenuation, cleanup, and diagnostics. Weighted selection honors row weights; no-duplicates
avoids repeating a variant when the row contains a real alternative. Looping holds replace the prior hold
for that unit. See `MSUIClient/World/Spells/SpellSoundSystem.cs:13-170`.

### 13.2 Trigger matrix now implemented

- START: stop old hold, play precast kit sound.
- GO: stop hold, play cast kit sound.
- IMPACT: play impact kit sound.
- STATE arrival: play only for the local player.
- FAIL: stop caster hold.
- CHANNEL start/replacement/clear: play and stop with channel lifetime.
- PUSHED KIT: play the raw kit's sound.
- MISSILE: start an independent loop on actual launch; stop on arrival/destruction.

Triggers are in `MSUIClient/Program.Casting.cs:26-38,53-64,110-119,132-170,187-200,332-381,536-574`.
Ownership initialization and shutdown are `MSUIClient/Program.Net.cs:46,123` and
`MSUIClient/Program.cs:3239-3244`.

The audible backend uses Windows MCI and temporary per-process archive extraction. Positional loop volume
is refreshed against current unit/listener positions; out-of-range or missing-owner loops stop. On
non-Windows platforms routing and counters remain observable but playback is intentionally silent.

### 13.3 Sound still missing

- StrikeSound is parsed but the `$TRD`/strike animation-event trigger is not wired because the current body
  animation driver exposes no event cursor to the spell system.
- General effect-model `$SND` event playback is absent.
- SoundEntries pitch variation flag `0x400` is not applied.
- Benilla's category concurrency cap is not implemented.
- The backend is audible only on Windows.

These are the remaining parts of A §12 and B §19.5. They are explicit, not hidden behind the new kit audio.

## 14. Cast failure names and user text

The partial cast-result switch was replaced with the complete build-5875 `0x00..0x91` symbolic table and
user-facing mapping. Hidden/don't-report results remain empty; unknown values retain their numeric identity.
Important corrected examples include line-of-sight `0x2A`, reagents `0x5C`, and totems `0x78`.

See `MSUIClient/Net/SpellCastResultNames.cs:3-115`. This implements A §16.2's centralized mapping contract
and closes B §19.9's partial error-text gap.

## 15. Spell-class coverage after the change

There is still no per-spell switch. The same data-driven pipeline covers the complete class matrix described
by A §13.2 and B §18:

| Spell class | Resulting path |
|---|---|
| Instant self/unit spell | Ordered START then GO; persistent precast is reaped in the same drain; cast kit is transient. |
| Timed self/unit spell | START hold/effects/sound and cast bar; GO release/effects/sound/cooldown. |
| Projectile hit | One release-keyed homing missile per GO hit; launch loop; impact/state handoff on arrival. |
| Projectile miss | One missile per GO miss; no impact; Dodge/Block only body defense; loop still ends. |
| Speed-zero hostile/friendly spell | Immediate arrival per GO hit; impact/state path is independent of local damage/heal logic. |
| Caster AoE / multi-target / chain | One immediate arrival or missile per server hit/miss row; no local radius or chain synthesis. |
| Self buff / target buff / debuff | Cast arrival is transient; descriptor-owned aura icon/timer/state FX persists until server removal. |
| Periodic aura / proc / shield | Aura lifetime is descriptor-owned; ticks/outcomes remain combat-log/server-owned. |
| Channel | Local message-driven bar plus descriptor-visible hold/FX/sound; remote descriptor watcher. |
| Tracking | Excluded from normal aura list; last raw matching slot drives minimap; normal cancelability law. |
| Auto-repeat / ranged | START/GO remain separate, no normal ranged cast bar, sheath/release and missile fallback preserved. |
| On-next-swing | Remains queued rather than presented as an immediate ordinary cast. |
| Profession opener | Opens profession UI and sends no cast. |
| Recipe/craft | Reagent/tool/focus admission improved; inventory outcome remains server-owned. |
| Open lock / gathering GO | Specialized learned OPEN_LOCK scan and `0x4800` game-object packet. |
| Generic ground cursor | Not originatable, matching Benilla's explicit limitation; incoming GO targets still present per GUID. |
| Pushed visual kit | New transient kit/body/sound per packet, outside normal cast ownership. |
| Missing metadata/model/audio | Visible/icon fallback where defined, retryable model failure, and silent skip rather than a crash. |

Damage, healing, energize, aura application, threat, dispel, summon, teleport, item creation, and periodic
tick mechanics are not locally executed. This is deliberate: A §13.1, §17, §18.8, and §20.4 establish the
server as gameplay authority. Visual arrivals and combat logs remain separate inputs rather than being
deduplicated or used to manufacture gameplay.

## 16. Exhaustive B §19 boundary disposition

This ledger addresses every pre-change boundary from B §19.1-§19.9. It is the authoritative answer to
"what happened to each gap?"

### 16.1 B §19.1 — admission, targeting and gameplay

| # | B boundary | Disposition after this pass | A reference |
|---:|---|---|---|
| 1 | No local effect execution | **Preserved Benilla boundary.** Server still owns all gameplay outcomes. | A §13.1, §17, §18.8, §20.4 |
| 2 | Ordinary casting only self/unit | **Matched for Benilla's ordinary path.** GO now has its separate specialized path; item/skinning special paths remain. | A §14.6 |
| 3 | No ground/GO/item/trade/source/destination/string/multi builder | **Partial.** Specialized GO added. Generic ground/source/string/trade remains absent like Benilla; item-craft remains a genuine MSUI gap. | A §14.1, §14.6, §20.4 |
| 4 | Only implicit target A drives local targeting | **Preserved Benilla boundary.** No unsupported local effect-lane synthesis was added. | A §14.3 |
| 5 | Party/raid only self | **Preserved Benilla boundary.** | A §14.3, §20.4 |
| 6 | Many local admission gates absent | **Partial.** Learned spell, profession opener, reagents, tools/totems, focus, equipped tool count added; form/equipment/aura-state and other stated gates remain. | A §14.4 |
| 7 | 3D reach-expanded range only | **Already matched.** This is Benilla's own 3D range formula and named simplification. | A §14.5 |
| 8 | No queue window/latency prediction/batch reconciliation | **Preserved Benilla boundary.** No speculative client authority was added. | A §16.1, §20.4 |
| 9 | Send becomes pending before acceptance | **Already matched.** This is Benilla's `PendingCast` behavior. | A §16.1 |

### 16.2 B §19.2 — cast, channel and cooldown state

| # | B boundary | Disposition after this pass | A reference |
|---:|---|---|---|
| 1 | Ranged START has no normal cast bar | **Preserved Benilla behavior.** | A §16.4 |
| 2 | Local channel packet assumed self | **Preserved wire behavior.** The message has no caster field. | A §15.9 |
| 3 | Remote channels have no bars/durations | **Preserved descriptor limitation.** Remote FX/body/sound lifecycle is maintained. | A §15.9 |
| 4 | Escape does not cancel channels | **Preserved Benilla behavior.** | A §16.5 |
| 5 | Channel movement waits for server; normal cast fails locally | **Preserved Benilla behavior.** | A §16.5 |
| 6 | Matching failure clears GCD but not recovery | **Already matched.** | A §16.2, §16.7 |
| 7 | Category fan-out absent | **Matched.** Separate spell/category clocks and longest-active selection added. | A §16.6-§16.7 |
| 8 | Live cooldown-list updates absent | **Remaining.** Initial list and local GO edges work; later authoritative overwrite packets are not handled. | A §16.7 |
| 9 | Action tint omits gates/cooldowns; secondary overlays absent | **Partial.** Main action cooldown/category tint is wired; most full-usability gates and secondary overlays remain. | A §14.4, §16.6 |

### 16.3 B §19.3 — START/GO, miss and missile routing

| # | B boundary | Disposition after this pass | A reference |
|---:|---|---|---|
| 1 | First packed GUID discarded | **Matched.** `ItemCaster` is retained on START and GO. | A §14.2, §19.2 |
| 2 | START targets do not place precast FX | **Preserved correct behavior.** Precast FX attach to the caster's authored kit tags, not the target block. | A §4.2, §6 |
| 3 | Explicit unit/destination alone does not create GO arrival | **Preserved Benilla boundary.** Hit/miss arrays own arrival multiplicity; destination-stage art is deferred. | A §10.1, §14.6, §20.4 |
| 4 | No local AoE/chain target computation | **Preserved Benilla/server-authoritative behavior.** | A §13.3, §17.4 |
| 5 | Missile deadline computed at GO | **Matched.** Remaining time is recomputed at release and subtracts queued time. | A §10.3, §19.6 |
| 6 | Fixed release-identifier priority | **Matched.** Earliest event across all four identifiers wins. | A §10.2 |
| 7 | Missing target homes to zero | **Matched.** Missing source/target destroys the missile. | A §10.4, §19.6 |
| 8 | Miss impact suppressed; Dodge/Block react | **Preserved exact Benilla behavior.** | A §10.5, §17.7, §20.4 |
| 9 | No in-flight sound | **Matched.** Launch/arrival-owned independent loops added. Collision/ballistics remain deliberately absent. | A §10.8, §12 |

### 16.4 B §19.4 — aura behavior

| # | B boundary | Disposition after this pass | A reference |
|---:|---|---|---|
| 1 | Helpful/harmful is slot number | **Preserved exact Benilla behavior.** | A §15.1-§15.2 |
| 2 | Stack change reports REMOVE+STACK | **Matched.** A same-spell stack change is now only `STACK`. | A §15.2 |
| 3 | Slot timer can attach to replacement aura | **Matched.** Spell binding plus one-second receipt freshness added. | A §15.3 |
| 4 | Timer reaching zero does not remove aura | **Preserved exact server-authoritative behavior.** | A §15.3-§15.4 |
| 5 | Failed state spawn marked active | **Matched.** Key arms only after successful effect spawn; failure retries. | A §15.5 |
| 6 | Missing art hidden; UI metadata limited | **Partial.** Missing metadata/art fails open, player insertion order and tracking filtering added. Tooltip/caster/dispel coloring/expiration animation remain. | A §15.2-§15.3 |
| 7 | Tracking cancel bypasses cancelability | **Matched.** All UI cancellation uses flag bit `0x01`. | A §15.2, §15.4 |

### 16.5 B §19.5 — effect assets and animation

| # | B boundary | Disposition after this pass | A reference |
|---:|---|---|---|
| 1 | Failed model/path permanently cached | **Matched.** Failure expires for retry after ten seconds. | A §5.1, §5.7, §19.8 |
| 2 | Animator/skin array shared by asset path | **Remaining architectural difference.** Current consumers evaluate from explicit instance age before use, but storage is still shared. | A §5.1-§5.3 |
| 3 | Effect clip is missile 144 or first sequence | **Preserved Benilla behavior.** | A §1.6, §5.3, §10.6 |
| 4 | Body stage animation exact/may be absent | **Already matched and retained.** No fabricated generic impact animation. | A §4.5, §11 |
| 5 | Player combat action can override active spell hold | **Remaining.** Full Benilla layered driver/secondary wound blend is not ported. | A §11.3-§11.5 |
| 6 | Newer M2/external skin/anim unsupported | **Remaining asset-format limitation.** | A §1, §5 |
| 7 | Hermite/Bezier interpolation approximated | **Remaining.** New particle spline sampling does not change generic M2 track interpolation. | A §1.4, §2.9 |
| 8 | Static/looping policy differences | **Remaining.** | A §1.4-§1.6, §11.6 |
| 9 | No spell-stage/general M2 audio | **Partial.** Kit/channel/missile stage audio added; strike and general M2 event sounds remain. | A §12 |

### 16.6 B §19.6 — mesh, material and ground

| # | B boundary | Disposition after this pass | A reference |
|---:|---|---|---|
| 1 | One texture unit; no transforms/combiners/layers/priority | **Remaining.** | A §5.5-§5.6 |
| 2 | Fixed lighting/fog | **Remaining.** | A §5.5 |
| 3 | No transparent depth sorting | **Remaining.** | A §5.5, §8 |
| 4 | Custom ammo texture fallback-only | **Retained.** It remains the last model fallback branch. | A §4.11, §10.7 |
| 5 | Narrow four-vertex ground recognition | **Remaining.** | A §1.8, §9 |
| 6 | Height-snap ground projection, not triangle clipping | **Remaining major visual gap.** Particle birth ground snap was added, but effect decals still use the existing surface approximation. | A §9, Appendix C |
| 7 | Ground eligibility requested attachment `0x13` | **Already matched.** This is the authored ground-anchor route. | A §5.2, §9 |

### 16.7 B §19.7 — particles

| # | B boundary | Disposition after this pass | A reference |
|---:|---|---|---|
| 1 | 250-yard cutoff and 1024 cap | **Preserved Benilla constants.** | A §7.8 |
| 2 | Spline uses plane kernel | **Matched.** Cubic Bezier/arc-length spline birth implemented. | A §7.3, §20.4 |
| 3 | Head-only; no tails/XY/spin/tumble/model | **Partial.** Head/tail/both, XY and spin added; tumble and model particles remain. | A §8.1-§8.5 |
| 4 | No inherit/follow/wind/children/collision/ground/soft | **Partial.** Inherit, follow and ground snap added; wind, children, collision and soft particles remain. | A §7.6-§7.8 |
| 5 | Path text decides attachment | **Matched.** Explicit ownership flag replaces the heuristic. | A §7.1-§7.2 |
| 6 | Clamped dt/no long-frame compensation | **Preserved Benilla freeze/no-catch-up behavior.** | A §7.4-§7.5, §20.4 |
| 7 | Textures did not repeat | **Matched.** Repeat sampling now agrees with wrapped cell ramps. | A §8.5 |
| 8 | No lighting/fog/gamma/transparent sort | **Remaining.** | A §8.6-§8.7 |

### 16.8 B §19.8 — ribbons

| # | B boundary | Disposition after this pass | A reference |
|---:|---|---|---|
| 1 | At most one new edge/frame | **Preserved Benilla behavior.** | A §8.8, §20.4 |
| 2 | Incremental `2gdt` sag | **Preserved Benilla behavior.** | A §8.8 |
| 3 | Current color/alpha uniform on old edges | **Preserved Benilla behavior.** | A §8.8 |
| 4 | TextureSlot/atlas ignored | **Matched.** Live slot and cell-relative UV implemented. | A §2.7, §8.8 |
| 5 | Blend 5/6 fold to alpha | **Preserved named Benilla approximation.** | A §8.6, §20.4 |
| 6 | No depth sorting/spline smoothing | **Unchanged.** No unsupported smoothing was invented; global transparent sorting remains a renderer gap. | A §8.7-§8.8 |

### 16.9 B §19.9 — outcomes and UI

| # | B boundary | Disposition after this pass | A reference |
|---:|---|---|---|
| 1 | Combat logs and visual arrivals independent | **Preserved exact architecture.** | A §3.0, §17 |
| 2 | Logs do not create body wounds | **Preserved.** Authored visual impact owns spell body animation. | A §11.5, §17.3 |
| 3 | Outgoing heals/energizes and most other-player rows lack world numbers | **Remaining UI gap.** | A §17.6, §17.10 |
| 4 | Feedback cap drops excess display rows | **Unchanged and explicitly retained.** Recording remains separate from display capacity. | A §17.10 |
| 5 | Cast-error table partial | **Matched.** Complete build-5875 names/text added. | A §16.2 |
| 6 | Pet cooldown/power/usability tails absent | **Remaining.** | A §13.2, §16.6 |

## 17. Remaining work required for closer visual parity

The largest remaining visual gap is not a spell-type router problem; it is the ground/material/rendering
layer:

1. Project ground decals by clipping/re-emitting terrain and WMO triangles as described in A §9, including
   the full 360-degree winding, slab rejection, hide-without-surface rule, depth bias, and render order.
2. Implement material layers, texture transforms, combiners, priority planes, animated per-instance
   color/alpha, and transparent ordering from A §5.
3. Add model-particle meshes, tumble, recursive child emitters, wind, and the remaining particle render
   policies from A §7/§8.
4. Expose body/effect animation event cursors so strike `$TRD`, general `$SND`, and related model events can
   drive sound.
5. Replace or extend the sound backend for audible non-Windows playback and pitch/category behavior.
6. Complete Benilla's remaining specialized outbound item/skinning flows and the full modeled usability
   predicate.
7. Add live authoritative cooldown-list overwrite/event-release handling and pet cooldown/usability tails.
8. Reproduce the full layered body driver so combat actions and wound overlays cannot incorrectly evict
   cast holds.

These items are deliberately separated from server-owned gameplay. None requires writing per-spell damage,
healing, AoE, aura, summon, or periodic logic into the client.

## 18. Files changed by this parity pass

This list identifies the spell-parity work, not unrelated pre-existing edits in the dirty worktree.

| File | Responsibility added or changed |
|---|---|
| `MSUIClient/Net/SpellPackets.cs` | Complete START/GO caster, ammo and target facts. |
| `MSUIClient/Program.SpellEvents.cs` | New ordered presentation-event boundary and drain. |
| `MSUIClient/Program.Net.cs` | Enqueue-only spell packet handling, ordered drain, spell sound ownership, shader lookup. |
| `MSUIClient/Net/PlayerActions.cs` | Independent spell/category cooldown clocks and longest-active query. |
| `MSUIClient/Formats/SpellCatalog.cs` | Spell category retention plus reagent/tool access used by admission. |
| `MSUIClient/Program.ActionBars.cs` | Learned/profession/reagent/tool/focus admission and category-aware cooldown UI. |
| `MSUIClient/Net/CastTargetLaw.cs` | New explicit local refusal reasons. |
| `MSUIClient/Program.DevTools.SpellSweep.cs` | Category-aware diagnostic cooldown verdicts. |
| `MSUIClient/Net/WorldSession.cs` | Specialized `0x4800` game-object cast body. |
| `MSUIClient/Net/NetworkClient.cs` | Game-object cast session surface. |
| `MSUIClient/Program.GameObjects.cs` | Learned OPEN_LOCK/Lock.dbc matching and specialized send. |
| `MSUIClient/Program.Professions.cs` | Equipped inventory included in tool/reagent carried count. |
| `MSUIClient/Program.Casting.cs` | Stage router, cooldown edge, sound triggers, homing handoff, aura/channel watchers, ground query. |
| `MSUIClient/World/Units/SpellEffectSource.cs` | Spawn success count, release/launch/homing lifetime, attachment flag, callbacks, asset retry. |
| `MSUIClient/Formats/M2Reader.cs` | Extended particle/ribbon fields, atlas sampling and spline sampling. |
| `MSUIClient/World/Spells/SpellParticleSystem.cs` | Benilla-specific birth/integration/follow/inherit/ground and head/tail/XY/spin render path. |
| `MSUIClient/Shaders/spell_particle.vert` | Per-particle head/tail axes and atlas-ready instance attributes. |
| `MSUIClient/World/Units/SpellRibbonRenderer.cs` | TextureSlot and atlas-cell UV behavior. |
| `MSUIClient/Program.DevTools.Auras.cs` | Timer freshness, insertion order, filtering, cancellation and honest stack transitions. |
| `MSUIClient/Program.UnitFrames.cs` | Ordered/filtered aura feeds with fallback art and matching timer display. |
| `MSUIClient/Program.Minimap.cs` | Tracking aura selection and standard cancelability. |
| `MSUIClient/Formats/SoundEntriesCatalog.cs` | New SoundEntries data catalog. |
| `MSUIClient/Formats/SpellVisualCatalog.cs` | StrikeSound field retention. |
| `MSUIClient/World/Spells/SpellSoundSystem.cs` | Weighted variants, tracked loops, positional attenuation and Windows playback. |
| `MSUIClient/Net/SpellCastResultNames.cs` | Complete build-5875 cast-result name/text table. |
| `MSUIClient/Program.cs` | Spell particle ground callback use and spell sound disposal. |

## 19. Verification performed

The following checks were run after implementation:

| Check | Result |
|---|---|
| `dotnet build MSUIClient/MSUIClient.csproj -c Debug --no-restore` | Passed, 0 errors. One pre-existing CA2014 warning remains at `Engine/UI/GlueAdditive.cs:141`. |
| `dotnet build MSUIClient.sln -c Debug --no-restore` | Passed, 0 errors. |
| `dotnet build MSUIClient/MSUIClient.csproj -c Release --no-restore` | Passed, 0 errors. Same pre-existing CA2014 warning. |
| `dotnet build tools/spell-visual-diagnose/spell-visual-diagnose.csproj -c Debug --no-restore` | Passed, 0 errors; the spell-visual catalog/parser diagnostic still compiles against the expanded model data. |
| Static event/lifetime audit | Confirmed packet enqueue/drain order, separate cooldown clocks, launch-time missile clock, target-loss removal, explicit attachment, aura timer spell binding, state-spawn retry, particle/ribbon atlas paths and sound trigger ownership. |

No live 1.12 server capture, GPU frame comparison, or audible archive fixture was available in this run.
Therefore compile/static verification is complete, while visual/audio acceptance still needs the fixture
matrix in A §19.9. The high-value first live captures are Fireball 133, an instant self buff, Arcane
Explosion 1449, Frost Nova 122, a multi-target projectile, tracking, a channel, and an open-lock object.

## 20. Final parity statement

MSUI now has Benilla-like separation across wire facts, ordered cast events, stage lifetimes, cooldown
families, missiles, aura UI/state, particles, ribbons, open-lock targeting, failure text, and spell sound.
Those changes apply data-driven behavior to self, unit, buff, debuff, instant, timed, speed-zero AoE,
multi-target, projectile, miss, channel, tracking, ranged, next-swing, profession, and game-object spell
classes without adding per-spell code.

It is not yet a perfect visual clone. Ground triangle projection, advanced materials, model-particle/child
emitter behavior, full animation-event audio, complete body-animation layering, some specialized outbound
paths, live cooldown overwrite, and pet tails remain. The exhaustive ledger above states each boundary so
none of them can be mistaken for completed behavior in a later comparison.
