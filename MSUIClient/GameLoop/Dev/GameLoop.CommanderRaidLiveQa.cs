using System.Globalization;
using System.Numerics;
using System.Text.Json;
using MSUIClient.Engine.UI;
using MSUIClient.Net;
using MSUIClient.World.Encounters;

namespace MSUIClient;

/// <summary>Opt-in real-network acceptance steps. Never supplies synthetic members or combat results.</summary>
public sealed partial class GameLoop
{
    private CommanderRaidAttemptStage _raidQaStage;
    private long _raidQaEventSequence;
    private string _raidQaCommand = "";
    private string? _raidQaAttemptDirectory;
    private double _raidQaDeadline;
    private bool _raidQaSent, _raidQaPersistent, _raidQaSawCombat, _raidQaBossHidden;
    private double _raidQaNextCast, _raidQaNextSample, _raidQaStarted, _raidQaNextPotion;
    private double _raidQaInboxBlockedSince, _raidQaNextDefenseReport, _raidQaNextGuidanceReport;
    private uint _raidQaInitialHealth, _raidQaConsumableBefore;
    private double _raidQaConsumableSnapshotRequestedAt, _raidQaConsumableNextRefresh;
    private ulong _raidQaBossGuid, _raidQaRangedPullGuid;
    private Vector3? _raidQaRangedApproachOrigin;
    private double _raidQaTankSince, _raidQaResetSince, _raidQaNextFeed, _raidQaNextControlRequest;
    private static bool IsAuthorizedRaidQaBot(ulong guid) => guid is >= 115 and <= 142 or >= 150 and <= 160;

