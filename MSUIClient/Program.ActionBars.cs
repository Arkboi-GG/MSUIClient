using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly PlayerActions _actions = new();
    private SpellCatalog? _spellCatalog;
    private SpellVisualCatalog? _spellVisualCatalog;
    private GameplayArt? _gameplayArt;
    private readonly bool[] _actionKeyWasDown = new bool[12];
    private long _actionUses;
    private int _pressedActionSlot = -1;
    private int _draggingActionSlot = -1;
    private Vector2 _actionPressPosition;
    private uint _pendingCastSpell;
    private uint _autoRepeatSpell;
    private uint _queuedMeleeSpell;
    private double _globalCooldownUntil;
    private int _actionPage = 1;
    private const int ActionPageCount = 6;
    private readonly ActionButtonVerdict?[] _lastActionButtonVerdicts =
        new ActionButtonVerdict?[120];


    private void InitGameplayUi(GL gl)
    {
        if (_mpq is null) return;
        try
        {
            _spellCatalog = SpellCatalog.Load(_mpq);
            _spellVisualCatalog = SpellVisualCatalog.Load(_mpq);
            _gameplayArt = new GameplayArt(gl, _mpq);
            Console.WriteLine(_spellCatalog is null
                ? "[actions] Spell/SpellIcon DBC unavailable"
                : $"[actions] spell catalog ready ({_spellCatalog.Count} rows)");
            Console.WriteLine(_spellVisualCatalog is null
                ? "[spell-fx] SpellVisual chain unavailable"
                : "[spell-fx] SpellVisual/Kit/EffectName chain ready");
        }
        catch (Exception ex) { Console.WriteLine($"[actions] UI initialization failed: {ex.Message}"); }
    }

    private void UpdateActionBarInput(bool typing)
    {
        for (int i = 0; i < _actionKeyWasDown.Length; i++)
        {
            bool down = BindingDown(ActionBinding(i));
            if (down && !_actionKeyWasDown[i] && !typing && _net is { IsInWorld: true })
                UseAction(ActionWireSlot(i));
            _actionKeyWasDown[i] = down;
        }
    }

    private void UseAction(int wireSlot)
    {
        if (_net is null || _actions[wireSlot] is not { } slot) return;
        _actionUses++;
        switch (slot.Kind)
        {
            case ActionSlot.Spell when slot.ActionId == 6603:
                if (_selectionGuid != 0) CommitSelection(_selectionGuid, beginAttack: true);
                break;
            case ActionSlot.Spell:
                TryCast(slot.ActionId);
                break;
            case ActionSlot.Item:
                UseItemAction(slot.ActionId);
                break;
            case ActionSlot.Macro:
                ExecuteMacro(slot.ActionId);
                break;
        }
    }

    private void TryCast(uint spellId)
    {
        if (_net is null) return;
        if (_spellCatalog is null || !_spellCatalog.TryGet(spellId, out SpellInfo spell) || spell.Passive)
        {
            EmitCastVerdict(spellId, CastTargetReason.UnavailableOrPassive, 0, sent: false);
            return;
        }
        if (!_actions.KnownSpells.Contains(spellId))
        {
            EmitCastVerdict(spellId, CastTargetReason.UnknownSpell, 0, sent: false);
            RefuseCast(spellId, "LOCAL_UNKNOWN_SPELL", "You have not learned that spell.");
            return;
        }
        if (TryOpenProfession(spellId))
        {
            EmitCastVerdict(spellId, CastTargetReason.ProfessionWindow, 0, sent: false);
            return;
        }
        double now = MovementInfo.ClientUptimeMs() / 1000.0;
        if (spell.AutoRepeat && _autoRepeatSpell == spellId)
        {
            _net.CancelAutoRepeat();
            _autoRepeatSpell = 0;
            SetVisualSheath(0);
            _character?.CancelSpellVisual();
            return;
        }
        if (spell.OnNextSwing && _queuedMeleeSpell == spellId)
        {
            EmitCastVerdict(spellId, CastTargetReason.AlreadyQueued, 0, sent: false);
            return;
        }
        if (_actions.IsOnCooldown(spellId, now, spell.Category) || now < _globalCooldownUntil)
        {
            EmitCastVerdict(spellId, CastTargetReason.CooldownOrGlobalCooldown, 0, sent: false);
            RefuseCast(spellId, "LOCAL_COOLDOWN", "Spell is not ready yet.");
            return;
        }
        if (!spell.AutoRepeat && !spell.OnNextSwing && _pendingCastSpell != 0)
        {
            // This gate is only reached once the GCD has elapsed (checked above), so the
            // only cast still legitimately in flight here is a timed one whose cast bar
            // outlasts the global cooldown. If no cast bar / channel is up, the pending
            // lock is stale: its SMSG_SPELL_GO was never received (observed with Arcane
            // Explosion 1449, whose only server GO in a run was the Clearcasting proc
            // 12536, never spell 1449 itself). Clear it so one dropped GO cannot deadlock
            // every future cast (the baseline symptom: Frost Nova refused "Another action
            // is in progress" 2.4s after the AoE, GCD already ready).
            if (_castBarPhase is CastBarPhase.Casting or CastBarPhase.Channel)
            {
                if (_pendingCastSpell != spellId) RefuseCast(spellId, "LOCAL_SPELL_IN_PROGRESS", "Another action is in progress");
                EmitCastVerdict(spellId, CastTargetReason.PendingCast, 0, sent: false);
                return;
            }
            _pendingCastSpell = 0;
        }
        if (_entities.TryGet(_net.PlayerGuid, out WorldEntity caster))
        {
            if (caster.Fields.MountDisplayId != 0 && (spell.Attributes & 0x0100_0000u) == 0)
            {
                EmitCastVerdict(spellId, CastTargetReason.Mounted, 0, sent: false);
                RefuseCast(spellId, "LOCAL_MOUNTED", "You are mounted");
                return;
            }
        }

        SpellReagent? missingReagent = _spellCatalog.Reagents(spellId)
            .FirstOrDefault(reagent => CarriedCount(reagent.ItemId) < reagent.Count);
        if (missingReagent is { ItemId: not 0 } reagent)
        {
            EmitCastVerdict(spellId, CastTargetReason.MissingReagent, 0, sent: false);
            RefuseCast(spellId, "LOCAL_MISSING_REAGENT",
                $"Missing reagent {reagent.ItemId} ({CarriedCount(reagent.ItemId)}/{reagent.Count}).");
            return;
        }
        uint missingTool = _spellCatalog.Tools(spellId).FirstOrDefault(tool => CarriedCount(tool) == 0);
        if (missingTool != 0)
        {
            EmitCastVerdict(spellId, CastTargetReason.MissingTool, 0, sent: false);
            RefuseCast(spellId, "LOCAL_MISSING_TOOL", $"Requires item {missingTool}.");
            return;
        }
        if (!HasNearbySpellFocus(spell.RequiredFocus))
        {
            EmitCastVerdict(spellId, CastTargetReason.MissingSpellFocus, 0, sent: false);
            RefuseCast(spellId, "LOCAL_MISSING_FOCUS",
                $"Requires {SpellFocusName(spell.RequiredFocus)}.");
            return;
        }

        CastTargetVerdict targetVerdict = ResolveCastTarget(spell);
        ulong target = targetVerdict.Guid;
        if (targetVerdict.Kind == CastTargetKind.Refused)
        {
            EmitCastVerdict(spellId, targetVerdict.Reason, target, sent: false);
            RefuseCast(spellId, targetVerdict.Reason.ToString(),
                _selectionGuid == 0 ? "You have no target." : "Invalid target");
            return;
        }
        if (target == _selectionGuid && target != _net.PlayerGuid &&
            CastRangeRefusal(spell) is { } rangeFailure)
        {
            EmitCastVerdict(spellId, rangeFailure.Reason, target, sent: false);
            RefuseCast(spellId, $"LOCAL_{rangeFailure.Reason}", rangeFailure.Text);
            return;
        }
        if (!SpellResourceGate(spell, out _, out _))
        {
            EmitCastVerdict(spellId, CastTargetReason.NotEnoughPower, target, sent: false);
            RefuseCast(spellId, "LOCAL_NO_POWER", $"Not enough {PowerName((byte)spell.PowerType).ToLowerInvariant()}");
            return;
        }
        bool sent = _net.CastSpell(spellId, target);
        EmitCastVerdict(spellId, targetVerdict.Reason, target, sent);
        if (!sent) return;
        if (spell.AutoRepeat) _autoRepeatSpell = spellId;
        else if (spell.OnNextSwing) _queuedMeleeSpell = spellId;
        else _pendingCastSpell = spellId;
        if (spell.StartRecoveryMs > 0)
        {
            _globalCooldownUntil = now + spell.StartRecoveryMs / 1000.0;
            _actions.StartCooldown(spellId, spell.StartRecoveryCategory, 0,
                spell.StartRecoveryMs, now);
        }
    }

    private void RefuseCast(uint spellId, string reason, string text) =>
        ShowSpellError(spellId, reason, text, "LOCAL_GATE");

    private (string Text, CastTargetReason Reason)? CastRangeRefusal(in SpellInfo spell)
    {
        if (_net is null || _controller is null || _spellCatalog is null || _selectionGuid == 0 ||
            !_entities.TryGet(_selectionGuid, out WorldEntity target) ||
            !_spellCatalog.TryGetRange(spell.RangeIndex, out SpellRangeRow row)) return null;
        float selfReach = _entities.TryGet(_net.PlayerGuid, out WorldEntity self)
            ? self.Fields.CombatReach : 1.5f;
        float targetReach = target.Fields.CombatReach;
        float min = row.Min, max = row.Max;
        if (row.Melee) { min = 0f; max = MathF.Max(selfReach + targetReach + 1.3333f, 5f); }
        else
        {
            if (min <= 0f && max <= 0f) return null;
            max += selfReach + targetReach;
            if (min != 0f) min += selfReach + targetReach;
        }
        float d2 = Vector3.DistanceSquared(_controller.Position, target.Position);
        if (min > 0f && d2 < min * min)
            return ("Target too close", CastTargetReason.TooClose);
        return d2 > max * max ? ("Out of range.", CastTargetReason.OutOfRange) : null;
    }

    // Benilla cast_target.rs transcribes Spell_C::ArmCast/BindTarget: seed the target word from
    // Spell.dbc Targets, apply EffectImplicitTargetA[0], then satisfy every unit-shaped bit.
    // A hostile selection therefore cannot receive Holy Light; autoSelfCast binds the player.
    private CastTargetVerdict ResolveCastTarget(in SpellInfo spell)
    {
        CastTargetCandidate? selected = null, self = null;
        if (_selectionGuid != 0 && _entities.TryGet(_selectionGuid, out WorldEntity selectedEntity))
        {
            selected = CastCandidate(selectedEntity, _selectionGuid == _net!.PlayerGuid);
            EmitCombat("SpellTargetCandidate", "cast-acting-path", selectedEntity.Guid,
                $"spell={spell.Id};mask=0x{CastTargetLaw.TargetMask(spell):X4};isSelf={selected.Value.IsSelf};" +
                $"friendly={selected.Value.Friendly};attackable={selected.Value.Attackable};dead={selected.Value.Dead};" +
                $"unitFlags=0x{selectedEntity.Fields.UnitFlags:X8};faction={selectedEntity.Fields.FactionTemplate};" +
                $"reaction={ReactionPlayerToward(selectedEntity)}");
        }
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
            self = CastCandidate(player, isSelf: true);
        return CastTargetLaw.Resolve(spell, selected, self);
    }

    private void EmitCastVerdict(uint spellId, CastTargetReason reason, ulong resolvedGuid, bool sent)
    {
        var verdict = new CastVerdict(
            NowSeconds(), spellId, reason, _selectionGuid, resolvedGuid, sent);
        _verdicts.Add(verdict);
        EmitSpellSweep(spellId, reason, resolvedGuid, sent);
        if (!sent || resolvedGuid != _selectionGuid)
            Console.WriteLine($"[verdict:cast] {verdict.ToLine()}");
    }

    private CastTargetCandidate CastCandidate(WorldEntity candidate, bool isSelf) => new(
        candidate.Guid, isSelf,
        isSelf || ReactionPlayerToward(candidate) == FactionReaction.Friendly,
        !isSelf && CanAttack(candidate), candidate.IsDead);

    private void DrawActionBars()
    {
        if (_net is not { IsInWorld: true } || _gameplayArt is null) return;
        Vector2 display = ImGui.GetIO().DisplaySize;
        float scale = GameplayUiScale();
        Vector2 barMin = GameplayBarMin(display, scale);
        CollectGameplayLayout("action-bar", 0f, 715f, 1024f, 53f,
            barMin, new Vector2(1024f, 53f) * scale);
        ImDrawListPtr bg = ImGui.GetBackgroundDrawList();

        if (_uiParityArmed && _uiParityPanel == "action-bar")
        {
            BeginUiParityFrame(barMin, scale);
            CollectUiParity("MainMenuBar", "Frame", barMin, new Vector2(1024, 53) * scale,
                parent: "", point: "BOTTOM", strata: "");
            CollectUiParity("MainMenuExpBar", "StatusBar", barMin, new Vector2(1024, 13) * scale,
                parent: "MainMenuBar", point: "TOP", texture: @"Interface\TargetingFrame\UI-StatusBar",
                strata: "");
            (string Name, float X, string Tex)[] xp =
            [
                ("MainMenuXPBarTexture0", 0, "0|0.79296875|1.0|0.83203125"),
                ("MainMenuXPBarTexture1", 256, "0|0.54296875|1.0|0.58203125"),
                ("MainMenuXPBarTexture2", 512, "0|0.29296875|1.0|0.33203125"),
                ("MainMenuXPBarTexture3", 768, "0|0.04296875|1.0|0.08203125"),
            ];
            foreach (var row in xp)
                CollectUiParity(row.Name, "Texture", barMin + new Vector2(row.X, 0) * scale,
                    new Vector2(256, 10) * scale, parent: "MainMenuExpBar", point: "BOTTOM",
                    offsetX: (row.X - 384).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    offsetY: "3", texture: @"Interface\MainMenuBar\UI-MainMenuBar-Dwarf",
                    layer: "OVERLAY", strata: "", texCoords: row.Tex);
            (string Name, float X, string Tex)[] art =
            [
                ("MainMenuBarTexture0", 0, "0|0.83203125|1.0|1.0"),
                ("MainMenuBarTexture1", 256, "0|0.58203125|1.0|0.75"),
                ("MainMenuBarTexture2", 512, "0|0.33203125|1.0|0.5"),
                ("MainMenuBarTexture3", 768, "0|0.08203125|1.0|0.25"),
            ];
            foreach (var row in art)
                CollectUiParity(row.Name, "Texture", barMin + new Vector2(row.X, 10) * scale,
                    new Vector2(256, 43) * scale, parent: "MainMenuBarArtFrame", point: "BOTTOM",
                    offsetX: (row.X - 384).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    offsetY: "0", texture: @"Interface\MainMenuBar\UI-MainMenuBar-Dwarf",
                    layer: "ARTWORK", strata: "", texCoords: row.Tex);
            CollectUiParity("MainMenuBarLeftEndCap", "Texture", barMin + new Vector2(-96, -75) * scale,
                new Vector2(128) * scale, parent: "MainMenuBarArtFrame", point: "BOTTOM", offsetX: "-544",
                offsetY: "0", texture: @"Interface\MainMenuBar\UI-MainMenuBar-EndCap-Dwarf",
                layer: "OVERLAY", strata: "");
            CollectUiParity("MainMenuBarRightEndCap", "Texture", barMin + new Vector2(992, -75) * scale,
                new Vector2(128) * scale, parent: "MainMenuBarArtFrame", point: "BOTTOM", offsetX: "544",
                offsetY: "0", texture: @"Interface\MainMenuBar\UI-MainMenuBar-EndCap-Dwarf",
                layer: "OVERLAY", strata: "", texCoords: "1.0|0.0|0.0|1.0");
            CollectUiParity("MainMenuBarPerformanceBarFrame", "Frame", barMin + new Vector2(781, -1) * scale,
                new Vector2(16, 64) * scale, parent: "MainMenuBar", point: "BOTTOMRIGHT", offsetX: "-227",
                offsetY: "-10", strata: "LOW");
            CollectUiParity("MainMenuBarPerformanceBar", "Texture", barMin + new Vector2(777, -1) * scale,
                new Vector2(20, 66) * scale, parent: "MainMenuBarPerformanceBarFrame", point: "TOPRIGHT",
                texture: @"Interface\MainMenuBar\UI-MainMenuBar-PerformanceBar", layer: "BACKGROUND", strata: "LOW");
        }

        // FrameXML child order: XP and the LOW-strata latency meter sit beneath the dwarf art.
        if (_uiParityArmed && _uiParityPanel == "reputation-bar") DrawReputationWatchBar(bg, barMin, scale);
        else DrawExpBar(bg, barMin, scale);
        DrawPerformanceMeter(bg, barMin, scale, display);
        DrawMainMenuBarArt(bg, barMin, scale);

        Vector2 inputMin = new(barMin.X, display.Y - 86f * scale);
        ImGui.SetNextWindowPos(inputMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(544f, 86f) * scale, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
                                 ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoBringToFrontOnFocus;
        if (!ImGui.Begin("##main-action-bar", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        double now = MovementInfo.ClientUptimeMs() / 1000.0;
        int hoveredSlot = -1;
        if (_pressedActionSlot >= 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left) &&
            Vector2.Distance(ImGui.GetIO().MousePos, _actionPressPosition) > 6f * scale)
            _draggingActionSlot = _pressedActionSlot;

        // Reference: an empty slot swaps to the UI-Quickslot grid ring only while a cursor
        // payload is held (BENILLA_ACTIONBAR_GRID_SHOWN); otherwise it keeps UI-Quickslot2.
        bool gridShown = _carriedBag >= 0 || _draggingSpellId != 0 || _draggingActionSlot >= 0;
        // Attack/auto-repeat flash is a plain 0.4 s show/hide toggle (ATTACK_BUTTON_FLASH_TIME).
        bool flashPhase = now % 0.8 < 0.4;
        WorldEntity? player = _entities.TryGet(_net.PlayerGuid, out WorldEntity self) ? self : null;

        for (int i = 0; i < 12; i++)
        {
            int wireSlot = ActionWireSlot(i);
            Vector2 buttonMin = new(barMin.X + (8f + 42f * i) * scale, display.Y - 40f * scale);
            Vector2 buttonMax = buttonMin + new Vector2(36f, 36f) * scale;
            CollectGameplayLayout($"action-slot-{i + 1}", 8f + 42f * i, 728f, 36f, 36f,
                buttonMin, buttonMax - buttonMin);
            ActionSlot? slot = _actions[wireSlot];

            if (i == 0 && _uiParityArmed && _uiParityPanel == "action-button")
            {
                BeginUiParityFrame(buttonMin, scale);
                CollectUiParity("ActionButton1", "CheckButton", buttonMin, new Vector2(36) * scale,
                    parent: "", point: "BOTTOMLEFT", offsetX: "8", offsetY: "4",
                    texture: @"Interface\Buttons\UI-Quickslot2", strata: "");
                CollectUiParity("ActionButton1Icon", "Texture", buttonMin, new Vector2(36) * scale,
                    parent: "ActionButton1", layer: "BACKGROUND", strata: "");
                CollectUiParity("ActionButton1HotKey", "FontString", buttonMin + new Vector2(-2, 2) * scale,
                    new Vector2(36, 10) * scale, parent: "ActionButton1", point: "TOPLEFT", offsetX: "-2",
                    offsetY: "-2", font: "NumberFontNormalSmallGray", fontPath: @"Fonts\ARIALN.TTF",
                    fontSize: "12", color: "#999999FF", layer: "ARTWORK", strata: "");
                CollectUiParity("ActionButton1NormalTexture", "NormalTexture",
                    buttonMin + new Vector2(-15, -14) * scale, new Vector2(66) * scale,
                    parent: "ActionButton1", point: "CENTER", offsetX: "0", offsetY: "-1",
                    texture: @"Interface\Buttons\UI-Quickslot2", strata: "");
            }

            ImGui.SetCursorScreenPos(buttonMin);
            bool clicked = ImGui.InvisibleButton($"##action-{i}", buttonMax - buttonMin);
            bool hovered = ImGui.IsItemHovered();
            bool activated = ImGui.IsItemActivated();
            bool pushed = ImGui.IsItemActive() || BindingDown(ActionBinding(i));
            if (hovered) hoveredSlot = wireSlot;

            if (slot is { } action)
            {
                string iconPath = @"Interface\Icons\INV_Misc_QuestionMark.blp";
                string title = $"Action {action.ActionId}";
                SpellInfo? spellInfo = null;
                if (action.Kind == ActionSlot.Spell && _spellCatalog?.TryGet(action.ActionId, out SpellInfo spell) == true)
                {
                    spellInfo = spell;
                    iconPath = ResolveSpellActionIcon(spell, player);
                    title = spell.Rank.Length > 0 ? $"{spell.Name} ({spell.Rank})" : spell.Name;
                }
                else if (action.Kind == ActionSlot.Item && _items?.TryGet(action.ActionId, out ItemTemplate? item) == true && item is not null)
                {
                    iconPath = item.IconPath;
                    title = item.Name;
                }
                else if (action.Kind == ActionSlot.Macro)
                {
                    iconPath = MacroIcon(action.ActionId);
                    title = action.ActionId is > 0 and <= 18 ? _macros[(int)action.ActionId - 1].Name : "Macro";
                }

                // ── the reference three-way usability verdict (ActionButton_UpdateUsable) ──
                // usable: icon+ring white; not enough power: icon+ring (0.5,0.5,1);
                // otherwise unusable: icon (0.4,0.4,0.4), ring reset to white.
                ActionButtonVerdict verdict = ComputeButtonVerdict(
                    wireSlot, action, spellInfo, player, pushed, hovered, gridShown);
                CollectGameplayAction(verdict);
                EmitActionButtonVerdict(verdict);
                uint iconTint = verdict.Usability switch
                {
                    ButtonUsability.NotEnoughPower => 0xffff8080u,
                    ButtonUsability.Usable => 0xffffffffu,
                    _ => 0xff666666u,
                };
                uint icon = _gameplayArt.Handle(iconPath);
                if (icon != 0) dl.AddImage((nint)icon, buttonMin, buttonMax, Vector2.Zero, Vector2.One, iconTint);

                if (verdict.Flashing && flashPhase)
                {
                    uint flash = _gameplayArt.Handle(@"Interface\Buttons\UI-QuickslotRed");
                    if (flash != 0) dl.AddImage((nint)flash, buttonMin, buttonMax);
                }

                uint cooldownCategory = !verdict.IsItem && _spellCatalog?.TryGet(verdict.ActionId,
                    out SpellInfo cooldownSpell) == true ? cooldownSpell.Category : 0;
                float cooldown = verdict.IsItem ? 0f : _actions.CooldownFraction(
                    verdict.ActionId, now, cooldownCategory);
                if (cooldown > 0f) DrawCooldownSwipe(dl, buttonMin, buttonMax, cooldown);

                if (activated)
                {
                    _pressedActionSlot = wireSlot;
                    _actionPressPosition = ImGui.GetIO().MousePos;
                }
                if (clicked && _draggingActionSlot < 0 && !PlaceCarriedItemOnAction(wireSlot))
                    UseAction(wireSlot);

                // PUSHED replaces the normal state while the mouse or the bound key is down.
                if (verdict.Pushed)
                {
                    uint depress = _gameplayArt.Handle(@"Interface\Buttons\UI-Quickslot-Depress");
                    if (depress != 0) dl.AddImage((nint)depress, buttonMin, buttonMax);
                }
                if (verdict.Hover)
                {
                    uint highlight = _gameplayArt.AdditiveHandle(@"Interface\Buttons\ButtonHilight-Square");
                    if (highlight != 0) dl.AddImage((nint)highlight, buttonMin, buttonMax);
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(title);
                    ImGui.TextDisabled($"Action {wireSlot + 1}");
                    ImGui.EndTooltip();
                }
                if (verdict.Checked)
                {
                    uint check = _gameplayArt.AdditiveHandle(@"Interface\Buttons\CheckButtonHilight");
                    if (check != 0) dl.AddImage((nint)check, buttonMin, buttonMax);
                }
                if (verdict.EquippedBorder)
                {
                    uint border = _gameplayArt.AdditiveHandle(@"Interface\Buttons\UI-ActionButton-Border");
                    if (border != 0)
                    {
                        Vector2 center = (buttonMin + buttonMax) * 0.5f;
                        Vector2 half = new(31f * scale); // 62x62, centered
                        dl.AddImage((nint)border, center - half, center + half, Vector2.Zero,
                            Vector2.One, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 1f, 0f, 0.35f)));
                    }
                }

                // The oom blue tints icon AND ring; grey-unusable resets the ring to white.
                DrawSlotRing(dl, buttonMin, buttonMax, @"Interface\Buttons\UI-Quickslot2", scale,
                    verdict.Usability == ButtonUsability.NotEnoughPower ? 0xffff8080u : 0xffffffffu);

                // Hotkey: red (1.0,0.1,0.1) while the selection is out of range, grey otherwise.
                uint hotkeyColor = verdict.Range == ButtonRange.OutOfRange
                    ? 0xff1a1affu : 0xff999999u;
                DrawActionText(dl, buttonMin, FriendlyKey(BoundKey(ActionBinding(i))), scale, hotkeyColor);

                if (verdict.IsItem && verdict.StackCount > 0)
                    DrawActionCount(dl, buttonMax, verdict.StackCount, scale);
            }
            else
            {
                ActionButtonVerdict verdict = ComputeButtonVerdict(
                    wireSlot, null, null, player, pushed, hovered, gridShown);
                CollectGameplayAction(verdict);
                EmitActionButtonVerdict(verdict);
                if (clicked) PlaceCarriedItemOnAction(wireSlot);
                DrawSlotRing(dl, buttonMin, buttonMax,
                    verdict.CarriedGrid
                        ? @"Interface\Buttons\UI-Quickslot"
                        : @"Interface\Buttons\UI-Quickslot2", scale);
                DrawActionText(dl, buttonMin, FriendlyKey(BoundKey(ActionBinding(i))), scale, 0xff999999);
            }
        }

        // ActionBar.xml anchors are Y-up. ImGui is Y-down, so XML y=-22/-42 become +22/+42.
        if (DrawPageArrowButton(dl, barMin, scale, new Vector2(522f, 22f), "Up", 0))
            ChangeActionPage(1);
        if (DrawPageArrowButton(dl, barMin, scale, new Vector2(522f, 42f), "Down", 1))
            ChangeActionPage(-1);
        ImGui.End();

        DrawMultiActionBars(display, barMin, scale);
        DrawMicroMenu(barMin, scale);

        if (_uiParityArmed && _uiParityPanel is "action-bar" or "action-button")
            MarkUiParityFrameComplete();

        if (_draggingActionSlot >= 0 && _actions[_draggingActionSlot] is { } dragged)
        {
            string iconPath = dragged.Kind == ActionSlot.Spell && _spellCatalog?.TryGet(dragged.ActionId, out SpellInfo info) == true
                ? ResolveSpellActionIcon(info, player)
                : dragged.Kind == ActionSlot.Item && _items?.TryGet(dragged.ActionId, out ItemTemplate? item) == true && item is not null
                    ? item.IconPath : dragged.Kind == ActionSlot.Macro ? MacroIcon(dragged.ActionId)
                    : @"Interface\Icons\INV_Misc_QuestionMark.blp";
            uint icon = _gameplayArt.Handle(iconPath);
            if (icon != 0)
            {
                Vector2 min = ImGui.GetIO().MousePos + new Vector2(10f) * scale;
                ImGui.GetForegroundDrawList().AddImage((nint)icon, min, min + new Vector2(32f) * scale,
                    Vector2.Zero, Vector2.One, 0xccffffff);
            }
        }
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (_draggingMacroId != 0)
            {
                if (hoveredSlot >= 0)
                {
                    var macroAction = new ActionSlot(ActionSlot.Macro, _draggingMacroId);
                    _actions.Set(hoveredSlot, macroAction);
                    _net?.SetActionButton((byte)hoveredSlot, macroAction.Packed);
                }
                _draggingMacroId = 0;
                _pressedMacroId = 0;
            }
            else if (_draggingSpellId != 0)
            {
                if (hoveredSlot >= 0)
                {
                    var spellAction = new ActionSlot(ActionSlot.Spell, _draggingSpellId);
                    _actions.Set(hoveredSlot, spellAction);
                    _net?.SetActionButton((byte)hoveredSlot, spellAction.Packed);
                }
                _draggingSpellId = 0;
            }
            else if (_draggingActionSlot >= 0)
            {
                if (hoveredSlot >= 0) SwapActions(_draggingActionSlot, hoveredSlot);
                else ClearAction(_draggingActionSlot);
            }
            _pressedActionSlot = -1;
            _draggingActionSlot = -1;
        }
    }

    private void DrawMultiActionBars(Vector2 display, Vector2 barMin, float scale)
    {
        bool proof = _uiParityArmed && _uiParityPanel == "multi-action-bar";
        (string Name, int FirstSlot, bool Vertical, Vector2 Origin)[] bars =
        [
            ("MultiBarBottomLeft", 60, false,
                new Vector2(barMin.X + 8 * scale, display.Y - 95 * scale)),
            ("MultiBarBottomRight", 48, false,
                new Vector2(barMin.X + 518 * scale, display.Y - 95 * scale)),
            ("MultiBarRight", 24, true,
                new Vector2(display.X - 45 * scale, display.Y - 598 * scale)),
            ("MultiBarLeft", 36, true,
                new Vector2(display.X - 88 * scale, display.Y - 598 * scale)),
        ];
        foreach (var bar in bars)
        {
            bool populated = Enumerable.Range(bar.FirstSlot, 12).Any(slot => _actions[slot] is not null);
            if (!populated && !(proof && bar.Name == "MultiBarBottomLeft")) continue;
            DrawMultiActionBar(bar.Name, bar.FirstSlot, bar.Vertical, bar.Origin, scale, proof);
        }
    }

    private void DrawMultiActionBar(string name, int firstSlot, bool vertical,
        Vector2 origin, float scale, bool proof)
    {
        if (_gameplayArt is null) return;
        Vector2 logicalSize = vertical ? new Vector2(38, 500) : new Vector2(500, 38);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(logicalSize * scale, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoBringToFrontOnFocus;
        if (!ImGui.Begin($"##{name}", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();

        if (proof && name == "MultiBarBottomLeft")
        {
            BeginUiParityFrame(origin, scale);
            CollectUiParity(name, "Frame", origin, logicalSize * scale, parent: "",
                point: "BOTTOMLEFT", relativeTo: "ActionButton1", relativePoint: "TOPLEFT",
                offsetX: "0", offsetY: "17", strata: "HIGH");
        }

        for (int i = 0; i < 12; i++)
        {
            Vector2 buttonMin = origin + (vertical
                ? new Vector2(2, i * 42)
                : new Vector2(i * 42, 2)) * scale;
            Vector2 buttonMax = buttonMin + new Vector2(36) * scale;
            int slotNumber = firstSlot + i;
            string button = name + "Button" + (i + 1);
            if (proof && name == "MultiBarBottomLeft" && i == 0)
            {
                CollectUiParity(button, "CheckButton", buttonMin, new Vector2(36) * scale,
                    parent: name, point: "BOTTOMLEFT", offsetX: "0", offsetY: "0",
                    texture: @"Interface\Buttons\UI-Quickslot2", strata: "HIGH");
                CollectUiParity(button + "Icon", "Texture", buttonMin, new Vector2(36) * scale,
                    parent: button, layer: "BACKGROUND", strata: "HIGH");
                CollectUiParity(button + "HotKey", "FontString", buttonMin + new Vector2(-2, 2) * scale,
                    new Vector2(36, 10) * scale, parent: button, point: "TOPLEFT", offsetX: "-2",
                    offsetY: "-2", font: "NumberFontNormalSmallGray", fontPath: @"Fonts\ARIALN.TTF",
                    fontSize: "12", color: "#999999FF", layer: "ARTWORK", strata: "HIGH");
                CollectUiParity(button + "NormalTexture", "NormalTexture",
                    buttonMin + new Vector2(-15, -14) * scale, new Vector2(66) * scale,
                    parent: button, point: "CENTER", offsetX: "0", offsetY: "-1",
                    texture: @"Interface\Buttons\UI-Quickslot2", strata: "HIGH");
            }

            ImGui.SetCursorScreenPos(buttonMin);
            if (ImGui.InvisibleButton($"##{name}-{i}", buttonMax - buttonMin)) UseAction(slotNumber);
            if (_actions[slotNumber] is { } action)
            {
                string iconPath = action.Kind == ActionSlot.Spell &&
                    _spellCatalog?.TryGet(action.ActionId, out SpellInfo spell) == true
                        ? ResolveSpellActionIcon(spell, _entities.TryGet(_net!.PlayerGuid,
                            out WorldEntity owner) ? owner : null)
                        : action.Kind == ActionSlot.Item && _items?.TryGet(action.ActionId, out ItemTemplate? item) == true && item is not null
                            ? item.IconPath : action.Kind == ActionSlot.Macro ? MacroIcon(action.ActionId)
                            : @"Interface\Icons\INV_Misc_QuestionMark.blp";
                uint icon = _gameplayArt.Handle(iconPath);
                if (icon != 0) dl.AddImage((nint)icon, buttonMin, buttonMax);
            }
            DrawSlotRing(dl, buttonMin, buttonMax, @"Interface\Buttons\UI-Quickslot2", scale);
        }
        if (proof && name == "MultiBarBottomLeft") MarkUiParityFrameComplete();
        ImGui.End();
    }

    private void DrawMicroMenu(Vector2 barMin, float scale)
    {
        if (_gameplayArt is null) return;
        Vector2 windowMin = barMin + new Vector2(552f, -5f) * scale;
        CollectGameplayLayout("micro-cluster", 552f, 710f, 211f, 58f,
            windowMin, new Vector2(211f, 58f) * scale);
        ImGui.SetNextWindowPos(windowMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(211f, 58f) * scale, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoBringToFrontOnFocus;
        if (!ImGui.Begin("##micro-menu", flags)) { ImGui.End(); return; }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        (string Art, string Label, bool Enabled, bool Pushed)[] buttons =
        [
            ("Character", "Character Info (C)", true, _characterOpen),
            ("Spellbook", "Spellbook & Abilities (P)", true, _spellbookOpen),
            ("Talents", "Talents", true, _talentOpen),
            ("Quest", "Quest Log", true, _questLogOpen),
            ("Socials", "Social", true, _socialOpen),
            ("World", "World Map", true, _worldMapOpen),
            ("MainMenu", "Game Menu (Esc)", true, _settingsOpen),
            ("Help", "Help Request", true, _helpOpen),
        ];

        for (int i = 0; i < buttons.Length; i++)
        {
            var button = buttons[i];
            Vector2 min = windowMin + new Vector2(26f * i, 0f) * scale;
            Vector2 max = min + new Vector2(29f, 58f) * scale;
            CollectGameplayLayout($"micro-{button.Art.ToLowerInvariant()}",
                552f + 26f * i, 710f, 29f, 58f, min, max - min);
            Vector2 mouse = ImGui.GetIO().MousePos;
            bool held = ImGui.IsMouseDown(ImGuiMouseButton.Left) &&
                mouse.X >= min.X && mouse.X <= max.X &&
                mouse.Y >= min.Y + 18f * scale && mouse.Y <= max.Y;
            bool pushed = button.Pushed || held;
            string state = !button.Enabled ? "Disabled" : pushed ? "Down" : "Up";
            string path = i == 0
                ? $@"Interface\Buttons\UI-MicroButtonCharacter-{state}"
                : $@"Interface\Buttons\UI-MicroButton-{button.Art}-{state}";
            uint texture = _gameplayArt.Handle(path);
            if (texture != 0) dl.AddImage((nint)texture, min, max);
            // MicroMenu.xml puts MicroButtonPortrait in OVERLAY, above the button state art.
            if (i == 0) DrawCharacterMicroPortrait(dl, min, scale, pushed);

            // The transparent top 18 pixels are authored decoration, not part of the hit rect.
            ImGui.SetCursorScreenPos(min + new Vector2(0f, 18f) * scale);
            if (ImGui.InvisibleButton($"##micro-{i}", new Vector2(29f, 40f) * scale) && button.Enabled)
            {
                switch (i)
                {
                    case 0:
                        _characterOpen = !_characterOpen;
                        if (_characterOpen) { _paperDollDirty = true; _spellbookOpen = false; }
                        break;
                    case 1:
                        _spellbookOpen = !_spellbookOpen;
                        if (_spellbookOpen) _characterOpen = false;
                        break;
                    case 2:
                        OpenTalentPanel();
                        break;
                    case 3:
                        _questLogOpen = !_questLogOpen;
                        if (_questLogOpen) { _questList = null; _questDetails = null; _questOffer = null; _questRequestItems = null; }
                        break;
                    case 4:
                        if (_socialOpen) _socialOpen = false; else OpenSocial();
                        break;
                    case 5:
                        _worldMapOpen = !_worldMapOpen;
                        break;
                    case 6:
                        OpenSettings();
                        break;
                    case 7:
                        if (_helpOpen) _helpOpen = false; else OpenHelp();
                        break;
                }
            }
            if (ImGui.IsItemHovered())
            {
                uint highlight = _gameplayArt.AdditiveHandle(@"Interface\Buttons\UI-MicroButton-Hilight");
                if (highlight != 0) dl.AddImage((nint)highlight, min, max);
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(button.Label);
                if (!button.Enabled) ImGui.TextDisabled("Not available yet");
                ImGui.EndTooltip();
            }
        }
        ImGui.End();
    }

    private void DrawCharacterMicroPortrait(ImDrawListPtr dl, Vector2 buttonMin,
        float scale, bool pushed)
    {
        uint portrait = _playerPortraitUsable ? _playerPortrait?.TextureHandle ?? 0 : 0;
        if (portrait == 0) return;

        // MicroMenu.xml:161-199,244-257: this is a crop of the same player portrait bake,
        // 18x25 at the button TOP -28. Render targets are vertically flipped for ImGui.
        Vector2 min = buttonMin + new Vector2(5.5f, 28f) * scale;
        Vector2 max = min + new Vector2(18f, 25f) * scale;
        Vector2 uv0 = pushed ? new Vector2(0.2666f, 0.8333f) : new Vector2(0.2f, 0.9f);
        Vector2 uv1 = pushed ? new Vector2(0.8666f, 0f) : new Vector2(0.8f, 0.0666f);
        uint tint = pushed ? 0x80ffffffu : 0xffffffffu;
        dl.AddImage((nint)portrait, min, max, uv0, uv1, tint);
    }

    private void SwapActions(int source, int destination)
    {
        if (_net is null || source == destination) return;
        ActionSlot? a = _actions[source], b = _actions[destination];
        _actions.Set(source, b);
        _actions.Set(destination, a);
        _net.SetActionButton((byte)source, b?.Packed ?? 0);
        _net.SetActionButton((byte)destination, a?.Packed ?? 0);
    }

    private void ClearAction(int slot)
    {
        if (_net is null) return;
        _actions.Set(slot, null);
        _net.SetActionButton((byte)slot, 0);
    }

    private void DrawMainMenuBarArt(ImDrawListPtr dl, Vector2 barMin, float scale)
    {
        uint dwarf = _gameplayArt!.Handle(@"Interface\MainMenuBar\UI-MainMenuBar-Dwarf.blp");
        if (dwarf != 0)
        {
            float[] top = [0.83203125f, 0.58203125f, 0.33203125f, 0.08203125f];
            for (int i = 0; i < 4; i++)
            {
                Vector2 min = barMin + new Vector2(i * 256f, 10f) * scale;
                Vector2 max = min + new Vector2(256f, 43f) * scale;
                dl.AddImage((nint)dwarf, min, max, new Vector2(0, top[i]), new Vector2(1, top[i] + 0.16796875f));
            }
        }

        uint cap = _gameplayArt.Handle(@"Interface\MainMenuBar\UI-MainMenuBar-EndCap-Dwarf.blp");
        if (cap != 0)
        {
            Vector2 size = new(128f * scale);
            // FrameXML anchors their centers at bar center +/-544, with bottoms flush to screen.
            Vector2 left = barMin + new Vector2(-96f, GameplayBarHeight - 128f) * scale;
            Vector2 right = barMin + new Vector2(992f, GameplayBarHeight - 128f) * scale;
            dl.AddImage((nint)cap, left, left + size);
            dl.AddImage((nint)cap, right, right + size, new Vector2(1, 0), new Vector2(0, 1));
        }

        float pageSize = 11f * scale;
        DrawCenteredActionText(dl,
            barMin + new Vector2(GameplayBarWidth * 0.5f + 30f, GameplayBarHeight * 0.5f + 5f) * scale,
            _actionPage.ToString(), pageSize, UiGoldU32());
    }

    private void DrawExpBar(ImDrawListPtr dl, Vector2 barMin, float scale)
    {
        if (_gameplayArt is null || _net is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;

        uint current = player.Fields.Experience;
        uint maximum = player.Fields.NextLevelExperience;
        float fraction = maximum > 0 ? Math.Clamp((float)current / maximum, 0f, 1f) : 0f;
        Vector2 size = new(GameplayBarWidth * scale, 13f * scale);
        dl.AddRectFilled(barMin, barMin + size, 0x80000000u);
        DrawVanillaStatusBar(dl, barMin, size, fraction, new Vector4(0.58f, 0f, 0.55f, 1f));
        uint rested = player.Fields.RestStateExperience;
        if (maximum > 0 && rested > 0)
        {
            float restedFraction = Math.Clamp((float)(current + Math.Min(rested, maximum)) / maximum, fraction, 1f);
            Vector2 restedMin = new(barMin.X + size.X * fraction, barMin.Y);
            Vector2 restedMax = new(barMin.X + size.X * restedFraction, barMin.Y + size.Y);
            dl.AddRectFilled(restedMin, restedMax, 0xB0B06000u);
        }

        uint dwarf = _gameplayArt.Handle(@"Interface\MainMenuBar\UI-MainMenuBar-Dwarf.blp");
        if (dwarf != 0)
        {
            float center = barMin.X + GameplayBarWidth * 0.5f * scale;
            (float X, float Top)[] notches =
            [
                (-384f, 0.79296875f), (-128f, 0.54296875f),
                (128f, 0.29296875f), (384f, 0.04296875f),
            ];
            foreach ((float x, float top) in notches)
            {
                Vector2 min = new(center + (x - 128f) * scale, barMin.Y);
                Vector2 max = min + new Vector2(256f, 10f) * scale;
                dl.AddImage((nint)dwarf, min, max, new Vector2(0f, top),
                    new Vector2(1f, top + 0.0390625f));
            }
        }

        Vector2 mouse = ImGui.GetIO().MousePos;
        if (mouse.X >= barMin.X && mouse.X <= barMin.X + size.X &&
            mouse.Y >= barMin.Y && mouse.Y <= barMin.Y + size.Y)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted($"Experience: {current} / {maximum}");
            ImGui.TextUnformatted($"Rested bonus: {rested} ({RestStateName(player.Fields.RestState)})");
            ImGui.EndTooltip();
        }
    }

    private void DrawPerformanceMeter(ImDrawListPtr dl, Vector2 barMin, float scale, Vector2 display)
    {
        if (_gameplayArt is null) return;
        uint texture = _gameplayArt.Handle(@"Interface\MainMenuBar\UI-MainMenuBar-PerformanceBar.blp");
        if (texture == 0) return;

        Vector2 frameBottomRight = new(
            barMin.X + (GameplayBarWidth - 227f) * scale,
            display.Y + 10f * scale);
        Vector2 frameTopLeft = frameBottomRight - new Vector2(16f, 64f) * scale;
        Vector2 textureTopRight = new(frameBottomRight.X, frameTopLeft.Y);
        Vector2 textureTopLeft = textureTopRight - new Vector2(20f * scale, 0f);
        Vector2 textureBottomRight = textureTopLeft + new Vector2(20f, 66f) * scale;

        int latency = _net?.LatencyMs ?? 0;
        Vector4 tint = latency > 600 ? new Vector4(1f, 0f, 0f, 1f)
            : latency > 300 ? new Vector4(1f, 1f, 0f, 1f)
            : new Vector4(0f, 1f, 0f, 1f);
        dl.AddImage((nint)texture, textureTopLeft, textureBottomRight,
            Vector2.Zero, Vector2.One, ImGui.ColorConvertFloat4ToU32(tint));

        Vector2 mouse = ImGui.GetIO().MousePos;
        if (mouse.X >= frameTopLeft.X && mouse.X <= frameBottomRight.X &&
            mouse.Y >= frameTopLeft.Y && mouse.Y <= frameBottomRight.Y)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted($"Latency: {latency}ms");
            ImGui.EndTooltip();
        }
    }

    private bool DrawPageArrowButton(ImDrawListPtr dl, Vector2 barMin, float scale,
        Vector2 center, string direction, int id)
    {
        Vector2 min = barMin + (center - new Vector2(16f)) * scale;
        Vector2 max = min + new Vector2(32f) * scale;
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton($"##action-page-{id}", max - min);
        bool pushed = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();

        uint texture = _gameplayArt?.Handle(
            $@"Interface\MainMenuBar\UI-MainMenu-Scroll{direction}Button-{(pushed ? "Down" : "Up")}") ?? 0;
        if (texture != 0) dl.AddImage((nint)texture, min, max);
        if (hovered)
        {
            // ActionBar.xml marks this texture ADD. GameplayArt converts its black additive
            // background to transparent for ImGui's alpha compositor.
            uint highlight = _gameplayArt?.AdditiveHandle(
                $@"Interface\MainMenuBar\UI-MainMenu-Scroll{direction}Button-Highlight") ?? 0;
            if (highlight != 0) dl.AddImage((nint)highlight, min, max);
        }
        return clicked;
    }

    private int ActionWireSlot(int button) => (_actionPage - 1) * 12 + button;

    private void ChangeActionPage(int delta)
    {
        _actionPage = ((_actionPage - 1 + delta + ActionPageCount) % ActionPageCount) + 1;
        _pressedActionSlot = -1;
        _draggingActionSlot = -1;
    }

    private static uint UiGoldU32() =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.82f, 0f, 1f));

    private static void DrawCenteredActionText(ImDrawListPtr dl, Vector2 center, string text,
        float size, uint color)
    {
        ImFontPtr font = ImGui.GetFont();
        Vector2 extent = ImGui.CalcTextSize(text) * (size / MathF.Max(ImGui.GetFontSize(), 1f));
        Vector2 pos = center - extent * 0.5f;
        dl.AddText(font, size, pos + Vector2.One, 0xff000000u, text);
        dl.AddText(font, size, pos, color, text);
    }

    private void DrawSlotRing(ImDrawListPtr dl, Vector2 buttonMin, Vector2 buttonMax,
        string art, float scale, uint tint = 0xffffffffu)
    {
        uint ring = _gameplayArt!.Handle(art);
        if (ring == 0) return;
        // NormalTexture is 66x66, centered on the 36x36 button with a (0,-1) offset.
        // FrameXML Y is up: its -1 anchor offset moves the texture one pixel down in screen space.
        Vector2 center = (buttonMin + buttonMax) * 0.5f + new Vector2(0, scale);
        Vector2 half = new(33f * scale);
        dl.AddImage((nint)ring, center - half, center + half, Vector2.Zero, Vector2.One, tint);
    }

    /// <summary>Hotkey label: right-justified in the top corner (reference offset (-2,-2)),
    /// with the standard 1.12 black text shadow.</summary>
    private static void DrawActionText(ImDrawListPtr dl, Vector2 buttonMin, string text, float scale,
        uint color)
    {
        float size = 12f * scale;
        ImFontPtr font = ImGui.GetFont();
        // This binding has no CalcTextSizeA; scale the base measure instead (see WowSkin.cs).
        float width = ImGui.CalcTextSize(text).X * (size / Math.Max(1f, ImGui.GetFontSize()));
        Vector2 pos = buttonMin + new Vector2(34f * scale - width, 2f * scale);
        dl.AddText(font, size, pos + Vector2.One * scale, 0xff000000, text);
        dl.AddText(font, size, pos, color, text);
    }

    /// <summary>Stack count for an ITEM action, bottom-right (reference offset (-2,2)).</summary>
    private static void DrawActionCount(ImDrawListPtr dl, Vector2 buttonMax, int count, float scale)
    {
        string text = count.ToString();
        float size = 12f * scale;
        ImFontPtr font = ImGui.GetFont();
        Vector2 extent = ImGui.CalcTextSize(text) * (size / Math.Max(1f, ImGui.GetFontSize()));
        Vector2 pos = buttonMax - extent - new Vector2(2f, 1f) * scale;
        dl.AddText(font, size, pos + Vector2.One * scale, 0xff000000, text);
        dl.AddText(font, size, pos, 0xffffffff, text);
    }

    private ActionButtonVerdict ComputeButtonVerdict(
        int slot,
        ActionSlot? action,
        SpellInfo? spell,
        WorldEntity? player,
        bool pushed,
        bool hover,
        bool carriedGrid)
    {
        bool isItem = action is { Kind: ActionSlot.Item };
        uint actionId = action?.ActionId ?? 0;
        ButtonUsability usability = action is null ? ButtonUsability.Unusable : ButtonUsability.Usable;
        int powerCost = 0, currentPower = 0, baseMana = 0, stackCount = 0;
        bool equipped = false;

        if (player is { } p)
        {
            baseMana = (int)Math.Min(p.Fields.BaseMana, int.MaxValue);
            if (spell is { } sp)
            {
                if (p.IsDead)
                {
                    usability = ButtonUsability.Unusable;
                }
                else
                {
                    byte powerType = (byte)sp.PowerType;
                    uint baseAmount = sp.ManaCostPercent == 0 ? 0u
                        : powerType == 0 ? p.Fields.BaseMana
                        : p.Fields.MaxPower(powerType);
                    uint cost = sp.ManaCost + baseAmount * sp.ManaCostPercent / 100;
                    uint power = p.Fields.Power(powerType);
                    powerCost = (int)Math.Min(cost, int.MaxValue);
                    currentPower = (int)Math.Min(power, int.MaxValue);
                    if (cost > 0 && power < cost)
                        usability = ButtonUsability.NotEnoughPower;
                }
            }
            else if (isItem)
            {
                stackCount = CountItemInBags(p, actionId);
                equipped = IsItemEquipped(p, actionId);
                usability = stackCount > 0 || equipped
                    ? ButtonUsability.Usable : ButtonUsability.Unusable;
            }
        }

        ButtonRange range = ButtonRange.NoCheck;
        int rangeIndex = spell is { } indexed
            ? (int)Math.Min(indexed.RangeIndex, int.MaxValue) : 0;
        float rangeMin = 0f, rangeMax = 0f, distance = -1f;
        if (spell is { } rangeSpell)
            (range, rangeMin, rangeMax, distance) = ComputeButtonRange(rangeSpell);

        bool isAttack = spell is { Id: 6603 };
        bool engaged = isAttack && _net is not null && _combat.IsEngaged(_net.PlayerGuid);
        bool autoRepeat = spell is { } repeat && repeat.Id == _autoRepeatSpell;
        bool checkedState = engaged || autoRepeat ||
            (spell is { } pending &&
             (pending.Id == _pendingCastSpell || pending.Id == _queuedMeleeSpell));

        return new ActionButtonVerdict(
            NowSeconds(), slot, isItem, actionId, usability, range,
            pushed, hover, checkedState, engaged || autoRepeat, carriedGrid, equipped,
            powerCost, currentPower, baseMana, rangeIndex, rangeMin, rangeMax, distance, stackCount);
    }

    private (ButtonRange Range, float Min, float Max, float Distance)
        ComputeButtonRange(in SpellInfo spell)
    {
        if (_net is null || _controller is null || _spellCatalog is null || _selectionGuid == 0 ||
            !_entities.TryGet(_selectionGuid, out WorldEntity target) ||
            !_spellCatalog.TryGetRange(spell.RangeIndex, out SpellRangeRow row))
            return (ButtonRange.NoCheck, 0f, 0f, -1f);

        float selfReach = _entities.TryGet(_net.PlayerGuid, out WorldEntity self)
            ? self.Fields.CombatReach : 1.5f;
        float targetReach = target.Fields.CombatReach;
        float min = row.Min, max = row.Max;
        if (row.Melee)
        {
            min = 0f;
            max = MathF.Max(selfReach + targetReach + 1.3333f, 5f);
        }
        else
        {
            if (min <= 0f && max <= 0f)
                return (ButtonRange.NoCheck, min, max, -1f);
            max += selfReach + targetReach;
            if (min != 0f) min += selfReach + targetReach;
        }

        float distanceSquared = Vector3.DistanceSquared(_controller.Position, target.Position);
        float distance = MathF.Sqrt(distanceSquared);
        ButtonRange range = distanceSquared >= min * min && distanceSquared <= max * max
            ? ButtonRange.InRange : ButtonRange.OutOfRange;
        return (range, min, max, distance);
    }

    private void EmitActionButtonVerdict(in ActionButtonVerdict verdict)
    {
        ActionButtonVerdict? previous = _lastActionButtonVerdicts[verdict.Slot];
        bool changed = previous is not { } old ||
            old.Usability != verdict.Usability ||
            old.Range != verdict.Range ||
            old.Flashing != verdict.Flashing ||
            old.Checked != verdict.Checked;
        _lastActionButtonVerdicts[verdict.Slot] = verdict;
        if (!changed) return;
        _verdicts.Add(verdict);
        Console.WriteLine($"[verdict:action] {verdict.ToLine()}");
    }

    /// <summary>Total stack count of an item entry across the backpack and equipped bags
    /// (the GetActionCount an ITEM action shows bottom-right).</summary>
    private int CountItemInBags(WorldEntity player, uint entry)
    {
        int total = 0;
        for (int i = 0; i < 16; i++)
        {
            ulong guid = player.Fields.PlayerBackpackSlot(i);
            if (guid != 0 && _entities.TryGet(guid, out WorldEntity item) && item.Entry == entry)
                total += (int)Math.Max(1, item.Fields.ItemStackCount);
        }
        for (int bagIndex = 0; bagIndex < 4; bagIndex++)
        {
            ulong bagGuid = player.Fields.PlayerInventorySlot(19 + bagIndex);
            if (bagGuid == 0 || !_entities.TryGet(bagGuid, out WorldEntity bag)) continue;
            int slots = (int)Math.Min(bag.Fields.ContainerNumSlots, 36);
            for (int slot = 0; slot < slots; slot++)
            {
                ulong guid = bag.Fields.ContainerSlot(slot);
                if (guid != 0 && _entities.TryGet(guid, out WorldEntity item) && item.Entry == entry)
                    total += (int)Math.Max(1, item.Fields.ItemStackCount);
            }
        }
        return total;
    }

    /// <summary>IsEquippedAction: any worn slot 0..18 holds this entry (green ADD border).</summary>
    private bool IsItemEquipped(WorldEntity player, uint entry)
    {
        for (int slot = 0; slot < 19; slot++)
        {
            ulong guid = player.Fields.PlayerInventorySlot(slot);
            if (guid != 0 && _entities.TryGet(guid, out WorldEntity item) && item.Entry == entry)
                return true;
        }
        return false;
    }

    private static void DrawCooldownSwipe(ImDrawListPtr dl, Vector2 min, Vector2 max, float elapsedFraction)
    {
        Vector2 center = (min + max) * 0.5f;
        float radius = (max.X - min.X) * 0.72f;
        float start = -MathF.PI * 0.5f + elapsedFraction * MathF.Tau;
        const int segments = 28;
        uint shade = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.60f));
        for (int i = 0; i < segments; i++)
        {
            float a0 = start + (MathF.Tau - (start + MathF.PI * 0.5f)) * i / segments;
            float a1 = start + (MathF.Tau - (start + MathF.PI * 0.5f)) * (i + 1) / segments;
            Vector2 p0 = center + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * radius;
            Vector2 p1 = center + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius;
            dl.AddTriangleFilled(center, p0, p1, shade);
        }
    }

    private void DisposeGameplayUi()
    {
        _gameplayArt?.Dispose();
        _gameplayArt = null;
    }
}
