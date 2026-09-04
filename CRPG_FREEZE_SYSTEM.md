# CRPG Tactical Freeze System

This document describes the localized Tactical Freeze system in plain language. Tactical Freeze is an explicit button inside Command View; Command View itself remains live and does not automatically freeze anything.

## Client behavior

1. Command View stays live as normal. It only freezes the world when the player presses the new **Freeze** button.

2. The client asks the Core to freeze; it does not pretend locally that a freeze succeeded. Lock membership, ownership, queues, releases, and results come from authoritative Core messages.

3. Frozen characters remain on the exact animation frame they were using, including:

   - Weapon raised
   - Mid-swing
   - Casting
   - Running
   - Standing idle
   - Mounted, emoting, or otherwise posed

4. The camera, menus, selection, inspection, and chat remain usable.

5. The player who started the freeze can queue up to five actions per commandable party or raid member:

   - Move somewhere
   - Attack a target
   - Cast a spell on a unit
   - Cast a spell at a ground location

6. Queues can be viewed, cleared, or have individual orders removed. Items are not queueable in the first wire version.

7. Other real players caught inside the freeze see that they are frozen, but cannot issue tactical orders or press Resume for a lock they did not create.

8. While frozen, the client prevents live actions from leaking through. This includes movement, attacks, spells, possession, pet commands, NPC services, trades, taxis, resurrection, party changes, and similar gameplay actions.

9. Previously prepared actions, such as walking toward an NPC or holding a delayed spell target, are cancelled so they cannot unexpectedly fire after the freeze ends.

10. Pressing Resume releases the physical freeze. Queued plans then execute in order, and ordinary live control returns after the queue has completely finished.

11. If multiple freezes overlap, the client combines their authoritative memberships. A character remains visually and physically frozen until every lock holding that character has been released.

## Core/server behavior

1. The server itself never pauses. Networking, chat, maps, other players, and the rest of the world keep running.

2. Only a real, connected player inside Command View can create a freeze. A socket-less bot session cannot initiate one.

3. The Core creates a fixed 100-yard, three-dimensional sphere centered on the body the player is currently controlling, not the camera and not necessarily the player's original body.

4. The sphere does not follow the player. Its center is fixed at the position where Freeze was pressed.

5. Every loaded, living unit inside is locked, including:

   - Players
   - Party companions
   - Pets
   - NPCs and enemies
   - Summons
   - Totems

6. A unit that enters the sphere later is immediately added to the freeze. Once added, it remains frozen until Resume; merely moving or being moved outside the radius cannot thaw it.

7. Frozen units stop advancing:

   - Movement and facing
   - AI decisions
   - Attacks and casts
   - Cooldowns and combat timers
   - Regeneration
   - Summon and totem lifetimes
   - Damage, healing, and resource effects

8. On thaw, important clocks are adjusted so a ten-second freeze does not consume ten seconds of cooldown time.

9. Only the real player who created a particular freeze may release it or edit its queues. Ownership is tied to that player's real socket identity, even when the freeze was centered on a possessed companion.

10. The Core independently validates every queued order. A modified or malicious client cannot command somebody else's character, queue an unsupported action, or exceed five actions per member.

11. Queued actions are stored and executed by the Core in first-in, first-out order for each actor. Unrelated actors may execute concurrently. If separate plans share an actor, that actor's plans are serialized in a deterministic order.

12. Overlapping freezes are reference-counted. If two spheres contain the same creature, releasing one sphere does not thaw it while the other still holds it.

13. If the owner leaves Command View, logs out, changes maps, or dies, the Core safely releases the active freeze.

14. The Core also rejects new gameplay intent that addresses a frozen subject, target, or physical service source. This prevents an unfrozen player just outside the boundary from mutating a frozen player, companion, creature, or NPC through another packet path.

15. Selection, inspection, chat, true read-only queries, cancellation, decline, cleanup, and ordinary guild or social metadata remain live. Party and raid membership changes are blocked because they affect command authority and tactical queue ownership.

## How the outside world behaves

1. Everything outside the 100-yard sphere continues normally. Enemies patrol, players move, cooldowns tick, and combat continues.

2. An outside creature can approach the boundary. The moment it crosses into the sphere, it freezes and becomes part of that lock.

3. An outside player remains fully mobile, but frozen units are read-only to them. They may:

   - See and select them
   - Inspect them
   - Chat with them

   They may not newly attack, cast on, trade with, summon, resurrect, duel, command, or use a frozen NPC's services.

4. Both the client and Core enforce that rule. Bypassing the client-side refusal still results in the Core rejecting the action.

5. Effects cannot cross the frozen boundary in either direction. Immediate attacks, healing, area effects, and procs are suppressed when either the source or target is frozen.

6. A projectile or explicitly targeted delayed spell already in flight waits until the target thaws instead of landing during the freeze. Immediate or area effects that cannot safely be retained are suppressed.

7. An enemy outside may continue chasing a frozen target. If it crosses into the radius during that chase, it freezes too.

8. Doors, scenery, transports, and the map itself are not globally paused. Tactical Freeze is a lock on loaded, living actors and their gameplay effects, not a server-wide time stop.

9. Separate freezes elsewhere can run at the same time. If their queued plans share an actor, the Core executes that actor's plans in a safe, deterministic order.

10. A real player caught by somebody else's radius cannot Resume that freeze. Only its initiating socket owner can release it.

