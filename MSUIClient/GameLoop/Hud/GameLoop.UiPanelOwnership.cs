using System.Numerics;
using MSUIClient.Engine.UI;

namespace MSUIClient;

public sealed partial class GameLoop
{
    // Frozen build-5875 registered-panel descriptors for the 22 MSUI host surfaces that have a
    // current visibility predicate. Unregistered and special-purpose frames are intentionally not
    // present. The two profession descriptors remain separate and are selected only from exact
    // retained opener provenance; diagnostic/manual profession opens remain unresolved.
    private static readonly UiPanelOwnershipLaw.Panel[] UiPanelOwnershipRegistry =
    [
        new("BenillaGossipFrame", UiPanelOwnershipLaw.Area.Left),
        new("BenillaMerchantFrame", UiPanelOwnershipLaw.Area.Left),
        new("BenillaMailFrame", UiPanelOwnershipLaw.Area.Left),
        new("BenillaTradeFrame", UiPanelOwnershipLaw.Area.Left, 1),
        new("BenillaTrainerFrame", UiPanelOwnershipLaw.Area.Left),
        new("BenillaBankFrame", UiPanelOwnershipLaw.Area.Left, 6),
        new("BenillaTaxiFrame", UiPanelOwnershipLaw.Area.Left),
        new("BenillaQuestFrame", UiPanelOwnershipLaw.Area.Left),
        new("BenillaQuestLogFrame", UiPanelOwnershipLaw.Area.Left),
        new("BenillaLootFrame", UiPanelOwnershipLaw.Area.Left, 7),
        new("BenillaCharacterFrame", UiPanelOwnershipLaw.Area.Left),
        new("BenillaInspectFrame", UiPanelOwnershipLaw.Area.Left),
        new("BenillaSpellBookFrame", UiPanelOwnershipLaw.Area.Left),
        new("BenillaTalentFrame", UiPanelOwnershipLaw.Area.Left, 6),
        new("BenillaFriendsFrame", UiPanelOwnershipLaw.Area.Left, WhileDead: true),
        new("BenillaMacroFrame", UiPanelOwnershipLaw.Area.Left, 5, WhileDead: true),
        new("BenillaTradeSkillFrame", UiPanelOwnershipLaw.Area.Left, 3),
        new("BenillaCraftFrame", UiPanelOwnershipLaw.Area.Left, 4),
        new("DressUpFrame", UiPanelOwnershipLaw.Area.Left, 2),
        new("GameMenuFrame", UiPanelOwnershipLaw.Area.Center, WhileDead: true),
        new("OptionsFrame", UiPanelOwnershipLaw.Area.Center, WhileDead: true),
        new("WorldMapFrame", UiPanelOwnershipLaw.Area.Fullscreen, WhileDead: true),
    ];

    private readonly UiPanelOwnershipObserver _uiPanelOwnershipObserver = new();
    private UiPanelOwnershipObservation? _uiPanelOwnershipObservation;

