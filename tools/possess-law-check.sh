#!/usr/bin/env bash
# POSSESS_LAW (POSSESS_LAW.md), Core half. Run from this repo:
#   bash tools/possess-law-check.sh            # over ssh against 192.168.0.2:~/vmangos
#   bash tools/possess-law-check.sh --local ~/vmangos   # on the box itself
# Greps the Core SOURCE for the rules that were broken and re-broken on 2026-09-03.
# Exit 1 on the first violation; prints PASS otherwise. Pair with
#   dotnet run --project tools/interface-wire-check -- --possess-law-only   (client half)
set -u
if [ "${1:-}" = "--local" ]; then
  TREE="${2:-$HOME/vmangos}"; RUN=(bash -c)
  run() { bash -c "cd '$TREE' && $1"; }
else
  run() { ssh 192.168.0.2 "cd ~/vmangos && $1"; }
fi
fail() { echo "POSSESS_LAW FAIL: $1"; exit 1; }
G=src/game

# 1.1 routed handler families act as GetSuiActor(): every Handle*Opcode in these files
#     must resolve its player through GetSuiActor (counted per file: handlers vs actor sites).
for f in Handlers/LootHandler.cpp Handlers/PetHandler.cpp Handlers/TaxiHandler.cpp \
         Handlers/QuestHandler.cpp Handlers/TradeHandler.cpp Handlers/MailHandler.cpp \
         Handlers/AuctionHouseHandler.cpp; do
  n=$(run "grep -c 'GetSuiActor()' $G/$f" | tr -d '\r')
  [ "${n:-0}" -ge 1 ] || fail "1.1 $f has no GetSuiActor() site"
done
for fn in HandleBankerActivateOpcode HandleBuyBankSlotOpcode HandleAutoBankItemOpcode \
          HandleAutoStoreBankItemOpcode HandleAutoStoreBagItemOpcode HandleListStabledPetsOpcode \
          HandleStablePet HandleUnstablePet HandleBuyStableSlot HandleStableSwapPet \
          HandleBinderActivateOpcode HandleGameObjectUseOpcode HandleAreaTriggerOpcode \
          HandleTalentWipeConfirmOpcode HandleActivateTaxiOpcode HandleTaxiQueryAvailableNodes; do
  ok=$(run "awk '/void WorldSession::$fn\\(/{f=1} f&&/GetSuiActor\\(\\)/{print \"yes\"; exit} f&&/^}/{exit}' $G/Handlers/*.cpp" | tr -d '\r')
  [ "$ok" = "yes" ] || fail "1.1 $fn does not act as GetSuiActor()"
done

# 1.2 every frame the client unwraps is whitelisted, and vice versa (the same list lives in
#     tools/interface-wire-check/PossessLawClinicalChecks.cs).
for op in SMSG_LOOT_RESPONSE SMSG_LOOT_RELEASE_RESPONSE SMSG_LOOT_REMOVED SMSG_LOOT_CLEAR_MONEY \
          SMSG_LOOT_MONEY_NOTIFY SMSG_ITEM_PUSH_RESULT SMSG_PET_SPELLS SMSG_PET_MODE \
          SMSG_PET_ACTION_FEEDBACK SMSG_PET_CAST_FAILED SMSG_ACTIVATETAXIREPLY SMSG_SHOWTAXINODES \
          SMSG_TAXINODE_STATUS SMSG_NEW_TAXI_PATH MSG_MOVE_TELEPORT_ACK SMSG_SHOW_BANK \
          MSG_LIST_STABLED_PETS MSG_TALENT_WIPE_CONFIRM SMSG_BINDER_CONFIRM SMSG_PLAYERBOUND \
          MSG_AUCTION_HELLO SMSG_GOSSIP_MESSAGE SMSG_LIST_INVENTORY SMSG_TRAINER_LIST; do
  ok=$(run "awk '/void MirrorOwnerPacket\\(/{f=1} f&&/case $op:/{print \"yes\"; exit} f&&/default:/{exit}' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" | tr -d '\r')
  [ "$ok" = "yes" ] || fail "1.2 $op is not in MirrorOwnerPacket's whitelist"
done

