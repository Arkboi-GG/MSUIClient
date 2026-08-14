# SYSTEM_MOUNTS — riding, and why it is the whole of "vehicles" in 1.12

Draft 1 — 2026-08-13. Built and verified offline the same day.

---

## 0. The one-paragraph version

`UNIT_FIELD_MOUNTDISPLAYID` holds a `CreatureDisplayInfo` id. When it is non-zero the
client draws that creature model at the unit's ground position and facing, animated from
the unit's own travel speed, and then draws **the unit itself parented to the mount's
attachment 0** — the saddle — playing `AnimationData.dbc` 91 "Mount". That is the entire
system. There is no seat table, no passenger list, no vehicle-relative movement: those
arrive in 3.0.

---

## 1. Why this is also the answer to "can you drive the Thousand Needles carts"

Yes, and they are not a special case. The Mirage Raceway racers are ordinary mounts:

| display | model | creature |
|---|---|---|
| 10318 | `Creature\GoblinRocketCar\GoblinRocketCar.m2` | 4251 Goblin Racer |
| 2490 | `Creature\GnomeRocketCar\GnomeRocketCar.m2` | 4252 Gnome Racer |
| 15381 | `Creature\SteamTonk\SteamTonk.m2` | 15328 Steam Tonk |

Both rocket cars carry **the same attachment set as `Creature\Horse`** — 0 (seat), 1/2
(rider's hands), 15–22 and 34 (effect points) — plus Stand/Walk/Run sequences. Blizzard
authored them rideable and then scripted NPCs onto them; nothing on the wire distinguishes
one from a Riding Horse. So "a drivable cart" costs a display id, not a vehicle system.

The Steam Tonk is the interesting exception: **no attachment 0**, because it is the one
thing in vanilla you drive by possession (the Winter Veil controller charms it) rather than
by sitting on it. See §5 for what the renderer does with a mount that has no saddle.

---

## 2. The transform

The steed draws first, because the rider's instance matrix *is* its saddle:

```
mountWorld = Scale(displayScale x modelScale) · RotY(yaw + headingOffset) · Basis · T(position)
seat       = T(attachment0.Position) · Skin[attachment0.BoneIndex] · mountWorld
riderWorld = Scale(riderScale) · seat
```

The middle line is `AttachedItemRenderer`'s pauldron chain one level up: **the character is
the attached model**. Because the seat comes out of the mount's *evaluated* skin, the rider
follows every bob of the gait for free — no second bone chain, no sync problem.

**Scale is applied once, on the steed.** The mount takes `ScaleMultiplier` (it is the thing
standing on the ground); the rider then contributes only its own `UNIT_FIELD_SCALE_X`
relative to the seat. Applying the multiplier at both ends squares it.

**The rider must not re-apply `Basis`, its heading, or its ZOffset.** All three already live
in `mountWorld`, and the seat is expressed in the mount's model space. This is the one thing
that is easy to get wrong and looks like "the body is rotated off the horse".

---

## 3. Where it lives

| file | what |
|---|---|
| `World/Units/CreatureRenderer.Mounts.cs` | the whole mount pass: load, animate, draw, saddle |
| `World/Units/CreatureRenderer.cs` | the unit loop calls `TryDrawMount` before it places each rider |
| `World/Units/CharacterRenderer.cs` | `MountSeat` (nullable), which short-circuits `BuildTransform` and `ChooseClip` |
| `Program.cs` `DrawSelfMount` | the local player's steed, drawn in the character pass |
| `Net/Entities.cs` | `WorldEntity.MountDisplayId` |

**Why inside `CreatureRenderer` and not its own renderer:** a mount is a plain creature
model. The model cache, appearance cache, async load path, animator and draw loop are all
already here and already budgeted. A separate renderer would have duplicated every one of
them to draw a horse.

**Two paths, deliberately.** Streamed units (NPCs and remote players) ride inside
`CreatureRenderer.Render`'s loop. The local player is drawn by `CharacterRenderer` from the
client-predicted position, in an earlier pass, so its steed is drawn by `TryDrawSelfMount`
before that pass and hands the seat over. A probe that only checks one signs off on half a
system — which is why `MSUI_MOUNT_PROBE` checks both.

**NAMING TRAP:** `AttachedItemRenderer.Mount` is a piece of *gear* hanging off an attachment
point (a helm, a pauldron). It has nothing to do with a steed. `CreatureRenderer.Mounts.cs`
is the only place the word means the thing you ride.

---

## 4. Animation

* The steed picks Stand / Walk / Run from the **rider's** ground speed — it has no spline of
  its own — with the stride rate matched to the clip's authored `MoveSpeed`, exactly as
  `SelectClip` does for creatures.
* The rider plays **91 "Mount"**, and it outranks locomotion selection. Vanilla dismounts
  you before a cast or a swing could want the frames, so this is not a lossy choice. Combat
  reactions still play (they are upper-body and read fine in the saddle).
* Mounted zeroes `LowerBodyYaw` and the torso counter-yaw. The seated pose is authored
  whole; twisting its hips against a saddle it is parented to only breaks it.

---

## 5. Edge cases the code makes explicit

* **Mount still streaming** — `TryDrawMount` returns false and the rider draws on the ground
  as it always did. A steed pops in; it never blocks its rider.
* **No attachment 0** (the Steam Tonk) — the rider is seated at the model origin. That is
  the honest answer: nobody authored a place to sit.
* **Dismount** — a `MountDisplayId` of 0 retires the seat and the steed's animation clock in
  the same frame (`ForgetMount`). Stale seats are also pruned after a second so a rider that
  left the world cannot keep supplying a saddle to the nameplate and ring queries, which run
  outside the loop.
* **Nameplate and selection ring** — the ring is sized from the steed's footprint (it is what
  is standing on the grass) and the name is measured from `seat height + rider height`, not
  from the rider's own feet, which are no longer on the ground.