    /// <summary>
    /// Samples current host-owned predicates into a conservative shadow. This method deliberately
    /// does not close/open a panel, dispatch an advisory effect, or invoke any host callback.
    /// </summary>
    private UiPanelOwnershipSample CaptureUiPanelOwnershipSample()
    {
        var visible = new List<UiPanelOwnershipLaw.Panel>(3);
        void IncludeWhen(bool predicate, int registryIndex)
        {
            if (predicate) visible.Add(UiPanelOwnershipRegistry[registryIndex]);
        }

        IncludeWhen(_gossipMenu is not null || _gossipGreeting is not null, 0);
        IncludeWhen(_vendor is not null, 1);
        IncludeWhen(_mailOpen, 2);
        IncludeWhen(_tradeOpen, 3);
        IncludeWhen(_trainer is not null, 4);
        IncludeWhen(_bankOpen, 5);
        IncludeWhen(_taxiOpen, 6);
        IncludeWhen(QuestNpcPanelNow() != QuestNpcPanel.None, 7);
        IncludeWhen(_questLogOpen, 8);
        IncludeWhen(_loot.IsOpen, 9);
        IncludeWhen(_characterOpen, 10);
        IncludeWhen(_inspectOpen, 11);
        IncludeWhen(_spellbookOpen, 12);
        IncludeWhen(_talentOpen, 13);
        // Guild is the third FriendsFrame tab in FrameXML. The host renders it as a separate
        // surface, but it retains the registered FriendsFrame panel owner and seat.
        IncludeWhen(_socialOpen || _guildOpen, 14);
        IncludeWhen(_macroOpen, 15);
        IncludeWhen(_professionOpen && _professionPanelKind == ProfessionPanelKind.TradeSkill, 16);
        IncludeWhen(_professionOpen && _professionPanelKind == ProfessionPanelKind.Craft, 17);
        IncludeWhen(_dressUpOpen, 18);
        IncludeWhen(_settingsOpen && _menuPage == MenuPage.GameMenu, 19);
        IncludeWhen(_settingsOpen && _menuPage != MenuPage.GameMenu, 20);
        IncludeWhen(_worldMapOpen, 21);

        // Diagnostic/manual opens deliberately retain no opener kind. Do not infer one from their
        // skill line (including Enchanting/333).
        string? unresolvedReason = _professionOpen && _professionPanelKind is null
            ? "profession-opener-kind-not-retained"
            : null;
        // The current in-world host exposes death through the streamed unit state. It retains no
        // separate ghost visibility bit, so this observer does not invent one.
        bool playerDeadOrGhost = _net is not null &&
            _entities.TryGet(ControlledGuid, out var player) && player.IsDead;
        return new UiPanelOwnershipSample(visible, playerDeadOrGhost, unresolvedReason);
    }

    private void ObserveUiPanelOwnership()
    {
        UiPanelOwnershipSample current = CaptureUiPanelOwnershipSample();
        if (_uiPanelOwnershipObservation is not
            { Confidence: UiPanelObservationConfidence.Known } before)
        {
            _uiPanelOwnershipObservation = _uiPanelOwnershipObserver.Observe(current);
            return;
        }

        string[] beforeIds = before.VisibleIds.ToArray();
        string[] currentIds = current.VisibleRegistered.Select(panel => panel.Id).ToArray();
        string[] added = currentIds.Except(beforeIds, StringComparer.Ordinal).ToArray();
        string[] removed = beforeIds.Except(currentIds, StringComparer.Ordinal).ToArray();

        // A surface may flip its own native flag during packet/input handling. Before anything
        // renders, run that single incoming edge through ShowUIPanel and perform only the host
        // effects the law asks for. This is what prevents two zero-push panels from both falling
        // back to x=0 after the read-only observer quite correctly rejects the overlap.
        if (added.Length == 1)
        {
            UiPanelOwnershipLaw.Panel incoming = current.VisibleRegistered.First(
                panel => string.Equals(panel.Id, added[0], StringComparison.Ordinal));
            UiPanelOwnershipLaw.Transition planned = UiPanelOwnershipLaw.Show(
                before.Seats, incoming, playerDeadOrGhost: current.PlayerDeadOrGhost);

            if (planned.Outcome == UiPanelOwnershipLaw.Outcome.Opened)
            {
                ApplyUiPanelEffects(planned.Effects, incoming.Id);
                UiPanelOwnershipSample after = CaptureUiPanelOwnershipSample();
                _uiPanelOwnershipObservation = _uiPanelOwnershipObserver.ConfirmPlannedTransition(
                    after, planned.Seats, planned.Effects,
                    $"host-reconciled-show:{incoming.Id}");
                return;
            }

            if (planned.Outcome is UiPanelOwnershipLaw.Outcome.RefusedByNativeCenter or
                UiPanelOwnershipLaw.Outcome.RefusedByFullscreen or
                UiPanelOwnershipLaw.Outcome.RefusedWhileDead)
            {
                _ = TryCloseRegisteredUiPanel(incoming.Id);
                _uiPanelOwnershipObservation = _uiPanelOwnershipObserver.ConfirmPlannedTransition(
                    CaptureUiPanelOwnershipSample(), before.Seats, [],
                    $"host-refused-show:{incoming.Id}:{planned.Outcome}");
                return;
            }
        }

        // Native code is allowed to have completed an atomic replace itself. Confirm it only if
        // the same one-added transition produces exactly the visible final seat set.
        if (added.Length == 1 && removed.Length > 0)
        {
            UiPanelOwnershipLaw.Panel incoming = current.VisibleRegistered.First(
                panel => string.Equals(panel.Id, added[0], StringComparison.Ordinal));
            UiPanelOwnershipLaw.Transition planned = UiPanelOwnershipLaw.Show(
                before.Seats, incoming, playerDeadOrGhost: current.PlayerDeadOrGhost);
            if (planned.Outcome == UiPanelOwnershipLaw.Outcome.Opened &&
                PlannedSeatsMatch(current, planned.Seats))
            {
                _uiPanelOwnershipObservation = _uiPanelOwnershipObserver.ConfirmPlannedTransition(
                    current, planned.Seats, planned.Effects,
                    $"host-confirmed-atomic-show:{incoming.Id}");
                return;
            }
        }

        _uiPanelOwnershipObservation = _uiPanelOwnershipObserver.Observe(current);
    }

