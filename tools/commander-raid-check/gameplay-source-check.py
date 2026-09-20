"""Read-only gameplay wiring contracts; not a substitute for an in-game click test."""
from pathlib import Path

root = Path(__file__).resolve().parents[2]
read = lambda path: (root / path).read_text(encoding="utf-8")
control = read("MSUIClient/GameLoop/Scene/GameLoop.Control.cs")
button = control.split("private void DrawTacticsButton()", 1)[1].split("private void DrawControlBanner()", 1)[0]
panel = read("MSUIClient/GameLoop/Panels/GameLoop.CommanderRaid.cs")
entry = read("MSUIClient/GameLoop/Panels/GameLoop.PartyTactics.cs")
hud = read("MSUIClient/GameLoop/Combat/GameLoop.CombatFeedback.cs")
facts = read("MSUIClient/GameLoop/Scene/GameLoop.MemberFacts.cs")
qa = read("MSUIClient/GameLoop/Dev/GameLoop.CommanderRaidLiveQa.cs")
gm = read("MSUIClient/GameLoop/Dev/GameLoop.DevTools.GmConsole.cs")
view = qa.split('case "freeview":',1)[1].split('case "drive":',1)[0]
clear = qa.split('case "pause":',1)[1].split('case "assign":',1)[0]
checks = {
 'Command View retries only after pending control resolves': 'ControlState.OwnChar or ControlState.Possessing' in view and 'NowSeconds() >= _raidQaNextControlRequest' in view and 'ToggleFreeView()' in view and 'NowSeconds() + 3' in view,
 'Pause and Clear retry only with no pending request': '_commanderRaidPending != 0) break' in clear and 'NowSeconds() >= _raidQaNextControlRequest' in clear and 'NowSeconds() + 3' in clear,
    "manual escape guidance refreshes promptly and reaches path corners": '_commanderRaidPollAt = now + (escaping ? .2 : 1)' in panel and 'a.Guid == ControlledGuid && a.Guidance.State is 2 or 4' in panel and 'delta.X * delta.X + delta.Y * delta.Y > .04f' in qa,

    "consumable baseline waits for a newly received controlled inventory": 'consumableSnapshotAt <= _raidQaConsumableSnapshotRequestedAt' in qa and qa.index('consumableSnapshotAt <= _raidQaConsumableSnapshotRequestedAt') < qa.index('var consumableCopies =') and '_net.SuiMemberFacts([ControlledGuid]);' in qa,

    "consumables use the controlled actual inventory and normal item handler": 'EnumerateActionItemCopies(consumableOwner, consumableEntry)' in qa and 'SendItemUse(consumableCopy.Bag, consumableCopy.Slot, consumableCopy.Item, consumableTemplate)' in qa,
    "consumable proof requires one item spent and received aura": 'CommanderRaidAttemptLaw.ConsumedExactlyOne(_raidQaConsumableBefore, consumableCount, auraObserved)' in qa and 'WriteRaidQaEvent("consumable-used"' in qa,

    "safe manual hold permits ordinary threat actions": "if (guidance is not null && guidance.State != 1)" in qa and "guidance?.State == 1 ? _controller.Position : CommanderRaidAttemptLaw.TankMovementGoal" in qa,
    "QA supplies are counted from the controlled received inventory": "EnumerateActionItemCopies(suppliesOwner, 2516)" in qa and "i.Item.Fields.ItemStackCount" in qa and "owner = ControlledGuid, ammunitionEntry = 2516u" in qa,
    "QA assignments reload production encounter files and preserve the chosen data": 'CommanderEncounterCatalog.Load(Path.Combine(_config.RepoRoot, "encounter-definitions"))' in qa and 'CommanderRaidPlan.ForEncounter(loadedEncounter)' in qa and '"encounter.json"' in qa and 'Assignment requires unsealed preparation.' in qa,
    "deferred setup waits before the unchanged GM guard": qa.index('if (RaidQaAnyCombat()) break;', qa.index('case "setup":')) < qa.index('SendGmCommand(string.Join', qa.index('case "setup":')) and 'Deferred setup requires the unsealed preparation stage.' in qa,
    "fast staging rechecks live exact40 full health and Clear": 'case "recovered":' in qa and 'CommanderRaidAttemptLaw.AuthorizedRoster(recovered.Select(m => m.Guid))' in qa and 'recovered.All(m => m.FactsReady && m.Alive' in qa and '_commanderRaidStatus?.State == 0' in qa,
    "pet feeding uses controlled inventory, normal item cast and received happiness": "ControlledGuid is < 158 or > 160" in qa and "EnumerateActionItemCopies(feedingOwner, foodEntry)" in qa and "CommitItemCast(feedingSpell, food.Guid)" in qa and "feedingPet.Fields.Power(4) >= happinessGoal" in qa and "send no feed cast while combat persists." in qa,

    "selected pet is unique and owned by an authorized character": "petOwnerToSelect is < 154 or > 160" in qa and "GuidInfo.IsPet(e.Guid) && e.Fields.SummonedBy == petOwnerToSelect" in qa and "ownedPetSubjects.Length == 1" in qa,
    "pet training uses ordinary learned cast and expected effect": "teacher.EffectTriggerSpells[e.i] == learned" in qa and "KnownSpells.Contains(petSpell)" in qa and "CommitSelection(_petGuid, false); TryCast(petSpell)" in qa,
    "QA drive returns normally from possession": "RequestControlRelease(toFreecam: false);" in qa and "done = !_freeView && ControlledGuid == LocalPlayerGuid;" in qa,
    "QA release retries are paced only after normal pending state ends": "_controlState == ControlState.Possessing && NowSeconds() >= _raidQaNextControlRequest" in qa and "_raidQaNextControlRequest = NowSeconds() + 3;" in qa and "if (NowSeconds() >= _raidQaDeadline)" in qa,
    "pet control retries use the guarded send only after pending state ends": "(_controlState is ControlState.OwnChar or ControlState.FreeCam) &&" in qa and "NowSeconds() >= _raidQaNextControlRequest" in qa and "RequestPossess(petOwner);" in qa and "_raidQaNextControlRequest = NowSeconds() + 3;" in qa and "pet-control-request" in qa,
    "pet readiness requires authorized owner and received ownership": "petOwner is < 154 or > 160" in qa and "inspectedPet.Fields.SummonedBy == petOwner" in qa and "Pet readiness inspection requires recovery." in qa and "if (RaidQaAnyCombat()) break; // Wait without switching control" in qa,
    "network-ended QA battle writes a failed terminal event": 'reason = "Live observation ended"' in (root/'MSUIClient/GameLoop/Dev/GameLoop.LiveRun.cs').read_text(encoding='utf-8'),
    "blocked staging records actual combat body identities": "combatBodies =" in qa and "combatObjectives =" in qa,
    "invalid floor pose terminates acceptance before status waits": 'pose.Z < CommanderEncounterLaw.Point(_commanderRaidDraft.Encounter.Bounds.Min).Z' in qa and qa.index('The controlled body left the encounter floor envelope.') < qa.index('CommanderRaidGuidanceLaw.Fresh(now - _commanderRaidStatusAt)'),
    "queued tank attacks cannot starve later threat abilities": "(ability.OnNextSwing && _queuedMeleeSpell == spell) ||" in qa and qa.index("(ability.OnNextSwing && _queuedMeleeSpell == spell) ||") < qa.index("TryCast(spell); break;"),
    "QA tank movement uses tested contact and threat law": "CommanderRaidAttemptLaw.TankMovementGoal" in qa,
    "preparation parks the exact forty and preserves owner AI across inspections": 'case "park":' in qa and 'RequestControlRelease(toFreecam: true)' in qa and 'parkedBots.Length != 39' in qa and 'parkedBots.Append(LocalPlayerGuid)' in qa and 'parkedRaid.All(g => _suiChain.TryGetValue' in qa,
    "observation abort marks failure before any recovery": 'return RaidQaFail("Observation aborted: "' in qa,
    "stopped executor ends the attempt even after main death": qa.index('The encounter stopped before a boss kill.') < qa.index('if (main.IsDead || main.Fields.PlayerIsGhost)'),

    "main death retains authoritative observation before visibility handling": qa.index('if (main.IsDead || main.Fields.PlayerIsGhost)') < qa.index('if (boss is null)') and 'status.Actors.All(m => m.Known && !m.Alive)' in qa,
    "ordinary map and opt-in driver consume the same guidance": 'CommanderRaidGuidanceLaw.Current' in panel and 'CommanderRaidGuidanceLaw.Current' in qa and 'guidance.Waypoint - _controller.Position' in qa,
    "actual server death and completion required": 'status.BossKnown && !status.BossAlive && status.BossHealth == 0' in qa and '_commanderRaidStatus?.State != 4' in qa,

    "raid QA command guard runs before sending to the server": gm.index("GuardCommanderRaidQaCommand(command)") < gm.index("_net?.SendChatSay(command)"),
    "raid QA seals pull provenance before normal aggro can begin": qa.index('WriteRaidQaEvent("pull-request"') < qa.index('SendGmCommand(".gm off"'),
    "raid QA preserves exact boss identity across GM-off acknowledgment": "status.BossGuid != _raidQaBossGuid" in qa,
    "normal gameplay HUD draws the planner": "DrawPartyTacticsPanel();" in hud and "DrawCommanderRaidPlanner();" in entry,
    "Commander button opens the gameplay panel": "OpenPartyTactics(tacticsBot);" in button and "DrawTacticsButton();" in control,
    "button is not gated by creator or developer mode": not any(s in button for s in ("_creator", "DevTools", "ProbeArmed")),
    "main role required before auto-assign": '"My role: "' in panel and "editing && _commanderRaidDraft.MainRole != CommanderRaidRole.Unassigned" in panel,
    "ghost players cannot satisfy living raid readiness": "!entity.Fields.IsDead && !entity.Fields.PlayerIsGhost" in panel,
    "full raid roster includes the actual main": "OwnCharacterPartyRow().Concat(_partyMembers).DistinctBy(m => m.Guid)" in panel,
    "spell facts are observed on the actual network handler": "_commanderFactsSeen.Add(guid)" in facts and "_commanderFactsSeen.Contains(member.Guid)" in panel,
    "live target supplies map and world position": "CommanderBossCatalog.Find(target.Fields.Entry" in panel and "UnitWorldPosition(target), roster.Count" in panel,
    "primary healing patient is editable in gameplay": '"raid-primary-patient"' in panel and "HealPrimary = assignments[" in panel,
    "arming requires acceptance of this exact draft": "ReferenceEquals(_commanderRaidApplied, _commanderRaidDraft)" in panel,
    "background status polling preserves a rejected mutation": "_commanderRaidPendingOperation != CommanderRaidOperation.Inspect" in panel and "else if (mutation || !_commanderRaidMutationFailed)" in panel,
    "synthetic roster is excluded from gameplay state": "_commanderRaidProbeRoster" not in panel and "_commanderRaidProbeRoster" not in entry,
}
for name, passed in checks.items():
    print(("PASS " if passed else "FAIL ") + name)
if not all(checks.values()):
    raise SystemExit(1)
print(f"{len(checks)} gameplay source contracts passed; live interaction remains to be tested.")