* **Ground shadow** — the steed's, for the same reason.

---

## 6. Verified

`MSUI_MOUNT_PROBE="mount=<display>;rider=<display>"` boots the offline creator world, seats
both a spawned NPC and the player on the same steed, prints the seats, screenshots, then
dismounts both and asserts the saddles are gone. Output lands in `dumps/gameplay-mount-probe-*`.

2026-08-13, `mount=2404` (Riding Horse) `rider=240` (Orc Male Warrior):

```
[mount-probe] mounts drawn this frame: 2
[mount-probe] player seat ok: (-8957.48, -135.78, 84.72) vs feet (-8957.68, -135.85, 82.86) scale=(1.000, 1.000, 1.000)
[mount-probe] npc Rider 240 seat ok: (-8950.29, -133.43, 85.44) scale=(1.000, 1.000, 1.000)
[mount-probe] dismount ok: player seat cleared
[mount-probe] dismount ok: npc Rider 240 seat cleared
```

The player's seat sits `(0.20, 0.07, +1.86)` from its feet, which is `Horse.m2`'s authored
attachment 0 `(0.213, 0.0, 1.866)` to two decimals. Screenshots confirm both riders astride,
hands forward, facing with the horse.

The creator sandbox has the same controls interactively: **Target → Advanced (raw display
id) → Mount display / Ride it**. Non-zero seats every new spawn; "Ride it" seats you too.

---

## 7. Known: the rocket cars sit 3.16 yards off their own origin

`GoblinRocketCar.m2`'s root bone (1) is authored with a **constant** translation of
`(-3.16, 0.0, 0.89)` across all three of its sequences — verified by reading the bone's
translation track ranges out of the archive: 87 keys for Stand, span `(0, 0, 0)`. It is a
baked offset, not root motion, and it moves the mesh *and* the saddle together, so the rider
sits correctly in a car that is drawn three yards from the unit's logical position.

This is the model's own data — the reference client renders it the same way — and it is why
the raceway's scripted spawn points look right while a player mounted on 10318 appears
displaced. `Horse.m2`'s root translation is ~0 for Stand, which is why it lands exactly.

Do not "fix" this in the renderer. Compensating would be a guess applied to every other
mount. If the cars are ever wanted as player mounts that track the unit exactly, the honest
fix is a per-display offset table with these two entries in it, and it should say so.

---

## 8. The toolkit (`Program.Mount.Toolkit.cs`)

A DevTools window, open from the creator sandbox (**Target → Advanced → Mount toolkit**) or
in-world (**Server panel → Mount toolkit**). Everything it changes is persisted under
`Mounts` in `settings.json` and applied live.

**Ride anything.** A display id, a *Ride it* toggle and four presets (Riding Horse, both
rocket cars, Steam Tonk). Client-side: nothing is sent, the server is not told, and it
outranks `UNIT_FIELD_MOUNTDISPLAYID` while it is on. This is what makes every mount in the
archives reachable for tuning without a GM command or a real mount item.

