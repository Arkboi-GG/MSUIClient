# Emotes / stand-state / one-shot timing — session handoff

Date: 2026-08-16. Branch: `main`, uncommitted (never got branched per the usual
`cam/<feature>` convention — do that before committing any of this). Scope:
wiring emote animations (SMSG_EMOTE + UNIT_NPC_EMOTESTATE), sit/kneel/sleep
stand-state, and the timing of when a one-shot animation lets go of the base
gait. Read this before touching any of the files below — several of the
"obvious" fixes here already turned out to have a real client source behind
them, not a guess, and re-deriving that from scratch would waste time.

**Status when this session ended: Cam reported it "still isn't working
right,"** diagnosed as §1 below, and the fix for §1 was then implemented and
**built clean (0 errors) but never live-tested** before the session ended
again. **Start next session by live-testing §1's test plan** - if it's still
wrong, the fix's reasoning (not just its code) needs re-checking, since this
is now the second attempt at the same rule.

---

## 1. FIXED (untested) — the movement-break rule was a level check, needed to be edge-triggered

This was almost certainly why "still isn't working right" after the first
movement-break pass. It has since been fixed in code but **not yet confirmed
live** - verify this first, before anything else in this doc.

**What was wrong:** [`CharacterRenderer.cs`](../../MSUIClient/World/Units/CharacterRenderer.cs)
`Update()` (and the mirrored remote-unit version in `CreatureRenderer.cs`) had:

```csharp
if (_combatAction is not null && state.Moving)
    _combatAction = null;
```

A **continuous** condition, checked every `Update()` call. The Benilla trace
it's sourced from (`driver.rs:847-877`) says a one-shot ends on
"`oneshot_finished(id)` **or a movement-flag change**" - a *change*, i.e.
edge-triggered (movement transitioning from off to on), not "moving is
currently true." Since `TriggerOneShot`/`TriggerCombatSwing`/`HandleEmoteReceive`
etc. set `_combatAction` and the **very next** `Update()` call ran the check
above *before* `ChooseClip` ever got to return it, any one-shot triggered
while the player was **already moving** (the ordinary case - waving while
walking, swinging mid-chase) was nulled out before it was ever drawn at all.

**What changed:** now edge-triggered on the `state.Moving` transition, not its
level:

```csharp
if (_combatAction is not null && state.Moving != _wasMovingLastFrame)
    _combatAction = null;
_wasMovingLastFrame = state.Moving;
```

(`_wasMovingLastFrame` is a new field on `CharacterRenderer`.) An action armed
while stationary still gets cut short the instant movement starts; one armed
while already moving now plays out its full authored duration instead of
never showing at all - a deliberate compromise (the real client would mask it
to the upper body and keep playing regardless of when it started; tabled, see
§4) but at least not a regression from pre-session behavior anymore.

**Mirrored for remote units** in `CreatureRenderer.cs`: a new per-guid
`_wasMovingByGuid` dict (pruned in `PruneAnimState`, alongside `_animTime`
etc.) drives the same edge check, pruning `_combatActions[guid]` only on a
movement-flag change rather than gating the render branch on a continuous
`!remoteMoving`.

**Deliberately left continuous** (NOT edge-triggered, and this is correct, not
a leftover bug): the `EmoteState`/seated-pose `!state.Moving` guards in
`ChooseClip`, and the equivalent `!remoteMoving` guards in `CreatureRenderer`.
Dance and Sit/Kneel/Sleep are real vanilla *states* that cannot coexist with
movement at all (confirmed by Cam's own account of standing up the instant
movement starts) - unlike an ordinary one-shot, which the real client masks
and keeps playing. If live-testing finds these need to become edge-triggered
too, that contradicts this session's reasoning and is worth re-deriving from
scratch rather than just copying the `_combatAction` pattern over.

**Test plan (do this first):** wave while already running - should now
actually play (overriding the run pose is still correct/period-accurate, see
§4 - the point is only confirming it draws *at all*, which was completely
broken before this fix). Trigger a melee swing while running toward a mob.
Start standing still, wave, then start moving mid-wave - should cut the wave
short immediately (this case was already working before this fix and must
still work after it). Have someone else's client emote while already moving
and confirm you see it play. If any of these still fail, the bug is not what
this section describes - stop and re-diagnose rather than patching further on
top of this reasoning.

---

## 2. Confirmed working (Cam-verified, before §1's SECOND fix - the edge-trigger one - landed)

- Text emotes' chat lines (pre-existing, untouched this session).
- Emote **animations** via SMSG_EMOTE for ordinary one-shots (wave, etc.) -
  confirmed after the `TriggerOneShot` bake-on-demand fix
  (`CharacterRenderer.cs` `TriggerOneShot`, `bakeOnDemand: true`).