    private static bool PlannedSeatsMatch(
        UiPanelOwnershipSample sample, UiPanelOwnershipLaw.Seats seats)
    {
        string[] expected = new[] { seats.Left, seats.Center, seats.Fullscreen }
            .Where(panel => panel is not null)
            .Select(panel => panel!.Value.Id)
            .ToArray();
        string[] actual = sample.VisibleRegistered.Select(panel => panel.Id).ToArray();
        return expected.Length == actual.Length &&
            expected.All(id => actual.Contains(id, StringComparer.Ordinal));
    }

    private void ApplyUiPanelEffects(
        IReadOnlyList<UiPanelOwnershipLaw.Effect> effects,
        string alreadyVisibleIncoming)
    {
        foreach (UiPanelOwnershipLaw.Effect effect in effects)
        {
            switch (effect.Kind)
            {
                case UiPanelOwnershipLaw.EffectKind.Hide when
                    effect.PanelId is { } id &&
                    !string.Equals(id, alreadyVisibleIncoming, StringComparison.Ordinal):
                    _ = TryCloseRegisteredUiPanel(id);
                    break;
                case UiPanelOwnershipLaw.EffectKind.CloseEscapeContainers:
                    CloseAllBagWindows();
                    break;
                case UiPanelOwnershipLaw.EffectKind.CloseAllBags:
                    CloseAllNormalBagWindows();
                    break;
            }
        }
    }

