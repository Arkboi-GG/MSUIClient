#!/usr/bin/env python3
"""Source regression guard for GI-39/60/78/80; no server or database access.

Run on the Core box: python3 check-actor-fixes.py /home/wowvmangos/vmangos
These structural checks complement compilation and the pending live matrix.
"""
from pathlib import Path
import sys
import re

tree = Path(sys.argv[1])
game = tree / 'src/game'
def source(path):
    return (game / path).read_text(encoding='utf-8')
def body(text, signature):
    start = text.index('{', text.index(signature))
    depth = 1
    end = start + 1
    while depth:
        depth += (text[end] == '{') - (text[end] == '}')
        end += 1
    return text[start:end]
def require(condition, message):
    if not condition:
        raise AssertionError(message)

mail = source('Handlers/MailHandler.cpp')
opcodes = source('Server/Protocol/Opcodes.cpp')
for opcode in ('CMSG_SEND_MAIL', 'CMSG_SUI_CONTROL_RELEASE', 'CMSG_SUI_TACTICAL_FREEZE'):
    require(re.search(r'DEFINE_HANDLER\(' + opcode + r',\s*STATUS_LOGGEDIN,\s*PACKET_PROCESS_WORLD,', opcodes),
            opcode + ': mail and control edges must share the ordered world queue')
for name in ('HandleSendMail', 'HandleSendMailCallback', 'HandleMailMarkAsRead',
             'HandleMailDelete', 'HandleMailReturnToSender', 'HandleMailTakeItem',
             'HandleMailTakeMoney', 'HandleGetMailList', 'HandleMailCreateTextItem',
             'HandleQueryNextMailTime'):
    fn = body(mail, 'void WorldSession::' + name + '(')
    require('pActor->GetSession()->GetMasterPlayer()' in fn, name + ': wrong mail store')
    require('= GetMasterPlayer()' not in fn, name + ': session mail store returned')
send = body(mail, 'void WorldSession::HandleSendMail(')
require('req->senderGuid = pActor->GetObjectGuid()' in send, 'send actor not retained')
require('req->sessionPlayerGuid = GetPlayer()->GetObjectGuid()' in send, 'socket identity not retained')
require(re.search(r'if \(SuiTacticalFreeze::IsSessionGameplayFrozen\(this\)\)\s*\{\s*'
                  r'(?://[^\n]*\n\s*)*SendMailResult\(0, MAIL_SEND, MAIL_ERR_INTERNAL_ERROR\)', send),
        'frozen mail send must acknowledge refusal instead of leaving the client pending')
callback = body(mail, 'void WorldSession::HandleSendMailCallback(')
for token in ('pActor->GetObjectGuid() != req->senderGuid',
              'IsSessionGameplayFrozen(this)', 'CheckMailBox(req->mailboxGuid)',
              'req->receiverPtr = sObjectMgr.GetPlayer(req->receiver)'):
    require(token in callback and callback.index(token) < callback.index('ModifyMoney'),
            'async mail commit guard missing: ' + token)
require('GetObjectGuid() != sessionPlayerGuid' in body(mail, 'void Callback('),
        'mail callback no longer checks the original socket character')

obj = source('Objects/Object.cpp')
require('actor->GetClass() != CLASS_HUNTER' in obj and
        'target->GetClass() != CLASS_HUNTER' not in obj, 'stable visibility uses session class')
require(obj.count('SuiPossess::IsControlledOwnerOf(ToUnit(), target)') == 2,
        'possessed owner must grant both stats and true health')
require('(fieldFlags[index] & UF_FLAG_OWNER_ONLY) && !(fieldFlags[index] & visibleFlags)' in obj,
        'forced refresh discloses revoked owner data')
possess = source('SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp')
ownership = body(possess, 'bool IsControlledOwnerOf(')
for token in ('GetControlledBot(observer->GetSession())', 'GetPossessor(controlled) == observer',
              'unit->GetOwnerGuid() == controlled->GetObjectGuid()',
              'unit->GetCharmerGuid() == controlled->GetObjectGuid()'):
    require(token in ownership, 'pet ownership missing: ' + token)
require('RefreshActorVisibility(session, possessor->GetObjectGuid())' in body(possess, 'static AckResult TryBegin('),
        'grant does not refresh pre-existing objects')
require('RefreshActorVisibility(session, botGuid)' in body(possess, 'static bool DoRelease('),
        'release does not revoke cached owner visibility')
refresh = body(possess, 'static void RefreshActorVisibility(')
for token in ('m_visibleGUIDs_lock', 'UF_FLAG_OWNER_ONLY, true', 'UNIT_NPC_FLAGS',
              'UNIT_FIELD_HEALTH', 'UNIT_FIELD_MAXHEALTH', 'owner == previousActor',
              'charmer == previousActor', 'data.Send(session)'):
    require(token in refresh, 'visibility refresh missing: ' + token)

npc = source('Handlers/NPCHandler.cpp')
stable = body(npc, 'bool WorldSession::CheckStableMaster(')
require('pActor->GetClass() != CLASS_HUNTER' in stable, 'stable class gate missing')
require('pActor->GetNPCIfCanInteractWith(guid, UNIT_NPC_FLAG_STABLEMASTER)' in stable,
        'stable mutation distance uses the session body')
require('GetPlayer()->GetNPCIfCanInteractWith' not in stable, 'session distance gate returned')
require('CheckStableMaster(guid)' in body(npc, 'void WorldSession::SendStablePet('),
        'gossip stable opening bypasses actor eligibility')
for name in ('HandleStablePet', 'HandleUnstablePet', 'HandleBuyStableSlot', 'HandleStableSwapPet'):
    require('CheckStableMaster(packet.npcGuid)' in body(npc, 'void WorldSession::' + name + '('),
            name + ': stable follow-up bypasses actor gate')
for name, calls in (
        ('HandleStablePet', ('pet->Unsummon(PetSaveMode(free_slot), pActor)',)),
        ('HandleUnstablePet', ('newpet->LoadPetFromDB(pActor, creatureId, packet.petNumber)',)),
        ('HandleStableSwapPet', ('pet->Unsummon(PetSaveMode(slot), pActor)',
                                 'newpet->LoadPetFromDB(pActor, creature_id, packet.petNumber)'))):
    fn = body(npc, 'void WorldSession::' + name + '(')
    for call in calls:
        require(call in fn, name + ': pet save/load must use the acting owner: ' + call)
    require('LoadPetFromDB(_player,' not in fn and ', _player)' not in fn,
            name + ': session owner returned to pet save/load')

follow = body(source('SuperUiContent/SuiBots/AiBotAIMain.cpp'), 'void AiBotAI::DoPartyFollow(')
cross = body(follow, 'if (pBoss->GetMap() != me->GetMap())')
for token in ('m_suiLandedHold = true', 'NotifyChainChanged(me)', 'SuiStopFollowForHold()', 'return;'):
    require(token in cross, 'cross-map hold missing: ' + token)
require('TeleportTo' not in cross, 'cross-map automatic summon returned')
require('(bossChanged || bossPorted) && dist > AIBOT_PARTY_CATCHUP_TELEPORT' in follow,
        'same-anchor port no longer holds')
hold = body(follow, 'if (m_suiLandedHold)')
require('SuiStopFollowForHold()' in hold and 'm_suiLandedHold = false' in hold,
        'hold must end follow and recover on anchor return')
require(follow.index('if (m_suiLandedHold)') < follow.index('me->NearTeleportTo'),
        'ordinary catch-up bypasses a world hold')
print('Core actor fixes: PASS (mail, port holds, pet visibility, stable eligibility/range)')
