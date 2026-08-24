using System.Reflection;
using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class UiPanelOwnershipAdapterClinicalChecks
{
    private static readonly UiPanelOwnershipLaw.Panel Gossip =
        new("BenillaGossipFrame", UiPanelOwnershipLaw.Area.Left);
    private static readonly UiPanelOwnershipLaw.Panel Merchant =
        new("BenillaMerchantFrame", UiPanelOwnershipLaw.Area.Left);
    private static readonly UiPanelOwnershipLaw.Panel Taxi =
        new("BenillaTaxiFrame", UiPanelOwnershipLaw.Area.Left);
    private static readonly UiPanelOwnershipLaw.Panel Bank =
        new("BenillaBankFrame", UiPanelOwnershipLaw.Area.Left, 6);
    private static readonly UiPanelOwnershipLaw.Panel Character =
        new("BenillaCharacterFrame", UiPanelOwnershipLaw.Area.Left);
    private static readonly UiPanelOwnershipLaw.Panel SpellBook =
        new("BenillaSpellBookFrame", UiPanelOwnershipLaw.Area.Left);
    private static readonly UiPanelOwnershipLaw.Panel Talent =
        new("BenillaTalentFrame", UiPanelOwnershipLaw.Area.Left, 6);
    private static readonly UiPanelOwnershipLaw.Panel Friends =
        new("BenillaFriendsFrame", UiPanelOwnershipLaw.Area.Left, WhileDead: true);
    private static readonly UiPanelOwnershipLaw.Panel TradeSkill =
        new("BenillaTradeSkillFrame", UiPanelOwnershipLaw.Area.Left, 3);
    private static readonly UiPanelOwnershipLaw.Panel Craft =
        new("BenillaCraftFrame", UiPanelOwnershipLaw.Area.Left, 4);
    private static readonly UiPanelOwnershipLaw.Panel DressUp =
        new("DressUpFrame", UiPanelOwnershipLaw.Area.Left, 2);
    private static readonly UiPanelOwnershipLaw.Panel GameMenu =
        new("GameMenuFrame", UiPanelOwnershipLaw.Area.Center, WhileDead: true);
    private static readonly UiPanelOwnershipLaw.Panel Options =
        new("OptionsFrame", UiPanelOwnershipLaw.Area.Center, WhileDead: true);
    private static readonly UiPanelOwnershipLaw.Panel WorldMap =
        new("WorldMapFrame", UiPanelOwnershipLaw.Area.Fullscreen, WhileDead: true);

    public static void Run()
    {
        CheckExactRegistry();
        CheckSingleEdgeAndIdempotence();
        CheckAuthoredSeatOrigins();
        CheckUnknownLatchAndAllClosedRecovery();
        CheckPlannedTransitionConfirmation();
        CheckRefusalAndLegacyInconsistencies();
        CheckHostConfirmedCharacterSpellbookPair();
        CheckHostTransitionGatesBeforeCallbacks();
        CheckHostTransitionFailureTruth();
        CheckProfessionOpenerProvenance();
        CheckProfessionMappingAndUnresolved();
        CheckLockedTaxiIsObservedWithoutCallback();
        CheckProfessionSourceFence();
        CheckObservationOnlySourceFence();
        CheckRuntimeReconciliationSourceFence();
        CheckGameMenuOptionsAtomicReplacementFence();
        CheckHostTransitionSourceFence();
    }

    private static void CheckExactRegistry()
    {
        UiPanelOwnershipLaw.Panel[] expected =
        [
            Gossip,
            Merchant,
            new("BenillaMailFrame", UiPanelOwnershipLaw.Area.Left),
            new("BenillaTradeFrame", UiPanelOwnershipLaw.Area.Left, 1),
            new("BenillaTrainerFrame", UiPanelOwnershipLaw.Area.Left),
            Bank,
            Taxi,
            new("BenillaQuestFrame", UiPanelOwnershipLaw.Area.Left),
            new("BenillaQuestLogFrame", UiPanelOwnershipLaw.Area.Left),
            new("BenillaLootFrame", UiPanelOwnershipLaw.Area.Left, 7),
            Character,
            new("BenillaInspectFrame", UiPanelOwnershipLaw.Area.Left),
            SpellBook,
            Talent,
            Friends,
            new("BenillaMacroFrame", UiPanelOwnershipLaw.Area.Left, 5, WhileDead: true),
            TradeSkill,
            Craft,
            DressUp,
            GameMenu,
            new("OptionsFrame", UiPanelOwnershipLaw.Area.Center, WhileDead: true),
            WorldMap,
        ];
        FieldInfo registryField = typeof(GameLoop).GetField("UiPanelOwnershipRegistry",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidDataException("UI-panel observer registry seam is missing");
        Check(registryField.GetValue(null) is UiPanelOwnershipLaw.Panel[] actual &&
              actual.SequenceEqual(expected) && actual.Select(panel => panel.Id).Distinct(
                  StringComparer.Ordinal).Count() == 22,
            "UI-panel observer 22-row id/area/pushable/whileDead registry drift");
    }

    private static void CheckAuthoredSeatOrigins()
    {
        UiPanelOwnershipLaw.Seats seats = UiPanelOwnershipLaw.Show(
            UiPanelOwnershipLaw.Show(UiPanelOwnershipLaw.Seats.Empty, SpellBook).Seats,
            Talent).Seats;
        Check(seats == new UiPanelOwnershipLaw.Seats(SpellBook, Talent, null) &&
              UiPanelOwnershipLaw.TryLogicalSeatOrigin(seats, SpellBook,
                  out Vector2 spellbookOrigin) && spellbookOrigin == new Vector2(0, 104) &&
              UiPanelOwnershipLaw.TryLogicalSeatOrigin(seats, Talent,
                  out Vector2 talentOrigin) && talentOrigin == new Vector2(384, 104),
            "SpellBookFrame/TalentFrame must consume separate left/center authored seats");

        string root = ClientConfig.FindRepoRoot();
        string spellbook = SourceText.Read(Path.Combine(root,
            "MSUIClient", "Program.Spellbook.cs"));
        string talents = SourceText.Read(Path.Combine(root,
            "MSUIClient", "Program.Talents.cs"));
        string social = SourceText.Read(Path.Combine(root,
            "MSUIClient", "GameLoop", "Panels", "GameLoop.Social.cs"));
        string guild = SourceText.Read(Path.Combine(root,
            "MSUIClient", "GameLoop", "Panels", "GameLoop.Guild.cs"));
        string guildInfo = SourceText.Read(Path.Combine(root,
            "MSUIClient", "GameLoop", "Panels", "GameLoop.GuildInfo.cs"));
        string guildMember = SourceText.Read(Path.Combine(root,
            "MSUIClient", "GameLoop", "Panels", "GameLoop.GuildMemberDetail.cs"));
        string guildControl = SourceText.Read(Path.Combine(root,
            "MSUIClient", "GameLoop", "Panels", "GameLoop.GuildControl.cs"));
        string macro = SourceText.Read(Path.Combine(root,
            "MSUIClient", "GameLoop", "Panels", "GameLoop.Macro.cs"));
        string quest = SourceText.Read(Path.Combine(root,
            "MSUIClient", "GameLoop", "Panels", "GameLoop.Quest.cs"));
        string gossip = SourceText.Read(Path.Combine(root,
            "MSUIClient", "GameLoop", "Panels", "GameLoop.Gossip.cs"));
        string trade = SourceText.Read(Path.Combine(root,
            "MSUIClient", "GameLoop", "Panels", "GameLoop.Trade.cs"));
        string bank = SourceText.Read(Path.Combine(root,
            "MSUIClient", "GameLoop", "Panels", "GameLoop.Bank.cs"));
        string trainer = SourceText.Read(Path.Combine(root,
            "MSUIClient", "GameLoop", "Panels", "GameLoop.Trainer.cs"));
        string taxi = SourceText.Read(Path.Combine(root,
            "MSUIClient", "GameLoop", "Panels", "GameLoop.Taxi.cs"));
        string professions = SourceText.Read(Path.Combine(root,
            "MSUIClient", "GameLoop", "Panels", "GameLoop.Professions.cs"));
        string loot = SourceText.Read(Path.Combine(root,
            "MSUIClient", "GameLoop", "Panels", "GameLoop.Loot.cs"));
        string inspect = SourceText.Read(Path.Combine(root,
            "MSUIClient", "GameLoop", "Panels", "GameLoop.Inspect.cs"));
        string mail = SourceText.Read(Path.Combine(root,
            "MSUIClient", "GameLoop", "Panels", "GameLoop.Mail.cs"));
        string dressUp = SourceText.Read(Path.Combine(root,
            "MSUIClient", "GameLoop", "Panels", "GameLoop.DressUp.cs"));
        Check(spellbook.Contains(
                  "UiPanelFrameOrigin(UiPanelOwnershipRegistry[12], s)",
                  StringComparison.Ordinal) &&
              talents.Contains(
                  "UiPanelFrameOrigin(UiPanelOwnershipRegistry[13], s)",
                  StringComparison.Ordinal) &&
              social.Contains(
                  "UiPanelFrameLogicalOrigin(UiPanelOwnershipRegistry[14])",
                  StringComparison.Ordinal) &&
              guild.Contains("UiPanelFrameOrigin(UiPanelOwnershipRegistry[14], s)",
                  StringComparison.Ordinal) &&
              guildInfo.Contains("UiPanelFrameOrigin(UiPanelOwnershipRegistry[14], scale)",
                  StringComparison.Ordinal) &&
              guildMember.Contains("UiPanelFrameOrigin(UiPanelOwnershipRegistry[14], scale)",
                  StringComparison.Ordinal) &&
              guildControl.Contains("UiPanelFrameOrigin(UiPanelOwnershipRegistry[14], scale)",
                  StringComparison.Ordinal) &&
              macro.Contains("UiPanelFrameLogicalOrigin(UiPanelOwnershipRegistry[15])",
                  StringComparison.Ordinal) &&
              quest.Contains(
                  "UiPanelFrameOrigin(UiPanelOwnershipRegistry[logMode ? 8 : 7], s)",
                  StringComparison.Ordinal) &&
              gossip.Contains("UiPanelFrameOrigin(UiPanelOwnershipRegistry[0], s)",
                  StringComparison.Ordinal) &&
              trade.Contains("UiPanelFrameLogicalOrigin(UiPanelOwnershipRegistry[3])",
                  StringComparison.Ordinal) &&
              bank.Contains("UiPanelFrameOrigin(UiPanelOwnershipRegistry[5], s)",
                  StringComparison.Ordinal) &&
              trainer.Contains("UiPanelFrameOrigin(UiPanelOwnershipRegistry[4], scale)",
                  StringComparison.Ordinal) &&
              taxi.Contains("UiPanelFrameOrigin(UiPanelOwnershipRegistry[6], s)",
                  StringComparison.Ordinal) &&
              professions.Contains(
                  "UiPanelFrameLogicalOrigin(UiPanelOwnershipRegistry[panelIndex])",
                  StringComparison.Ordinal) &&
              loot.Contains("UiPanelFrameOrigin(UiPanelOwnershipRegistry[9], s)",
                  StringComparison.Ordinal) &&
              inspect.Contains("UiPanelFrameOrigin(UiPanelOwnershipRegistry[11], s)",
                  StringComparison.Ordinal) &&
              mail.Contains("UiPanelFrameOrigin(UiPanelOwnershipRegistry[2], s)",
                  StringComparison.Ordinal) &&
              mail.Contains("MailUiLaw.OpenMailOrigin(_mailFrameOrigin, s)",
                  StringComparison.Ordinal) &&
              mail.Contains("MailUiLaw.ConfirmationOrigin(display, s)",
                  StringComparison.Ordinal) &&
              dressUp.Contains(
                  "UiPanelFrameLogicalOrigin(UiPanelOwnershipRegistry[18])",
                  StringComparison.Ordinal) &&
              !mail.Contains("MailPanelClip(new(0, 104 * s)", StringComparison.Ordinal),
            "registered panel or FriendsFrame satellite bypassed the authored panel-seat law");
    }

    private static void CheckPlannedTransitionConfirmation()
    {
        var observer = new UiPanelOwnershipObserver();
        UiPanelOwnershipObservation initial = observer.Observe(new([Character], false));
        UiPanelOwnershipLaw.Transition planned = UiPanelOwnershipLaw.Show(
            initial.Seats, SpellBook);
        UiPanelOwnershipObservation confirmed = observer.ConfirmPlannedTransition(
            new([SpellBook], false), planned.Seats, planned.Effects, "clinical-atomic-replace");
        Check(confirmed.Confidence == UiPanelObservationConfidence.Known &&
              confirmed.Seats == new UiPanelOwnershipLaw.Seats(SpellBook, null, null) &&
              confirmed.VisibleIds.SequenceEqual([SpellBook.Id]) &&
              confirmed.Reason == "clinical-atomic-replace",
            "host-confirmed multi-edge transition did not commit the law-planned seats");

        UiPanelOwnershipObservation rejected = observer.ConfirmPlannedTransition(
            new([SpellBook, Character], false), planned.Seats, planned.Effects, "invalid");
        Check(rejected.Confidence == UiPanelObservationConfidence.Unknown &&
              rejected.Reason == "planned-seat-visible-inconsistency",
            "planned-transition confirmation accepted a host-visible set outside its seats");
    }

    private static void CheckSingleEdgeAndIdempotence()
    {
        var observer = new UiPanelOwnershipObserver();
        UiPanelOwnershipObservation opened = observer.Observe(new([Character], false));
        Check(opened.Confidence == UiPanelObservationConfidence.Known &&
              opened.Seats == new UiPanelOwnershipLaw.Seats(Character, null, null) &&
              opened.VisibleIds.SequenceEqual([Character.Id]) &&
              EffectKinds(opened).SequenceEqual(
                  [UiPanelOwnershipLaw.EffectKind.AnchorLeft,
                   UiPanelOwnershipLaw.EffectKind.Show]),
            "UI-panel observer failed a matching single registered show edge");

        UiPanelOwnershipObservation unchanged = observer.Observe(new([Character], false));
        Check(unchanged.Confidence == UiPanelObservationConfidence.Known &&
              unchanged.Seats == opened.Seats && unchanged.AdvisoryEffects.Count == 0 &&
              unchanged.VisibleIds.SequenceEqual(opened.VisibleIds) &&
              unchanged.Reason == "unchanged",
            "UI-panel observer identical census was not idempotent");

        UiPanelOwnershipObservation pushed = observer.Observe(new([Character, Bank], false));
        Check(pushed.Confidence == UiPanelObservationConfidence.Known &&
              pushed.Seats == new UiPanelOwnershipLaw.Seats(Character, Bank, null),
            "UI-panel observer failed a matching single registered add into the center seat");
        UiPanelOwnershipObservation removed = observer.Observe(new([Bank], false));
        Check(removed.Confidence == UiPanelObservationConfidence.Known &&
              removed.Seats == new UiPanelOwnershipLaw.Seats(Bank, null, null) &&
              EffectKinds(removed).SequenceEqual(
                  [UiPanelOwnershipLaw.EffectKind.Hide,
                   UiPanelOwnershipLaw.EffectKind.AnchorLeft]),
            "UI-panel observer failed a matching single registered removal edge");

        UiPanelOwnershipObservation closed = observer.Observe(new([], false));
        Check(closed.Confidence == UiPanelObservationConfidence.Known &&
              closed.Seats == UiPanelOwnershipLaw.Seats.Empty &&
              closed.AdvisoryEffects.Count == 0 && closed.Reason == "all-closed-recovery",
            "UI-panel observer did not accept the authoritative all-closed baseline");
    }

    private static void CheckUnknownLatchAndAllClosedRecovery()
    {
        var observer = new UiPanelOwnershipObserver();
        UiPanelOwnershipObservation multi = observer.Observe(
            new([Character, SpellBook], false));
        Check(multi.Confidence == UiPanelObservationConfidence.Unknown &&
              multi.AdvisoryEffects.Count == 0 &&
              multi.Reason == "multiple-visibility-edges:2",
            "UI-panel observer accepted an ambiguous multi-edge census");
        UiPanelOwnershipObservation stillUnknown = observer.Observe(new([Character], false));
        Check(stillUnknown.Confidence == UiPanelObservationConfidence.Unknown &&
              stillUnknown.Seats == UiPanelOwnershipLaw.Seats.Empty &&
              stillUnknown.AdvisoryEffects.Count == 0,
            "UI-panel observer reconstructed ownership before an all-closed census");
        UiPanelOwnershipObservation recovered = observer.Observe(new([], false));
        Check(recovered.Confidence == UiPanelObservationConfidence.Known &&
              recovered.Seats == UiPanelOwnershipLaw.Seats.Empty &&
              recovered.AdvisoryEffects.Count == 0 &&
              recovered.Reason == "all-closed-recovery",
            "UI-panel observer did not recover from Unknown at the all-closed baseline");

        var multipleRemoval = new UiPanelOwnershipObserver();
        _ = multipleRemoval.Observe(new([Character], false));
        _ = multipleRemoval.Observe(new([Character, Bank], false));
        UiPanelOwnershipObservation closedAfterTwo = multipleRemoval.Observe(new([], false));
        Check(closedAfterTwo.Confidence == UiPanelObservationConfidence.Known &&
              closedAfterTwo.Seats == UiPanelOwnershipLaw.Seats.Empty &&
              closedAfterTwo.AdvisoryEffects.Count == 0 &&
              closedAfterTwo.Reason == "all-closed-recovery",
            "UI-panel observer made an authoritative multi-removal all-closed census Unknown");

        UiPanelOwnershipObservation reset = observer.Reset();
        Check(reset.Confidence == UiPanelObservationConfidence.Known &&
              reset.Seats == UiPanelOwnershipLaw.Seats.Empty &&
              reset.VisibleIds.Count == 0 && reset.AdvisoryEffects.Count == 0 &&
              reset.Reason == "reset" &&
              observer.Observe(new([Character], false)).Confidence ==
                  UiPanelObservationConfidence.Known,
            "UI-panel observer Reset did not restore the empty known baseline");
    }

    private static void CheckRefusalAndLegacyInconsistencies()
    {
        var dead = new UiPanelOwnershipObserver();
        UiPanelOwnershipObservation refusedDead = dead.Observe(new([Character], true));
        Check(refusedDead.Confidence == UiPanelObservationConfidence.Unknown &&
              refusedDead.Reason == "show-refused:RefusedWhileDead" &&
              refusedDead.AdvisoryEffects.Count == 0,
            "UI-panel observer converted a frozen dead-player refusal into known ownership");
        _ = dead.Reset();
        Check(dead.Observe(new([Friends], true)).Confidence ==
                  UiPanelObservationConfidence.Known,
            "UI-panel observer refused a registered whileDead panel");

        var nativeCenter = new UiPanelOwnershipObserver();
        Check(nativeCenter.Observe(new([GameMenu], false)).Confidence ==
                  UiPanelObservationConfidence.Known,
            "UI-panel observer native-center fixture did not open");
        UiPanelOwnershipObservation centerConflict = nativeCenter.Observe(
            new([GameMenu, Character], false));
        Check(centerConflict.Confidence == UiPanelObservationConfidence.Unknown &&
              centerConflict.Reason == "show-refused:RefusedByNativeCenter",
            "UI-panel observer accepted a non-center add behind the native center gate");

        var fullscreen = new UiPanelOwnershipObserver();
        Check(fullscreen.Observe(new([WorldMap], false)).Confidence ==
                  UiPanelObservationConfidence.Known,
            "UI-panel observer fullscreen fixture did not open");
        UiPanelOwnershipObservation fullscreenConflict = fullscreen.Observe(
            new([WorldMap, Taxi], false));
        Check(fullscreenConflict.Confidence == UiPanelObservationConfidence.Unknown &&
              fullscreenConflict.Reason == "show-refused:RefusedByFullscreen",
            "UI-panel observer accepted a non-fullscreen add behind the fullscreen gate");

        var centerAndFullscreen = new UiPanelOwnershipObserver();
        _ = centerAndFullscreen.Observe(new([GameMenu], false));
        UiPanelOwnershipObservation incompatibleSeats = centerAndFullscreen.Observe(
            new([GameMenu, WorldMap], false));
        Check(incompatibleSeats.Confidence == UiPanelObservationConfidence.Unknown &&
              incompatibleSeats.Reason == "show-refused:RefusedByNativeCenter",
            "UI-panel observer treated simultaneous native-center/fullscreen flags as known seats");

        var legacy = new UiPanelOwnershipObserver();
        _ = legacy.Observe(new([Character], false));
        UiPanelOwnershipObservation impossiblePair = legacy.Observe(
            new([Character, Merchant], false));
        Check(impossiblePair.Confidence == UiPanelObservationConfidence.Unknown &&
              impossiblePair.Reason == "law-seat-visible-inconsistency",
            "UI-panel observer normalized conflicting legacy left-panel flags");

        var changedDescriptor = new UiPanelOwnershipObserver();
        _ = changedDescriptor.Observe(new([Character], false));
        _ = changedDescriptor.Observe(new([], false));
        UiPanelOwnershipObservation descriptorConflict = changedDescriptor.Observe(
            new([Character with { Pushable = 1 }], false));
        Check(descriptorConflict.Confidence == UiPanelObservationConfidence.Unknown &&
              descriptorConflict.Reason == $"descriptor-inconsistency:{Character.Id}",
            "UI-panel observer accepted changed registry metadata for a known frame id");

        var unregistered = new UiPanelOwnershipObserver();
        Check(unregistered.Observe(new(
                  [new("Special", UiPanelOwnershipLaw.Area.Unregistered)], false)).Confidence ==
                  UiPanelObservationConfidence.Unknown,
            "UI-panel observer accepted an unregistered/special frame in its registered census");
    }

    private static void CheckHostConfirmedCharacterSpellbookPair()
    {
        var empty = new HostTransitionRig();
        UiPanelHostTransition.Result opened = empty.Show(Character);
        Check(opened.Outcome == UiPanelHostTransition.Outcome.Opened && opened.Succeeded &&
              empty.Character.Visible && !empty.SpellBook.Visible &&
              opened.Observation.Confidence == UiPanelObservationConfidence.Known &&
              opened.Observation.Seats ==
                  new UiPanelOwnershipLaw.Seats(Character, null, null) &&
              empty.Calls.SequenceEqual([
                  $"preflight-show:{Character.Id}",
                  $"show:{Character.Id}",
              ]),
            "host-confirmed empty-seat CharacterFrame open or callback order drift");

        UiPanelHostTransition.Result identical = empty.Show(Character);
        Check(identical.Outcome == UiPanelHostTransition.Outcome.AlreadyVisible &&
              identical.Succeeded && empty.Calls.Count == 0 && empty.Character.Visible,
            "host-confirmed identical CharacterFrame show invoked a host callback");

        UiPanelHostTransition.Result toSpellBook = empty.Show(SpellBook);
        Check(toSpellBook.Outcome == UiPanelHostTransition.Outcome.Opened &&
              !empty.Character.Visible && empty.SpellBook.Visible &&
              toSpellBook.Observation.Seats ==
                  new UiPanelOwnershipLaw.Seats(SpellBook, null, null) &&
              empty.Calls.SequenceEqual([
                  $"preflight-show:{SpellBook.Id}",
                  $"preflight-displace:{Character.Id}",
                  $"displace:{Character.Id}",
                  $"show:{SpellBook.Id}",
              ]),
            "CharacterFrame -> SpellBookFrame host replacement/callback order drift");

        UiPanelHostTransition.Result toCharacter = empty.Show(Character);
        Check(toCharacter.Outcome == UiPanelHostTransition.Outcome.Opened &&
              empty.Character.Visible && !empty.SpellBook.Visible &&
              toCharacter.Observation.Seats ==
                  new UiPanelOwnershipLaw.Seats(Character, null, null) &&
              empty.Calls.SequenceEqual([
                  $"preflight-show:{Character.Id}",
                  $"preflight-displace:{SpellBook.Id}",
                  $"displace:{SpellBook.Id}",
                  $"show:{Character.Id}",
              ]),
            "SpellBookFrame -> CharacterFrame host replacement/callback order drift");

        // A normal direct close owns its native flag and may run immediately before the other
        // binding in the same Update pass. The incoming transaction must census that all-closed
        // truth, recover the observer, and open without inventing a displacement callback.
        var directCloseThenOpen = new HostTransitionRig();
        directCloseThenOpen.Character.Visible = true;
        Check(directCloseThenOpen.ObserveNow().Confidence ==
                  UiPanelObservationConfidence.Known,
            "same-frame direct-close fixture failed to seed CharacterFrame ownership");
        directCloseThenOpen.Character.Visible = false;
        UiPanelHostTransition.Result recoveredOpen = directCloseThenOpen.Show(SpellBook);
        Check(recoveredOpen.Outcome == UiPanelHostTransition.Outcome.Opened &&
              !directCloseThenOpen.Character.Visible && directCloseThenOpen.SpellBook.Visible &&
              recoveredOpen.Observation.Confidence == UiPanelObservationConfidence.Known &&
              recoveredOpen.Observation.Seats ==
                  new UiPanelOwnershipLaw.Seats(SpellBook, null, null) &&
              directCloseThenOpen.Calls.SequenceEqual([
                  $"preflight-show:{SpellBook.Id}",
                  $"show:{SpellBook.Id}",
              ]),
            "same-frame direct Character close -> SpellBook open did not recover by census");
    }

    private static void CheckHostTransitionGatesBeforeCallbacks()
    {
        var unknown = new HostTransitionRig();
        unknown.Character.Visible = true;
        unknown.SpellBook.Visible = true;
        Check(unknown.Show(Character).Outcome ==
                  UiPanelHostTransition.Outcome.ObservationUnknown &&
              unknown.Calls.Count == 0,
            "unknown UI-panel census reached a Character/SpellBook host callback");

        var nativeCenter = new HostTransitionRig { NativeCenterVisible = true };
        Check(nativeCenter.Show(Character).Outcome ==
                  UiPanelHostTransition.Outcome.RefusedByNativeCenter &&
              nativeCenter.Calls.Count == 0,
            "native-center gate ran after a CharacterFrame host callback");

        var fullscreen = new HostTransitionRig { FullscreenVisible = true };
        Check(fullscreen.Show(SpellBook).Outcome ==
                  UiPanelHostTransition.Outcome.RefusedByFullscreen &&
              fullscreen.Calls.Count == 0,
            "fullscreen gate ran after a SpellBookFrame host callback");

        var dead = new HostTransitionRig { PlayerDeadOrGhost = true };
        Check(dead.Show(Character).Outcome ==
                  UiPanelHostTransition.Outcome.RefusedWhileDead && dead.Calls.Count == 0,
            "dead-player gate ran after a CharacterFrame host callback");

        var pushOwner = new HostTransitionRig { BankVisible = true };
        Check(pushOwner.Show(SpellBook).Outcome ==
                  UiPanelHostTransition.Outcome.UnsupportedShape && pushOwner.Calls.Count == 0,
            "unsupported push owner reached the bounded pair callbacks");

        var otherLeft = new HostTransitionRig { MerchantVisible = true };
        Check(otherLeft.Show(Character).Outcome ==
                  UiPanelHostTransition.Outcome.UnsupportedShape && otherLeft.Calls.Count == 0 &&
              otherLeft.MerchantVisible && !otherLeft.Character.Visible,
            "unsupported ordinary other-left owner reached the bounded pair callbacks");

        var occupiedCenter = new HostTransitionRig();
        occupiedCenter.Character.Visible = true;
        Check(occupiedCenter.ObserveNow().Confidence == UiPanelObservationConfidence.Known,
            "host-transition center fixture failed to seed CharacterFrame");
        occupiedCenter.BankVisible = true;
        Check(occupiedCenter.ObserveNow().Confidence == UiPanelObservationConfidence.Known,
            "host-transition center fixture failed to seed a pushed center owner");
        Check(occupiedCenter.Show(SpellBook).Outcome ==
                  UiPanelHostTransition.Outcome.UnsupportedShape &&
              occupiedCenter.Calls.Count == 0,
            "occupied left-area center seat reached the bounded pair callbacks");

        var incomingPreflight = new HostTransitionRig();
        incomingPreflight.Character.Visible = true;
        incomingPreflight.SpellBook.ShowPreflightAccepted = false;
        Check(incomingPreflight.Show(SpellBook).Outcome ==
                  UiPanelHostTransition.Outcome.IncomingPreflightRefused &&
              incomingPreflight.Character.Visible && !incomingPreflight.SpellBook.Visible &&
              incomingPreflight.Calls.SequenceEqual([
                  $"preflight-show:{SpellBook.Id}",
              ]),
            "incoming preflight refusal displaced CharacterFrame");

        var displacedPreflight = new HostTransitionRig();
        displacedPreflight.Character.Visible = true;
        displacedPreflight.Character.DisplacePreflightAccepted = false;
        Check(displacedPreflight.Show(SpellBook).Outcome ==
                  UiPanelHostTransition.Outcome.DisplacedPreflightRefused &&
              displacedPreflight.Character.Visible && !displacedPreflight.SpellBook.Visible &&
              displacedPreflight.Calls.SequenceEqual([
                  $"preflight-show:{SpellBook.Id}",
                  $"preflight-displace:{Character.Id}",
              ]),
            "displaced-host preflight refusal mutated the pair");

        var mutatingPreflight = new HostTransitionRig();
        mutatingPreflight.Character.Visible = true;
        mutatingPreflight.SpellBook.MutateVisibleDuringShowPreflight = true;
        Check(mutatingPreflight.Show(SpellBook).Outcome ==
                  UiPanelHostTransition.Outcome.CensusChangedDuringPreflight &&
              mutatingPreflight.Character.Visible && mutatingPreflight.SpellBook.Visible &&
              !mutatingPreflight.Calls.Contains($"displace:{Character.Id}") &&
              !mutatingPreflight.Calls.Contains($"show:{SpellBook.Id}"),
            "mutating preflight was not stopped by the unchanged-census confirmation");
    }

    private static void CheckHostTransitionFailureTruth()
    {
        var closeRefused = new HostTransitionRig();
        closeRefused.Character.Visible = true;
        closeRefused.Character.DisplaceAccepted = false;
        closeRefused.Character.DisplaceMutates = false;
        UiPanelHostTransition.Result refused = closeRefused.Show(SpellBook);
        Check(refused.Outcome == UiPanelHostTransition.Outcome.DisplacementCallbackFailed &&
              closeRefused.Character.Visible && !closeRefused.SpellBook.Visible &&
              !closeRefused.Calls.Contains($"show:{SpellBook.Id}"),
            "refused CharacterFrame close continued into SpellBookFrame open");

        var closeStillVisible = new HostTransitionRig();
        closeStillVisible.Character.Visible = true;
        closeStillVisible.Character.DisplaceMutates = false;
        UiPanelHostTransition.Result unconfirmedClose = closeStillVisible.Show(SpellBook);
        Check(unconfirmedClose.Outcome ==
                  UiPanelHostTransition.Outcome.DisplacementNotConfirmed &&
              closeStillVisible.Character.Visible && !closeStillVisible.SpellBook.Visible &&
              unconfirmedClose.Observation.Seats ==
                  new UiPanelOwnershipLaw.Seats(Character, null, null) &&
              !closeStillVisible.Calls.Contains($"show:{SpellBook.Id}"),
            "still-visible displaced host continued into the incoming callback");

        var openFailed = new HostTransitionRig();
        openFailed.Character.Visible = true;
        openFailed.SpellBook.ShowAccepted = false;
        openFailed.SpellBook.ShowMutates = false;
        UiPanelHostTransition.Result failedOpen = openFailed.Show(SpellBook);
        Check(failedOpen.Outcome == UiPanelHostTransition.Outcome.OpenCallbackFailed &&
              !openFailed.Character.Visible && !openFailed.SpellBook.Visible &&
              failedOpen.Observation.Seats == UiPanelOwnershipLaw.Seats.Empty &&
              Count(string.Join('|', openFailed.Calls), $"show:{Character.Id}") == 0,
            "failed incoming callback rolled back/reopened the displaced CharacterFrame");

        var postPredicateMismatch = new HostTransitionRig();
        postPredicateMismatch.Character.Visible = true;
        postPredicateMismatch.SpellBook.ShowMutates = false;
        UiPanelHostTransition.Result mismatch = postPredicateMismatch.Show(SpellBook);
        Check(mismatch.Outcome == UiPanelHostTransition.Outcome.OpenNotConfirmed &&
              !postPredicateMismatch.Character.Visible && !postPredicateMismatch.SpellBook.Visible &&
              mismatch.Observation.Confidence == UiPanelObservationConfidence.Known &&
              mismatch.Observation.Seats == UiPanelOwnershipLaw.Seats.Empty &&
              Count(string.Join('|', postPredicateMismatch.Calls),
                  $"show:{Character.Id}") == 0,
            "postpredicate mismatch invented planned seats or rolled back the displaced host");

        var falseButOpened = new HostTransitionRig();
        falseButOpened.Character.Visible = true;
        falseButOpened.SpellBook.ShowAccepted = false;
        UiPanelHostTransition.Result truthful = falseButOpened.Show(SpellBook);
        Check(truthful.Outcome == UiPanelHostTransition.Outcome.OpenCallbackFailed &&
              !falseButOpened.Character.Visible && falseButOpened.SpellBook.Visible &&
              truthful.Observation.Seats ==
                  new UiPanelOwnershipLaw.Seats(SpellBook, null, null),
            "false-returning open callback hid the confirmed host predicate truth");
    }

    private static void CheckProfessionOpenerProvenance()
    {
        Check(ProfessionPanelOpenerLaw.Resolve([47u, 0u, 0u], [0, 9, -1]) ==
                  new ProfessionPanelOpenerProvenance(true, ProfessionPanelKind.TradeSkill),
            "profession opener did not route misc0 zero to TradeSkillFrame");
        Check(ProfessionPanelOpenerLaw.Resolve([47u, 0u, 0u], [1, 0, 0]) ==
                  new ProfessionPanelOpenerProvenance(true, ProfessionPanelKind.Craft, 1) &&
              ProfessionPanelOpenerLaw.Resolve([47u], [-1]) ==
                  new ProfessionPanelOpenerProvenance(true, ProfessionPanelKind.Craft),
            "profession opener did not route signed misc0 nonzero to CraftFrame");

        Check(ProfessionPanelOpenerLaw.Resolve([0u, 47u, 0u], [0, 1, 0]) ==
                  new ProfessionPanelOpenerProvenance(false, null) &&
              ProfessionPanelOpenerLaw.Resolve([46u], [1]) ==
                  new ProfessionPanelOpenerProvenance(false, null),
            "profession opener classifier scanned a later lane or accepted a non-47 first effect");
        Check(ProfessionPanelOpenerLaw.Resolve([47u], null) ==
                  new ProfessionPanelOpenerProvenance(true, null) &&
              ProfessionPanelOpenerLaw.Resolve([47u], []) ==
                  new ProfessionPanelOpenerProvenance(true, null),
            "effect-47 opener with missing misc provenance was not intercepted as unresolved");
        Check(ProfessionPanelOpenerLaw.Resolve(null, [0]) ==
                  new ProfessionPanelOpenerProvenance(false, null) &&
              ProfessionPanelOpenerLaw.Resolve([], [0]) ==
                  new ProfessionPanelOpenerProvenance(false, null),
            "missing first-effect provenance was classified as a profession opener");
    }

    private static void CheckProfessionMappingAndUnresolved()
    {
        var resolvedTradeSkill = new UiPanelOwnershipObserver();
        UiPanelOwnershipObservation tradeSkill = resolvedTradeSkill.Observe(
            new([TradeSkill], false));
        Check(tradeSkill.Confidence == UiPanelObservationConfidence.Known &&
              tradeSkill.Seats == new UiPanelOwnershipLaw.Seats(TradeSkill, null, null) &&
              tradeSkill.VisibleIds.SequenceEqual([TradeSkill.Id]),
            "retained zero-misc profession provenance did not observe TradeSkillFrame");

        var resolvedCraft = new UiPanelOwnershipObserver();
        UiPanelOwnershipObservation craft = resolvedCraft.Observe(new([Craft], false));
        Check(craft.Confidence == UiPanelObservationConfidence.Known &&
              craft.Seats == new UiPanelOwnershipLaw.Seats(Craft, null, null) &&
              craft.VisibleIds.SequenceEqual([Craft.Id]),
            "retained nonzero-misc profession provenance did not observe CraftFrame");

        var observer = new UiPanelOwnershipObserver();
        UiPanelOwnershipObservation unresolved = observer.Observe(new([], false,
            "profession-opener-kind-not-retained"));
        Check(unresolved.Confidence == UiPanelObservationConfidence.Unknown &&
              unresolved.VisibleIds.Count == 0 && unresolved.AdvisoryEffects.Count == 0 &&
              unresolved.Reason ==
                  "unresolved:profession-opener-kind-not-retained",
            "UI-panel observer guessed TradeSkillFrame/CraftFrame from _professionOpen");
        Check(observer.Observe(new([], false)).Confidence == UiPanelObservationConfidence.Known,
            "UI-panel observer did not recover after unresolved profession state became all closed");
    }

    private static void CheckProfessionSourceFence()
    {
        string root = ClientConfig.FindRepoRoot();
        string law = SourceText.Read(Path.Combine(root,
            "MSUIClient", "Engine", "UI", "ProfessionPanelOpenerLaw.cs"));
        string professions = SourceText.Read(Path.Combine(root,
            "MSUIClient", "Program.Professions.cs"));
        string tryOpen = Slice(professions, "    private bool TryOpenProfession(uint spellId)",
            "    private bool OpenFirstProfession()");
        string actionBars = SourceText.Read(Path.Combine(root,
            "MSUIClient", "Program.ActionBars.cs"));
        string tryCast = Slice(actionBars, "    private void TryCast(uint spellId)",
            "    private void CommitCastSend(in SpellInfo spell");

        Check(law.Contains("effectIds[0] != TradeSkillEffect", StringComparison.Ordinal) &&
              law.Contains("int misc = effectMiscValues[0];", StringComparison.Ordinal) &&
              law.Contains("misc == 0", StringComparison.Ordinal) &&
              law.Contains("misc > 0 ? (uint)misc : 0", StringComparison.Ordinal) &&
              law.Contains("return new(true, null);", StringComparison.Ordinal) &&
              !law.Contains("333", StringComparison.Ordinal) &&
              !law.Contains(".Any(", StringComparison.Ordinal) &&
              !law.Contains(".Contains(", StringComparison.Ordinal),
            "profession opener law no longer uses exact lane-zero effect/misc provenance");
        string[] forbiddenLawEffects =
        [
            "ImGui", "Console", "EmitInterface", "PlayUiSound", "_net", "CastSpell",
            "SendPacket", "Telemetry", "SkillLineCatalog", "OpenProfession",
        ];
        Check(forbiddenLawEffects.All(operation =>
                  !law.Contains(operation, StringComparison.Ordinal)),
            "pure profession opener law gained a host, wire, sound, or diagnostic dependency");

        Check(tryOpen.Contains(
                  "ProfessionPanelOpenerLaw.Resolve(\n            opener.EffectIds, opener.EffectMiscValues);",
                  StringComparison.Ordinal) &&
              tryOpen.Contains("if (!provenance.IsProfessionOpener) return false;",
                  StringComparison.Ordinal) &&
              tryOpen.Contains("if (_skillLines is null) return true;", StringComparison.Ordinal) &&
              tryOpen.Contains(
                  "if (!IsCraftProfessionLine(line) || _spellCatalog.CreatedItem(spellId) != 0) return true;",
                  StringComparison.Ordinal) &&
              tryOpen.Contains(
                  "_ = OpenProfession(line, spellId, provenance.PanelKind, provenance.CraftType);",
                  StringComparison.Ordinal) &&
              !tryOpen.Contains("hasKnownRecipe", StringComparison.Ordinal) &&
              tryOpen.EndsWith("        return true;\n    }\n\n", StringComparison.Ordinal) &&
              Count(tryOpen, "return false;") == 2 &&
              !tryOpen.Contains("return OpenProfession", StringComparison.Ordinal) &&
              !tryOpen.Contains("CastSpell", StringComparison.Ordinal) &&
              !tryOpen.Contains("_net", StringComparison.Ordinal),
            "effect-47 profession opener can fall through to the ordinary cast wire");

        Check(Count(professions, "OpenProfession(line, 0, panelKind: null)") == 2 &&
              professions.Contains(
                  "_professionPanelKind = _professionOpen ? panelKind : null;",
                  StringComparison.Ordinal) &&
              professions.Contains(
                  "_professionCraftType, preserveSelection: true);",
                  StringComparison.Ordinal) &&
              Count(professions, " OpenProfession(") == 5,
            "diagnostic/manual profession opens retained or invented opener-kind provenance");

        int catalogLookup = tryCast.IndexOf(
            "_spellCatalog.TryGet(spellId, out SpellInfo spell)", StringComparison.Ordinal);
        int professionIntercept = tryCast.IndexOf("if (TryOpenProfession(spellId))",
            StringComparison.Ordinal);
        int passiveGate = tryCast.IndexOf("if (spell.Passive)", StringComparison.Ordinal);
        int knownGate = tryCast.IndexOf("if (!_actions.KnownSpells.Contains(spellId))",
            StringComparison.Ordinal);
        int ordinaryCastLadder = tryCast.IndexOf("double now =", StringComparison.Ordinal);
        Check(catalogLookup >= 0 && professionIntercept > catalogLookup &&
              passiveGate > professionIntercept && knownGate > passiveGate &&
              ordinaryCastLadder > knownGate &&
              Count(tryCast, "TryOpenProfession(spellId)") == 1,
            "TryCast does not intercept effect-47 immediately after catalog lookup and before gates");
    }

    private static void CheckLockedTaxiIsObservedWithoutCallback()
    {
        var observer = new UiPanelOwnershipObserver();
        UiPanelOwnershipObservation taxi = observer.Observe(new([Taxi], false));
        Check(taxi.Confidence == UiPanelObservationConfidence.Known &&
              taxi.Seats.Left == Taxi && taxi.AdvisoryEffects.Count == 2,
            "UI-panel observer requires a host close callback before observing TaxiFrame");

        string adapter = AdapterSource();
        Check(adapter.Contains("IncludeWhen(_taxiOpen, 6);", StringComparison.Ordinal) &&
              !adapter.Contains("_taxiLocked", StringComparison.Ordinal) &&
              !adapter.Contains("TryClosePlayerPanelOnEscape", StringComparison.Ordinal),
            "UI-panel observer gated locked TaxiFrame on a close callback or lock state");
    }

    private static void CheckObservationOnlySourceFence()
    {
        string adapter = Slice(AdapterSource(),
            "    private UiPanelOwnershipSample CaptureUiPanelOwnershipSample()",
            "    private void ObserveUiPanelOwnership()");
        string observer = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
            "MSUIClient", "Engine", "UI", "UiPanelOwnershipObserver.cs"));
        string program = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
            "MSUIClient", "Program.cs"));

        string[] exactCensus =
        [
            "IncludeWhen(_gossipMenu is not null || _gossipGreeting is not null, 0);",
            "IncludeWhen(_vendor is not null, 1);",
            "IncludeWhen(_mailOpen, 2);",
            "IncludeWhen(_tradeOpen, 3);",
            "IncludeWhen(_trainer is not null, 4);",
            "IncludeWhen(_bankOpen, 5);",
            "IncludeWhen(_taxiOpen, 6);",
            "IncludeWhen(QuestNpcPanelNow() != QuestNpcPanel.None, 7);",
            "IncludeWhen(_questLogOpen, 8);",
            "IncludeWhen(_loot.IsOpen, 9);",
            "IncludeWhen(_characterOpen, 10);",
            "IncludeWhen(_inspectOpen, 11);",
            "IncludeWhen(_spellbookOpen, 12);",
            "IncludeWhen(_talentOpen, 13);",
            "IncludeWhen(_socialOpen || _guildOpen, 14);",
            "IncludeWhen(_macroOpen, 15);",
            "IncludeWhen(_professionOpen && _professionPanelKind == ProfessionPanelKind.TradeSkill, 16);",
            "IncludeWhen(_professionOpen && _professionPanelKind == ProfessionPanelKind.Craft, 17);",
            "IncludeWhen(_dressUpOpen, 18);",
            "IncludeWhen(_settingsOpen && _menuPage == MenuPage.GameMenu, 19);",
            "IncludeWhen(_settingsOpen && _menuPage != MenuPage.GameMenu, 20);",
            "IncludeWhen(_worldMapOpen, 21);",
        ];
        Check(exactCensus.All(line => adapter.Contains(line, StringComparison.Ordinal)) &&
              adapter.Contains("\"profession-opener-kind-not-retained\"",
                  StringComparison.Ordinal),
            "UI-panel observer current-predicate census or unresolved profession boundary drift");

        string[] excludedHostFlags =
        [
            "_auctionOpen", "_helpOpen", "_keybindingsOpen", "_tabardOpen",
            "_backpackOpen", "_keyringOpen", "_equippedBagOpen", "_gameObjectPages",
            "_deathRezOpen",
        ];
        Check(excludedHostFlags.All(flag => !adapter.Contains(flag, StringComparison.Ordinal)),
            "UI-panel observer admitted an unregistered or special-purpose host frame");

        string[] forbiddenOperations =
        [
            "ImGui", "Console", "EmitInterface", "PlayUiSound", "CloseQuestNpcFrame",
            "CloseMailSession", "CloseInspect", "ResetTrade", "ResetAuction", "ActivateTaxi",
            "SendPacket", "Telemetry", "Action<UiPanelOwnershipLaw.Effect>",
        ];
        Check(forbiddenOperations.All(operation =>
                  !adapter.Contains(operation, StringComparison.Ordinal) &&
                  !observer.Contains(operation, StringComparison.Ordinal)),
            "UI-panel observation-only seam gained rendering, wire, sound, telemetry, or host effects");

        string[] sourceOwnedFields =
        [
            "_gossipMenu", "_vendor", "_mailOpen", "_tradeOpen", "_trainer", "_bankOpen",
            "_taxiOpen", "_questLogOpen", "_characterOpen", "_inspectOpen", "_spellbookOpen",
            "_talentOpen", "_socialOpen", "_macroOpen", "_settingsOpen", "_menuPage",
            "_worldMapOpen", "_professionOpen", "_professionPanelKind", "_dressUpOpen",
        ];
        Check(sourceOwnedFields.All(field =>
                  !System.Text.RegularExpressions.Regex.IsMatch(adapter,
                      System.Text.RegularExpressions.Regex.Escape(field) + @"\s*=(?!=)")),
            "UI-panel observer adapter writes a host-owned visibility flag or payload");

        string normalizedProgram = program.Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] lifecycleOrder =
        [
            "UpdateQuestNpcLifecycle();", "UpdateVendorLifecycle();",
            "UpdateGossipLifecycle();", "UpdateTrainerLifecycle();",
            "UpdateTaxiLifecycle();", "UpdateBankLifecycle();",
            "UpdateNpcGreetingLifecycle();", "ObserveUiPanelOwnership();",
        ];
        int previousLifecycle = -1;
        bool orderedLifecycle = true;
        foreach (string call in lifecycleOrder)
        {
            int currentLifecycle = normalizedProgram.IndexOf(call, StringComparison.Ordinal);
            orderedLifecycle &= currentLifecycle > previousLifecycle;
            previousLifecycle = currentLifecycle;
        }
        Check(orderedLifecycle && Count(program, "ObserveUiPanelOwnership();") == 1,
            "UI-panel census is not after the registered NPC lifecycle passes in normal Update");
    }

    private static void CheckRuntimeReconciliationSourceFence()
    {
        string root = ClientConfig.FindRepoRoot();
        string adapter = AdapterSource();
        string settings = SourceText.Read(Path.Combine(root,
            "MSUIClient", "GameLoop", "Panels", "GameLoop.Settings.cs"));
        string inventory = SourceText.Read(Path.Combine(root,
            "MSUIClient", "GameLoop", "Panels", "GameLoop.Inventory.cs"));
        string observer = SourceText.Read(Path.Combine(root,
            "MSUIClient", "Engine", "UI", "UiPanelOwnershipObserver.cs"));

        Check(adapter.Contains("UiPanelOwnershipLaw.Show(\n                before.Seats, incoming",
                  StringComparison.Ordinal) &&
              adapter.Contains("ApplyUiPanelEffects(planned.Effects, incoming.Id);",
                  StringComparison.Ordinal) &&
              adapter.Contains("ConfirmPlannedTransition(\n                    after, planned.Seats",
                  StringComparison.Ordinal) &&
              adapter.Contains("case \"DressUpFrame\":", StringComparison.Ordinal) &&
              adapter.Contains("UiPanelOwnershipLaw.CloseAllWindows(",
                  StringComparison.Ordinal) &&
              adapter.Contains("UiPanelOwnershipLaw.CloseWindows(",
                  StringComparison.Ordinal) &&
              adapter.Contains("bool specialVisible = CloseItemRefTooltip();",
                  StringComparison.Ordinal),
            "registered host reconciliation or CloseWindows runtime dispatch drift");

        Check(observer.Contains("public UiPanelOwnershipObservation ConfirmPlannedTransition(",
                  StringComparison.Ordinal) &&
              observer.Contains("SeatsMatchVisible(plannedSeats, current)",
                  StringComparison.Ordinal),
            "host-confirmed multi-edge observer entrance lost final-seat validation");

        Check(settings.Contains(
                  "TryCloseRegisteredUiPanels(closeEscapeContainers: true)",
                  StringComparison.Ordinal) &&
              settings.Contains(
                  "TryCloseRegisteredUiPanels(closeEscapeContainers: false)",
                  StringComparison.Ordinal) &&
              settings.Contains("CloseAllNormalBagWindows();", StringComparison.Ordinal) &&
              inventory.Contains("private bool CloseAllNormalBagWindows()",
                  StringComparison.Ordinal) &&
              !Slice(inventory, "    private bool CloseAllNormalBagWindows()",
                  "    private void TriggerItemPushAnimation")
                  .Contains("InventoryUiLaw.KeyringContainer", StringComparison.Ordinal),
            "Escape CloseAllWindows or native-center CloseAllBags/keyring split drift");
    }

    private static void CheckGameMenuOptionsAtomicReplacementFence()
    {
        UiPanelOwnershipLaw.Seats gameMenuSeats = UiPanelOwnershipLaw.Show(
            UiPanelOwnershipLaw.Seats.Empty, GameMenu).Seats;
        UiPanelOwnershipLaw.Transition replacement = UiPanelOwnershipLaw.Show(
            gameMenuSeats, Options);
        Check(replacement.Outcome == UiPanelOwnershipLaw.Outcome.Opened &&
              replacement.Seats == new UiPanelOwnershipLaw.Seats(null, Options, null),
            "GameMenuFrame -> OptionsFrame is no longer a valid atomic center replacement");

        string adapter = AdapterSource().Replace("\r\n", "\n", StringComparison.Ordinal);
        int addOnly = adapter.IndexOf(
            "if (added.Length == 1 && removed.Length == 0)", StringComparison.Ordinal);
        int atomicReplace = adapter.IndexOf(
            "if (added.Length == 1 && removed.Length > 0)", StringComparison.Ordinal);
        Check(addOnly >= 0 && atomicReplace > addOnly,
            "runtime reconciliation can consume an atomic replacement as an add and close its shared host");
    }

    private static void CheckHostTransitionSourceFence()
    {
        string root = ClientConfig.FindRepoRoot();
        string transition = SourceText.Read(Path.Combine(root,
            "MSUIClient", "Engine", "UI", "UiPanelHostTransition.cs"));
        string adapter = AdapterSource();
        string character = SourceText.Read(Path.Combine(root,
            "MSUIClient", "Program.CharacterPage.cs"));
        string spellbook = SourceText.Read(Path.Combine(root,
            "MSUIClient", "Program.Spellbook.cs"));
        string actionBars = SourceText.Read(Path.Combine(root,
            "MSUIClient", "Program.ActionBars.cs"));
        string settings = SourceText.Read(Path.Combine(root,
            "MSUIClient", "Program.Settings.cs"));
        string devParity = SourceText.Read(Path.Combine(root,
            "MSUIClient", "Program.DevTools.UiParity.cs"));
        string liveRun = SourceText.Read(Path.Combine(root,
            "MSUIClient", "Program.LiveRun.cs"));

        int incomingPreflight = transition.IndexOf("if (!incoming.PreflightShow())",
            StringComparison.Ordinal);
        int displacedPreflight = transition.IndexOf(
            "displaced is not null && !displaced.PreflightDisplace()",
            StringComparison.Ordinal);
        int unchangedCensus = transition.IndexOf(
            "UiPanelOwnershipSample afterPreflight = capture();", StringComparison.Ordinal);
        int displaceCallback = transition.IndexOf(
            "bool callbackAccepted = displaced.Displace();", StringComparison.Ordinal);
        int removedCensus = transition.IndexOf(
            "UiPanelOwnershipSample afterDisplacement = capture();", StringComparison.Ordinal);
        int openCallback = transition.IndexOf("bool openAccepted = incoming.Show();",
            StringComparison.Ordinal);
        int finalCensus = transition.IndexOf("UiPanelOwnershipSample afterOpen = capture();",
            StringComparison.Ordinal);
        Check(transition.Contains("UiPanelOwnershipLaw.Show(", StringComparison.Ordinal) &&
              transition.Contains("force: false", StringComparison.Ordinal) &&
              transition.Contains("hosts.Count is < 1 or > 2", StringComparison.Ordinal) &&
              transition.Contains("panel.Pushable == 0 && !panel.WhileDead",
                  StringComparison.Ordinal) &&
              Count(transition, "observer.Observe(") == 4 &&
              incomingPreflight >= 0 && displacedPreflight > incomingPreflight &&
              unchangedCensus > displacedPreflight && displaceCallback > unchangedCensus &&
              removedCensus > displaceCallback && openCallback > removedCensus &&
              finalCensus > openCallback,
            "host transition no longer gates, preflights, and confirms in frozen callback order");

        string[] forbiddenCoordinatorDependencies =
        [
            "_characterOpen", "_spellbookOpen", "_settingsOpen", "_worldMapOpen",
            "ImGui", "Console", "PlayUiSound", "EmitInterface", "SendPacket", "Telemetry",
            "MSUIClient.Net", "SetCharacterPageOpen", "SetSpellbookOpen",
        ];
        Check(forbiddenCoordinatorDependencies.All(value =>
                  !transition.Contains(value, StringComparison.Ordinal)) &&
              !System.Text.RegularExpressions.Regex.IsMatch(transition,
                  @"planned\.Seats\s*=(?!=)") &&
              !transition.Contains("observer.Reset", StringComparison.Ordinal),
            "host transition gained a native flag, renderer, wire, sound, telemetry, or planned-seat commit");

        Check(adapter.Contains(
                  "UiPanelHostTransition.Show(\n            _uiPanelOwnershipObserver,\n            CaptureUiPanelOwnershipSample,",
                  StringComparison.Ordinal) &&
              Count(adapter, "UiPanelHostTransition.Show(") == 1 &&
              adapter.Contains("[character, spellbook]", StringComparison.Ordinal) &&
              adapter.Contains("UiPanelOwnershipRegistry[10]", StringComparison.Ordinal) &&
              adapter.Contains("UiPanelOwnershipRegistry[12]", StringComparison.Ordinal) &&
              adapter.Contains("() => SetCharacterPageOpen(false)", StringComparison.Ordinal) &&
              adapter.Contains("() => SetSpellbookOpen(false)", StringComparison.Ordinal),
            "Program host adapter widened beyond or detached from the Character/SpellBook pair");

        string characterSetter = Slice(character,
            "    private bool SetCharacterPageOpen(bool open, bool playSound = true,",
            "    private void PlayCharacterTransition(");
        string characterInput = Slice(character,
            "    private void UpdateCharacterPageInput(bool typing)",
            "    private void DrawCharacterPage()");
        Check(characterSetter.Contains("_characterOpen = open;", StringComparison.Ordinal) &&
              characterSetter.Contains("_paperDollDirty = true;", StringComparison.Ordinal) &&
              characterSetter.Contains("PlayCharacterTransition(", StringComparison.Ordinal) &&
              !characterSetter.Contains("_spellbookOpen", StringComparison.Ordinal) &&
              characterInput.Contains("ToggleCharacterPageThroughUiPanel();",
                  StringComparison.Ordinal) &&
              Count(characterInput, "ToggleCharacterPageThroughUiPanel();") == 1 &&
              characterInput.Contains(
                  "OpenCharacterPageThroughUiPanel(\n                        soundCategory: \"ui.skill-frame\",\n                        requestedTab: SkillFrameUiLaw.SkillsTab);",
                  StringComparison.Ordinal) &&
              Count(characterInput, "OpenCharacterPageThroughUiPanel(") == 1,
            "CharacterFrame setter/sound law or normal binding authority route drift");

        string spellbookSetter = Slice(spellbook,
            "    private bool SetSpellbookOpen(bool open)",
            "    private void UpdateSpellbookInput(bool typing)");
        string spellbookInput = Slice(spellbook,
            "    private void UpdateSpellbookInput(bool typing)",
            "    private void DrawSpellbook()");
        Check(spellbookSetter.Contains("if (_spellbookOpen == open) return false;",
                  StringComparison.Ordinal) &&
              spellbookSetter.Contains("_spellbookOpen = open;", StringComparison.Ordinal) &&
              System.Text.RegularExpressions.Regex.Matches(spellbookSetter,
                  @"_spellbookOpen\s*=(?!=)").Count == 1 &&
              spellbookInput.Contains("ToggleSpellbookThroughUiPanel();",
                  StringComparison.Ordinal) &&
              Count(spellbookInput, "ToggleSpellbookThroughUiPanel();") == 1 &&
              !System.Text.RegularExpressions.Regex.IsMatch(spellbookInput,
                  @"_spellbookOpen\s*=(?!=)") &&
              spellbook.Contains(
                  "if (ImGui.IsItemClicked()) SetSpellbookOpen(false);",
                  StringComparison.Ordinal),
            "SpellBookFrame native setter, binding toggle, or close-button semantics drift");

        Check(actionBars.Contains("ToggleCharacterPageThroughUiPanel();",
                  StringComparison.Ordinal) &&
              actionBars.Contains("ToggleSpellbookThroughUiPanel();", StringComparison.Ordinal) &&
              Count(actionBars, "ToggleCharacterPageThroughUiPanel();") == 1 &&
              Count(actionBars, "ToggleSpellbookThroughUiPanel();") == 1 &&
              !actionBars.Contains("_spellbookOpen =", StringComparison.Ordinal) &&
              settings.Contains(
                  "if (_spellbookOpen) { SetSpellbookOpen(false); return true; }",
                  StringComparison.Ordinal),
            "normal microbutton or escape close paths bypass the bounded host setters");

        Check(devParity.Contains(
                  "if (panel == \"spellbook\") { _spellbookOpen = true; _characterOpen = false; }",
                  StringComparison.Ordinal) &&
              liveRun.Contains(
                  "private bool OpenLiveCharacter() { _characterOpen = true; _paperDollDirty = true; return true; }",
                  StringComparison.Ordinal) &&
              liveRun.Contains(
                  "private bool OpenLiveSpellbook() { _spellbookLine = 0; _spellbookPage = 0; _spellbookOpen = true; return true; }",
                  StringComparison.Ordinal) &&
              !devParity.Contains("ThroughUiPanel", StringComparison.Ordinal) &&
              !liveRun.Contains("ThroughUiPanel", StringComparison.Ordinal),
            "diagnostic/dev direct-open paths were accidentally admitted to UI-panel authority");
    }

    private sealed class HostTransitionRig
    {
        public sealed class Surface(UiPanelOwnershipLaw.Panel panel)
        {
            public UiPanelOwnershipLaw.Panel Panel { get; } = panel;
            public bool Visible { get; set; }
            public bool ShowPreflightAccepted { get; set; } = true;
            public bool DisplacePreflightAccepted { get; set; } = true;
            public bool ShowAccepted { get; set; } = true;
            public bool DisplaceAccepted { get; set; } = true;
            public bool ShowMutates { get; set; } = true;
            public bool DisplaceMutates { get; set; } = true;
            public bool MutateVisibleDuringShowPreflight { get; set; }
        }

        private readonly UiPanelOwnershipObserver _observer = new();

        public Surface Character { get; } = new(UiPanelOwnershipAdapterClinicalChecks.Character);
        public Surface SpellBook { get; } = new(UiPanelOwnershipAdapterClinicalChecks.SpellBook);
        public bool NativeCenterVisible { get; set; }
        public bool FullscreenVisible { get; set; }
        public bool MerchantVisible { get; set; }
        public bool BankVisible { get; set; }
        public bool PlayerDeadOrGhost { get; set; }
        public List<string> Calls { get; } = [];

        public UiPanelHostTransition.Result Show(UiPanelOwnershipLaw.Panel incoming)
        {
            Calls.Clear();
            UiPanelHostTransition.Host character = HostFor(Character);
            UiPanelHostTransition.Host spellBook = HostFor(SpellBook);
            UiPanelHostTransition.Host selected = incoming == Character.Panel
                ? character
                : incoming == SpellBook.Panel
                    ? spellBook
                    : new(incoming, () => true, () => true, () => true, () => true);
            return UiPanelHostTransition.Show(
                _observer, Capture, selected, [character, spellBook]);
        }

        public UiPanelOwnershipObservation ObserveNow() => _observer.Observe(Capture());

        private UiPanelHostTransition.Host HostFor(Surface surface) =>
            new(
                surface.Panel,
                () =>
                {
                    Calls.Add($"preflight-show:{surface.Panel.Id}");
                    if (surface.MutateVisibleDuringShowPreflight) surface.Visible = true;
                    return surface.ShowPreflightAccepted;
                },
                () =>
                {
                    Calls.Add($"show:{surface.Panel.Id}");
                    if (surface.ShowMutates) surface.Visible = true;
                    return surface.ShowAccepted;
                },
                () =>
                {
                    Calls.Add($"preflight-displace:{surface.Panel.Id}");
                    return surface.DisplacePreflightAccepted;
                },
                () =>
                {
                    Calls.Add($"displace:{surface.Panel.Id}");
                    if (surface.DisplaceMutates) surface.Visible = false;
                    return surface.DisplaceAccepted;
                });

        private UiPanelOwnershipSample Capture()
        {
            var visible = new List<UiPanelOwnershipLaw.Panel>(4);
            if (Character.Visible) visible.Add(Character.Panel);
            if (SpellBook.Visible) visible.Add(SpellBook.Panel);
            if (MerchantVisible) visible.Add(Merchant);
            if (BankVisible) visible.Add(Bank);
            if (NativeCenterVisible) visible.Add(GameMenu);
            if (FullscreenVisible) visible.Add(WorldMap);
            return new(visible, PlayerDeadOrGhost);
        }
    }

    private static IEnumerable<UiPanelOwnershipLaw.EffectKind> EffectKinds(
        UiPanelOwnershipObservation observation) =>
        observation.AdvisoryEffects.Select(effect => effect.Kind);

    private static string AdapterSource() => SourceText.Read(Path.Combine(
        ClientConfig.FindRepoRoot(), "MSUIClient", "Program.UiPanelOwnership.cs"));

    private static int Count(string source, string value)
    {
        int count = 0;
        for (int at = source.IndexOf(value, StringComparison.Ordinal);
             at >= 0;
             at = source.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string Slice(string source, string start, string end)
    {
        string normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        int first = normalized.IndexOf(start, StringComparison.Ordinal);
        int last = normalized.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Check(first >= 0 && last > first, $"source-fence slice missing: {start}");
        return normalized[first..last];
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