    /// <summary>
    /// Runtime CloseWindows/CloseAllWindows. The pure law captures the original
    /// three owners and fixes the order; this adapter merely invokes each
    /// surface's existing close behavior and confirms the final census.
    /// </summary>
    private bool TryCloseRegisteredUiPanels(
        bool closeEscapeContainers,
        bool ignoreNativeCenter = false)
    {
        UiPanelOwnershipSample current = CaptureUiPanelOwnershipSample();
        UiPanelOwnershipObservation observation = _uiPanelOwnershipObservation is
            { Confidence: UiPanelObservationConfidence.Known } known &&
            known.VisibleIds.SequenceEqual(
                current.VisibleRegistered.Select(panel => panel.Id))
            ? known
            : _uiPanelOwnershipObserver.Observe(current);
        if (observation.Confidence != UiPanelObservationConfidence.Known)
        {
            _uiPanelOwnershipObservation = observation;
            return false;
        }

        UiPanelOwnershipLaw.Transition planned = closeEscapeContainers
            ? UiPanelOwnershipLaw.CloseAllWindows(observation.Seats, ignoreNativeCenter)
            : UiPanelOwnershipLaw.CloseWindows(observation.Seats, ignoreNativeCenter);
        bool containersVisible = closeEscapeContainers &&
            (_backpackOpen || _keyringOpen || _equippedBagOpen.Any(open => open) ||
             _bankBagOpen.Any(open => open));

        ApplyUiPanelEffects(planned.Effects, alreadyVisibleIncoming: "");
        // UISpecialFrames follows the captured panel owners. ItemRefTooltip is
        // MSUI's one implemented member; ColorPicker has no runtime consumer.
        bool specialVisible = CloseItemRefTooltip();
        UiPanelOwnershipSample after = CaptureUiPanelOwnershipSample();
        _uiPanelOwnershipObservation = _uiPanelOwnershipObserver.ConfirmPlannedTransition(
            after, planned.Seats, planned.Effects,
            closeEscapeContainers ? "host-close-all-windows" : "host-close-windows");
        return planned.FoundAny || containersVisible || specialVisible;
    }

    private bool TryCloseRegisteredUiPanel(string id)
    {
        switch (id)
        {
            case "BenillaGossipFrame":
                if (_gossipMenu is null && _gossipGreeting is null) return false;
                ResetGossip(); return true;
            case "BenillaMerchantFrame": return CloseVendorSession();
            case "BenillaMailFrame":
                if (!_mailOpen) return false; CloseMailSession(); return true;
            case "BenillaTradeFrame":
                if (!_tradeOpen) return false; _net?.CancelTrade(); ResetTrade(); return true;
            case "BenillaTrainerFrame": return CloseTrainerSession();
            case "BenillaBankFrame": return CloseBankSession();
            case "BenillaTaxiFrame": return CloseTaxiMap();
            case "BenillaQuestFrame":
                if (QuestNpcPanelNow() == QuestNpcPanel.None) return false;
                CloseQuestNpcFrame(playSound: true); return true;
            case "BenillaQuestLogFrame":
                if (!_questLogOpen) return false; _questLogOpen = false; return true;
            case "BenillaLootFrame": return TryCloseLootOnEscape();
            case "BenillaCharacterFrame": return SetCharacterPageOpen(false);
            case "BenillaInspectFrame":
                if (!_inspectOpen) return false; CloseInspect(playSound: true); return true;
            case "BenillaSpellBookFrame": return SetSpellbookOpen(false);
            case "BenillaTalentFrame":
                if (!_talentOpen) return false; _talentOpen = false; return true;
            case "BenillaFriendsFrame":
                return CloseFriendsFrame();
            case "BenillaMacroFrame":
                if (!_macroOpen) return false; CloseMacros(); return true;
            case "BenillaTradeSkillFrame":
            case "BenillaCraftFrame":
                return CloseProfessionFrame();
            case "DressUpFrame":
                if (!_dressUpOpen) return false; CloseDressUp(); return true;
            case "GameMenuFrame":
            case "OptionsFrame": return CloseSettingsUiPanel();
            case "WorldMapFrame":
                if (!_worldMapOpen) return false; _worldMapOpen = false; return true;
            default: return false;
        }
    }

    private bool CloseSettingsUiPanel()
    {
        if (!_settingsOpen) return false;
        _settingsOpen = false;
        _optionsSearch = "";
        _settingsPopupCloseRequested = true;
        PlayUiSound(GameMenuUiLaw.EscapeCloseSound);
        if (!_settingsCancelling) CommitSettings();
        _settingsCancelling = false;
        return true;
    }