- `/dance` - confirmed working after wiring `UNIT_NPC_EMOTESTATE` (field 148),
  which is what real `/dance` actually rides (SMSG_EMOTE never fires for it -
  see §5).
- `/sit`, `/kneel` - confirmed working (pose + toggle).
- Loot kneel (pre-existing, rides the same `TriggerOneShot` path, unaffected).

These were all confirmed BEFORE the movement-break work (§1) existed at all,
so none of them exercise that code path directly - they should be unaffected
by §1's fix, but re-confirming costs nothing and rules out any interaction
(e.g. `_wasMovingLastFrame` state leaking between an emote and a later /sit).

**Untested / unknown as of session end** (built but never confirmed live):
§1's fix itself (highest priority - see §1's test plan), `/sleep`, the
seated-pose Down→Loop→Up transition edge cases (switching Sit→Kneel directly
without passing through Stand; standing up then re-sitting before the Up clip
finishes), the movement gate ("You cannot do this while moving.") for all
five commands, the jump/movement auto-stand correction, and remote units
(other players) showing any of sit/kneel/dance/emotes at all - none of this
was visually confirmed before the session ended, only compiled.

---

## 3. Eat/drink seated animation - FIXED (was misdiagnosed here as server-side)

**This section's original conclusion was WRONG and has been corrected.** It
claimed the eat/drink stand-state was server-side and "not fixable in this
repo," reasoning only from the SEND side (`SendStandStateChange` is only called
by the slash commands). It never watched the actual fields arrive. It was a
real client bug and is now fixed.

**Empirical wire capture (2026-08-25, temporary `[diag:posefield]`/`[diag:emote]`
logging on the local player, since removed):**
- The server sends `UNIT_FIELD_BYTES_1` StandState = **Sit (1) for BOTH food
  (spell 434) and drink (spell 431)** - the auto-sit asymmetry Cam originally
  reported did not exist; both sit. On closer look the real symptom was that
  BOTH flickered: seated between animation loops, standing during them.
- `UNIT_NPC_EMOTESTATE` is **never set** for either (stays 0) - so this is not
  the EmoteState/Dance path.
- The consume animation arrives as **periodic `SMSG_EMOTE` one-shots**, emoteId
  **7 (ONESHOT_EAT → AnimationData 61) for BOTH** food and drink. Vanilla has
  no separate DRINK body animation (confirmed against `dumps/Emotes.dbc`); food
  and drink share the eat animation and differ only in the held item model.

**The bug:** `SMSG_EMOTE` → `ApplyEmote` → `_character.TriggerOneShot` sets
`_combatAction`, which `ChooseClip` returned **first**, over a standing pose,
before the `StandState`→sit branch ever ran. Each consume tick played a
full-body standing eat over the seated pose; between ticks the sit showed
through - the flicker.

**The fix (this repo, `CharacterRenderer` + `M2Animator`):** the SEATED half of
the Benilla `route_oneshot` committed_lower rule that ChooseClip's big "KNOWN
WRONG" comment already documented. While the server holds a ground stand-state
(sit/sleep/kneel, `SeatedMaskState`), `_combatAction` is no longer handed back
as the whole body; the seated pose is the base and the emote is layered onto the
**SpineLow (`TorsoBone`) subtree** as an upper-body mask via the existing
`EvaluateWithArmOverlays` machinery (new `torsoOverlay` param), on its own clock
(`_actionOverlayTime`). Legs stay seated, upper body eats/drinks. Cam
verified live (2026-08-25). See §4 - the MOVING/turning/swimming half of the
same rule is still tabled.

**Remote players (mirror) - DONE and verified (2026-08-25).** The same torso mask
is now wired in `CreatureRenderer` (per-guid: resolve+expire the seated action,
render it via `EvaluateWithArmOverlays`'s `torsoOverlay`, gated on
`SeatedLoopAnimId(StandState) != 0 && !remoteMoving`). Verified live against a
second character eating in view. One extra remote-only fix was needed: the server
**double-fires** the eat emote ~0.1-0.2s apart (confirmed on the wire), which
restarted the animation from frame 0 each tick; `CreatureRenderer.TriggerOneShot`
now preserves the existing `StartedAt` on a same-animation re-trigger so the clip
stays continuous (the local player was already immune - `Resolve` hands back a
cached clip and its overlay clock resets only on clip identity). The chosen feel
is to KEEP the ~5s server bite rhythm (settle between bites), only killing the
double-fire restart - not to force a continuous loop.

---

## 3b. Sit -> run transition (stand-up while moving) - FIXED (2026-08-26)

