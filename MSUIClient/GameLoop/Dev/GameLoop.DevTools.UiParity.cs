using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record UiParityDrawTrace(string TexturePath, uint Color, string DrawLayer,
        string Point, string RelativeTo, string RelativePoint, float OffsetX, float OffsetY,
        string FontPath = "", float FontSize = 0, string TexCoords = "",
        Vector4? ContentRect = null, Vector4? ClipRect = null, string ClipMask = "",
        string BlendMode = "", bool Visible = true, bool? Enabled = null,
        string InteractionState = "", Vector2? HitMin = null, Vector2? HitMax = null,
        string Strata = "");
    private sealed record UiParityActualRow(string[] Values);
    private readonly List<UiParityActualRow> _uiParityRows = [];
    private bool _uiParityArmed;
    private bool _uiParityFrameSeen;
    private int _uiParityPresentedFrames;
    private string _uiParityPanel = "";
    private string _uiParityStamp = "";
    private Vector2 _uiParityOrigin;
    private float _uiParityLogicalScale = 1f;
    private int _uiParityDrawIndex;
    private string _uiParityCompletedPanel = "";
    private string _uiParityCompletedManifest = "";
    private string _uiParityCaptureError = "";
    private int _uiParityEquippedBagContainer = 1;
    private string _uiParityEnchantConfirmRequestedState = "";
    private bool _uiParityFixtureStaged;
    private bool _multiActionProtocolFixtureStaged;
    private bool _multiActionUiFixtureRestorePending;
    private ActionSlot? _multiActionUiFixtureLeft;
    private ActionSlot? _multiActionUiFixtureRight;
    private ActionSlot? _multiActionUiFixtureActionCursor;
    private bool _multiActionUiFixtureActionCursorChanged;
    private uint _multiActionUiFixtureDraggingSpell;
    private int _multiActionUiFixtureCarriedContainer = InventoryUiLaw.EmptyContainer;
    private int _multiActionUiFixtureCarriedSlot = -1;
    private int? _multiActionUiFixtureCarriedCount;
    private string _uiParityRequestedPanel = "";
    private string _uiParityCompletedProvenance = "";
    private string _uiParityCompletedScenario = "";
    private string _uiParityFrameScenarioSummary = "";
    private Dictionary<string, object?> _uiParityFrameScenario = [];

    private void ArmUiParityCapture(string panel, bool stageFixture = false)
    {
        // A synthetic MultiBars frame is transactional. If a previous staged capture was
        // interrupted or a second command arrives, put the real local actions/cursor back before
        // doing any validation or arming. Ordinary capture can therefore never inherit a fixture
        // and call it observed runtime state.
        RestoreMultiActionUiParityFixture();
        string requestedPanel = panel;
        // Dev-only capture affordance: "character-frame:N" opens the character frame at tab N
        // (0 Character, 2 Reputation, 3 Skills, 4 Honor) only in explicit fixture mode. The
        // suffix still selects telemetry in observational mode; it never changes the live tab.
        int characterTab = 0;
        int equippedBagContainer = 1;
        string enchantConfirmState = "";
        string questFrameState = "";
        if (panel.StartsWith("character-frame:", StringComparison.Ordinal))
        {
            int.TryParse(panel["character-frame:".Length..], out characterTab);
            panel = "character-frame";
        }
        if (panel.StartsWith("equipped-bag:", StringComparison.Ordinal))
        {
            int.TryParse(panel["equipped-bag:".Length..], out equippedBagContainer);
            equippedBagContainer = Math.Clamp(equippedBagContainer, 1, 4);
            panel = "equipped-bag";
        }
        if (panel.StartsWith("enchant-confirm:", StringComparison.OrdinalIgnoreCase))
        {
            enchantConfirmState = panel["enchant-confirm:".Length..].ToLowerInvariant();
            if (enchantConfirmState is not ("bind" or "replace")) return;
            panel = "enchant-confirm";
        }
        if (panel.StartsWith("quest-frame:", StringComparison.OrdinalIgnoreCase))
        {
            questFrameState = panel["quest-frame:".Length..].ToLowerInvariant();
            if (questFrameState is not ("greeting" or "detail" or "progress" or "reward")) return;
            panel = "quest-frame";
        }
        if (!_config.DevTools || panel is not ("game-menu" or "options" or "keybindings" or "macro" or "tooltip" or "ui-errors" or "static-popup" or "player-frame" or "target-frame" or "party-frame" or "party-invite" or
            "action-bar" or "action-button" or "multi-action-bar" or "pet-action-bar" or "cast-bar" or "buff-frame" or "minimap" or "chat-frame" or "reputation-bar" or "backpack" or "bag-bar" or "equipped-bag" or "enchant-confirm" or "inspect-frame" or "skill-frame" or "character-frame" or "spellbook" or "talent-frame" or "quest-log" or "quest-frame" or "merchant" or "trainer" or "bank" or "mail" or "auction" or "loot" or "guild" or "gossip" or "taxi" or "trade" or "social" or "social-who")) return;
        _uiParityPanel = panel;
        _uiParityStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        _uiParityRows.Clear(); _uiParityArmed = true; _uiParityFrameSeen = false; _uiParityPresentedFrames = 0;
        _uiParityDrawIndex = 0; _uiParityCompletedPanel = ""; _uiParityCompletedManifest = "";
        _uiParityCaptureError = "";
        _uiParityEquippedBagContainer = equippedBagContainer;
        _uiParityEnchantConfirmRequestedState = enchantConfirmState;
        _uiParityFixtureStaged = stageFixture;
        _uiParityRequestedPanel = requestedPanel;
        _uiParityCompletedProvenance = "";
        _uiParityCompletedScenario = "";
        _uiParityFrameScenarioSummary = "";
        _uiParityFrameScenario.Clear();

        // Party evidence is observational only. Synthetic roster/invite commands previously
        // leaked into later captures and could be mislabeled as live runtime state.
        if (panel is "party-frame" or "party-invite")
        {
            if (stageFixture)
            {
                _uiParityArmed = false;
                _uiParityCaptureError = $"{panel}-rejects-staged-fixtures";
                return;
            }
            if (panel == "party-frame" && PartyFrameMembers().Length == 0)
            {
                _uiParityArmed = false;
                _uiParityCaptureError = "party-frame-requires-observed-wire-roster";
                return;
            }
            if (panel == "party-invite" &&
                !PartyFrameUiLaw.IsPartyInviteVisible(_staticPopupSlots))
            {
                _uiParityArmed = false;
                _uiParityCaptureError = "party-invite-requires-observed-inbound-invitation";
                return;
            }
        }

        // `ui-parity` is an observer: arming telemetry must not open/close a panel or rewrite
        // runtime state. Deterministic synthetic presentation is an explicit `ui-parity-stage`
        // operation and is labelled as such in the manifest.
        if (!stageFixture) return;

        // Inspect has no truthful synthetic player target. A staged capture would either invent
        // public equipment fields or send CMSG_INSPECT, so it is rejected rather than mislabeled.
        if (panel == "inspect-frame")
        {
            _uiParityArmed = false;
            _uiParityCaptureError = "inspect-frame-requires-observed-runtime-state";
            return;
        }
        // SkillFrame must be proved from the authenticated player's PLAYER_SKILL_INFO fields.
        // Staging the pane, selection, abandon flag, or popup would erase the very state/wire
        // boundary this packet is meant to establish.
        if (panel == "skill-frame")
        {
            _uiParityArmed = false;
            _uiParityCaptureError = "skill-frame-requires-observed-player-skill-state";
            return;
        }
        // A player-shaped fixture is not a pet. PetActionBar proof must originate in an
        // authenticated world after SMSG_PET_SPELLS names a real controlled unit.
        if (panel == "pet-action-bar")
        {
            _uiParityArmed = false;
            _uiParityCaptureError = "pet-action-bar-requires-observed-controlled-unit-state";
            return;
        }

        if (panel == "game-menu") OpenSettings();
        if (panel == "options") { OpenSettings(); _menuPage=MenuPage.Video; }
        if (panel == "backpack") _backpackOpen = true;
        if (panel == "equipped-bag")
        {
            Array.Fill(_equippedBagOpen, false);
            if (_net is not null && _entities.TryGet(_net.PlayerGuid, out var bagPlayer))
                for (int i = 0; i < _equippedBagOpen.Length; i++)
                    _equippedBagOpen[i] = bagPlayer.Fields.PlayerInventorySlot(19 + i) != 0;
        }
        if (panel == "character-frame") { _characterOpen = true; _characterTab = characterTab; _paperDollDirty = true; }
        if (panel == "spellbook") { _spellbookOpen = true; _characterOpen = false; }
        if (panel == "talent-frame") _talentOpen = true;
        if (panel == "social") { _socialOpen = true; _socialPage = 0; _showIgnore = false; }
        if (panel == "social-who") { _socialOpen = true; _socialPage = 1; _net?.Who(""); }
        if (panel == "quest-log") _questLogOpen = true;
        if (panel == "quest-frame") StageQuestFrameProof(
            questFrameState.Length == 0 ? "greeting" : questFrameState);
        if (panel == "trade") _tradeOpen = true;
        if (panel == "keybindings") _keybindingsOpen = true;
        if (panel == "macro") _macroOpen = true;
        if (panel == "tooltip") _tooltipParityOpen = true;
        if (panel == "ui-errors") _uiErrorsParityOpen = true;
        if (panel == "static-popup") _staticPopupParityOpen = true;
        if (panel == "multi-action-bar")
        {
            // Explicit fixture only: two occupied seats plus a carried spell expose both
            // UI-Quickslot2 and the cursor-only UI-Quickslot wells. Snapshot every cursor field
            // this fixture masks and restore it after the evidence is persisted. No packet is sent.
            _multiActionUiFixtureLeft = _actions[MultiActionBarUiLaw.BottomLeftBase];
            _multiActionUiFixtureRight = _actions[MultiActionBarUiLaw.BottomRightBase];
            _multiActionUiFixtureActionCursor = _actionCursor;
            _multiActionUiFixtureActionCursorChanged = _actionCursorChangedThisFrame;
            _multiActionUiFixtureDraggingSpell = _draggingSpellId;
            _multiActionUiFixtureCarriedContainer = _carriedContainer;
            _multiActionUiFixtureCarriedSlot = _carriedSlot;
            _multiActionUiFixtureCarriedCount = _carriedCount;
            _multiActionUiFixtureRestorePending = true;
            _actions.Set(MultiActionBarUiLaw.BottomLeftBase,
                new ActionSlot(ActionSlot.Spell, 1459));
            _actions.Set(MultiActionBarUiLaw.BottomRightBase,
                new ActionSlot(ActionSlot.Spell, 6603));
            _actionCursor = null;
            _actionCursorChangedThisFrame = false;
            _carriedContainer = InventoryUiLaw.EmptyContainer;
            _carriedSlot = -1;
            _carriedCount = null;
            _draggingSpellId = 1459;
        }
        if (panel == "cast-bar")
        {
            _castBarSpell = 133; // deterministic Fireball fixture; never used by observational capture
            _castBarPhase = CastBarPhase.Casting;
            _castBarText = "Fireball";
            _castBarStarted = NowSeconds() - 2;
            _castBarEnds = NowSeconds() + 2;
            _castBarFinishedAt = 0;
            _castBarDisplayUntil = 0;
            _castBarPushbackTotalMs = 0;
        }
    }

    private string UiParityProvenance => _uiParityPanel is "party-frame" or "party-invite"
        ? "observed-party-wire-runtime"
        : _uiParityFixtureStaged
        ? "explicit-ui-parity-fixture"
        : _uiParityPanel == "multi-action-bar" && _multiActionProtocolFixtureStaged
            ? "explicit-live-protocol-fixture"
            : "observed-runtime-state";

    private void RestoreMultiActionUiParityFixture()
    {
        if (!_multiActionUiFixtureRestorePending) return;
        _actions.Set(MultiActionBarUiLaw.BottomLeftBase, _multiActionUiFixtureLeft);
        _actions.Set(MultiActionBarUiLaw.BottomRightBase, _multiActionUiFixtureRight);
        _actionCursor = _multiActionUiFixtureActionCursor;
        _actionCursorChangedThisFrame = _multiActionUiFixtureActionCursorChanged;
        _draggingSpellId = _multiActionUiFixtureDraggingSpell;
        _carriedContainer = _multiActionUiFixtureCarriedContainer;
        _carriedSlot = _multiActionUiFixtureCarriedSlot;
        _carriedCount = _multiActionUiFixtureCarriedCount;
        _multiActionUiFixtureLeft = null;
        _multiActionUiFixtureRight = null;
        _multiActionUiFixtureActionCursor = null;
        _multiActionUiFixtureRestorePending = false;
    }

    private string MultiActionCursorKind => HasCarriedItem ? "item"
        : _actionCursor is { } action ? action.Kind switch
        {
            ActionSlot.Spell => "action-spell",
            ActionSlot.Item => "action-item",
            ActionSlot.Macro => "action-macro",
            _ => "action-unknown",
        }
        : _draggingSpellId != 0 ? "spell"
        : _draggingMacroId != 0 ? "macro"
        : _draggingPetAction.HasValue ? "pet-action"
        : "none";

    private sealed record SkillFrameParityState(bool CharacterOpen, int CharacterTab,
        int SkillCount, int HeaderCount, int RowCount, int Scroll, int MaximumScroll,
        uint SelectedSkill, bool SelectedPresent, bool SelectedExpanded,
        bool SelectedAbandonable, string CollapsedCategoryIds, bool PopupPresent,
        uint PopupSkill, bool PopupMatchesSelection);

    private SkillFrameParityState CurrentSkillFrameUiParityState()
    {
        WorldEntity? player = _net is { IsInWorld: true } net &&
            _entities.TryGet(net.PlayerGuid, out WorldEntity found) ? found : null;
        var categories = new HashSet<uint>();
        var skillsByCategory = new Dictionary<uint, int>();
        bool selectedPresent = false;
        int skillCount = 0;
        if (player is not null && _skillLines is not null)
            for (int slot = 0; slot < 128; slot++)
            {
                ushort field = (ushort)(ObjectFields.PLAYER_SKILL_INFO_1_1 + slot * 3);
                uint id = (ushort)(player.Fields.GetU32(field) ?? 0);
                if (id == 0 || !_skillLines.TryGet(id, out var info) || info.CategoryId == 12)
                    continue;
                skillCount++;
                selectedPresent |= id == _selectedSkill;
                categories.Add(info.CategoryId);
                skillsByCategory[info.CategoryId] =
                    skillsByCategory.GetValueOrDefault(info.CategoryId) + 1;
            }
        int rowCount = categories.Count + skillsByCategory
            .Where(pair => !_collapsedSkillCategories.Contains(pair.Key))
            .Sum(pair => pair.Value);
        bool selectedExpanded = selectedPresent && SelectedSkillIsExpanded();
        bool selectedAbandonable = player is not null && selectedPresent &&
            SkillIsCurrentlyAbandonable(player, _selectedSkill);
        SkillUnlearnConfirmation? popup = _skillUnlearnConfirmation;
        string collapsed = string.Join('|', categories
            .Where(_collapsedSkillCategories.Contains).OrderBy(id => id));
        return new(_characterOpen, _characterTab, skillCount, categories.Count, rowCount,
            _skillScroll, SkillFrameUiLaw.MaximumScroll(rowCount), _selectedSkill,
            selectedPresent, selectedExpanded, selectedAbandonable, collapsed,
            popup is not null, popup?.SkillId ?? 0,
            popup is not null && popup.SkillId == _selectedSkill);
    }

    private string CurrentUiParityScenarioSummary()
    {
        if (_uiParityPanel == "enchant-confirm")
            return CurrentEnchantConfirmUiParityScenarioSummary();
        if (_uiParityPanel is "party-frame" or "party-invite")
        {
            PartyMember[] slots = PartyFrameMembers();
            (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? popup =
                PartyFrameUiLaw.PartyInvitePopup(_staticPopupSlots);
            StaticPopupCoordinatorLaw.Definition definition =
                popup?.Instance.Definition ?? PartyFrameUiLaw.PartyInvitePopupDefinition;
            string guids = string.Join('|', slots.Select(member => $"0x{member.Guid:X16}"));
            string sources = string.Join('|', slots.Select((member, index) =>
                $"{index + 1}:entity={_entities.TryGet(member.Guid, out _).ToString().ToLowerInvariant()}," +
                $"stats={_partyStats.ContainsKey(member.Guid).ToString().ToLowerInvariant()}"));
            return $"panel={_uiParityPanel};stateSource=party-wire-runtime;fixtureStaged=false;" +
                   "captureStateMutation=false;captureNetworkMutation=false;" +
                   $"fullRoster={_partyMembers.Count};ownSubgroup={_partyOwnFlags & 0x7f};" +
                   $"compactSlots={slots.Length};slotGuids={guids};slotSources={sources};" +
                   $"inviteVisible={(popup is not null).ToString().ToLowerInvariant()};" +
                   $"inviter={popup?.Instance.DataToken ?? ""};slot={popup?.Slot ?? 0};" +
                   $"type={popup?.Instance.Definition.Type ?? ""};timeLeftSeconds=" +
                   $"{(popup?.Instance.TimeLeft ?? 0).ToString("R", CultureInfo.InvariantCulture)};" +
                   $"definitionFlags=whileDead:{definition.WhileDead}," +
                   $"hideOnEscape:{definition.HideOnEscape},hasAccept:{definition.HasAccept}," +
                   $"hasCancel:{definition.HasCancel},hasOnShow:{definition.HasOnShow}," +
                   $"hasOnHide:{definition.HasOnHide},hasOnUpdate:{definition.HasOnUpdate}," +
                   $"hasEditBox:{definition.HasEditBox}," +
                   $"usesTimeoutText:{definition.UsesTimeoutText}," +
                   $"usesDelayText:{definition.UsesDelayText};integratedTypes=[PARTY_INVITE]";
        }
        if (_uiParityPanel == "inspect-frame")
            return $"panel=inspect-frame;stateSource=inspect-runtime;" +
                   $"inspectOpen={_inspectOpen.ToString().ToLowerInvariant()};" +
                   $"inspectedGuid=0x{_inspectGuid:X16};binding={_inspectBinding.Kind};" +
                   $"partyIndex={_inspectBinding.PartyIndex};selectionGuid=0x{_selectionGuid:X16};" +
                   "captureStateMutation=false;captureNetworkMutation=false";
        if (_uiParityPanel == "skill-frame")
        {
            SkillFrameParityState state = CurrentSkillFrameUiParityState();
            return $"panel=skill-frame;stateSource=player-skill-fields;" +
                   $"characterOpen={state.CharacterOpen.ToString().ToLowerInvariant()};" +
                   $"tab={state.CharacterTab};skills={state.SkillCount};headers={state.HeaderCount};" +
                   $"rows={state.RowCount};scroll={state.Scroll}/{state.MaximumScroll};" +
                   $"selected={state.SelectedSkill};selectedPresent={state.SelectedPresent.ToString().ToLowerInvariant()};" +
                   $"selectedExpanded={state.SelectedExpanded.ToString().ToLowerInvariant()};" +
                   $"popup={state.PopupPresent.ToString().ToLowerInvariant()};" +
                   "captureStateMutation=false;captureNetworkMutation=false";
        }
        if (_uiParityPanel is "game-menu" or "options")
            return $"panel={_uiParityPanel};requested={_uiParityRequestedPanel};" +
                   $"fixtureStaged={_uiParityFixtureStaged.ToString().ToLowerInvariant()};" +
                   $"menuOpen={_settingsOpen.ToString().ToLowerInvariant()};page={_menuPage};" +
                   $"logoutUiActive={LogoutUiActive.ToString().ToLowerInvariant()}";
        if (_uiParityPanel == "quest-frame")
            return $"panel=quest-frame;state={QuestNpcPanelNow()};giver=0x{QuestGiverGuid():X16};" +
                   $"stateSource={(_uiParityFixtureStaged ? "ui-parity-stage" : "quest-wire-runtime")};" +
                   $"fixtureStaged={_uiParityFixtureStaged.ToString().ToLowerInvariant()};" +
                   $"scroll={_questNpcScroll:R};contentHeight={_questNpcContentHeight:R};" +
                   $"choice={_questRewardChoice}";
        if (_uiParityPanel == "multi-action-bar")
        {
            int left = Enumerable.Range(MultiActionBarUiLaw.BottomLeftBase,
                MultiActionBarUiLaw.ButtonsPerBar).Count(slot => _actions[slot] is not null);
            int right = Enumerable.Range(MultiActionBarUiLaw.BottomRightBase,
                MultiActionBarUiLaw.ButtonsPerBar).Count(slot => _actions[slot] is not null);
            bool grid = HasCarriedItem || HasActionBarCursor;
            string source = _uiParityFixtureStaged ? "ui-parity-stage"
                : _multiActionProtocolFixtureStaged ? "live-protocol-fixture"
                : "action-runtime";
            return $"panel=multi-action-bar;stateSource={source};leftOccupied={left};" +
                   $"rightOccupied={right};gridShown={grid.ToString().ToLowerInvariant()};" +
                   $"cursor={MultiActionCursorKind};" +
                   $"captureStateMutation={_uiParityFixtureStaged.ToString().ToLowerInvariant()};" +
                   "captureNetworkMutation=false";
        }
        if (_uiParityPanel == "pet-action-bar")
        {
            bool descriptor = _petGuid != 0 && _entities.TryGet(_petGuid, out WorldEntity pet) &&
                pet.IsUnit;
            int named = _petActions.Count(word =>
            {
                uint action = PetActionBarUiLaw.Action(word);
                byte kind = PetActionBarUiLaw.Kind(word);
                return PetActionBarUiLaw.IsSpell(word)
                    ? _spellCatalog?.TryGet(action, out _) == true
                    : PetTokenName(action, kind) is not null;
            });
            return $"panel=pet-action-bar;stateSource=pet-wire-runtime;" +
                   $"petGuid=0x{_petGuid:X16};descriptorPresent={descriptor.ToString().ToLowerInvariant()};" +
                   $"namedSlots={named};attacking={_petAttacking.ToString().ToLowerInvariant()};" +
                   $"gridShown={_draggingPetAction.HasValue.ToString().ToLowerInvariant()};" +
                   "captureStateMutation=false;captureNetworkMutation=false";
        }
        if (_uiParityPanel != "cast-bar")
            return $"panel={_uiParityPanel};requested={_uiParityRequestedPanel};" +
                   $"fixtureStaged={_uiParityFixtureStaged.ToString().ToLowerInvariant()}";
        return $"panel=cast-bar;spellId={_castBarSpell};spellName={_castBarText};" +
               $"phase={_castBarPhase.ToString().ToUpperInvariant()};" +
               $"stateSource={(_uiParityFixtureStaged ? "ui-parity-stage" : "spell-lifecycle-runtime")};" +
               $"fixtureFractionForced={_uiParityFixtureStaged.ToString().ToLowerInvariant()}";
    }

    private Dictionary<string, object?> CurrentUiParityScenario(
        double? renderedAtClientSeconds = null, float? renderedFraction = null)
    {
        var scenario = new Dictionary<string, object?>
        {
            ["panel"] = _uiParityPanel,
            ["requestedPanel"] = _uiParityRequestedPanel,
            ["fixtureStaged"] = _uiParityFixtureStaged,
            ["inWorld"] = _net?.IsInWorld == true,
            ["networkState"] = _net?.State.ToString() ?? "unavailable",
        };
        if (_uiParityPanel == "enchant-confirm")
        {
            AddEnchantConfirmUiParityScenario(scenario);
            return scenario;
        }
        if (_uiParityPanel is "party-frame" or "party-invite")
        {
            PartyMember[] slots = PartyFrameMembers();
            (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? popup =
                PartyFrameUiLaw.PartyInvitePopup(_staticPopupSlots);
            StaticPopupCoordinatorLaw.Definition definition =
                popup?.Instance.Definition ?? PartyFrameUiLaw.PartyInvitePopupDefinition;
            scenario["stateSource"] = "party-wire-runtime";
            scenario["fixtureStaged"] = false;
            scenario["captureStateMutation"] = false;
            scenario["captureNetworkMutation"] = false;
            scenario["fullRosterCount"] = _partyMembers.Count;
            scenario["ownSubgroup"] = _partyOwnFlags & 0x7f;
            scenario["compactSlotCount"] = slots.Length;
            scenario["compactSlotGuids"] = slots.Select(member => $"0x{member.Guid:X16}").ToArray();
            scenario["compactSlotSources"] = slots.Select((member, index) => new
            {
                slot = index + 1,
                guid = $"0x{member.Guid:X16}",
                entitySource = _entities.TryGet(member.Guid, out _),
                statsSource = _partyStats.ContainsKey(member.Guid),
                rosterStatus = member.Status,
            }).ToArray();
            scenario["inviteVisible"] = popup is not null;
            scenario["inviter"] = popup?.Instance.DataToken ?? "";
            scenario["slot"] = popup?.Slot ?? 0;
            scenario["type"] = popup?.Instance.Definition.Type ?? "";
            scenario["timeLeftSeconds"] = popup?.Instance.TimeLeft ?? 0;
            scenario["definitionFlags"] = new
            {
                whileDead = definition.WhileDead,
                hideOnEscape = definition.HideOnEscape,
                hasAccept = definition.HasAccept,
                hasCancel = definition.HasCancel,
                hasOnShow = definition.HasOnShow,
                hasOnHide = definition.HasOnHide,
                hasOnUpdate = definition.HasOnUpdate,
                hasEditBox = definition.HasEditBox,
                usesTimeoutText = definition.UsesTimeoutText,
                usesDelayText = definition.UsesDelayText,
            };
            scenario["integratedTypes"] = new[] { PartyInvitePopupType };
            scenario["frameTelemetryExpected"] = PartyFrameUiLaw.MemberCount;
            scenario["popupTelemetryExpected"] = _uiParityPanel == "party-invite";
            return scenario;
        }
        if (_uiParityPanel == "inspect-frame")
        {
            WorldEntity? inspected = _inspectOpen &&
                _entities.TryGet(_inspectGuid, out WorldEntity found) ? found : null;
            scenario["stateSource"] = "inspect-runtime";
            scenario["captureStateMutation"] = false;
            scenario["captureNetworkMutation"] = false;
            scenario["inspectOpen"] = _inspectOpen;
            scenario["inspectedGuid"] = $"0x{_inspectGuid:X16}";
            scenario["selectionGuid"] = $"0x{_selectionGuid:X16}";
            scenario["bindingKind"] = _inspectBinding.Kind.ToString();
            scenario["partyIndex"] = _inspectBinding.PartyIndex;
            scenario["frameWidth"] = InspectUiLaw.FrameWidth;
            scenario["frameHeight"] = InspectUiLaw.FrameHeight;
            scenario["slotCount"] = InspectUiLaw.EquipmentSlotCount;
            scenario["selectedTabEnabled"] = false;
            scenario["portraitAperture"] = "authored-background-overlay";
            scenario["slotTooltipAnchor"] = "ANCHOR_RIGHT";
            scenario["slotModifiers"] = "ctrl-inert;shift-inert;plain-inert";
            scenario["equipmentDataSource"] = "PLAYER_VISIBLE_ITEM";
            scenario["privateItemFieldsRead"] = false;
            scenario["modelUsable"] = _inspectPaperDollUsable;
            scenario["hoveredSlots"] = _inspectParityHoveredSlots;
            scenario["visibleItemEntries"] = inspected is null ? 0 :
                Enumerable.Range(0, 19).Count(i => inspected.Fields.PlayerVisibleItemEntry(i) != 0);
            scenario["visibleEnchantIds"] = inspected is null ? 0 :
                Enumerable.Range(0, 19).Sum(i => Enumerable.Range(0, 7)
                    .Count(j => inspected.Fields.PlayerVisibleItemEnchant(i, j) != 0));
            return scenario;
        }
        if (_uiParityPanel == "skill-frame")
        {
            SkillFrameParityState state = CurrentSkillFrameUiParityState();
            scenario["stateSource"] = "player-skill-fields";
            scenario["captureStateMutation"] = false;
            scenario["captureNetworkMutation"] = false;
            scenario["characterOpen"] = state.CharacterOpen;
            scenario["characterTab"] = state.CharacterTab;
            scenario["skillsTab"] = SkillFrameUiLaw.SkillsTab;
            scenario["skillCount"] = state.SkillCount;
            scenario["headerCount"] = state.HeaderCount;
            scenario["rowCount"] = state.RowCount;
            scenario["visibleRows"] = SkillFrameUiLaw.VisibleRows;
            scenario["scroll"] = state.Scroll;
            scenario["maximumScroll"] = state.MaximumScroll;
            scenario["collapsedCategoryIds"] = state.CollapsedCategoryIds;
            scenario["selectedSkill"] = state.SelectedSkill;
            scenario["selectedPresent"] = state.SelectedPresent;
            scenario["selectedExpanded"] = state.SelectedExpanded;
            scenario["selectedAbandonable"] = state.SelectedAbandonable;
            scenario["popupPresent"] = state.PopupPresent;
            scenario["popupSkill"] = state.PopupSkill;
            scenario["popupMatchesSelection"] = state.PopupMatchesSelection;
            scenario["directBindingCommand"] = SkillFrameUiLaw.BindingCommand;
            scenario["directBindingLabel"] = SkillFrameUiLaw.BindingLabel;
            scenario["rowHitWidth"] = SkillFrameUiLaw.SkillRowHitWidth;
            scenario["rowHitHeight"] = SkillFrameUiLaw.SkillRowHitHeight;
            scenario["dividerTop"] = SkillFrameUiLaw.DividerLeftRect.Y;
            scenario["unlearnOpcode"] = $"0x{SkillFrameUiLaw.UnlearnOpcode:X4}";
            scenario["authoritativeMutation"] = "PLAYER_SKILL_INFO-update-only";
            return scenario;
        }
        if (_uiParityPanel is "game-menu" or "options")
        {
            scenario["stateSource"] = _uiParityFixtureStaged
                ? "ui-parity-stage" : "menu-runtime";
            scenario["menuOpen"] = _settingsOpen;
            scenario["menuPage"] = _menuPage.ToString();
            scenario["logoutUiActive"] = LogoutUiActive;
            scenario["logoutEnabled"] = _net is { IsInWorld: true } && !LogoutUiActive;
            scenario["exitEnabled"] = !LogoutUiActive;
            return scenario;
        }
        if (_uiParityPanel == "quest-frame")
        {
            ulong giver = QuestGiverGuid();
            scenario["stateSource"] = _uiParityFixtureStaged
                ? "ui-parity-stage" : "quest-wire-runtime";
            scenario["captureStateMutation"] = _uiParityFixtureStaged;
            scenario["captureNetworkMutation"] = false;
            scenario["questPanel"] = QuestNpcPanelNow().ToString();
            scenario["giverGuid"] = $"0x{giver:X16}";
            scenario["giverKind"] = GuidInfo.IsItem(giver) ? "item" : "world-unit";
            scenario["scroll"] = _questNpcScroll;
            scenario["contentHeight"] = _questNpcContentHeight;
            scenario["rewardChoice"] = _questRewardChoice;
            scenario["activeRows"] = _questList?.Quests.Count(q =>
                QuestFrameUiLaw.GreetingPool(q.Icon) == QuestGreetingPool.Active) ?? 0;
            scenario["availableRows"] = _questList?.Quests.Count(q =>
                QuestFrameUiLaw.GreetingPool(q.Icon) == QuestGreetingPool.Available) ?? 0;
            return scenario;
        }
        if (_uiParityPanel == "multi-action-bar")
        {
            int left = Enumerable.Range(MultiActionBarUiLaw.BottomLeftBase,
                MultiActionBarUiLaw.ButtonsPerBar).Count(slot => _actions[slot] is not null);
            int right = Enumerable.Range(MultiActionBarUiLaw.BottomRightBase,
                MultiActionBarUiLaw.ButtonsPerBar).Count(slot => _actions[slot] is not null);
            bool grid = HasCarriedItem || HasActionBarCursor;
            scenario["stateSource"] = _uiParityFixtureStaged ? "ui-parity-stage"
                : _multiActionProtocolFixtureStaged ? "live-protocol-fixture"
                : "action-runtime";
            scenario["captureStateMutation"] = _uiParityFixtureStaged;
            scenario["captureNetworkMutation"] = false;
            scenario["protocolFixtureStaged"] = _multiActionProtocolFixtureStaged;
            scenario["leftOccupied"] = left;
            scenario["rightOccupied"] = right;
            scenario["gridShown"] = grid;
            scenario["cursorKind"] = MultiActionCursorKind;
            scenario["leftWireSlots"] = "60..71";
            scenario["rightWireSlots"] = "48..59";
            scenario["interactiveButtons"] = grid
                ? MultiActionBarUiLaw.ButtonsPerBar * 2 : left + right;
            return scenario;
        }
        if (_uiParityPanel == "pet-action-bar")
        {
            WorldEntity? pet = _petGuid != 0 && _entities.TryGet(_petGuid, out WorldEntity found) &&
                found.IsUnit ? found : null;
            int named = _petActions.Count(word =>
            {
                uint action = PetActionBarUiLaw.Action(word);
                byte kind = PetActionBarUiLaw.Kind(word);
                return PetActionBarUiLaw.IsSpell(word)
                    ? _spellCatalog?.TryGet(action, out _) == true
                    : PetTokenName(action, kind) is not null;
            });
            scenario["stateSource"] = "pet-wire-runtime";
            scenario["captureStateMutation"] = false;
            scenario["captureNetworkMutation"] = false;
            scenario["petGuid"] = $"0x{_petGuid:X16}";
            scenario["petDescriptorPresent"] = pet is not null;
            scenario["namedSlots"] = named;
            scenario["gridShown"] = _draggingPetAction.HasValue;
            scenario["cursorKind"] = _draggingPetAction.HasValue ? "pet-action" : "none";
            scenario["actionsUsable"] = PetActionBarUiLaw.Usable(
                _petState, pet?.Fields.UnitFlags ?? 0);
            scenario["petAttacking"] = _petAttacking;
            scenario["topOffset"] = PetActionBarUiLaw.BaseTopOffset +
                PetActionBarUiLaw.BottomMultiBarStep;
            scenario["buttonTop"] = PetActionBarUiLaw.ButtonTop;
            scenario["activeAuraButtons"] = pet is null ? 0 : _petActions.Count(word =>
                PetActionBarUiLaw.IsSpell(word) &&
                _spellCatalog?.TryGet(PetActionBarUiLaw.Action(word), out SpellInfo spell) == true &&
                IsPetSpellShowingActive(word, spell, pet));
            return scenario;
        }
        if (_uiParityPanel != "cast-bar") return scenario;

        double now = renderedAtClientSeconds ?? NowSeconds();
        float fraction = renderedFraction ?? (_castBarPhase switch
        {
            CastBarPhase.Casting => CastingBarUiLaw.Progress(
                _castBarStarted, _castBarEnds, now, channel: false),
            CastBarPhase.Channel => CastingBarUiLaw.Progress(
                _castBarStarted, _castBarEnds, now, channel: true),
            CastBarPhase.Success or CastBarPhase.Failed => 1f,
            _ => 0f,
        });
        scenario["stateSource"] = _uiParityFixtureStaged
            ? "ui-parity-stage" : "spell-lifecycle-runtime";
        scenario["spellId"] = _castBarSpell;
        scenario["spellName"] = _castBarText;
        scenario["phase"] = _castBarPhase.ToString().ToUpperInvariant();
        scenario["startedSeconds"] = _castBarStarted;
        scenario["endsSeconds"] = _castBarEnds;
        scenario["capturedAtClientSeconds"] = now;
        scenario["fraction"] = fraction;
        scenario["fixtureFractionForced"] = _uiParityFixtureStaged;
        return scenario;
    }

    private void SnapshotUiParityScenario(double? renderedAtClientSeconds = null,
        float? renderedFraction = null)
    {
        _uiParityFrameScenarioSummary = CurrentUiParityScenarioSummary();
        _uiParityFrameScenario = CurrentUiParityScenario(
            renderedAtClientSeconds, renderedFraction);
    }

    private void BeginUiParityFrame(Vector2 origin, float logicalScale = 0f)
    {
        if (!_uiParityArmed || _uiParityFrameSeen || _uiParityPanel == "game-menu" && _menuPage != MenuPage.GameMenu) return;
        _uiParityOrigin = origin;
        _uiParityLogicalScale = logicalScale > 0 ? logicalScale : S;
        _uiParityRows.Clear();
        _uiParityDrawIndex = 0;
        _uiParityFrameScenarioSummary = "";
        _uiParityFrameScenario.Clear();
    }

    private void CollectUiParity(string element, string type, Vector2 min, Vector2 size,
        string parent = "GameMenuFrame", string point = "", string relativeTo = "",
        string relativePoint = "", string offsetX = "", string offsetY = "", string texture = "",
        string font = "", string fontPath = "", string fontSize = "", string color = "",
        string layer = "", string strata = "DIALOG", string bgFile = "", string edgeFile = "",
        string tileSize = "", string edgeSize = "", string insets = "", string texCoords = "")
    {
        if (!_uiParityArmed || _uiParityFrameSeen) return;
        static string N(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        static string Norm(string path) => path.Length == 0 || path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? path : path + ".blp";
        texture = Norm(texture); bgFile = Norm(bgFile); edgeFile = Norm(edgeFile);
        string assets = string.Join('|', new[] { texture, bgFile, edgeFile }.Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(path =>
                _mpq?.ReadFileWithSupplier(path) is { } hit ? $"{hit.Supplier}:{path}" : $"MISSING:{path}"));
        string fontSource = fontPath.Length > 0 && _mpq?.ReadFileWithSupplier(fontPath) is { } fontHit
            ? $"{fontHit.Supplier}:{fontPath}" : "";
        float logicalScale = MathF.Max(_uiParityLogicalScale, 0.001f);
        Vector2 relative = element is "GameMenuFrame" or "PlayerFrame" or "TargetFrame" or "PartyMemberFrame1" or
            "MainMenuBar" or "ActionButton1" or "MultiBarBottomLeft" or "CastingBarFrame" or "BuffFrame" or "MinimapCluster" or "ChatFrame1" or "ReputationWatchBar" or "ContainerFrame1"
            ? Vector2.Zero : (min - _uiParityOrigin) / logicalScale;
        bool unsized = size == Vector2.Zero;
        // Legacy declarations contain call-site copies of reference metadata. Preserve their
        // measured geometry, but never accept those declarations as evidence.
        _uiParityRows.Add(new([_uiParityPanel, element, type, parent,
            unsized ? "" : N(relative.X), unsized ? "" : N(relative.Y),
            unsized ? "" : N(size.X / logicalScale), unsized ? "" : N(size.Y / logicalScale),
            "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "",
            "", "", "", "", "", "", "", "", "", "", "MSUI:legacy-geometry-only", "", "",
            "DRAWN-NOT-INSTRUMENTED"]));
    }

    private void CollectUiParityDraw(string element, string type, Vector2 min, Vector2 size,
        string parent, UiParityDrawTrace trace)
    {
        if (!_uiParityArmed || _uiParityFrameSeen) return;
        static string N(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        static string Norm(string path) => path.Length == 0 || path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? path : path + ".blp";
        float logicalScale = MathF.Max(_uiParityLogicalScale, 0.001f);
        Vector2 relative = element is "GameMenuFrame" or "OptionsFrame" or "KeyBindingFrame" or "MacroFrame" or "GameTooltip" or "UIErrorsFrame" or "StaticPopup1" or "PlayerFrame" or "TargetFrame" or "CharacterFrame" or "PaperDollFrame" or "BenillaSkillFrame" or "InspectFrame" or "SpellBookFrame" or "TalentFrame" or "QuestLogFrame" or "MultiBarBottomLeft" or "MultiBarBottomRight" or "MerchantFrame" or "ClassTrainerFrame" or "BankFrame" or "MailFrame" or "AuctionFrame" or "LootFrame" or "GuildFrame" or "GossipFrame" or "TaxiFrame" or "TradeFrame" ? Vector2.Zero : (min - _uiParityOrigin) / logicalScale;
        string texture = Norm(trace.TexturePath);
        string asset = texture.Length == 0 ? "" : _mpq?.ReadFileWithSupplier(texture) is { } hit
            ? $"{hit.Supplier}:{texture}" : $"MISSING:{texture}";
        string fontSource = trace.FontPath.Length == 0 ? "" : _mpq?.ReadFileWithSupplier(trace.FontPath) is { } fontHit
            ? $"{fontHit.Supplier}:{trace.FontPath}" : $"MISSING:{trace.FontPath}";
        string assetAvailability = asset.Contains("MISSING:", StringComparison.OrdinalIgnoreCase) ||
            fontSource.Contains("MISSING:", StringComparison.OrdinalIgnoreCase) ? "MISSING" :
            asset.Length > 0 || fontSource.Length > 0 ? "PRESENT" : "NOT_APPLICABLE";
        uint c = trace.Color;
        string color = c == 0 ? "" : $"#{c & 0xff:X2}{(c >> 8) & 0xff:X2}{(c >> 16) & 0xff:X2}{(c >> 24) & 0xff:X2}";
        string Rect(Vector4 rect)
            => $"{N((rect.X-_uiParityOrigin.X)/logicalScale)}|{N((rect.Y-_uiParityOrigin.Y)/logicalScale)}|" +
               $"{N((rect.Z-_uiParityOrigin.X)/logicalScale)}|{N((rect.W-_uiParityOrigin.Y)/logicalScale)}";
        Vector4 geometry = new(min.X, min.Y, min.X + size.X, min.Y + size.Y);
        string contentRect = Rect(trace.ContentRect ?? geometry);
        string clipRect = trace.ClipRect is Vector4 clip ? Rect(clip) : "";
        bool interactive = type is "Button" or "CheckButton" or "Slider" or "EditBox";
        string enabled = trace.Enabled is bool isEnabled ? isEnabled.ToString().ToLowerInvariant() :
            interactive ? "UNMEASURED" : "";
        string state = trace.InteractionState.Length > 0 ? trace.InteractionState :
            interactive ? "UNMEASURED" : "";
        string hitRect = trace.HitMin is Vector2 hitMin && trace.HitMax is Vector2 hitMax
            ? Rect(new(hitMin.X, hitMin.Y, hitMax.X, hitMax.Y))
            : interactive && !trace.Visible && trace.Enabled == false ? "NONE"
            : interactive ? "UNMEASURED" : "";
        string texCoords = trace.TexCoords.Length > 0 ? trace.TexCoords : texture.Length > 0 ? "0|0|1|1" : "";
        string blendMode = trace.BlendMode.Length > 0 ? trace.BlendMode : texture.Length > 0 ? "BLEND" : "";
        string[] values = [_uiParityPanel, element, type, parent, N(relative.X), N(relative.Y), N(size.X/logicalScale), N(size.Y/logicalScale),
            trace.Point, trace.RelativeTo, trace.RelativePoint, N(trace.OffsetX), N(trace.OffsetY), texture, "", trace.FontPath,
            trace.FontSize > 0 ? N(trace.FontSize) : "", color, trace.DrawLayer, trace.Strata, "", "", "", "", "", texCoords,
            contentRect, clipRect, trace.ClipMask, (_uiParityDrawIndex++).ToString(CultureInfo.InvariantCulture),
            blendMode, trace.Visible.ToString().ToLowerInvariant(), enabled, state, hitRect, assetAvailability,
            "MSUI:derived-from-draw-variables", asset, fontSource, "DRAWN-INSTRUMENTED"];
        _uiParityRows.Add(new(values));
    }

    private void ClassifyUiParity(string element, string type, string parent, string coverage, string reason = "")
    {
        if (!_uiParityArmed || _uiParityFrameSeen || coverage is not ("DRAWN-NOT-INSTRUMENTED" or "NOT-DRAWN")) return;
        // Keep this indexed to the 40-column CSV schema. A short initializer silently shifts
        // provenance into assetAvailability and leaves coverage blank in Import-Csv.
        string[] values = Enumerable.Repeat("", 40).ToArray();
        values[0] = _uiParityPanel;
        values[1] = element;
        values[2] = type;
        values[3] = parent;
        if (coverage == "NOT-DRAWN")
        {
            values[31] = "false";
            values[35] = "NOT_APPLICABLE";
        }
        values[36] = reason.Length == 0 ? "MSUI:panel-draw-walk" :
            $"MSUI:panel-draw-walk;reason={reason}";
        values[39] = coverage;
        _uiParityRows.Add(new(values));
    }

    private void MarkUiParityFrameComplete()
    {
        if (!_uiParityArmed || _uiParityFrameSeen || _uiParityRows.Count == 0) return;
        if (_uiParityFrameScenario.Count == 0) SnapshotUiParityScenario();
        _uiParityFrameSeen = true;
    }

    private void FinishUiParityCapture()
    {
        if (!_uiParityArmed || !_uiParityFrameSeen || _liveRunOptions is null) return;
        // The modal is opened during the first ImGui frame. Discard that frame's telemetry,
        // then recollect on the second frame and read its framebuffer immediately so CSV and
        // PNG describe the same rendered state.
        if (++_uiParityPresentedFrames < 2)
        {
            _uiParityFrameSeen = false;
            _uiParityRows.Clear();
            _uiParityDrawIndex = 0;
            _uiParityFrameScenarioSummary = "";
            _uiParityFrameScenario.Clear();
            return;
        }
        string dir = Path.GetFullPath(Path.IsPathRooted(_liveRunOptions.OutputDirectory)
            ? _liveRunOptions.OutputDirectory : Path.Combine(_config.RepoRoot, _liveRunOptions.OutputDirectory));
        Directory.CreateDirectory(dir);
        string stem = $"ui-parity-{_uiParityPanel}-{_uiParityStamp}";
        string csv = Path.Combine(dir, stem + "-actual.csv"), png = Path.Combine(dir, stem + "-actual.png");
        const string header = "panel,element,type,parent,x,y,width,height,point,relativeTo,relativePoint,offsetX,offsetY,texture,font,fontPath,fontSize,color,layer,strata,bgFile,edgeFile,tileSize,edgeSize,insets,texCoords,contentRect,clipRect,clipMask,drawIndex,blendMode,visible,enabled,interactionState,hitRect,assetAvailability,source,assetSource,fontSource,coverage";
        int schemaColumns = header.Count(c => c == ',') + 1;
        int malformedRow = _uiParityRows.FindIndex(r => r.Values.Length != schemaColumns ||
            string.IsNullOrWhiteSpace(r.Values[^1]));
        if (malformedRow >= 0)
        {
            _uiParityCaptureError = $"telemetry schema violation at row {malformedRow + 1};" +
                                    $"columns={_uiParityRows[malformedRow].Values.Length}/{schemaColumns};" +
                                    $"coverage={_uiParityRows[malformedRow].Values.LastOrDefault()}";
            Console.Error.WriteLine($"[ui-parity] FAIL {_uiParityCaptureError}");
            RestoreMultiActionUiParityFixture();
            _uiParityArmed = false; _uiParityFrameSeen = false; _uiParityPresentedFrames = 0; _uiParityRows.Clear();
            return;
        }
        static string Csv(IEnumerable<string> values) => string.Join(',', values.Select(v => '"' + v.Replace("\"", "\"\"") + '"'));
        File.WriteAllLines(csv, new[] { header }.Concat(_uiParityRows.Select(r => Csv(r.Values))));
        if (!TrySaveGameplayScreenshot(png) || !File.Exists(png) || new FileInfo(png).Length == 0)
        {
            _uiParityCaptureError = $"screenshot write failed: {png}";
            Console.Error.WriteLine($"[ui-parity] FAIL {_uiParityCaptureError}");
            RestoreMultiActionUiParityFixture();
            _uiParityArmed = false; _uiParityFrameSeen = false; _uiParityPresentedFrames = 0; _uiParityRows.Clear();
            return;
        }
        static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        string manifest = Path.Combine(dir, stem + "-manifest.json");
        string provenance = UiParityProvenance;
        string scenarioSummary = _uiParityFrameScenarioSummary;
        Dictionary<string, object?> scenario = _uiParityFrameScenario;
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            schemaVersion = 3,
            panel = _uiParityPanel,
            captureCommand = _uiParityFixtureStaged ? "ui-parity-stage" : "ui-parity",
            provenance,
            scenarioSummary,
            scenario,
            capturedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            sameRenderedFrame = true,
            rows = _uiParityRows.Count,
            instrumentedRows = _uiParityRows.Count(r => r.Values[^1] == "DRAWN-INSTRUMENTED"),
            notDrawnRows = _uiParityRows.Count(r => r.Values[^1] == "NOT-DRAWN"),
            blankCoverageRows = _uiParityRows.Count(r => string.IsNullOrWhiteSpace(r.Values[^1])),
            files = new[]
            {
                new { path = Path.GetFileName(csv), bytes = new FileInfo(csv).Length, sha256 = Sha(csv) },
                new { path = Path.GetFileName(png), bytes = new FileInfo(png).Length, sha256 = Sha(png) },
            }
        }, new JsonSerializerOptions { WriteIndented = true }));
        _uiParityCompletedPanel = _uiParityPanel;
        _uiParityCompletedManifest = manifest;
        _uiParityCompletedProvenance = provenance;
        _uiParityCompletedScenario = scenarioSummary;
        Console.WriteLine($"[ui-parity] actual draw capture {csv} (+ .png + manifest)");
        RestoreMultiActionUiParityFixture();
        _uiParityArmed = false; _uiParityFrameSeen = false; _uiParityPresentedFrames = 0; _uiParityRows.Clear();
    }
}
