# POSSESS_LAW — the body you drive is the body that acts

Owner law, settled 2026-09-03 across a full day of "you did it again". Applies to
BOTH modes: direct control (possessing a party bot or a companion) and the Command
View (commanding from the sky). Read this before touching possession, companions,
the Command View, any NPC/loot/taxi/mail interaction, or the fleet follow logic.

Enforcement lives next to the law: `dotnet run --project tools/interface-wire-check
-- --possess-law-only` (client source) and `tools/possess-law-check.sh` (Core
source on the box, run over ssh). Both must stay green. A rule that is not
enforced by a check is a rule that will be broken again — add the check with the
fix.

Vocabulary: **actor / driven body** = the possessed bot while driving one, else
the session's own character (`WorldSession::GetSuiActor()` server-side,
`ControlledGuid` / `TryGetInteractionBodyPose` client-side). **Main** = the
logged-in character while a bot is driven. **Session body** = the main, always.

---

## 1. Server: every gameplay handler acts as the actor

1.1 A handler that means "the character doing this" resolves its `Player*` via
`GetSuiActor()`, never `_player` / `GetPlayer()`. Session-only things stay on the
session: security level, locale, error toasts (`SendEquipError`, `SendBuyError`)
which go to `_player` so the commander's socket shows them.

1.2 Every reply the routed code emits on the ACTOR's session (`actor->GetSession()
->SendPacket`, `Player::Send*` helpers, `Loot::Notify*`, pet owner packets, the
Bind spell, `OnGossipSelect`) MUST be in `SuiPossess::MirrorOwnerPacket`'s
whitelist AND have a `case Op.X:` in the client's `ApplySuiProxy`. A reply that
lands on the bot's socket-less session and is not mirrored "silently does
nothing" — the owner's exact words. That does not count as functioning.

1.3 `Player::OnGossipSelect` answers on the bot's session for every family. When
a family is routed, audit that switch and mirror its frames (done: vendor,
trainer, quest, taxi, bank, stable, talent wipe, innkeeper bind, auction hello).

1.4 Owner-only fields the commander cannot see (bags, coin, bank, buyback,
stats, talent points) ride `SMSG_SUI_SNAPSHOT`. After any routed edit of them
call `SuiPossess::ResnapshotControlled(this)`.

1.5 Pair-deploy: a new client opcode against an old Core kicks the session; a
new Core is inert without the client bit. Capability bits gate new wires.

## 2. Client: every gate ranges from the driven body

2.1 Distance/eligibility gates for anything the actor does use
`TryGetInteractionBodyPose`, never `TryGetSessionBodyPose`: loot, vendor,
trainer, gossip, quest, bank, taxi, mail, auction, stable, innkeeper, game
objects (mailbox, plaque, chest), area triggers, the world cursor verdict, the
Command View walker. `TryGetSessionBodyPose` is legal only for things that are
genuinely the main's: its own corpse/rez, the dev live-run tool, the tabard/help
frames until they are routed.

2.2 Purse and bag displays read `ControlledGuid`'s entity, never
`_net.PlayerGuid`. The coin the panel shows is the coin that pays.

2.3 Body-scoped session UI changes hands on every control ack, both directions:
pet bar, loot window, bank session, taxi map, server ride
(`ResetBodySessionUiOnControlChange`). The server pushes the new body's pet bar
after the ack.

2.4 The world map arrow is the driven body; the yellow dots are everyone else.

## 3. Movement, travel, teleports

3.1 A flight never releases possession. The human rides the bot's flight (the
ride spline may own the controller while possessing). The landing does NOT
teleport a driven flyer to the node (`TaxiStepFinished`).

3.2 A same-map (near) teleport of EITHER side keeps the possession. The bot's:
the ack is mirrored, the client snaps its controller and acks for the bot,
`PlayerBotAI` stands down its own ack while possessed. The main's (its chain
catch-up): the client adopts it on the streamed entity and acks with the main's
guid, which the server accepts while the mover is the bot. A map change or a
transport still breaks the pair.

3.3 Hopping (Ctrl+Tab / Ctrl+Click) to a party member out of streaming range on
the same map is granted IN PLACE: the camera moves, the main does not. Only a
cross-map hop relocates the main.

3.4 Area triggers fire for the driven body (client scans them at the interaction
body; server checks them against the actor).

## 4. The rest of the party STAYS

4.1 In direct control, when the driven body flies or the human hops to a far
body, every follower — the unattended main included — HOLDS. It never
catch-up-teleports after a gryphon or across a zone. The left-behind hold
(`AiBotAI::m_suiLandedHold`) is set when: a bot lands a flight without its
human; the human hops to a body beyond catch-up range (`HoldIfLeftBehind` at
grant and at release, and the last-boss identity rule in `DoPartyFollow`); a
boss that was flying lands far away. It clears when the anchor comes within
catch-up range, on possession of that body, and RTS orders bypass it.

4.2 A hold also ENDS an active follow leg (`SuiStopFollowForHold`) — returning
early from `DoPartyFollow` leaves the old follow generator chasing.

4.3 The boss position is recorded EVERY tick, flights included. A same-map gap
beyond catch-up range is a PORT only when the SAME boss jumped in one tick. The
CHAIN follows a port: every linked member, the unattended main included,
catch-up teleports after the driven body (the tower portal — owner: "the
non-main follow me through the portal... at least it worked"). The main's own
near teleport must never break the possession (`OnPlayerTeleport` possessor
near case; `HandleMoveTeleportAck` accepts the session player's ack while the
mover is the bot). A flight or a hop is NOT a port: those hold (4.1).

4.4 Command View party flight: the whole commanded party takes the flight from
the flight master; nobody flies unless everyone can board or the commander
confirmed ("fly with the rest?").

## 5. Command View interaction shape

5.1 Nothing opens until the acting body is physically at the NPC: walk first,
open on arrival. That includes the multi-offer chooser ("quests, flight map, or
talk?") — it is raised on arrival, never on click.

5.2 A chooser only for NPCs with two DISTINCT offers. The database stamps the
innkeeper bit on every bowyer; use `EffectiveNpcFlags` (name-verified
innkeeper), never the raw flag word.

5.3 Every dialog we own (chooser, party-flight confirm) auto-hides when the
acting body leaves talking range or the NPC is gone — the same lifecycle the
vendor and bank windows have.

5.4 Q / Shift+Q cycles the primary strictly within the currently selected
command cards, in their displayed order. With zero or one selected card it is a
no-op; it never imports nearby party members, companions, or local faction bots
into the selection.

## 6. What stays on the main (owner decisions)

Chat (say/yell/emote stay the main's voice), group verbs, duel, petitions,
character. Guild is unrouted (low priority). Cross-map possession hops still
relocate the main (no camera across maps).

---

## Where the code is

| Piece | Location |
|---|---|
| Actor + mirror whitelist + grant/release + holds | box `src/game/SuperUiContent/SuiWorld/CRPG/SuiPossess.{h,cpp}` |
| Party flight wire | box `.../CRPG/SuiTaxi.{h,cpp}`, opcodes 868/869, capability bit 11 |
| Fleet follow / holds | box `src/game/SuperUiContent/SuiBots/AiBotAIMain.{h,cpp}` `DoPartyFollow` |
| Landing / teleport hooks | box `src/game/Objects/Player.cpp` (`TaxiStepFinished`, `TeleportTo`), `Movement/WaypointMovementGenerator.cpp`, `PlayerBots/PlayerBotAI.cpp` |
| Client proxy unwrap | `MSUIClient/GameLoop/Scene/GameLoop.Control.cs` `ApplySuiProxy` |
| Client body gates | `TryGetInteractionBodyPose` (Control.cs), used by every panel/gate |
| Command View walk-then-open | Control.cs `BeginCommandViewInteraction` / `UpdateCommandViewPendingInteraction` |
| Tactical Freeze client state/wire | `GameLoop.TacticalFreeze.cs`, `TacticalFreezeWire.cs`, `TacticalFreezePoseLaw.cs` |
| Tactical Freeze server lock/queue | box `src/game/SuperUiContent/SuiWorld/CRPG/SuiTacticalFreeze.{h,cpp}` |
| Wire spec | box `docs/SUI_WIRE_PROTOCOL.md` (SMSG_SUI_PROXY table, Party flight) |
| Day log | `docs/current/CRPG_RTS_WIP.md` sections dated 2026-09-03 |

## 7. The chain (owner 2026-09-03)

7.1 Chain state is SERVER truth, carried per roster row (`SMSG_SUI_CONTROL_ROSTER`
v2: chain state + anchor guid) and re-pushed on every edge. The client draws
exactly that; the saved per-name link intent is only a fallback for an old core.

7.2 Three states. Linked (green): follows its anchor, ports included. Unlinked by
the human (red): holds until re-linked, regardless of range. World hold (amber):
landed alone, human hopped far, boss flew off — clears by itself when the anchor
is back in catch-up range. An explicit re-link also lifts a world hold.

7.3 WHO: the anchor (`FindEscortBoss`, the body the formation keys on) is shown
next to every link and named in its tooltip.

7.4 The anchor is ANY group member, not only the driven body (owner: "chain 2
players and 2 others — it's not just main to main"). ORDER_FOLLOW with a target
stores `AiBotAI::m_suiChainAnchor`; `FindEscortBoss` honours it first (a real
player anchors at their driven body, a bot at itself); empty clears back to the
group rules. Following IS linking (it lifts an unlink or a world hold). Command
View: right-click a party member with a selection = chain the selection to it;
party frames: drop a portrait on another = chain to that member, on the player
frame = back to the default. The party portrait and command card carry the chain
state and WHO; Command View world models stay visually clean (7.7).

7.5 Badge art is one shared silver rounded-square frame with a silver chain;
state is carried by the flat inner field. Linked = green field + intact chain.
Explicit unlink = red field + visibly broken chain. World hold = yellow field +
intact chain. The anchor-initial medallion (WHO, for example the liked `Z`) stays
separate and unchanged.

7.6 On party portraits, the chain badge occupies the lower-left position at
`PartyMemberLogicalOrigin + (11.5, 39.5)`. The separate anchor-initial medallion
(WHO) sits on the upper-left rim just above 9 o'clock at
`PartyMemberLogicalOrigin + (8.5, 18.5)`.

7.7 Command View world models carry no chain badge, WHO medallion, or chain
connector line. The party portraits and small command cards are sufficient and
remain the only chain-state visuals.

## 8. Tactical Freeze: owner, actor, and pose are different identities

8.1 Command View remains live. It does not imply a pause. Only its explicit
eighth command-card button requests a localized server lock, and only a real
player session may initiate one. The server owns the radius, membership,
revision, release, and every queue mutation.

8.2 The lock's `ownerGuid` is the real socketed player and is the only
authorization identity. The one member row carrying `AnchorBody` is the driven
body sampled when the lock begins; it may be a possessed companion and MUST NOT
be required to equal `ownerGuid`. Client ownership checks compare `ownerGuid`
with `LocalPlayerGuid`, never `ControlledGuid`.

8.3 Only the owner may Resume or author/cancel/clear orders. Another real human
inside the radius is frozen and read-only even if they are in Command View. Their
currently driven body suppresses movement, attack, and cast prediction in normal
view too; chat and UI stay live.

8.4 Each authoritative member holds its exact current world pose: spline and
facing stop, the sampled animation/emote/combat frame remains visible, and all
affected clocks rebase on thaw. Membership is tracked per lock so releasing one
overlapping radius cannot thaw a body still held by another.

8.5 Leaving Command View asks the server to release an owned lock, but it does
not clear locally. Only an authoritative release/NOT_FOUND snapshot, capability
or session teardown changes client truth. A release tombstone retains its lock
identity so overlap removal is exact. Queues remain keyed to that lock until the
server drains them.

8.6 Queued spells carry their actor explicitly and never use possession as a
handoff. Queue v1 supports move, attack, and cast only, up to five actions per
commandable member. Item quickslots are consumed with an honest refusal while
frozen; they must never fall through to `CMSG_USE_ITEM` or possession.

8.7 The read-only boundary is both actor- and addressed-object-aware. A client
outside another owner's radius may keep selection, inspection, chat, cleanup,
and true static queries live, but it MUST refuse a newly authored interaction
when the named player, NPC, pet, creature corpse, or stored service source is an
authoritative frozen member. This includes opening and mutating gossip, vendor,
bank, auction, loot, trade, taxi, trainer/talent-wipe, binder, tabard, stable,
quest, resurrection, party/raid, and follow/duel paths. The check belongs before
any optimistic state or deferred send is armed; cancellation, decline, release,
and other retractions remain legal.
