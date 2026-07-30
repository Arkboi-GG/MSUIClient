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

    private static readonly Key[] ActionKeys =
    [
        Key.Number1, Key.Number2, Key.Number3, Key.Number4, Key.Number5, Key.Number6,
        Key.Number7, Key.Number8, Key.Number9, Key.Number0, Key.Minus, Key.Equal,
    ];
    private static readonly string[] ActionLabels = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "="];

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
        for (int i = 0; i < ActionKeys.Length; i++)
        {
            bool down = _window.IsDown(ActionKeys[i]);
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
        if (_actions.IsOnCooldown(spellId, now) || now < _globalCooldownUntil)
        {
            EmitCastVerdict(spellId, CastTargetReason.CooldownOrGlobalCooldown, 0, sent: false);
            RefuseCast("Spell is not ready yet.");
            return;
        }
        if (!spell.AutoRepeat && !spell.OnNextSwing && _pendingCastSpell != 0)
        {
            if (_pendingCastSpell != spellId) RefuseCast("Another action is in progress");
            EmitCastVerdict(spellId, CastTargetReason.PendingCast, 0, sent: false);
            return;
        }
        if (_entities.TryGet(_net.PlayerGuid, out WorldEntity caster))
        {
            if (caster.Fields.MountDisplayId != 0 && (spell.Attributes & 0x0100_0000u) == 0)
            {
                EmitCastVerdict(spellId, CastTargetReason.Mounted, 0, sent: false);
                RefuseCast("You are mounted");
                return;
            }
        }

        CastTargetVerdict targetVerdict = ResolveCastTarget(spell);
        ulong target = targetVerdict.Guid;
        if (targetVerdict.Kind == CastTargetKind.Refused)
        {
            EmitCastVerdict(spellId, targetVerdict.Reason, target, sent: false);
            RefuseCast(_selectionGuid == 0 ? "You have no target." : "Invalid target");
            return;
        }
        if (target == _selectionGuid && target != _net.PlayerGuid &&
            CastRangeRefusal(spell) is { } rangeFailure)
        {
            EmitCastVerdict(spellId, rangeFailure.Reason, target, sent: false);
            RefuseCast(rangeFailure.Text);
            return;
        }
        _net.CastSpell(spellId, target);
        EmitCastVerdict(spellId, targetVerdict.Reason, target, sent: true);
        if (spell.AutoRepeat) _autoRepeatSpell = spellId;
        else if (spell.OnNextSwing) _queuedMeleeSpell = spellId;
        else _pendingCastSpell = spellId;
        if (spell.StartRecoveryMs > 0)
        {
            _globalCooldownUntil = now + spell.StartRecoveryMs / 1000.0;
            _actions.StartCooldown(spellId, spell.StartRecoveryCategory, spell.StartRecoveryMs, now);
        }
    }

    private void RefuseCast(string text) => PushCenterText(text, CenterCombatTextStyle.Damage);

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
            selected = CastCandidate(selectedEntity, _selectionGuid == _net!.PlayerGuid);
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
            self = CastCandidate(player, isSelf: true);
        return CastTargetLaw.Resolve(spell, selected, self);
    }

    private void EmitCastVerdict(uint spellId, CastTargetReason reason, ulong resolvedGuid, bool sent)
    {
        var verdict = new CastVerdict(
            NowSeconds(), spellId, reason, _selectionGuid, resolvedGuid, sent);
        _verdicts.Add(verdict);
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
        ImDrawListPtr bg = ImGui.GetBackgroundDrawList();

        // FrameXML child order: XP and the LOW-strata latency meter sit beneath the dwarf art.
        DrawExpBar(bg, barMin, scale);
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
            ActionSlot? slot = _actions[wireSlot];

            if (slot is { } action)
            {
                string iconPath = @"Interface\Icons\INV_Misc_QuestionMark.blp";
                string title = $"Action {action.ActionId}";
                SpellInfo? spellInfo = null;
                ItemTemplate? itemInfo = null;
                if (action.Kind == ActionSlot.Spell && _spellCatalog?.TryGet(action.ActionId, out SpellInfo spell) == true)
                {
                    spellInfo = spell;
                    iconPath = spell.IconPath;
                    title = spell.Rank.Length > 0 ? $"{spell.Name} ({spell.Rank})" : spell.Name;
                }
                else if (action.Kind == ActionSlot.Item && _items?.TryGet(action.ActionId, out ItemTemplate? item) == true && item is not null)
                {
                    itemInfo = item;
                    iconPath = item.IconPath;
                    title = item.Name;
                }

                // ── the reference three-way usability verdict (ActionButton_UpdateUsable) ──
                // usable: icon+ring white; not enough power: icon+ring (0.5,0.5,1);
                // otherwise unusable: icon (0.4,0.4,0.4), ring reset to white.
                bool usable = true, oom = false;
                int itemCount = 0;
                bool equipped = false;
                if (player is { } p)
                {
                    if (spellInfo is { } sp) (usable, oom) = SpellActionUsable(sp, p);
                    else if (action.Kind == ActionSlot.Item)
                    {
                        itemCount = CountItemInBags(p, action.ActionId);
                        equipped = IsItemEquipped(p, action.ActionId);
                        usable = itemCount > 0 || equipped;
                    }
                }
                uint iconTint = oom ? 0xffff8080u : usable ? 0xffffffffu : 0xff666666u;
                uint icon = _gameplayArt.Handle(iconPath);
                if (icon != 0) dl.AddImage((nint)icon, buttonMin, buttonMax, Vector2.Zero, Vector2.One, iconTint);

                // current / auto-repeat state (CheckedTexture + red flash)
                bool isAttack = spellInfo is { Id: 6603 };
                bool engaged = _combat.IsEngaged(_net.PlayerGuid);
                bool autoRepeat = spellInfo is { } s2 && s2.Id == _autoRepeatSpell;
                bool current = (isAttack && engaged) || autoRepeat ||
                               (spellInfo is { } s3 && (s3.Id == _pendingCastSpell || s3.Id == _queuedMeleeSpell));
                if (((isAttack && engaged) || autoRepeat) && flashPhase)
                {
                    uint flash = _gameplayArt.Handle(@"Interface\Buttons\UI-QuickslotRed");
                    if (flash != 0) dl.AddImage((nint)flash, buttonMin, buttonMax);
                }

                float cooldown = action.Kind == ActionSlot.Spell
                    ? _actions.CooldownFraction(action.ActionId, now) : 0f;
                if (cooldown > 0f) DrawCooldownSwipe(dl, buttonMin, buttonMax, cooldown);

                ImGui.SetCursorScreenPos(buttonMin);
                bool clicked = ImGui.InvisibleButton($"##action-{i}", buttonMax - buttonMin);
                if (ImGui.IsItemHovered()) hoveredSlot = wireSlot;
                if (ImGui.IsItemActivated())
                {
                    _pressedActionSlot = wireSlot;
                    _actionPressPosition = ImGui.GetIO().MousePos;
                }
                if (clicked && _draggingActionSlot < 0 && !PlaceCarriedItemOnAction(wireSlot))
                    UseAction(wireSlot);

                // PUSHED replaces the normal state while the mouse or the bound key is down.
                bool pushed = ImGui.IsItemActive() || _window.IsDown(ActionKeys[i]);
                if (pushed)
                {
                    uint depress = _gameplayArt.Handle(@"Interface\Buttons\UI-Quickslot-Depress");
                    if (depress != 0) dl.AddImage((nint)depress, buttonMin, buttonMax);
                }
                if (ImGui.IsItemHovered())
                {
                    uint highlight = _gameplayArt.AdditiveHandle(@"Interface\Buttons\ButtonHilight-Square");
                    if (highlight != 0) dl.AddImage((nint)highlight, buttonMin, buttonMax);
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(title);
                    ImGui.TextDisabled($"Action {wireSlot + 1}");
                    ImGui.EndTooltip();
                }
                if (current)
                {
                    uint check = _gameplayArt.AdditiveHandle(@"Interface\Buttons\CheckButtonHilight");
                    if (check != 0) dl.AddImage((nint)check, buttonMin, buttonMax);
                }
                if (equipped)
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
                    oom ? 0xffff8080u : 0xffffffffu);

                // Hotkey: red (1.0,0.1,0.1) while the selection is out of range, grey otherwise.
                uint hotkeyColor = 0xff999999;
                if (spellInfo is { } rangeSpell && ActionInRange(rangeSpell) == false)
                    hotkeyColor = 0xff1a1aff;
                DrawActionText(dl, buttonMin, ActionLabels[i], scale, hotkeyColor);

                if (itemInfo is not null && itemCount > 0)
                    DrawActionCount(dl, buttonMax, itemCount, scale);
            }
            else
            {
                ImGui.SetCursorScreenPos(buttonMin);
                if (ImGui.InvisibleButton($"##action-{i}", buttonMax - buttonMin))
                    PlaceCarriedItemOnAction(wireSlot);
                if (ImGui.IsItemHovered()) hoveredSlot = wireSlot;
                DrawSlotRing(dl, buttonMin, buttonMax,
                    gridShown ? @"Interface\Buttons\UI-Quickslot" : @"Interface\Buttons\UI-Quickslot2", scale);
                DrawActionText(dl, buttonMin, ActionLabels[i], scale, 0xff999999);
            }
        }

        // ActionBar.xml anchors are Y-up. ImGui is Y-down, so XML y=-22/-42 become +22/+42.
        if (DrawPageArrowButton(dl, barMin, scale, new Vector2(522f, 22f), "Up", 0))
            ChangeActionPage(1);
        if (DrawPageArrowButton(dl, barMin, scale, new Vector2(522f, 42f), "Down", 1))
            ChangeActionPage(-1);
        ImGui.End();

        DrawMicroMenu(barMin, scale);

        if (_draggingActionSlot >= 0 && _actions[_draggingActionSlot] is { } dragged)
        {
            string iconPath = dragged.Kind == ActionSlot.Spell && _spellCatalog?.TryGet(dragged.ActionId, out SpellInfo info) == true
                ? info.IconPath
                : dragged.Kind == ActionSlot.Item && _items?.TryGet(dragged.ActionId, out ItemTemplate? item) == true && item is not null
                    ? item.IconPath : @"Interface\Icons\INV_Misc_QuestionMark.blp";
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
            if (_draggingSpellId != 0)
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

    private void DrawMicroMenu(Vector2 barMin, float scale)
    {
        if (_gameplayArt is null) return;
        Vector2 windowMin = barMin + new Vector2(552f, -5f) * scale;
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
            ("Talents", "Talents", false, false),
            ("Quest", "Quest Log", false, false),
            ("Socials", "Social", false, false),
            ("World", "World Map", false, false),
            ("MainMenu", "Game Menu (Esc)", true, _settingsOpen),
            ("Help", "Help Request", false, false),
        ];

        for (int i = 0; i < buttons.Length; i++)
        {
            var button = buttons[i];
            Vector2 min = windowMin + new Vector2(26f * i, 0f) * scale;
            Vector2 max = min + new Vector2(29f, 58f) * scale;
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
                    case 6:
                        OpenSettings();
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
        Vector2 center = (buttonMin + buttonMax) * 0.5f + new Vector2(0, -scale);
        Vector2 half = new(33f * scale);
        dl.AddImage((nint)ring, center - half, center + half, Vector2.Zero, Vector2.One, tint);
    }

    /// <summary>Hotkey label: right-justified in the top corner (reference offset (-2,-2)),
    /// with the standard 1.12 black text shadow.</summary>
    private static void DrawActionText(ImDrawListPtr dl, Vector2 buttonMin, string text, float scale,
        uint color)
    {
        float size = 11f * scale;
        ImFontPtr font = ImGui.GetFont();
        // This binding has no CalcTextSizeA; scale the base measure instead (see WowSkin.cs).
        float width = ImGui.CalcTextSize(text).X * (size / Math.Max(1f, ImGui.GetFontSize()));
        Vector2 pos = buttonMin + new Vector2(34f * scale - width, 1f * scale);
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

    /// <summary>
    /// The power leg of the reference usable walk (benilla usable.rs:172-186): dead is grey;
    /// cost = ManaCost + base * ManaCostPercent / 100, where base is BASE_MANA for power type
    /// mana and MAXPOWER for the rest; short power reads blue (oom), everything else white.
    /// The other eleven gates (reagents, stances, aura states…) are later port slices.
    /// </summary>
    private static (bool Usable, bool Oom) SpellActionUsable(in SpellInfo spell, WorldEntity player)
    {
        if (player.IsDead) return (false, false);
        byte powerType = (byte)spell.PowerType;
        uint baseAmount = spell.ManaCostPercent == 0 ? 0u
            : powerType == 0 ? player.Fields.BaseMana
            : player.Fields.MaxPower(powerType);
        uint cost = spell.ManaCost + baseAmount * spell.ManaCostPercent / 100;
        if (cost > 0 && player.Fields.Power(powerType) < cost) return (false, true);
        return (true, false);
    }

    /// <summary>
    /// Range verdict against the current selection (benilla state.rs resolve_range): melee rows
    /// use edge-to-edge reach max(selfReach+targetReach+1.3333, 5.0); ranged rows widen both
    /// ends by the reaches, the min only when it is non-zero. Null = no gate (never reddens).
    /// </summary>
    private bool? ActionInRange(in SpellInfo spell)
    {
        if (_net is null || _controller is null || _spellCatalog is null || _selectionGuid == 0 ||
            !_entities.TryGet(_selectionGuid, out WorldEntity target) ||
            !_spellCatalog.TryGetRange(spell.RangeIndex, out SpellRangeRow row))
            return null;
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
            if (min <= 0f && max <= 0f) return null; // self/no-range row: no gate
            max += selfReach + targetReach;
            if (min != 0f) min += selfReach + targetReach;
        }
        float d2 = Vector3.DistanceSquared(_controller.Position, target.Position);
        return d2 >= min * min && d2 <= max * max;
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