    /// <summary>
    /// Returns the frozen SetLeftFrame/SetCenterFrame seat for a registered gameplay panel.
    /// Unknown/first-frame observations conservatively use the left seat until the next census;
    /// they never invent a center owner.
    /// </summary>
    private Vector2 UiPanelFrameLogicalOrigin(UiPanelOwnershipLaw.Panel panel)
    {
        if (_uiPanelOwnershipObservation is
                { Confidence: UiPanelObservationConfidence.Known } observation &&
            UiPanelOwnershipLaw.TryLogicalSeatOrigin(observation.Seats, panel,
                out Vector2 logicalOrigin))
            return logicalOrigin;

        return new Vector2(UiPanelOwnershipLaw.LeftSeatX,
            UiPanelOwnershipLaw.PanelTop);
    }

    private Vector2 UiPanelFrameOrigin(UiPanelOwnershipLaw.Panel panel, float scale) =>
        UiPanelFrameLogicalOrigin(panel) * scale;

    /// <summary>
    /// The first authoritative host wedge is deliberately limited to the ordinary zero-push
    /// CharacterFrame/SpellBookFrame replacement pair. Every callback below remains owned by its
    /// native surface; the coordinator only accepts observer-confirmed censuses.
    /// </summary>
    private bool ToggleCharacterPageThroughUiPanel(string soundCategory = "ui") =>
        _characterOpen
            ? SetCharacterPageOpen(false, soundCategory: soundCategory)
            : OpenCharacterPageThroughUiPanel(soundCategory);

    private bool OpenCharacterPageThroughUiPanel(
        string soundCategory = "ui",
        int? requestedTab = null)
    {
        UiPanelHostTransition.Host character = CharacterUiPanelHost(
            soundCategory, requestedTab);
        return ShowCharacterSpellbookHost(character, character, SpellbookUiPanelHost());
    }

    private bool ToggleSpellbookThroughUiPanel() =>
        ToggleSpellbookTypeThroughUiPanel(petBook: false);

    private bool TogglePetSpellbookThroughUiPanel() =>
        ToggleSpellbookTypeThroughUiPanel(petBook: true);

    private bool ToggleSpellbookTypeThroughUiPanel(bool petBook)
    {
        if (petBook && !HasPetBookSpells) return false;
        if (_spellbookOpen && _spellbookPetBook == petBook)
            return SetSpellbookOpen(false);
        if (_spellbookOpen)
        {
            SetSpellbookOpen(false);
            _spellbookPetBook = petBook;
            return SetSpellbookOpen(true);
        }
        _spellbookPetBook = petBook;
        return OpenSpellbookThroughUiPanel();
    }

    private bool OpenSpellbookThroughUiPanel()
    {
        UiPanelHostTransition.Host spellbook = SpellbookUiPanelHost();
        return ShowCharacterSpellbookHost(
            spellbook, CharacterUiPanelHost(), spellbook);
    }

    private bool ShowCharacterSpellbookHost(
        UiPanelHostTransition.Host incoming,
        UiPanelHostTransition.Host character,
        UiPanelHostTransition.Host spellbook)
    {
        UiPanelHostTransition.Result result = UiPanelHostTransition.Show(
            _uiPanelOwnershipObserver,
            CaptureUiPanelOwnershipSample,
            incoming,
            [character, spellbook]);
        _uiPanelOwnershipObservation = result.Observation;
        return result.Succeeded;
    }

    private UiPanelHostTransition.Host CharacterUiPanelHost(
        string openSoundCategory = "ui",
        int? requestedTab = null) =>
        new(
            UiPanelOwnershipRegistry[10],
            () => !_characterOpen,
            () =>
            {
                if (requestedTab is { } tab) _characterTab = tab;
                return SetCharacterPageOpen(true, soundCategory: openSoundCategory);
            },
            () => _characterOpen,
            () => SetCharacterPageOpen(false));

    private UiPanelHostTransition.Host SpellbookUiPanelHost() =>
        new(
            UiPanelOwnershipRegistry[12],
            () => !_spellbookOpen,
            () => SetSpellbookOpen(true),
            () => _spellbookOpen,
            () => SetSpellbookOpen(false));
}