    private bool AdvanceCommanderRaidLiveQa(string line)
    {
        if (_liveRunOptions is null || _net is not { IsInWorld: true } || LocalPlayerGuid != 787)
            return RaidQaFail("The authorized Testwar session is required.");
        if (_raidQaCommand != line)
        { _raidQaCommand = line; _raidQaDeadline = NowSeconds() + (line == "raidqa fight" ? 900 : line.StartsWith("raidqa pet-feed ", StringComparison.Ordinal) ? 90 : 35); _raidQaSent = false; _raidQaNextFeed = 0; _raidQaNextControlRequest = 0; _raidQaConsumableSnapshotRequestedAt = 0; _raidQaConsumableNextRefresh = 0; }
        string[] args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (args[1] is not ("attempt" or "await") && _raidQaAttemptDirectory is null)
            return RaidQaFail("Create a labeled raidqa attempt before setup or combat.");
        bool done = false;
        switch (args[1])
        {
            case "await":
                _raidQaPersistent = true;
                string inbox = Path.Combine(Path.GetFullPath(_liveRunOptions.OutputDirectory), "next.protocol");
                if (!File.Exists(inbox)) return false;
                List<string> next;
                try
                {
                    next = File.ReadAllLines(inbox).Select(x => x.Split('#')[0].Trim()).Where(x => x.Length > 0).ToList();
                    File.Move(inbox, inbox + ".consumed-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
                    _raidQaInboxBlockedSince = 0;
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
                {
                    // Windows scanners can briefly hold an atomically published inbox.
                    // Consume nothing and retain the sealed stage until the rename succeeds.
                    if (_raidQaInboxBlockedSince == 0) _raidQaInboxBlockedSince = NowSeconds();
                    if (NowSeconds() - _raidQaInboxBlockedSince >= 10) throw;
                    return false;
                }
                next.Add("raidqa await");
                _liveSteps = next; _liveStep = -1;
                done = true;
                break;
            case "attempt":
                if (_raidQaStage is CommanderRaidAttemptStage.Fighting or CommanderRaidAttemptStage.PullPending) return RaidQaFail("End the current battle before opening another attempt.");
                string label = args.Length == 3 ? args[2] : "";
                if (label.Length == 0 || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
                    return RaidQaFail("Attempt labels must contain only ASCII letters, digits, hyphens or underscores.");
                string attempt = Path.Combine(Path.GetFullPath(_liveRunOptions.OutputDirectory), label);
                if (Directory.Exists(attempt)) return RaidQaFail("Attempt evidence already exists; use a new label.");
                Directory.CreateDirectory(attempt); _raidQaAttemptDirectory = attempt;
                _raidQaStage = CommanderRaidAttemptStage.Recovery; _raidQaEventSequence = 0; _raidQaRangedPullGuid = 0; _raidQaRangedApproachOrigin = null;
                WriteRaidQaEvent("attempt", new { label, main = LocalPlayerGuid });
                done = true;
                break;
            case "recovery":
                if (_raidQaStage is CommanderRaidAttemptStage.Fighting or CommanderRaidAttemptStage.PullPending)
                    WriteRaidQaEvent("invalidated", new { reason = "Explicit recovery ended combat acceptance." });
                _raidQaStage = CommanderRaidAttemptStage.Recovery;
                _liveHeld.Clear(); StopAttack("raid-qa-recovery");
                WriteRaidQaEvent("recovery", new { });
                done = true;
                break;
            case "prepare":
                if (RaidQaAnyCombat()) return RaidQaFail("Recovery must finish before preparation starts.");
                _raidQaStage = CommanderRaidAttemptStage.Preparation;
                WriteRaidQaEvent("preparation", new { });
                done = true;
                break;
            case "recovered":
                if (_raidQaStage != CommanderRaidAttemptStage.Recovery || _config.Start.Map != 0 || ControlledGuid != LocalPlayerGuid)
                    return RaidQaFail("Fast staging requires Testwar at the outside recovery area.");
                var recovered = CommanderRaidRoster(fresh: true);
                done = _commanderRaidStatus?.State == 0 && CommanderRaidAttemptLaw.AuthorizedRoster(recovered.Select(m => m.Guid)) &&
                    recovered.All(m => m.FactsReady && m.Alive && _entities.TryGet(m.Guid, out var e) &&
                        !e.Fields.PlayerIsGhost && !e.InCombat && e.Fields.MaxHealth > 0 && e.Fields.Health == e.Fields.MaxHealth);
                if (done) WriteRaidQaEvent("recovered-ready", new { map = _config.Start.Map, count = recovered.Count });
                break;
            case "setup":
                // Wait out transient combat before attempting a preparation command.
                // The existing SendGmCommand guard still owns permission to send it.
                if (_raidQaStage != CommanderRaidAttemptStage.Preparation)
                    return RaidQaFail("Deferred setup requires the unsealed preparation stage.");
                if (RaidQaAnyCombat()) break;
                if (args.Length < 3 || !args[2].StartsWith('.'))
                    return RaidQaFail("Deferred setup requires an explicit game command.");
                done = SendGmCommand(string.Join(" ", args.Skip(2)), "raid-qa-deferred-setup");
                break;
            case "select":
                ulong guid = ulong.Parse(args[2], CultureInfo.InvariantCulture);
                if (_entities.TryGet(guid, out var member) && member.IsPlayer &&
                    ResolveUnitName(guid).Equals(args[3], StringComparison.OrdinalIgnoreCase))
                { CommitSelection(guid, false); done = true; }
                break;
            case "select-pet":
                if (_raidQaStage != CommanderRaidAttemptStage.Recovery || ControlledGuid != LocalPlayerGuid || RaidQaAnyCombat())
                    return RaidQaFail("Selected-pet preparation requires Testwar's out-of-combat recovery control.");
                ulong petOwnerToSelect = ulong.Parse(args[2], CultureInfo.InvariantCulture);
                if (petOwnerToSelect is < 154 or > 160) return RaidQaFail("Pet owner is outside the authorized roster.");
                var ownedPetSubjects = _entities.Units.Where(e => GuidInfo.IsPet(e.Guid) && e.Fields.SummonedBy == petOwnerToSelect).ToArray();
                if (ownedPetSubjects.Length == 1) { CommitSelection(ownedPetSubjects[0].Guid, false); done = true; }
                break;
            case "item-owner":
                ulong itemOwner = ulong.Parse(args[2], CultureInfo.InvariantCulture);
                if (!CommanderRaidAttemptLaw.ConsumablePreparationAllowed(_raidQaStage, itemOwner, false))
                    return RaidQaFail("Consumable control requires an authorized character during recovery.");
                if (RaidQaAnyCombat()) break;
                if (ControlledGuid != itemOwner)
                {
                    if (!_raidQaSent) { RequestPossess(itemOwner); _raidQaSent = true; }
                    break;
                }
                done = CanAuthorControlledOrSelf && _entities.TryGet(itemOwner, out var itemSubject) && itemSubject.IsPlayer && !itemSubject.IsDead;
                break;
            case "consume":
                if (!CommanderRaidAttemptLaw.ConsumablePreparationAllowed(_raidQaStage, ControlledGuid, false))
                    return RaidQaFail("Consumables require an authorized controlled character during recovery.");
                if (RaidQaAnyCombat()) break;
                uint consumableEntry = uint.Parse(args[2], CultureInfo.InvariantCulture);
                uint consumableAura = uint.Parse(args[3], CultureInfo.InvariantCulture);
                if (!_entities.TryGet(ControlledGuid, out var consumableOwner) || !CanAuthorControlledOrSelf) break;
                // Possession authority can arrive before the refreshed bag snapshot. Never use
                // the previous possession's stack count as the consumption baseline.
                if (ControlledGuid != LocalPlayerGuid)
                {
                    if (_raidQaConsumableSnapshotRequestedAt == 0)
                    {
                        _raidQaConsumableSnapshotRequestedAt = NowSeconds();
                        _net.SuiMemberFacts([ControlledGuid]);
                        break;
                    }
                    if (!_suiSnapshotAtByBot.TryGetValue(ControlledGuid, out double consumableSnapshotAt) ||
                        consumableSnapshotAt <= _raidQaConsumableSnapshotRequestedAt) break;
                    // Normal use refreshes can precede a cast's completion. Keep requesting
                    // received inventory while waiting for the exact decrement and aura.
                    if (_raidQaSent && NowSeconds() >= _raidQaConsumableNextRefresh)
                    {
                        _net.SuiMemberFacts([ControlledGuid]);
                        _raidQaConsumableNextRefresh = NowSeconds() + 1;
                    }
                }
                var consumableCopies = EnumerateActionItemCopies(consumableOwner, consumableEntry).Where(i => !i.Worn).ToArray();
                uint consumableCount = (uint)consumableCopies.Sum(i => (long)i.Item.Fields.ItemStackCount);
                if (_raidQaSent)
                {
                    bool auraObserved = consumableOwner.Fields.Auras().Any(a => a.SpellId == consumableAura);
                    done = CommanderRaidAttemptLaw.ConsumedExactlyOne(_raidQaConsumableBefore, consumableCount, auraObserved);
                    if (done) WriteRaidQaEvent("consumable-used", new { owner = ControlledGuid, item = consumableEntry, aura = consumableAura,
                        before = _raidQaConsumableBefore, after = consumableCount, auraObserved });
                    break;
                }
                if (consumableCount == 0 || consumableCopies.Length == 0 || _items is null) break;
                var consumableCopy = consumableCopies[0];
                _items.Require(consumableEntry, consumableCopy.Item.Guid, _net);
                if (!_items.TryGet(consumableEntry, out var consumableTemplate) || consumableTemplate is null) break;
                if (consumableTemplate.UseSpellId != consumableAura || consumableTemplate.SpellCharges0 != -1)
                    return RaidQaFail("The prepared consumable must be a one-use item with the expected aura spell.");
                if (consumableOwner.Fields.UnitStandState != StandStateUiLaw.Stand && !TrySetLocalStandState(StandStateUiLaw.Stand)) break;
                _raidQaConsumableBefore = consumableCount;
                if (SendItemUse(consumableCopy.Bag, consumableCopy.Slot, consumableCopy.Item, consumableTemplate))
                {
                    _raidQaSent = true;
                    WriteRaidQaEvent("consumable-use-request", new { owner = ControlledGuid, item = consumableEntry,
                        aura = consumableAura, before = consumableCount });
                }
                break;
            case "pet-owner":
                if (_raidQaStage != CommanderRaidAttemptStage.Recovery)
                    return RaidQaFail("Pet readiness inspection requires recovery.");
                if (RaidQaAnyCombat()) break; // Wait without switching control during temporary combat.
                ulong petOwner = ulong.Parse(args[2], CultureInfo.InvariantCulture);
                if (petOwner is < 154 or > 160) return RaidQaFail("Only the seven authorized pet owners may be inspected.");
                if (ControlledGuid != petOwner)
                {
                    if ((_controlState is ControlState.OwnChar or ControlState.FreeCam) &&
                        NowSeconds() >= _raidQaNextControlRequest)
                    {
                        // A transient refusal returns to ordinary control. Retry
                        // through the production guard after its pending watchdog.
                        RequestPossess(petOwner);
                        _raidQaNextControlRequest = NowSeconds() + 3;
                        WriteRaidQaEvent("pet-control-request", new { owner = petOwner, state = _controlState.ToString() });
                    }
                    break;
                }
                done = _petGuid != 0 && _entities.TryGet(_petGuid, out var inspectedPet) && inspectedPet.Fields.SummonedBy == petOwner;
                break;
            case "pet-feed":
                if (_raidQaStage != CommanderRaidAttemptStage.Recovery || ControlledGuid is < 158 or > 160)
                    return RaidQaFail("Pet feeding requires an authorized hunter during recovery.");
                if (RaidQaAnyCombat())
                {
                    if (!_raidQaSent) WriteRaidQaEvent("pet-feed-wait-combat", new { bodies = _partyMembers.Select(m => m.Guid).Prepend(LocalPlayerGuid).Where(g => _entities.TryGet(g, out var e) && e.InCombat).ToArray() });
                    _raidQaSent = true; break; // Keep the deadline; send no feed cast while combat persists.
                }
                _raidQaSent = false;
                if (!_entities.TryGet(ControlledGuid, out var feedingOwner) || !TryGetControlledPet(out var feedingPet) ||
                    !GuidInfo.IsPet(feedingPet.Guid) || feedingPet.Fields.SummonedBy != ControlledGuid ||
                    feedingPet.IsDead || feedingPet.Fields.Health == 0) break;
                uint foodEntry = uint.Parse(args[2], CultureInfo.InvariantCulture);
                uint happinessGoal = uint.Parse(args[3], CultureInfo.InvariantCulture);
                if (happinessGoal == 0 || happinessGoal > feedingPet.Fields.MaxPower(4))
                    return RaidQaFail("Happiness goal must fit the received pet power range.");
                if (feedingPet.Fields.Power(4) >= happinessGoal)
                {
                    WriteRaidQaEvent("pet-fed", new { owner = ControlledGuid, pet = feedingPet.Guid, happiness = feedingPet.Fields.Power(4), happinessGoal });
                    done = true; break;
                }
                if (NowSeconds() < _raidQaNextFeed) break;
                var food = EnumerateActionItemCopies(feedingOwner, foodEntry).FirstOrDefault(i => !i.Worn).Item;
                uint feedingSpell = FeedPetSpell();
                if (food is null || !FeedPetLaw.CanFeed(feedingPet.Guid, _petGuid, feedingPet.Fields.CreatedBySpell,
                    feedingPet.Fields.CreatedBy, ControlledGuid, feedingSpell, food.Guid))
                    return RaidQaFail("The controlled hunter needs a learned Feed Pet spell and carried food for its own pet.");
                if (feedingOwner.Fields.UnitStandState != StandStateUiLaw.Stand && !TrySetLocalStandState(StandStateUiLaw.Stand)) break;
                CommitItemCast(feedingSpell, food.Guid);
                _raidQaNextFeed = NowSeconds() + 11;
                WriteRaidQaEvent("pet-feed-request", new { owner = ControlledGuid, pet = feedingPet.Guid, food = food.Guid, foodEntry, feedingSpell, happiness = feedingPet.Fields.Power(4) });
                break;
            case "pet-train":
            case "pet-autocast":
                if (_raidQaStage != CommanderRaidAttemptStage.Recovery || ControlledGuid is < 154 or > 160)
                    return RaidQaFail("Pet preparation requires an authorized owner during recovery.");
                // Rank replacement can briefly rebuild the received pet state. Do
                // not issue another command until ownership/life/combat is known again.
                if (RaidQaAnyCombat() || !GuidInfo.IsPet(_petGuid) || !_entities.TryGet(_petGuid, out var petSubject) ||
                    petSubject.Fields.SummonedBy != ControlledGuid || petSubject.IsDead || petSubject.Fields.Health == 0) break;
                uint petSpell = uint.Parse(args[2], CultureInfo.InvariantCulture);
                if (args[1] == "pet-train")
                {
                    uint learned = uint.Parse(args[3], CultureInfo.InvariantCulture);
                    if (_spellCatalog?.TryGet(petSpell, out var teacher) != true || teacher.EffectIds is null || teacher.EffectTriggerSpells is null ||
                        !teacher.EffectIds.Select((effect, i) => (effect, i)).Any(e => e.effect is 36 or 57 &&
                            e.i < teacher.EffectTriggerSpells.Length && teacher.EffectTriggerSpells[e.i] == learned))
                        return RaidQaFail("Pet training requires a matching learned-spell effect.");
                    done = _petBookSpells.Any(v => (v & 0xFFFFFF) == learned);
                    if (!done && !_raidQaSent)
                    {
                        if (!ActionsFor(ControlledGuid).KnownSpells.Contains(petSpell)) return RaidQaFail("Owner does not know the pet training spell.");
                        CommitSelection(_petGuid, false); TryCast(petSpell); _raidQaSent = true;
                        WriteRaidQaEvent("pet-training-request", new { owner = ControlledGuid, pet = _petGuid, teacher = petSpell, learned });
                    }
                }
                else if (!_raidQaSent)
                {
                    if (!_petBookSpells.Any(v => (v & 0xFFFFFF) == petSpell)) return RaidQaFail("Pet does not know this autocast spell.");
                    bool enabled = args[3] == "on";
                    if (args[3] is not ("on" or "off")) return RaidQaFail("Expected explicit pet autocast on/off.");
                    done = _net.PetSpellAutocast(_petGuid, petSpell, enabled); _raidQaSent = done;
                    WriteRaidQaEvent("pet-autocast-request", new { owner = ControlledGuid, pet = _petGuid, spell = petSpell, enabled, sent = done });
                }
                break;
            case "convert":
                if (!_raidQaSent && _partyInGroup) { _net.GroupRaidConvert(); _raidQaSent = true; }
                done = _partyGroupType == 1;
                break;
            case "roster":
                if (_partyMembers.Count == 39 && (_partyMembers.Any(m => !IsAuthorizedRaidQaBot(m.Guid)) ||
                    _partyMembers.Select(m => m.Guid).Distinct().Count() != 39))
                    return RaidQaFail("The roster differs from the owner's exact 39 authorized bots.");
                if (!_raidQaSent && _partyMembers.Count == 39 && _partyLeaderGuid != LocalPlayerGuid)
                    _raidQaSent = RequestPartyLeadClaim();
                done = _partyMembers.Count == 39 && _partyLeaderGuid == LocalPlayerGuid;
                break;
            case "freeview":
                if (!_freeView && (_controlState is ControlState.OwnChar or ControlState.Possessing) &&
                    NowSeconds() >= _raidQaNextControlRequest)
                {
                    // A startup capability/ACK may not be ready on the first try.
                    // Retry only after normal pending control has resolved.
                    ToggleFreeView(); _raidQaNextControlRequest = NowSeconds() + 3;
                    WriteRaidQaEvent("command-view-request", new { state = _controlState.ToString() });
                }
                done = _freeView;
                break;
            case "park":
                if (_raidQaStage != CommanderRaidAttemptStage.Recovery)
                    return RaidQaFail("Parking the raid requires preparation/recovery.");
                if (ControlledGuid != LocalPlayerGuid && _controlState == ControlState.Possessing &&
                    NowSeconds() >= _raidQaNextControlRequest)
                {
                    RequestControlRelease(toFreecam: true);
                    _raidQaNextControlRequest = NowSeconds() + 3;
                }
                else if (!_freeView && _controlState == ControlState.OwnChar &&
                    NowSeconds() >= _raidQaNextControlRequest)
                {
                    ToggleFreeView(); _raidQaNextControlRequest = NowSeconds() + 3;
                }
                if (!_freeView || ControlledGuid != LocalPlayerGuid) break;
                var parkedBots = _partyMembers.Select(m => m.Guid).Distinct().ToArray();
                if (LocalPlayerGuid != 787 || parkedBots.Length != 39 || parkedBots.Any(g => !IsAuthorizedRaidQaBot(g)))
                    return RaidQaFail("Parking requires Testwar and the exact authorized 39 bots.");
                var parkedRaid = parkedBots.Append(LocalPlayerGuid).ToArray();
                if (!_raidQaSent)
                {
                    if (!TrySendLiveSuiOrder(2, parkedRaid, 0, 0, 0, 0) ||
                        !TrySendLiveSuiOrder(6, parkedRaid, 0, 0, 0, 0))
                        return RaidQaFail("The whole-raid Hold was not sent.");
                    _raidQaSent = true;
                }
                done = parkedRaid.All(g => _suiChain.TryGetValue(g, out var parkedChain) && parkedChain.Item1 == 1);
                break;
            case "drive":
                if (!_raidQaSent && _freeView) { ToggleFreeView(); _raidQaSent = true; }
                else if (_controlState == ControlState.Possessing && NowSeconds() >= _raidQaNextControlRequest)
                {
                    // Retry only after the ordinary control watchdog has returned
                    // from ReleasePending. The same guarded release owns every send.
                    RequestControlRelease(toFreecam: false);
                    _raidQaNextControlRequest = NowSeconds() + 3;
                    WriteRaidQaEvent("control-release-request", new { state = _controlState.ToString(), controlled = ControlledGuid });
                }
                done = !_freeView && ControlledGuid == LocalPlayerGuid;
                break;
            case "hold":
                var subjects = _partyMembers.Select(m => m.Guid).Distinct().ToArray();
                if (subjects.Length != 39 || subjects.Any(g => !IsAuthorizedRaidQaBot(g)))
                    return RaidQaFail("Hold requires the exact authorized 39-bot roster.");
                if (_freeView && !_raidQaSent)
                {
                    // The deployed Core batches one roster publication per group/order.
                    if (!TrySendLiveSuiOrder(2, subjects, 0, 0, 0, 0) ||
                        !TrySendLiveSuiOrder(6, subjects, 0, 0, 0, 0))
                        return RaidQaFail("The bulk Hold command was not sent.");
                    _raidQaSent = true;
                }
                done = _raidQaSent && subjects.All(g => _suiChain.TryGetValue(g, out var chain) && chain.Item1 == 1);
                break;
            case "pause":
            case "clear":
                OpenPartyTactics(LocalPlayerGuid);
                if (_commanderRaidStatus is null || _commanderRaidPending != 0) break;
                byte desired = args[1] == "pause" ? (byte)3 : (byte)0;
                done = _commanderRaidStatus.State == desired || args[1] == "pause" && _commanderRaidStatus.State != 2;
                if (!done && NowSeconds() >= _raidQaNextControlRequest)
                {
                    // Pending requests are excluded above. A lost response must
                    // not make an idempotent Pause/Clear a one-shot dead end.
                    SendCommanderRaid(args[1] == "pause" ? CommanderRaidOperation.Pause : CommanderRaidOperation.Clear);
                    _raidQaNextControlRequest = NowSeconds() + 3;
                }
                break;
            case "assign":
                if (_raidQaStage != CommanderRaidAttemptStage.Preparation)
                    return RaidQaFail("Assignment requires unsealed preparation.");
                OpenPartyTactics(LocalPlayerGuid);
                if (!_raidQaSent)
                {
                    _commanderEncounters = CommanderEncounterCatalog.Load(Path.Combine(_config.RepoRoot, "encounter-definitions"));
                    string encounterId = args.Length > 2 ? args[2] : CommanderEncounterCatalog.Default.Id;
                    var loadedEncounter = _commanderEncounters.FirstOrDefault(d => d.Id == encounterId);
                    if (loadedEncounter is null) return RaidQaFail("The requested encounter definition is not loaded.");
                    _commanderRaidLoaded = true;
                    _commanderRaidDraft = CommanderRaidPlan.ForEncounter(loadedEncounter) with { MainGuid = LocalPlayerGuid, MainRole = CommanderRaidRole.MainTank };
                    File.WriteAllText(Path.Combine(_raidQaAttemptDirectory!, "encounter.json"), loadedEncounter.ToJson());
                    File.WriteAllText(Path.Combine(_raidQaAttemptDirectory!, "effective-tuning.json"), JsonSerializer.Serialize(CommanderRaidTuningLaw.Effective(loadedEncounter.Tuning)));
                    _raidQaSent = true;
                }
                RequestPartyMemberFacts("live raid acceptance");
                var roster = CommanderRaidRoster(fresh: true);
                if (roster.Count == 40 && roster.All(m => m.FactsReady && m.Alive))
                {
                    AutoAssignCommanderRaid(roster, LocalPlayerGuid);
                    var errors = CommanderRaidPlanLaw.Validate(_commanderRaidDraft, roster);
                    if (errors.Count > 0) return RaidQaFail(string.Join("; ", errors));
                    done = true;
                }
                break;
            case "apply":
                done = _raidQaSent && _commanderRaidStatus?.State == 1 && ReferenceEquals(_commanderRaidApplied, _commanderRaidDraft);
                if (!done && _commanderRaidAvailable && _commanderRaidPending == 0 &&
                    NowSeconds() >= _raidQaNextControlRequest)
                {
                    SendCommanderRaid(CommanderRaidOperation.Apply);
                    _raidQaSent |= _commanderRaidPending != 0;
                    _raidQaNextControlRequest = NowSeconds() + 3;
                }
                break;
            case "arm":
                if (!_raidQaSent && _commanderRaidPending == 0 && ReferenceEquals(_commanderRaidApplied, _commanderRaidDraft))
                { SendCommanderRaid(CommanderRaidOperation.Arm); _raidQaSent = _commanderRaidPending != 0; }
                done = _raidQaSent && _commanderRaidStatus?.State == 2;
                break;
            case "geometry":
                if (_raidQaStage is not (CommanderRaidAttemptStage.Recovery or CommanderRaidAttemptStage.Preparation) || RaidQaAnyCombat())
                    return RaidQaFail("Geometry observation requires unsealed, out-of-combat preparation.");
                // A solo survey reads encounter bounds without changing the live draft,
                // requesting assignments or requiring the other39 to enter an unverified room.
                var surveyEncounter = args.Length == 3
                    ? CommanderEncounterCatalog.Load(Path.Combine(_config.RepoRoot, "encounter-definitions")).FirstOrDefault(d => d.Id == args[2])
                    : _commanderRaidDraft.Encounter;
                if (args.Length > 3 || surveyEncounter is null) return RaidQaFail("The requested survey encounter definition is not loaded.");
                if (_collision is null || _config.Start.Map != surveyEncounter.MapId) return RaidQaFail("Geometry observation requires the requested raid room.");
                var floorSamples = new List<object>();
                var surveyBounds = surveyEncounter.Bounds;
                float surveyStep = Math.Max(2, Math.Max(surveyBounds.Max[0] - surveyBounds.Min[0], surveyBounds.Max[1] - surveyBounds.Min[1]) / 100);
                for (float x = surveyBounds.Min[0]; x <= surveyBounds.Max[0]; x += surveyStep)
                    for (float y = surveyBounds.Min[1]; y <= surveyBounds.Max[1]; y += surveyStep)
                    {
                        var floor = _collision.Raycast(new Vector3(x, y, surveyBounds.Max[2] + 10), -Vector3.UnitZ, surveyBounds.Max[2] - surveyBounds.Min[2] + 20);
                        if (floor is { } f) floorSamples.Add(new { point = f.Point, normal = f.Normal });
                    }
                File.WriteAllText(Path.Combine(_raidQaAttemptDirectory ?? Path.GetFullPath(_liveRunOptions.OutputDirectory), "geometry.json"),
                    JsonSerializer.Serialize(new { encounter = surveyEncounter.Id, map = _config.Start.Map, floorSamples,
                        creatures = _entities.Units.Where(e => e.Entry != 0).Select(e => new { e.Guid, e.Entry, e.Position, e.Orientation,
                            e.Fields.CombatReach, e.Fields.BoundingRadius, e.Fields.Health, e.InCombat }) },
                        new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
                done = true;
                break;
            case "prepull":
                var ready = CommanderRaidRoster(fresh: true);
                if (_raidQaStage != CommanderRaidAttemptStage.Preparation || _serverGmMode != true || _commanderRaidStatus?.State != 2 || !CommanderRaidAttemptLaw.AuthorizedRoster(ready.Select(m => m.Guid)) || ready.Any(m => !m.Alive || !m.FactsReady))
                    return RaidQaFail("Pre-pull check requires an armed, living forty-character raid in preparation mode.");
                if (_entities.Units.Any(e => e.Entry == _commanderRaidDraft.BossEntry && e.InCombat))
                    return RaidQaFail("The boss was pulled before preparation finished.");
                done = ready.All(m => _entities.TryGet(m.Guid, out var e) && e.Fields.MaxHealth > 0 && e.Fields.Health == e.Fields.MaxHealth && !e.InCombat);
                if (done)
                {
                    _raidQaStage = CommanderRaidAttemptStage.Ready;
                    WriteRaidQaEvent("ready", new { roster = ready, plan = _commanderRaidDraft, effectiveTuning = CommanderRaidTuningLaw.Effective(_commanderRaidDraft.Encounter.Tuning),
                        status = _commanderRaidStatus, map = _config.Start.Map,
                        observed = ready.Select(m => _entities.TryGet(m.Guid, out var e) ? new {
                            guid = m.Guid, health = e.Fields.Health, maxHealth = e.Fields.MaxHealth,
                            position = e.Position, combatReach = e.Fields.CombatReach,
                            skills = e.Fields.PlayerSkills().Select(k => new { id = k.SkillId, value = k.Value }).ToArray()
                        } : null) });
                }
                break;
            case "begin":
                var pullBoss = _entities.Units.FirstOrDefault(e => e.Guid == _commanderRaidStatus?.BossGuid && e.Entry == _commanderRaidDraft.BossEntry);
                if (pullBoss is null) return RaidQaFail("The selected encounter boss is not visible.");
                if (!_raidQaSent)
                {
                    var pullRoster = CommanderRaidRoster(fresh: true);
                    string? refusal = CommanderRaidAttemptLaw.StartRefusal(_raidQaStage, pullRoster.Select(m => m.Guid),
                        pullRoster.All(m => m.Alive && m.FactsReady && _entities.TryGet(m.Guid, out var e) &&
                            e.Fields.MaxHealth > 0 && e.Fields.Health == e.Fields.MaxHealth && !e.Fields.PlayerIsGhost),
                        _commanderRaidStatus?.State == 2 && ReferenceEquals(_commanderRaidApplied, _commanderRaidDraft),
                        _serverGmMode == true, !pullBoss.IsDead && pullBoss.Fields.MaxHealth > 0 && pullBoss.Fields.Health == pullBoss.Fields.MaxHealth,
                        RaidQaAnyCombat() || pullBoss.InCombat);
                    if (refusal is not null) return RaidQaFail(refusal);
                    // Normal aggro may begin as soon as GM-off reaches Core. Seal before sending it.
                    _raidQaInitialHealth = _commanderRaidStatus!.BossHealth; _raidQaBossGuid = pullBoss.Guid;
                    WriteRaidQaEvent("pull-request", new { bossGuid = pullBoss.Guid, bossEntry = pullBoss.Entry,
                        map = _config.Start.Map, health = _commanderRaidStatus!.BossHealth, maxHealth = _commanderRaidStatus.BossMaxHealth });
                    if (!SendGmCommand(".gm off", "raid-qa-begin")) return RaidQaFail("GM-off could not be sent.");
                    _raidQaStage = CommanderRaidAttemptStage.PullPending;
                    _raidQaSent = true;
                    SendGmCommand(".gm", "raid-qa-confirm-ordinary-mode");
                }
                done = _serverGmMode == false;
                if (done)
                {
                    _raidQaStage = CommanderRaidAttemptStage.Fighting;
                    WriteRaidQaEvent("pull", new { bossGuid = _raidQaBossGuid, gmOff = true });
                }
                break;
            case "fight":
                return AdvanceCommanderRaidBattle();
            case "report":
                string output = _raidQaAttemptDirectory ?? Path.GetFullPath(_liveRunOptions.OutputDirectory);
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, args.Length > 2 ? Path.GetFileName(args[2]) + ".json" : "raid.json"),
                    JsonSerializer.Serialize(new { main = LocalPlayerGuid, controlled = ControlledGuid, map = _config.Start.Map,
                                                creator = CreatorInWorld, roster = CommanderRaidRoster(fresh: true),
                        observed = _partyMembers.Select(m => m.Guid).Prepend(LocalPlayerGuid).Distinct().Select(g =>
                            _entities.TryGet(g, out var e) ? new { guid = g, health = e.Fields.Health, maxHealth = e.Fields.MaxHealth,
                                position = e.Position, orientation = e.Orientation, combatReach = e.Fields.CombatReach, inCombat = e.InCombat, ghost = e.Fields.PlayerIsGhost, auras = e.Fields.Auras().Select(a => a.SpellId).ToArray(), skills = e.Fields.PlayerSkills().Select(s => new { id = s.SkillId, value = s.Value }).ToArray() } : null),
                        controlledSupplies = new { owner = ControlledGuid, ammunitionEntry = 2516u,
                            ammunitionCount = _entities.TryGet(ControlledGuid, out var suppliesOwner)
                                ? EnumerateActionItemCopies(suppliesOwner, 2516).Where(i => !i.Worn).Sum(i => (long)i.Item.Fields.ItemStackCount) : 0 },
                        controlledPet = new { guid = _petGuid, actions = _petActions, book = _petBookSpells,
                            owner = _entities.TryGet(_petGuid, out var controlledPet) ? controlledPet.Fields.SummonedBy : null },
                        ownedPets = _entities.Units.Where(e => e.Fields.SummonedBy is >= 154 and <= 160)
                            .Select(e => new { e.Guid, e.Entry, owner = e.Fields.SummonedBy, health = e.Fields.Health,
                                maxHealth = e.Fields.MaxHealth, level = e.Fields.Level, happiness = e.Fields.Power(4), maxHappiness = e.Fields.MaxPower(4), loyalty = e.Fields.PetLoyaltyLevel, e.Position }).ToArray(),
                        draft = _commanderRaidDraft,
                        status = _commanderRaidStatus, message = _commanderRaidMessage }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
                done = true;
                break;
            default: return RaidQaFail("Unknown raid QA operation.");
        }
        if (done)
        { Console.WriteLine("[raid-live-qa] PASS " + line); _raidQaCommand = ""; return true; }
        if (NowSeconds() >= _raidQaDeadline)
            return RaidQaFail($"Timeout: {line}; members={_partyMembers.Count}; freeview={_freeView}; control={_controlState}; roster={_suiRoster.Count}; capability={_commanderRaidAvailable}; {_commanderRaidMessage}");
        return false;
    }

    // Drives only the explicitly authorized human through normal movement and spell APIs.
    // The raid executor must independently control the other 39 actual server players.
    private bool AdvanceCommanderRaidBattle()
    {
        double now = NowSeconds();
        string abortPath = Path.Combine(Path.GetFullPath(_liveRunOptions!.OutputDirectory), "abort-attempt.txt");
        if (File.Exists(abortPath))
        {
            string reason = File.ReadAllText(abortPath);
            File.Move(abortPath, abortPath + ".consumed-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            return RaidQaFail("Observation aborted: " + reason[..Math.Min(reason.Length, 256)]);
        }
        if (_freeView || ControlledGuid != LocalPlayerGuid || CreatorInWorld || _config.Start.Map != _commanderRaidDraft.MapId || _serverGmMode != false)
            return RaidQaFail($"Combat requires Testwar under ordinary control in the real instance with GM mode confirmed off. map={_config.Start.Map}, expected={_commanderRaidDraft.MapId}, controlled={ControlledGuid}, freeview={_freeView}, creator={CreatorInWorld}, gm={_serverGmMode}");
        if (!_entities.TryGet(LocalPlayerGuid, out var main) || _controller is null) return false;
        var pose = _controller.Position;
        if (!float.IsFinite(pose.X) || !float.IsFinite(pose.Y) || !float.IsFinite(pose.Z) ||
            pose.Z < CommanderEncounterLaw.Point(_commanderRaidDraft.Encounter.Bounds.Min).Z)
            return RaidQaFail("The controlled body left the encounter floor envelope.");
        var status = _commanderRaidStatus;
        if (status is null || !CommanderRaidGuidanceLaw.Fresh(now - _commanderRaidStatusAt))
        {
            _liveHeld.Clear(); StopAttack("raid-qa-await-fresh-status");
            if (now >= _raidQaNextGuidanceReport)
            {
                _raidQaNextGuidanceReport = now + 1;
                WriteRaidQaEvent("guidance-wait", new { reason = "stale-status", age = now - _commanderRaidStatusAt,
                    guidance = status?.Actors.FirstOrDefault(a => a.Guid == LocalPlayerGuid)?.Guidance });
            }
            if (now >= _raidQaDeadline) return RaidQaFail("Authoritative encounter status stopped arriving.");
            return false;
        }
        var boss = _entities.Units.FirstOrDefault(e => e.Guid == _raidQaBossGuid && e.Entry == _commanderRaidDraft.BossEntry);
        if (!_raidQaSent)
        {
            if (_raidQaStage != CommanderRaidAttemptStage.Fighting || status.BossGuid != _raidQaBossGuid)
                return RaidQaFail("Begin must seal and acknowledge this exact boss pull first.");
            _raidQaSent = true; _raidQaStarted = now;
            _raidQaNextCast = 0; _raidQaNextSample = 0; _raidQaTankSince = 0;
            _raidQaSawCombat = false; _raidQaResetSince = 0; _raidQaBossHidden = false;
            if (boss is not null) CommitSelection(boss.Guid, false);
        }
        if (status.BossGuid != _raidQaBossGuid) return RaidQaFail("The authoritative boss identity changed during the attempt.");
        if (now >= _raidQaNextSample)
        {
            _raidQaNextSample = now + 5;
            var row = new { time = now - _raidQaStarted, mainHealth = main.Fields.Health,
                rage = main.Fields.Power(1), mainAuras = main.Fields.Auras().Select(a => a.SpellId).ToArray(), position = _controller.Position, bossHealth = status.BossHealth, bossMaxHealth = status.BossMaxHealth,
                bossPosition = boss?.Position, bossOrientation = boss?.Orientation, bossReach = boss?.Fields.CombatReach, bossCombat = status.BossInCombat, bossVisible = boss is not null, bossTarget = boss?.Fields.Target,
                receivedEncounterUnits = _entities.Units.Where(e => e.Guid == _raidQaBossGuid || _commanderRaidDraft.Encounter.AddEntries.Contains(e.Entry))
                    .Take(256).Select(e => new { guid = e.Guid, entry = e.Entry, health = e.Fields.Health, maxHealth = e.Fields.MaxHealth,
                        position = e.Position, orientation = e.Orientation, target = e.Fields.Target, inCombat = e.InCombat,
                        auras = e.Fields.Auras().Select(a => a.SpellId).ToArray() }).ToArray(),
                receivedNearbyHostiles = _entities.Units.Where(e => e.Entry != 0 && CanAttack(e))
                    .Take(256).Select(e => new { guid=e.Guid, entry=e.Entry, health=e.Fields.Health,
                        position=e.Position, target=e.Fields.Target, inCombat=e.InCombat }).ToArray(),
                receivedRaidUnits = _entities.Units.Where(e => _commanderRaidDraft.Assignments.Any(a => a.Guid == e.Guid))
                    .Select(e => new { guid = e.Guid, health = e.Fields.Health, maxHealth = e.Fields.MaxHealth, mana = e.Fields.Power(0),
                        position = e.Position, target = e.Fields.Target, inCombat = e.InCombat,
                        auras = e.Fields.Auras().Select(a => a.SpellId).ToArray() }).ToArray(),
                alive = status.Actors.Count(m => m.Known && m.Alive), status };
            string json = JsonSerializer.Serialize(row, new JsonSerializerOptions { IncludeFields = true });
            File.AppendAllText(Path.Combine(_raidQaAttemptDirectory ?? Path.GetFullPath(_liveRunOptions!.OutputDirectory), "battle.jsonl"), json + Environment.NewLine);
            SendGmCommand(".list threat", "raid-qa-read-only-threat");
            Console.WriteLine($"[raid-battle] seconds={now-_raidQaStarted:F0} main={main.Fields.Health} boss={status.BossHealth}/{status.BossMaxHealth} alive={row.alive} state={_commanderRaidStatus?.State} phase={_commanderRaidStatus?.Phase}");
        }
        bool deathComplete = _commanderRaidDraft.Encounter.Mechanics.Completion == "death" && status.BossKnown && !status.BossAlive && status.BossHealth == 0;
        bool surrenderComplete = _commanderRaidDraft.Encounter.Mechanics.Completion == "friendlySurrender" && status.BossKnown && status.BossAlive &&
            status.BossFriendly && status.BossNonAttackable && !status.BossInCombat && _raidQaSawCombat;
        if ((deathComplete || surrenderComplete) && _raidQaInitialHealth > 0)
        {
            _liveHeld.Clear(); StopAttack("raid-qa-boss-dead");
            if (_commanderRaidStatus?.State != 4) return false;
            _raidQaStage = CommanderRaidAttemptStage.Finished;
            WriteRaidQaEvent("complete", new { bossGuid = status.BossGuid, bossHealth = status.BossHealth,
                bossDead = !status.BossAlive, completionPredicate = _commanderRaidDraft.Encounter.Mechanics.Completion, status });
            Console.WriteLine("[raid-live-qa] PASS native completion predicate and executor completion observed");
            _raidQaCommand = ""; return true;
        }
        if (status.BossInCombat) { _raidQaSawCombat = true; _raidQaResetSince = 0; }
        else if (_raidQaSawCombat && status.BossKnown && status.BossMaxHealth > 0 && status.BossHealth == status.BossMaxHealth)
        {
            if (_raidQaResetSince == 0) _raidQaResetSince = now;
            if (now - _raidQaResetSince >= 8) return RaidQaFail("The boss evaded and reset; the attempt ended without a kill.");
        }
        else _raidQaResetSince = 0;
        if (_commanderRaidStatus?.State != 2) return RaidQaFail("The encounter stopped before a boss kill.");
        if (main.IsDead || main.Fields.PlayerIsGhost)
        {
            _liveHeld.Clear(); StopAttack("raid-qa-main-dead");
            if (status.Actors.Count == 40 && status.Actors.All(m => m.Known && !m.Alive))
                return RaidQaFail("All forty characters died during the actual encounter.");
            if (now >= _raidQaDeadline) return RaidQaFail("The remaining raid exceeded the encounter time limit.");
            return false; // Surviving bots can still earn a legitimate kill; keep recording.
        }
        if (now >= _raidQaDeadline) return RaidQaFail("The real encounter exceeded 15 minutes.");
        if (main.HealthFraction < .5f && now >= _raidQaNextPotion)
        {
            _raidQaNextPotion = now + 1;
            UseItemAction(13446); // Carried Major Healing Potion; normal item/category cooldown and consumption.
        }
        if (main.HealthFraction < .5f && now >= _raidQaNextDefenseReport)
        {
            _raidQaNextDefenseReport = now + 1;
            var defensiveActions = ActionsFor(LocalPlayerGuid);
            var candidates = new List<object>();
            foreach (string name in new[] { "Last Stand", "Shield Wall" })
            {
                uint id = defensiveActions.KnownSpells.Where(id => _spellCatalog?.TryGet(id, out var info) == true && info.Name == name)
                    .OrderByDescending(id => _spellCatalog!.TryGet(id, out var info) ? info.SpellLevel : 0).FirstOrDefault();
                if (id == 0 || _spellCatalog?.TryGet(id, out var spell) != true)
                { candidates.Add(new { name, id, known = false }); continue; }
                bool canPay = ActorCanPaySpell(spell, main, out uint power, out uint cost);
                candidates.Add(new { name, id, known = true, canPay, power, cost,
                    cooldown = defensiveActions.CooldownRemaining(id, 0, spell, now),
                    recovery = defensiveActions.CooldownRemaining(id, now, spell.Category),
                    reactive = SpellReactiveLaw.Refusal(spell, main, boss)?.ToString(),
                    target = ResolveCastTarget(spell, 0).Kind.ToString() });
            }
            WriteRaidQaEvent("defensive-readiness", new { health = main.Fields.Health, maximum = main.Fields.MaxHealth,
                stand = main.Fields.UnitStandState, nextCast = _raidQaNextCast - now,
                controlLocked = VanillaSelfControlLocksMover, candidates });
        }
        var guidance = CommanderRaidGuidanceLaw.Current(status, LocalPlayerGuid, _commanderRaidDraft.Encounter, now - _commanderRaidStatusAt);
        var guidanceActor = status.Actors.FirstOrDefault(a => a.Guid == LocalPlayerGuid);
        if (guidance is null && (guidanceActor is not { Known: true } || guidanceActor.Guidance.State != 0))
        {
            _liveHeld.Clear(); StopAttack("raid-qa-await-valid-guidance");
            if (now >= _raidQaNextGuidanceReport)
            {
                _raidQaNextGuidanceReport = now + 1;
                WriteRaidQaEvent("guidance-wait", new { reason = "invalid-guidance", age = now - _commanderRaidStatusAt,
                    guidance = guidanceActor?.Guidance });
            }
            return false; // Rejected advice is not permission to return to an ordinary station.
        }
        if (guidance is not null && guidance.State != 1)
        {
            _liveHeld.Clear(); StopAttack("raid-qa-shared-hazard-guidance");
            if (guidance.State is 2 or 4)
            {
                var delta = guidance.Waypoint - _controller.Position;
                _controller.Yaw = MathF.Atan2(delta.Y, delta.X);
                _window.Camera.Yaw = _controller.Yaw; _window.Camera.OrbitYaw = 0;
                if (delta.X * delta.X + delta.Y * delta.Y > .04f) _liveHeld.Add("W");
            }
            return false;
        }
        if (boss is null)
        {
            _liveHeld.Clear(); StopAttack("raid-qa-boss-temporarily-hidden");
            if (!_raidQaBossHidden) WriteRaidQaEvent("boss-visibility", new { visible = false, status.BossHealth, status.BossInCombat });
            _raidQaBossHidden = true;
            return false;
        }
        if (_raidQaBossHidden) WriteRaidQaEvent("boss-visibility", new { visible = true });
        _raidQaBossHidden = false;
        var phase = _commanderRaidDraft.Encounter.Phases.FirstOrDefault(p => p.Id == _commanderRaidStatus?.Phase);
        if (phase is { Melee: false })
        {
            _liveHeld.Remove("W"); _liveHeld.Remove("S"); StopAttack("raid-qa-air-phase");
            if (guidance?.State == 1) return false; // Keep a proven safe hold, without cancelling ground threat casts.
            var airPoint = _commanderRaidDraft.Assignments.First(a => a.Guid == LocalPlayerGuid).Air;
            var toAir = airPoint - _controller.Position;
            _controller.Yaw = MathF.Atan2(toAir.Y, toAir.X);
            _window.Camera.Yaw = _controller.Yaw; _window.Camera.OrbitYaw = 0;
            if (toAir.X * toAir.X + toAir.Y * toAir.Y > 4) _liveHeld.Add("W");
            return false;
        }
        // Ordinary learned ranged attacks can initiate without walking into a
        // neighbouring patrol. Once combat begins, normal tank guidance resumes.
        if (!boss.InCombat && TryRaidQaRangedPull(main, boss, now)) return false;
        // Acquire real melee contact and threat before returning to the wall station.
        // Healer aggro and knockbacks can move the boss away from that station.
        var toBoss = boss.Position - _controller.Position;
        float bossDistance = MathF.Sqrt(toBoss.X * toBoss.X + toBoss.Y * toBoss.Y);
        float meleeReach = MathF.Sqrt(WorldCursorUiLaw.UnitMeleeReachSquared(main.Fields.CombatReach, boss.Fields.CombatReach));
        bool tanking = boss.Fields.Target == LocalPlayerGuid;
        if (!tanking) _raidQaTankSince = 0;
        else if (_raidQaTankSince == 0) _raidQaTankSince = now;
        var station = CommanderEncounterLaw.Point(_commanderRaidDraft.Encounter.TankAnchor);
        bool returningRangedPull = _raidQaRangedPullGuid == boss.Guid;
        if (returningRangedPull && bossDistance <= meleeReach)
        { _raidQaRangedPullGuid = 0; returningRangedPull = false; }
        var movementGoal = guidance?.State == 1 ? _controller.Position : returningRangedPull
            ? CommanderRaidAttemptLaw.RangedPullReturnGoal(_controller.Position, boss.Position, station)
            : CommanderRaidAttemptLaw.TankMovementGoal(_controller.Position, boss.Position, station, meleeReach, tanking,
            (CommanderEncounterLaw.Point(_commanderRaidDraft.Encounter.Bounds.Min) + CommanderEncounterLaw.Point(_commanderRaidDraft.Encounter.Bounds.Max)) * .5f);
        var toStation = movementGoal - _controller.Position;
        _liveHeld.Remove("W"); _liveHeld.Remove("S");
        if (toStation.X * toStation.X + toStation.Y * toStation.Y > .04f)
        {
            bool towardBoss = toStation.X * toBoss.X + toStation.Y * toBoss.Y >= 0;
            _controller.Yaw = MathF.Atan2(toStation.Y, toStation.X) + (towardBoss ? 0 : MathF.PI);
            _liveHeld.Add(towardBoss ? "W" : "S");
        }
        else _controller.Yaw = MathF.Atan2(toBoss.Y, toBoss.X);
        _window.Camera.Yaw = _controller.Yaw; _window.Camera.OrbitYaw = 0;
        if (_attackTargetGuid != boss.Guid) CommitSelection(boss.Guid, true);
        if (now >= _raidQaNextCast && boss.InCombat)
        {
            _raidQaNextCast = now + .2;
            var actions = ActionsFor(LocalPlayerGuid);
            var priority = new List<string>();
            bool AuraNamed(WorldEntity unit, string name) => unit.Fields.Auras().Any(a =>
                _spellCatalog?.TryGet(a.SpellId, out var info) == true && info.Name == name);
            // React before a short burst can finish the tank, and stagger major
            // cooldowns instead of spending both on the same health dip.
            bool majorDefensive = AuraNamed(main, "Last Stand") || AuraNamed(main, "Shield Wall");
            if (main.HealthFraction < .5f && (!majorDefensive || main.HealthFraction < .15f))
                priority.AddRange(["Last Stand", "Shield Wall"]);
            if (main.Fields.Power(1) < 100 && main.HealthFraction > .65f) priority.Add("Bloodrage");
            if (tanking && bossDistance < meleeReach && !AuraNamed(main, "Shield Block")) priority.Add("Shield Block");
            priority.AddRange(["Revenge", "Shield Slam"]);
            if (main.Fields.Power(1) > 500) priority.Add("Heroic Strike");
            if (tanking && now - _raidQaTankSince >= 8 && !AuraNamed(boss, "Demoralizing Shout")) priority.Add("Demoralizing Shout");
            priority.Add("Sunder Armor");
            foreach (string name in priority)
            {
                uint spell = actions.KnownSpells.Where(id => _spellCatalog?.TryGet(id, out var info) == true && info.Name == name)
                    .OrderByDescending(id => _spellCatalog!.TryGet(id, out var info) ? info.SpellLevel : 0).FirstOrDefault();
                if (spell == 0 || _spellCatalog?.TryGet(spell, out var ability) != true ||
                    (ability.OnNextSwing && _queuedMeleeSpell == spell) ||
                    actions.IsOnCooldown(spell, 0, ability, now) ||
                    actions.CooldownRemaining(spell, now, ability.Category) > 0 ||
                    !ActorCanPaySpell(ability, main, out _, out _) ||
                    SpellReactiveLaw.Refusal(ability, main, boss) is not null ||
                    ResolveCastTarget(ability, 0).Kind == CastTargetKind.Refused) continue;
                TryCast(spell); break;
            }
        }
        return false;
    }

    private bool TryRaidQaRangedPull(WorldEntity main, WorldEntity boss, double now)
    {
        uint ammo = main.Fields.PlayerAmmoId;
        if (_spellCatalog is null || _items is null || ammo == 0 || CarriedAmmoCount(main, ammo) == 0 ||
            !_entities.TryGet(main.Fields.PlayerInventorySlot(17), out var rangedItem) ||
            !_items.TryGet(rangedItem.Entry, out var weapon) || weapon is null) return false;
        var actions = ActionsFor(LocalPlayerGuid);
        var shot = actions.KnownSpells.Select(id => _spellCatalog.TryGet(id, out var info) ? info : default)
            .Where(info => info.Id != 0 && !info.Passive && info.Ranged && info.Name.StartsWith("Shoot", StringComparison.Ordinal) &&
                info.EquippedItemClass == (int)weapon.Class && weapon.Subclass < 32 &&
                (info.EquippedItemSubclassMask & (1u << (int)weapon.Subclass)) != 0)
            .OrderBy(info => info.Id).FirstOrDefault();
        if (shot.Id == 0 || !_spellCatalog.TryGetRange(shot.RangeIndex, out var range) || range.Max < 15) return false;
        var goal = CommanderRaidAttemptLaw.RangedPullGoal(_controller!.Position, boss.Position, range.Max);
        _raidQaRangedApproachOrigin ??= _controller.Position;
        var outsider = _entities.Units.Where(e => e.Guid != boss.Guid && !e.IsDead && !e.InCombat && CanAttack(e) &&
                !_commanderRaidDraft.Encounter.AddEntries.Contains(e.Entry) &&
                !_commanderRaidDraft.Encounter.Objectives.Contains(e.Entry))
            .FirstOrDefault(e => !CommanderRaidAttemptLaw.RangedPullRouteClear(_controller.Position, goal, e.Position, 40));
        if (outsider is not null)
        {
            _liveHeld.Clear(); StopAttack("raid-qa-ranged-patrol-wait");
            var escape = CommanderRaidAttemptLaw.RangedPullRouteClear(_controller.Position, _controller.Position, outsider.Position, 40)
                ? _controller.Position : CommanderRaidAttemptLaw.RangedPullReturnGoal(_controller.Position, outsider.Position, _raidQaRangedApproachOrigin.Value);
            var retreat = escape - _controller.Position;
            if (retreat.X * retreat.X + retreat.Y * retreat.Y > .04f)
            {
                _controller.Yaw = MathF.Atan2(retreat.Y, retreat.X);
                _window.Camera.Yaw = _controller.Yaw; _window.Camera.OrbitYaw = 0; _liveHeld.Add("W");
            }
            if (now >= _raidQaNextGuidanceReport)
            {
                _raidQaNextGuidanceReport = now + 1;
                WriteRaidQaEvent("ranged-patrol-wait", new { outsider=outsider.Guid, outsider.Entry, outsider.Position, goal, escape });
            }
            return true;
        }
        var delta = goal - _controller.Position;
        var facing = boss.Position - _controller.Position;
        _liveHeld.Clear(); StopAttack("raid-qa-ranged-pull");
        _controller.Yaw = MathF.Atan2(facing.Y, facing.X);
        _window.Camera.Yaw = _controller.Yaw; _window.Camera.OrbitYaw = 0;
        if (delta.X * delta.X + delta.Y * delta.Y > .04f) _liveHeld.Add("W");
        else if (now >= _raidQaNextCast)
        {
            _raidQaNextCast = now + 2;
            _raidQaRangedPullGuid = boss.Guid;
            TryCast(shot.Id, boss.Guid);
            WriteRaidQaEvent("ranged-pull-request", new { spell = shot.Id, bossGuid = boss.Guid, ammo,
                distance = MathF.Sqrt(facing.X * facing.X + facing.Y * facing.Y), maximumRange = range.Max });
        }
        return true;
    }

    private bool RaidQaAnyCombat() => _partyMembers.Select(m => m.Guid).Prepend(LocalPlayerGuid)
        .Any(g => _entities.TryGet(g, out var e) && e.InCombat) ||
        _entities.Units.Any(e => e.Entry == _commanderRaidDraft.BossEntry && e.InCombat);

    private void WriteRaidQaEvent(string kind, object detail)
    {
        if (_raidQaAttemptDirectory is null) return;
        File.AppendAllText(Path.Combine(_raidQaAttemptDirectory, "events.jsonl"),
            JsonSerializer.Serialize(new { schema = 1, sequence = ++_raidQaEventSequence,
                utc = DateTime.UtcNow, monotonic = NowSeconds(), kind, stage = _raidQaStage.ToString(), detail },
                new JsonSerializerOptions { IncludeFields = true }) + Environment.NewLine);
    }

    private bool GuardCommanderRaidQaCommand(string command)
    {
        if (_liveRunOptions is null) return true;
        if (_raidQaAttemptDirectory is null)
        {
            if (!_raidQaPersistent) return true;
            RaidQaFail("Create a labeled raidqa attempt before sending preparation commands.");
            return false;
        }
        string? refusal = CommanderRaidAttemptLaw.CommandRefusal(_raidQaStage, command, RaidQaAnyCombat());
        if (refusal is null) return true;
        WriteRaidQaEvent("blocked-command", new { command, refusal,
            combatBodies = _partyMembers.Select(m => m.Guid).Prepend(LocalPlayerGuid).Distinct()
                .Where(g => _entities.TryGet(g, out var e) && e.InCombat).ToArray(),
            combatObjectives = _entities.Units.Where(e => e.Entry == _commanderRaidDraft.BossEntry && e.InCombat)
                .Select(e => new { e.Guid, e.Position }).ToArray() });
        RaidQaFail(refusal);
        return false;
    }

    private void ObserveCommanderRaidQaCommand(string command, string cause, bool sent)
    {
        if (_liveRunOptions is not null && _raidQaAttemptDirectory is not null)
            WriteRaidQaEvent("command", new { command, cause, sent,
                readOnly = CommanderRaidAttemptLaw.ReadOnlyCommand(command), combat = RaidQaAnyCombat() });
    }

    private bool RaidQaFail(string reason)
    {
        _raidQaStage = CommanderRaidAttemptStage.Failed;
        WriteRaidQaEvent("failed", new { reason });
        _liveHeld.Clear();
        Console.WriteLine("[raid-live-qa] FAIL " + reason);
        if (_raidQaPersistent)
        {
            StopAttack("raid-qa-attempt-failed");
            string failure = Path.Combine(_raidQaAttemptDirectory ?? Path.GetFullPath(_liveRunOptions!.OutputDirectory),
                "failure-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".json");
            File.WriteAllText(failure, JsonSerializer.Serialize(new { reason, roster = CommanderRaidRoster(fresh: true),
                status = _commanderRaidStatus }, new JsonSerializerOptions { WriteIndented = true }));
            _liveSteps = ["raidqa await"]; _liveStep = 0; _raidQaCommand = "";
            Console.WriteLine("[raid-live-qa] Session retained; waiting for next.protocol");
        }
        else FinishLiveBootstrap("RAID_QA_FAILED", reason);
        return false;
    }
}