Cam: walking out of a sit played the full stand-up before the run - a ~2s stall.
The real 1.12 client (watched live on the reference client, char "Testmage") does
NOT play the stand-up clip when you move out of a sit: it blends the seated pose
**straight into the gait**, "very clipped, quick but smooth." A stationary `/stand`
still plays the full deliberate rise.

Two dead ends first (both rejected by Cam): playing the authored `SitGroundUp`
(anim 98) to completion (~2s stall), then **compressing** it to ~0.2-0.45s - which
looked like the character sliding forward in the seated pose and then a sped-up
full stand-up. Compressing the stand-up clip is simply the wrong model.

**The fix (`CharacterRenderer.ChooseClip` + `SwitchClip`):** while `state.Moving`,
the seated Up bracket is **not armed at all** (and an in-progress stationary
stand-up is dropped); ChooseClip falls straight through to locomotion, and a
one-shot `_forceNextBlendSeconds = SeatedRunBlendSeconds` (0.18s, tunable) makes
`SwitchClip` cross-fade the seated pose directly into the run at a fixed smooth
length regardless of the run clip's own authored blendTime. Cam: indistinguishable
from the official client.

NOTE for the tabled masking work (§4): trying to capture the reference animation
by screen-grabbing the foreground official client failed - GPU readback contends
with the game's own rendering, so grabs drop to ~0.8s each while the scene is in
motion (fast ~16ms only when static/backgrounded). Driving it with synthesized
scancodes (SetForegroundWindow + keybd_event, X=sit W=move) works fine; the
capture is the bottleneck. Reference behavior came from Cam's direct observation
instead.

---

## 4. Deliberately out of scope this session (tabled, not forgotten)

- **Masked upper-body overlay** (legs keep running while the torso plays a
  one-shot/cast, ~8:1 weight on the SpineLow subtree). Confirmed via Benilla
  (`driver.rs:631-644`, `:1137-1178`, `select.rs:813-825`) that this is the
  real client's actual behavior for ordinary emotes/swings/casts while
  moving - NOT the "let it float full-body" call this session initially and
  wrongly made (see the git history / earlier chat turns for the
  correction). Cam explicitly said: table this, separate session. When it
  happens: `M2Animator` already resolves a `TorsoBone` (key-bone 4, SpineLow,
  see `ResolveTwistBone`/`ResolveTorsoBone`) for the existing torso-yaw twist -
  that's most of the subtree-walk needed, it just doesn't blend a *second
  clip's* bone-local transforms into that subtree yet. The five stand-state
  commands (`/sit /kneel /stand /dance /sleep(+lie)`) are the one exception -
  they refuse outright with "You cannot do this while moving." (§4 below,
  already built) rather than ever being masked.
- **Chair-sitting** (`SitChair`/`SitLowChair`/`SitMediumChair`/`SitHighChair`,
  `UnitStandState` 2/4/5/6). Driven by GameObject-use, not the `/sit` command;
  needs per-chair seat-offset math this client doesn't have. `ChooseClip`'s
  seated-pose switch only handles Sit(1)/Sleep(3)/Kneel(8).
- Eat/drink seated masking is now DONE for the local player (§3); the
  remote-player (`CreatureRenderer`) mirror of it is still pending.

---

## 5. Key facts worth NOT re-deriving (all confirmed against real sources, not recalled)

- **`Emotes.dbc`'s `EmoteType`/`EmoteSpecProc` column decides SMSG_EMOTE vs
  UNIT_NPC_EMOTESTATE.** `Unit::HandleEmote` (vmangos/core,
  `src/game/Objects/Unit.cpp`): `EmoteType == 0` → `HandleEmoteCommand`
  (SMSG_EMOTE, 0x0103, `u32 emoteId + u64 unitGuid`). `EmoteType` 1 or 2 →
  `HandleEmoteState` (`SetUInt32Value(UNIT_NPC_EMOTESTATE, emoteId)`, a plain
  object field, no packet). `/dance`'s `EmotesText.dbc` row (id 34) targets
  `Emotes.dbc` id **10** (`STATE_DANCE`, `EmoteType 2`) - read directly out of
  `dumps/EmotesText.dbc`'s own `EmoteID` column, not assumed.
- **`WorldSession::HandleTextEmoteOpcode`** (vmangos/core,
  `src/game/Handlers/ChatHandler.cpp`) explicitly `break`s (does nothing) for
  `EMOTE_STATE_SLEEP`/`SIT`/`KNEEL`/`EMOTE_ONESHOT_NONE` - the chat-text
  emote **never** carries the sit/kneel/sleep pose. That's
  `CMSG_STANDSTATECHANGE` (0x0101, `u32 UnitStandStateType`), handled by
  `WorldSession::HandleStandStateChangeOpcode` in `MiscHandler.cpp`, which
  just writes `UNIT_FIELD_BYTES_1` byte 0 via `Unit::SetStandState`.
