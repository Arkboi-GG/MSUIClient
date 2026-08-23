using System.Numerics;
using System.Text;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly byte[] _guildAddMemberInput =
        new byte[GuildFrameUiLaw.AddMemberMaxLetters + 1];
    private bool _guildAddMemberFocusRequested;
    private bool _guildAddMemberEditFocused;
    private string _guildAddRankInput = "";
    private bool _guildAddRankFocusRequested;
    private bool _guildAddRankEditFocused;
    private readonly string[] _guildNoteInputs = ["", ""];
    private readonly bool[] _guildNoteFocusRequested = new bool[2];
    private readonly bool[] _guildNoteEditFocused = new bool[2];
    private readonly string[] _guildPopupMemberNames = ["", ""];

    private void ShowGuildAddMemberPopup() => ExecuteStaticPopupPlan(
        StaticPopupCoordinatorLaw.Show(_staticPopupSlots,
            GuildFrameUiLaw.AddMemberDefinition, playerDeadOrGhost: false));

    private void ShowGuildAddRankPopup() => ExecuteStaticPopupPlan(
        StaticPopupCoordinatorLaw.Show(_staticPopupSlots,
            GuildFrameUiLaw.AddRankDefinition, playerDeadOrGhost: false));

    private void ShowGuildActionPopup(StaticPopupCoordinatorLaw.Definition definition,
        string value) => ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(
            _staticPopupSlots, definition, playerDeadOrGhost: false, dataToken: value));

    private void ShowGuildInvitePopup(in GuildInviteWire invite) => ExecuteStaticPopupPlan(
        StaticPopupCoordinatorLaw.Show(_staticPopupSlots,
            GuildFrameUiLaw.InviteDefinition, playerDeadOrGhost: false,
            dataToken: GuildFrameUiLaw.InvitePopupToken(invite.Inviter, invite.Guild)));

    private void ApplyGuildInvitePopupEffect(StaticPopupCoordinatorLaw.Effect effect)
    {
        switch (effect.Kind)
        {
            case StaticPopupCoordinatorLaw.EffectKind.Accept:
                _net?.GuildAccept();
                break;
            case StaticPopupCoordinatorLaw.EffectKind.CancelWithoutReason:
            case StaticPopupCoordinatorLaw.EffectKind.CancelOverride:
            case StaticPopupCoordinatorLaw.EffectKind.CancelClicked:
            case StaticPopupCoordinatorLaw.EffectKind.CancelTimeout:
                _net?.GuildDecline();
                break;
        }
    }

    private string GuildAddMemberInput()
    {
        int end = Array.IndexOf(_guildAddMemberInput, (byte)0);
        return Encoding.UTF8.GetString(_guildAddMemberInput, 0,
            end < 0 ? _guildAddMemberInput.Length : end);
    }

    private void ApplyGuildAddMemberPopupEffect(StaticPopupCoordinatorLaw.Effect effect)
    {
        switch (effect.Kind)
        {
            case StaticPopupCoordinatorLaw.EffectKind.ClearEditBox:
                Array.Clear(_guildAddMemberInput);
                break;
            case StaticPopupCoordinatorLaw.EffectKind.OnShow:
                _guildAddMemberFocusRequested = true;
                break;
            case StaticPopupCoordinatorLaw.EffectKind.Hide:
            case StaticPopupCoordinatorLaw.EffectKind.ClearEditBoxFocus:
                _guildAddMemberFocusRequested = false;
                _guildAddMemberEditFocused = false;
                if (_chatEditOpen) _chatEditJustOpened = true;
                break;
            case StaticPopupCoordinatorLaw.EffectKind.Accept:
            case StaticPopupCoordinatorLaw.EffectKind.EditBoxEnter:
                _net?.GuildInvite(GuildAddMemberInput());
                break;
        }
    }

    private void ApplyGuildMemberPopupEffect(StaticPopupCoordinatorLaw.Effect effect)
    {
        int seat = Math.Clamp(effect.Slot - 1, 0, 1);
        switch (effect.Kind)
        {
            case StaticPopupCoordinatorLaw.EffectKind.PrepareContent:
                StaticPopupCoordinatorLaw.Instance? instance = effect.Slot == 1
                    ? _staticPopupSlots.First : _staticPopupSlots.Second;
                _guildPopupMemberNames[seat] = instance?.DataToken ??
                    SelectedGuildMember()?.Name ?? "";
                break;
            case StaticPopupCoordinatorLaw.EffectKind.ClearEditBox:
                _guildNoteInputs[seat] = "";
                break;
            case StaticPopupCoordinatorLaw.EffectKind.OnShow:
                if (effect.Type is GuildFrameUiLaw.SetPublicNotePopupType or
                    GuildFrameUiLaw.SetOfficerNotePopupType)
                {
                    GuildMember? member = _guildMembers.FirstOrDefault(row =>
                        string.Equals(row.Name, _guildPopupMemberNames[seat],
                            StringComparison.OrdinalIgnoreCase));
                    string note = effect.Type == GuildFrameUiLaw.SetPublicNotePopupType
                        ? member?.PublicNote ?? "" : member?.OfficerNote ?? "";
                    _guildNoteInputs[seat] = GuildFrameUiLaw.TruncateNote(note);
                    _guildNoteFocusRequested[seat] = true;
                }
                break;
            case StaticPopupCoordinatorLaw.EffectKind.Hide:
            case StaticPopupCoordinatorLaw.EffectKind.ClearEditBoxFocus:
                _guildNoteFocusRequested[seat] = false;
                _guildNoteEditFocused[seat] = false;
                if (_chatEditOpen) _chatEditJustOpened = true;
                if (effect.Kind == StaticPopupCoordinatorLaw.EffectKind.Hide)
                    _guildPopupMemberNames[seat] = "";
                break;
            case StaticPopupCoordinatorLaw.EffectKind.Accept:
            case StaticPopupCoordinatorLaw.EffectKind.EditBoxEnter:
                string name = _guildPopupMemberNames[seat];
                if (effect.Type == GuildFrameUiLaw.RemoveMemberPopupType)
                {
                    _net?.GuildRemove(name);
                    _guildMemberDetailOpen = false;
                    _guildSelected = -1;
                }
                else if (effect.Type == GuildFrameUiLaw.ConfirmPromotePopupType)
                    _net?.GuildLeader(name);
                else if (effect.Type == GuildFrameUiLaw.ConfirmLeavePopupType)
                    _net?.GuildLeave();
                else if (effect.Type == GuildFrameUiLaw.SetPublicNotePopupType)
                    _net?.GuildSetPublicNote(name, _guildNoteInputs[seat]);
                else if (effect.Type == GuildFrameUiLaw.SetOfficerNotePopupType)
                    _net?.GuildSetOfficerNote(name, _guildNoteInputs[seat]);
                break;
        }
    }

    private void ApplyGuildAddRankPopupEffect(StaticPopupCoordinatorLaw.Effect effect)
    {
        switch (effect.Kind)
        {
            case StaticPopupCoordinatorLaw.EffectKind.ClearEditBox:
                _guildAddRankInput = "";
                break;
            case StaticPopupCoordinatorLaw.EffectKind.OnShow:
                _guildAddRankFocusRequested = true;
                break;
            case StaticPopupCoordinatorLaw.EffectKind.Hide:
            case StaticPopupCoordinatorLaw.EffectKind.ClearEditBoxFocus:
                _guildAddRankFocusRequested = false;
                _guildAddRankEditFocused = false;
                if (_chatEditOpen) _chatEditJustOpened = true;
                break;
            case StaticPopupCoordinatorLaw.EffectKind.Accept:
            case StaticPopupCoordinatorLaw.EffectKind.EditBoxEnter:
                _net?.GuildAddRank(_guildAddRankInput);
                break;
        }
    }

    private void DrawGuildMemberPopups()
    {
        DrawGuildActionPopup();
        DrawGuildNotePopup(GuildFrameUiLaw.SetPublicNotePopupType, "Set Player Note:");
        DrawGuildNotePopup(GuildFrameUiLaw.SetOfficerNotePopupType, "Set Officer Note:");
        DrawGuildAddRankPopup();
    }

    private void DrawGuildInvitePopup()
    {
        var popup = GuildFrameUiLaw.Popup(_staticPopupSlots,
            GuildFrameUiLaw.InvitePopupType);
        if (popup is not { } visible || _skin is null) return;
        float scale = GameplayUiScale();
        (string inviter, string guild) = GuildFrameUiLaw.InvitePopupData(
            visible.Instance.DataToken);
        string text = GuildFrameUiLaw.InvitePopupText(inviter, guild);
        string[] lines = WrapTooltipText(text, "GameFontHighlight", scale,
            GuildFrameUiLaw.InvitePopupTextWidth * scale).ToArray();
        float logicalTextHeight = lines.Length *
            GameText.LinePitch("GameFontHighlight", 1);
        Vector2 logicalSize = GuildFrameUiLaw.InvitePopupSize(logicalTextHeight);
        Vector2 origin = StaticPopupOrigin(visible.Slot,
            GuildFrameUiLaw.InvitePopupWidth, scale);
        if (!BeginGuildPopupWindow($"##guild-invite-popup-{visible.Slot}", origin,
                logicalSize, scale, out ImDrawListPtr draw)) return;
        Vector2 size = logicalSize * scale;
        draw.PushClipRectFullScreen();
        _skin.DrawBackdrop(draw, origin, origin + size, WowSkin.Dialog);
        for (int i = 0; i < lines.Length; i++)
            GameText.DrawCentered(draw, "GameFontHighlight", lines[i],
                origin + GuildFrameUiLaw.InvitePopupLineCenter(i,
                    GameText.LinePitch("GameFontHighlight", 1)) * scale, scale);
        GuildFrameUiLaw.LogicalRect acceptSeat =
            GuildFrameUiLaw.InvitePopupButton(1, logicalTextHeight);
        bool accept = DrawPartyInviteButton(draw,
            $"StaticPopup{visible.Slot}Button1", "Accept",
            origin + acceptSeat.Min * scale,
            scale, false, default);
        GuildFrameUiLaw.LogicalRect declineSeat =
            GuildFrameUiLaw.InvitePopupButton(2, logicalTextHeight);
        bool decline = DrawPartyInviteButton(draw,
            $"StaticPopup{visible.Slot}Button2", "Decline",
            origin + declineSeat.Min * scale,
            scale, false, default);
        draw.PopClipRect();
        ImGui.End();
        if (accept) ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
            _staticPopupSlots, visible.Slot, 1));
        else if (decline) ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
            _staticPopupSlots, visible.Slot, 2));
    }

    private void DrawGuildAddRankPopup()
    {
        var popup = GuildFrameUiLaw.Popup(_staticPopupSlots,
            GuildFrameUiLaw.AddRankPopupType);
        if (popup is not { } visible || _skin is null) return;
        float scale = GameplayUiScale();
        float textHeight = GameText.LinePitch("GameFontHighlight", 1);
        StaticPopupCoordinatorLaw.NarrowEditBoxLayout layout =
            StaticPopupCoordinatorLaw.NarrowEditLayout(textHeight);
        Vector2 origin = StaticPopupOrigin(visible.Slot, layout.Width, scale);
        Vector2 size = layout.Size;
        if (!BeginGuildPopupWindow($"##guild-add-rank-popup-{visible.Slot}", origin,
                size, scale, out ImDrawListPtr draw)) return;
        _skin.DrawBackdrop(draw, origin, origin + size * scale, WowSkin.Dialog);
        GameText.DrawCentered(draw, "GameFontHighlight", GuildFrameUiLaw.AddRankLabel,
            origin + layout.Text.Center * scale, scale);
        Vector2 editMin = origin + layout.EditBox.Min * scale;
        DrawStaticPopupEditBoxBorder(draw, editMin, scale);
        ImGui.SetCursorScreenPos(editMin + GuildFrameUiLaw.NarrowPopupEditTextOffset * scale);
        ImGui.SetNextItemWidth(layout.EditBox.Width * scale);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        if (_guildAddRankFocusRequested)
        {
            ImGui.SetKeyboardFocusHere();
            _guildAddRankFocusRequested = false;
        }
        bool entered = ImGui.InputText("##guild-add-rank-edit", ref _guildAddRankInput,
            128, ImGuiInputTextFlags.EnterReturnsTrue);
        if (_guildAddRankInput.Length > GuildFrameUiLaw.RankNameMaxLetters)
            _guildAddRankInput = _guildAddRankInput[..GuildFrameUiLaw.RankNameMaxLetters];
        _guildAddRankEditFocused = ImGui.IsItemActive();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
        bool accept = DrawPartyInviteButton(draw,
            $"StaticPopup{visible.Slot}Button1", "Accept",
            origin + layout.Button1.Min * scale,
            scale, false, default);
        bool cancel = DrawPartyInviteButton(draw,
            $"StaticPopup{visible.Slot}Button2", "Cancel",
            origin + layout.Button2.Min * scale,
            scale, false, default);
        ImGui.End();
        if (entered)
        {
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.EditBoxEnter(
                _staticPopupSlots, visible.Slot));
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(
                _staticPopupSlots, GuildFrameUiLaw.AddRankPopupType));
        }
        else if (accept) ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
            _staticPopupSlots, visible.Slot, 1));
        else if (cancel) ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
            _staticPopupSlots, visible.Slot, 2));
    }

    private void DrawGuildActionPopup()
    {
        var popup = GuildFrameUiLaw.GuildActionPopup(_staticPopupSlots);
        if (popup is not { } visible || _skin is null) return;
        float scale = GameplayUiScale();
        string type = visible.Instance.Definition.Type;
        string text = GuildFrameUiLaw.GuildActionText(type,
            visible.Instance.DataToken ?? "");
        string[] lines = WrapTooltipText(text, "GameFontHighlight", scale,
            StaticPopupCoordinatorLaw.TextWidth * scale).ToArray();
        float textHeight = lines.Length * GameText.LinePitch("GameFontHighlight", 1);
        Vector2 size = GuildFrameUiLaw.RemoveMemberPopupSize(textHeight);
        Vector2 origin = StaticPopupOrigin(visible.Slot, size.X, scale);
        if (!BeginGuildPopupWindow($"##guild-action-popup-{visible.Slot}", origin,
                size, scale, out ImDrawListPtr draw)) return;
        _skin.DrawBackdrop(draw, origin, origin + size * scale, WowSkin.Dialog);
        for (int i = 0; i < lines.Length; i++)
            GameText.DrawCentered(draw, "GameFontHighlight", lines[i],
                origin + GuildFrameUiLaw.RemoveMemberPopupLineCenter(i,
                    GameText.LinePitch("GameFontHighlight", 1)) * scale, scale);
        GuildFrameUiLaw.LogicalRect yesSeat =
            GuildFrameUiLaw.RemoveMemberPopupButton(1, textHeight);
        (string acceptText, string cancelText) = GuildFrameUiLaw.GuildActionButtons(type);
        bool yes = DrawPartyInviteButton(draw, $"StaticPopup{visible.Slot}Button1", acceptText,
            origin + yesSeat.Min * scale, scale, false, default);
        GuildFrameUiLaw.LogicalRect noSeat =
            GuildFrameUiLaw.RemoveMemberPopupButton(2, textHeight);
        bool no = DrawPartyInviteButton(draw, $"StaticPopup{visible.Slot}Button2", cancelText,
            origin + noSeat.Min * scale, scale, false, default);
        ImGui.End();
        if (yes) ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
            _staticPopupSlots, visible.Slot, 1));
        else if (no) ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
            _staticPopupSlots, visible.Slot, 2));
    }

    private void DrawGuildNotePopup(string type, string label)
    {
        var popup = GuildFrameUiLaw.Popup(_staticPopupSlots, type);
        if (popup is not { } visible || _skin is null) return;
        int seat = visible.Slot - 1;
        float scale = GameplayUiScale();
        float textHeight = GameText.LinePitch("GameFontHighlight", 1);
        StaticPopupCoordinatorLaw.WideEditBoxLayout layout =
            StaticPopupCoordinatorLaw.WideEditLayout(textHeight);
        Vector2 origin = StaticPopupOrigin(visible.Slot, layout.Width, scale);
        Vector2 size = layout.Size;
        if (!BeginGuildPopupWindow($"##guild-note-popup-{type}-{visible.Slot}", origin,
                size, scale, out ImDrawListPtr draw)) return;
        _skin.DrawBackdrop(draw, origin, origin + size * scale, WowSkin.Dialog);
        GameText.DrawCentered(draw, "GameFontHighlight", label,
            origin + layout.Text.Center * scale, scale);
        Vector2 editMin = origin + layout.EditBox.Min * scale;
        DrawStaticPopupWideEditBoxBorder(draw, editMin, scale);
        ImGui.SetCursorScreenPos(editMin + GuildFrameUiLaw.WidePopupEditTextOffset * scale);
        ImGui.SetNextItemWidth(layout.EditBox.Width * scale);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        if (_guildNoteFocusRequested[seat])
        {
            ImGui.SetKeyboardFocusHere();
            _guildNoteFocusRequested[seat] = false;
        }
        bool entered = ImGui.InputText($"##guild-note-edit-{seat}",
            ref _guildNoteInputs[seat], 256, ImGuiInputTextFlags.EnterReturnsTrue);
        _guildNoteInputs[seat] = GuildFrameUiLaw.TruncateNote(_guildNoteInputs[seat]);
        _guildNoteEditFocused[seat] = ImGui.IsItemActive();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
        bool accept = DrawPartyInviteButton(draw,
            $"StaticPopup{visible.Slot}Button1", "Accept",
            origin + layout.Button1.Min * scale,
            scale, false, default);
        bool cancel = DrawPartyInviteButton(draw,
            $"StaticPopup{visible.Slot}Button2", "Cancel",
            origin + layout.Button2.Min * scale,
            scale, false, default);
        ImGui.End();
        if (entered)
        {
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.EditBoxEnter(
                _staticPopupSlots, visible.Slot));
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(
                _staticPopupSlots, type));
        }
        else if (accept) ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
            _staticPopupSlots, visible.Slot, 1));
        else if (cancel) ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
            _staticPopupSlots, visible.Slot, 2));
    }

    private static bool BeginGuildPopupWindow(string id, Vector2 origin, Vector2 logicalSize,
        float scale, out ImDrawListPtr draw)
    {
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(logicalSize * scale, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        bool begun = ImGui.Begin(id, ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav);
        ImGui.PopStyleVar(2);
        draw = ImGui.GetWindowDrawList();
        if (!begun) ImGui.End();
        return begun;
    }

    private void DrawStaticPopupWideEditBoxBorder(ImDrawListPtr draw, Vector2 editMin,
        float scale)
    {
        uint left = _gameplayArt?.Handle(@"Interface\ChatFrame\UI-ChatInputBorder-Left") ?? 0;
        uint right = _gameplayArt?.Handle(@"Interface\ChatFrame\UI-ChatInputBorder-Right") ?? 0;
        if (left != 0)
        {
            GuildFrameUiLaw.LogicalRect cap = GuildFrameUiLaw.WideEditBorderLeft;
            GuildFrameUiLaw.LogicalRect middle = GuildFrameUiLaw.WideEditBorderMiddle;
            draw.AddImage((nint)left, editMin + cap.Min * scale,
                editMin + (cap.Min + cap.Size) * scale);
            draw.AddImage((nint)left, editMin + middle.Min * scale,
                editMin + (middle.Min + middle.Size) * scale,
                GuildFrameUiLaw.WideEditBorderMiddleUvMin, Vector2.One);
        }
        if (right != 0)
        {
            GuildFrameUiLaw.LogicalRect cap = GuildFrameUiLaw.WideEditBorderRight;
            draw.AddImage((nint)right, editMin + cap.Min * scale,
                editMin + (cap.Min + cap.Size) * scale,
                GuildFrameUiLaw.WideEditBorderRightUvMin, Vector2.One);
        }
    }

    private void DrawGuildAddMemberPopup()
    {
        (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? popup =
            GuildFrameUiLaw.AddMemberPopup(_staticPopupSlots);
        if (popup is not { } visible || _skin is null) return;
        float s = GameplayUiScale();
        float textHeight = GameText.LinePitch("GameFontHighlight", 1);
        StaticPopupCoordinatorLaw.NarrowEditBoxLayout layout =
            StaticPopupCoordinatorLaw.NarrowEditLayout(textHeight);
        Vector2 origin = StaticPopupOrigin(visible.Slot, layout.Width, s);
        Vector2 size = layout.Size * s;

        // ImGui hosts only the law-resolved modal and hit rectangles.
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav;
        bool begun = ImGui.Begin($"##guild-add-member-popup-{visible.Slot}", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.PushClipRectFullScreen();
        _skin.DrawBackdrop(dl, origin, origin + size, WowSkin.Dialog);
        GameText.DrawCentered(dl, "GameFontHighlight", GuildFrameUiLaw.AddMemberLabel,
            origin + layout.Text.Center * s, s);
        Vector2 editMin = origin + layout.EditBox.Min * s;
        DrawStaticPopupEditBoxBorder(dl, editMin, s);
        ImGui.SetCursorScreenPos(editMin + GuildFrameUiLaw.NarrowPopupEditTextOffset * s);
        ImGui.SetNextItemWidth(layout.EditBox.Width * s);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        if (_guildAddMemberFocusRequested)
        {
            ImGui.SetKeyboardFocusHere();
            _guildAddMemberFocusRequested = false;
        }
        bool entered = ImGui.InputText("##guild-add-member-edit", _guildAddMemberInput,
            (uint)_guildAddMemberInput.Length, ImGuiInputTextFlags.EnterReturnsTrue);
        _guildAddMemberEditFocused = ImGui.IsItemActive();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
        bool accepted = DrawPartyInviteButton(dl,
            $"StaticPopup{visible.Slot}Button1", "Accept",
            origin + layout.Button1.Min * s,
            s, capture: false, default);
        bool cancelled = DrawPartyInviteButton(dl,
            $"StaticPopup{visible.Slot}Button2", "Cancel",
            origin + layout.Button2.Min * s,
            s, capture: false, default);
        dl.PopClipRect();
        ImGui.End();

        if (entered)
        {
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.EditBoxEnter(
                _staticPopupSlots, visible.Slot));
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(
                _staticPopupSlots, GuildFrameUiLaw.AddMemberPopupType));
        }
        else if (accepted)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 1));
        else if (cancelled)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 2));
    }
}