**Look, per display id.** Seat forward/right/up, rider yaw/pitch/roll, rider scale, steed
forward/right/up, steed scale. Offsets are in the steed's model space in yards — the same
space attachment 0 is authored in — so a nudge composes with the artist's saddle instead of
fighting it. Saved per display because a horse's saddle and a rocket car's have nothing to
say to each other.

**Cancel baked offset** measures the model's root translation from its own idle pose and
writes the negation into the steed offset. It is the honest form of the §7 problem: a
per-display correction the user can see and edit, rather than a constant buried in the
renderer. On display 10318 it reads `(-3.16, 0.89, 0.00)` and puts the car back over its rider.

**Feel, global.** Speed, turn and jump multipliers, plus a gait-rate multiplier for the
steed's own animation. They multiply values the controller and animator already use, so 1.0
is exactly stock. **They are client prediction**: offline nothing argues, on a live realm the
server still believes its own speed and will correct you. The panel says so on screen.

---

## 9. The cart kit (`Program.Mount.Kit.cs`)

What the thing you are riding can *do*. Up to six slots per cart, persisted with that cart's
tuning, and every one of them is two independent fields:

* **A spell**, for the look only — its authored 1.12 visual, played through the same
  `PresentSpellEffect` path the networked casts use. Cone of Cold looks like Cone of Cold.
* **An effect**, for the behaviour: `Slow` (everything in radius, by a factor, for a
  duration), `Dash` (throw the cart forward along its facing — Blink as a cart move), or
  `None` (visual only).

They are deliberately not coupled. Which spell dresses which effect is the tuning pass this
was built to make cheap, and neither field is load-bearing for the other.

**Nothing deals damage.** The kit is control, by design.

**Charges are the resource**, and where a spent charge comes back from is a policy:

| `Mounts.Recharge` | behaviour |
|---|---|
| `Time` | a per-slot timer. The default, so the kit is playable before the track exists |
| `Token` | nothing regenerates on its own — only `NoteMountKitToken(slot)` returns a charge |

`NoteMountKitToken` is the seam for the pickup on the track. It is already wired to the
toolkit's **Drop a token** button, so the whole loop — spend, run dry, collect, fire again —
can be felt now and re-pointed at a real pickup later without touching the kit.

**Firing** is `1..6` while mounted, but only for slots that hold a spell: anything the cart
does not carry falls straight through to the ordinary action bar, so an unconfigured mount
changes nothing about how the client plays. There is also a per-slot **Fire** button.

**Install frost kit** builds Cone of Cold / Blizzard / Blink by NAME out of this machine's own
catalog, so the ids are real rather than hard-coded and drifting.

**Slows live in one table**, `MountKitSlowFactor(guid)`. Today it is filled by a local cast and
read by the local controller and the panel; a real debuff would fill the same table from the
server. That is the whole extent of the coupling, on purpose.

**Everything here is client-side.** No cast is sent, no aura is real. That is the right shape
while the rules are being invented: it makes the cart playable today, and the two places a
server would eventually own — the cast and the aura — are single calls.

Verified 2026-08-13, `MSUI_MOUNT_PROBE="mount=2404;rider=240;kit=1"`:

```
[mount-probe] kit installed: 3 slot(s)
[mount-kit] Cone of Cold: slowed 1 to 50% for 4s  (2/3)
[mount-kit] Blizzard: slowed 1 to 65% for 4s  (2/3)
[mount-kit] Blink: dashed 20 yd  (1/2)
[mount-probe] dash moved the cart 20.1 yd
```

---

## 10. Not done

* The **token pickup** itself: `NoteMountKitToken` exists and is called by a button. What
  drops a token on a track, and what collecting one looks like, is undesigned.
* The cart bar is **plain ImGui**, not Blizzard art. It is readable while driving, which was
  the requirement; the HUD-art version belongs with the rest of the gameplay UI.
* Slows are **tracked but only bite the local player.** A server-driven unit's movement cannot
  be slowed from here; `MountKitSlowFactor` is the seam a real debuff would fill.
* Mounted **swimming/flying** poses — vanilla dismounts on entering deep water, so this only
  matters if the server allows it.
* **Mount special** (animation 94), the idle flourish, has no trigger yet.
* Mounted **spell casts**: the rider holds 91 through a cast because vanilla dismounts first.
  If the server ever allows a mounted cast, `ChooseClip`'s mounted branch is the place.
* **Portraits** of a mounted unit show the rider only, which matches vanilla; untested against
  a real mounted target.
