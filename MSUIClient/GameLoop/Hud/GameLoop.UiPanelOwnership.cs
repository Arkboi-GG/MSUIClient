using System.Numerics;
using MSUIClient.Engine.UI;

namespace MSUIClient;

public sealed partial class GameLoop
{
    // Frozen build-5875 registered-panel descriptors for the 21 MSUI host surfaces that have a
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
        IncludeWhen(_socialOpen, 14);
        IncludeWhen(_macroOpen, 15);
        IncludeWhen(_professionOpen && _professionPanelKind == ProfessionPanelKind.TradeSkill, 16);
        IncludeWhen(_professionOpen && _professionPanelKind == ProfessionPanelKind.Craft, 17);
        IncludeWhen(_settingsOpen && _menuPage == MenuPage.GameMenu, 18);
        IncludeWhen(_settingsOpen && _menuPage != MenuPage.GameMenu, 19);
        IncludeWhen(_worldMapOpen, 20);

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

    private void ObserveUiPanelOwnership() =>
        _uiPanelOwnershipObservation = _uiPanelOwnershipObserver.Observe(
            CaptureUiPanelOwnershipSample());

    /// <summary>
    /// Returns the frozen SetLeftFrame/SetCenterFrame seat for a registered gameplay panel.
    /// Unknown/first-frame observations conservatively use the left seat until the next census;
    /// they never invent a center owner.
    /// </summary>
    private Vector2 UiPanelFrameOrigin(UiPanelOwnershipLaw.Panel panel, float scale)
    {
        if (_uiPanelOwnershipObservation is
                { Confidence: UiPanelObservationConfidence.Known } observation &&
            UiPanelOwnershipLaw.TryLogicalSeatOrigin(observation.Seats, panel,
                out Vector2 logicalOrigin))
            return logicalOrigin * scale;

        return new Vector2(UiPanelOwnershipLaw.LeftSeatX,
            UiPanelOwnershipLaw.PanelTop) * scale;
    }

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
        _spellbookOpen ? SetSpellbookOpen(false) : OpenSpellbookThroughUiPanel();

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