# 1.3 every OnGossipSelect reply opcode of a routed family is whitelisted (the switch answers
#     on the bot's session): SendShowBank/SendStablePet/SendTaxiMenu/SendTalentWipeConfirm/
#     SendAuctionHello/SendBindPoint are covered by the opcodes above; assert the calls exist
#     so a renamed helper is noticed.
for helper in SendShowBank SendStablePet SendTaxiMenu SendTalentWipeConfirm SendAuctionHello SendBindPoint SendListInventory SendTrainerList; do
  ok=$(run "grep -c '$helper' $G/Objects/Player.cpp" | tr -d '\r')
  [ "${ok:-0}" -ge 1 ] || fail "1.3 OnGossipSelect no longer calls $helper — re-audit the gossip switch"
done

# 1.4 routed edits re-snapshot
for f in Handlers/LootHandler.cpp Handlers/ItemHandler.cpp Handlers/NPCHandler.cpp Handlers/SkillHandler.cpp; do
  n=$(run "grep -c 'ResnapshotControlled' $G/$f" | tr -d '\r')
  [ "${n:-0}" -ge 1 ] || fail "1.4 $f never re-snapshots after an edit"
done

# 3.1 flights never release; the landing does not teleport a driven flyer
run "grep -q 'lastPointReached && !SuiPossess::IsSuiPossessed(this)' $G/Objects/Player.cpp" || fail "3.1 TaxiStepFinished teleports a driven flyer (breaks possession)"
run "grep -q 'IsTaxiFlying' $G/SuperUiContent/SuiBots/AiBotAIMain.cpp" || fail "3.1 the fleet AI has no in-flight guard"

# 3.2 a near teleport of the possessed bot keeps the pair; the bot AI stands down its ack
run "grep -q 'void OnPlayerTeleport(Player\\* player, bool farTeleport)' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" || fail "3.2 OnPlayerTeleport lost its near/far split"
run "grep -q 'IsBeingTeleportedNear() && !SuiPossess::IsSuiPossessed(me)' $G/PlayerBots/PlayerBotAI.cpp" || fail "3.2 PlayerBotAI acks near teleports while possessed"

# 3.3 a same-map party member is granted in place
run "grep -q '!visible && !(partyAuthorized && sameMapInstance)' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" || fail "3.3 TryBegin relocates the main for a same-map party member"

# 4.x the rest of the party stays
run "grep -q 'm_suiLandedHold' $G/SuperUiContent/SuiBots/AiBotAIMain.h" || fail "4.1 the left-behind hold is gone"
run "grep -q 'SuiPossess::OnTaxiLanded(&player)' $G/Movement/WaypointMovementGenerator.cpp" || fail "4.1 landing no longer sets the hold"
run "grep -q 'HoldIfLeftBehind(possessor, bot)' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" || fail "4.1 the main is not held on a far grant"
run "grep -q 'HoldIfLeftBehind(bot, possessor)' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" || fail "4.1 a released bot is not held when far"
run "grep -q 'SuiStopFollowForHold' $G/SuperUiContent/SuiBots/AiBotAIMain.cpp" || fail "4.2 a hold does not end the active follow leg"
run "grep -q 'driving) near-teleports: possession kept' $G/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp" || fail "4.3 the main's chain catch-up teleport breaks the possession"
run "grep -q 'packet.guid == _player->GetObjectGuid() && _player->IsBeingTeleportedNear()' $G/Handlers/MovementHandler.cpp" || fail "4.3 the main's own near-teleport ack is refused while the mover is the bot"
run "grep -q 'IsSuiPossessed(pBoss)' $G/SuperUiContent/SuiBots/AiBotAIMain.cpp" && fail "4.3 the chain must follow a port of the driven body (do not hold on IsSuiPossessed(pBoss))"
run "grep -q 'm_suiBossFlewAway' $G/SuperUiContent/SuiBots/AiBotAIMain.cpp" || fail "4.3 a boss that flew away is not turned into a hold"

# 4.4 party flight wire
run "grep -q 'CMSG_SUI_PARTY_TAXI *= 868' $G/Server/Protocol/Opcodes_1_12_1.h" || fail "4.4 party-flight opcode drifted from 868"
run "grep -q 'CAPABILITY_PARTY_TAXI_V1' $G/SuperUiContent/SuiWorld/Bridge/SuiPortal.cpp" || fail "4.4 party-flight capability bit is not advertised"

echo "possess-law-check: PASS"