- **Field indices**, cross-checked against vmangos/core's
  `UpdateFields_1_12_1.h` (`OBJECT_END + 0x84`/`0x8A` etc.) landing exactly on
  this project's own already-confirmed neighbors (137/139/144/147):
  `UNIT_FIELD_BYTES_1 = 138` (byte 0 = StandState), `UNIT_NPC_EMOTESTATE = 148`.
- **Real `AnimationData.dbc` ids** for the seated poses, read directly out of
  the client's own data (`dumps/AnimationData.dbc`, 208 rows, has a real name
  string column in this era): `SitGroundDown/SitGround/SitGroundUp` =
  96/97/98, `SleepDown/Sleep/SleepUp` = 99/100/101,
  `KneelStart/KneelLoop/KneelEnd` = 114/115/116. Dance = AnimID 69, already a
  looping M2 sequence with no separate Down/Up bracket in the real data.
- **`UnitStandState` enum** (vmangos/core `Unit.h`): Stand 0, Sit 1, SitChair
  2, Sleep 3, SitLowChair 4, SitMediumChair 5, SitHighChair 6, Dead 7, Kneel
  8, Custom 9.
- All of the above extraction used `tools/mpqpeek/mpqpeek.py cat
  'DBFilesClient\<name>.dbc' -o dumps/<name>.dbc` against
  `C:\VanillaWoWPrivate\WoW Vanilla\Data` (pass `--data` before the
  subcommand, not after) - same recipe as the pre-existing `EmotesText.dbc`
  dump. `dumps/emote-animation-table.cs.txt` has the full 78-row Emotes.dbc
  dump for reference. NOTE (2026-08-26): the hand-transcribed `EmoteAnimationLaw`
  table was DELETED - both the state-emote (Dance) path and the SMSG_EMOTE
  one-shot path now resolve Emotes.dbc id -> AnimationData id through the single
  live `EmoteCatalog` (reads the real DBC), so there is no static table to
  re-derive or drift. See §3b's closing note and §6.

---

## 6. Where everything is

| Concern | File |
|---|---|
| Emotes.dbc id → AnimationData id (+ EventSoundId), LIVE from the DBC | `MSUIClient/Formats/EmoteCatalog.cs` (Yaf's); resolver `GameLoop.ResolveEmoteAnim` wires it onto both renderers' `EmoteAnimResolver`. (The old static `EmoteAnimationLaw.cs` was deleted - see §5.) |
| SMSG_EMOTE dispatch + handler | `GameLoop/Scene/GameLoop.Net.cs` (`ApplyEmote` case), `GameLoop/Combat/GameLoop.Emotes.cs` (`ApplyEmote`) |
| `/sit /kneel /sleep /stand` send + toggle + movement gate | `GameLoop/Panels/GameLoop.Chat.cs` (`TrySubmitTextEmote`, `SubmitStandStateChange`, `MovementGatedCommands`, `StandStateCommands`, `IsMoving`) |
| `CMSG_STANDSTATECHANGE` opcode | `Net/Opcodes.cs` |
| `UNIT_FIELD_BYTES_1`/`UNIT_NPC_EMOTESTATE` field indices + accessors + `UnitStandState` enum | `Net/ObjectFields.cs` |
| Send plumbing | `Net/WorldSession.cs` (`SendStandStateChange`), `Net/NetworkClient.cs` (forwarder) |
| Local player: `TriggerOneShot` bake fix, Dance-loops-not-expires fix, seated Down/Loop/Up state machine, EmoteState branch, movement-break (§1's bug), auto-stand send hook | `World/Units/CharacterRenderer.cs` (`ChooseClip`, `Update`), `Program.cs` (`BuildUnitState`, the `_wasStandTriggerActiveLastFrame` block near `_moveForward`/`_moveStrafe`) |
| Remote units: same Dance/seated-pose/movement-break mirror | `World/Units/CreatureRenderer.cs` (main per-entity branch chain around line ~500, `TriggerEmoteAnimation`, `SeatedLoopAnimId`) |

---

## 7. Suggested next-session order

1. Run §1's test plan first, exactly as written there. If any case still
   fails, stop and re-diagnose rather than layering another patch on top -
   this is already the second attempt at this specific rule.
2. Once §1 is confirmed, quickly re-confirm §2's "confirmed working" list
   (cheap insurance, see §2's note on why).
3. Live-test everything in §2's "untested" list.
4. Only then: decide whether to start the masked-overlay work (§4) or keep
   polishing the interim behavior.
